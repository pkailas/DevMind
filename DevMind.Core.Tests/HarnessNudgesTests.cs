// File: HarnessNudgesTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Watchlist H-25 / H-26: the optional-work spend guard, the repeated-compile-error nudge,
// and the no-toolchain-quirk rule. Fragments below are shaped after the real jobs:
// job-1698 (InternalsVisibleTo rabbit hole on an optional test) and job-1699 (CS1061 on
// AutoScaleMode blamed on "a recurring quirk" of the net48 compile context).

using System.Text.RegularExpressions;
using Xunit;

namespace DevMind.Core.Tests
{
    public class HarnessNudgesTests
    {
        private const string Brief1698 =
            "Fix the Designer layout of FuncGroupRow so the panel docks correctly.\n" +
            "1. Change pnlFuncGroupRow to Dock = Top in FuncGroupRow.Designer.cs.\n" +
            "2. Build and run the existing tests.\n" +
            "3. Optional: add a test that asserts the private designer field through InternalsVisibleTo.\n";

        // ── Optional-item detector ──────────────────────────────────────────

        [Theory]
        [InlineData("Optional: add a screenshot probe for the dialog.")]
        [InlineData("Add a screenshot probe for the dialog if quick.")]
        [InlineData("A screenshot probe for the dialog would be nice to have.")]
        [InlineData("Add a screenshot probe for the dialog (skip this if the probe needs admin).")]
        public void Detector_FindsEachMarker(string sentence)
        {
            var items = HarnessNudges.FindOptionalItems("Fix the dialog layout.\n" + sentence);

            var item = Assert.Single(items);
            Assert.Equal(sentence, item.Sentence);
            Assert.Contains("screenshot", item.Keywords, StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("Fix the dialog layout. Build and run the tests.")]
        [InlineData("Fix the dialog layout; optionally log the old size.")]   // "optionally" is not the marker
        [InlineData("The regression test is not optional.")]
        public void Detector_NoMarker_FindsNothing(string brief)
        {
            Assert.Empty(HarnessNudges.FindOptionalItems(brief));
        }

        [Fact]
        public void Detector_KeywordsExcludeWordsSharedWithRequiredWork()
        {
            var item = Assert.Single(HarnessNudges.FindOptionalItems(Brief1698));

            Assert.Contains("InternalsVisibleTo", item.Keywords);
            Assert.Contains("private", item.Keywords, StringComparer.OrdinalIgnoreCase);
            // "test"/"tests" and the Designer names also occur in the required steps.
            Assert.DoesNotContain("test", item.Keywords, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("pnlFuncGroupRow", item.Keywords);
        }

        [Fact]
        public void Overlap_NeedsTwoKeywords_WhenTheItemHasSeveral()
        {
            var item = Assert.Single(HarnessNudges.FindOptionalItems(Brief1698));

            Assert.False(item.IsReferencedBy("Reading the private members list."));             // 1 keyword
            Assert.True(item.IsReferencedBy("Adding InternalsVisibleTo so the private field is reachable.")); // 2
            Assert.False(item.IsReferencedBy("Building FuncGroupRow.Designer.cs again."));    // required work only
        }

        // ── Spend guard ──────────────────────────────────────────────────────

        private const string OnOptional =
            "The test still cannot see the private field — adding InternalsVisibleTo to AssemblyInfo.cs.";
        private const string OnRequired = "Running the build for FuncGroupRow.Designer.cs.";

        [Fact]
        public void SpendGuard_FiresOnTheEighthConsecutiveIteration_Once()
        {
            var nudges = new HarnessNudges();
            nudges.AddBrief(Brief1698);

            for (int i = 1; i < HarnessNudges.OptionalWindow; i++)
                Assert.Empty(nudges.ObserveIteration(OnOptional, ""));

            var fired = Assert.Single(nudges.ObserveIteration(OnOptional, ""));
            Assert.StartsWith(HarnessNudges.OptionalWorkMessage, fired);
            Assert.Contains("InternalsVisibleTo", fired);

            for (int i = 0; i < 2 * HarnessNudges.OptionalWindow; i++)
                Assert.Empty(nudges.ObserveIteration(OnOptional, ""));   // once per item
        }

        [Fact]
        public void SpendGuard_AnIterationOnRequiredWorkBreaksTheRun()
        {
            var nudges = new HarnessNudges();
            nudges.AddBrief(Brief1698);

            for (int i = 0; i < HarnessNudges.OptionalWindow - 1; i++)
                Assert.Empty(nudges.ObserveIteration(OnOptional, ""));
            Assert.Empty(nudges.ObserveIteration(OnRequired, ""));
            for (int i = 0; i < HarnessNudges.OptionalWindow - 1; i++)
                Assert.Empty(nudges.ObserveIteration(OnOptional, ""));

            Assert.Single(nudges.ObserveIteration(OnOptional, ""));   // 8 in a row again
        }

        [Fact]
        public void SpendGuard_ToolCallArgumentsCountAsReferences()
        {
            var call = new ToolCallResult
            {
                Name = "patch_file",
                Arguments = new Dictionary<string, string>
                {
                    ["filename"] = "Properties/AssemblyInfo.cs",
                    ["replace"] = "[assembly: InternalsVisibleTo(\"Tests\")] // expose private designer fields",
                },
            };
            string text = HarnessNudgeEvidence.AgentText("", new[] { call });

            var item = Assert.Single(HarnessNudges.FindOptionalItems(Brief1698));
            Assert.True(item.IsReferencedBy(text));
        }

        [Fact]
        public void SpendGuard_BriefWithoutOptionalItems_NeverFires()
        {
            var nudges = new HarnessNudges();
            nudges.AddBrief("Fix the Designer layout. Build and run the tests.");

            for (int i = 0; i < 3 * HarnessNudges.OptionalWindow; i++)
                Assert.Empty(nudges.ObserveIteration(OnOptional, ""));
        }

        // ── Repeated compile error ───────────────────────────────────────────

        private const string Build1699 =
            "  Probe.cs(14,17): error CS1061: 'Control' does not contain a definition for 'AutoScaleMode' and no accessible extension method 'AutoScaleMode' accepting a first argument of type 'Control' could be found [C:\\probe\\Probe.csproj]\n" +
            "\nBuild FAILED.\n\n" +
            "  Probe.cs(14,17): error CS1061: 'Control' does not contain a definition for 'AutoScaleMode' and no accessible extension method 'AutoScaleMode' accepting a first argument of type 'Control' could be found [C:\\probe\\Probe.csproj]\n" +
            "    0 Warning(s)\n    1 Error(s)\n";

        private const string OtherMember =
            "  Probe.cs(20,9): error CS1061: 'Control' does not contain a definition for 'pnlFuncGroupRow' and no accessible extension method 'pnlFuncGroupRow' accepting a first argument of type 'Control' could be found\n";

        [Fact]
        public void CompileErrorKeys_ReadCodeAndMember_OncePerOutput()
        {
            var keys = HarnessNudges.CompileErrorKeys(Build1699).ToList();
            Assert.Equal(new[] { "CS1061 'AutoScaleMode'" }, keys);   // the summary repeat counts once
        }

        [Fact]
        public void RepeatedError_TwoOccurrencesNothing_ThirdNudges_Once()
        {
            var nudges = new HarnessNudges();

            Assert.Empty(nudges.ObserveIteration("", Build1699));
            Assert.Empty(nudges.ObserveIteration("", Build1699));
            var fired = Assert.Single(nudges.ObserveIteration("", Build1699));
            Assert.StartsWith(HarnessNudges.RepeatedCompileErrorMessage, fired);
            Assert.EndsWith("CS1061 'AutoScaleMode'", fired);

            Assert.Empty(nudges.ObserveIteration("", Build1699));   // once per key
        }

        [Fact]
        public void RepeatedError_DifferentMember_IsCountedSeparately()
        {
            var nudges = new HarnessNudges();

            Assert.Empty(nudges.ObserveIteration("", Build1699));
            Assert.Empty(nudges.ObserveIteration("", Build1699 + OtherMember));
            var fired = Assert.Single(nudges.ObserveIteration("", Build1699 + OtherMember));
            Assert.EndsWith("'AutoScaleMode'", fired);                 // 3rd AutoScaleMode, 2nd pnlFuncGroupRow

            var second = Assert.Single(nudges.ObserveIteration("", OtherMember));
            Assert.EndsWith("'pnlFuncGroupRow'", second);
        }

        [Fact]
        public void RepeatedError_CountsResetPerTurn()
        {
            var nudges = new HarnessNudges();
            nudges.ObserveIteration("", Build1699);
            nudges.ObserveIteration("", Build1699);

            nudges.ResetCompileErrors();

            Assert.Empty(nudges.ObserveIteration("", Build1699));
        }

        [Fact]
        public void Fold_FramesTheNudgeAsTheHarness()
        {
            string folded = HarnessNudgeEvidence.Fold("Continue.", HarnessNudges.OptionalWorkMessage);
            Assert.Equal("Continue.\n\n[HARNESS GUARD] " + HarnessNudges.OptionalWorkMessage, folded);
        }

        // ── H-25 prompt rule ─────────────────────────────────────────────────

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void HeadlessAddendum_CarriesTheNoQuirkRule(bool harnessVerifiesTests)
        {
            string addendum = HeadlessAgent.BuildHeadlessAddendum(harnessVerifiesTests);
            Assert.Contains("Never attribute a failure to a 'quirk' of the compiler, SDK or toolchain", addendum);
            Assert.Contains("state that the cause is unknown and report it", addendum);
        }
    }

    // End to end through HeadlessSession: a model that keeps running a command whose output
    // carries the same CS1061 gets the nudge folded into the prompt exactly once.
    public class HarnessNudgesSessionTests : IDisposable
    {
        private readonly string _dir;
        private string NoPromptFile => Path.Combine(_dir, "no-system-prompt.md");

        public HarnessNudgesSessionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_nudge_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public async Task RepeatedCompileError_NudgeInjectedOnce_AndJournalled()
        {
            if (!OperatingSystem.IsWindows() || !ShellRunner.IsPowerShellAvailable())
                return; // the scripted command runs through PowerShell

            using var server = new FakeSseServer { RepeatLastWhenExhausted = true };
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("run_shell",
                "{\"command\":\"Write-Output \\\"Probe.cs(14,17): error CS1061: 'Control' does not contain a definition for 'AutoScaleMode'\\\"\"}"));

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                var result = await HeadlessAgent.RunAsync(
                    "Fix the probe build.",
                    new HeadlessOptions
                    {
                        RequestTimeoutMinutes = 1,
                        FirstTokenTimeoutMinutes = 1,
                        ManualContextSize = 32768, // skip context probes
                        AgenticLoopMaxDepth = 6,
                    },
                    server.BaseUrl, apiKey: null!, workingDirectory: _dir, buildCommand: "dotnet build",
                    ct: CancellationToken.None, promptFilePath: NoPromptFile);

                Assert.Null(result.Error);
                Assert.Single(result.Actions, a => a.Kind == "nudge");

                // History carries the injected prompt into every later request, so "once" means
                // it is in the final request exactly once and absent before the 4th request.
                const string marker = "do not attribute it to the toolchain";
                string last;
                lock (server.RequestBodies) last = server.RequestBodies[^1];
                Assert.Single(Regex.Matches(last, Regex.Escape(marker)));
                lock (server.RequestBodies)
                    for (int i = 0; i < 3 && i < server.RequestBodies.Count; i++)
                        Assert.DoesNotContain(marker, server.RequestBodies[i]);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }
    }
}
