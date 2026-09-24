// File: ResumeReplayTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// What a resumed session puts back on the screen.
//
// The drawing needs Terminal.Gui and cannot be reached from here. What can be is the
// decision: which of the restored messages is a question and which is an answer, that they
// come back in the order they happened, and that the rule closes the replay.
//
// The rule is not decoration. Everything above it is drawn by the same code a live turn
// uses — that is the point, a replayed exchange should be indistinguishable from one you
// watched arrive — which is exactly why the boundary has to be there. Without it the
// operator cannot tell what they are about to add to from what they are looking back at.
//
// The other property worth stating is negative: Plan is given the arrays that went to the
// model, so the screen cannot disagree with the context behind it. A replay assembled from a
// second read of the store could, and would be worse than the blank pane it replaced.

using System.Linq;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class ResumeReplayTests
    {
        private static readonly string[] Roles    = { "user", "assistant", "user", "assistant" };
        private static readonly string[] Contents = { "First?", "First answer.", "Second?", "Second answer." };

        [Fact]
        public void QuestionsAndAnswersAlternate_InTheOrderTheyHappened()
        {
            var plan = ResumeReplay.Plan(Roles, Contents);

            Assert.Equal(
                new[] { ReplayKind.User, ReplayKind.Answer, ReplayKind.User, ReplayKind.Answer, ReplayKind.Rule },
                plan.Select(s => s.Kind));

            Assert.Equal(
                new[] { "First?", "First answer.", "Second?", "Second answer." },
                plan.Take(4).Select(s => s.Text));
        }

        [Fact]
        public void TheReplayIsClosedByTheRule()
        {
            var plan = ResumeReplay.Plan(Roles, Contents);

            Assert.Equal(ReplayKind.Rule, plan.Last().Kind);
            Assert.Equal(ResumeReplay.RuleText, plan.Last().Text);
            Assert.Single(plan, s => s.Kind == ReplayKind.Rule);
        }

        [Fact]
        public void TheRuleSaysWhichSideIsWhich()
        {
            // "──────" alone would be a divider between two things it does not name.
            Assert.Contains("resumed above", ResumeReplay.RuleText);
            Assert.Contains("new turns below", ResumeReplay.RuleText);
        }

        [Fact]
        public void AnAsciiRuleIsAvailableForATerminalWithoutBoxDrawing()
        {
            Assert.All(ResumeReplay.AsciiRuleText, c => Assert.True(c < 128));
            Assert.Contains("resumed above", ResumeReplay.AsciiRuleText);
        }

        [Fact]
        public void NothingResumedDrawsNothing_NotEvenTheRule()
        {
            // A rule on its own would announce a boundary in a session that has no other side.
            Assert.Empty(ResumeReplay.Plan(new string[0], new string[0]));
            Assert.Empty(ResumeReplay.Plan(null, null));
            Assert.Empty(ResumeReplay.Plan(Roles, null));
        }

        [Fact]
        public void RaggedArraysDrawNothing()
        {
            // Mismatched lengths mean something upstream is broken, and a half-drawn
            // conversation is a worse answer to that than an empty one.
            Assert.Empty(ResumeReplay.Plan(new[] { "user", "assistant" }, new[] { "only one" }));
        }

        [Fact]
        public void AnUnexpectedRoleIsDrawnAsAnAnswer_NeverDropped()
        {
            // The pairing only ever emits user/assistant. A third would be new information,
            // and the transcript's job is to show it rather than decide it did not happen.
            var plan = ResumeReplay.Plan(new[] { "system" }, new[] { "something new" });

            Assert.Equal(ReplayKind.Answer, plan[0].Kind);
            Assert.Equal("something new", plan[0].Text);
        }

        [Fact]
        public void ThePlanIsBuiltFromTheArraysTheModelWasGiven()
        {
            // The contract that keeps the screen and the context in agreement: whatever
            // PairMessages produced and PrependMessages consumed is what is drawn, verbatim,
            // with no second filtering pass of its own.
            var rows = new[]
            {
                new HistoryMessage { Role = "user",      Content = "What does LoopDriver do?" },
                new HistoryMessage { Role = "assistant", Content = "[CONTEXT] 12 / 40\nIt drives one iteration." },
            };

            var (roles, contents, _) = SessionResume.PairMessages(rows);
            var plan = ResumeReplay.Plan(roles, contents);

            Assert.Equal(roles.Length + 1, plan.Count);
            for (int i = 0; i < roles.Length; i++)
                Assert.Equal(contents[i], plan[i].Text);

            // And the decoration the pairing stripped stays stripped on screen.
            Assert.DoesNotContain("[CONTEXT]", plan[1].Text);
        }

        [Fact]
        public void EveryRestoredMessageIsDrawn()
        {
            // The count the [RESUME] line reports is the count the operator should see.
            var plan = ResumeReplay.Plan(Roles, Contents);

            Assert.Equal(Roles.Length, plan.Count(s => s.Kind != ReplayKind.Rule));
        }
    }
}
