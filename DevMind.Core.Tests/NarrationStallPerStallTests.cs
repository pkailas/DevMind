// File: NarrationStallPerStallTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-52: a narration-only reply ended a delegated job as "done".
//
// job-1974 (2026-10-02) ended state=done after 85 iterations with the single line "Let me
// confirm `MailConnectionSettingsJson` API ... then check the `SmtpSecurityMode` enum
// values." as its answer — no task_done. The narration-stall retry and the prose-finish
// re-prompt each fired at most once per USER TURN, and a delegated job is one user turn: an
// early stall spent them, and the next narration-only reply hit "accepting prose-finish".
//
// Pinned here:
//   * the two guards are per STALL — any tool call re-arms them (headless and TUI driver);
//   * three no-tool-call responses in a row still terminate;
//   * a headless job that ends without task_done / ask_caller is EndedWithoutTaskDone, its
//     narration kept under an [INCOMPLETE: …] marker;
//   * a task_done job and the TUI's prose answer path are unchanged.

using Xunit;
using static DevMind.Core.Tests.LoopDriverTest;   // Tool(); Env lives in LoopDriverTestEnv.cs

namespace DevMind.Core.Tests
{
    public class NarrationStallPerStallTests : IDisposable
    {
        // job-1974's final answer, verbatim. "confirm" is not a narration-claim verb, so this
        // reaches the prose-finish re-prompt (PromptedForTaskDone), not the forced retry.
        private const string Job1974Narration =
            "Let me confirm `MailConnectionSettingsJson` API ... then check the `SmtpSecurityMode` enum values.";

        // job-1975's final answer. "Let me read" IS a claim, so it hits the forced retry first.
        private const string Job1975Narration =
            "Let me read the Notifications CreateConnection tail ...";

        private readonly string _dir;

        public NarrationStallPerStallTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h52_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        // Never created: the hermetic system-prompt seam (see HeadlessAgentTests.NoPromptFile).
        private string NoPromptFile => Path.Combine(_dir, "no-system-prompt.md");

        private static HeadlessOptions Options(int maxDepth = 10) => new HeadlessOptions
        {
            RequestTimeoutMinutes = 1,
            FirstTokenTimeoutMinutes = 1,
            ManualContextSize = 32768,
            AgenticLoopMaxDepth = maxDepth,
        };

        private async Task<HeadlessAgentResult> RunScripted(FakeSseServer server)
        {
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            TextWriter prevOut = Console.Out;
            Console.SetOut(new StringWriter());
            try
            {
                return await HeadlessAgent.RunAsync(
                    "Add the SMTP connection settings.",
                    Options(), server.BaseUrl, apiKey: null!,
                    workingDirectory: _dir, buildCommand: "dotnet build",
                    ct: CancellationToken.None,
                    promptFilePath: NoPromptFile);
            }
            finally
            {
                Console.SetOut(prevOut);
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        // ── Headless ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task SecondNarrationStall_AfterAToolCall_GetsItsOwnRePrompt_AndReachesTaskDone()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("create_file",
                "{\"filename\":\"a.txt\",\"content\":\"one\"}"));
            server.SseQueue.Add(FakeSseServer.BuildTextSse(Job1974Narration));   // stall 1 → re-prompt
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("create_file",
                "{\"filename\":\"b.txt\",\"content\":\"two\"}"));                 // re-arms the guards
            server.SseQueue.Add(FakeSseServer.BuildTextSse(Job1974Narration));   // stall 2 → re-prompt (was: ended "done")
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"Added the SMTP settings.\"}"));

            var result = await RunScripted(server);

            Assert.Null(result.Error);
            Assert.Equal(5, result.Iterations);
            Assert.Equal(5, server.RequestBodies.Count);
            Assert.False(result.EndedWithoutTaskDone);
            Assert.Equal("Added the SMTP settings.", result.Answer);
            Assert.True(File.Exists(Path.Combine(_dir, "b.txt")));
        }

        [Fact]
        public async Task ThreeConsecutiveNarrations_Terminate_AsEndedWithoutTaskDone_KeepingTheNarration()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("create_file",
                "{\"filename\":\"a.txt\",\"content\":\"one\"}"));
            server.SseQueue.Add(FakeSseServer.BuildTextSse(Job1975Narration));   // forced narration retry
            server.SseQueue.Add(FakeSseServer.BuildTextSse(Job1975Narration));   // prose-finish re-prompt
            server.SseQueue.Add(FakeSseServer.BuildTextSse(Job1975Narration));   // third in a row → stop
            // Never reached: a fourth request would mean the loop kept going.
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"should not be requested\"}"));

            var result = await RunScripted(server);

            Assert.Null(result.Error);
            Assert.Equal(4, result.Iterations);
            Assert.Equal(4, server.RequestBodies.Count);
            Assert.True(result.EndedWithoutTaskDone);
            Assert.False(result.HitDepthCap);
            Assert.False(result.ThrashStopped);
            Assert.False(result.NeedsInput);
            Assert.StartsWith(HeadlessAgent.EndedWithoutTaskDoneMarker, result.Answer);
            Assert.Contains(Job1975Narration, result.Answer);
        }

        [Fact]
        public async Task NormalTaskDoneJob_IsNotEndedWithoutTaskDone()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("create_file",
                "{\"filename\":\"a.txt\",\"content\":\"one\"}"));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"Created a.txt.\"}"));

            var result = await RunScripted(server);

            Assert.Null(result.Error);
            Assert.False(result.EndedWithoutTaskDone);
            Assert.Equal("Created a.txt.", result.Answer);
            Assert.DoesNotContain("INCOMPLETE", result.Answer);
        }

        // ── LoopDriver (shared by the TUI) ─────────────────────────────────────────

        [Fact]
        public async Task ToolCall_ReArmsBothStallGuards_AndClearsTheCounter()
        {
            using var env = new Env();
            env.State.NarrationRetryUsed             = true;
            env.State.PromptedForTaskDone            = true;
            env.State.ConsecutiveNoToolCallResponses = 2;

            await env.RunToolTurn(Tool("read_file", "{\"filename\":\"x.cs\"}"));

            Assert.False(env.State.NarrationRetryUsed);
            Assert.False(env.State.PromptedForTaskDone);
            Assert.Equal(0, env.State.ConsecutiveNoToolCallResponses);
        }

        [Fact]
        public async Task Tui_ProseStall_ToolCall_ProseStall_RePromptsBothTimes()
        {
            using var env = new Env();
            var first = await env.RunProseTurn(Job1974Narration);
            Assert.Equal(SyntheticPrompts.ProseFinish, first.NextContextualMessage);

            await env.RunToolTurn(Tool("read_file", "{\"filename\":\"x.cs\"}"));

            var second = await env.RunProseTurn(Job1974Narration);
            Assert.Equal(LoopIterationKind.ShouldReTrigger, second.Kind);
            Assert.Equal(SyntheticPrompts.ProseFinish, second.NextContextualMessage);
        }

        [Fact]
        public async Task Tui_ProseAnswerAfterRePrompt_StillTerminatesWithNoReason()
        {
            // The interactive path is unchanged: prose after the re-prompt is a valid end
            // (an answer to a question), terminal with no reason.
            using var env = new Env();
            await env.RunProseTurn("I need to clarify: which database should I target here?");
            var second = await env.RunProseTurn("PostgreSQL 16 is the target; the schema is in db/init.sql.");

            Assert.Equal(LoopIterationKind.Terminal, second.Kind);
            Assert.Null(second.TerminalReason);
        }

        [Fact]
        public async Task StallCap_TerminatesEvenWithBothGuardsArmed()
        {
            // The explicit hard stop: guards somehow re-armed mid-stall (a future third
            // re-prompt, say) must not let a narrating model loop forever.
            using var env = new Env();
            env.State.ConsecutiveNoToolCallResponses = LoopDriver.MaxConsecutiveNoToolCallResponses - 1;

            var turn = await env.RunProseTurn(Job1975Narration);

            Assert.Equal(LoopIterationKind.Terminal, turn.Kind);
            Assert.False(env.State.NarrationRetryUsed);
            Assert.False(env.State.PromptedForTaskDone);
        }
    }
}
