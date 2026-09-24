// File: ProseBlockMarkerTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// One marker per line.
//
// The ◆ lead marks where the model started talking. When its first sentence is already a
// list item the line gets two markers — "◆ - Use sum()" — which reads as a nested bullet the
// model did not write, and is the sort of thing that looks like a rendering bug rather than
// a rule nobody thought through.
//
// The decision is "does this line already carry its own marker", which is a question about
// text, so it lives where the other line-shape questions live and is tested here. Whether
// the host then draws a diamond or an indent is one branch at the call site.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class ProseBlockMarkerTests
    {
        [Theory]
        [InlineData("- Use sum()")]
        [InlineData("* Use sum()")]
        [InlineData("+ Use sum()")]
        [InlineData("  - indented under something")]
        [InlineData("- ")]   // an empty item is still the model's own marker
        [InlineData("1. First")]
        [InlineData("10. Tenth")]
        [InlineData("1) First")]
        public void ALineThatAlreadyCarriesAMarkerIsRecognised(string line)
        {
            Assert.True(MarkdownInlineRenderer.IsListItem(line));
        }

        [Theory]
        [InlineData("Use sum() instead.")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("-no space after the dash")]
        [InlineData("*emphasis* at the start")]
        [InlineData("1.no space")]
        [InlineData("2026. was the year")]      // four digits — a sentence, not an item
        public void OrdinaryProseIsNot(string? line)
        {
            Assert.False(MarkdownInlineRenderer.IsListItem(line));
        }

        [Fact]
        public void ABareNumberAndDotIsNotAList()
        {
            // "1." at the end of a line is a fragment; a list item has content after it.
            Assert.False(MarkdownInlineRenderer.IsListItem("1."));
            Assert.False(MarkdownInlineRenderer.IsListItem("1.\n"));
        }

        [Fact]
        public void TheDiamondAndTheIndentAreDistinct()
        {
            // The substitution only helps if what replaces the diamond occupies the same
            // gutter — a list item that starts in column zero would not line up with the
            // prose blocks around it.
            Assert.Equal(TuiAgenticHost.ProseLead.Length, TuiAgenticHost.ProseHangingIndent.Length);
            Assert.NotEqual(TuiAgenticHost.ProseLead, TuiAgenticHost.ProseHangingIndent);
        }
    }
}
