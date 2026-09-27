// File: PlanModePromptNoteTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The plan-mode note in the TUI's system prompt. BuildCombinedSystemPrompt is a private
// static on Program; what a test CAN pin is the note itself: present exactly while the
// mode is Plan, and empty otherwise. The prompt is rebuilt per turn from options, so the
// note's presence/absence is the mode's presence/absence — no extra plumbing to remove it
// when /mode or Shift+Tab leaves Plan.

using Xunit;

namespace DevMind.TUI.Tests
{
    public sealed class PlanModePromptNoteTests
    {
        [Fact]
        public void TheNoteIsPresentInPlan()
        {
            string note = Program.PlanModePromptNote(ApprovalMode.Plan);

            Assert.NotEmpty(note);
            Assert.Contains("PLAN MODE", note, StringComparison.Ordinal);
            Assert.Contains("plan mode", note, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("concrete plan", note, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheNoteIsAbsentInAutoAndManual()
        {
            Assert.Equal(string.Empty, Program.PlanModePromptNote(ApprovalMode.Auto));
            Assert.Equal(string.Empty, Program.PlanModePromptNote(ApprovalMode.Manual));
        }
    }
}
