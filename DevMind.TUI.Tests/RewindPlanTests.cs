// File: RewindPlanTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The cut is the one place a /rewind off-by-one would be silent — a row too far in and the
// fork carries the turn it was supposed to undo; a row too far short and it loses the last
// real exchange. These tables pin it: the numbering skips synthetic user rows, the cut
// lands EXACTLY before turn N's user message in the store's chronological order, and the
// rows of turn N-1 — assistant rows included — are the ones that cross the cut.
//
// The rows are built the way the store returns them (TurnIndex ASC, CreatedAt ASC), and
// one of them deliberately breaks the naive arithmetic (two user rows under ONE turn
// number — the ask_caller answer re-enters the loop under the same turn clock) to prove
// the cut stands on position, not on TurnIndex math.

using DevMind;
using System;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class RewindPlanTests
    {
        // A session's rows in the store's own order. TurnIndex is the context-aging clock:
        // one value per user turn, shared by every iteration of it.
        private static HistoryMessage Row(string sessionId, int turn, string role, string content,
                                          bool synthetic = false, int createdMs = 0)
            => new HistoryMessage
            {
                SessionId = sessionId,
                MachineName = "beast",
                TurnIndex = turn,
                Role = role,
                Content = content,
                CreatedAt = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc).AddMilliseconds(createdMs),
                IsSynthetic = synthetic,
            };

        private const string S = "sess-1";

        /// <summary>
        /// A realistic FIVE-turn session:
        ///  turn 1: "Fix the login bug." → answer (clock 0)
        ///  turn 2: "Now add tests." → synthetic continuation + second answer (same clock 1)
        ///  turn 3: "Why did you skip the edge case?" → ask_caller (clock 2)
        ///  turn 4: "Because the token can expire mid-flight." — the ask_caller ANSWER.
        ///           It is a real user turn (the user typed it), and it shares clock 2 with the
        ///           question (TurnClock holds on a needs_input answer). Two REAL user rows under
        ///           ONE turn number — the case TurnIndex arithmetic cannot express.
        ///  turn 5: "Ship it." → answer (clock 3)
        /// </summary>
        private static HistoryMessage[] Session() => new[]
        {
            Row(S, 0, "user", "Fix the login bug."),
            Row(S, 0, "assistant", "Fixed it in Auth.cs."),

            Row(S, 1, "user", "Now add tests."),
            Row(S, 1, "user", SyntheticPrompts.Continue, synthetic: true, createdMs: 1),
            Row(S, 1, "assistant", "Tests added for the happy path."),
            Row(S, 1, "assistant", "And the token-expiry case."),

            // Two REAL user rows under ONE turn number: the ask_caller question, then the
            // user's answer to it.
            Row(S, 2, "user", "Why did you skip the edge case?"),
            Row(S, 2, "user", "Because the token can expire mid-flight.", createdMs: 2),
            Row(S, 2, "assistant", "That is the edge case — covered now."),

            Row(S, 3, "user", "Ship it."),
            Row(S, 3, "assistant", "Shipped."),
        };

        // ── Numbering ─────────────────────────────────────────────────────────

        [Fact]
        public void ListTurns_NumberOnlyRealUserRows_SkippingSynthetic()
        {
            var turns = RewindPlanner.ListTurns(Session());

            Assert.Equal(5, turns.Count);
            Assert.Equal(1, turns[0].Number);
            Assert.Equal("Fix the login bug.", turns[0].Prompt);
            Assert.Equal(2, turns[1].Number);
            Assert.Equal("Now add tests.", turns[1].Prompt);
            Assert.Equal(3, turns[2].Number);
            Assert.Equal("Why did you skip the edge case?", turns[2].Prompt);
            Assert.Equal(4, turns[3].Number);
            // The ask_caller answer is a real user turn — the user typed it.
            Assert.Equal("Because the token can expire mid-flight.", turns[3].Prompt);
            Assert.Equal(5, turns[4].Number);
            Assert.Equal("Ship it.", turns[4].Prompt);
        }

        [Fact]
        public void ListTurns_FlaglessLegacySyntheticPrompt_IsAlsoSkipped()
        {
            var rows = new[]
            {
                Row(S, 0, "user", "Do the thing."),
                Row(S, 0, "assistant", "Done."),
                // Written before the IsSynthetic column existed: no flag, only the text.
                Row(S, 1, "user", SyntheticPrompts.Continue, synthetic: false, createdMs: 1),
                Row(S, 1, "assistant", "Done for real."),
                Row(S, 2, "user", "Second question."),
            };

            var turns = RewindPlanner.ListTurns(rows);

            Assert.Equal(2, turns.Count);
            Assert.Equal("Do the thing.", turns[0].Prompt);
            Assert.Equal("Second question.", turns[1].Prompt);
        }

        [Fact]
        public void ListTurns_EmptySession_IsEmpty()
        {
            Assert.Empty(RewindPlanner.ListTurns(Array.Empty<HistoryMessage>()));
            Assert.Empty(RewindPlanner.ListTurns(null));
        }

        [Fact]
        public void ListTurnsText_SharesTheNumbering_AndShowsTimeAndFirstLine()
        {
            string text = RewindPlanner.ListTurnsText(Session());

            // Times are LOCAL (the store's CreatedAt is UTC). The Session() rows are at
            // 10:00 UTC + createdMs, so compute the expected local time the same way.
            string t0 = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm");
            Assert.Contains($"[1] {t0}  Fix the login bug.", text);
            Assert.Contains($"[2] {t0}  Now add tests.", text);
            Assert.Contains($"[3] {t0}  Why did you skip the edge case?", text);
            Assert.Contains($"[4] {t0}  Because the token can expire mid-flight.", text);
            Assert.Contains($"[5] {t0}  Ship it.", text);
            Assert.DoesNotContain("Continue with the task.", text);
            Assert.Contains("/rewind <n>", text);
        }

        [Fact]
        public void FirstLineOf_TruncatesToSeventyChars_WithAnEllipsisInsideTheCap()
        {
            string shortLine = RewindPlanner.FirstLineOf("short");
            Assert.Equal("short", shortLine);

            string longLine = new string('a', 100) + "\nsecond line";
            string cut = RewindPlanner.FirstLineOf(longLine);
            Assert.Equal(70, cut.Length);
            Assert.EndsWith("…", cut);

            // First line only — the rest of a multi-line prompt is not displayed.
            Assert.Equal("first", RewindPlanner.FirstLineOf("first\nsecond line"));
            Assert.Equal("", RewindPlanner.FirstLineOf("  \n"));
            Assert.Equal("", RewindPlanner.FirstLineOf(null));
        }

        // ── The cut ───────────────────────────────────────────────────────────

        [Fact]
        public void Build_TurnTwo_CutLandsExactlyBeforeThatUserRow()
        {
            var plan = RewindPlanner.Build(Session(), 2, "fork-1", DateTime.UtcNow, out string error);

            Assert.Null(error);
            Assert.Equal(2, plan.TurnNumber);
            // Exactly turn 0's rows: the question and its answer. Nothing from turn 1 on.
            Assert.Equal(new[]
            {
                ("user", "Fix the login bug."),
                ("assistant", "Fixed it in Auth.cs."),
            }, plan.MessagesToCopy.Select(m => (m.Role, m.Content)));
            // The forked rows are stamped for the new session, TurnIndex preserved.
            Assert.All(plan.MessagesToCopy, m => Assert.Equal("fork-1", m.SessionId));
            Assert.Equal(0, plan.MessagesToCopy[0].TurnIndex);
            // The restored prompt is turn 2's ORIGINAL text.
            Assert.Equal("Now add tests.", plan.RestoredPrompt);
        }

        [Fact]
        public void Build_TurnOne_CopiesNothing_AndRestoresTheFirstPrompt()
        {
            var plan = RewindPlanner.Build(Session(), 1, "fork-1", DateTime.UtcNow, out string error);

            Assert.Null(error);
            Assert.Empty(plan.MessagesToCopy);
            Assert.Equal("Fix the login bug.", plan.RestoredPrompt);
        }

        [Fact]
        public void Build_TurnFive_KeepsAssistantAndSyntheticRowsOfThePreviousTurns()
        {
            var plan = RewindPlanner.Build(Session(), 5, "fork-1", DateTime.UtcNow, out string error);

            Assert.Null(error);
            // Everything before "Ship it." — assistant rows AND the synthetic continuation,
            // in order, including the ask_caller answer (a real turn) under clock 2.
            Assert.Equal(new[]
            {
                ("user", "Fix the login bug."),
                ("assistant", "Fixed it in Auth.cs."),
                ("user", "Now add tests."),
                ("user", SyntheticPrompts.Continue),
                ("assistant", "Tests added for the happy path."),
                ("assistant", "And the token-expiry case."),
                ("user", "Why did you skip the edge case?"),
                ("user", "Because the token can expire mid-flight."),
                ("assistant", "That is the edge case — covered now."),
            }, plan.MessagesToCopy.Select(m => (m.Role, m.Content)));
            Assert.Equal("Ship it.", plan.RestoredPrompt);
        }

        [Fact]
        public void Build_CutStandsOnPosition_NotTurnIndexArithmetic()
        {
            // Two REAL user rows share turn number 2 (the ask_caller question and the
            // user's answer). A plan for turn 4 (the answer) must copy the question — which
            // carries the SAME TurnIndex as the cut — because the cut is the answer row's
            // POSITION, not "every row with TurnIndex < 2" or "< 3". Arithmetic on TurnIndex
            // alone can express neither: <2 drops the question, <3 keeps the answer.
            var plan = RewindPlanner.Build(Session(), 4, "fork-1", DateTime.UtcNow, out string error);

            Assert.Null(error);
            Assert.Equal(7, plan.MessagesToCopy.Length);
            Assert.Contains(plan.MessagesToCopy, m =>
                m.Role == "user" && m.Content == "Why did you skip the edge case?");
            // The answer row itself is NOT copied — the fork goes before it.
            Assert.DoesNotContain(plan.MessagesToCopy, m =>
                m.Role == "user" && m.Content == "Because the token can expire mid-flight.");
            Assert.Equal("Because the token can expire mid-flight.", plan.RestoredPrompt);
        }

        // ── Errors ────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        [InlineData(6)]
        public void Build_OutOfRange_IsAnErrorNamingTheRealRange(int turn)
        {
            var plan = RewindPlanner.Build(Session(), turn, "fork-1", DateTime.UtcNow, out string error);

            Assert.Null(plan);
            Assert.Contains("out of range", error);
            Assert.Contains("5 user", error);
        }

        [Fact]
        public void Build_NoUserTurns_IsAnError()
        {
            var onlySynthetic = new[]
            {
                Row(S, 0, "user", SyntheticPrompts.Continue, synthetic: true),
                Row(S, 0, "assistant", "Acknowledged."),
            };

            var plan = RewindPlanner.Build(onlySynthetic, 1, "fork-1", DateTime.UtcNow, out string error);

            Assert.Null(plan);
            Assert.Contains("no user turns", error);
        }

        [Fact]
        public void Build_OnlyAssistantRows_IsAnError()
        {
            var orphan = new[] { Row(S, 0, "assistant", "A row with no question.") };

            var plan = RewindPlanner.Build(orphan, 1, "fork-1", DateTime.UtcNow, out string error);

            Assert.Null(plan);
            Assert.Contains("no user turns", error);
        }

        // ── Title ─────────────────────────────────────────────────────────────

        [Fact]
        public void BuildTitle_UsesTheOldTitle_WithTheTurnNumber()
        {
            Assert.Equal("Auth work (rewound from #2)",
                RewindPlanner.BuildTitle("Auth work", Session(), 2));
        }

        [Fact]
        public void BuildTitle_UntitledSession_FallsBackToTheFirstPrompt()
        {
            Assert.Equal("Fix the login bug. (rewound from #3)",
                RewindPlanner.BuildTitle("", Session(), 3));
        }

        [Fact]
        public void BuildTitle_EmptyFirstPrompt_IsUntitled()
        {
            var blank = new[] { Row(S, 0, "user", "   "), Row(S, 1, "user", "   ") };
            Assert.Equal("(untitled) (rewound from #2)",
                RewindPlanner.BuildTitle(null, blank, 2));
        }

        // ── Local time in the listing ─────────────────────────────────────────

        /// <summary>
        /// The store's CreatedAt is UTC; the operator reads local wall-clock time. The
        /// listing must convert, and it must include the date only when the turns span
        /// more than one local day. Single-day session: HH:mm only. Multi-day: yyyy-MM-dd HH:mm.
        /// </summary>
        [Fact]
        public void ListTurnsText_ShowsLocalTime_AndDateOnlyWhenTurnsSpanDays()
        {
            // Single-day session: all turns on the same UTC day. No date in the listing.
            var singleDay = new[]
            {
                Row(S, 0, "user", "First prompt."),
                Row(S, 1, "user", "Second prompt."),
            };
            string text = RewindPlanner.ListTurnsText(singleDay);
            // No date component (yyyy-MM-dd) — single day.
            Assert.DoesNotContain("2026-09-27", text);
            // Has HH:mm times (computed in local time).
            string localTime = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc)
                .ToLocalTime().ToString("HH:mm");
            Assert.Contains(localTime, text);

            // Multi-day session: turns on two different UTC days that don't collapse into
            // the same local day (24-hour gap). Date IS shown.
            var multiDay = new[]
            {
                new HistoryMessage { SessionId = S, MachineName = "beast", TurnIndex = 0, Role = "user",
                    Content = "Day one prompt.", CreatedAt = new DateTime(2026, 9, 26, 20, 0, 0, DateTimeKind.Utc) },
                new HistoryMessage { SessionId = S, MachineName = "beast", TurnIndex = 1, Role = "user",
                    Content = "Day two prompt.", CreatedAt = new DateTime(2026, 9, 27, 20, 0, 0, DateTimeKind.Utc) },
            };
            string multiText = RewindPlanner.ListTurnsText(multiDay);
            // Both local dates appear (the turns are 24h apart — they cannot be the same local day).
            string d1 = new DateTime(2026, 9, 26, 20, 0, 0, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd");
            string d2 = new DateTime(2026, 9, 27, 20, 0, 0, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd");
            Assert.Contains(d1, multiText);
            Assert.Contains(d2, multiText);
        }

        // ── The cut, by name ──────────────────────────────────────────────────

        [Fact]
        public void MutationCheck_CutByContentName_IsStableUnderShuffledTies()
        {
            // The cut must find turn 4 by its content, "Because the token can expire
            // mid-flight.", no matter how the rows within a turn shuffle — the store orders
            // by (TurnIndex, CreatedAt), and millisecond ties inside one turn are not a
            // promise. Name the boundary by what it is, not by where it sits.
            var shuffled = Session();
            var q = shuffled.Single(m => m.Role == "user" && m.Content == "Why did you skip the edge case?");
            var a = shuffled.Single(m => m.Role == "user" && m.Content == "Because the token can expire mid-flight.");
            (q.CreatedAt, a.CreatedAt) = (a.CreatedAt.AddMilliseconds(5), q.CreatedAt.AddMilliseconds(-5));

            var plan = RewindPlanner.Build(shuffled, 4, "fork-1", DateTime.UtcNow, out string error);

            Assert.Null(error);
            // The answer row that OPENS turn 4 is still the boundary: not copied.
            Assert.DoesNotContain(plan.MessagesToCopy, m => m.Content == "Because the token can expire mid-flight.");
            // The question (which now sorts FIRST within its turn) is copied either way —
            // it precedes the boundary in the store's order.
            Assert.Contains(plan.MessagesToCopy, m => m.Content == "Why did you skip the edge case?");
            Assert.Equal("Because the token can expire mid-flight.", plan.RestoredPrompt);
        }
    }
}
