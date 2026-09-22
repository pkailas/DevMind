// File: PatchCardDecisionTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Manual mode's promise, for the one mutation kind that has a card.
//
// Every other mutation is gated by the executor asking a yes/no question. Patches are gated
// by forcing the diff card instead, on the premise that the card asks — and in the TUI it
// did not. It painted the diff, approved the patch and printed "Auto-approved", so the mode
// was off for exactly the operation whose preview exists because it is worth looking at.
//
// The card is a Terminal.Gui modal and no test can stand one up. What a test can reach is
// whether the mode asks at all, so that is what these pin: that Manual poses the question
// and returns its answer, that Auto never poses it, and — the part the old code got wrong —
// that a fuzzy match in Auto is still approved without a prompt, because it is the REASON
// the card appears in Auto and must not become a question.

using System.Threading.Tasks;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class PatchCardDecisionTests
    {
        // Counts the asking as well as answering it: "Manual returned true" is also true of
        // code that never asked, so the count is the assertion that separates them.
        private sealed class Asker
        {
            private readonly bool _answer;
            public int Calls;

            public Asker(bool answer) { _answer = answer; }

            public Task<bool> Ask()
            {
                Calls++;
                return Task.FromResult(_answer);
            }
        }

        [Fact]
        public async Task Manual_AsksExactlyOnce_AndReturnsTheAnswer()
        {
            var yes = new Asker(true);
            Assert.True(await PatchCardDecision.ApproveAsync(ApprovalMode.Manual, PatchConfidence.Exact, yes.Ask));
            Assert.Equal(1, yes.Calls);

            var no = new Asker(false);
            Assert.False(await PatchCardDecision.ApproveAsync(ApprovalMode.Manual, PatchConfidence.Exact, no.Ask));
            Assert.Equal(1, no.Calls);
        }

        [Fact]
        public async Task Auto_ApprovesWithoutAsking()
        {
            var asker = new Asker(false);   // would decline if it were ever consulted

            Assert.True(await PatchCardDecision.ApproveAsync(ApprovalMode.Auto, PatchConfidence.Exact, asker.Ask));
            Assert.Equal(0, asker.Calls);
        }

        [Fact]
        public async Task Auto_WithAFuzzyMatch_StillApprovesWithoutAsking()
        {
            // A fuzzy match is why the card is shown in Auto at all. Turning that into a
            // prompt would make Auto stop and wait, which is the opposite of what it means.
            var asker = new Asker(false);

            Assert.True(await PatchCardDecision.ApproveAsync(ApprovalMode.Auto, PatchConfidence.Fuzzy, asker.Ask));
            Assert.Equal(0, asker.Calls);
        }

        [Fact]
        public async Task Manual_AsksAboutAFuzzyMatchToo()
        {
            var asker = new Asker(false);

            Assert.False(await PatchCardDecision.ApproveAsync(ApprovalMode.Manual, PatchConfidence.Fuzzy, asker.Ask));
            Assert.Equal(1, asker.Calls);
        }

        [Fact]
        public void TheQuestionNamesTheFileAndTheMatchQuality()
        {
            Assert.Equal("Apply patch to Widget.cs? (Exact ✓)",
                PatchCardDecision.Question("Widget.cs", PatchConfidence.Exact));

            Assert.Equal("Apply patch to Widget.cs? (Fuzzy ⚠)",
                PatchCardDecision.Question("Widget.cs", PatchConfidence.Fuzzy));
        }

        [Fact]
        public void TheDeclineLineNamesTheFile_AndDoesNotSayAutoApproved()
        {
            string line = PatchCardDecision.DeclinedLine("Widget.cs");

            Assert.Equal("[PATCH] Declined by user: Widget.cs\n", line);
            Assert.DoesNotContain("Auto-approved", line);
        }
    }
}
