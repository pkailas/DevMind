// File: SteerFoldTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The TUI's per-iteration drain. Steer.IsLastIteration and Steer.Apply are already pinned by
// SteerDecisionTests and are not re-tested here; what these cover is the WIRING, which is
// where a TUI-specific mistake would actually live: passing the depth cap and the current
// depth in the wrong order, reporting a rejection as a fold, or losing the prompt when a
// steer is refused.
//
// That wiring used to sit inline in RunTurnAsync, unreachable without Terminal.Gui and so
// verifiable only by running the app and typing. It is the reason SteerFold exists.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class SteerFoldTests
    {
        private const int MaxDepth = 25;

        [Fact]
        public void ASuggestion_IsFoldedIntoThePrompt_AndReported()
        {
            var fold = SteerFold.Fold("do the work", "also check the CLI", SteerMode.Suggest,
                MaxDepth, agenticDepth: 3);

            Assert.Equal(SteerDisposition.Consumed, fold.Disposition);
            Assert.Contains("do the work", fold.Prompt, StringComparison.Ordinal);
            Assert.Contains("also check the CLI", fold.Prompt, StringComparison.Ordinal);
            Assert.Contains("[STEER] suggest folded into the prompt", fold.TranscriptLine, StringComparison.Ordinal);
            Assert.Equal(OutputColor.Success, fold.Color);
        }

        [Fact]
        public void AnOverride_OffTheLastIteration_IsFolded()
        {
            var fold = SteerFold.Fold("do the work", "stop, fix the build", SteerMode.Override,
                MaxDepth, agenticDepth: 3);

            Assert.Equal(SteerDisposition.Consumed, fold.Disposition);
            Assert.Contains("stop, fix the build", fold.Prompt, StringComparison.Ordinal);
            Assert.Contains("[STEER] override folded into the prompt", fold.TranscriptLine, StringComparison.Ordinal);
        }

        // The wiring test that matters. An override with no iterations left cannot be acted
        // on, so it is refused and the prompt is left EXACTLY as it was — folding it in and
        // then stopping would send a redirection the model has no chance to follow.
        [Fact]
        public void AnOverride_OnTheLastIteration_IsRejected_AndLeavesThePromptAlone()
        {
            var fold = SteerFold.Fold("do the work", "change course", SteerMode.Override,
                MaxDepth, agenticDepth: MaxDepth);

            Assert.Equal(SteerDisposition.Rejected, fold.Disposition);
            Assert.Equal("do the work", fold.Prompt);
            Assert.Contains("REJECTED", fold.TranscriptLine, StringComparison.Ordinal);
            Assert.Contains("no iterations left", fold.TranscriptLine, StringComparison.Ordinal);
            Assert.Equal(OutputColor.Warning, fold.Color);
        }

        // A suggestion is always foldable — it adds to the current approach rather than
        // redirecting it, so having no iterations left is not a reason to refuse it.
        [Fact]
        public void ASuggestion_OnTheLastIteration_IsStillFolded()
        {
            var fold = SteerFold.Fold("do the work", "mention the caveat", SteerMode.Suggest,
                MaxDepth, agenticDepth: MaxDepth);

            Assert.Equal(SteerDisposition.Consumed, fold.Disposition);
            Assert.Contains("mention the caveat", fold.Prompt, StringComparison.Ordinal);
        }

        // Argument order, pinned directly: the cap comes first, the current depth second.
        // Swapping them silently turns "last iteration" into "first iteration" and every
        // override gets rejected — with a message that reads plausible.
        [Fact]
        public void TheDepthArgumentsAreNotInterchangeable()
        {
            var early = SteerFold.Fold("p", "m", SteerMode.Override, maxDepth: MaxDepth, agenticDepth: 0);
            var late  = SteerFold.Fold("p", "m", SteerMode.Override, maxDepth: MaxDepth, agenticDepth: MaxDepth);

            Assert.Equal(SteerDisposition.Consumed, early.Disposition);
            Assert.Equal(SteerDisposition.Rejected, late.Disposition);
        }

        // An uncapped session has no last iteration, so an override is never refused for
        // having run out of room.
        [Fact]
        public void WithNoDepthCap_AnOverrideIsNeverRejected()
        {
            var fold = SteerFold.Fold("p", "change course", SteerMode.Override,
                maxDepth: 0, agenticDepth: 9999);

            Assert.Equal(SteerDisposition.Consumed, fold.Disposition);
        }

        // The line always carries the user's own words back, so the transcript shows what
        // was queued and not merely that something was.
        [Theory]
        [InlineData(SteerMode.Suggest)]
        [InlineData(SteerMode.Override)]
        public void TheTranscriptLineQuotesTheMessage(SteerMode mode)
        {
            var fold = SteerFold.Fold("p", "THE-STEER-TEXT", mode, MaxDepth, agenticDepth: 1);

            Assert.Contains("THE-STEER-TEXT", fold.TranscriptLine, StringComparison.Ordinal);
            Assert.EndsWith("\n", fold.TranscriptLine, StringComparison.Ordinal);
        }
    }
}
