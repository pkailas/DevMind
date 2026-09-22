// File: DiffPainterTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// What a painted diff actually puts on the screen.
//
// Every one of these was previously unaskable. The colouring lived inside a method that
// needed a running Terminal.Gui view to call, so "is a removed line tinted red" and "does
// the gutter line up" were questions you answered by starting the app, making an edit and
// looking — which is why the old renderer could flatten a changed line to one colour for as
// long as it did without anyone calling it a defect.
//
// The painter takes a span sink, so the answers are assertions: which text, which
// foreground, which background, in order.

using System.Collections.Generic;
using System.Linq;
using Xunit;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;

namespace DevMind.TUI.Tests
{
    public class DiffPainterTests
    {
        // Deliberately garish and unlike each other: a test that fails must say WHICH colour
        // arrived, and two near-identical tints would not.
        private static readonly TgColor ContextBg = new TgColor(0x00, 0x00, 0x00);
        private static readonly TgColor RemovedBg = new TgColor(0x4B, 0x1F, 0x1F);
        private static readonly TgColor AddedBg   = new TgColor(0x1F, 0x3A, 0x1F);
        private static readonly TgColor Gutter    = new TgColor(0x88, 0x88, 0x88);

        private static readonly TgColor FgPlain   = new TgColor(0xD4, 0xD4, 0xD4);
        private static readonly TgColor FgKeyword = new TgColor(0x56, 0x9C, 0xD6);
        private static readonly TgColor FgString  = new TgColor(0xCE, 0x91, 0x78);
        private static readonly TgColor FgComment = new TgColor(0x6A, 0x99, 0x55);

        private static TgColor Foreground(TokenKind kind)
        {
            switch (kind)
            {
                case TokenKind.Keyword:   return FgKeyword;
                case TokenKind.StringLit: return FgString;
                case TokenKind.Comment:   return FgComment;
                default:                  return FgPlain;
            }
        }

        private static DiffPalette Palette() => new DiffPalette
        {
            Foreground = Foreground,
            Gutter     = Gutter,
            ContextBg  = ContextBg,
            RemovedBg  = RemovedBg,
            AddedBg    = AddedBg,
        };

        private sealed class Sink
        {
            public readonly List<(string Text, TgAttribute Attr)> Spans = new List<(string, TgAttribute)>();
            public void Write(string text, TgAttribute attr) => Spans.Add((text, attr));

            /// <summary>The spans regrouped into the transcript lines they form.</summary>
            public List<List<(string Text, TgAttribute Attr)>> Lines()
            {
                var lines = new List<List<(string, TgAttribute)>>();
                var current = new List<(string, TgAttribute)>();
                foreach (var span in Spans)
                {
                    current.Add(span);
                    // A span may carry its own newline (the hunk ellipsis does), so the break
                    // is "ends with", not "is".
                    if (span.Text.EndsWith("\n")) { lines.Add(current); current = new List<(string, TgAttribute)>(); }
                }
                if (current.Count > 0) lines.Add(current);
                return lines;
            }

            public string Rendered()
                => string.Concat(Spans.Select(s => s.Text));
        }

        private static IReadOnlyList<DiffLine> Lines(params DiffLine[] lines) => lines;

        private static DiffLine Hunk(int oldNo, int newNo)
            => new DiffLine(DiffLineKind.HunkHeader, oldNo, newNo, $"@@ -{oldNo} +{newNo} @@");

        // ── Backgrounds ──────────────────────────────────────────────────────────

        [Fact]
        public void RemovedLinesAreTintedRed_AddedGreen_ContextPlain()
        {
            var sink = new Sink();
            DiffPainter.Paint(Lines(
                Hunk(40, 40),
                new DiffLine(DiffLineKind.Context, 40, 40, "int a = 1;"),
                new DiffLine(DiffLineKind.Removed, 41, null, "int b = 2;"),
                new DiffLine(DiffLineKind.Added, null, 41, "int b = 3;")),
                "Thing.cs", Palette(), sink.Write);

            var lines = sink.Lines();
            Assert.Equal(4, lines.Count);   // hunk ellipsis + three lines

            Assert.All(lines[1], s => Assert.Equal(ContextBg, s.Attr.Background));
            Assert.All(lines[2], s => Assert.Equal(RemovedBg, s.Attr.Background));
            Assert.All(lines[3], s => Assert.Equal(AddedBg, s.Attr.Background));
        }

        [Fact]
        public void TheTintCoversTheGutterAndTheMarker_NotJustTheCode()
        {
            // A tint that starts after the marker reads as a highlight on the text rather than
            // as a changed line, which is the distinction the whole treatment exists to make.
            var sink = new Sink();
            DiffPainter.Paint(Lines(
                Hunk(1, 1),
                new DiffLine(DiffLineKind.Added, null, 1, "x")),
                "Thing.cs", Palette(), sink.Write);

            var line = sink.Lines().Last();
            Assert.Contains(line, s => s.Text.Contains("+"));
            Assert.All(line, s => Assert.Equal(AddedBg, s.Attr.Background));
        }

        // ── Syntax colour inside the line ────────────────────────────────────────

        [Fact]
        public void CodeInsideAChangedLineKeepsItsSyntaxColours()
        {
            var sink = new Sink();
            DiffPainter.Paint(Lines(
                Hunk(1, 1),
                new DiffLine(DiffLineKind.Added, null, 1, "public string Name = \"x\";")),
                "Thing.cs", Palette(), sink.Write);

            var line = sink.Lines().Last();

            Assert.Contains(line, s => s.Text == "public" && s.Attr.Foreground == FgKeyword);
            Assert.Contains(line, s => s.Text == "\"x\"" && s.Attr.Foreground == FgString);
            // …and every one of them still on the added tint.
            Assert.All(line, s => Assert.Equal(AddedBg, s.Attr.Background));
        }

        [Fact]
        public void ContextLinesAreSyntaxColouredToo()
        {
            // Qwen colours them, and the alternative — dim grey context around coloured
            // changes — makes the unchanged code look like it is also part of the edit.
            var sink = new Sink();
            DiffPainter.Paint(Lines(
                Hunk(1, 1),
                new DiffLine(DiffLineKind.Context, 1, 1, "public int Count;")),
                "Thing.cs", Palette(), sink.Write);

            Assert.Contains(sink.Lines().Last(), s => s.Text == "public" && s.Attr.Foreground == FgKeyword);
        }

        [Fact]
        public void APlainTextFileIsTintedButNotHighlighted()
        {
            // The generic lexer would colour the word "class" in a README. A file nobody
            // claimed was code gets one plain token.
            var sink = new Sink();
            DiffPainter.Paint(Lines(
                Hunk(1, 1),
                new DiffLine(DiffLineKind.Added, null, 1, "class notes for the meeting")),
                "notes.txt", Palette(), sink.Write);

            var line = sink.Lines().Last();
            Assert.All(line, s => Assert.Equal(AddedBg, s.Attr.Background));
            Assert.Contains(line, s => s.Text == "class notes for the meeting" && s.Attr.Foreground == FgPlain);
        }

        // ── Gutter ───────────────────────────────────────────────────────────────

        [Fact]
        public void TheGutterIsRightAlignedToTheWidestNumber()
        {
            var sink = new Sink();
            DiffPainter.Paint(Lines(
                Hunk(998, 998),
                new DiffLine(DiffLineKind.Context, 998, 998, "a"),
                new DiffLine(DiffLineKind.Added, null, 1000, "b")),
                "Thing.cs", Palette(), sink.Write);

            var lines = sink.Lines();
            Assert.Equal(" 998   ", lines[1][0].Text);
            Assert.Equal("1000 + ", lines[2][0].Text);
        }

        [Fact]
        public void TheGutterNeverNarrowsBelowThreeColumns()
        {
            Assert.Equal(3, DiffPainter.GutterWidth(Lines(
                new DiffLine(DiffLineKind.Context, 1, 1, "a"))));

            Assert.Equal(5, DiffPainter.GutterWidth(Lines(
                new DiffLine(DiffLineKind.Context, 12345, 12345, "a"))));
        }

        [Fact]
        public void ARemovedLineIsNumberedInTheFileItWasRemovedFrom()
        {
            // Numbering a deletion by the new file would point at whatever sits there now.
            var sink = new Sink();
            DiffPainter.Paint(Lines(
                Hunk(70, 40),
                new DiffLine(DiffLineKind.Removed, 71, null, "gone")),
                "Thing.cs", Palette(), sink.Write);

            Assert.StartsWith(" 71 - ", sink.Lines().Last()[0].Text);
        }

        [Fact]
        public void AHunkBoundaryIsAnEllipsis_NotTheAtAtHeader()
        {
            var sink = new Sink();
            DiffPainter.Paint(Lines(
                Hunk(40, 40),
                new DiffLine(DiffLineKind.Context, 40, 40, "a"),
                Hunk(90, 90),
                new DiffLine(DiffLineKind.Context, 90, 90, "b")),
                "Thing.cs", Palette(), sink.Write);

            Assert.DoesNotContain("@@", sink.Rendered());
            Assert.Equal(2, sink.Spans.Count(s => s.Text.Contains("…")));
        }

        [Fact]
        public void AHunkThatStartsAtTheTopOfTheFileHasNoBreakBeforeIt()
        {
            var sink = new Sink();
            DiffPainter.Paint(Lines(
                Hunk(1, 1),
                new DiffLine(DiffLineKind.Context, 1, 1, "a")),
                "Thing.cs", Palette(), sink.Write);

            Assert.DoesNotContain("…", sink.Rendered());
        }

        // ── The cap ──────────────────────────────────────────────────────────────

        [Fact]
        public void TheCapIsSharedBetweenHunks_NotSpentOnTheFirst()
        {
            // The failure this prevents: 80 lines of hunk 1 and no sign that hunk 2 exists.
            var lines = new List<DiffLine> { Hunk(1, 1) };
            for (int i = 1; i <= 40; i++) lines.Add(new DiffLine(DiffLineKind.Added, null, i, $"first {i}"));
            lines.Add(Hunk(500, 500));
            for (int i = 1; i <= 40; i++) lines.Add(new DiffLine(DiffLineKind.Added, null, 500 + i, $"second {i}"));

            var sink = new Sink();
            DiffPainter.Paint(lines, "Thing.cs", Palette(), sink.Write, maxLines: 20);

            string rendered = sink.Rendered();
            Assert.Contains("first 1", rendered);
            Assert.Contains("second 1", rendered);
        }

        [Fact]
        public void TheCapNoticeNamesTheLinesAndTheHunks()
        {
            var lines = new List<DiffLine> { Hunk(1, 1) };
            for (int i = 1; i <= 40; i++) lines.Add(new DiffLine(DiffLineKind.Added, null, i, $"first {i}"));
            lines.Add(Hunk(500, 500));
            for (int i = 1; i <= 40; i++) lines.Add(new DiffLine(DiffLineKind.Added, null, 500 + i, $"second {i}"));

            var sink = new Sink();
            string notice = DiffPainter.Paint(lines, "Thing.cs", Palette(), sink.Write, maxLines: 20);

            Assert.NotNull(notice);
            Assert.Contains("62 more changed lines", notice);   // 80 changed, 18 shown
            Assert.Contains("in 2 hunks", notice);
        }

        [Fact]
        public void ADiffThatFits_GetsNoNotice()
        {
            var sink = new Sink();
            string notice = DiffPainter.Paint(Lines(
                Hunk(1, 1),
                new DiffLine(DiffLineKind.Added, null, 1, "a")),
                "Thing.cs", Palette(), sink.Write);

            Assert.Null(notice);
        }

        [Fact]
        public void AnEmptyDiffPaintsNothing()
        {
            var sink = new Sink();
            Assert.Null(DiffPainter.Paint(new List<DiffLine>(), "Thing.cs", Palette(), sink.Write));
            Assert.Empty(sink.Spans);
        }
    }
}
