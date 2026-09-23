// File: SessionResumeRoundTripTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The one test that proves --resume and --continue survive a store.
//
// Everything else about resuming is pinned in pieces: the filter against in-memory rows, the
// adopt guard against static state, the flags against an argv array. What none of them can
// say is whether the id written on the way out is the id found on the way back in, through a
// real provider, in a fresh process-equivalent. That is the whole feature — a session id is
// worth nothing if it does not round-trip — so it is worth the file I/O.
//
// SQLite is used because it is file-backed and needs no server. The store is opened twice on
// purpose: the second open is the "fresh launch" that --continue would do.

using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    public class SessionResumeRoundTripTests : IDisposable
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"devmind-resume-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* temp file */ }
        }

        private static HistoryMessage Row(string sessionId, string machine, int turn,
                                          string role, string content, bool synthetic = false)
            => new HistoryMessage
            {
                SessionId = sessionId,
                MachineName = machine,
                TurnIndex = turn,
                Role = role,
                Content = content,
                CreatedAt = DateTime.UtcNow,
                IsSynthetic = synthetic,
            };

        private async Task SeedAsync(string machine, string olderId, string newerId)
        {
            var store = new SqliteHistoryStore(_dbPath);
            await store.InitAsync();

            await store.UpsertSessionAsync(olderId, machine);
            await store.SaveMessagesAsync(new[]
            {
                Row(olderId, machine, 0, "user", "An older question."),
                Row(olderId, machine, 0, "assistant", "An older answer."),
            });

            await store.UpsertSessionAsync(newerId, machine);
            await store.SetSessionTitleAsync(newerId, "The session to continue");
            await store.SaveMessagesAsync(new[]
            {
                Row(newerId, machine, 0, "user", "What does LoopDriver do?"),
                Row(newerId, machine, 0, "assistant", "[CONTEXT] 12 / 40\nIt drives one agentic iteration."),
                Row(newerId, machine, 1, "user", SyntheticPrompts.Continue, synthetic: true),
                Row(newerId, machine, 1, "assistant", "And it owns LoopState."),
            });

            await store.CloseAsync();
        }

        [Fact]
        public async Task ContinueResolvesTheMostRecentSession_AndItsPairsComeBackIntact()
        {
            string machine = "round-trip-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string olderId = "2026-09-19T090000Z-pid1";
            string newerId = "2026-09-20T101500Z-pid2";

            await SeedAsync(machine, olderId, newerId);

            // A fresh store, as a fresh launch would open.
            var reopened = new SqliteHistoryStore(_dbPath);
            await reopened.InitAsync();
            try
            {
                SessionSummary[] sessions = await reopened.ListSessionsAsync(machine);

                // --continue takes the most recent session. Both providers order by
                // LastActiveAt descending, and the newest is the one someone means by
                // "continue" — picking the oldest would still open A session and still print
                // a confident line, which is why this is asserted through the real store.
                Assert.True(sessions.Length >= 2);
                SessionSummary? picked = SessionResume.SelectSession(sessions, null, continueLatest: true);

                Assert.NotNull(picked);
                Assert.Equal(newerId, picked.SessionId);
                Assert.Equal("The session to continue", picked.Title);

                HistoryMessage[] messages = await reopened.LoadSessionMessagesAsync(picked.SessionId);
                var (roles, contents, skipped) = SessionResume.PairMessages(messages);

                Assert.Equal(new[] { "user", "assistant" }, roles);
                Assert.Equal("What does LoopDriver do?", contents[0]);
                // The decoration line is gone and the two assistant rows are one answer.
                Assert.Equal("It drives one agentic iteration.\n\nAnd it owns LoopState.", contents[1]);
                Assert.Equal(1, skipped);   // the synthetic continuation
            }
            finally
            {
                await reopened.CloseAsync();
            }
        }

        [Fact]
        public async Task ResumeByIdFindsTheOlderSession_NotTheMostRecentOne()
        {
            string machine = "round-trip-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string olderId = "2026-09-19T090000Z-pid1";
            string newerId = "2026-09-20T101500Z-pid2";

            await SeedAsync(machine, olderId, newerId);

            var reopened = new SqliteHistoryStore(_dbPath);
            await reopened.InitAsync();
            try
            {
                SessionSummary[] sessions = await reopened.ListSessionsAsync(machine);
                // --resume names an id, so it must reach past the most recent session.
                SessionSummary? target = SessionResume.SelectSession(sessions, olderId, continueLatest: false);

                Assert.NotNull(target);
                Assert.Equal(olderId, target.SessionId);

                var (roles, contents, _) =
                    SessionResume.PairMessages(await reopened.LoadSessionMessagesAsync(target.SessionId));

                Assert.Equal(2, roles.Length);
                Assert.Equal("An older question.", contents[0]);
            }
            finally
            {
                await reopened.CloseAsync();
            }
        }

        [Fact]
        public async Task NewTurnsWrittenUnderTheResumedId_JoinTheSameSession()
        {
            // The point of adopting the id rather than starting fresh: /history must show one
            // session afterwards, not a fork.
            string machine = "round-trip-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string olderId = "2026-09-19T090000Z-pid1";
            string newerId = "2026-09-20T101500Z-pid2";

            await SeedAsync(machine, olderId, newerId);

            var reopened = new SqliteHistoryStore(_dbPath);
            await reopened.InitAsync();
            try
            {
                int before = (await reopened.ListSessionsAsync(machine)).Length;

                // What a resumed session's next turn writes.
                await reopened.SaveMessagesAsync(new[]
                {
                    Row(newerId, machine, 2, "user", "A follow-up."),
                    Row(newerId, machine, 2, "assistant", "Its answer."),
                });
                await reopened.UpsertSessionAsync(newerId, machine);

                SessionSummary[] after = await reopened.ListSessionsAsync(machine);

                Assert.Equal(before, after.Length);   // no new session appeared
                SessionSummary? resumed = Array.Find(after, s => s.SessionId == newerId);
                Assert.NotNull(resumed);
                Assert.Equal(6, resumed.MessageCount);

                var (roles, _, _) =
                    SessionResume.PairMessages(await reopened.LoadSessionMessagesAsync(newerId));
                Assert.Equal(4, roles.Length);        // two exchanges now
            }
            finally
            {
                await reopened.CloseAsync();
            }
        }
    }
}
