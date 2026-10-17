// File: LiveTokenFallbackTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// LlmClient's client-side live tok/s fallback. Strata ignores timings_per_token: it sends no
// per-chunk timings and no per-chunk usage, only the terminal chunk carries them. Live counters
// used to sit at 0 for the whole stream, so the TUI divided its onToken count by wall time and
// missed every tool_call delta (~5 tok/s shown against a true ~150). The fallback counts
// token-bearing chunks (reasoning, content, tool_calls) when, and only when, the server has sent
// no live data; the terminal timings still finalize the numbers. llama.cpp streams (per-chunk
// timings) must keep reading the server's values untouched.
//
// Mid-stream values are sampled through StreamDataReceived, which LlmClient invokes at the start
// of each data line, so each sample is the state after the previous chunk was processed.

using Newtonsoft.Json;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class LiveTokenFallbackTests : IDisposable
    {
        private const int ArgChunks = 20;
        private static readonly string[] Content = { "Writing", " the", " file" };

        private readonly FakeSseServer _server = new FakeSseServer();

        public void Dispose() => _server.Dispose();

        private LlmClient NewClient(string serverType)
        {
            var client = new LlmClient(new HeadlessOptions
            {
                SystemPrompt = "test",
                RequestTimeoutMinutes = 1,
                FirstTokenTimeoutMinutes = 1,
                ManualContextSize = 32768, // skip context probes
            });
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", serverType);
            try { client.Configure(_server.BaseUrl, apiKey: null!); }
            finally { Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior); }
            return client;
        }

        private static string Chunk(string deltaJson, string? extra = null)
            => "data: {\"choices\":[{\"index\":0,\"delta\":" + deltaJson + ",\"finish_reason\":null}]"
               + (extra == null ? "" : "," + extra) + "}\n\n";

        private static string ContentDelta(string text)
            => "{\"content\":" + JsonConvert.SerializeObject(text) + "}";

        private static string ToolOpenerDelta()
            => "{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\"," +
               "\"function\":{\"name\":\"write_file\",\"arguments\":\"\"}}]}";

        private static string ToolArgDelta(string fragment)
            => "{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":" + JsonConvert.SerializeObject(fragment) + "}}]}";

        private static string ArgFragment(int i)
            => i == 0 ? "{\"path\":\"a.txt\",\"content\":\"" : i == ArgChunks - 1 ? "z\"}" : "x";

        private static string Terminal(int predictedN, double predictedMs, double perSec)
            => "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]," +
               "\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":" + predictedN + ",\"total_tokens\":" + (1000 + predictedN) + "}," +
               "\"timings\":{\"prompt_n\":10,\"cache_n\":990,\"prompt_ms\":50.0,\"predicted_n\":" + predictedN +
               ",\"predicted_ms\":" + predictedMs.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               ",\"predicted_per_second\":" + perSec.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}\n\n";

        /// <summary>Sends one request; returns LiveGeneratedTokens sampled at the start of each data line.</summary>
        private static async Task<List<int>> SendAsync(LlmClient client, FakeSseServer server, string sse)
        {
            server.SseQueue.Add(sse);
            var samples = new List<int>();
            client.StreamDataReceived = () => samples.Add(client.LiveGeneratedTokens);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await client.SendMessageAsync("go",
                onToken: _ => { },
                onComplete: () => done.TrySetResult(true),
                onError: ex => done.TrySetException(ex));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            client.StreamDataReceived = null;
            return samples;
        }

        [Fact]
        public async Task StrataShapedStream_CountsToolCallChunksLive_ThenFinalizesFromTerminalTimings()
        {
            LlmClient client = NewClient("strata");

            var sb = new System.Text.StringBuilder();
            sb.Append(Chunk("{\"role\":\"assistant\",\"content\":\"\"}")); // role-only opener: no token
            foreach (string t in Content) sb.Append(Chunk(ContentDelta(t)));
            sb.Append(Chunk(ToolOpenerDelta()));
            for (int i = 0; i < ArgChunks; i++) sb.Append(Chunk(ToolArgDelta(ArgFragment(i))));
            // Terminal count deliberately BELOW the 24 counted chunks: the estimate can overshoot,
            // and the final value must snap to the server's number, not keep the overshoot.
            sb.Append(Terminal(predictedN: 22, predictedMs: 154.5, perSec: 142.4));
            sb.Append("data: [DONE]\n\n");

            List<int> samples = await SendAsync(client, _server, sb.ToString());

            // samples[k] = state after chunk k-1. Chunk 0 is the role-only opener (not counted).
            int tokenChunks = Content.Length + 1 + ArgChunks; // 24
            Assert.Equal(tokenChunks + 2, samples.Count);     // opener + token chunks + terminal
            Assert.Equal(0, samples[0]);
            Assert.Equal(0, samples[1]);                      // after the role-only opener
            for (int k = 1; k <= tokenChunks; k++)
                Assert.Equal(k, samples[k + 1]);              // advances once per token chunk, tool_calls included
            Assert.Equal(tokenChunks, samples[^1]);           // live before the terminal chunk

            // Terminal timings are authoritative once the stream ends.
            Assert.Equal(22, client.LastGeneratedTokens);
            Assert.Equal(154.5, client.LastGeneratedMs, 3);
            Assert.Equal(22, client.LiveGeneratedTokens);
            Assert.Equal(142.4, client.LiveTokensPerSecond, 3);
            Assert.Single(client.LastToolCalls);
        }

        [Fact]
        public async Task LlamaShapedStream_ReadsServerLiveValues_FallbackDormant()
        {
            LlmClient client = NewClient("llama");

            // Per-chunk timings whose running count trails the chunk count (two chunks per token),
            // so an active fallback would be visible: it would push LiveGeneratedTokens to the
            // chunk count. Dormant, the live value is exactly the server's predicted_n.
            int chunks = Content.Length + 1 + ArgChunks; // 24
            var expected = new List<int>();
            var sb = new System.Text.StringBuilder();
            for (int c = 0; c < chunks; c++)
            {
                int predictedN = c / 2 + 1;
                expected.Add(predictedN);
                string timings = "\"timings\":{\"prompt_n\":10,\"cache_n\":990,\"predicted_n\":" + predictedN +
                                 ",\"predicted_ms\":" + (predictedN * 10) + ".0,\"predicted_per_second\":100.0}";
                string delta = c < Content.Length ? ContentDelta(Content[c])
                    : c == Content.Length ? ToolOpenerDelta()
                    : ToolArgDelta(ArgFragment(c - Content.Length - 1));
                sb.Append(Chunk(delta, timings));
            }
            sb.Append(Terminal(predictedN: 12, predictedMs: 120.0, perSec: 98.7));
            sb.Append("data: [DONE]\n\n");

            List<int> samples = await SendAsync(client, _server, sb.ToString());

            Assert.Equal(chunks + 1, samples.Count);
            Assert.Equal(0, samples[0]);
            for (int c = 0; c < chunks; c++)
                Assert.Equal(expected[c], samples[c + 1]);

            Assert.Equal(12, client.LastGeneratedTokens);
            Assert.Equal(120.0, client.LastGeneratedMs, 3);
            Assert.Equal(12, client.LiveGeneratedTokens);
            Assert.Equal(98.7, client.LiveTokensPerSecond, 3);
        }
    }
}
