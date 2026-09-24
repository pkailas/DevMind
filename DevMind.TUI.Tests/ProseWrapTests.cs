// File: ProseWrapTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Keeping a list's shape when it is too long for the window.
//
// The Editor wraps to column zero. For a paragraph that is untidy; for a list it is
// destructive — the second line of a bullet starts level with the marker, so three items
// become six unrelated lines and there is no way to tell continuation from new item. That is
// the one case where the wrap has to happen before the Editor sees the text.
//
// So the assertions are about indents as much as about break points, and the list cases are
// the ones that matter: a continuation has to sit under the item's TEXT, which means the
// marker's width is part of the indent and "- " and "12. " do not indent the same.

using System.Linq;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class ProseWrapTests
    {
        private const string Indent = "  ";   // TuiAgenticHost.ProseHangingIndent

        private static string TextOf(ProseLine line)
            => string.Concat(line.Runs.Select(r => r.Text));

        private static int WidthOf(ProseLine line)
            => line.Indent.Length + TextOf(line).Length;

        [Fact]
        public void AShortLineIsOneLineWithNoAddedIndent()
        {
            // The caller has already drawn the ◆ or the hanging indent for line one.
            var plan = ProseWrap.Plan("Short enough.", Indent, 80);

            Assert.Single(plan);
            Assert.Equal("", plan[0].Indent);
            Assert.Equal("Short enough.", TextOf(plan[0]));
        }

        [Fact]
        public void ALongSentenceBreaksAndTheSecondLineCarriesTheBlockIndent()
        {
            string sentence = string.Join(" ", Enumerable.Repeat("word", 20));   // 99 chars

            var plan = ProseWrap.Plan(sentence, Indent, 40);

            Assert.True(plan.Count >= 2);
            Assert.Equal("", plan[0].Indent);
            Assert.All(plan.Skip(1), l => Assert.Equal(Indent, l.Indent));
            Assert.All(plan, l => Assert.True(WidthOf(l) <= 40, $"{WidthOf(l)} > 40"));
        }

        [Fact]
        public void ABulletsContinuationSitsUnderItsText_NotUnderItsMarker()
        {
            // The whole point. "- " is two columns, so a continuation indents by the block
            // indent plus two, and the wrapped text lines up with "A manual loop…".
            string item = "- " + string.Join(" ", Enumerable.Repeat("word", 20));

            var plan = ProseWrap.Plan(item, Indent, 40);

            Assert.True(plan.Count >= 2);
            Assert.Equal(Indent + "  ", plan[1].Indent);
        }

        [Fact]
        public void AnOrderedItemIndentsByItsOwnMarkerWidth()
        {
            string item = "12. " + string.Join(" ", Enumerable.Repeat("word", 20));

            var plan = ProseWrap.Plan(item, Indent, 40);

            Assert.True(plan.Count >= 2);
            Assert.Equal(Indent + "    ", plan[1].Indent);   // "12. " is four columns
        }

        [Fact]
        public void ANestedItemKeepsItsOwnLeadingSpaces()
        {
            string item = "  - " + string.Join(" ", Enumerable.Repeat("word", 20));

            var plan = ProseWrap.Plan(item, Indent, 40);

            Assert.True(plan.Count >= 2);
            Assert.Equal(Indent + "    ", plan[1].Indent);   // two of nesting plus "- "
        }

        [Theory]
        [InlineData("- item", 2)]
        [InlineData("* item", 2)]
        [InlineData("+ item", 2)]
        [InlineData("1. item", 3)]
        [InlineData("12. item", 4)]
        [InlineData("1) item", 3)]
        [InlineData("  - item", 4)]
        [InlineData("Just prose.", 0)]
        [InlineData("", 0)]
        public void TheMarkerWidthIsWhatTheMarkerOccupies(string line, int expected)
        {
            Assert.Equal(expected, ProseWrap.MarkerWidth(line));
        }

        [Fact]
        public void AHeadingIsNeverWrapped()
        {
            // A heading is short by nature and its shape is the message; breaking it
            // mid-phrase to satisfy a narrow window loses more than it saves.
            string heading = "## " + string.Join(" ", Enumerable.Repeat("word", 20));

            var plan = ProseWrap.Plan(heading, Indent, 20);

            Assert.Single(plan);
        }

        [Fact]
        public void AnUnbreakableTokenIsHardBrokenRatherThanOverflowing()
        {
            // A 60-character path or identifier has no space to break at. Letting it run
            // would hand the line back to the Editor, which is what this exists to avoid.
            string token = new string('x', 60);

            var plan = ProseWrap.Plan(token, Indent, 30);

            Assert.True(plan.Count >= 2);
            Assert.All(plan, l => Assert.True(WidthOf(l) <= 30, $"{WidthOf(l)} > 30"));
            Assert.Equal(token, string.Concat(plan.Select(TextOf)));
        }

        [Fact]
        public void NoTextIsLostToTheWrap()
        {
            string sentence = string.Join(" ", Enumerable.Repeat("alpha beta", 15));

            var plan = ProseWrap.Plan(sentence, Indent, 35);

            // Words survive; only the spaces at break points are consumed.
            string rejoined = string.Join(" ", plan.Select(TextOf));
            Assert.Equal(sentence.Replace("  ", " "), rejoined);
        }

        [Fact]
        public void InlineStylesSurviveTheBreak()
        {
            string line = "The `--resume` flag " + string.Join(" ", Enumerable.Repeat("word", 20));

            var plan = ProseWrap.Plan(line, Indent, 40);

            Assert.Contains(plan.SelectMany(l => l.Runs),
                r => r.Style == InlineTextStyle.InlineCode && r.Text.Contains("--resume"));
        }

        [Fact]
        public void AnUnknownWidthFallsBackInsteadOfCollapsing()
        {
            string sentence = string.Join(" ", Enumerable.Repeat("word", 40));

            var plan = ProseWrap.Plan(sentence, Indent, 0);

            Assert.All(plan, l => Assert.True(WidthOf(l) <= ProseWrap.FallbackWidth));
        }

        [Fact]
        public void AnAbsurdWidthStillProducesAReadableColumn()
        {
            // Below the floor the Editor will wrap anyway; shredding prose to three columns
            // first would be a worse answer than letting it.
            var plan = ProseWrap.Plan(string.Join(" ", Enumerable.Repeat("word", 20)), Indent, 4);

            Assert.All(plan, l => Assert.True(TextOf(l).Length <= ProseWrap.MinTextWidth));
        }
    }
}
