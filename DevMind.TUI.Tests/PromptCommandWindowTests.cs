// File: PromptCommandWindowTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Pins the context-window denominator the /prompt command reports against.
//
// The drift: Program.cs wired CommandContext.ContextWindowSize to
// llmClient.MaxPromptTokens, while the system-prompt size warning in LlmClient divided by
// the RAW window. Those agree once the server reports n_ctx, but before detection
// completes MaxPromptTokens is HistoryHardLimit — the window MINUS the 15% response-
// headroom reservation — so /prompt and the warning printed different percentages for the
// same prompt text, against the same "5% of the loaded context window" guidance.
//
// Program.cs now assigns llmClient.EffectiveContextWindow, the same accessor the warning
// uses. These tests exercise the handler through the real dispatcher with both candidate
// denominators, pinning the arithmetic that distinguishes them rather than trusting it.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class PromptCommandWindowTests
    {
        // 13,372 is the pre-detection fallback window; 11,367 is what MaxPromptTokens
        // returns in that same state (13,372 - (int)(13,372 * 0.15)). A prompt is sized to
        // sit just under the 5% guideline against the true window, so the two denominators
        // land on opposite sides of it — the case where the disagreement actually misleads.
        private const int RawWindow = 13_372;
        private const int HistoryCeiling = 11_367;

        // 2,800 chars => (2800 / 4) + 4 = 704 tokens, matching LlmClient.EstimateTokens.
        private static string PromptOfTokens() => new string('p', 2_800);

        private static async Task<string> RunPromptAsync(int windowSize)
        {
            var ctx = new CommandContext
            {
                SystemPrompt = PromptOfTokens(),
                ContextWindowSize = windowSize,
            };
            var result = await SlashCommand.Dispatch("/prompt", ctx);
            Assert.False(result.IsError, result.Message);
            return result.Message;
        }

        // AFTER the fix: /prompt divides by the raw window and reports 5.3%, matching what
        // the size warning reports for the same text.
        [Fact]
        public async Task RawWindow_ReportsPercentageUnderTheGuideline()
        {
            string message = await RunPromptAsync(RawWindow);

            Assert.Contains("704 tokens", message);
            Assert.Contains("5.3%", message);
            Assert.Contains("13,372 window", message);
        }

        // BEFORE the fix: the same prompt against MaxPromptTokens reported 6.2% — a
        // materially different figure for identical text, and on the wrong side of the
        // 5% guideline the same output advertises.
        [Fact]
        public async Task HistoryCeiling_InflatesThePercentage()
        {
            string message = await RunPromptAsync(HistoryCeiling);

            Assert.Contains("704 tokens", message); // same prompt, same token count
            Assert.Contains("6.2%", message);       // different percentage — the bug
        }

        // The two denominators differ by more than presentation: they disagree about
        // whether this prompt is within the budget /prompt itself prints.
        [Fact]
        public async Task TheTwoDenominators_DisagreeAcrossTheFivePercentGuideline()
        {
            string correct = await RunPromptAsync(RawWindow);
            string inflated = await RunPromptAsync(HistoryCeiling);

            Assert.Contains("Budget: 5% of the loaded context window", correct);
            Assert.NotEqual(correct, inflated);
            Assert.Contains("5.3%", correct);  // under budget
            Assert.Contains("6.2%", inflated); // over budget, for the same text
        }

        // The zero case must degrade to a statement rather than dividing by zero.
        [Fact]
        public async Task UndeterminedWindow_SaysSoInsteadOfReportingAPercentage()
        {
            string message = await RunPromptAsync(0);

            Assert.Contains("window not yet determined", message);
            Assert.DoesNotContain("%", message.Split("Guidance for authoring")[0]);
        }
    }
}
