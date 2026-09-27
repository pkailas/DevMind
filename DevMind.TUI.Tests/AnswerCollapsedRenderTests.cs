// File: AnswerCollapsedRenderTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// What the collapsed-answer line looks like on screen, and that a rebuild draws it the same.
//
// When the terminal iteration streams the report as prose and the executor then draws the
// task_done summary, the host records one TranscriptEntry.AnswerCollapsed instead of the
// summary's prose. The renderer turns that entry into a single dim stand-in line. These
// tests pin that line at the renderer — the deepest seam the TUI can be tested at without a
// live Terminal.Gui — and pin the property the whole design rests on: the live path
// (render one entry as it arrives) and a rebuild (RenderAll over the retained entries)
// produce the same characters.
//
// TuiAgenticHost itself cannot be constructed here (it needs a live Terminal.Gui view), so
// the decision is exercised through the two pieces it composes — the entry it records and
// the renderer that draws it. The untested call site is TuiAgenticHost.AppendAnswer's
// collapse branch (the ShouldCollapse call, the ParkAnswer, and the Record of the entry).

using System.Collections.Generic;
using System.Text;
using Xunit;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;

namespace DevMind.TUI.Tests
{
    public class AnswerCollapsedRenderTests
    {
        private sealed class Sink
        {
            public readonly List<(string Text, TgAttribute Attr, bool NonCopyable)> Spans = new();
            public void Write(string t, TgAttribute a, bool nonCopyable = false) => Spans.Add((t, a, nonCopyable));

            public string Text
            {
                get
                {
                    var sb = new StringBuilder();
                    foreach (var (t, _, _) in Spans) sb.Append(t);
                    return sb.ToString();
                }
            }
        }

        private static readonly TgColor Bg = new TgColor(0, 0, 0);

        private static TranscriptRenderer Make(Sink sink, int width) =>
            new TranscriptRenderer(
                sink:        sink.Write,
                width:       () => width,
                proseCap:    0,
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
                verbose: false,
                resolveColor: c => new TgAttribute(new TgColor(0x88, 0x88, 0x88), Bg));

        // The terminal iteration's prose, then the collapsed summary — the exact entry
        // sequence the live host records for a turn that ends with a redundant task_done.
        // The summary is worded differently from the prose on purpose: the real defect is the
        // SAME report in two wordings, and a test that made them identical could not tell the
        // two copies apart. The prose says one thing, the summary says the same in other words,
        // and the collapse must hide the SUMMARY's wording, not the prose's.
        private const string ProseLine1 = "Plan mode is active, so file creation was refused. Plan:";
        private const string ProseLine2 = "1) create hello.txt; 2) read it back to verify.";
        // The summary: the same report, reworded — its distinctive tail is what must NOT appear.
        private const string Summary =
            "Plan mode is active so file creation was refused. " +
            "Create hello.txt then read it back to verify before proceeding.";

        private static List<TranscriptEntry> TerminalTurn()
        {
            return new List<TranscriptEntry>
            {
                TranscriptEntry.Prose(ProseLine1 + "\n"),
                TranscriptEntry.Prose(ProseLine2 + "\n"),
                TranscriptEntry.ProseFlush(),
                TranscriptEntry.AnswerCollapsed(Summary),
            };
        }

        // The summary was already streamed as prose, so the transcript shows the prose once,
        // the dim stand-in line, and NOT the summary's wording again.
        [Fact]
        public void CollapsedSummary_DrawsProseOncePlusDimLine_NotTheSummaryText()
        {
            var sink = new Sink();
            Make(sink, 100).RenderAll(TerminalTurn());

            string text = sink.Text;

            // The prose is there (the diamond-lead block the user watched stream).
            Assert.Contains(ProseLine1, text);
            Assert.Contains(ProseLine2, text);

            // The stand-in line is there, in full.
            Assert.Contains(TranscriptRenderer.AnswerCollapsedLine, text);

            // The summary's own wording is NOT drawn. The prose above it already said the same
            // thing in its own words; the stand-in line does not carry the summary's text.
            Assert.DoesNotContain(Summary, text, System.StringComparison.Ordinal);
            Assert.DoesNotContain("before proceeding", text, System.StringComparison.Ordinal);
        }

        // The live path (render one entry as it arrives) and a rebuild (RenderAll over the
        // retained entries) must agree — the property that makes a resize show the same
        // collapsed result it showed live.
        [Fact]
        public void TheLivePathAndTheRebuildAgree_OnACollapsedAnswer()
        {
            var entries = TerminalTurn();

            var live = new Sink();
            var renderer = Make(live, 100);
            foreach (TranscriptEntry e in entries) renderer.Render(e);

            var rebuild = new Sink();
            Make(rebuild, 100).RenderAll(entries);

            Assert.Equal(live.Text, rebuild.Text);
            Assert.Contains(TranscriptRenderer.AnswerCollapsedLine, rebuild.Text);
        }

        // The stand-in line is drawn through the OutputColor.Dim path, not the model's Normal
        // prose path — it is the transcript's note, not the model's words. The renderer's
        // Emit(text, color) resolves the color via the `resolve` delegate, so this test maps
        // Dim and Normal to different attributes and checks the stand-in line carries Dim.
        [Fact]
        public void TheStandInLine_IsResolvedViaDim_NotNormal()
        {
            var sink = new Sink();
            var renderer = new TranscriptRenderer(
                sink:        sink.Write,
                width:       () => 100,
                proseCap:    0,
                resolve:     c => c == OutputColor.Dim
                                     ? new TgAttribute(new TgColor(0x11, 0x11, 0x11), Bg)
                                     : new TgAttribute(new TgColor(0x22, 0x22, 0x22), Bg),
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
                verbose: false,
                resolveColor: c => new TgAttribute(new TgColor(0x88, 0x88, 0x88), Bg));

            renderer.RenderAll(TerminalTurn());

            TgAttribute dim = new TgAttribute(new TgColor(0x11, 0x11, 0x11), Bg);
            bool found = false;
            foreach (var (t, a, _) in sink.Spans)
            {
                if (t.Contains(TranscriptRenderer.AnswerCollapsedLine))
                {
                    found = true;
                    Assert.Equal(dim, a);
                }
            }
            Assert.True(found, "the stand-in line was not drawn");
        }

        // A rebuild is a second pass over the same entries; the stand-in line must not
        // multiply or drift.
        [Fact]
        public void RenderingTwice_GivesTheSameCollapsedLine()
        {
            var entries = TerminalTurn();

            var sink = new Sink();
            var renderer = Make(sink, 100);
            renderer.RenderAll(entries);
            string first = sink.Text;

            sink.Spans.Clear();
            renderer.RenderAll(entries);

            Assert.Equal(first, sink.Text);
        }
    }
}
