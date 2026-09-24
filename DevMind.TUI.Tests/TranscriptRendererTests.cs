// File: TranscriptRendererTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// One renderer, two callers, the same output.
//
// The transcript is now a projection of retained source: entries are drawn as they arrive,
// and drawn again from scratch whenever the width changes. That only holds together if the
// two are the same code — and "the same code" is a claim, so it is tested twice here. Once
// as idempotence (rendering the same entries twice gives the same characters, which is what
// proves Reset clears every piece of arrival-order state), and once as live-equals-rebuild
// (feeding entries one at a time gives what RenderAll gives).
//
// The failure those guard against is the worst kind this design can have: a rebuild that is
// subtly different from what was on screen a moment ago — a missing ◆, a lost indent — at
// some width nobody happened to try.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;

namespace DevMind.TUI.Tests
{
    public class TranscriptRendererTests
    {
        private sealed class Sink
        {
            public readonly List<(string Text, TgAttribute Attr)> Spans = new();
            public void Write(string t, TgAttribute a) => Spans.Add((t, a));

            public string Text
            {
                get
                {
                    var sb = new StringBuilder();
                    foreach (var (t, _) in Spans) sb.Append(t);
                    return sb.ToString();
                }
            }
        }

        private static readonly TgColor Bg = new TgColor(0, 0, 0);

        private static TranscriptRenderer Make(Sink sink, int width) =>
            new TranscriptRenderer(
                sink:        sink.Write,
                width:       () => width,
                resolve:     c => new TgAttribute(new TgColor(0xCC, 0xCC, 0xCC), Bg),
                prose:       st => new TgAttribute(new TgColor((byte)st, 0, 0), Bg),
                syntax:      k => new TgAttribute(new TgColor(0, (byte)k, 0), Bg),
                diffPalette: () => new DiffPalette
                {
                    Foreground = _ => new TgColor(0xD4, 0xD4, 0xD4),
                    Gutter     = new TgColor(0x88, 0x88, 0x88),
                    ContextBg  = Bg,
                    RemovedBg  = new TgColor(0x4B, 0x1F, 0x1F),
                    AddedBg    = new TgColor(0x1F, 0x3A, 0x1F),
                },
                verbose: false);

        private static string Render(IEnumerable<TranscriptEntry>? entries, int width)
        {
            var sink = new Sink();
            Make(sink, width).RenderAll(entries);
            return sink.Text;
        }

        private static readonly string[] SixColumn =
        {
            "| One | Two | Three | Four | Five | Six |",
            "|---|---|---|---|---|---|",
            "| a reasonably long cell | another one here | and a third | plus four | five | six |",
            "| short | short | short | short | short | short |",
        };

        private static List<TranscriptEntry> Conversation()
        {
            var entries = new List<TranscriptEntry>
            {
                TranscriptEntry.Output("[SHELL] > dotnet build\n", OutputColor.Dim),
                TranscriptEntry.Output("Build succeeded.\n", OutputColor.Normal),
                TranscriptEntry.Prose("Here is what I found.\n"),
                TranscriptEntry.Prose("- A bullet that is quite a lot longer than forty columns would allow\n"),
            };
            foreach (string row in SixColumn) entries.Add(TranscriptEntry.Prose(row + "\n"));
            entries.Add(TranscriptEntry.ProseFlush());

            // Ends with prose on purpose: that leaves the prose-block flag SET, so a Reset
            // that does not clear it changes the second render. Ending on the table would
            // clear the flag for us and hide exactly the bug this sequence is here to catch.
            entries.Add(TranscriptEntry.Prose("And a closing thought.\n"));
            return entries;
        }

        // ── The two claims the design rests on ───────────────────────────────────

        [Fact]
        public void RenderingTheSameEntriesTwiceGivesTheSameCharacters()
        {
            // If Reset misses one piece of arrival-order state, the second pass differs —
            // and a rebuild is exactly a second pass. The sequence opens with PROSE on
            // purpose: a transcript that opens with a tool line would have that line clear
            // the prose-block flag for us and hide a Reset that never touched it.
            var entries = new List<TranscriptEntry> { TranscriptEntry.Prose("Opening line.\n") };
            entries.AddRange(Conversation());

            var sink = new Sink();
            var renderer = Make(sink, 100);

            renderer.RenderAll(entries);
            string first = sink.Text;

            sink.Spans.Clear();
            renderer.RenderAll(entries);

            Assert.Equal(first, sink.Text);
            Assert.StartsWith(TranscriptRenderer.ProseLead, first);
        }

        [Fact]
        public void TheLivePathAndTheRebuildProduceTheSameOutput()
        {
            // Live is "render one entry as it arrives"; a rebuild is "render them all".
            var entries = Conversation();

            var live = new Sink();
            var renderer = Make(live, 100);
            foreach (TranscriptEntry e in entries) renderer.Render(e);

            Assert.Equal(Render(entries, 100), live.Text);
        }

        [Fact]
        public void ResetReturnsItToADrawnNothingState()
        {
            var sink = new Sink();
            var renderer = Make(sink, 100);

            renderer.Render(TranscriptEntry.Prose("First block.\n"));
            sink.Spans.Clear();

            renderer.Reset();
            renderer.Render(TranscriptEntry.Prose("First block.\n"));

            // A fresh block leads with the diamond; a continued one would not.
            Assert.Contains(TranscriptRenderer.ProseLead, sink.Text);
            Assert.Empty(renderer.EntryOffsets.Skip(1));
        }

        // ── Width changes the projection ─────────────────────────────────────────

        [Fact]
        public void ASixColumnTableIsAGridWhenWideAndRecordsWhenNarrow()
        {
            var entries = Conversation();

            string wide = Render(entries, 200);
            string narrow = Render(entries, 40);

            Assert.Contains(TableGlyphs.Unicode.TopLeft, wide);
            Assert.DoesNotContain(TableGlyphs.Unicode.TopLeft, narrow);

            // The record form names every field instead of heading a column.
            Assert.Contains("One: ", narrow);
            Assert.Contains("Six: ", narrow);
        }

        [Fact]
        public void AGridThatStillFitsIsLeftAsAGrid()
        {
            // 60 columns is not narrow enough for six columns to give up: the threshold is
            // 41 and the cells here wrap to four lines, not five. Pinned so the flip point
            // is a decision on record rather than something rediscovered by dragging.
            Assert.Contains(TableGlyphs.Unicode.TopLeft, Render(Conversation(), 60));
        }

        [Fact]
        public void ALongBulletWrapsAtSixtyAndDoesNotAtTwoHundred()
        {
            var entry = new[]
            {
                TranscriptEntry.Prose("- A bullet that is quite a lot longer than sixty columns would allow it to be\n"),
                TranscriptEntry.ProseFlush(),
            };

            int wideLines = Render(entry, 200).TrimEnd('\n').Split('\n').Length;
            int narrowLines = Render(entry, 60).TrimEnd('\n').Split('\n').Length;

            Assert.Equal(1, wideLines);
            Assert.True(narrowLines >= 2, $"expected a wrap at 60, got {narrowLines} line(s)");
        }

        [Fact]
        public void EveryEntryGetsAnOffset_AndTheyAscend()
        {
            // The scroll anchor is a binary search over these; out of order and it lands the
            // reader somewhere they never were.
            var sink = new Sink();
            var renderer = Make(sink, 100);
            var entries = Conversation();

            renderer.RenderAll(entries);

            Assert.Equal(entries.Count, renderer.EntryOffsets.Count);
            for (int i = 1; i < renderer.EntryOffsets.Count; i++)
                Assert.True(renderer.EntryOffsets[i] >= renderer.EntryOffsets[i - 1]);
            Assert.Equal(sink.Text.Length, renderer.Emitted);
        }

        // ── What the renderer keeps doing ────────────────────────────────────────

        [Fact]
        public void ToolOutputStillNestsUnderItsCall()
        {
            string text = Render(new[]
            {
                TranscriptEntry.Output("[SHELL] > ls\n", OutputColor.Dim),
                TranscriptEntry.Output("a.txt\n", OutputColor.Normal),
            }, 100);

            Assert.Contains("    a.txt", text);
        }

        [Fact]
        public void ProseStillLeadsWithTheDiamond()
        {
            Assert.Contains(TranscriptRenderer.ProseLead,
                Render(new[] { TranscriptEntry.Prose("The model speaks.\n") }, 100));
        }

        [Fact]
        public void AListingNestsAndItsTruncationNoticeNestsWithIt()
        {
            var body = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line {i}"));

            string text = Render(new[] { TranscriptEntry.Listing(body, "x.txt", 500) }, 100);

            Assert.Contains("    line 1", text);
            Assert.Contains("    … (100 more lines — 500 total)", text);
        }

        [Fact]
        public void ADiffIsRenderedFromItsTwoVersions_SoItRelaysOutLikeAnythingElse()
        {
            string text = Render(new[]
            {
                TranscriptEntry.Diff("a\nb\nc\n", "a\nB\nc\n", "x.cs"),
            }, 100);

            Assert.Contains("b", text);
            Assert.Contains("B", text);
        }

        [Fact]
        public void AnEmptyModelDrawsNothing()
        {
            Assert.Equal("", Render(new List<TranscriptEntry>(), 100));
            Assert.Equal("", Render(null, 100));
        }
    }
}
