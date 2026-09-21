// File: ManualContextSizeSyncTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// LlmClient.Configure() fires DetectContextSizeAsync() without awaiting it; the task
// is awaited only on the first SendMessageAsync. The manual-context-size branch inside
// that task performs no I/O at all — it just assigns _contextSize/_budget/ServerContextSize
// — so making it wait for the task to be scheduled bought nothing and cost correctness:
// until the task ran, ServerContextSize was 0 and EffectiveContextWindow fell back to the
// 13,372 default. Anything reading the window before the first message (the status bar,
// /prompt, the system-prompt size warning) reported 13,372 instead of the configured size.
//
// Configure() now applies the override synchronously. The test that matters is therefore
// the one that reads the window BEFORE any message is sent — a test that sends first would
// pass against the broken code too.
//
// The endpoint deliberately points at a closed port and DEVMIND_SERVER_TYPE is deliberately
// UNSET, so DetectContextSizeAsync() genuinely suspends at its server-type probe before it
// ever reaches the manual branch. With the env override set the method would run to
// completion synchronously and the test would be vacuous.

using System.Net;
using System.Net.Sockets;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ManualContextSizeSyncTests : IDisposable
    {
        private const int ManualSize = 32_768;
        private const int FallbackContextSize = 13_372;   // LlmClient's hardcoded pre-detection default

        private readonly string? _priorServerType;

        public ManualContextSizeSyncTests()
        {
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", null);
        }

        public void Dispose()
            => Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);

        private sealed class ManualSizeOptions : ILlmOptions
        {
            public int Manual { get; init; } = ManualSize;

            public string SystemPrompt => "You are a test assistant.";
            public string ModelName => "test-model";
            public int RequestTimeoutMinutes => 1;
            public int FirstTokenTimeoutMinutes => 1;
            public bool ShowDebugOutput => false;
            public bool ShowContextBudget => false;
            public bool ShowLlmThinking => false;
            public ContextEvictionMode ContextEviction => ContextEvictionMode.Off;
            public int ManualContextSize => Manual;
            public LlmServerType ServerType => LlmServerType.LlamaServer;
            public string CustomContextEndpoint => null!;
            public int MicroCompactThreshold => 99;
            public int NearlineIngestThresholdChars => 8_000;
            public bool MicroCompactSummarize => false;
            public bool MicroCompactBrainwash => false;
            public bool AlwaysConfirmPatch => false;
            public int AgenticLoopMaxDepth => 25;
            public int AgenticContextLimitPercent => 0;
        }

        /// <summary>A port nothing is listening on, so the fire-and-forget server-type probe
        /// inside DetectContextSizeAsync() suspends (and later fails harmlessly — the method
        /// swallows its own IO errors).</summary>
        private static string ClosedEndpoint()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return $"http://127.0.0.1:{port}/v1";
        }

        [Fact]
        public void ManualContextSize_IsInEffectAsSoonAsConfigureReturns()
        {
            var client = new LlmClient(new ManualSizeOptions());

            // Precondition: nothing has been detected yet, so this cannot pass by accident.
            Assert.Equal(0, client.ServerContextSize);

            client.Configure(ClosedEndpoint(), apiKey: null!);

            // NO message is sent, and the detection task is NOT awaited.
            Assert.Equal(ManualSize, client.ServerContextSize);
            Assert.Equal(ManualSize, client.EffectiveContextWindow);
            Assert.NotEqual(FallbackContextSize, client.EffectiveContextWindow);

            // The budget was rebuilt for the real window too, not left on the fallback.
            Assert.Equal(ManualSize, client.MaxPromptTokens);
        }

        // The auto-detection path must be untouched: with no override, Configure() must leave
        // ServerContextSize at 0 and the window on the fallback until detection actually runs.
        [Fact]
        public void NoManualContextSize_LeavesDetectionToDoItsJob()
        {
            var client = new LlmClient(new ManualSizeOptions { Manual = 0 });

            client.Configure(ClosedEndpoint(), apiKey: null!);

            Assert.Equal(0, client.ServerContextSize);
            Assert.Equal(FallbackContextSize, client.EffectiveContextWindow);
        }
    }
}
