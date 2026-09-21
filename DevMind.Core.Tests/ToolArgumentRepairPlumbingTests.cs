// File: ToolArgumentRepairPlumbingTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// ToolArgumentRepairTests pins the ladder in isolation. This pins that the ladder is
// actually REACHED by the arguments the loop executes — which is a separate claim, because
// the sanitizer that motivated the work only cleans what goes into history, while
// LastToolCalls (what the tool actually runs with) is built by a different parser.
// Repairing one and not the other would leave history and execution disagreeing about what
// the model asked for.
//
// Driven end to end over a real HTTP SSE stream from the fake server, so the payload
// travels the same path a model's would.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ToolArgumentRepairPlumbingTests
    {
        /// <summary>Sends one turn and returns everything written to onToken.</summary>
        private static async Task<(LlmClient client, string transcript)> RunOneTurnAsync(string rawArgumentsJson)
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("read_file", rawArgumentsJson));

            var client = new LlmClient(new FakeLlmOptions());

            // Both probes are skipped, so the only request the server sees is the
            // /chat/completions POST under test.
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                client.Configure(server.BaseUrl, apiKey: null!);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }

            var transcript = new StringBuilder();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.IncrementTurn();
            await client.SendMessageAsync(
                "read the file",
                onToken: t => { lock (transcript) transcript.Append(t); },
                onComplete: () => done.TrySetResult(true),
                onError: ex => done.TrySetException(ex));

            Assert.True(await Task.WhenAny(done.Task, Task.Delay(15000)) == done.Task,
                "SendMessageAsync did not complete within 15s");
            await done.Task;

            lock (transcript) return (client, transcript.ToString());
        }

        // ── The arguments the loop will actually execute ────────────────────────

        [Fact]
        public async Task TruncatedArgumentsOverTheWire_ReachTheToolWithTheirValues()
        {
            // Before the ladder this produced Arguments = {} and read_file failed with
            // "filename is required" — an error about the wrong thing entirely.
            var (client, _) = await RunOneTurnAsync("{\"filename\":\"Program.cs\",\"start_line\":10");

            var call = Assert.Single(client.LastToolCalls!);
            Assert.Equal("read_file", call.Name);
            Assert.Equal("Program.cs", call.Arguments["filename"]);
            Assert.Equal("10", call.Arguments["start_line"]);
        }

        [Fact]
        public async Task DuplicatedArgumentsOverTheWire_ReachTheToolOnce()
        {
            var (client, _) = await RunOneTurnAsync("{\"filename\":\"a.cs\"}{\"filename\":\"a.cs\"}");

            var call = Assert.Single(client.LastToolCalls!);
            Assert.Equal("a.cs", call.Arguments["filename"]);
        }

        [Fact]
        public async Task WellFormedArgumentsOverTheWire_AreUntouchedAndSilent()
        {
            var (client, transcript) = await RunOneTurnAsync("{\"filename\":\"Program.cs\"}");

            var call = Assert.Single(client.LastToolCalls!);
            Assert.Equal("Program.cs", call.Arguments["filename"]);
            Assert.Single(call.Arguments);

            // A clean call must produce no repair chatter at all.
            Assert.DoesNotContain("[TOOL_ARGS]", transcript, StringComparison.Ordinal);
        }

        // ── The repair is visible ───────────────────────────────────────────────

        [Fact]
        public async Task ARepairedCallAnnouncesItself_NamingTheToolAndTheSize()
        {
            var (_, transcript) = await RunOneTurnAsync("{\"filename\":\"Program.cs\",\"start_line\":10");

            Assert.Contains("[TOOL_ARGS]", transcript, StringComparison.Ordinal);
            Assert.Contains("read_file", transcript, StringComparison.Ordinal);
            Assert.Contains("truncated at", transcript, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AnUnrepairableCallAnnouncesTheFailure_AndStillRunsTheTool()
        {
            var (client, transcript) = await RunOneTurnAsync("this is not json");

            // Still a tool call — dropping it would push the loop onto the prose-finish
            // path, which asks for task_done and ends the run on a parse glitch.
            var call = Assert.Single(client.LastToolCalls!);
            Assert.Equal("read_file", call.Name);
            Assert.Empty(call.Arguments);

            Assert.Contains("could not be parsed or repaired", transcript, StringComparison.Ordinal);
            Assert.Contains("running with empty arguments", transcript, StringComparison.Ordinal);
            Assert.Contains("this is not json", transcript, StringComparison.Ordinal);
        }
    }
}
