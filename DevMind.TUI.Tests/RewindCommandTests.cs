// File: RewindCommandTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// /rewind end-to-end through the CommandContext: the fork is a NEW session in the store,
// the original is untouched, the /resume load path is what re-populates the TUI, and the
// prompt comes back in the input box without being sent.
//
// The store tests use SQLite (file-backed, no server) — the same provider the app uses —
// so "the original is byte-for-byte unchanged" is checked against real rows, not a fake.

using DevMind;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class RewindCommandTests : IDisposable
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"devmind-rewind-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* temp file */ }
        }

        // ── In-memory store for handler tests ─────────────────────────────────

        private sealed class ScriptedHistoryStore : IHistoryStore
        {
            public HistoryMessage[] CurrentSessionMessages { get; set; } = Array.Empty<HistoryMessage>();
            public SessionSummary[] Sessions { get; set; } = Array.Empty<SessionSummary>();
            public HistoryMessage[]? Saved { get; private set; }
            public string? UpsertedId { get; private set; }
            public string? TitledId { get; private set; }
            public string? Title { get; private set; }

            public Task<HistoryMessage[]> LoadSessionMessagesAsync(string sessionId)
                => Task.FromResult(CurrentSessionMessages);

            public Task SaveMessagesAsync(HistoryMessage[] messages)
            { Saved = messages; return Task.CompletedTask; }

            public Task UpsertSessionAsync(string sessionId, string machineName)
            { UpsertedId = sessionId; return Task.CompletedTask; }

            public Task SetSessionTitleAsync(string sessionId, string title)
            { TitledId = sessionId; Title = title; return Task.CompletedTask; }

            public Task<SessionSummary[]> ListSessionsAsync(string machineName)
                => Task.FromResult(Sessions);

            public Task<HistoryMessage[]> LoadMessagesAsync(string machineName, int maxTurns)
                => Task.FromResult(Array.Empty<HistoryMessage>());
            public Task InitAsync() => Task.CompletedTask;
            public Task CloseAsync() => Task.CompletedTask;
        }

        private static HistoryMessage Row(string sessionId, int turn, string role, string content,
                                          bool synthetic = false)
            => new HistoryMessage
            {
                SessionId = sessionId,
                MachineName = "beast",
                TurnIndex = turn,
                Role = role,
                Content = content,
                CreatedAt = new DateTime(2026, 9, 27, 10, 0, turn, 0, DateTimeKind.Utc),
                IsSynthetic = synthetic,
            };

        private const string S = "2026-09-27T100000Z-pid1";

        private static HistoryMessage[] Session() => new[]
        {
            Row(S, 0, "user", "Fix the login bug."),
            Row(S, 0, "assistant", "Fixed it in Auth.cs."),
            Row(S, 1, "user", "Now add tests."),
            Row(S, 1, "user", SyntheticPrompts.Continue, synthetic: true),
            Row(S, 1, "assistant", "Tests added."),
            Row(S, 2, "user", "Ship it."),
            Row(S, 2, "assistant", "Shipped."),
        };

        private sealed class HostState
        {
            public string? AdoptedId;
            public bool ResetCalled;
            public string? InputText;
            public string[]? PrependedRoles;
            public string[]? PrependedContents;
            public string[]? ReplayedRoles;
            public string[]? ReplayedContents;
        }

        private static (CommandContext ctx, ScriptedHistoryStore store, HostState host)
            Context(HistoryMessage[] messages)
        {
            var store = new ScriptedHistoryStore
            {
                CurrentSessionMessages = messages,
                Sessions = new[]
                {
                    new SessionSummary { SessionId = S, Title = "Auth work", MessageCount = 6 },
                },
            };
            var host = new HostState();
            var ctx = new CommandContext
            {
                HistoryStore = store,
                SessionId = S,
                MachineName = "beast",
                ResetConversation = () => host.ResetCalled = true,
                AdoptSessionId = id => host.AdoptedId = id,
                SetInputBoxText = text => host.InputText = text,
                PrependMessages = (roles, contents) =>
                {
                    host.PrependedRoles = roles;
                    host.PrependedContents = contents;
                },
                ReplayTranscript = (roles, contents) =>
                {
                    host.ReplayedRoles = roles;
                    host.ReplayedContents = contents;
                },
            };
            return (ctx, store, host);
        }

        // ── The listing ───────────────────────────────────────────────────────

        [Fact]
        public async Task Rewind_NoArgs_ListsThisSessionsTurns_NumberedLikeHistory()
        {
            var (ctx, _, _) = Context(Session());

            var result = await SlashCommand.Dispatch("/rewind", ctx);

            Assert.False(result.IsError, result.Message);
            // Times are LOCAL (the store's CreatedAt is UTC). The Session() rows are at
            // 10:00 UTC + turn*MILLISECONDS (the Row helper's createdMs param is milliseconds,
            // not minutes), so all three turns share the same HH:mm. Compute the expected
            // local time the same way the listing does.
            string t0 = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm");
            Assert.Contains($"[1] {t0}  Fix the login bug.", result.Message);
            Assert.Contains($"[2] {t0}  Now add tests.", result.Message);
            Assert.Contains($"[3] {t0}  Ship it.", result.Message);
            Assert.DoesNotContain("Continue with the task.", result.Message);
        }

        [Fact]
        public async Task Rewind_NoHistory_SaysSo()
        {
            var ctx = new CommandContext { HistoryStore = null, SessionId = S };

            var result = await SlashCommand.Dispatch("/rewind", ctx);

            Assert.True(result.IsError);
            Assert.Contains("History is not enabled", result.Message);
        }

        // ── The fork ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Rewind_N_Forks_ANewSession_LeavesTheOriginal_AndLoadsTheResumePath()
        {
            var (ctx, store, host) = Context(Session());

            var result = await SlashCommand.Dispatch("/rewind 3", ctx);

            Assert.False(result.IsError, result.Message);

            // The fork: the five rows before turn 3 ("Ship it."), stamped for the NEW id.
            Assert.NotNull(store.Saved);
            Assert.Equal(5, store.Saved.Length);
            Assert.Equal("Fix the login bug.", store.Saved[0].Content);
            Assert.Equal("Fixed it in Auth.cs.", store.Saved[1].Content);
            Assert.Equal("Now add tests.", store.Saved[2].Content);
            Assert.Equal(SyntheticPrompts.Continue, store.Saved[3].Content);
            Assert.Equal("Tests added.", store.Saved[4].Content);
            Assert.All(store.Saved, m => Assert.Equal(host.AdoptedId, m.SessionId));
            Assert.All(store.Saved, m => Assert.NotEqual(S, m.SessionId));
            // TurnIndex preserved — the fork's order is the original's.
            Assert.Equal(new[] { 0, 0, 1, 1, 1 }, store.Saved.Select(m => m.TurnIndex).ToArray());

            // The new session is upserted and titled, and the host adopted it.
            Assert.Equal(host.AdoptedId, store.UpsertedId);
            Assert.Equal(host.AdoptedId, store.TitledId);
            Assert.Equal("Auth work (rewound from #3)", store.Title);
            Assert.NotNull(host.AdoptedId);
            Assert.NotEqual(S, host.AdoptedId);

            // Reset ran (the /new reset), THEN the /resume load path with the planned pairs.
            Assert.True(host.ResetCalled);
            // The /resume pairing: the synthetic continuation is scaffolding, so the two
            // answers join turn 2's one segment — the same pairs /resume of the fork would make.
            Assert.Equal(new[] { "user", "assistant", "user", "assistant" }, host.PrependedRoles);
            Assert.Equal(new[]
            {
                "Fix the login bug.",
                "Fixed it in Auth.cs.",
                "Now add tests.",
                "Tests added.",
            }, host.PrependedContents);
            // The replay is the SAME pairs the model was given — the transcript cannot
            // disagree with the context behind it.
            Assert.Equal(host.PrependedRoles, host.ReplayedRoles);
            Assert.Equal(host.PrependedContents, host.ReplayedContents);

            // Turn 3's ORIGINAL prompt is back in the input box, not sent.
            Assert.Equal("Ship it.", host.InputText);

            // The one dim line.
            Assert.Contains("Rewound to before turn 3", result.Message);
            Assert.Contains("Original kept", result.Message);
            Assert.Contains("/resume", result.Message);
            Assert.Contains("Files on disk were NOT changed", result.Message);
        }

        /// <summary>
        /// The threading seam: /rewind's UI effects (transcript replay, input-box text)
        /// MUST go through ctx.RunOnUiThread — the dispatcher awaits on a pool thread, and
        /// Terminal.Gui throws "Call from invalid thread" for off-thread view access. This
        /// test wires a RunOnUiThread that records every effect it is asked to marshal, and a
        /// SetInputBoxText / ReplayTranscript that THROW if called directly (simulating what
        /// Terminal.Gui does off-thread). On the old wiring (no seam) both callbacks fire
        /// directly and the test fails; on the new wiring they fire only through the seam.
        /// </summary>
        [Fact]
        public async Task Rewind_UiEffects_GoThroughTheUiThreadSeam_NotDirectly()
        {
            var (ctx, _, host) = Context(Session());

            var uiThreadEffects = new System.Collections.Generic.List<string>();

            // The seam: record that it was asked to marshal. Do NOT run the action — the
            // callbacks throw to prove they would crash if hit off-thread. The point of this
            // test is that the handler routes through the seam, not that the seam executes.
            ctx.RunOnUiThread = action =>
            {
                uiThreadEffects.Add("marshalled");
                // action() intentionally NOT called — the callbacks throw by design.
            };

            // The view callbacks THROW if hit off-thread (simulating Terminal.Gui's
            // "Call from invalid thread"). The handler's catch block turns the throw into an
            // error CommandResult — the test asserts the error message names the off-thread
            // call, proving the effect did NOT go through the seam. On the new wiring the
            // seam intercepts first, the callbacks never throw, and the rewind succeeds.
            ctx.ReplayTranscript = (roles, contents) =>
            {
                throw new InvalidOperationException("ReplayTranscript called off the UI thread");
            };
            ctx.SetInputBoxText = text =>
            {
                throw new InvalidOperationException("SetInputBoxText called off the UI thread");
            };

            var result = await SlashCommand.Dispatch("/rewind 3", ctx);

            // The rewind succeeded — the effects went through the seam, not directly.
            Assert.False(result.IsError, result.Message);
            // Both UI effects were marshalled through the seam (ReplayTranscript + SetInputBoxText).
            Assert.Equal(2, uiThreadEffects.Count);
        }

        /// <summary>
        /// Without the seam, the UI effects fire directly on the pool thread and Terminal.Gui
        /// throws. The handler's catch block turns that into an error result naming the
        /// off-thread call. This test proves the OLD wiring (no RunOnUiThread) fails: the
        /// rewind is an error, and the error message names the off-thread ReplayTranscript.
        /// </summary>
        [Fact]
        public async Task Rewind_WithoutUiThreadSeam_FailsWithOffThreadError()
        {
            var (ctx, _, host) = Context(Session());
            // No RunOnUiThread — the OLD wiring. The effects fire directly.
            ctx.RunOnUiThread = null;

            ctx.ReplayTranscript = (roles, contents) =>
            {
                throw new InvalidOperationException("Call from invalid thread.");
            };
            ctx.SetInputBoxText = text =>
            {
                throw new InvalidOperationException("Call from invalid thread.");
            };

            var result = await SlashCommand.Dispatch("/rewind 3", ctx);

            // The rewind FAILED — the off-thread call threw and the catch block reported it.
            Assert.True(result.IsError, "Expected an error when UI effects fire off-thread");
            Assert.Contains("Call from invalid thread", result.Message);
        }

        [Fact]
        public async Task Rewind_One_WithNothingBefore_ForksAnEmptySession_AndRestoresTheFirstPrompt()
        {
            var (ctx, store, host) = Context(Session());

            var result = await SlashCommand.Dispatch("/rewind 1", ctx);

            Assert.False(result.IsError, result.Message);
            Assert.Null(store.Saved);                       // nothing to copy — no SaveMessagesAsync
            Assert.Equal(host.AdoptedId, store.UpsertedId); // the session record still exists
            Assert.Null(host.PrependedRoles);               // nothing to load
            Assert.Equal("Fix the login bug.", host.InputText);
        }

        [Fact]
        public async Task Rewind_WhileATurnRuns_IsRefused()
        {
            var (ctx, store, host) = Context(Session());
            ctx.IsTurnRunning = true;

            var result = await SlashCommand.Dispatch("/rewind 2", ctx);

            Assert.True(result.IsError);
            Assert.Contains("while a turn is running", result.Message);
            Assert.Null(store.Saved);
            Assert.Null(host.AdoptedId);
            Assert.False(host.ResetCalled);
        }

        [Theory]
        [InlineData("/rewind 0")]
        [InlineData("/rewind -1")]
        [InlineData("/rewind 4")]
        [InlineData("/rewind abc")]
        public async Task Rewind_OutOfRangeOrGarbage_SaysWhy_AndDoesNothing(string input)
        {
            var (ctx, store, host) = Context(Session());

            var result = await SlashCommand.Dispatch(input, ctx);

            Assert.True(result.IsError, result.Message);
            if (int.TryParse(input.Substring("/rewind ".Length), out int n) && n > 0)
                Assert.Contains("out of range", result.Message);
            else
                Assert.Contains("Usage:", result.Message);
            Assert.Null(store.Saved);
            Assert.Null(host.AdoptedId);
            Assert.False(host.ResetCalled);
        }

        [Fact]
        public async Task Rewind_NoUserTurns_SaysSo_AndDoesNothing()
        {
            var onlySynthetic = new[]
            {
                Row(S, 0, "user", SyntheticPrompts.Continue, synthetic: true),
                Row(S, 0, "assistant", "Acknowledged."),
            };
            var (ctx, store, host) = Context(onlySynthetic);

            var result = await SlashCommand.Dispatch("/rewind 1", ctx);

            Assert.True(result.IsError);
            Assert.Contains("no user turns", result.Message);
            Assert.Null(store.Saved);
            Assert.Null(host.AdoptedId);
        }

        // ── SQLite: the fork is exactly the plan; the original is untouched ───

        [Fact]
        public async Task Rewind_ThroughSqlite_TheForkMatchesThePlan_AndTheOriginalIsByteForByte()
        {
            var store = new SqliteHistoryStore(_dbPath);
            await store.InitAsync();

            string machine = "rewind-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await store.UpsertSessionAsync(S, machine);
            await store.SetSessionTitleAsync(S, "Auth work");
            await store.SaveMessagesAsync(new[]
            {
                Row(S, 0, "user", "Fix the login bug."),
                Row(S, 0, "assistant", "Fixed it in Auth.cs."),
                Row(S, 1, "user", "Now add tests."),
                Row(S, 1, "user", SyntheticPrompts.Continue, synthetic: true),
                Row(S, 1, "assistant", "Tests added."),
                Row(S, 2, "user", "Ship it."),
                Row(S, 2, "assistant", "Shipped."),
            });

            // The original, captured BEFORE the fork: id, title, and every row.
            HistoryMessage[] originalBefore = await store.LoadSessionMessagesAsync(S);
            SessionSummary originalSummaryBefore =
                (await store.ListSessionsAsync(machine)).First(x => x.SessionId == S);
            int sessionCountBefore = (await store.ListSessionsAsync(machine)).Length;

            var host = new HostState();
            var ctx = new CommandContext
            {
                HistoryStore = store,   // the REAL store — the handler writes the fork into it
                SessionId = S,
                MachineName = machine,
                ResetConversation = () => host.ResetCalled = true,
                AdoptSessionId = id => host.AdoptedId = id,
                SetInputBoxText = text => host.InputText = text,
                PrependMessages = (roles, contents) => { host.PrependedRoles = roles; },
                ReplayTranscript = (roles, contents) => { host.ReplayedRoles = roles; },
            };

            var result = await SlashCommand.Dispatch("/rewind 3", ctx);

            Assert.False(result.IsError, result.Message);
            string? forkId = host.AdoptedId;
            Assert.NotNull(forkId);
            Assert.NotEqual(S, forkId);

            // The new session contains EXACTLY the planned messages — nothing more,
            // nothing less, in order, with TurnIndex preserved.
            HistoryMessage[] fork = await store.LoadSessionMessagesAsync(forkId);
            Assert.Equal(new[]
            {
                ("user", "Fix the login bug."),
                ("assistant", "Fixed it in Auth.cs."),
                ("user", "Now add tests."),
                ("user", SyntheticPrompts.Continue),
                ("assistant", "Tests added."),
            }, fork.Select(m => (m.Role, m.Content)));
            Assert.Equal(new[] { 0, 0, 1, 1, 1 }, fork.Select(m => m.TurnIndex).ToArray());

            SessionSummary forkSummary = (await store.ListSessionsAsync(machine)).First(x => x.SessionId == forkId);
            Assert.Equal(5, forkSummary.MessageCount);
            Assert.Equal("Auth work (rewound from #3)", forkSummary.Title);

            // The original is byte-for-byte unchanged: same rows, same order, same title,
            // same count — and no row in it was re-stamped with the fork's id.
            HistoryMessage[] originalAfter = await store.LoadSessionMessagesAsync(S);
            Assert.Equal(originalBefore.Length, originalAfter.Length);
            for (int i = 0; i < originalBefore.Length; i++)
            {
                Assert.Equal(originalBefore[i].Role, originalAfter[i].Role);
                Assert.Equal(originalBefore[i].Content, originalAfter[i].Content);
                Assert.Equal(originalBefore[i].TurnIndex, originalAfter[i].TurnIndex);
                Assert.Equal(originalBefore[i].IsSynthetic, originalAfter[i].IsSynthetic);
                Assert.Equal(S, originalAfter[i].SessionId);
            }
            SessionSummary originalSummaryAfter =
                (await store.ListSessionsAsync(machine)).First(x => x.SessionId == S);
            Assert.Equal(originalSummaryBefore.MessageCount, originalSummaryAfter.MessageCount);
            Assert.Equal("Auth work", originalSummaryAfter.Title);

            // One session appeared — the fork.
            Assert.Equal(sessionCountBefore + 1, (await store.ListSessionsAsync(machine)).Length);

            await store.CloseAsync();
        }

        // ── Non-monotonic TurnIndex: the store's insertion order wins ───────────

        /// <summary>
        /// TurnIndex is the context-aging clock (llmClient.CurrentTurn), and it is NOT
        /// monotonic across a session: ClearHistory (/new) resets it to 0, so a turn after
        /// a /new gets a SMALLER index than the turn before it. The live session that
        /// motivated this fix had turns at 19:47 (ti=1), 19:48 (ti=2), 21:53 (ti=1), 21:53
        /// (ti=2), 21:53 (ti=3) — ordering by TurnIndex ASC listed them 19:47, 21:53, 21:53,
        /// 19:48, 21:53, and /rewind 2 cut the 21:53 turn instead of the 19:48 one.
        ///
        /// The store's Id column (AUTOINCREMENT rowid / SQL IDENTITY) IS insertion order.
        /// This test inserts rows with deliberately out-of-order TurnIndex and proves
        /// LoadSessionMessagesAsync returns them in insertion order, and that /rewind's cut
        /// follows that order — the listing and the cut agree.
        /// </summary>
        [Fact]
        public async Task Rewind_NonMonotonicTurnIndex_ListAndCutFollowInsertionOrder()
        {
            var store = new SqliteHistoryStore(_dbPath);
            await store.InitAsync();

            string machine = "nonmono-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await store.UpsertSessionAsync(S, machine);

            // Insert in chronological order, but TurnIndex goes 1, 2, 1, 2, 3 — the
            // post-/new reset makes later turns reuse smaller indices. The OLD ordering
            // (TurnIndex ASC, CreatedAt ASC) would list: ti1-19:47, ti1-21:53, ti2-19:48,
            // ti2-21:53, ti3-21:53. The CORRECT order is insertion order.
            var rows = new[]
            {
                // 19:47 UTC — turn 1 (ti=1)
                new HistoryMessage { SessionId = S, MachineName = machine, TurnIndex = 1, Role = "user",
                    Content = "Create a file hello.txt containing hi", CreatedAt = new DateTime(2026, 9, 27, 19, 47, 0, DateTimeKind.Utc) },
                new HistoryMessage { SessionId = S, MachineName = machine, TurnIndex = 1, Role = "assistant",
                    Content = "Created hello.txt.", CreatedAt = new DateTime(2026, 9, 27, 19, 47, 1, DateTimeKind.Utc) },
                // 19:48 UTC — turn 2 (ti=2)
                new HistoryMessage { SessionId = S, MachineName = machine, TurnIndex = 2, Role = "user",
                    Content = "go ahead", CreatedAt = new DateTime(2026, 9, 27, 19, 48, 0, DateTimeKind.Utc) },
                new HistoryMessage { SessionId = S, MachineName = machine, TurnIndex = 2, Role = "assistant",
                    Content = "Done.", CreatedAt = new DateTime(2026, 9, 27, 19, 48, 1, DateTimeKind.Utc) },
                // 21:53 UTC — turn 3 (ti=1, post-/new reset)
                new HistoryMessage { SessionId = S, MachineName = machine, TurnIndex = 1, Role = "user",
                    Content = "Remember the word APPLE. Just reply OK.", CreatedAt = new DateTime(2026, 9, 27, 21, 53, 0, DateTimeKind.Utc) },
                new HistoryMessage { SessionId = S, MachineName = machine, TurnIndex = 1, Role = "assistant",
                    Content = "OK.", CreatedAt = new DateTime(2026, 9, 27, 21, 53, 1, DateTimeKind.Utc) },
                // 21:53 UTC — turn 4 (ti=2)
                new HistoryMessage { SessionId = S, MachineName = machine, TurnIndex = 2, Role = "user",
                    Content = "Remember the word BANANA. Just reply OK", CreatedAt = new DateTime(2026, 9, 27, 21, 53, 2, DateTimeKind.Utc) },
                new HistoryMessage { SessionId = S, MachineName = machine, TurnIndex = 2, Role = "assistant",
                    Content = "OK.", CreatedAt = new DateTime(2026, 9, 27, 21, 53, 3, DateTimeKind.Utc) },
                // 21:53 UTC — turn 5 (ti=3)
                new HistoryMessage { SessionId = S, MachineName = machine, TurnIndex = 3, Role = "user",
                    Content = "Remember the word CHERRY. Just reply OK", CreatedAt = new DateTime(2026, 9, 27, 21, 53, 4, DateTimeKind.Utc) },
                new HistoryMessage { SessionId = S, MachineName = machine, TurnIndex = 3, Role = "assistant",
                    Content = "OK.", CreatedAt = new DateTime(2026, 9, 27, 21, 53, 5, DateTimeKind.Utc) },
            };
            await store.SaveMessagesAsync(rows);

            // The listing follows insertion order: turns 1-5 in chronological sequence.
            var loaded = await store.LoadSessionMessagesAsync(S);
            Assert.Equal(10, loaded.Length);
            // Turn order: hello.txt(1), go ahead(2), APPLE(3), BANANA(4), CHERRY(5).
            // The OLD ordering (TurnIndex ASC) would put APPLE (ti=1) before "go ahead" (ti=2).
            Assert.Equal("Create a file hello.txt containing hi", loaded[0].Content);
            Assert.Equal("go ahead", loaded[2].Content);
            Assert.Equal("Remember the word APPLE. Just reply OK.", loaded[4].Content);

            // /rewind 3 should cut BEFORE "Remember the word APPLE" (turn 3), keeping
            // turns 1 and 2 (hello.txt + go ahead) — 4 rows.
            var host = new HostState();
            var ctx = new CommandContext
            {
                HistoryStore = store,
                SessionId = S,
                MachineName = machine,
                ResetConversation = () => host.ResetCalled = true,
                AdoptSessionId = id => host.AdoptedId = id,
                SetInputBoxText = text => host.InputText = text,
                PrependMessages = (roles, contents) => { host.PrependedRoles = roles; },
                ReplayTranscript = (roles, contents) => { host.ReplayedRoles = roles; },
            };

            var result = await SlashCommand.Dispatch("/rewind 3", ctx);
            Assert.False(result.IsError, result.Message);

            // The fork has exactly the 4 rows before turn 3.
            string? forkId = host.AdoptedId;
            Assert.NotNull(forkId);
            var fork = await store.LoadSessionMessagesAsync(forkId);
            Assert.Equal(4, fork.Length);
            Assert.Equal("Create a file hello.txt containing hi", fork[0].Content);
            Assert.Equal("Created hello.txt.", fork[1].Content);
            Assert.Equal("go ahead", fork[2].Content);
            Assert.Equal("Done.", fork[3].Content);
            // The cut prompt is APPLE's — turn 3's original prompt.
            Assert.Equal("Remember the word APPLE. Just reply OK.", host.InputText);

            // The listing shows turns in chronological order (1..5), not TurnIndex order.
            var listResult = await SlashCommand.Dispatch("/rewind", ctx);
            Assert.False(listResult.IsError, listResult.Message);
            // Turn 1 = hello.txt, turn 2 = go ahead, turn 3 = APPLE.
            int idxHello = listResult.Message.IndexOf("hello.txt", StringComparison.Ordinal);
            int idxGoAhead = listResult.Message.IndexOf("go ahead", StringComparison.Ordinal);
            int idxApple = listResult.Message.IndexOf("APPLE", StringComparison.Ordinal);
            Assert.True(idxHello < idxGoAhead, $"hello.txt ({idxHello}) should precede go ahead ({idxGoAhead})");
            Assert.True(idxGoAhead < idxApple, $"go ahead ({idxGoAhead}) should precede APPLE ({idxApple})");

            await store.CloseAsync();
        }
    }
}
