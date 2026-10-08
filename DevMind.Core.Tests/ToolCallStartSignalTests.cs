// File: ToolCallStartSignalTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// LlmClient's onToolCallStart: the one signal that the model stopped reasoning and started a
// tool call. tool_calls deltas never pass through onToken, so a reasoning → tool-call stream
// used to say nothing between its last reasoning token and the end of the request — the TUI
// read "Thinking…" while a long write_file streamed — and it never closed the synthetic
// <think> either, so the status lines after it read as thought.
//
// Streams are scripted the way Strata delivers them: reasoning_content deltas, then tool_calls
// deltas split across chunks (id + name first, arguments in fragments), no content at all.

using Newtonsoft.Json;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class ToolCallStartSignalTests : IDisposable
    {
        private const string Start = "<<TOOL_CALL_START>>";
        private const string Complete = "<<COMPLETE>>";

        private static readonly string[] Reasoning = { "The", " file", " is", " missing", ";", " write", " it", "." };
        private static readonly string[] ArgFragments = { "{\"path\":", "\"notes.txt\",", "\"content\":\"hi\"}" };

        private readonly FakeSseServer _server = new FakeSseServer();
        private readonly LlmClient _client;

        public ToolCallStartSignalTests()
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

        private static string ToolCallDeltas()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\"," +
                      "\"function\":{\"name\":\"create_file\",\"arguments\":\"\"}}]},\"finish_reason\":null}]}\n\n");
            foreach (string f in ArgFragments)
                sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":" +
                          JsonConvert.SerializeObject(f) + "}}]},\"finish_reason\":null}]}\n\n");
            sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
            return sb.ToString();
        }

        private static string Stream(bool reasoning, bool content, bool toolCall)
        {
            var sb = new System.Text.StringBuilder();
            if (reasoning) foreach (string t in Reasoning) sb.Append(Delta("reasoning_content", t));
            if (content) foreach (string t in new[] { "Done", "." }) sb.Append(Delta("content", t));
            if (toolCall) sb.Append(ToolCallDeltas());
            return sb.Append("data: [DONE]\n\n").ToString();
        }

        /// <summary>Every onToken chunk, onToolCallStart and onComplete, in arrival order.</summary>
        private async Task<List<string>> SendAsync(string sse)
        {
            _server.SseQueue.Add(sse);
            var events = new List<string>();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _client.SendMessageAsync("go",
                onToken: t => events.Add(t),
                onComplete: () => { events.Add(Complete); done.TrySetResult(true); },
                onError: ex => done.TrySetException(ex),
                onToolCallStart: () => events.Add(Start));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return events;
        }

        [Fact]
        public async Task ReasoningThenToolCall_SignalsOnce_AfterTheReasoning_AndClosesTheThinkBlock()
        {
            List<string> events = await SendAsync(Stream(reasoning: true, content: false, toolCall: true));

            Assert.Single(events, e => e == Start);
            int start = events.IndexOf(Start);
            int lastReasoning = events.LastIndexOf(Reasoning[^1]);
            int close = events.IndexOf("</think>");

            Assert.True(lastReasoning >= 0, "reasoning never reached onToken");
            Assert.True(close > lastReasoning, "no </think> after the reasoning: " + string.Join(" | ", events));
            Assert.True(start > close, "the signal came before the think block closed");
            Assert.True(start < events.IndexOf(Complete), "the signal came after the request completed");

            // Everything after the signal is outside the think block, so a status line such as
            // [TOOL_USE] reaches the caller as visible text, not as thought.
            var filter = new ThinkFilter();
            string visibleAfter = string.Concat(events.Take(events.IndexOf(Complete))
                .Select(e => e == Start ? "" : filter.Process(e, showThinking: true, out _)));
            Assert.Contains("[TOOL_USE]", visibleAfter);
        }

        [Fact]
        public async Task ReasoningThenToolCall_AccumulatesTheSameToolCall()
        {
            await SendAsync(Stream(reasoning: false, content: false, toolCall: true));
            ToolCallResult baseline = Assert.Single(_client.LastToolCalls);

            await SendAsync(Stream(reasoning: true, content: false, toolCall: true));
            ToolCallResult withReasoning = Assert.Single(_client.LastToolCalls);

            Assert.Equal("call_1", withReasoning.Id);
            Assert.Equal("create_file", withReasoning.Name);
            Assert.Equal("notes.txt", withReasoning.Arguments["path"]);
            Assert.Equal("hi", withReasoning.Arguments["content"]);
            Assert.Equal(baseline.Id, withReasoning.Id);
            Assert.Equal(baseline.Name, withReasoning.Name);
            Assert.Equal(baseline.Arguments, withReasoning.Arguments);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NoToolCall_NeverSignals(bool reasoning)
        {
            List<string> events = await SendAsync(Stream(reasoning, content: true, toolCall: false));

            Assert.DoesNotContain(Start, events);
            Assert.Contains(Complete, events);
        }

        [Fact]
        public async Task ToolCallWithoutReasoning_SignalsOnce_WithNoStrayCloseTag()
        {
            List<string> events = await SendAsync(Stream(reasoning: false, content: false, toolCall: true));

            Assert.Single(events, e => e == Start);
            Assert.DoesNotContain("</think>", events);
        }
    }
}
