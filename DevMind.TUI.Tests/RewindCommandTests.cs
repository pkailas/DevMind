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
using System.Text;
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
            // The title names the origin: the turn AND the original's short id (pid1).
            Assert.Equal("Auth work (rewound from #3 of pid1)", store.Title);
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
            Assert.Equal("Auth work (rewound from #3 of pid1)", forkSummary.Title);

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

        // ── Repo memory (part 2a): the live APPLE/BANANA/CHERRY scenario ──────
        //
        // The conversation fork cut the rows; /rewind must also cut the files those rows
        // wrote via save_memory — the MEMORY.md index is in every conversation's system
        // prompt, so an abandoned branch leaks itself back in through memory. The
        // checkpoint roots are injected (temp dirs), so the real %LOCALAPPDATA% and the
        // repo are never touched.

        private readonly string _memCpRoot = Path.Combine(Path.GetTempPath(), $"devmind-rewind-memcp-{Guid.NewGuid():N}");
        private readonly string _memRepo = Path.Combine(Path.GetTempPath(), $"devmind-rewind-repo-{Guid.NewGuid():N}");

        void SetupMemoryRoots(CommandContext ctx)
        {
            Directory.CreateDirectory(_memCpRoot);
            Directory.CreateDirectory(_memRepo);
            ctx.MemoryCheckpointRoot = _memCpRoot;
            ctx.MemoryRoot = _memRepo;
        }

        void WriteMemIndex(string content)
        {
            File.WriteAllText(Path.Combine(_memRepo, "MEMORY.md"), content, Encoding.UTF8);
        }

        void WriteMemTopic(string slug, string content)
        {
            string dir = Path.Combine(_memRepo, ".devmind", "memory");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, slug + ".md"), content, Encoding.UTF8);
        }

        string? ReadMemIndex() =>
            File.Exists(Path.Combine(_memRepo, "MEMORY.md"))
                ? File.ReadAllText(Path.Combine(_memRepo, "MEMORY.md"), Encoding.UTF8)
                : null;

        string? ReadMemTopic(string slug)
        {
            string p = Path.Combine(_memRepo, ".devmind", "memory", slug + ".md");
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
        }

        [Fact]
        public async Task Rewind_LiveScenario_MemoryWrittenInTurn3_IsGoneAfterRewind3()
        {
            var (ctx, _, host) = Context(Session());
            SetupMemoryRoots(ctx);

            // Turns 1-3 ran. At the START of each turn (before the prompt is sent) the TUI
            // snapshots the memory set. Turns 1-3 all start with NO memory, so all three
            // checkpoints are empty. Simulate the turn-start snapshots.
            var cp = new MemoryCheckpoint(_memCpRoot, S, _memRepo);
            cp.SnapshotAtTurnStart(1);   // empty
            cp.SnapshotAtTurnStart(2);   // empty (identical → same-as-1, no copy)
            cp.SnapshotAtTurnStart(3);   // empty (identical → same-as-1, no copy)

            // Turn 3 RUNS and the model calls save_memory: a topic file + an index entry —
            // the live APPLE/BANANA/CHERRY write. This happens AFTER the turn-3 snapshot,
            // so the snapshot does not contain it.
            WriteMemIndex("# DevMind Memory Index\n- [word-remembered] User asked to remember the words: APPLE, BANANA, CHERRY");
            WriteMemTopic("word-remembered", "User asked to remember the words: APPLE, BANANA, CHERRY");

            var result = await SlashCommand.Dispatch("/rewind 3", ctx);

            Assert.False(result.IsError, result.Message);

            // The memory from turn 3 is GONE: the turn-3 checkpoint (taken at the START of
            // turn 3, before save_memory ran) was empty, so the topic file is deleted and
            // the index is gone too. This is the exact live scenario — the abandoned branch
            // must not leak back in through MEMORY.md.
            Assert.True(ReadMemTopic("word-remembered") == null,
                "the topic file written in turn 3 must be deleted by /rewind 3");
            Assert.True(ReadMemIndex() == null,
                "the MEMORY.md index written in turn 3 must be deleted by /rewind 3");

            // The message says so, and the files-on-disk line is still true (for non-memory files).
            Assert.Contains("Memory restored to turn 3", result.Message);
            Assert.Contains("Files on disk were NOT changed", result.Message);

            // The fork got the original's checkpoints for turns 1..2 (n-1 = 3-1 = 2), so a
            // later /rewind inside the fork works. Turn 3 is NOT copied — the fork starts
            // BEFORE turn 3.
            string forkId = host.AdoptedId!;
            Assert.True(Directory.Exists(Path.Combine(_memCpRoot, forkId, "turn-1")),
                "the fork must inherit the original's turn-1 checkpoint");
            Assert.True(Directory.Exists(Path.Combine(_memCpRoot, forkId, "turn-2")),
                "the fork must inherit the original's turn-2 checkpoint");
            Assert.False(Directory.Exists(Path.Combine(_memCpRoot, forkId, "turn-3")),
                "the fork must NOT inherit the turn it was rewound from");
        }

        [Fact]
        public async Task Rewind_NoCheckpointForTurnN_MemoryUnchanged_AndSaysSo()
        {
            var (ctx, _, _) = Context(Session());
            SetupMemoryRoots(ctx);

            // Memory exists, but NO checkpoint was taken (pre-feature session, or a failed
            // snapshot). /rewind must leave it exactly as it is and say so.
            WriteMemIndex("# DevMind Memory Index\n- [existing] a pre-existing topic");
            WriteMemTopic("existing", "a pre-existing topic");

            var result = await SlashCommand.Dispatch("/rewind 3", ctx);

            Assert.False(result.IsError, result.Message);
            Assert.Equal("# DevMind Memory Index\n- [existing] a pre-existing topic", ReadMemIndex());
            Assert.Equal("a pre-existing topic", ReadMemTopic("existing"));
            Assert.Contains("no checkpoint for turn 3", result.Message);
            Assert.Contains("unchanged", result.Message);
        }

        [Fact]
        public async Task Rewind_NoCheckpointRootsWired_MemoryUnchanged_AndSaysSo()
        {
            var (ctx, _, _) = Context(Session());
            // No SetupMemoryRoots — a host that does not wire the roots (both null). The
            // conversation fork still happens; the memory restore is skipped and the
            // message says so. No files are touched at all, so nothing to assert on disk.

            var result = await SlashCommand.Dispatch("/rewind 3", ctx);

            Assert.False(result.IsError, result.Message);
            Assert.Contains("checkpoints unavailable", result.Message);
            Assert.Contains("unchanged", result.Message);
            Assert.Contains("Files on disk were NOT changed", result.Message);
        }

        [Fact]
        public async Task Rewind_MemoryRestore_DoesNotTouchNonMemoryFiles_Sentinel()
        {
            var (ctx, _, _) = Context(Session());
            SetupMemoryRoots(ctx);

            var cp = new MemoryCheckpoint(_memCpRoot, S, _memRepo);
            // Turn 2 ran with "index t2". At the START of turn 3 (before the prompt is
            // sent) the TUI snapshots the memory set — it still holds "index t2".
            WriteMemIndex("index t2");
            cp.SnapshotAtTurnStart(3);

            // A sentinel NEXT to the memory set — part 2a must not touch it.
            string sentinel = Path.Combine(_memRepo, "NOT-memory.cs");
            File.WriteAllText(sentinel, "class C {}");

            // Turn 3 RUNS: save_memory rewrites the index and adds a topic, and the
            // abandoned branch mutates the sentinel too (part 2b would handle that;
            // part 2a must leave it alone).
            WriteMemIndex("index t3");
            WriteMemTopic("word-remembered", "APPLE");
            File.WriteAllText(sentinel, "class C { /* mutated */ }");

            var result = await SlashCommand.Dispatch("/rewind 3", ctx);

            Assert.False(result.IsError, result.Message);
            // Memory is back to turn 2…
            Assert.Equal("index t2", ReadMemIndex());
            Assert.True(ReadMemTopic("word-remembered") == null);
            // …and the non-memory sentinel is exactly as the abandoned branch left it.
            Assert.Equal("class C { /* mutated */ }", File.ReadAllText(sentinel, Encoding.UTF8));
        }

        // ── Checkpoint NUMBERING: the fork's turns are the fork's, not the process's ──
        //
        // The checkpoint a turn is written under MUST be the number RewindPlanner assigns
        // that turn in THIS session (NextTurnNumber over the session's stored rows) — not a
        // process-wide counter. After a fork, the fork's second turn is turn 2 of the fork,
        // and a later /rewind 2 inside the fork must find the fork's OWN turn-2 checkpoint,
        // not a stale one copied from the original.

        [Fact]
        public async Task Rewind_ThenContinue_ForksTurnsAreNumberedByTheFork_NotTheProcess()
        {
            // Original session S with 3 turns. Snapshots exist for turns 1..3 (as the TUI
            // would have taken them at each turn start). Memory is empty throughout.
            var (ctx, _, host) = Context(Session());
            SetupMemoryRoots(ctx);

            var originalCp = new MemoryCheckpoint(_memCpRoot, S, _memRepo);
            originalCp.SnapshotAtTurnStart(1);
            originalCp.SnapshotAtTurnStart(2);
            originalCp.SnapshotAtTurnStart(3);

            // /rewind 2: the fork gets the original's turns 1..1 (n-1) checkpoints, and the
            // conversation is cut before turn 2. The fork now has 1 turn of its own history.
            var rewind = await SlashCommand.Dispatch("/rewind 2", ctx);
            Assert.False(rewind.IsError, rewind.Message);
            string forkId = host.AdoptedId!;

            // The fork inherits the original's turn-1 checkpoint (so a /rewind 1 inside the
            // fork works), and NOT turn 2 (the turn it was rewound from).
            Assert.True(Directory.Exists(Path.Combine(_memCpRoot, forkId, "turn-1")));
            Assert.False(Directory.Exists(Path.Combine(_memCpRoot, forkId, "turn-2")));

            // Now the user types a NEW turn in the fork. This is turn 2 OF THE FORK — its
            // history has 1 turn, so NextTurnNumber is 2. The TUI snapshots it as turn-2 in
            // the FORK's folder. (A process-wide counter would have made this turn-4.)
            int forkNextTurn = RewindPlanner.NextTurnNumber(
                new[] { Row(forkId, 0, "user", "Fix the login bug."),
                        Row(forkId, 0, "assistant", "Fixed it in Auth.cs.") });
            // The fork's next turn is turn 2 of the fork.
            Assert.Equal(2, forkNextTurn);

            // The fork's turn 2 runs and writes memory.
            var forkCp = new MemoryCheckpoint(_memCpRoot, forkId, _memRepo);
            forkCp.SnapshotAtTurnStart(forkNextTurn);   // → turn-2 in the fork's folder
            WriteMemIndex("fork turn-2 index");
            WriteMemTopic("fork-note", "written in the fork's turn 2");

            // /rewind 2 INSIDE the fork: it must restore the fork's OWN turn-2 checkpoint
            // (empty — taken before the write above), NOT a stale turn-2 from the original
            // (there is none, and there must not be one on disk under the fork's id).
            var ctx2 = Context(Session()).ctx;
            ctx2.SessionId = forkId;
            ctx2.MemoryCheckpointRoot = _memCpRoot;
            ctx2.MemoryRoot = _memRepo;
            var rewind2 = await SlashCommand.Dispatch("/rewind 2", ctx2);
            Assert.False(rewind2.IsError, rewind2.Message);

            // The fork's turn-2 memory write is gone — the fork's own turn-2 snapshot was empty.
            Assert.True(ReadMemTopic("fork-note") == null,
                "the fork's turn-2 write must be removed by a /rewind 2 inside the fork");
            Assert.True(ReadMemIndex() == null);
            Assert.Contains("Memory restored to turn 2", rewind2.Message);

            // The checkpoint on disk under the fork's id is turn-2, not turn-4.
            Assert.True(Directory.Exists(Path.Combine(_memCpRoot, forkId, "turn-2")),
                "the fork's second turn's checkpoint must be turn-2 under the fork's id");
            Assert.False(Directory.Exists(Path.Combine(_memCpRoot, forkId, "turn-4")),
                "a process-wide counter would have stamped it turn-4 — it must not");
        }

        [Fact]
        public void CheckpointNumbering_ResumedSession_ContinuesFromStoredHistory()
        {
            // A fresh process (no in-memory counter) resumes a session that already has 3
            // turns. Its next turn's checkpoint must be turn-4, and turns 1..3 untouched —
            // a reset counter would have stamped it turn-1 and clobbered the turn-1 snapshot.
            //
            // The durable fact is the session's stored rows; NextTurnNumber over them is the
            // number. (The TUI's RunTurnAsync reads exactly these rows via
            // LoadSessionMessagesAsync and calls NextTurnNumber — this is that derivation.)
            var resumed = new[]
            {
                Row(S, 0, "user", "One."), Row(S, 0, "assistant", "a."),
                Row(S, 1, "user", "Two."), Row(S, 1, "assistant", "b."),
                Row(S, 2, "user", "Three."), Row(S, 2, "assistant", "c."),
            };

            int next = RewindPlanner.NextTurnNumber(resumed);
            // A resumed 3-turn session's next turn is turn 4.
            Assert.Equal(4, next);

            // Snapshot under that number: it is turn-4, and turn-1..3 are distinct entries
            // that a turn-1 stamp would have overwritten.
            var cp = new MemoryCheckpoint(_memCpRoot, S, _memRepo);
            Directory.CreateDirectory(_memRepo);
            cp.SnapshotAtTurnStart(1);
            cp.SnapshotAtTurnStart(2);
            cp.SnapshotAtTurnStart(3);
            cp.SnapshotAtTurnStart(next);   // → turn-4

            Assert.True(Directory.Exists(Path.Combine(_memCpRoot, S, "turn-4")),
                "the resumed session's new turn snapshots as turn-4");
            // turns 1..3 still exist and were not clobbered by a turn-1 write.
            for (int k = 1; k <= 3; k++)
                Assert.True(Directory.Exists(Path.Combine(_memCpRoot, S, $"turn-{k}")),
                    $"turn-{k} must survive — a reset counter would have clobbered turn-1");
        }
    }
}
