// File: ConvergenceTrackerTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The evidence behind the depth-cap auto-extension, and the decision it feeds.
//
// ConvergenceTracker counts what a job actually did in a sliding window — mutations that
// landed and failures it moved past — and DepthAutoExtender turns that into extend / decline
// at the cap. Each decline names the condition that failed, so the transcript says why.

using Xunit;

namespace DevMind.Core.Tests
{
    public class ConvergenceTrackerTests
    {
        // ── Resolution counting ──────────────────────────────────────────────────

        [Fact]
        public void AFailureClearedByACleanRun_IsAResolution()
        {
            var t = new ConvergenceTracker();
            t.Observe(0, "error CS1001: x", 1, "dotnet build");
            t.Observe(1, null, null, "");                 // the fix
            t.Observe(0, null, 0, "dotnet build");        // clean

            Assert.Equal(1, t.ResolutionsInWindow);
            Assert.Null(t.CurrentFailure);
            Assert.True(t.LatestBuildOrTestGreen);
        }

        [Fact]
        public void ADifferentFailureAfterAMutation_IsAResolution()
        {
            var t = new ConvergenceTracker();
            t.Observe(0, "error CS1001: x", 1, "dotnet build");
            t.Observe(1, null, null, "");
            t.Observe(0, "error CS1002: y", 1, "dotnet build");

            Assert.Equal(1, t.ResolutionsInWindow);
            Assert.Equal("error CS1002: y", t.CurrentFailure);
        }

        [Fact]
        public void TheSameFailureAgain_IsNotAResolution_EvenAfterAMutation()
        {
            var t = new ConvergenceTracker();
            t.Observe(0, "error CS1001: x", 1, "dotnet build");
            t.Observe(1, null, null, "");
            t.Observe(0, "error CS1001: x", 1, "dotnet build");

            Assert.Equal(0, t.ResolutionsInWindow);
        }

        [Fact]
        public void ADifferentFailureWithNoChangeInBetween_IsNotAResolution()
        {
            // Nothing changed; the job just ran something else that failed.
            var t = new ConvergenceTracker();
            t.Observe(0, "error CS1001: x", 1, "dotnet build");
            t.Observe(0, "Could not find file 'a.txt'", null, "");

            Assert.Equal(0, t.ResolutionsInWindow);
            Assert.Equal("Could not find file 'a.txt'", t.CurrentFailure);
        }

        [Fact]
        public void ABuildFailureFollowedByATestFailure_IsAResolution_WithoutAMutation()
        {
            // The build passes now — the tests could not have run otherwise. It moved forward.
            var t = new ConvergenceTracker();
            t.Observe(0, "error CS1001: x", 1, "dotnet build");
            t.Observe(0, "Failed FooTests.Bar", 1, "dotnet test");

            Assert.Equal(1, t.ResolutionsInWindow);
            Assert.False(t.LatestBuildOrTestGreen);
        }

        [Fact]
        public void ATestFailureFollowedByABuildFailure_WithoutAMutation_IsNotAResolution()
        {
            var t = new ConvergenceTracker();
            t.Observe(0, "Failed FooTests.Bar", 1, "TEST Foo.Tests");
            t.Observe(0, "error CS1001: x", 1, "dotnet build");

            Assert.Equal(0, t.ResolutionsInWindow);
        }

        [Fact]
        public void AReadOnlyIterationBetweenFailures_LeavesTheFailureOutstanding()
        {
            var t = new ConvergenceTracker();
            t.Observe(0, "error CS1001: x", 1, "dotnet build");
            t.Observe(0, null, null, "");

            Assert.Equal(0, t.ResolutionsInWindow);
            Assert.Equal("error CS1001: x", t.CurrentFailure);
        }

        // ── Window ───────────────────────────────────────────────────────────────

        [Fact]
        public void TheWindowSlides_OldMutationsAndResolutionsAgeOut()
        {
            var t = new ConvergenceTracker(window: 4);
            t.Observe(0, "error A", 1, "dotnet build");
            t.Observe(2, "error B", 1, "dotnet build");   // 2 mutations + 1 resolution
            Assert.Equal(2, t.MutationsInWindow);
            Assert.Equal(1, t.ResolutionsInWindow);

            t.Observe(0, null, null, "");
            t.Observe(0, null, null, "");
            t.Observe(0, null, null, "");                  // B is the oldest of the last 4 — still in
            Assert.Equal(2, t.MutationsInWindow);
            Assert.Equal(1, t.ResolutionsInWindow);

            t.Observe(0, null, null, "");                  // and now it has aged out
            Assert.Equal(0, t.MutationsInWindow);
            Assert.Equal(0, t.ResolutionsInWindow);
        }

        [Fact]
        public void LatestBuildOrTest_IsNotWindowed_AndIgnoresOtherCommands()
        {
            var t = new ConvergenceTracker(window: 2);
            Assert.Null(t.LatestBuildOrTestGreen);
            t.Observe(0, null, 0, "dotnet build");
            t.Observe(0, "nope", 1, "git status");         // not a build/test
            t.Observe(0, null, null, "");
            t.Observe(0, null, null, "");
            Assert.True(t.LatestBuildOrTestGreen);
        }

        // ── The decision ─────────────────────────────────────────────────────────

        private static ConvergenceTracker Converging()
        {
            var t = new ConvergenceTracker();
            for (int i = 0; i < 4; i++)
            {
                t.Observe(1, null, null, "");
                t.Observe(0, $"error CS100{i}: thing {i}", 1, "dotnet build");
            }
            return t;   // 4 mutations, 3 resolutions
        }

        private static (DepthAutoExtender extender, List<int> applied) Extender(
            int originalCap = 40, int maxExtensions = 2, ConvergenceTracker? tracker = null)
        {
            var applied = new List<int>();
            return (new DepthAutoExtender(originalCap, maxExtensions, applied.Add, tracker ?? Converging()), applied);
        }

        [Fact]
        public void Converging_Extends_ByHalfTheOriginalCap_ThroughTheApplyPath()
        {
            var (x, applied) = Extender(originalCap: 40);

            var d = x.Decide(40, 40, new LoopState(), null, 78);

            Assert.True(d.Extended);
            Assert.Equal(60, d.NewCap);
            Assert.Equal(new[] { 60 }, applied);
            var e = Assert.Single(x.Extensions);
            Assert.Equal(40, e.AtDepth);
            Assert.Equal(60, e.NewCap);
            Assert.Contains("3 failure(s) resolved", e.SignalSummary);
        }

        [Fact]
        public void TheExtension_IsAtLeastTen_AndIsSizedFromTheOriginalCapNotTheCurrentOne()
        {
            var (small, _) = Extender(originalCap: 6);
            Assert.Equal(6 + 10, small.Decide(6, 6, new LoopState(), null, 0).NewCap);

            var (x, _) = Extender(originalCap: 200);
            Assert.Equal(300, x.Decide(200, 200, new LoopState(), null, 0).NewCap);
            Assert.Equal(400, x.Decide(300, 300, new LoopState(), null, 0).NewCap);   // past the 200 clamp
        }

        [Fact]
        public void MaxExtensions_IsHonoured()
        {
            var (x, applied) = Extender(maxExtensions: 2);
            Assert.True(x.Decide(40, 40, new LoopState(), null, 0).Extended);
            Assert.True(x.Decide(60, 60, new LoopState(), null, 0).Extended);

            var third = x.Decide(80, 80, new LoopState(), null, 0);
            Assert.False(third.Extended);
            Assert.Equal("extensions used up (2/2)", third.Detail);
            Assert.Equal(new[] { 60, 80 }, applied);
        }

        [Theory]
        [InlineData(3, false)]
        [InlineData(1, true)]   // the research directive was issued for the current signature
        public void ThrashState_BlocksExtension(int repeatedFailures, bool nudgeIssued)
        {
            var (x, applied) = Extender();
            var state = new LoopState { RepeatedFailureCount = repeatedFailures, ResearchNudgeIssued = nudgeIssued };

            var d = x.Decide(40, 40, state, null, 0);

            Assert.False(d.Extended);
            Assert.StartsWith("thrashing", d.Detail);
            Assert.Empty(applied);
        }

        [Fact]
        public void TooFewMutations_BlocksExtension()
        {
            var t = new ConvergenceTracker();
            t.Observe(0, "error A", 1, "dotnet build");
            t.Observe(1, "error B", 1, "dotnet build");   // a resolution, but only one change
            var (x, _) = Extender(tracker: t);

            var d = x.Decide(40, 40, new LoopState(), null, 0);

            Assert.False(d.Extended);
            Assert.Equal("only 1 mutation(s) in the last 30 iterations (need 3)", d.Detail);
        }

        [Fact]
        public void NoResolutionAndNoGreenRun_BlocksExtension()
        {
            var t = new ConvergenceTracker();
            for (int i = 0; i < 5; i++) t.Observe(1, null, null, "");   // edits, nothing verified
            var (x, _) = Extender(tracker: t);

            var d = x.Decide(40, 40, new LoopState(), null, 0);

            Assert.False(d.Extended);
            Assert.StartsWith("no failure resolved", d.Detail);
        }

        [Fact]
        public void AGreenBuildAfterChanges_IsEnough_WithoutAResolution()
        {
            var t = new ConvergenceTracker();
            for (int i = 0; i < 3; i++) t.Observe(1, null, null, "");
            t.Observe(0, null, 0, "dotnet build");
            var (x, _) = Extender(tracker: t);

            Assert.True(x.Decide(40, 40, new LoopState(), null, 0).Extended);
        }

        [Fact]
        public void ContextAtTheGuardLimit_BlocksExtension_ButUnknownUsageDoesNot()
        {
            var (x, _) = Extender();
            var d = x.Decide(40, 40, new LoopState(), 78, 78);
            Assert.False(d.Extended);
            Assert.Equal("context at 78% (limit 78%)", d.Detail);

            Assert.True(x.Decide(40, 40, new LoopState(), null, 78).Extended);
            var (y, _) = Extender();
            Assert.True(y.Decide(40, 40, new LoopState(), 95, 0).Extended);   // no limit configured
        }

        // ── Reset (a caller override steer) ──────────────────────────────────────

        [Fact]
        public void Reset_ForgetsTheWindow_TheOutstandingFailure_AndTheLatestBuild()
        {
            var t = Converging();
            t.Observe(0, null, 0, "dotnet build");
            Assert.True(t.MutationsInWindow > 0);

            t.Reset();

            Assert.Equal(0, t.MutationsInWindow);
            Assert.Equal(0, t.ResolutionsInWindow);
            Assert.Null(t.LatestBuildOrTestGreen);
            Assert.Null(t.CurrentFailure);
            Assert.Contains("in the last 0 iteration(s)", t.Summary());

            // A failure after the reset is a first failure, not a resolution of the old one.
            t.Observe(1, "error CS9999: new direction", 1, "dotnet build");
            Assert.Equal(0, t.ResolutionsInWindow);
        }

        [Fact]
        public void AfterAReset_ExtensionIsDeclined_UntilTheJobConvergesAgain()
        {
            var (x, applied) = Extender();
            x.Tracker.Reset();

            var declined = x.Decide(40, 40, new LoopState(), null, 0);
            Assert.False(declined.Extended);
            Assert.StartsWith("no failure resolved", declined.Detail);

            // Still armed: new evidence earns the extension.
            for (int i = 0; i < 4; i++)
            {
                x.Tracker.Observe(1, null, null, "");
                x.Tracker.Observe(0, $"error CS200{i}: after the redirect", 1, "dotnet build");
            }
            Assert.True(x.Decide(40, 40, new LoopState(), null, 0).Extended);
            Assert.Equal(new[] { 60 }, applied);
        }
    }
}
