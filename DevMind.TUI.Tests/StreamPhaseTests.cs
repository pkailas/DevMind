// File: StreamPhaseTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The status bar's Thinking → Generating flip. A Strata stream carries reasoning on
// delta.reasoning_content; LlmClient re-synthesizes <think>…</think> around it and feeds every
// chunk through onToken. The first reasoning chunk used to flip the phase, so the bar read
// "Generating…" for the whole think. These drive a real LlmClient over a scripted SSE stream
// through the same split RunTurnAsync's onToken lambda does (status check, ThinkFilter,
// StreamPhase) and record the phase after every chunk.

using DevMind.Core.Tests;
using Newtonsoft.Json;
using Xunit;

namespace DevMind.TUI.Tests
{
    public sealed class StreamPhaseTests : IDisposable
    {
        private readonly FakeSseServer _server = new FakeSseServer();
        private readonly LlmClient _client;

        public StreamPhaseTests()
        {
            _client = new LlmClient(new HeadlessOptions
            {
                SystemPrompt = "test",
                ShowLlmThinking = true,
                RequestTimeoutMinutes = 1,
                FirstTokenTimeoutMinutes = 1,
                ManualContextSize = 32768, // skip context probes
            });
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try { _client.Configure(_server.BaseUrl, apiKey: null!); }
            finally { Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior); }
        }

        public void Dispose() => _server.Dispose();

        private static string Delta(string field, string text)
            => "data: {\"choices\":[{\"index\":0,\"delta\":{\"" + field + "\":" + JsonConvert.SerializeObject(text) + "},\"finish_reason\":null}]}\n\n";

        private sealed record Step(string Token, bool IsStatus, string? Think, string Visible, bool Generating);

        /// <summary>Streams one response and returns, per onToken chunk, the phase after it.</summary>
        private async Task<List<Step>> StreamAsync(string sse)
        {
            _server.SseQueue.Add(sse);
            var steps = new List<Step>();
            var filter = new ThinkFilter();
            bool generating = false;
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await _client.SendMessageAsync("hi",
                onToken: token =>
                {
                    bool isStatus = TranscriptNoise.IsInternalStatusLine(token);
                    string visible = filter.Process(token, showThinking: true, out string thinkText);
                    if (!generating && StreamPhase.EndsThinking(isStatus, thinkText, visible))
                        generating = true;
                    steps.Add(new Step(token, isStatus, thinkText, visible, generating));
                },
                onComplete: () => done.TrySetResult(true),
                onError: ex => done.TrySetException(ex),
                // As RunTurnAsync wires it: a tool call starting ends the phase too.
                onToolCallStart: () =>
                {
                    if (StreamPhase.EndsThinkingAtToolCall(generating)) generating = true;
                    steps.Add(new Step(ToolCallStart, false, null, "", generating));
                });
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return steps;
        }

        private const string ToolCallStart = "<<TOOL_CALL_START>>";

        [Fact]
        public async Task ReasoningThenAToolCall_LeavesThinking_AtTheToolCallSignal()
        {
            // No content at all: the model reasons, then streams a tool call. Before the
            // signal existed nothing ended the phase until the request was over.
            string[] reasoning = { "Need", " the", " file", "." };
            var sse = new System.Text.StringBuilder();
            foreach (string t in reasoning) sse.Append(Delta("reasoning_content", t));
            sse.Append("data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\"," +
                       "\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}}]},\"finish_reason\":null}]}\n\n");
            sse.Append("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
            sse.Append("data: [DONE]\n\n");

            List<Step> steps = await StreamAsync(sse.ToString());

            int signal = steps.FindIndex(s => s.Token == ToolCallStart);
            Assert.True(signal > 0, "onToolCallStart never fired: " + string.Join(" | ", steps.Select(s => s.Token)));
            Assert.All(steps.Take(signal), s => Assert.False(s.Generating,
                $"phase left Thinking before the tool call, at '{s.Token}'"));
            Assert.All(steps.Skip(signal), s => Assert.True(s.Generating,
                $"phase was Thinking after the tool call started, at '{s.Token}'"));
        }

        [Fact]
        public void AToolCallEndsThinking_OnlyIfItHasNotEndedAlready()
        {
            Assert.True(StreamPhase.EndsThinkingAtToolCall(generating: false));
            Assert.False(StreamPhase.EndsThinkingAtToolCall(generating: true));
        }

        [Fact]
        public async Task ReasoningContentDeltas_StayThinking_UntilTheFirstContentDelta()
        {
            string[] reasoning = { "The", " user", " wants", " a", " port", ".", "\n\n", "Pick", " 5005", "." };
            string[] content = { "Use", " port", " 5005", "." };
            var sse = new System.Text.StringBuilder();
            foreach (string t in reasoning) sse.Append(Delta("reasoning_content", t));
            foreach (string t in content) sse.Append(Delta("content", t));
            sse.Append("data: [DONE]\n\n");

            List<Step> steps = await StreamAsync(sse.ToString());

            // Every reasoning chunk reached the lambda as thinking text — and none flipped.
            var thinking = steps.Where(s => !string.IsNullOrEmpty(s.Think)).ToList();
            Assert.Equal(string.Concat(reasoning), string.Concat(thinking.Select(s => s.Think)));
            Assert.All(thinking, s => Assert.False(s.Generating,
                $"phase flipped to Generating on reasoning chunk '{s.Token}'"));

            // The first content delta is exactly where Generating starts. DevMind's own status
            // lines ([LLM], [CONTEXT], …) are visible too, but they are not the model talking.
            int firstVisible = steps.FindIndex(s => !s.IsStatus && !string.IsNullOrEmpty(s.Visible));
            Assert.True(firstVisible > 0, "no visible content reached the lambda: "
                + string.Join(" | ", steps.Select(s => s.Token)));
            Assert.Equal("Use", steps[firstVisible].Visible);
            Assert.All(steps.Take(firstVisible), s => Assert.False(s.Generating));
            Assert.All(steps.Skip(firstVisible), s => Assert.True(s.Generating));
        }

        [Fact]
        public void StatusLines_AndEmptyChunks_DoNotEndThinking()
        {
            Assert.False(StreamPhase.EndsThinking(isStatus: true, thinkText: null!, visible: "[LLM] Sending…\n"));
            Assert.False(StreamPhase.EndsThinking(isStatus: false, thinkText: "still reasoning", visible: ""));
            Assert.False(StreamPhase.EndsThinking(isStatus: false, thinkText: null!, visible: null!));
            Assert.True(StreamPhase.EndsThinking(isStatus: false, thinkText: "last thought", visible: "Answer"));
        }
    }
}
