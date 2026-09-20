// File: TurnClockIncrementTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Behaviour of the context-aging turn clock against the REAL HeadlessSession loop, after the
// fix that moved the turn increment out of the agentic loop (one increment per user turn, not
// one per iteration).
//
// The bug: HeadlessAgent and the TUI each called LlmClient.IncrementTurn() at the top of their
// while(true) loop — once per ITERATION — violating the contract ("once per user-initiated
// send, not per agentic resubmit"). dropAge (8 Balanced / 5 Aggressive) was tuned for
// user-turns, so per-iteration aging ran the eviction clock ~an order of magnitude too fast:
// in a long job, everything older than 8 iterations was dropped even while the context window
// was nearly empty.
//
// These tests drive a real multi-iteration turn through the GatedSseServer harness and assert:
//   * a multi-iteration turn advances the clock exactly ONCE, and
//   * a continuation on the same session does NOT age-evict a prior job's still-recent
//     messages (the concrete "37 dropped at 3% of context" regression).

using System;
using System.IO;
using Xunit;

namespace DevMind.Core.Tests
{
    public class TurnClockIncrementTests : IDisposable
    {
        private readonly string _dir;

        public TurnClockIncrementTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_turn_clock_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private static HeadlessOptions Options() => new HeadlessOptions
        {
            RequestTimeoutMinutes = 1,
            FirstTokenTimeoutMinutes = 1,
            ManualContextSize = 32768,   // skip context probes
            AgenticLoopMaxDepth = 40,    // high cap: the turn ends on task_done, not the cap
        };

        private HeadlessSession NewSession(string endpoint) => new(
            Options(), endpoint, apiKey: null!,
            workingDirectory: _dir, buildCommand: "dotnet build",
            promptFilePath: Path.Combine(_dir, "nonexistent-prompt.md"));

        // `toolCalls` re-triggering tool calls then task_done (terminal) = toolCalls+1 iterations.
        private static void QueueTurn(GatedSseServer server, int toolCalls)
        {
            for (int i = 0; i < toolCalls; i++)
                server.SseQueue.Add(FakeSseServer.BuildToolCallSse(
                    "create_file", $"{{\"filename\":\"f{Guid.NewGuid():N}.txt\",\"content\":\"x\"}}"));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));
        }

        // A multi-iteration headless turn advances the turn clock exactly ONCE, not once per
        // iteration. Before the fix this was N (one per iteration); the context-aging clock ran
        // N times faster than dropAge was tuned for.
        [Fact]
        public async Task MultiIterationTurn_IncrementsTheClockOnce_NotPerIteration()
        {
            using var server = new GatedSseServer();
            QueueTurn(server, toolCalls: 2);   // 3 iterations

            string? priorType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = NewSession(server.BaseUrl);
                var result = await session.RunTurnAsync("Make three files.");
                Assert.Null(result.Error);
                Assert.Equal(3, result.Iterations);

                // One user turn → one clock advance, regardless of the three iterations.
                Assert.Equal(1, session.CurrentTurnForTest);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", priorType);
            }
        }

        // THE regression (concretely, "37 dropped at 3% of context"). A long job (10
        // iterations) followed by a short continuation on the SAME session must NOT age-evict
        // the job's still-recent messages.
        //
        //   Before:  the job's iterations aged the clock to turn 10; the continuation advanced
        //            it to 11. The job's messages, at the continuation's first send, have ages
        //            1..10 — and age > dropAge (8) drops the oldest two turns in one batch,
        //            while the window is at a few % of capacity.
        //   After:   the whole job shares turn 1; the continuation is turn 2. Every job message
        //            is age 1 < 8, so nothing is dropped.
        //
        // EvictedMessageCountForTest isolates AGE eviction (EvictStaleContext) from the
        // token-budget trims, which are not active at this small context.
        [Fact]
        public async Task Continuation_DoesNot_AgeEvict_PriorJobMessages()
        {
            using var server = new GatedSseServer();
            QueueTurn(server, toolCalls: 9);    // job 1: 10 iterations
            QueueTurn(server, toolCalls: 0);    // job 2: 1 iteration (a continuation)

            string? priorType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            string? priorStrategy = Environment.GetEnvironmentVariable("DEVMIND_CONTEXT_STRATEGY");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            Environment.SetEnvironmentVariable("DEVMIND_CONTEXT_STRATEGY", "transformer");
            try
            {
                using var session = NewSession(server.BaseUrl);

                var job1 = await session.RunTurnAsync("Do ten steps of work.");
                Assert.Null(job1.Error);
                Assert.Equal(10, job1.Iterations);
                Assert.Equal(1, session.CurrentTurnForTest);   // the whole job shared one turn

                var job2 = await session.RunTurnAsync("And one more step.");
                Assert.Null(job2.Error);
                Assert.Equal(1, job2.Iterations);
                Assert.Equal(2, session.CurrentTurnForTest);   // the continuation is the NEXT turn

                // The job's messages are age 1 at the continuation's send — nothing to evict.
                Assert.Equal(0, session.EvictedMessageCountForTest);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", priorType);
                Environment.SetEnvironmentVariable("DEVMIND_CONTEXT_STRATEGY", priorStrategy);
            }
        }
    }
}
