// File: SessionResumeTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Turning stored rows back into a conversation.
//
// This filter decides what a resumed session remembers, and every one of its rules exists
// because the naive version gets something wrong that nobody would notice until the model
// answered oddly. The agentic loop writes synthetic user turns between a real question and
// the prose that eventually answers it, so pairing rows in order attaches the answer to a
// machine-generated "continue" and the real question is left with nothing. Older rows carry
// engine status lines inside the assistant text, and feeding those back as the model's own
// past words teaches it that reciting context meters is part of answering.
//
// None of that was tested, because it lived inside a TUI command handler that needed a
// running terminal to reach. It is in Core now, and these are the rules.

using System.Collections.Generic;
using Xunit;

namespace DevMind.Core.Tests
{
    public class SessionResumeTests
    {
        private static HistoryMessage User(string content, bool synthetic = false)
            => new HistoryMessage { Role = "user", Content = content, IsSynthetic = synthetic };

        private static HistoryMessage Assistant(string content)
            => new HistoryMessage { Role = "assistant", Content = content };

        [Fact]
        public void SyntheticUserRows_AreSkipped_AndDoNotOpenASegment()
        {
            // The answer belongs to the real question, not to the "continue" that preceded it.
            var rows = new[]
            {
                User("What does LoopDriver do?"),
                User("continue", synthetic: true),
                Assistant("It drives one agentic iteration."),
            };

            var (roles, contents, skipped) = SessionResume.PairMessages(rows);

            Assert.Equal(new[] { "user", "assistant" }, roles);
            Assert.Equal("What does LoopDriver do?", contents[0]);
            Assert.Equal("It drives one agentic iteration.", contents[1]);
            Assert.Equal(1, skipped);
        }

        [Fact]
        public void ALegacySyntheticRow_IsRecognisedByItsText()
        {
            // Rows written before the IsSynthetic column existed carry no flag. Without the
            // text check every legacy session resumes with the loop's scaffolding in it.
            var rows = new List<HistoryMessage> { User("Real question?") };
            foreach (string prompt in SyntheticPrompts.All)
                rows.Add(User(prompt));   // flag NOT set
            rows.Add(Assistant("The answer."));

            var (roles, _, skipped) = SessionResume.PairMessages(rows);

            Assert.Equal(2, roles.Length);
            Assert.Equal(SyntheticPrompts.All.Length, skipped);
        }

        [Fact]
        public void AssistantRowsBeforeTheFirstRealQuestion_AreDropped()
        {
            var rows = new[]
            {
                Assistant("Orphaned prose from a session that opened oddly."),
                User("The first real question."),
                Assistant("Its answer."),
            };

            var (roles, contents, skipped) = SessionResume.PairMessages(rows);

            Assert.Equal(2, roles.Length);
            Assert.Equal("The first real question.", contents[0]);
            Assert.Equal(1, skipped);
        }

        [Fact]
        public void DecorationLinesAreStripped_WhereverTheyOccur()
        {
            var rows = new[]
            {
                User("Question?"),
                Assistant("[CONTEXT] 12 / 40\nReal prose.\n[LLM] 51 tok in 0.5s\nMore prose."),
            };

            var (_, contents, skipped) = SessionResume.PairMessages(rows);

            Assert.Equal("Real prose.\nMore prose.", contents[1]);
            Assert.Equal(0, skipped);
        }

        [Fact]
        public void AnAllDecorationAnswer_CountsAsSkipped()
        {
            var rows = new[]
            {
                User("Question?"),
                Assistant("[CONTEXT] 12 / 40\n[TOOL_USE] Processing tool call(s)...\n"),
                Assistant("The real answer."),
            };

            var (roles, contents, skipped) = SessionResume.PairMessages(rows);

            Assert.Equal(2, roles.Length);
            Assert.Equal("The real answer.", contents[1]);
            Assert.Equal(1, skipped);
        }

        [Fact]
        public void ARealQuestionWithNoSurvivingAnswer_IsDropped()
        {
            // Half an exchange is worse than none: the model reads an unanswered question as
            // something it failed to answer.
            var rows = new[]
            {
                User("Unanswered question."),
                Assistant("[DROPPED] 2 tool results"),
                User("Answered question."),
                Assistant("Answer."),
            };

            var (roles, contents, skipped) = SessionResume.PairMessages(rows);

            Assert.Equal(2, roles.Length);
            Assert.Equal("Answered question.", contents[0]);
            Assert.Equal(2, skipped);   // the stripped-empty answer, and the question it left bare
        }

        [Fact]
        public void TwoQuestions_ProduceTwoPairsInOrder()
        {
            var rows = new[]
            {
                User("First?"),
                Assistant("First answer."),
                User("Second?"),
                Assistant("Second answer."),
            };

            var (roles, contents, skipped) = SessionResume.PairMessages(rows);

            Assert.Equal(new[] { "user", "assistant", "user", "assistant" }, roles);
            Assert.Equal(new[] { "First?", "First answer.", "Second?", "Second answer." }, contents);
            Assert.Equal(0, skipped);
        }

        [Fact]
        public void ManyAssistantRowsInOneSegment_JoinIntoOneAnswer()
        {
            // A real question often produces several assistant rows across synthetic
            // continuation turns. They are one answer, and they must not become several
            // assistant messages in a row — the API expects alternation.
            var rows = new[]
            {
                User("Do the work."),
                Assistant("Reading the file."),
                User("continue", synthetic: true),
                Assistant("Done."),
            };

            var (roles, contents, _) = SessionResume.PairMessages(rows);

            Assert.Equal(new[] { "user", "assistant" }, roles);
            Assert.Equal("Reading the file.\n\nDone.", contents[1]);
        }

        [Fact]
        public void RolesAndContentsAlternate_AndAreTheSameLength()
        {
            var rows = new[]
            {
                User("A?"), Assistant("a"), User("B?"), Assistant("b"), User("C?"), Assistant("c"),
            };

            var (roles, contents, _) = SessionResume.PairMessages(rows);

            Assert.Equal(roles.Length, contents.Length);
            for (int i = 0; i < roles.Length; i++)
                Assert.Equal(i % 2 == 0 ? "user" : "assistant", roles[i]);
        }

        // ── Which session a launch flag opens ────────────────────────────────────

        private static SessionSummary Session(string id, string title = "")
            => new SessionSummary { SessionId = id, Title = title };

        [Fact]
        public void ContinueTakesTheMostRecentSession()
        {
            // The store orders newest first; --continue means the one you were just in.
            var sessions = new[] { Session("newest"), Session("middle"), Session("oldest") };

            Assert.Equal("newest", SessionResume.SelectSession(sessions, null, continueLatest: true).SessionId);
        }

        [Fact]
        public void ResumeReachesPastTheMostRecentSession_ToTheIdItWasGiven()
        {
            var sessions = new[] { Session("newest"), Session("middle"), Session("oldest") };

            Assert.Equal("oldest", SessionResume.SelectSession(sessions, "oldest", continueLatest: false).SessionId);
        }

        [Fact]
        public void AnIdMatchesRegardlessOfCase_AndIgnoresSurroundingSpace()
        {
            // Ids are copied out of /history, never typed from memory.
            var sessions = new[] { Session("2026-09-20T101500Z-pid42") };

            Assert.NotNull(SessionResume.SelectSession(sessions, "  2026-09-20t101500z-PID42  ", false));
        }

        [Fact]
        public void NoMatchAndNoSessions_AreBothNull()
        {
            var sessions = new[] { Session("a") };

            Assert.Null(SessionResume.SelectSession(sessions, "b", continueLatest: false));
            Assert.Null(SessionResume.SelectSession(sessions, null, continueLatest: false));
            Assert.Null(SessionResume.SelectSession(new SessionSummary[0], null, continueLatest: true));
            Assert.Null(SessionResume.SelectSession(null, "a", continueLatest: false));
        }

        [Fact]
        public void NothingToPair_IsEmptyRatherThanAnError()
        {
            Assert.Empty(SessionResume.PairMessages(null).Roles);
            Assert.Empty(SessionResume.PairMessages(new HistoryMessage[0]).Roles);

            var (roles, _, skipped) = SessionResume.PairMessages(new[] { Assistant("orphan") });
            Assert.Empty(roles);
            Assert.Equal(1, skipped);
        }
    }
}
