// File: DiffEmptyLineTintTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// An added blank line is still an added line.
//
// A blank line inside a docstring, added by an edit, has no text to tint — so what the
// reader sees is the gutter and the marker and then nothing. Qwen Code renders that case as
// a short green block; the report from the first live run was that DevMind's came out black.
//
// This pins the part that is answerable here: every span the painter emits for such a line
// carries the added tint, including the newline. If the row still reads as black on screen
// after that, the cause is below the painter — the region past end-of-text, which is the
// ragged right edge that brief 16 §F explicitly leaves alone — and not a colour this code
// chose.

using System.Linq;
using Xunit;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;

namespace DevMind.TUI.Tests
{
    public class DiffEmptyLineTintTests
    {
        private static readonly TgColor ContextBg = new TgColor(0x00, 0x00, 0x00);
        private static readonly TgColor RemovedBg = new TgColor(0x4B, 0x1F, 0x1F);
        private static readonly TgColor AddedBg   = new TgColor(0x1F, 0x3A, 0x1F);

        private static DiffPalette Palette() => new DiffPalette
        {
            Foreground = _ => new TgColor(0xD4, 0xD4, 0xD4),
            Gutter     = new TgColor(0x88, 0x88, 0x88),
            ContextBg  = ContextBg,
            RemovedBg  = RemovedBg,
            AddedBg    = AddedBg,
        };

        private static List<(string Text, TgAttribute Attr)> Paint(params DiffLine[] lines)
        {
            var spans = new List<(string, TgAttribute)>();
            DiffPainter.Paint(lines, "Thing.cs", Palette(), (t, a) => spans.Add((t, a)));
            return spans;
        }

        [Fact]
        public void AnAddedEmptyLineIsEntirelyInTheAddedTint()
        {
            var spans = Paint(
                new DiffLine(DiffLineKind.HunkHeader, 6, 6, "@@"),
                new DiffLine(DiffLineKind.Added, null, 7, ""));

            // The hunk ellipsis is context; everything after it belongs to the added row.
            var row = spans.Skip(1).ToList();

            Assert.NotEmpty(row);
            Assert.All(row, s => Assert.Equal(AddedBg, s.Attr.Background));
        }

        [Fact]
        public void TheNewlineCarriesTheTintToo()
        {
            // Explicit because it is the span most easily left on the default background,
            // and it is the one that covers the break between rows.
            var spans = Paint(
                new DiffLine(DiffLineKind.HunkHeader, 6, 6, "@@"),
                new DiffLine(DiffLineKind.Added, null, 7, ""));

            var newline = spans.Last();
            Assert.Equal("\n", newline.Text);
            Assert.Equal(AddedBg, newline.Attr.Background);
        }

        [Fact]
        public void TheGutterAndMarkerAreStillDrawnForAnEmptyLine()
        {
            // Without them the row is invisible and the numbering skips a line.
            var spans = Paint(
                new DiffLine(DiffLineKind.HunkHeader, 6, 6, "@@"),
                new DiffLine(DiffLineKind.Added, null, 7, ""));

            Assert.Contains(spans, s => s.Text.Contains("7") && s.Text.Contains("+"));
        }

        [Fact]
        public void ARemovedEmptyLineIsEntirelyInTheRemovedTint()
        {
            var spans = Paint(
                new DiffLine(DiffLineKind.HunkHeader, 6, 6, "@@"),
                new DiffLine(DiffLineKind.Removed, 7, null, ""));

            Assert.All(spans.Skip(1), s => Assert.Equal(RemovedBg, s.Attr.Background));
        }

        [Fact]
        public void NoSpanOfAnAddedLineEverCarriesTheContextBackground()
        {
            var spans = Paint(
                new DiffLine(DiffLineKind.HunkHeader, 6, 6, "@@"),
                new DiffLine(DiffLineKind.Added, null, 7, ""),
                new DiffLine(DiffLineKind.Added, null, 8, "    return total"));

            Assert.DoesNotContain(spans.Skip(1), s => s.Attr.Background == ContextBg);
        }
    }
}
