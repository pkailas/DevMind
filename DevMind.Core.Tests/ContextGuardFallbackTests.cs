// File: ContextGuardFallbackTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The context-window guard (LoopDriver: pause and ask once usage passes
// AgenticContextLimitPercent, default 78) requires LastContextUsed > 0. On llama-server
// that value came from timings.n_past — and b10499's timings object does not carry it.
// Measured: n_past > 0 in 0 of 15,135 recorded turns. So /context-limit, --context-limit,
// contextLimitPercent and AgenticContextLimitPercent were an operator-configurable pause
// that could not fire, while the server reported prompts of 234,612 tokens against 262,144.
//
// The fix: when n_past is absent, LastContextUsed falls back to prompt_n + cache_n — the
// full prompt, which equals usage.prompt_tokens and is exactly what ParseVllmUsage already
// treats as n_past's analogue on vLLM. n_past still wins when present.
//
// A test that passes because the guard never runs proves nothing, so the deciding test here
// is end to end: a real LlmClient parses a hand-built terminal chunk carrying prompt_n and
// cache_n but no n_past, a real LoopDriver then runs the iteration on the tool call that
// same chunk carried, and the operator prompt is asserted on — not a flag.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class ContextGuardFallbackTests
    {
        private const int Window = 262_144;

        /// <summary>Options with the guard armed at 78% and the window pinned so the test is
        /// deterministic without a context probe.</summary>
        private sealed class GuardedOptions : ILlmOptions
        {
            public string SystemPrompt => "You are a test assistant.";
            public string ModelName => "test-model";
            public int RequestTimeoutMinutes => 1;
            public int FirstTokenTimeoutMinutes => 1;
            public bool ShowDebugOutput => false;
            public bool ShowContextBudget => false;
            public bool ShowLlmThinking => false;
            public ContextEvictionMode ContextEviction => ContextEvictionMode.Off;
            public int ManualContextSize => Window;
            public LlmServerType ServerType => LlmServerType.LlamaServer;
            public string CustomContextEndpoint => null!;
            public int MicroCompactThreshold => 0;      // compaction out of the way
            public int NearlineIngestThresholdChars => 8_000;
            public bool MicroCompactSummarize => false;
            public bool MicroCompactBrainwash => false;
            public bool AlwaysConfirmPatch => false;
            public int AgenticLoopMaxDepth => 25;
            public int AgenticContextLimitPercent => 78;
        }

        // Pieces of the SSE payload, kept as constants so the escaping is written once.
        private const string Q = "\"";

        /// <summary>
        /// A streamed response whose terminal chunk carries a llama-server-style <c>timings</c>
        /// object built from the given fields (null omits the field). With
        /// <paramref name="toolCall"/> the response is a <c>run_shell</c> tool call rather than
        /// text, so the REAL parser populates LastToolCalls and the loop takes the tool path.
        /// </summary>
        private static string SseWithTimings(int? promptN, int? cacheN, int? nPast, bool toolCall = false)
        {
            var parts = new List<string>();
            if (promptN.HasValue) parts.Add(Q + "prompt_n" + Q + ":" + promptN);
            if (cacheN.HasValue) parts.Add(Q + "cache_n" + Q + ":" + cacheN);
            if (nPast.HasValue) parts.Add(Q + "n_past" + Q + ":" + nPast);
            parts.Add(Q + "prompt_ms" + Q + ":10.0");
            parts.Add(Q + "predicted_n" + Q + ":5");
            parts.Add(Q + "predicted_ms" + Q + ":50.0");
            parts.Add(Q + "n_ctx" + Q + ":" + Window);
            string timings = "{" + string.Join(",", parts) + "}";

            var sb = new StringBuilder();
            if (toolCall)
            {
                // arguments is a JSON *string*: {"command":"dotnet build"} with its quotes escaped.
                string args = "{" + "\\" + Q + "command" + "\\" + Q + ":" + "\\" + Q + "dotnet build" + "\\" + Q + "}";
                sb.Append("data: {" + Q + "choices" + Q + ":[{" + Q + "delta" + Q + ":{" + Q + "tool_calls" + Q + ":[{"
                          + Q + "index" + Q + ":0," + Q + "id" + Q + ":" + Q + "call_1" + Q + "," + Q + "type" + Q + ":" + Q + "function" + Q + ","
                          + Q + "function" + Q + ":{" + Q + "name" + Q + ":" + Q + "run_shell" + Q + "," + Q + "arguments" + Q + ":" + Q + args + Q + "}}]}}]}\n\n");
            }
            else
            {
                sb.Append("data: {" + Q + "choices" + Q + ":[{" + Q + "delta" + Q + ":{" + Q + "content" + Q + ":" + Q + "ok" + Q + "}}]}\n\n");
            }

            string finish = toolCall ? "tool_calls" : "stop";
            sb.Append("data: {" + Q + "choices" + Q + ":[{" + Q + "delta" + Q + ":{}," + Q + "finish_reason" + Q + ":" + Q + finish + Q + "}],"
                      + Q + "timings" + Q + ":" + timings + "}\n\n");
            sb.Append("data: [DONE]\n\n");
            return sb.ToString();
        }

        private static LlmClient NewClient(FakeSseServer server)
        {
            var client = new LlmClient(new GuardedOptions());
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try { client.Configure(server.BaseUrl, apiKey: null!); }
            finally { Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior); }
            return client;
        }

        private static async Task SendAsync(LlmClient client, string message)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.IncrementTurn();
            await client.SendMessageAsync(message, onToken: _ => { },
                onComplete: () => done.TrySetResult(true), onError: ex => done.TrySetException(ex));
            Assert.True(await Task.WhenAny(done.Task, Task.Delay(15000)) == done.Task, "send timed out");
            await done.Task;
        }

        // ── The fallback itself ─────────────────────────────────────────────────

        [Fact]
        public async Task WithoutNPast_LastContextUsedComesFromPromptPlusCache()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(SseWithTimings(promptN: 200_000, cacheN: 30_000, nPast: null));
            var client = NewClient(server);

            Assert.Equal(0, client.LastContextUsed);   // precondition: nothing measured yet

            await SendAsync(client, "hello");

            Assert.Equal(230_000, client.LastContextUsed);
            Assert.Equal(Window, client.ServerContextSize);
        }

        [Fact]
        public async Task WhenNPastIsPresent_ItWins()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(SseWithTimings(promptN: 200_000, cacheN: 30_000, nPast: 50_000));
            var client = NewClient(server);

            await SendAsync(client, "hello");

            Assert.Equal(50_000, client.LastContextUsed);   // not 230,000
        }

        [Fact]
        public async Task WithNoCountAtAll_TheAnchorStaysFrozen()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(SseWithTimings(promptN: null, cacheN: null, nPast: null));
            var client = NewClient(server);

            await SendAsync(client, "hello");

            Assert.Equal(0, client.LastContextUsed);
        }

        // The fallback must maintain what the n_past block maintained, not just the headline
        // number: the growth delta feeds the predictive compaction threshold, and a fallback
        // that set LastContextUsed alone would leave it permanently 0.
        [Fact]
        public async Task TheFallback_AlsoTracksGrowthBetweenTurns()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(SseWithTimings(promptN: 100_000, cacheN: 0, nPast: null));
            server.SseQueue.Add(SseWithTimings(promptN: 4_000, cacheN: 100_000, nPast: null));
            var client = NewClient(server);

            await SendAsync(client, "first");
            Assert.Equal(100_000, client.LastContextUsed);
            Assert.Equal(0, client.LastContextDelta);            // nothing to grow from yet

            await SendAsync(client, "second");
            Assert.Equal(104_000, client.LastContextUsed);
            Assert.Equal(4_000, client.LastContextDelta);        // 104,000 − 100,000
        }

        // ── The guard actually fires ────────────────────────────────────────────

        private static async Task<(FakeHost host, LoopState state, LoopIterationResult turn)> RunGuardedIterationAsync(
            int promptN, int cacheN, string dir)
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(SseWithTimings(promptN: promptN, cacheN: cacheN, nPast: null, toolCall: true));
            var client = NewClient(server);

            var host = new FakeHost(dir);
            var state = new LoopState();
            state.ResetForUserTurn();                          // arms the guard, as a real turn does
            var driver = new LoopDriver(client, host, new NoopCallbacks(), new GuardedOptions(), state);

            await SendAsync(client, "do the thing");
            Assert.Equal(promptN + cacheN, client.LastContextUsed);   // the measurement arrived
            Assert.NotNull(client.LastToolCalls);                     // and so did the tool call, via the real parser

            var turn = await driver.ProcessIterationAsync("do the thing", "", "dotnet build", CancellationToken.None);
            return (host, state, turn);
        }

        [Fact]
        public async Task TheContextWindowGuard_FiresOnPromptPlusCacheAlone()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"devmind_ctxguard_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // 230,000 / 262,144 = 87% — past the 78% limit, from prompt_n + cache_n only.
                var (host, state, turn) = await RunGuardedIterationAsync(200_000, 30_000, dir);

                // Not "the turn ended" — the operator was ASKED, with the real numbers.
                var prompt = Assert.Single(host.ConfirmPrompts);
                Assert.Contains("Context window at 87%", prompt, StringComparison.Ordinal);
                Assert.Contains("230,000 / 262,144", prompt, StringComparison.Ordinal);
                Assert.Contains("past the 78% limit", prompt, StringComparison.Ordinal);

                // Answered "continue", so the loop goes on — and the guard disarms until usage
                // drops and climbs again, exactly as before.
                Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);
                Assert.False(state.ContextGuardArmed);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task BelowTheLimit_TheGuardStaysQuiet()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"devmind_ctxguard_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // 130,000 / 262,144 = 49% — under 78%.
                var (host, state, _) = await RunGuardedIterationAsync(100_000, 30_000, dir);

                Assert.Empty(host.ConfirmPrompts);
                Assert.True(state.ContextGuardArmed);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
