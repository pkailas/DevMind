// File: ThoughtCollapseTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The one dim line that stands in for a response's hidden reasoning.
//
// The line is a promise: it says reasoning happened, roughly how much, and that /expand can
// produce it. The promise is only kept if the parked text is the whole thought — which is
// why the accumulation is pinned here alongside the format, and why the "[THINKING] " tag
// ThinkFilter adds for the inline path is stripped rather than counted as part of the model's
// reasoning.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class ThoughtCollapseTests
    {
        [Fact]
        public void TheSummaryLine_SaysHowLongAndRoughlyHowMuch()
        {
            string line = ThoughtCollapse.Summary(TimeSpan.FromSeconds(5), charCount: 4960);

            Assert.Equal("∴ Thought for 5s (~1,240 tokens) — ctrl+o or /expand to show", line);
        }

        [Fact]
        public void TheLiveLine_TicksInWholeSeconds()
        {
            Assert.Equal("∵ Thinking… 11s", ThoughtCollapse.Live(TimeSpan.FromSeconds(11)));
            Assert.Equal("∵ Thinking… 11s", ThoughtCollapse.Live(TimeSpan.FromSeconds(11.4)));
        }

        [Fact]
        public void TheLiveLineAndTheSummary_AreDistinguishableAtAGlance()
        {
            // Different glyph, different tense: the reader must be able to tell a line that
            // is still ticking from one that has finished without reading the number.
            Assert.StartsWith("∵ Thinking", ThoughtCollapse.Live(TimeSpan.FromSeconds(3)));
            Assert.StartsWith("∴ Thought", ThoughtCollapse.Summary(TimeSpan.FromSeconds(3), 100));
        }

        [Fact]
        public void TheSummaryNamesBothWaysToSeeTheText()
        {
            string line = ThoughtCollapse.Summary(TimeSpan.FromSeconds(3), 100);
            Assert.Contains("ctrl+o", line);
            Assert.Contains("/expand", line);
        }

        [Fact]
        public void TheTokenCountIsMarkedAsAnEstimate()
        {
            // The tilde is not decoration. The real split between reasoning and visible
            // output is not reported per response, so a bare number here would read as
            // server truth when it is chars/4.
            Assert.Contains("~", ThoughtCollapse.Summary(TimeSpan.FromSeconds(1), 400));
        }

        [Fact]
        public void AVeryFastThought_StillReportsATime()
        {
            // 0s would read as "did not happen"; the floor keeps the line honest.
            Assert.Contains("1s", ThoughtCollapse.Summary(TimeSpan.FromMilliseconds(12), 40));
            Assert.Contains("1s", ThoughtCollapse.Live(TimeSpan.Zero));
        }

        [Fact]
        public void WithThinkingDisplayOn_NothingIsCollapsedAndNoSummaryIsEmitted()
        {
            // The summary line stands in for text the reader cannot see. With thinking on the
            // text is right there above it, so emitting one would be a second, worse copy of
            // something already on screen — and would claim /expand has a thought it never
            // parked.
            var collapse = new ThoughtCollapse(showThinking: true);

            Assert.Equal("reasoning…", collapse.Route("reasoning…"));
            Assert.False(collapse.HasThought);
            Assert.Null(collapse.Summarize(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void WithThinkingDisplayOff_TheChunkIsCollapsedRatherThanShown()
        {
            var collapse = new ThoughtCollapse(showThinking: false);

            Assert.Null(collapse.Route("reasoning…"));
            Assert.True(collapse.HasThought);
            Assert.Equal("reasoning…", collapse.Text);
            Assert.NotNull(collapse.Summarize(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void AResponseThatNeverReasoned_GetsNoSummaryEither()
        {
            var collapse = new ThoughtCollapse(showThinking: false);

            Assert.Null(collapse.Summarize(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void TheParkedTextIsTheConcatenatedThinkText()
        {
            var collapse = new ThoughtCollapse(showThinking: false);
            Assert.False(collapse.HasThought);

            collapse.Append("[THINKING] The user wants ");
            collapse.Append("the transcript quiet, ");
            collapse.Append("so the filter moves.");

            Assert.True(collapse.HasThought);
            Assert.Equal("The user wants the transcript quiet, so the filter moves.", collapse.Text);
        }

        [Fact]
        public void TheDisplayTagIsStrippedOnlyFromTheFirstChunk()
        {
            var collapse = new ThoughtCollapse(showThinking: false);
            collapse.Append("[THINKING] first");
            collapse.Append(" then [THINKING] literal");

            Assert.Equal("first then [THINKING] literal", collapse.Text);
        }

        [Fact]
        public void TheSummaryCountsTheStrippedText_NotTheTag()
        {
            var tagged = new ThoughtCollapse(showThinking: false);
            tagged.Append("[THINKING] " + new string('x', 400));

            var bare = new ThoughtCollapse(showThinking: false);
            bare.Append(new string('x', 400));

            Assert.Equal(bare.Summarize(TimeSpan.FromSeconds(2)), tagged.Summarize(TimeSpan.FromSeconds(2)));
            Assert.Contains("~100 tokens", tagged.Summarize(TimeSpan.FromSeconds(2)));
        }

        [Fact]
        public void ResetClearsTheResponse()
        {
            var collapse = new ThoughtCollapse(showThinking: false);
            collapse.Append("one response");
            collapse.Reset();

            Assert.False(collapse.HasThought);
            Assert.Equal(string.Empty, collapse.Text);
        }

        [Fact]
        public void EmptyChunksAreIgnored()
        {
            var collapse = new ThoughtCollapse(showThinking: false);
            collapse.Append(null);
            collapse.Append("");

            Assert.False(collapse.HasThought);
        }
    }
}
