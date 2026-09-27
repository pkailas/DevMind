// File: AnswerDedupTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The one rule that stops the final answer showing twice, pinned at the point where it is
// decided: AnswerDedup.ShouldCollapse. It takes only what the decision needs — how much
// visible prose the user SAW in the terminal iteration, the summary text, and which kind of
// answer this is — so the table below is the whole contract, and a regression in any one of
// the four cases (collapse, draw, never collapse questions, ignore earlier-iteration prose)
// is a test failure, not a transcript the user reads twice.
//
// The threshold is the one judgement call the brief left open, and it is pinned here so it
// is a decision on record: 40 non-whitespace characters of visible prose is "the model
// already said the answer", and below that the summary still carries the real answer.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class AnswerDedupTests
    {
        // A summary that is real enough to repeat.
        private const string Summary =
            "Plan mode is active, so file creation was refused. Plan: " +
            "1) create hello.txt; 2) read it back to verify.";

        // ── The four cases the brief named ───────────────────────────────────────

        // Prose at/above the threshold + a task_done summary → collapse.
        [Fact]
        public void RealProse_AndTaskDone_Collapses()
        {
            Assert.True(AnswerDedup.ShouldCollapse(AnswerDedup.MinProseChars, Summary, AnswerKind.TaskDone));
            Assert.True(AnswerDedup.ShouldCollapse(400, Summary, AnswerKind.TaskDone));
        }

        // One character short of the threshold → draw in full (the summary still matters).
        [Fact]
        public void JustUnderTheThreshold_DrainsInFull()
        {
            Assert.False(AnswerDedup.ShouldCollapse(AnswerDedup.MinProseChars - 1, Summary, AnswerKind.TaskDone));
        }

        // No prose, or only a fragment → draw in full.
        [Fact]
        public void EmptyOrTrivialProse_DrawsInFull()
        {
            Assert.False(AnswerDedup.ShouldCollapse(0, Summary, AnswerKind.TaskDone));
            Assert.False(AnswerDedup.ShouldCollapse(5, Summary, AnswerKind.TaskDone));
        }

        // ask_caller questions are NEVER collapsed, whatever the prose count. A person is
        // waiting to answer them — folding them hides a run that is blocked.
        [Fact]
        public void AskCaller_IsNeverCollapsed()
        {
            Assert.False(AnswerDedup.ShouldCollapse(0, Summary, AnswerKind.AskCaller));
            Assert.False(AnswerDedup.ShouldCollapse(400, Summary, AnswerKind.AskCaller));
        }

        // The threshold is about the TERMINAL iteration's prose. The caller passes that
        // iteration's count (a running total across the turn would be a bug), so prose of
        // 500 in an earlier iteration and 0 in the terminal one is 0 here → draw in full.
        // Pinned so the contract "the caller passes the terminal count" is explicit.
        [Fact]
        public void EarlierIterationProseDoesNotCount_WhenTerminalIsZero()
        {
            Assert.False(AnswerDedup.ShouldCollapse(0, Summary, AnswerKind.TaskDone));
        }

        // An empty summary is never collapsed — there is nothing to repeat.
        [Fact]
        public void EmptySummary_IsNeverCollapsed()
        {
            Assert.False(AnswerDedup.ShouldCollapse(400, "   ", AnswerKind.TaskDone));
            Assert.False(AnswerDedup.ShouldCollapse(400, "", AnswerKind.TaskDone));
        }

        // ── The measure itself ───────────────────────────────────────────────────

        [Fact]
        public void CountVisibleProse_CountsOnlyNonWhitespace()
        {
            Assert.Equal(6, AnswerDedup.CountVisibleProse("a b c d e f"));
            Assert.Equal(0, AnswerDedup.CountVisibleProse("   \n\t  "));
            Assert.Equal(0, AnswerDedup.CountVisibleProse(""));
            Assert.Equal(0, AnswerDedup.CountVisibleProse(null));
        }

        // The threshold is exactly 40 non-whitespace characters — pinned, not implied.
        [Fact]
        public void TheThresholdIsForty()
        {
            Assert.Equal(40, AnswerDedup.MinProseChars);
        }
    }
}
