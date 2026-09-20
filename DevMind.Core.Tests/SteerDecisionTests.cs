// File: SteerDecisionTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The DECISION logic for steering a running headless job (devmind_task_steer), kept
// pure so the two things the feature must get right are pinned by tests rather than
// asserted in prose:
//
//   1. The LAST-ITERATION PREDICATE — whether the iteration about to start is the final
//      one before the depth cap. This was flagged as "possibly off by one", so it is
//      proven by [Theory] against the exact depth the driver has at each iteration
//      boundary (see the trace at the top of IsLastIteration_MatchesTheDriverSchedule),
//      not assumed.
//
//   2. The FOLD/REJECT DECISION — an override on the last iteration is refused (no
//      iterations left to act on it) and leaves the prompt unchanged; a suggestion on the
//      last iteration is folded, as is everything off the last iteration.
//
// The audit seam (BufferedAgenticHost.RecordSteer) is pinned here too: each disposition
// maps to its own journal kind so a result that changed because of an injection at
// iteration N is explainable from the action journal alone.

using Xunit;

namespace DevMind.Core.Tests
{
    public class SteerDecisionTests
    {
        // ── 1. The last-iteration predicate, proven against the driver schedule ──

        // The driver (LoopDriver.cs, ProcessIterationAsync) handles each tool-calling
        // iteration in this exact order, at the top of iteration k the loop sees
        // AgenticDepth == k-1:
        //   (a) depth-cap check:  if (maxDepth > 0 && AgenticDepth >= maxDepth) → terminal
        //   (b) AgenticDepth++    (increment at the END of a re-triggering iteration)
        //   (c) finish-up reserve: else if (maxDepth > 0 && AgenticDepth == maxDepth)
        //       → nextMessage = "This is your LAST iteration…"  (starts iteration N/N)
        //
        // Consequence, traced for maxDepth == 3 (AgenticDepth shown at the TOP of each
        // iteration, i.e. what the steer drain reads via Steer.IsLastIteration):
        //   iter 1: depth 0  → not last
        //   iter 2: depth 1  → not last
        //   iter 3: depth 2  → not last   (its re-trigger hits the finish-up reserve,
        //                                   which builds the prompt for iteration 4)
        //   iter 4: depth 3  → LAST — this is the iteration STARTED by the finish-up
        //                                 reserve prompt ("This is your LAST iteration…"),
        //                                 and the very next cap check (3 >= 3) terminates.
        //
        // So "the last iteration" is precisely the one that ENTERS with
        // AgenticDepth == maxDepth — the driver's own comment says "== maxDepth means
        // the request built here starts iteration N/N — the last one before the cap
        // check fires." The predicate agrees; [InlineData] below pins the boundaries.
        [Theory]
        [InlineData(3, 0, false)]  // maxDepth 3, entering iter 1 (depth 0)
        [InlineData(3, 1, false)]  // entering iter 2
        [InlineData(3, 2, false)]  // entering iter 3 — still one re-trigger left
        [InlineData(3, 3, true)]   // entering iter 4 — the finish-up / LAST iteration
        [InlineData(5, 4, false)]  // maxDepth 5, depth 4 — the 5th, not yet the last
        [InlineData(5, 5, true)]   // depth 5 — the last (iter 6)
        [InlineData(5, 6, true)]   // defensive: the cap fires first, but the predicate
                                   // must still read "last" (>=, not ==)
        public void IsLastIteration_MatchesTheDriverSchedule(int maxDepth, int agenticDepth, bool expected)
        {
            Assert.Equal(expected, Steer.IsLastIteration(maxDepth, agenticDepth));
        }

        // maxDepth == 1 — the smallest cap. Iteration 1 enters at depth 0 (NOT the
        // last — the model still has a re-trigger left to it); iteration 2 enters at
        // depth 1, which the finish-up reserve flagged as the last. This is the case
        // most likely to be off by one (treating the FIRST iteration as the last).
        [Theory]
        [InlineData(1, 0, false)]  // first and only re-triggering iteration
        [InlineData(1, 1, true)]   // the LAST iteration the driver will run
        public void IsLastIteration_MaxDepthOne_FirstIterationIsNotTheLast(int maxDepth, int agenticDepth, bool expected)
        {
            Assert.Equal(expected, Steer.IsLastIteration(maxDepth, agenticDepth));
        }

        // Uncapped (maxDepth <= 0) — the loop never stops on depth, so there is no
        // "last iteration" to refuse an override against; a steer is always foldable.
        [Theory]
        [InlineData(0, 0, false)]
        [InlineData(0, 5, false)]
        [InlineData(-1, 9, false)]
        public void IsLastIteration_Uncapped_NeverTheLast(int maxDepth, int agenticDepth, bool expected)
        {
            Assert.Equal(expected, Steer.IsLastIteration(maxDepth, agenticDepth));
        }

        // A turn that ends BEFORE any increment — the model answers in prose or calls
        // task_done on the very first iteration, so the driver never reaches its
        // increment and AgenticDepth stays 0. The predicate must NOT call that the
        // last iteration (that would refuse a steer on an ordinary first turn).
        [Theory]
        [InlineData(1, 0, false)]   // maxDepth 1, but the turn never re-triggered
        [InlineData(5, 0, false)]
        [InlineData(0, 0, false)]
        public void IsLastIteration_TurnEndsBeforeAnyIncrement_NeverTheLast(int maxDepth, int agenticDepth, bool expected)
        {
            Assert.Equal(expected, Steer.IsLastIteration(maxDepth, agenticDepth));
        }

        // ── 2. The fold / reject decision ──

        [Fact]
        public void Apply_OverrideOnLastIteration_IsRejected_AndPromptUnchanged()
        {
            const string prompt = "This is your LAST iteration before the 2-iteration cap. …";
            var (folded, disposition) = Steer.Apply(prompt, "drop the tests, just ship it",
                SteerMode.Override, isLastIteration: true);

            Assert.Equal(SteerDisposition.Rejected, disposition);
            Assert.Equal(prompt, folded);               // the prompt is left EXACTLY as it was
            Assert.DoesNotContain("[CALLER STEER", folded);
        }

        [Fact]
        public void Apply_SuggestOnLastIteration_IsFolded_NotRejected()
        {
            const string prompt = "This is your LAST iteration before the 2-iteration cap. …";
            var (folded, disposition) = Steer.Apply(prompt, "mention the rollback plan in your summary",
                SteerMode.Suggest, isLastIteration: true);

            // The narrow point of the refinement: a suggestion on the last iteration is
            // consumed normally — "mention X in your summary" is exactly what you want
            // to inject at that moment, and it costs nothing.
            Assert.Equal(SteerDisposition.Consumed, disposition);
            Assert.StartsWith(prompt, folded);          // the finish-up prompt is preserved…
            Assert.Contains("mention the rollback plan in your summary", folded);
            Assert.Contains("[CALLER STEER — suggestion]", folded);
        }

        [Fact]
        public void Apply_OverrideOffLastIteration_IsFolded()
        {
            var (folded, disposition) = Steer.Apply("do the thing", "actually do it the other way",
                SteerMode.Override, isLastIteration: false);

            Assert.Equal(SteerDisposition.Consumed, disposition);
            Assert.StartsWith("do the thing", folded);
            Assert.Contains("[CALLER STEER — override]", folded);
        }

        [Fact]
        public void Apply_SuggestOffLastIteration_IsFolded()
        {
            var (folded, disposition) = Steer.Apply("do the thing", "also handle the edge case",
                SteerMode.Suggest, isLastIteration: false);

            Assert.Equal(SteerDisposition.Consumed, disposition);
            Assert.Contains("[CALLER STEER — suggestion]", folded);
        }

        // The steer is appended as its OWN delimited block — never glued into the
        // driver's synthetic re-trigger, which it must read as visibly distinct.
        [Fact]
        public void Apply_FoldsTheSteer_AsItsOwnDelimitedBlock_SeparateFromThePrompt()
        {
            var (folded, _) = Steer.Apply("Continue with the task.", "add a unit test",
                SteerMode.Suggest, isLastIteration: false);

            // The original prompt, then a blank line, then the framed steer — a distinct
            // block, not spliced into the re-trigger sentence.
            Assert.Equal("Continue with the task.\n\n"
                + "[CALLER STEER — suggestion] While you continue your current approach, the caller adds:\n"
                + "add a unit test\n"
                + "Fold this in as you go; it is not a reason to abandon your current line of work.",
                folded);
        }

        // Empty current prompt (defensive edge): the steer becomes the whole prompt.
        [Fact]
        public void Apply_EmptyPrompt_SteerBecomesThePrompt()
        {
            var (folded, disposition) = Steer.Apply("", "just say hi",
                SteerMode.Suggest, isLastIteration: false);

            Assert.Equal(SteerDisposition.Consumed, disposition);
            Assert.Equal(Steer.Frame(SteerMode.Suggest, "just say hi"), folded);
            Assert.DoesNotContain("\n\n", folded);      // no dangling leading separator
        }

        // ── Framing: the mode is explicit and GENERATES the framing the model sees ──

        [Fact]
        public void Frame_DiffersByMode_AndIsMarkedAsACallerSteer()
        {
            string suggest = Steer.Frame(SteerMode.Suggest, "handle the null case");
            string override_ = Steer.Frame(SteerMode.Override, "handle the null case");

            Assert.Equal(
                "[CALLER STEER — suggestion] While you continue your current approach, the caller adds:\n"
                + "handle the null case\n"
                + "Fold this in as you go; it is not a reason to abandon your current line of work.",
                suggest);

            Assert.Equal(
                "[CALLER STEER — override] The caller is redirecting you. Stop your current approach and change course:\n"
                + "handle the null case\n"
                + "This supersedes your current direction — follow it.",
                override_);

            // Distinct framing (the modes are not interchangeable), and both carry the
            // caller-attributed marker so the model can tell a human steer from the
            // harness's own plain-prose re-trigger (Continue / finish-up / thrash).
            Assert.NotEqual(suggest, override_);
            Assert.Contains("[CALLER STEER", suggest);
            Assert.Contains("[CALLER STEER", override_);
        }

        // ── Audit seam: disposition → journal kind ──

        private static BufferedAgenticHost NewHost()
            => new BufferedAgenticHost(Path.Combine(Path.GetTempPath(), $"devmind_steer_{Guid.NewGuid():N}"));

        [Theory]
        [InlineData(SteerDisposition.Consumed,   "steer",             true)]
        [InlineData(SteerDisposition.Rejected,   "steer_rejected",    false)]
        [InlineData(SteerDisposition.Unconsumed, "steer_unconsumed",  false)]
        public void RecordSteer_RecordsTheDisposition_AsItsOwnJournalKind(SteerDisposition disposition, string expectedKind, bool expectedSuccess)
        {
            var host = NewHost();
            host.RecordSteer("mention the rollback plan", SteerMode.Suggest, disposition);

            HostAction action = Assert.Single(host.GetActions());
            Assert.Equal(expectedKind, action.Kind);
            Assert.Equal(expectedSuccess, action.Success);
            Assert.Contains("mention the rollback plan", action.Detail);
        }

        // A superseded / rejected steer keeps its MODE and REASON in the detail, so the
        // journal shows not just that something happened but what kind of steer it was
        // and why it did not land.
        [Fact]
        public void RecordSteer_RejectedOverride_RecordsModeAndReason()
        {
            var host = NewHost();
            host.RecordSteer("drop the tests", SteerMode.Override, SteerDisposition.Rejected, "last_iteration");

            HostAction action = Assert.Single(host.GetActions());
            Assert.Equal("steer_rejected", action.Kind);
            Assert.False(action.Success);
            Assert.Contains("[override]", action.Detail);   // mode preserved
            Assert.Contains("last_iteration", action.Detail);
        }
    }
}
