// File: CodeGutterTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The model's fenced code carries a dim line-number gutter — the same look a diff gets —
// so a long block can be pointed at line-by-line. It is display chrome: marked
// non-copyable so a copy of the block returns the code the model wrote.
//
// These pin the renderer's gutter decisions at the sink: what is emitted, in what
// colour, and which spans carry the non-copyable flag. The scope is one decision —
// ONLY the model's fenced code gets one. Tool listings, shell output and diffs
// (which have their own gutter) do not, and these tests say so explicitly.

using System.Collections.Generic;
using Xunit;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;

namespace DevMind.TUI.Tests
{
    public class CodeGutterTests
    {
        private sealed class Sink
        {
            public readonly List<(string Text, TgAttribute Attr, bool NonCopyable)> Spans = new();
            public void Write(string t, TgAttribute a, bool nonCopyable = false) => Spans.Add((t, a, nonCopyable));
        }

        private static readonly TgColor Bg = new TgColor(0, 0, 0);
        private static readonly TgColor GutterColor = new TgColor(0x88, 0x88, 0x88);

        private static (TranscriptRenderer Renderer, Sink Sink) Make()
        {
            var sink = new Sink();
            var renderer = new TranscriptRenderer(
                sink:        sink.Write,
                width:       () => 100,
                proseCap:    0,
                resolve:     c => new TgAttribute(new TgColor(0xCC, 0xCC, 0xCC), Bg),
                prose:       st => new TgAttribute(new TgColor((byte)st, 0, 0), Bg),
                syntax:      k => new TgAttribute(new TgColor(0, (byte)k, 0), Bg),
                diffPalette: () => new DiffPalette
                {
                    Foreground = _ => new TgColor(0xD4, 0xD4, 0xD4),
                    Gutter     = GutterColor,
                    ContextBg  = Bg,
                    RemovedBg  = new TgColor(0x4B, 0x1F, 0x1F),
                    AddedBg    = new TgColor(0x1F, 0x3A, 0x1F),
                },
                verbose: false,
                resolveColor: c => new TgAttribute(GutterColor, Bg));
            return (renderer, sink);
        }

        // ── The gutter itself ────────────────────────────────────────────────────

        [Fact]
        public void AThreeLineBlockGetsNumbersOneTwoThree_RightsInThreeColumns()
        {
            var (renderer, sink) = Make();
            renderer.Render(TranscriptEntry.Code("alpha\nbeta\ngamma\n", "csharp", nestUnderCall: false));

            // Each line: three columns of gutter, right-aligned, then two spaces — the
            // diff's look. The numbers and their padding are one non-copyable span each,
            // in the gutter colour, and nothing else in the block is non-copyable.
            int gutterSpans = 0;
            foreach (var (text, attr, nonCopyable) in sink.Spans)
            {
                if (!nonCopyable) continue;
                gutterSpans++;
                Assert.True(GutterColor.Equals(attr.Foreground),
                    $"expected gutter foreground {GutterColor}, got {attr.Foreground}");
            }

            var sb = new System.Text.StringBuilder();
            foreach (var (t, _, _) in sink.Spans) sb.Append(t);

            Assert.Equal(6, gutterSpans);   // number + separator per line
            Assert.Contains("  1  ", sb.ToString());
            Assert.Contains("  2  ", sb.ToString());
            Assert.Contains("  3  ", sb.ToString());
        }

        [Fact]
        public void GutterNumbersAreDim_AndCodeAfterThemIsNot()
        {
            var (renderer, sink) = Make();
            renderer.Render(TranscriptEntry.Code("alpha\n", "csharp", nestUnderCall: false));

            // The first two spans are the gutter (number, then separator); the code that
            // follows carries its own (syntax) colour and is NOT non-copyable — only the
            // gutter is chrome.
            Assert.True(sink.Spans.Count >= 3);
            Assert.True(sink.Spans[0].NonCopyable);
            Assert.True(GutterColor.Equals(sink.Spans[0].Attr.Foreground),
                $"expected gutter foreground {GutterColor}, got {sink.Spans[0].Attr.Foreground}");
            Assert.True(sink.Spans[1].NonCopyable);
            Assert.True(GutterColor.Equals(sink.Spans[1].Attr.Foreground),
                $"expected separator foreground {GutterColor}, got {sink.Spans[1].Attr.Foreground}");
            Assert.False(sink.Spans[2].NonCopyable);
        }

        [Fact]
        public void NumberingRestartsAtOne_ForTheSecondBlock()
        {
            var (renderer, sink) = Make();
            renderer.Render(TranscriptEntry.Code("one\ntwo\n", "csharp", nestUnderCall: false));
            int afterFirst = sink.Spans.Count;
            renderer.Render(TranscriptEntry.Code("three\n", "csharp", nestUnderCall: false));

            // The second block's first gutter span is its 1, not a 3 continuing the
            // first block's count.
            var firstOfSecond = sink.Spans[afterFirst];
            Assert.True(firstOfSecond.NonCopyable);
            Assert.Contains("1", firstOfSecond.Text);
            Assert.DoesNotContain("3", firstOfSecond.Text);
        }

        [Fact]
        public void ABlankLineInABlockStillGetsANumber()
        {
            var (renderer, sink) = Make();
            renderer.Render(TranscriptEntry.Code("top\n\nbottom\n", "csharp", nestUnderCall: false));

            var text = new System.Text.StringBuilder();
            foreach (var (t, _, _) in sink.Spans) text.Append(t);

            // Three lines, three numbers — the blank one in the middle is line 2.
            Assert.Contains("  1  ", text.ToString());
            Assert.Contains("  2  ", text.ToString());
            Assert.Contains("  3  ", text.ToString());
        }

        [Fact]
        public void ABlockOverNineHundredNinetyNineLinesWidensTheGutter()
        {
            var (renderer, sink) = Make();
            var lines = new List<string>();
            for (int i = 0; i < 1000; i++) lines.Add($"line{i}");
            renderer.Render(TranscriptEntry.Code(string.Join("\n", lines) + "\n", "csharp", nestUnderCall: false));

            var text = new System.Text.StringBuilder();
            foreach (var (t, _, _) in sink.Spans) text.Append(t);

            // Line 1000 is four digits: the gutter widens for it (a 1000-line block is
            // rare; the first 999 lines stay three columns and accept the misalignment).
            Assert.Contains("1000", text.ToString());
        }

        // ── The scope: what does NOT get a gutter ────────────────────────────────

        [Fact]
        public void ProseGetsNoCodeGutter()
        {
            var (renderer, sink) = Make();
            renderer.Render(TranscriptEntry.Prose("Just the model talking.\n"));

            foreach (var (_, _, nonCopyable) in sink.Spans)
                Assert.False(nonCopyable);
        }

        [Fact]
        public void AListingGetsNoCodeGutter()
        {
            // A file a Read call returned shows a FILE — its numbers are the file's,
            // not the transcript's. It keeps its nest indent, no gutter.
            var (renderer, sink) = Make();
            renderer.Render(TranscriptEntry.Listing("line1\nline2\n", "x.txt", 2));

            foreach (var (_, _, nonCopyable) in sink.Spans)
                Assert.False(nonCopyable);

            var text = new System.Text.StringBuilder();
            foreach (var (t, _, _) in sink.Spans) text.Append(t);
            Assert.Contains("    line1", text.ToString());
        }

        [Fact]
        public void ADiffGetsNoCodeGutter()
        {
            // Diffs have their own gutter, painted by DiffPainter — not the code gutter.
            var (renderer, sink) = Make();
            renderer.Render(TranscriptEntry.Diff("a\nb\n", "a\nB\n", "x.cs"));

            // The diff's gutter is not the code gutter's: nothing it paints is
            // non-copyable.
            foreach (var (_, _, nonCopyable) in sink.Spans)
                Assert.False(nonCopyable);
        }

        [Fact]
        public void EngineOutputGetsNoCodeGutter()
        {
            var (renderer, sink) = Make();
            renderer.Render(TranscriptEntry.Output("[SHELL] > ls\n", OutputColor.Dim));

            foreach (var (_, _, nonCopyable) in sink.Spans)
                Assert.False(nonCopyable);
        }

        // ── Live path equals rebuild for the gutter too ──────────────────────────

        [Fact]
        public void LiveAndRebuildAgreeOnTheGutter()
        {
            var entries = new List<TranscriptEntry>
            {
                TranscriptEntry.Prose("Here is code.\n"),
                TranscriptEntry.Code("a\nb\n", "csharp", nestUnderCall: false),
                TranscriptEntry.Prose("Done.\n"),
            };

            var (live, liveSink) = Make();
            foreach (TranscriptEntry e in entries) live.Render(e);

            var (rebuild, rebuildSink) = Make();
            rebuild.RenderAll(entries);

            Assert.Equal(liveSink.Spans.Count, rebuildSink.Spans.Count);
            for (int i = 0; i < liveSink.Spans.Count; i++)
            {
                Assert.Equal(liveSink.Spans[i].Text, rebuildSink.Spans[i].Text);
                Assert.Equal(liveSink.Spans[i].NonCopyable, rebuildSink.Spans[i].NonCopyable);
            }
        }
    }
}
