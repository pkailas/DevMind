// File: MarkdownInlineRendererTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Direct unit tests for DevMind.TUI/MarkdownInlineRenderer.cs — the pure inline
// markdown renderer (headings, **bold**, `inline code`). The renderer is the piece
// that decides which markers are consumed and which stay literal, so the literal
// cases (lone '*', unmatched '**', lone backtick) are pinned as hard expectations:
// a line with no resolvable markers must come back byte-identical in one Normal
// run. Inline code wins over bold (backticked **x** is literal code), and headings
// have their remaining spans parsed (a heading may contain bold/code).

using DevMind;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class MarkdownInlineRendererTests
    {
        // ── Headings ─────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("# H1", "H1")]
        [InlineData("## H2", "H2")]
        [InlineData("### H3", "H3")]
        [InlineData("###### H6", "H6")]
        public void Heading_OneToThreeHashes_IsOneHeadingRunWithMarkersConsumed(string line, string text)
        {
            var runs = MarkdownInlineRenderer.Render(line);

            Assert.Single(runs);
            Assert.Equal(text, runs[0].Text);
            Assert.Equal(InlineTextStyle.Heading, runs[0].Style);
        }

        // '#' not followed by a space is NOT a heading — markers stay literal.
        [Fact]
        public void HashWithoutFollowingSpace_IsNotAHeading()
        {
            var runs = MarkdownInlineRenderer.Render("#not a heading");

            Assert.Single(runs);
            Assert.Equal("#not a heading", runs[0].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[0].Style);
        }

        // Seven hashes exceeds the 1-6 range — not a heading.
        [Fact]
        public void SevenHashes_IsNotAHeading()
        {
            var runs = MarkdownInlineRenderer.Render("####### Seven");

            Assert.Single(runs);
            Assert.Equal("####### Seven", runs[0].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[0].Style);
        }

        // Inline spans inside a heading are parsed: the non-span remainder is a
        // Heading run, the span keeps its own style.
        [Fact]
        public void Heading_WithInlineSpans_ParsesThem()
        {
            var runs = MarkdownInlineRenderer.Render("## Title with **bold** part");

            Assert.Equal(3, runs.Count);
            Assert.Equal("Title with ", runs[0].Text);
            Assert.Equal(InlineTextStyle.Heading, runs[0].Style);
            Assert.Equal("bold", runs[1].Text);
            Assert.Equal(InlineTextStyle.Bold, runs[1].Style);
            Assert.Equal(" part", runs[2].Text);
            Assert.Equal(InlineTextStyle.Heading, runs[2].Style);
        }

        // ── Bold ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Bold_MidSentence_MarkersConsumed()
        {
            var runs = MarkdownInlineRenderer.Render("use the **bold** word here");

            Assert.Equal(3, runs.Count);
            Assert.Equal("use the ", runs[0].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[0].Style);
            Assert.Equal("bold", runs[1].Text);
            Assert.Equal(InlineTextStyle.Bold, runs[1].Style);
            Assert.Equal(" word here", runs[2].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[2].Style);
        }

        [Fact]
        public void Bold_TwoSpansInOneLine_BothStyled()
        {
            var runs = MarkdownInlineRenderer.Render("**one** and **two**");

            Assert.Equal(3, runs.Count);
            Assert.Equal(InlineTextStyle.Bold, runs[0].Style);
            Assert.Equal("one", runs[0].Text);
            Assert.Equal(" and ", runs[1].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[1].Style);
            Assert.Equal(InlineTextStyle.Bold, runs[2].Style);
            Assert.Equal("two", runs[2].Text);
        }

        // A lone '*' prints exactly as it arrived.
        [Fact]
        public void LoneStar_StaysLiteral()
        {
            var runs = MarkdownInlineRenderer.Render("5 * 3 = 15");

            Assert.Single(runs);
            Assert.Equal("5 * 3 = 15", runs[0].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[0].Style);
        }

        // A '**' with no closing pair stays literal — the whole line is Normal.
        [Fact]
        public void UnmatchedDoubleStar_StaysLiteral()
        {
            var runs = MarkdownInlineRenderer.Render("trailing ** and more");

            Assert.Single(runs);
            Assert.Equal("trailing ** and more", runs[0].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[0].Style);
        }

        // ── Inline code ──────────────────────────────────────────────────────────

        [Fact]
        public void InlineCode_MidSentence_MarkersConsumed()
        {
            var runs = MarkdownInlineRenderer.Render("run `dotnet build` first");

            Assert.Equal(3, runs.Count);
            Assert.Equal("run ", runs[0].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[0].Style);
            Assert.Equal("dotnet build", runs[1].Text);
            Assert.Equal(InlineTextStyle.InlineCode, runs[1].Style);
            Assert.Equal(" first", runs[2].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[2].Style);
        }

        // A lone backtick stays literal.
        [Fact]
        public void LoneBacktick_StaysLiteral()
        {
            var runs = MarkdownInlineRenderer.Render("a ` single backtick");

            Assert.Single(runs);
            Assert.Equal("a ` single backtick", runs[0].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[0].Style);
        }

        // Backticked content is LITERAL: `**x**` is five characters styled as code,
        // never bold.
        [Fact]
        public void BacktickedDoubleStars_AreCodeNotBold()
        {
            var runs = MarkdownInlineRenderer.Render("see `**x**` here");

            Assert.Equal(3, runs.Count);
            Assert.Equal("see ", runs[0].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[0].Style);
            Assert.Equal("**x**", runs[1].Text);
            Assert.Equal(InlineTextStyle.InlineCode, runs[1].Style);
            Assert.Equal(" here", runs[2].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[2].Style);
        }

        // ── Plain lines ──────────────────────────────────────────────────────────

        [Fact]
        public void LineWithNoMarkers_IsExactlyOneUnchangedNormalRun()
        {
            var runs = MarkdownInlineRenderer.Render("just a plain line of prose");

            Assert.Single(runs);
            Assert.Equal("just a plain line of prose", runs[0].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[0].Style);
        }

        // Backslash escaping is deliberately out of scope — the backslash prints
        // literally, so this stays one unchanged Normal run.
        [Fact]
        public void BackslashBeforeMarker_LeavesBothLiteral()
        {
            var runs = MarkdownInlineRenderer.Render("C:\\path\\to\\file and \\*not bold\\*");

            Assert.Single(runs);
            Assert.Equal("C:\\path\\to\\file and \\*not bold\\*", runs[0].Text);
            Assert.Equal(InlineTextStyle.Normal, runs[0].Style);
        }

        [Fact]
        public void EmptyLine_IsZeroRuns()
        {
            Assert.Empty(MarkdownInlineRenderer.Render(string.Empty));
        }
    }
}
