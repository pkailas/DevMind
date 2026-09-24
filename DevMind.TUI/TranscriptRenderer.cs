// File: TranscriptRenderer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Drawing a transcript entry — once, live, or again at a new width.
//
// These are the emitters that used to live on TuiAgenticHost. They moved here for one
// reason: a rebuild has to produce exactly what the live path produced, and the only way to
// guarantee that is for there to be one path. "Append one entry and draw it" and "draw them
// all" are now the same code called with one entry or with every entry, so the two cannot
// drift — which is the failure a second renderer would have, quietly, at some width nobody
// tested.
//
// Nothing here touches a view. The width, the attributes and the sink all arrive as
// delegates, which is what lets every decision in this file be tested without standing up
// Terminal.Gui — the property the whole transcript has been built on since brief 09.
//
// The state that depends on arrival order — whether a tool call is open, whether a prose
// block is running, what the table buffer is holding — lives here and is cleared by Reset.
// A rebuild that forgot to clear it would render the second copy differently from the
// first, which is exactly what the idempotence test pins.

using System;
using System.Collections.Generic;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace DevMind
{
    /// <summary>
    /// Renders <see cref="TranscriptEntry"/> values into spans. Stateful across calls in
    /// arrival order; <see cref="Reset"/> returns it to the state it had at the first entry.
    /// </summary>
    internal sealed class TranscriptRenderer
    {
        private readonly Action<string, TgAttribute> _sink;
        private readonly Func<int> _width;
        private readonly Func<OutputColor, TgAttribute> _resolve;
        private readonly Func<InlineTextStyle, TgAttribute> _prose;
        private readonly Func<TokenKind, TgAttribute> _syntax;
        private readonly Func<DiffPalette> _diffPalette;
        private readonly bool _verbose;

        // Arrival-order state. Every field here must be cleared by Reset.
        private TranscriptBlock _block;
        private TableBuffer _tables;
        private bool _inProseBlock;
        private bool _lastWriteWasToolLine;

        // Where each rendered entry started, in characters emitted so far. The scroll anchor
        // needs it to keep the reader's place across a rebuild.
        private readonly List<int> _entryOffsets = new List<int>();
        private int _emitted;

        public TranscriptRenderer(
            Action<string, TgAttribute> sink,
            Func<int> width,
            Func<OutputColor, TgAttribute> resolve,
            Func<InlineTextStyle, TgAttribute> prose,
            Func<TokenKind, TgAttribute> syntax,
            Func<DiffPalette> diffPalette,
            bool verbose)
        {
            _sink        = sink ?? throw new ArgumentNullException(nameof(sink));
            _width       = width ?? throw new ArgumentNullException(nameof(width));
            _resolve     = resolve ?? throw new ArgumentNullException(nameof(resolve));
            _prose       = prose ?? throw new ArgumentNullException(nameof(prose));
            _syntax      = syntax ?? throw new ArgumentNullException(nameof(syntax));
            _diffPalette = diffPalette ?? throw new ArgumentNullException(nameof(diffPalette));
            _verbose     = verbose;

            Reset();
        }

        /// <summary>Document start offset of each entry rendered since the last <see cref="Reset"/>.</summary>
        public IReadOnlyList<int> EntryOffsets => _entryOffsets;

        /// <summary>Characters emitted since the last <see cref="Reset"/>.</summary>
        public int Emitted => _emitted;

        /// <summary>The rules that give tables their glyphs. One constant, swappable for ASCII.</summary>
        public static readonly TableGlyphs TableRules = TableGlyphs.Unicode;

        /// <summary>The diamond, and the hanging indent that lines a block up underneath it.</summary>
        public const string ProseLead = "◆ ";
        public const string ProseHangingIndent = "  ";

        /// <summary>Back to the state of a transcript that has drawn nothing.</summary>
        public void Reset()
        {
            _block = new TranscriptBlock(_verbose);
            _tables = new TableBuffer();
            _inProseBlock = false;
            _lastWriteWasToolLine = false;
            _entryOffsets.Clear();
            _emitted = 0;
        }

        /// <summary>Draw every entry in order, from a clean slate.</summary>
        public void RenderAll(IEnumerable<TranscriptEntry> entries)
        {
            Reset();
            if (entries == null) return;
            foreach (TranscriptEntry entry in entries) Render(entry);
        }

        /// <summary>Draw one entry, continuing from whatever came before it.</summary>
        public void Render(TranscriptEntry entry)
        {
            if (entry == null) return;

            _entryOffsets.Add(_emitted);

            switch (entry.Kind)
            {
                case TranscriptEntryKind.Output:    RenderOutput(entry.Text, entry.Color); break;
                case TranscriptEntryKind.Prose:     RenderProse(entry.Text); break;
                case TranscriptEntryKind.ProseFlush: RenderTableResult(_tables.Flush()); break;
                case TranscriptEntryKind.Code:      RenderCode(entry.Text, entry.Language, entry.NestUnderCall); break;
                case TranscriptEntryKind.Listing:   RenderListing(entry.Text, entry.Path, entry.Count); break;
                case TranscriptEntryKind.Diff:      RenderDiff(entry.Text, entry.Second, entry.Path); break;
            }
        }

        // ── The sink ─────────────────────────────────────────────────────────────

        private void Emit(string text, TgAttribute attr)
        {
            if (string.IsNullOrEmpty(text)) return;
            _emitted += text.Length;
            _sink(text, attr);
        }

        private void Emit(string text, OutputColor color) => Emit(text, _resolve(color));

        // ── Engine output ────────────────────────────────────────────────────────

        private void RenderOutput(string text, OutputColor color)
        {
            if (string.IsNullOrEmpty(text)) return;

            foreach (TranscriptLine line in _block.Accept(text, color))
                Emit(line.Text, line.Color);

            _lastWriteWasToolLine = true;
            _inProseBlock = false;
        }

        // ── Prose, and the tables hiding inside it ───────────────────────────────

        private void RenderProse(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            RenderTableResult(_tables.Feed(line));
        }

        private void RenderTableResult(TableBufferResult result)
        {
            if (result.Action == TableBufferAction.Table && result.Table != null)
                RenderTable(result.Table);

            foreach (string prose in result.Prose)
                RenderProseLine(prose);
        }

        private void RenderTable(PipeTableModel table)
        {
            _block.CloseBlock();

            // The table is a block of the model's own output, so it gets the same separation
            // from a tool line that prose does, and it ends whatever prose block preceded it.
            if (_lastWriteWasToolLine)
            {
                _lastWriteWasToolLine = false;
                Emit("\n", OutputColor.Normal);
            }
            _inProseBlock = false;

            // The table keeps its own indent subtraction: the rows are drawn inside the
            // hanging indent this emits, so the columns have that much less to work with.
            int width = Math.Max(PipeTable.MinColumnWidth * 2, _width() - ProseHangingIndent.Length);

            foreach (TableRowLine row in PipeTable.Layout(table, width,
                                                          MarkdownInlineRenderer.Render, TableRules))
            {
                Emit(ProseHangingIndent, OutputColor.Normal);
                foreach (InlineRun run in row.Segments) Emit(run.Text, _prose(run.Style));
                Emit("\n", OutputColor.Normal);
            }

            // A blank line after, so the next sentence is not read as another row.
            Emit("\n", OutputColor.Normal);
        }

        private void RenderProseLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            // Prose is not engine output, so whatever call was open is closed: the model
            // talking is never something a shell command produced.
            _block.CloseBlock();

            // Open a prose block that follows a tool line with one blank line. A line that is
            // itself blank needs no help, and would otherwise double the gap.
            bool blank = line.Trim('\r', '\n', ' ', '\t').Length == 0;

            if (_lastWriteWasToolLine)
            {
                _lastWriteWasToolLine = false;
                if (!blank) Emit("\n", OutputColor.Normal);
            }

            // ask_caller's heading is an event wearing prose clothing: the run has stopped and
            // is waiting for a person. It gets the question glyph, and the numbered questions
            // that follow hang under it as the block's continuation.
            if (!blank && TranscriptVocabulary.IsNeedsInputHeading(line))
            {
                _inProseBlock = true;
                Emit(TranscriptVocabulary.NeedsInputLine + "\n", OutputColor.Warning);
                return;
            }

            // The model's own words lead with ◆ and hang under it. One diamond per block, not
            // per line: a marker on every line is a bullet list, which says something the
            // model did not. Blank lines inside the block do not end it — a two-paragraph
            // answer is one thing the model said.
            if (!blank)
            {
                // A block that opens with the model's own list marker takes the hanging indent
                // instead of the diamond: "◆ - Use sum()" is two markers for one line, and
                // reads as a nested bullet nobody wrote.
                bool opensWithMarker = !_inProseBlock && MarkdownInlineRenderer.IsListItem(line);

                if (!_inProseBlock && !opensWithMarker)
                {
                    _inProseBlock = true;
                    Emit(ProseLead, OutputColor.Dim);
                }
                else
                {
                    _inProseBlock = true;
                    Emit(ProseHangingIndent, OutputColor.Normal);
                }
            }

            string content = line.TrimEnd('\r');
            bool hasNewline = content.Length > 0 && content[content.Length - 1] == '\n';
            int textEnd = hasNewline ? content.Length - 1 : content.Length;

            // ProseWrap subtracts the block indent itself, so it is handed the width BEFORE
            // the indent is taken off. Subtracting twice — which is what happened while the
            // host owned this — wrapped prose two columns early for no visible reason.
            IReadOnlyList<ProseLine> planned =
                ProseWrap.Plan(content.Substring(0, textEnd), ProseHangingIndent, _width());

            for (int i = 0; i < planned.Count; i++)
            {
                ProseLine planLine = planned[i];

                if (i > 0) Emit("\n", OutputColor.Normal);
                if (planLine.Indent.Length > 0) Emit(planLine.Indent, OutputColor.Normal);

                foreach (InlineRun run in planLine.Runs) Emit(run.Text, _prose(run.Style));
            }

            if (hasNewline) Emit("\n", OutputColor.Normal);
        }

        // ── Code, listings, diffs ────────────────────────────────────────────────

        private void RenderCode(string code, string language, bool nestUnderCall)
        {
            if (string.IsNullOrEmpty(code)) return;

            _lastWriteWasToolLine = false;
            _inProseBlock = false;
            _block.CloseBlock();

            var tokens = SyntaxHighlighter.Highlight(code, language);

            if (!nestUnderCall)
            {
                foreach (var t in tokens) Emit(t.Text, _syntax(t.Kind));
                return;
            }

            // The indent is prefixed at each line start rather than applied to the block,
            // because the highlighter's tokens do not align with lines: a comment or a string
            // can carry its own newline through the middle of one token.
            string indent = new string(' ', TranscriptBlock.OutputIndent);
            bool atLineStart = true;

            foreach (var t in tokens)
            {
                TgAttribute attr = _syntax(t.Kind);
                foreach (string piece in SplitKeepingNewlines(t.Text))
                {
                    bool isBreak = piece.Length == 1 && piece[0] == '\n';
                    if (atLineStart && !isBreak)
                    {
                        Emit(indent, OutputColor.Normal);
                        atLineStart = false;
                    }
                    Emit(piece, attr);
                    if (isBreak) atLineStart = true;
                }
            }
        }

        /// <summary>"a\nb" → "a", "\n", "b", so a line start can be recognised between them.</summary>
        private static IEnumerable<string> SplitKeepingNewlines(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                if (i > start) yield return text.Substring(start, i - start);
                yield return "\n";
                start = i + 1;
            }
            if (start < text.Length) yield return text.Substring(start);
        }

        /// <summary>Lines of a file shown before the rest is summarised.</summary>
        public const int MaxListingLines = 400;

        private void RenderListing(string content, string fullPath, int lineCount)
        {
            string lang = SyntaxHighlighter.LanguageFromExtension(fullPath);

            if (lineCount > MaxListingLines)
            {
                string[] lines = content.Replace("\r\n", "\n").Split('\n');
                string head = string.Join("\n", lines, 0, Math.Min(MaxListingLines, lines.Length));

                RenderCode(head + "\n", lang, nestUnderCall: true);

                // Indented explicitly: painting the listing closed the call block, so the
                // door will not nest this one, and a truncation notice hanging left of the
                // text it truncates reads as a new event rather than as part of the listing.
                Emit(new string(' ', TranscriptBlock.OutputIndent) +
                     $"… ({lineCount - MaxListingLines:N0} more lines — {lineCount:N0} total)\n",
                     OutputColor.Dim);
            }
            else
            {
                RenderCode(content.EndsWith("\n", StringComparison.Ordinal) ? content : content + "\n",
                           lang, nestUnderCall: true);
            }
        }

        private void RenderDiff(string oldContent, string newContent, string path)
        {
            try
            {
                IReadOnlyList<DiffLine> lines = DiffRenderer.Build(oldContent, newContent);
                if (lines.Count == 0) return;

                _lastWriteWasToolLine = true;   // the diff is a tool artefact, not prose
                _inProseBlock = false;
                _block.CloseBlock();

                string notice = DiffPainter.Paint(lines, path, _diffPalette(), Emit);
                if (notice != null) Emit(notice + "\n", OutputColor.Dim);
            }
            catch
            {
                // Diff display is best-effort — never break a successful patch over it.
            }
        }
    }
}
