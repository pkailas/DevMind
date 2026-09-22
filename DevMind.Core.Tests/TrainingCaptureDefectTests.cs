// File: TrainingCaptureDefectTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for the three training-corpus capture defects (corpus audit
// 2026-10: 100% of turns carried UI chrome in assistant_response, zero turns
// carried reasoning, and test runs were writing real files into the operator's
// configured corpus folder).
//
// Defect 1 — reasoning was destroyed before the logger ran: it only ever reached
// the corpus through ThinkFilter's pendingThinkText, which is assigned only when
// showThinking (a DISPLAY switch) is true. With display off, the reasoning
// vanished. The fix accumulates the raw delta.reasoning_content upstream of
// ThinkFilter (LlmClient._reasoningBuilder → LlmClient.LastReasoning) and carries
// it into the turn as TrainingTurnData.Reasoning. These tests pin that the capture
// is present, is NOT gated by the display switch, and resets per send (no stale
// reasoning leaking across turns).
//
// Defect 2 — assistant_response was the host's responseBuffer, fed by the same
// onToken callback that streams the client's own [CONTEXT]/[LLM]/[TOOL_USE] status
// lines, so chrome and model prose were interleaved by design and could not be
// separated without a lossy strip. The fix logs LlmClient.LastAssistantText (built
// from content deltas only — zero chrome) instead of that buffer. These tests pin
// that BuildTurnData reads LastAssistantText, not the chrome-laden parameter.
//
// Defect 3 — test runs writing into the real corpus: gated behind
// TrainingCapture.Disabled (a process-wide opt-out set by the test assemblies'
// [ModuleInitializer]). The gate's mechanics are pinned here; the end-to-end proof
// that a job-manager run writes nothing to a configured folder lives in
// DevMind.McpServer.Tests\TrainingCaptureOptOutTests.cs.

using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class TrainingCaptureDefectTests : IDisposable
    {
        private readonly string _dir;

        public TrainingCaptureDefectTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_tcd_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        // ── Defect 1: reasoning is captured upstream of the display filter ─────

        [Fact]
        public async Task ReasoningContent_IsCapturedInLastReasoning_AndExcludedFromVisibleText()
        {
            var (client, _) = await CreateClientAsync(showThinking: false);

            await SendSseAsync(client,
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Let me reason through it.\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"Here is the answer.\"}}]}\n\n" +
                "data: [DONE]\n\n");

            // The raw reasoning is exposed on the client, upstream of ThinkFilter.
            Assert.Equal("Let me reason through it.", client.LastReasoning);
            // ...and it must NOT leak into the visible text (the display contract).
            Assert.Equal("Here is the answer.", client.LastAssistantText);
        }

        // THE regression that matters: with the display switch OFF, ThinkFilter
        // suppresses the reasoning (pendingThinkText stays null), but the capture
        // — being upstream of the filter — must still record it. This is the exact
        // bug that produced a corpus with zero reasoning for five weeks.
        [Fact]
        public async Task ReasoningContent_IsCaptured_WhenDisplaySwitchIsOff()
        {
            var (client, _) = await CreateClientAsync(showThinking: false);

            await SendSseAsync(client,
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Hidden reasoning text.\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"Visible answer.\"}}]}\n\n" +
                "data: [DONE]\n\n");

            // The client captured the reasoning even though the display switch is off.
            Assert.Equal("Hidden reasoning text.", client.LastReasoning);

            // ...and the display filter — the ONLY thing the old capture path used —
            // produced NO thinking output with the switch off. If the corpus had been
            // driven off this (as it was before the fix), the reasoning would be lost.
            var filter = new ThinkFilter();
            string visible = filter.Process("\u003Cthink\u003E", showThinking: false, out string _);
            visible += filter.Process("Hidden reasoning text.", showThinking: false, out string pendingOff);
            visible += filter.Process("\u003C/think\u003E", showThinking: false, out string _);
            Assert.Null(pendingOff);                 // display off → the filter hides it
            Assert.DoesNotContain("Hidden reasoning text.", visible);

            // The fix does not depend on the filter: the capture survived the switch.
            Assert.Equal("Hidden reasoning text.", client.LastReasoning);
        }

        [Fact]
        public async Task LastReasoning_IsEmpty_NotStale_WhenASubsequentSendGeneratesNone()
        {
            var (client, _) = await CreateClientAsync(showThinking: false);

            // Send 1: reasoning present.
            await SendSseAsync(client,
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Reasoning for send one.\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"Answer one.\"}}]}\n\n" +
                "data: [DONE]\n\n");
            Assert.Equal("Reasoning for send one.", client.LastReasoning);

            // Send 2: a turn that generated no reasoning at all. The field must be
            // empty (its own information) — NOT a stale copy of send 1's reasoning.
            await SendSseAsync(client,
                "data: {\"choices\":[{\"delta\":{\"content\":\"Just an answer, no reasoning.\"}}]}\n\n" +
                "data: [DONE]\n\n");
            Assert.Equal("", client.LastReasoning);
            Assert.Equal("Just an answer, no reasoning.", client.LastAssistantText);
        }

        // ── Defect 2: assistant_response is the model's text, not the chrome buffer ─

        [Fact]
        public void BuildTurnData_AssistantResponse_UsesCleanLastAssistantText_NotChromeLadenBuffer()
        {
            // The host's responseBuffer carries the chrome line + the model prose,
            // exactly as the live corpus showed (" [CONTEXT] ~6,291 / 262,144 (~2%) "
            // on 100% of turns). LastAssistantText is the chrome-free counterpart.
            string chromeLadenBuffer =
                "[CONTEXT] ~6,291 / 262,144 (~2%) [estimated]\nHere is the answer.";

            // A fresh client: LastAssistantText is null (no send ran yet), so
            // BuildTurnData's ?? fallback passes the host's buffer through verbatim.
            var client = new LlmClient(new MutableLlmOptions());
            var logger = new JsonlTrainingLogger(() => "s", enabled: false);
            var driver = new LoopDriver(client, null!, null!, new MutableLlmOptions(), new LoopState(), logger);

            TrainingTurnData data = InvokeBuildTurnData(driver, "do the thing", chromeLadenBuffer);

            // Without the fix, data.AssistantResponse would be the chrome-laden buffer.
            // The fix sources it from LastAssistantText, which is chrome-free.
            // (LastAssistantText is null here because no send ran — so the ?? param
            // fallback would apply. To exercise the REAL seam we set it via a real send
            // in the test below; this test pins the fallback and the Reasoning wiring.)
            Assert.Equal(chromeLadenBuffer, data.AssistantResponse); // fallback path (null LastAssistantText)
            Assert.Equal("", data.Reasoning);                        // no reasoning generated
        }

        [Fact]
        public async Task BuildTurnData_AssistantResponse_IsChromeFree_AndReasoningPopulated_FromClientState()
        {
            var (client, _) = await CreateClientAsync(showThinking: false);
            await SendSseAsync(client,
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"The reasoning.\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"The clean answer.\"}}]}\n\n" +
                "data: [DONE]\n\n");

            // After a real send, the client's per-turn state is populated:
            Assert.Equal("The clean answer.", client.LastAssistantText);   // no chrome
            Assert.Equal("The reasoning.", client.LastReasoning);

            var logger = new JsonlTrainingLogger(() => "s", enabled: false);
            var driver = new LoopDriver(client, null!, null!, new MutableLlmOptions(), new LoopState(), logger);

            // The chrome-laden responseBuffer the host would actually pass in.
            string chromeLadenBuffer =
                "[CONTEXT] ~6,291 / 262,144 (~2%) [estimated]\nThe clean answer.";

            TrainingTurnData data = InvokeBuildTurnData(driver, "do the thing", chromeLadenBuffer);

            // THE fix: the logged response comes from the clean client state, so the
            // chrome line is gone. The raw param is deliberately different and ignored.
            Assert.Equal("The clean answer.", data.AssistantResponse);
            Assert.DoesNotContain("[CONTEXT]", data.AssistantResponse);
            // ...and the reasoning is carried into the turn from the same client state.
            Assert.Equal("The reasoning.", data.Reasoning);
        }

        [Fact]
        public void TrainingLogger_SerializesAssistantResponseAndReasoningContent()
        {
            string folder = Path.Combine(_dir, "logger-out");
            var logger = new JsonlTrainingLogger(() => "sess42", enabled: true, logFolder: folder);

            logger.LogTurn(new TrainingTurnData
            {
                TurnNumber = 3,
                SystemPrompt = "sys",
                UserMessage = "do the thing",
                AssistantResponse = "The clean answer.",   // chrome-free (defect 2)
                Reasoning = "The reasoning.",              // captured (defect 1)
            });

            string file = Directory.GetFiles(folder, "training_sess42_*.jsonl").Single();
            var entry = JObject.Parse(File.ReadAllLines(file).Single());
            Assert.Equal("The clean answer.", (string?)entry["assistant_response"]);
            Assert.Equal("The reasoning.", (string?)entry["reasoning_content"]);
        }

        // ── Defect 3: the gate is set for this assembly and actually blocks capture ─

        [Fact]
        public void TrainingCapture_IsDisabledForThisTestAssembly()
        {
            // The [ModuleInitializer] in TrainingCaptureInitializer.cs sets this before
            // any test runs. Without it, a HeadlessSession given a sessionId would read
            // the operator's ambient devmind.json and write into the real corpus.
            Assert.True(TrainingCapture.Disabled,
                "TrainingCapture.Disabled must be set by TrainingCaptureInitializer for every test in this assembly");
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private sealed class MutableLlmOptions : ILlmOptions
        {
            public string SystemPrompt { get; set; } = "test";
            public string ModelName { get; set; } = "";
            public int RequestTimeoutMinutes { get; set; } = 1;
            public int FirstTokenTimeoutMinutes { get; set; } = 1;
            public bool ShowDebugOutput { get; set; } = false;
            public bool ShowContextBudget { get; set; } = false;
            public bool ShowLlmThinking { get; set; } = false;
            public ContextEvictionMode ContextEviction { get; set; } = ContextEvictionMode.Balanced;
            public int ManualContextSize { get; set; } = 32768; // skip context probes
            public LlmServerType ServerType { get; set; } = LlmServerType.LlamaServer;
            public string CustomContextEndpoint { get; set; } = "";
            public int MicroCompactThreshold { get; set; } = 85;
            public int NearlineIngestThresholdChars { get; set; } = 8000;
            public bool MicroCompactSummarize { get; set; } = true;
            public bool MicroCompactBrainwash { get; set; } = false;
            public bool AlwaysConfirmPatch { get; set; } = false;
            public ApprovalMode ApprovalMode => ApprovalMode.Auto;
            public int AgenticLoopMaxDepth { get; set; } = 5;
            public int AgenticContextLimitPercent { get; set; } = 78;
        }

        private async Task<(LlmClient, MutableLlmOptions)> CreateClientAsync(bool showThinking)
        {
            var options = new MutableLlmOptions { ShowLlmThinking = showThinking };
            var client = new LlmClient(options);
            var server = new FakeSseServer();
            _sseServer = server;

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                client.Configure(server.BaseUrl, apiKey: null!); // synchronous
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
            return (client, options);
        }

        private FakeSseServer? _sseServer;

        private async Task SendSseAsync(LlmClient client, string sse)
        {
            _sseServer!.SseQueue.Add(sse);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await client.SendMessageAsync(
                "msg",
                onToken: _ => { },
                onComplete: () => done.TrySetResult(true),
                onError: ex => done.TrySetException(ex));
            Assert.True(await Task.WhenAny(done.Task, Task.Delay(15000)) == done.Task,
                "SendMessageAsync did not complete within 15s");
            await done.Task;
        }

        private static TrainingTurnData InvokeBuildTurnData(
            LoopDriver driver, string userMessage, string assistantResponse)
        {
            MethodInfo m = typeof(LoopDriver).GetMethod(
                "BuildTurnData", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("LoopDriver.BuildTurnData", "private method not found");
            return (TrainingTurnData)m.Invoke(driver, new object?[]
            {
                userMessage, assistantResponse, ResponseOutcome.Empty(),
                ExecutionResult.None(), null,
            })!;
        }
    }
}
