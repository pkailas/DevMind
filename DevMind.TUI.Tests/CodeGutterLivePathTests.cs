// File: CodeGutterLivePathTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The gutter numbers a FENCED BLOCK, not a CODE ENTRY.
//
// CodeBlockStreamer emits each completed code line separately, and the host records ONE
// TranscriptEntry.Code per line — that is how a model answer reaches the renderer live,
// and how a rebuild re-plays it. The pre-fix renderer kept its line number as a LOCAL in
// RenderCode, so every per-line entry restarted the count at 1 and a six-line block read
// "1 1 1 1 1 1". The whole-block tests (CodeGutterTests) could never see it: they feed
// one entry carrying the whole block, where a local count and a block count agree.
//
// These drive the LIVE shape — a 6-line fenced C# block, blank line included, fed to
// CodeBlockStreamer in small chunks, each emitted line rendered as its own entry — and
// pin what the fix must keep true:
//   * the gutter runs 1..6 across the per-line entries (numbering follows the block);
//   * RenderAll over the same recorded entries paints byte-identical output (live ==
//     rebuild), because the count is renderer state, not a function of the entry;
//   * a second fenced block restarts at 1;
//   * the gutter the live insert sequence records is non-copyable, so a CopyText.Build
//     over the block returns the code the model wrote, no "  N  " prefixes — the copy
//     probe for the reported "copied text still had gutters" bug.
//
// xUnit v2: no 3-arg Assert.Equal/Contains message overloads; Assert.True carries messages.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevMind;
using Xunit;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;

namespace DevMind.TUI.Tests
{
    public class CodeGutterLivePathTests
    {
        private sealed class Sink
        {
            public readonly List<(string Text, TgAttribute Attr, bool NonCopyable)> Spans = new();
            public void Write(string t, TgAttribute a, bool nonCopyable = false) => Spans.Add((t, a, nonCopyable));
            public string Text { get { var sb = new StringBuilder(); foreach (var (t, _, _) in Spans) sb.Append(t); return sb.ToString(); } }
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

        // The live answer: a 6-line fenced C# block with a blank line, exactly the shape
        // the model's turn produced when the bug was reported.
        private const string LiveAnswer =
            "Here is the program:\n" +
            "```csharp\n" +
            "using System;\n" +
            "\n" +
            "class Program\n" +
            "{\n" +
            "    static void Main() { Console.WriteLine(\"Hello, World!\"); }\n" +
            "}\n" +
            "```\n";

        // Drive CodeBlockStreamer exactly the way the host does: per-line code emissions
        // become per-line Code entries, in arrival order. `chunkSize` splits the answer
        // into small Feed calls the way SSE tokens arrive; the entry sequence must not
        // depend on the chunking.
        private static List<TranscriptEntry> StreamToEntries(string answer, int chunkSize)
        {
            var entries = new List<TranscriptEntry>();
            var streamer = new CodeBlockStreamer(
                prose: line => entries.Add(TranscriptEntry.Prose(line)),
                code:  (line, lang, blockStart) =>
                        entries.Add(TranscriptEntry.CodeLine(line, lang, blockStart)));

            for (int i = 0; i < answer.Length; i += chunkSize)
                streamer.Feed(answer.Substring(i, Math.Min(chunkSize, answer.Length - i)));
            streamer.Flush();
            return entries;
        }

        // ── The numbering follows the block, not the entry ──────────────────────

        [Fact]
        public void StreamedSixLineBlock_RendersGuttersOneThroughSix_PerLineEntries()
        {
            var entries = StreamToEntries(LiveAnswer, chunkSize: 7);

            // Sanity: this is the LIVE shape — six separate Code entries, one per line,
            // not a single whole-block entry. Without this the test would be testing the
            // thing CodeGutterTests already cover.
            var codeEntries = entries.Where(e => e.Kind == TranscriptEntryKind.Code).ToList();
            Assert.Equal(6, codeEntries.Count);
            Assert.True(codeEntries[0].BlockStart,
                "the first line after the opening fence must be marked as a block start");
            Assert.True(codeEntries.Skip(1).All(e => !e.BlockStart),
                "only the first line of the block is a start; the rest are continuations");

            var (renderer, sink) = Make();
            foreach (TranscriptEntry e in entries) renderer.Render(e);

            var text = sink.Text;
            // The exact gutters the reader sees: 1..6, right-aligned in three columns,
            // blank line included (line 2).
            foreach (int n in new[] { 1, 2, 3, 4, 5, 6 })
                Assert.True(text.Contains($"  {n}  "),
                    $"expected gutter \"  {n}  \" in the live output, got:\n{text}");
            // And the pre-fix symptom is gone: no gutter repeats the first line's number.
            int ones = CountOccurrences(text, "  1  ");
            Assert.True(ones == 1, $"expected exactly one \"  1  \" gutter, found {ones}:\n{text}");
        }

        [Fact]
        public void RenderAllOverTheSameEntries_PaintsIdenticalGutters_LiveEqualsRebuild()
        {
            var entries = StreamToEntries(LiveAnswer, chunkSize: 7);

            var (live, liveSink) = Make();
            foreach (TranscriptEntry e in entries) live.Render(e);

            var (rebuild, rebuildSink) = Make();
            rebuild.RenderAll(entries);

            Assert.Equal(liveSink.Text, rebuildSink.Text);
            Assert.Equal(liveSink.Spans.Count, rebuildSink.Spans.Count);
            for (int i = 0; i < liveSink.Spans.Count; i++)
                Assert.True(liveSink.Spans[i].NonCopyable == rebuildSink.Spans[i].NonCopyable,
                    $"span {i}: live and rebuild disagree on the non-copyable flag");
        }

        [Fact]
        public void ASecondFencedBlock_RestartsTheGutterAtOne()
        {
            const string answer = LiveAnswer + "\nAnd another:\n```\nfoo\nbar\n```\n";
            var entries = StreamToEntries(answer, chunkSize: 11);

            var codeEntries = entries.Where(e => e.Kind == TranscriptEntryKind.Code).ToList();
            Assert.Equal(8, codeEntries.Count);
            Assert.True(codeEntries[0].BlockStart && codeEntries[6].BlockStart,
                "both fenced blocks must mark their first line as a start");
            Assert.True(codeEntries.Skip(1).Take(5).All(e => !e.BlockStart),
                "the first block's lines 2..6 are continuations");

            var (renderer, sink) = Make();
            foreach (TranscriptEntry e in entries) renderer.Render(e);

            // The second block's first line is its own 1 — not a 7 continuing the first.
            var text = sink.Text;
            int secondLine = text.LastIndexOf("foo", System.StringComparison.Ordinal);
            Assert.True(secondLine > 0, "the second block's content is in the output");
            Assert.True(text.Substring(0, secondLine).Contains("  6  "),
                "the first block ran to 6");
            // The second line's gutter is the five characters right before its "foo".
            Assert.True(text.Substring(secondLine - 5, 5) == "  1  ",
                $"the second block restarts at 1, got:\n{text.Substring(secondLine - 5, 12)}");
        }

        // ── The copy path: the gutter the live insert sequence records is skipped ─

        // The host's ColorSpanList is private and TuiAgenticHost needs a live view, so the
        // test drives the SAME shape the live path builds: the renderer's sink is the host's
        // EnqueueSpan, which records (start, length, attr, nonCopyable) for every insert at
        // the document end. This replicates that recording with running offsets — the
        // offsets the live path computes — and the host's IsNonCopyableAt lookup verbatim.
        private sealed class SpanRecorder
        {
            private readonly List<(int Start, int Length, bool NonCopyable)> _spans = new();
            public string Document { get; private set; } = string.Empty;

            // The host's EnqueueSpan → InsertSpan: append at the document end, record the span.
            public void Write(string text, TgAttribute attr, bool nonCopyable)
            {
                if (string.IsNullOrEmpty(text)) return;
                int start = Document.Length;
                Document += text;
                _spans.Add((start, text.Length, nonCopyable));
            }

            // TuiAgenticHost.ColorSpanList.IsNonCopyable, copied: binary search for the first
            // span whose end is strictly greater than the offset.
            public bool IsNonCopyableAt(int offset)
            {
                int lo = 0, hi = _spans.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (_spans[mid].Start + _spans[mid].Length <= offset) lo = mid + 1;
                    else hi = mid;
                }
                return lo < _spans.Count && _spans[lo].NonCopyable;
            }
        }

        [Fact]
        public void CopyOfTheStreamedBlock_ReturnsCodeWithoutTheGutter()
        {
            var entries = StreamToEntries(LiveAnswer, chunkSize: 7);

            // The live insert sequence: each recorded entry rendered into the span store
            // the host builds, offsets and flags and all.
            var store = new SpanRecorder();
            var renderer = new TranscriptRenderer(
                sink:        store.Write,
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
            foreach (TranscriptEntry e in entries) renderer.Render(e);

            // The user selected the block: from the first gutter to the end of the last
            // code line (the "}" before the closing fence).
            int start = store.Document.IndexOf("  1  using", System.StringComparison.Ordinal);
            int end = store.Document.LastIndexOf("}\n", System.StringComparison.Ordinal) + 2;
            Assert.True(start >= 0 && end > start, "the block is in the document");

            // The pure copy decision, with the host's predicate.
            string copied = CopyText.Build(store.Document, start, end, store.IsNonCopyableAt);

            // The gutter is display chrome: a copy of the block is the code the model wrote.
            var expected = new StringBuilder();
            foreach (string line in new[]
            {
                "using System;",
                "",
                "class Program",
                "{",
                "    static void Main() { Console.WriteLine(\"Hello, World!\"); }",
                "}",
            })
                expected.Append(line).Append('\n');
            Assert.Equal(expected.ToString(), copied);

            // The pre-fix symptom, stated as an assertion: no "  N  " gutter survives the copy.
            Assert.DoesNotContain("  1  ", copied);
            Assert.DoesNotContain("  2  ", copied);
        }

        // ── Bug 2, live path: the render pump's flush coalescing keeps the flag ──────
        //
        // The live streaming path is EnqueueSpan → _pending → FlushPending. FlushPending does
        // one insert per batch and records each coalesced color run as a ColorSpan — that
        // coalescing is where the nonCopyable flag the renderer's sink passed IS OR IS NOT
        // carried into the span. TuiAgenticHost.CoalesceFlushRuns is that logic, extracted
        // pure; this drives it with the exact run sequence a streamed 6-line block produces
        // (gutter number + separator both dim AND non-copyable, the code runs not) and
        // checks the spans it records — the same spans IsNonCopyableAt answers from.
        [Fact]
        public void FlushCoalescing_KeepsTheNonCopyableFlagOnGutterSpans()
        {
            // The dim attribute the gutter is painted with — the same value for every gutter
            // piece, which is exactly what makes the runs coalesce into one span per line.
            var dim = new TgAttribute(GutterColor, Bg);
            var code = new TgAttribute(new TgColor(0xD4, 0xD4, 0xD4), Bg);

            // One line of the live block, as the renderer emits it: number, separator, code.
            // Both gutter pieces are (dim, nonCopyable); the code run is (code, copyable).
            var runs = new List<(string Text, bool NonCopyable, TgAttribute Attr)>
            {
                ("  1  ", true, dim),
                ("using System;", false, code),
                ("\n", false, code),
            };

            var spans = TuiAgenticHost.CoalesceFlushRuns(runs, start: 0);
            string spanDump = string.Join(", ", spans.Select(s => "[" + s.Start + "+" + s.Length + " nc=" + s.NonCopyable + "]"));

            // The gutter is one coalesced span covering "  1  " — and it must carry the flag.
            var gutterSpan = spans.FirstOrDefault(s => s.Start == 0 && s.Length == 5);
            Assert.True(gutterSpan.Start == 0 && gutterSpan.Length == 5,
                "expected one span covering the 5-char gutter, got: " + spanDump);
            Assert.True(gutterSpan.NonCopyable,
                "the gutter span lost its non-copyable flag in the flush coalescing — " +
                "a copy of the live block keeps the gutters: " + spanDump);

            // The code that follows stays copyable — coalescing must not bleed the flag.
            var codeSpan = spans.FirstOrDefault(s => s.Start == 5);
            Assert.True(codeSpan.Start == 5 && !codeSpan.NonCopyable,
                "the code run after the gutter must remain copyable");
        }

        // The pre-fix shape, named: one whole-block entry still numbers 1..6 inside itself.
        // Whole-block entries keep working; per-line entries are the new shape they must
        // agree with.
        [Fact]
        public void AWholeBlockEntry_StillNumbersItsLines_OneThroughSix()
        {
            var (renderer, sink) = Make();
            renderer.Render(TranscriptEntry.Code(
                "using System;\n\nclass Program\n{\n    static void Main() { }\n}\n",
                "csharp", nestUnderCall: false));

            var text = sink.Text;
            foreach (int n in new[] { 1, 2, 3, 4, 5, 6 })
                Assert.True(text.Contains($"  {n}  "), $"expected gutter \"  {n}  \" in:\n{text}");
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }
    }
}
