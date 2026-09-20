// File: ContextWindowDenominatorTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Pins the denominator used for "what percentage of the context window is this prompt".
//
// The drift: the system-prompt size warning (LlmClient.UpdateSystemPrompt) used the RAW
// window, while the TUI's /prompt command was wired to MaxPromptTokens. Those agree once
// the server reports n_ctx, but before detection completes MaxPromptTokens returns
// HistoryHardLimit — the window MINUS the response-headroom reservation — so the two
// reported different percentages for the same prompt text. Both now read one accessor,
// LlmClient.EffectiveContextWindow, so they cannot drift apart again.
//
// These tests pin the distinction rather than the constants: the point is not that the
// fallback is 13,372 but that the two properties MEAN different things and only one of
// them is a valid percentage denominator.

using Xunit;

namespace DevMind.Core.Tests
{
    public class ContextWindowDenominatorTests
    {
        /// <summary>Options with no manual context size, so the client sits in the
        /// pre-detection fallback state where the two properties diverge.</summary>
        private sealed class NoManualSizeOptions : ILlmOptions
        {
            public string SystemPrompt => "You are a test assistant.";
            public string ModelName => "test-model";
            public int RequestTimeoutMinutes => 1;
            public int FirstTokenTimeoutMinutes => 1;
            public bool ShowDebugOutput => false;
            public bool ShowContextBudget => false;
            public bool ShowLlmThinking => false;
            public ContextEvictionMode ContextEviction => ContextEvictionMode.Off;
            public int ManualContextSize => 0;   // no override — detection has not run
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

        // The bug, stated as an invariant: before detection the two properties differ, and
        // MaxPromptTokens is the SMALLER of the two because it has already subtracted the
        // response headroom. Anything dividing by it overstates the percentage.
        [Fact]
        public void BeforeDetection_MaxPromptTokensIsReducedByResponseHeadroom()
        {
            var client = new LlmClient(new NoManualSizeOptions());

            Assert.Equal(0, client.ServerContextSize); // precondition: detection has not run

            int rawWindow = client.EffectiveContextWindow;
            int historyCeiling = client.MaxPromptTokens;

            Assert.True(historyCeiling < rawWindow,
                $"MaxPromptTokens ({historyCeiling}) must be below the raw window ({rawWindow}) " +
                "— it is the history ceiling, not the window");

            // The gap is exactly the response-headroom reservation, which is what makes
            // MaxPromptTokens the wrong denominator for a "% of window" figure.
            Assert.Equal(rawWindow - client.ResponseHeadroomTokens, historyCeiling);
        }

        // The concrete divergence the user sees: the same prompt reported against the two
        // denominators yields materially different percentages. This is the before/after
        // the fix removes — both callers now use EffectiveContextWindow.
        [Fact]
        public void BeforeDetection_TheTwoDenominatorsDisagreeMaterially()
        {
            var client = new LlmClient(new NoManualSizeOptions());

            // A prompt sized to land near the 5% guideline against the raw window.
            int promptTokens = (int)(client.EffectiveContextWindow * 0.05);

            double pctAgainstRawWindow = promptTokens * 100.0 / client.EffectiveContextWindow;
            double pctAgainstHistoryCeiling = promptTokens * 100.0 / client.MaxPromptTokens;

            Assert.Equal(5.0, pctAgainstRawWindow, precision: 1);
            Assert.True(pctAgainstHistoryCeiling > pctAgainstRawWindow);

            // Not a rounding difference — the headroom reservation is 15%, so the inflated
            // figure is ~17-18% higher in relative terms. Pin that it is well over 10%,
            // i.e. large enough to move a prompt across the 5% guideline spuriously.
            double relativeInflation =
                (pctAgainstHistoryCeiling - pctAgainstRawWindow) / pctAgainstRawWindow;
            Assert.True(relativeInflation > 0.10,
                $"expected a material gap, got {relativeInflation:P1} " +
                $"({pctAgainstRawWindow:F2}% vs {pctAgainstHistoryCeiling:F2}%)");
        }

        // End-to-end: the size warning the user actually reads must quote the RAW window.
        // This is the number /prompt is now wired to report too (Program.cs assigns
        // llmClient.EffectiveContextWindow to CommandContext.ContextWindowSize), so the two
        // agree by construction. The assertion is written against the two candidate values
        // explicitly: with a 32,768 window the correct figure is 32,768 and the figure the
        // old /prompt denominator would have produced is 27,853 (window - 15% headroom).
        [Fact]
        public async Task SizeWarning_QuotesTheRawWindow_NotTheHistoryCeiling()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"done\"}"));

            string dir = Path.Combine(Path.GetTempPath(), $"devmind_denom_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                var options = new HeadlessOptions
                {
                    RequestTimeoutMinutes = 1,
                    FirstTokenTimeoutMinutes = 1,
                    ManualContextSize = 32_768,
                    AgenticLoopMaxDepth = 5,
                    // ~5,000 tokens — comfortably over 5% of 32,768 (1,638), so the
                    // one-shot warning fires.
                    SystemPrompt = new string('p', 20_000),
                };

                var progress = new System.Text.StringBuilder();
                using var session = new HeadlessSession(options, server.BaseUrl, apiKey: null!,
                    workingDirectory: dir, buildCommand: "dotnet build",
                    promptFilePath: Path.Combine(dir, "no-system-prompt.md"));
                var result = await session.RunTurnAsync("Say done.",
                    progress: chunk => progress.Append(chunk), ct: CancellationToken.None);

                Assert.Null(result.Error);
                string emitted = progress.ToString();

                Assert.Contains("[PROMPT] System prompt", emitted);
                Assert.Contains("of 32,768", emitted);      // the raw window — correct
                Assert.DoesNotContain("of 27,853", emitted); // window minus headroom — the bug
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        // EffectiveContextWindow must never be 0 under normal construction, or the warning
        // and /prompt both silently skip the size check.
        [Fact]
        public void EffectiveContextWindow_IsPositiveBeforeAnyDetection()
        {
            Assert.True(new LlmClient(new NoManualSizeOptions()).EffectiveContextWindow > 0);
        }
    }
}
