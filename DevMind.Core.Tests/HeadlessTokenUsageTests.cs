// File: HeadlessTokenUsageTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Per-job token usage totals on the headless (delegated) result:
//   * llama.cpp-style timings (prompt_n / cache_n / predicted_n) over 3 requests —
//     totals equal the hand-computed sums for in_total, in_new, out, and the
//     transcript gains the final "[job] tokens:" line.
//   * server reporting usage with NO cached/new split — in_total and out set,
//     in_new null, no "(new)" in the transcript line.
//   * server reporting NO usage at all — all three null, no tokens line.
//   * mixed (split on some requests only) — partial flag true.
//   * a continuation turn counts only its OWN requests, not the chain total.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class HeadlessTokenUsageTests : IDisposable
    {
        private readonly string _dir;

        public HeadlessTokenUsageTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_tokusage_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private string NoPromptFile => Path.Combine(_dir, "no-system-prompt.md");

        private static HeadlessOptions Options(int maxDepth = 5) => new HeadlessOptions
        {
            RequestTimeoutMinutes = 1,
            FirstTokenTimeoutMinutes = 1,
            ManualContextSize = 32768,
            AgenticLoopMaxDepth = maxDepth,
        };

        private sealed class ConsoleGuard : IDisposable
        {
            private readonly TextWriter _prevOut;
            private readonly StringWriter _captured = new StringWriter();
            public ConsoleGuard() { _prevOut = Console.Out; Console.SetOut(_captured); }
            public string Captured => _captured.ToString();
            public void Dispose() => Console.SetOut(_prevOut);
        }

        /// <summary>Runs one headless task against a scripted fake server, restoring the
        /// server-type env var afterwards. Returns the turn result.</summary>
        private static async Task<HeadlessAgentResult> RunAgainstServerAsync(
            HeadlessTokenUsageTests self, FakeSseServer server, int maxDepth, string transcriptName)
        {
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var console = new ConsoleGuard();
                return await HeadlessAgent.RunAsync(
                    "do the task",
                    Options(maxDepth), server.BaseUrl, apiKey: null!,
                    workingDirectory: self._dir,
                    buildCommand: "dotnet build",
                    transcriptPath: Path.Combine(self._dir, transcriptName),
                    ct: CancellationToken.None,
                    promptFilePath: self.NoPromptFile);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        [Fact]
        public async Task ThreeTimedRequests_TotalsAreHandComputedSums()
        {
            using var server = new FakeSseServer();
            // 3 requests: (prompt_n, cache_n, predicted_n) — hand sums below.
            server.SseQueue.Add(FakeSseServer.BuildTimedToolCallSse("read_file",
                "{\"filename\":\"a.txt\"}", promptN: 100, cacheN: 400, predictedN: 10));
            server.SseQueue.Add(FakeSseServer.BuildTimedToolCallSse("read_file",
                "{\"filename\":\"b.txt\"}", promptN: 50, cacheN: 500, predictedN: 20));
            server.SseQueue.Add(FakeSseServer.BuildTimedToolCallSse("task_done",
                "{\"summary\":\"done\"}", promptN: 10, cacheN: 600, predictedN: 5));

            var result = await RunAgainstServerAsync(this, server, maxDepth: 5, "transcript1.log");

            Assert.Null(result.Error);
            Assert.Equal(3, result.Iterations);
            // in_total = (100+400) + (50+500) + (10+600) = 1660
            Assert.Equal(1660L, result.TokensInTotal);
            // in_new = 100 + 50 + 10 = 160
            Assert.Equal(160L, result.TokensInNew);
            Assert.False(result.TokensInNewPartial);
            // out = 10 + 20 + 5 = 35
            Assert.Equal(35L, result.TokensOut);

            string transcript = File.ReadAllText(Path.Combine(_dir, "transcript1.log"));
            Assert.Contains("[job] tokens: in 1,660 total (160 new) · out 35", transcript);
        }

        [Fact]
        public async Task NoCachedSplit_InNewIsNull_NoNewInTranscript()
        {
            using var server = new FakeSseServer();
            // usage.prompt_tokens / completion_tokens, no timings block at all → no split.
            server.SseQueue.Add(
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\"," +
                "\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"filename\\\":\\\"a.txt\\\"}\"}}]}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]," +
                "\"usage\":{\"prompt_tokens\":300,\"completion_tokens\":15}}\n\n" +
                "data: [DONE]\n\n");
            server.SseQueue.Add(
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\"," +
                "\"type\":\"function\",\"function\":{\"name\":\"task_done\",\"arguments\":\"{\\\"summary\\\":\\\"done\\\"}\"}}]}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]," +
                "\"usage\":{\"prompt_tokens\":400,\"completion_tokens\":7}}\n\n" +
                "data: [DONE]\n\n");

            var result = await RunAgainstServerAsync(this, server, maxDepth: 5, "transcript2.log");

            Assert.Null(result.Error);
            Assert.Equal(2, result.Iterations);
            Assert.Equal(700L, result.TokensInTotal);   // 300 + 400
            Assert.Null(result.TokensInNew);           // no cached/new split anywhere
            Assert.False(result.TokensInNewPartial);
            Assert.Equal(22L, result.TokensOut);        // 15 + 7

            string transcript = File.ReadAllText(Path.Combine(_dir, "transcript2.log"));
            Assert.Contains("[job] tokens: in 700 total · out 22", transcript);
            Assert.DoesNotContain("new", transcript);
        }

        [Fact]
        public async Task NoUsageAtAll_AllNull_NoTokensLine()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("read_file",
                "{\"filename\":\"a.txt\"}"));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"done\"}"));

            var result = await RunAgainstServerAsync(this, server, maxDepth: 5, "transcript3.log");

            Assert.Null(result.Error);
            Assert.Equal(2, result.Iterations);
            Assert.Null(result.TokensInTotal);
            Assert.Null(result.TokensInNew);
            Assert.Null(result.TokensOut);
            Assert.False(result.TokensInNewPartial);

            string transcript = File.ReadAllText(Path.Combine(_dir, "transcript3.log"));
            Assert.DoesNotContain("[job] tokens", transcript);
        }

        [Fact]
        public async Task MixedSplitAndNoSplit_PartialFlagTrue()
        {
            using var server = new FakeSseServer();
            // Request 1: llama.cpp timings WITH the split.
            server.SseQueue.Add(FakeSseServer.BuildTimedToolCallSse("read_file",
                "{\"filename\":\"a.txt\"}", promptN: 100, cacheN: 400, predictedN: 10));
            // Request 2: plain usage, NO split.
            server.SseQueue.Add(
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\"," +
                "\"type\":\"function\",\"function\":{\"name\":\"task_done\",\"arguments\":\"{\\\"summary\\\":\\\"done\\\"}\"}}]}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]," +
                "\"usage\":{\"prompt_tokens\":200,\"completion_tokens\":4}}\n\n" +
                "data: [DONE]\n\n");

            var result = await RunAgainstServerAsync(this, server, maxDepth: 5, "transcript4.log");

            Assert.Null(result.Error);
            Assert.Equal(2, result.Iterations);
            Assert.Equal(700L, result.TokensInTotal);   // 500 + 200
            Assert.Equal(100L, result.TokensInNew);     // only request 1 had the split
            Assert.True(result.TokensInNewPartial);
            Assert.Equal(14L, result.TokensOut);        // 10 + 4

            string transcript = File.ReadAllText(Path.Combine(_dir, "transcript4.log"));
            Assert.Contains("[job] tokens: in 700 total (100 new) (partial) · out 14", transcript);
        }

        [Fact]
        public async Task ContinuationTurn_CountsOnlyItsOwnRequests()
        {
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var server = new FakeSseServer();
                // Turn 1: (100+400) in, 10 out. Turn 2: (50+500) in, 20 out.
                server.SseQueue.Add(FakeSseServer.BuildTimedToolCallSse("task_done",
                    "{\"summary\":\"first part\"}", promptN: 100, cacheN: 400, predictedN: 10));
                server.SseQueue.Add(FakeSseServer.BuildTimedToolCallSse("task_done",
                    "{\"summary\":\"second part\"}", promptN: 50, cacheN: 500, predictedN: 20));

                using var console = new ConsoleGuard();
                var session = new HeadlessSession(Options(), server.BaseUrl, apiKey: null!,
                    workingDirectory: _dir, buildCommand: "dotnet build",
                    promptFilePath: NoPromptFile);

                var r1 = await session.RunTurnAsync("turn one",
                    Path.Combine(_dir, "t1.log"), ct: CancellationToken.None);
                Assert.Null(r1.Error);
                Assert.Equal(500L, r1.TokensInTotal);
                Assert.Equal(100L, r1.TokensInNew);
                Assert.Equal(10L, r1.TokensOut);

                // Turn 2 (continuation): per-job counts are THIS turn's requests only —
                // not the chain total (1050 in / 30 out), which a sticky accumulator would give.
                var r2 = await session.RunTurnAsync("continue",
                    Path.Combine(_dir, "t2.log"), ct: CancellationToken.None);
                Assert.Null(r2.Error);
                Assert.Equal(550L, r2.TokensInTotal);
                Assert.Equal(50L, r2.TokensInNew);
                Assert.Equal(20L, r2.TokensOut);
                session.Dispose();
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        [Fact]
        public void BuildTokenUsageLine_FormatsPerSpec()
        {
            // Full split.
            var full = new HeadlessAgentResult { TokensInTotal = 12345678, TokensInNew = 1234567, TokensOut = 98765 };
            Assert.Equal("in 12,345,678 total (1,234,567 new) · out 98,765",
                HeadlessSession.BuildTokenUsageLine(full));

            // No split → no "(… new)".
            var noSplit = new HeadlessAgentResult { TokensInTotal = 700, TokensInNew = null, TokensOut = 22 };
            Assert.Equal("in 700 total · out 22", HeadlessSession.BuildTokenUsageLine(noSplit));

            // Partial → " (partial)" appended.
            var partial = new HeadlessAgentResult { TokensInTotal = 700, TokensInNew = 100, TokensOut = 14, TokensInNewPartial = true };
            Assert.Equal("in 700 total (100 new) (partial) · out 14", HeadlessSession.BuildTokenUsageLine(partial));

            // No usage at all → no line.
            var none = new HeadlessAgentResult();
            Assert.Null(HeadlessSession.BuildTokenUsageLine(none));
        }

        [Fact]
        public void Aggregator_SumsAndNullPropagate()
        {
            var agg = new TokenUsageAggregator();
            Assert.Null(agg.TotalPromptTokens());
            Assert.Null(agg.TotalNewPromptTokens());
            Assert.Null(agg.TotalCompletionTokens());
            Assert.False(agg.IsNewPartial());

            agg.Add(new RequestUsage { PromptTotal = 500, PromptNew = 100, Completion = 10 });
            agg.Add(new RequestUsage { PromptTotal = 200, PromptNew = null, Completion = 4 });
            agg.Add(null); // failed request — contributes nothing

            Assert.Equal(700L, agg.TotalPromptTokens());
            Assert.Equal(100L, agg.TotalNewPromptTokens()); // partial sum
            Assert.True(agg.IsNewPartial());
            Assert.Equal(14L, agg.TotalCompletionTokens());
            Assert.Equal(2, agg.RequestCount);
        }
    }
}
