// File: ReasoningEffortRequestTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// chat_template_kwargs.reasoning_effort on the wire. The Qwen3.8 chat templates (Strata
// Flash-Next, 27B) read reasoning_effort only when enable_thinking is true and treat an ABSENT
// value as xhigh — so before this, every thinking-on request silently ran at xhigh.
//   * thinking off -> exactly { enable_thinking: false }, no reasoning_effort (unchanged)
//   * thinking on, option not set -> "medium" (ILlmOptions default member)
//   * explicit values pass through, normalized to lower case; garbage falls back to medium
//   * devmind.json "reasoningEffort" is read by TuiConfig

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ReasoningEffortRequestTests
    {
        /// <summary>ILlmOptions that does NOT implement ReasoningEffort — exercises the
        /// interface's default member ("medium").</summary>
        private class ThinkingOptions : ILlmOptions
        {
            public string SystemPrompt => "You are a test assistant.";
            public string ModelName => "test-model";
            public int RequestTimeoutMinutes => 1;
            public int FirstTokenTimeoutMinutes => 1;
            public bool ShowDebugOutput => false;
            public bool ShowContextBudget => false;
            public bool ShowLlmThinking { get; set; }
            public ContextEvictionMode ContextEviction => ContextEvictionMode.Off;
            public int ManualContextSize => 32768;
            public LlmServerType ServerType => LlmServerType.LlamaServer;
            public string CustomContextEndpoint => null!;
            public int MicroCompactThreshold => 99;
            public int NearlineIngestThresholdChars => 8_000;
            public bool MicroCompactSummarize => false;
            public bool MicroCompactBrainwash => false;
            public bool AlwaysConfirmPatch => false;
            public ApprovalMode ApprovalMode => ApprovalMode.Auto;
            public int AgenticLoopMaxDepth => 25;
            public int AgenticContextLimitPercent => 0;
        }

        /// <summary>Re-implements ILlmOptions so ReasoningEffort is settable.</summary>
        private sealed class EffortOptions : ThinkingOptions, ILlmOptions
        {
            public string ReasoningEffort { get; set; } = null!;
        }

        private static async Task<JObject> SendOneAndGetTemplateKwargs(ILlmOptions options)
        {
            using var server = new FakeSseServer();
            var client = new LlmClient(options);

            // DEVMIND_SERVER_TYPE=llama skips the /v1/models probe (the same branch an
            // auto-detected non-vLLM server such as Strata takes); ManualContextSize skips
            // the context probe, so the only request the server sees is the chat POST.
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try { client.Configure(server.BaseUrl, apiKey: null); }
            finally { Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior); }

            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.IncrementTurn();
            await client.SendMessageAsync(
                "hello",
                onToken: _ => { },
                onComplete: () => done.TrySetResult(true),
                onError: ex => done.TrySetException(ex));
            Assert.True(await Task.WhenAny(done.Task, Task.Delay(15000)) == done.Task,
                "SendMessageAsync did not complete within 15s");
            await done.Task;

            var body = JObject.Parse(server.RequestBodies.Single());
            return Assert.IsType<JObject>(body["chat_template_kwargs"]);
        }

        [Fact]
        public async Task ThinkingOff_SendsOnlyEnableThinkingFalse_NoReasoningEffort()
        {
            // Effort deliberately set: it must still not be sent while thinking is off.
            var ctk = await SendOneAndGetTemplateKwargs(
                new EffortOptions { ShowLlmThinking = false, ReasoningEffort = "xhigh" });

            Assert.False((bool)ctk["enable_thinking"]!);
            Assert.Null(ctk["reasoning_effort"]);
            Assert.Single(ctk.Properties());
        }

        [Fact]
        public async Task ThinkingOn_OptionNotImplemented_SendsMedium()
        {
            var ctk = await SendOneAndGetTemplateKwargs(new ThinkingOptions { ShowLlmThinking = true });

            Assert.True((bool)ctk["enable_thinking"]!);
            Assert.Equal("medium", (string?)ctk["reasoning_effort"]);
        }

        [Fact]
        public async Task ThinkingOn_HeadlessOptionsDefault_SendsMedium()
        {
            var opts = new HeadlessOptions { ShowLlmThinking = true, ManualContextSize = 32768 };
            Assert.Equal("medium", opts.ReasoningEffort);
            Assert.Equal(LlmServerType.LlamaServer, opts.ServerType); // the template-kwargs branch

            var ctk = await SendOneAndGetTemplateKwargs(opts);
            Assert.Equal("medium", (string?)ctk["reasoning_effort"]);
        }

        [Theory]
        [InlineData("low", "low")]
        [InlineData("xhigh", "xhigh")]
        [InlineData("HIGH", "high")]
        [InlineData(" Medium ", "medium")]
        [InlineData("turbo", "medium")] // unrecognised -> default, never absent (absent = xhigh)
        [InlineData("", "medium")]
        public async Task ThinkingOn_ExplicitEffort_PassedThroughNormalized(string configured, string expected)
        {
            var ctk = await SendOneAndGetTemplateKwargs(
                new EffortOptions { ShowLlmThinking = true, ReasoningEffort = configured });

            Assert.True((bool)ctk["enable_thinking"]!);
            Assert.Equal(expected, (string?)ctk["reasoning_effort"]);
        }

        [Theory]
        [InlineData("low", true, "low")]
        [InlineData("XHIGH", true, "xhigh")]
        [InlineData("  high ", true, "high")]
        [InlineData("extreme", false, null)]
        [InlineData("", false, null)]
        [InlineData(null, false, null)]
        public void TryNormalize(string? input, bool ok, string? expected)
        {
            Assert.Equal(ok, ReasoningEffort.TryNormalize(input!, out string normalized));
            Assert.Equal(expected, normalized);
        }

        [Fact]
        public void TuiConfig_ReadsReasoningEffort_AndOmitsItWhenAbsent()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"devmind_effort_cfg_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                string withKey = Path.Combine(dir, "with.json");
                File.WriteAllText(withKey, "{ \"reasoningEffort\": \"low\" }");
                Assert.Equal("low", TuiConfig.LoadFrom(withKey).ReasoningEffort);

                string without = Path.Combine(dir, "without.json");
                File.WriteAllText(without, "{ \"depthCap\": 30 }");
                Assert.Null(TuiConfig.LoadFrom(without).ReasoningEffort);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
