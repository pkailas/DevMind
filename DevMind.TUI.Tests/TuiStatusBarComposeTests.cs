// File: TuiStatusBarComposeTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The one line in the app that is allowed to change ten times a second.
//
// Everything the transcript stopped showing — elapsed time, round, tokens in and out — is now
// only here, so the composition stopped being a detail of the ticker and became the contract.
// Composed inline in Tick() it was verifiable only by starting a model and watching the
// bottom row; a dropped field or a mangled format would have looked like nothing at all.
//
// The Esc affordance is pinned for the same reason it exists: the transcript no longer prints
// anything during a long round, so the footer is the only place that says the round can be
// stopped.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class TuiStatusBarComposeTests
    {
        [Fact]
        public void AFullThinkingSet_RendersEveryField()
        {
            string line = TuiStatusBar.Compose(new StatusFields(
                frame: "⠹", state: StatusState.Thinking, elapsed: "00:02.4",
                round: 1, maxRounds: 5, inTokens: 133192, outTokens: 312, cancellable: true));

            Assert.Equal("⠹ Thinking... (00:02.4, round 1/5, 133,192 in / 312 out · Esc cancels)", line);
        }

        [Fact]
        public void TheGeneratingPhase_DiffersOnlyInItsLabel()
        {
            string line = TuiStatusBar.Compose(new StatusFields(
                frame: "⠸", state: StatusState.Busy, elapsed: "00:05.1",
                round: 3, maxRounds: 40, inTokens: 0, outTokens: 1024, cancellable: true));

            Assert.Equal("⠸ Generating... (00:05.1, round 3/40, 0 in / 1,024 out · Esc cancels)", line);
        }

        [Fact]
        public void AnIdleSet_IsJustReady()
        {
            string line = TuiStatusBar.Compose(new StatusFields(
                frame: "", state: StatusState.Ready, elapsed: "",
                round: 0, maxRounds: 0, inTokens: 0, outTokens: 0, cancellable: false));

            Assert.Equal("○ Ready", line);
        }

        [Fact]
        public void BeforeTheFirstToken_TheCountsAreOmittedRatherThanShownAsZero()
        {
            string line = TuiStatusBar.Compose(new StatusFields(
                frame: "⠋", state: StatusState.Thinking, elapsed: "00:00.3",
                round: 1, maxRounds: 5, inTokens: 0, outTokens: 0, cancellable: true));

            Assert.Equal("⠋ Thinking... (00:00.3, round 1/5 · Esc cancels)", line);
        }

        [Fact]
        public void WithNoDepthCap_TheRoundStandsAlone()
        {
            string line = TuiStatusBar.Compose(new StatusFields(
                frame: "⠏", state: StatusState.Thinking, elapsed: "00:01.0",
                round: 2, maxRounds: 0, inTokens: 10, outTokens: 0, cancellable: true));

            Assert.Contains("round 2,", line);
            Assert.DoesNotContain("round 2/", line);
        }

        [Fact]
        public void WhenNothingCanBeCancelled_TheAffordanceIsNotOffered()
        {
            string line = TuiStatusBar.Compose(new StatusFields(
                frame: "⠹", state: StatusState.Thinking, elapsed: "00:02.4",
                round: 1, maxRounds: 5, inTokens: 0, outTokens: 0, cancellable: false));

            Assert.DoesNotContain("Esc", line);
            Assert.Equal("⠹ Thinking... (00:02.4, round 1/5)", line);
        }

        [Fact]
        public void AnErrorStateSaysSo()
        {
            string line = TuiStatusBar.Compose(new StatusFields(
                frame: "", state: StatusState.Error, elapsed: "00:09.9",
                round: 0, maxRounds: 0, inTokens: 0, outTokens: 0, cancellable: false));

            Assert.Equal("Error (00:09.9)", line);
        }
    }
}
