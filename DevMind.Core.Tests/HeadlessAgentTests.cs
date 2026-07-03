// File: HeadlessAgentTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the headless delegation stack (BufferedAgenticHost + HeadlessAgent):
//   * scripted multi-iteration turn against FakeSseServer — a native create_file
//     tool call followed by a final text answer: the loop terminates, the file
//     lands on disk, the action journal records it, the transcript is written,
//     and NOTHING is printed to Console (stdout is JSON-RPC in the MCP process).
//   * depth cap — a model that tool-calls forever stops at AgenticLoopMaxDepth.
//   * unreachable endpoint — reported in result.Error, never thrown.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class HeadlessAgentTests : IDisposable
    {
        private readonly string _dir;

        public HeadlessAgentTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_headless_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private static HeadlessOptions Options(int maxDepth = 5) => new HeadlessOptions
        {
            RequestTimeoutMinutes = 1,
            FirstTokenTimeoutMinutes = 1,
            ManualContextSize = 32768, // skip context probes (same as FakeLlmOptions)
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

        [Fact]
        public async Task RunAsync_ToolCallThenAnswer_WritesFileJournalsAndTerminates()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("create_file",
                "{\"filename\":\"hello.txt\",\"content\":\"hello from devmind\"}"));
            // A well-behaved model ends with task_done — the answer travels in its
            // summary argument (plain prose instead triggers LoopDriver's one-shot
            // "call task_done" re-prompt, costing an extra iteration).
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"Created hello.txt as requested.\"}"));

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            string transcriptPath = Path.Combine(_dir, "transcript.log");
            var progress = new StringBuilder();
            try
            {
                using var console = new ConsoleGuard();
                var result = await HeadlessAgent.RunAsync(
                    "Create hello.txt containing a greeting.",
                    Options(), server.BaseUrl, apiKey: null!,
                    workingDirectory: _dir,
                    buildCommand: "dotnet build",   // explicit: keep BuildCommandResolver off the disk
                    transcriptPath: transcriptPath,
                    progress: chunk => progress.Append(chunk),
                    ct: CancellationToken.None);

                Assert.Equal("", console.Captured);                    // NOTHING on stdout
                Assert.Null(result.Error);
                Assert.False(result.Cancelled);
                Assert.False(result.HitDepthCap);
                Assert.Equal(2, result.Iterations);                    // tool turn + task_done turn
                Assert.Equal("Created hello.txt as requested.", result.Answer); // from task_done summary

                // The tool actually executed inside the working directory.
                Assert.Equal("hello from devmind", File.ReadAllText(Path.Combine(_dir, "hello.txt")));

                // Journal records the save with the full path.
                var save = Assert.Single(result.Actions, a => a.Kind == "save");
                Assert.Contains("hello.txt", save.Detail);
                Assert.True(save.Success);

                // Transcript written and streamed via progress.
                Assert.Equal(transcriptPath, result.TranscriptPath);
                string transcript = File.ReadAllText(transcriptPath);
                Assert.Contains("[FILE] Saved hello.txt", transcript);
                Assert.Contains("[AGENTIC] Task complete.", transcript);
                Assert.Equal(transcript, progress.ToString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        [Fact]
        public async Task Session_SecondTurn_CarriesFirstTurnConversation()
        {
            using var server = new FakeSseServer();
            // Turn 1: create a file, then task_done.
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("create_file",
                "{\"filename\":\"alpha.txt\",\"content\":\"alpha content\"}"));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"Wrote alpha.txt.\"}"));
            // Turn 2 (continuation): immediately done.
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"Nothing left to do.\"}"));

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var console = new ConsoleGuard();
                using var session = new HeadlessSession(Options(), server.BaseUrl, apiKey: null!,
                    workingDirectory: _dir, buildCommand: "dotnet build");

                var first = await session.RunTurnAsync("Create alpha.txt with some content.");
                Assert.Null(first.Error);
                Assert.Equal("Wrote alpha.txt.", first.Answer);
                Assert.Single(first.Actions, a => a.Kind == "save");

                var second = await session.RunTurnAsync("continue");
                Assert.Null(second.Error);
                Assert.Equal("Nothing left to do.", second.Answer);
                Assert.Empty(second.Actions); // journal is per-turn

                // The continuation request must carry the FIRST turn's conversation:
                // its prompt, its tool activity, and the bare "continue" itself.
                Assert.Equal(3, server.RequestBodies.Count);
                string continuationRequest = server.RequestBodies[2];
                Assert.Contains("alpha.txt", continuationRequest);
                Assert.Contains("continue", continuationRequest);
                Assert.Equal("", console.Captured);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        [Fact]
        public async Task RunAsync_ModelToolCallsForever_StopsAtDepthCap()
        {
            using var server = new FakeSseServer { RepeatLastWhenExhausted = true };
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("run_shell",
                "{\"command\":\"echo looping\"}"));

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var console = new ConsoleGuard();
                var result = await HeadlessAgent.RunAsync(
                    "Loop forever.", Options(maxDepth: 2), server.BaseUrl, apiKey: null!,
                    workingDirectory: _dir, buildCommand: "dotnet build",
                    ct: CancellationToken.None);

                Assert.Equal("", console.Captured);
                Assert.Null(result.Error);
                Assert.True(result.HitDepthCap);
                Assert.True(result.Iterations >= 2 && result.Iterations <= 3,
                    $"iterations {result.Iterations}");
                Assert.Contains(result.Actions, a => a.Kind == "shell" && a.Detail.Contains("echo looping"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        [Fact]
        public async Task RestrictedHost_BlocksWritesOutsideWorkingDirectory()
        {
            // Regression: a live headless run hallucinated /home/user/greeting.txt —
            // rooted, so it bypassed working-dir resolution and landed in C:\home\user.
            var host = new BufferedAgenticHost(_dir) { RestrictWritesToWorkingDirectory = true };
            IAgenticHost agenticHost = host;

            string outside = Path.Combine(Path.GetTempPath(), $"devmind_escape_{Guid.NewGuid():N}.txt");
            string saved = await agenticHost.SaveFileAsync("/home/user/escape.txt", "nope", fromToolCall: true);
            string savedOutside = await agenticHost.SaveFileAsync(outside, "nope", fromToolCall: true);

            Assert.Null(saved);
            Assert.Null(savedOutside);
            Assert.False(File.Exists(outside));
            Assert.False(File.Exists(@"C:\home\user\escape.txt"));
            Assert.Equal(2, host.GetActions().Count(a => a.Kind == "blocked"));

            // Inside the working directory (relative AND absolute) still works.
            Assert.NotNull(await agenticHost.SaveFileAsync("inside.txt", "yes", fromToolCall: true));
            Assert.NotNull(await agenticHost.SaveFileAsync(Path.Combine(_dir, "inside2.txt"), "yes", fromToolCall: true));
            Assert.True(File.Exists(Path.Combine(_dir, "inside.txt")));
            Assert.True(File.Exists(Path.Combine(_dir, "inside2.txt")));
        }

        [Fact]
        public async Task RunAsync_EndpointUnreachable_ReportsErrorInResult()
        {
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var console = new ConsoleGuard();
                var result = await HeadlessAgent.RunAsync(
                    "Anything.", Options(), "http://127.0.0.1:1/v1", apiKey: null!,
                    workingDirectory: _dir, buildCommand: "dotnet build",
                    ct: CancellationToken.None);

                Assert.Equal("", console.Captured);
                Assert.NotNull(result.Error);
                Assert.False(result.HitDepthCap);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }
    }
}
