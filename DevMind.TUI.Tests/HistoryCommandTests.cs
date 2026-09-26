// File: HistoryCommandTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// /history [count|all] and /resume <n>. /history used to hard-cap the listing at 20 with no
// way to see older sessions, and /resume capped its index at 20 too, so a session past the
// 20th was unreachable. Now /history takes a count or "all" (default 20), and /resume
// indexes the full list — the number any /history listing shows resolves to that session.

using DevMind;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class HistoryCommandTests
    {
        // Sessions come back most recent first (the store's contract); "Session 1" is newest.
        private sealed class FakeHistoryStore : IHistoryStore
        {
            private readonly SessionSummary[] _sessions;

            public FakeHistoryStore(int count)
            {
                var now = new DateTime(2026, 9, 26, 12, 0, 0);
                _sessions = Enumerable.Range(1, count).Select(i => new SessionSummary
                {
                    SessionId = $"sess-{i:D3}",
                    Title = $"Session {i}",
                    StartedAt = now.AddHours(-i),
                    LastActiveAt = now.AddHours(-i),
                    MessageCount = 2,
                }).ToArray();
            }

            public Task<SessionSummary[]> ListSessionsAsync(string machineName) => Task.FromResult(_sessions);

            public Task<HistoryMessage[]> LoadSessionMessagesAsync(string sessionId) => Task.FromResult(new[]
            {
                new HistoryMessage { SessionId = sessionId, Role = "user", Content = $"question in {sessionId}", TurnIndex = 0 },
                new HistoryMessage { SessionId = sessionId, Role = "assistant", Content = $"answer in {sessionId}", TurnIndex = 0 },
            });

            public Task SaveMessagesAsync(HistoryMessage[] messages) => Task.CompletedTask;
            public Task<HistoryMessage[]> LoadMessagesAsync(string machineName, int maxTurns) => Task.FromResult(Array.Empty<HistoryMessage>());
            public Task UpsertSessionAsync(string sessionId, string machineName) => Task.CompletedTask;
            public Task SetSessionTitleAsync(string sessionId, string title) => Task.CompletedTask;
            public Task InitAsync() => Task.CompletedTask;
            public Task CloseAsync() => Task.CompletedTask;
        }

        private static CommandContext Context(int sessions, List<string>? prepended = null) => new CommandContext
        {
            HistoryStore = new FakeHistoryStore(sessions),
            MachineName = "beast",
            PrependMessages = (roles, contents) => prepended?.AddRange(contents),
        };

        private static int ListedCount(string message) =>
            message.Split('\n').Count(l => l.TrimStart().StartsWith("[", StringComparison.Ordinal));

        [Fact]
        public async Task Default_ShowsTwenty_WithFooterNamingTheArgument()
        {
            var result = await SlashCommand.Dispatch("/history", Context(30));

            Assert.False(result.IsError, result.Message);
            Assert.Equal(20, ListedCount(result.Message));
            Assert.Contains("[20] Session 20", result.Message);
            Assert.DoesNotContain("[21]", result.Message);
            Assert.Contains("... and 10 more (showing 20 most recent)", result.Message);
            Assert.Contains("use /history all to see every session", result.Message);
        }

        [Fact]
        public async Task Count_ShowsThatMany()
        {
            var result = await SlashCommand.Dispatch("/history 5", Context(30));

            Assert.False(result.IsError, result.Message);
            Assert.Equal(5, ListedCount(result.Message));
            Assert.Contains("... and 25 more (showing 5 most recent)", result.Message);
        }

        [Fact]
        public async Task All_ShowsEverySession_WithoutFooter()
        {
            var result = await SlashCommand.Dispatch("/history all", Context(30));

            Assert.False(result.IsError, result.Message);
            Assert.Equal(30, ListedCount(result.Message));
            Assert.Contains("[30] Session 30", result.Message);
            Assert.DoesNotContain("more (showing", result.Message);
        }

        [Fact]
        public async Task CountAboveTheTotal_ShowsEverySession_WithoutFooter()
        {
            var result = await SlashCommand.Dispatch("/history 50", Context(30));

            Assert.Equal(30, ListedCount(result.Message));
            Assert.DoesNotContain("more (showing", result.Message);
        }

        [Theory]
        [InlineData("/history 0")]
        [InlineData("/history -3")]
        [InlineData("/history abc")]
        public async Task InvalidCount_IsAnError(string input)
        {
            var result = await SlashCommand.Dispatch(input, Context(30));

            Assert.True(result.IsError);
            Assert.Contains("/history [count|all]", result.Message);
        }

        [Fact]
        public async Task Resume_IndexesTheFullList_PastTheDisplayCap()
        {
            var prepended = new List<string>();

            var result = await SlashCommand.Dispatch("/resume 25", Context(30, prepended));

            Assert.False(result.IsError, result.Message);
            Assert.Contains("Resumed session: Session 25", result.Message);
            Assert.Contains("question in sess-025", prepended);
        }

        [Fact]
        public async Task Resume_NumberFromAHistoryListing_IsTheSameSession()
        {
            var ctx = Context(30, new List<string>());
            string listing = (await SlashCommand.Dispatch("/history all", ctx)).Message;
            Assert.Contains("[27] Session 27", listing);

            var result = await SlashCommand.Dispatch("/resume 27", ctx);

            Assert.Contains("Resumed session: Session 27", result.Message);
        }

        [Fact]
        public async Task Resume_BeyondTheFullList_IsAnErrorNamingTheRealRange()
        {
            var result = await SlashCommand.Dispatch("/resume 31", Context(30));

            Assert.True(result.IsError);
            Assert.Contains("Valid range: 1-30", result.Message);
        }

        [Fact]
        public void Help_ShowsTheNewUsage()
        {
            var cmd = SlashCommand.ListCommands().Single(c => c.Name == "/history");
            Assert.Equal("/history [count|all]", cmd.Usage);
        }
    }
}
