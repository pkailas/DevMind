// File: PipeTable.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Laying out a GFM pipe table as columns.
//
// The system prompt asks the model for "tables for comparisons" and it obliges; the TUI
// then printed the pipe source verbatim, which is the one markdown construct where the raw
// form is actively harder to read than the prose it replaced. A comparison is a thing you
// scan down a column, and there were no columns.
//
// This is different in kind from the inline markdown work. Bold and code are decidable
// inside one line, and prose arrives here one completed line at a time — but a column width
// depends on every row, so a table cannot be rendered until it has ended. Everything below
// is therefore pure and takes the whole table at once: parsing, fitting to a width, wrapping
// inside a cell, and padding. The state machine that decides when a table HAS ended is
// TableBuffer's problem, and the drawing is the host's.
//
// Width is a first-class input rather than an afterthought. The output Editor word-wraps, so
// a laid-out row wider than the view is re-broken by the Editor at whatever column it likes
// and the alignment dissolves — a table that does not fit is not a degraded table, it is
// worse than the source it replaced.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>Column alignment, from the separator row's colons.</summary>
    internal enum TableAlign
    {
        Left,
        Center,
        Right,
    }

    /// <summary>A parsed pipe table: raw cell text, not yet laid out.</summary>
    internal sealed class PipeTableModel
    {
        public string[] Header { get; }
        public TableAlign[] Aligns { get; }
        public List<string[]> Rows { get; }

        public PipeTableModel(string[] header, TableAlign[] aligns, List<string[]> rows)
        {
            Header = header;
            Aligns = aligns;
            Rows   = rows;
        }

        public int ColumnCount => Header.Length;
    }

    /// <summary>The rule characters. Swappable because no box drawing existed here before.</summary>
    internal sealed class TableGlyphs
    {
        public string Horizontal { get; }
        public string Vertical { get; }
        public string Cross { get; }

        private TableGlyphs(string horizontal, string vertical, string cross)
        {
            Horizontal = horizontal;
            Vertical   = vertical;
            Cross      = cross;
        }

        /// <summary>Box drawing. Reads as a table rather than as punctuation.</summary>
        public static readonly TableGlyphs Unicode = new TableGlyphs("─", "│", "┼");

        /// <summary>For a terminal whose font has no box drawing — a row of boxes is unreadable.</summary>
        public static readonly TableGlyphs Ascii = new TableGlyphs("-", "|", "+");
    }

    /// <summary>One laid-out visual line of a table: styled segments, already padded.</summary>
    internal sealed class TableRowLine
    {
        public List<InlineRun> Segments { get; } = new List<InlineRun>();

        /// <summary>The line's text, for width assertions and for plain rules.</summary>
        public string Text
        {
            get
            {
                var sb = new StringBuilder();
                foreach (InlineRun run in Segments) sb.Append(run.Text);
                return sb.ToString();
            }
        }
    }

    /// <summary>Parses and lays out GFM pipe tables. No Terminal.Gui, no view, no I/O.</summary>
    internal static class PipeTable
    {
        /// <summary>Narrowest a column is ever squeezed to. Below this, wrapping is shredding.</summary>
        public const int MinColumnWidth = 3;

        /// <summary>Assumed width before the view can report one (pre-init, or a zero frame).</summary>
        public const int FallbackWidth = 100;

        private const string CellSeparator = " ";   // padding either side of the vertical rule

        /// <summary>
        /// The separator row — what turns a run of pipe lines into a table. A line of pipes
        /// with no separator under it is prose that happens to contain pipes, and swallowing
        /// it would delete text the model wrote.
        /// </summary>
        private static readonly Regex SeparatorRow =
            new Regex(@"^\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?$", RegexOptions.Compiled);

        /// <summary>Whether a line could open or continue a table.</summary>
        public static bool IsPipeLine(string line)
            => line != null && line.TrimStart().StartsWith("|", StringComparison.Ordinal);

        /// <summary>Whether a line is the header/body separator.</summary>
        public static bool IsSeparatorLine(string line)
        {
            if (line == null) return false;
            string trimmed = line.Trim();
            if (trimmed.Length == 0) return false;
            // It must contain a dash; "|||" matches the shape of the regex otherwise.
            return trimmed.IndexOf('-') >= 0 && SeparatorRow.IsMatch(trimmed);
        }

        /// <summary>
        /// Parse a held run of lines. Returns null when they are not a table — which is the
        /// common case for a single pipe line, and must stay cheap and undestructive.
        /// </summary>
        public static PipeTableModel TryParse(IReadOnlyList<string> lines)
        {
            if (lines == null || lines.Count < 2) return null;
            if (!IsPipeLine(lines[0])) return null;
            if (!IsSeparatorLine(lines[1])) return null;

            string[] header = SplitCells(lines[0]);
            string[] alignCells = SplitCells(lines[1]);
            if (header.Length == 0) return null;

            var aligns = new TableAlign[header.Length];
            for (int i = 0; i < header.Length; i++)
                aligns[i] = i < alignCells.Length ? AlignOf(alignCells[i]) : TableAlign.Left;

            var rows = new List<string[]>();
            for (int i = 2; i < lines.Count; i++)
            {
                if (!IsPipeLine(lines[i])) continue;
                string[] cells = SplitCells(lines[i]);

                // GFM: a short row is padded, a long row is truncated to the header's width.
                // Keeping a ragged row would put a cell under no column at all.
                var normalized = new string[header.Length];
                for (int c = 0; c < header.Length; c++)
                    normalized[c] = c < cells.Length ? cells[c] : string.Empty;
                rows.Add(normalized);
            }

            return new PipeTableModel(header, aligns, rows);
        }

        private static TableAlign AlignOf(string cell)
        {
            string s = cell.Trim();
            bool left  = s.StartsWith(":", StringComparison.Ordinal);
            bool right = s.EndsWith(":", StringComparison.Ordinal);
            if (left && right) return TableAlign.Center;
            if (right) return TableAlign.Right;
            return TableAlign.Left;
        }

        /// <summary>
        /// Split a row on its pipes. A pipe inside a code span belongs to the code — the model
        /// writes `a|b` in a cell and means it — and a backslash-escaped pipe is a literal.
        /// </summary>
        public static string[] SplitCells(string line)
        {
            string s = line.Trim();
            if (s.StartsWith("|", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.EndsWith("|", StringComparison.Ordinal) && !s.EndsWith("\\|", StringComparison.Ordinal))
                s = s.Substring(0, s.Length - 1);

            var cells = new List<string>();
            var current = new StringBuilder();
            bool inCode = false;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                if (c == '\\' && i + 1 < s.Length && s[i + 1] == '|')
                {
                    current.Append('|');   // the escape is consumed; the pipe is text
                    i++;
                    continue;
                }

                if (c == '`') { inCode = !inCode; current.Append(c); continue; }

                if (c == '|' && !inCode)
                {
                    cells.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            cells.Add(current.ToString().Trim());
            return cells.ToArray();
        }

        /// <summary>
        /// Lay the table out to fit <paramref name="availableWidth"/>. Every returned line's
        /// text is at most that wide, which is the whole contract: one character over and the
        /// Editor re-wraps the row and the columns are gone.
        /// </summary>
        /// <param name="inline">
        /// The inline-markdown renderer, injected so the layout can measure what will actually
        /// be drawn — "**yes**" is three characters wide, not seven.
        /// </param>
        public static IReadOnlyList<TableRowLine> Layout(
            PipeTableModel table, int availableWidth,
            Func<string, IReadOnlyList<InlineRun>> inline, TableGlyphs glyphs = null)
        {
            if (table == null) return Array.Empty<TableRowLine>();
            glyphs = glyphs ?? TableGlyphs.Unicode;
            if (availableWidth <= 0) availableWidth = FallbackWidth;

            int columns = table.ColumnCount;

            // Every cell as runs, header forced bold. Code spans stay code: bold-and-teal is
            // not a style this palette has, and the teal is the more informative of the two.
            var headerRuns = new List<IReadOnlyList<InlineRun>>();
            foreach (string cell in table.Header)
                headerRuns.Add(Embolden(inline(cell)));

            var bodyRuns = new List<List<IReadOnlyList<InlineRun>>>();
            foreach (string[] row in table.Rows)
            {
                var cells = new List<IReadOnlyList<InlineRun>>();
                foreach (string cell in row) cells.Add(inline(cell));
                bodyRuns.Add(cells);
            }

            int[] widths = FitColumns(headerRuns, bodyRuns, columns, availableWidth, glyphs);

            var lines = new List<TableRowLine>();
            lines.AddRange(RenderRow(headerRuns, widths, table.Aligns, glyphs));
            lines.Add(RenderRule(widths, glyphs));
            foreach (var row in bodyRuns)
                lines.AddRange(RenderRow(row, widths, table.Aligns, glyphs));

            return lines;
        }

        /// <summary>
        /// Natural widths, shrunk widest-first until the row fits.
        /// <para>
        /// Widest-first rather than proportional because of what the columns hold. The wide
        /// column is nearly always the prose one — a reason, a consequence — and it is the one
        /// that survives wrapping, because wrapped prose is still prose. The narrow columns are
        /// labels ("low", "none", a filename), and taking two characters off those to be fair
        /// makes the column you scan unreadable while barely helping the one that needed the
        /// room.
        /// </para>
        /// </summary>
        public static int[] FitColumns(
            List<IReadOnlyList<InlineRun>> header,
            List<List<IReadOnlyList<InlineRun>>> body,
            int columns, int availableWidth, TableGlyphs glyphs)
        {
            var widths = new int[columns];
            for (int c = 0; c < columns; c++)
            {
                widths[c] = Math.Max(1, Width(header[c]));
                foreach (var row in body)
                    if (c < row.Count) widths[c] = Math.Max(widths[c], Width(row[c]));
            }

            int overhead = SeparatorWidth(glyphs) * (columns - 1);
            int budget = Math.Max(columns * MinColumnWidth, availableWidth - overhead);

            int total = 0;
            foreach (int w in widths) total += w;

            while (total > budget)
            {
                int widest = 0;
                for (int c = 1; c < columns; c++)
                    if (widths[c] > widths[widest]) widest = c;

                if (widths[widest] <= MinColumnWidth) break;   // nothing left to give
                widths[widest]--;
                total--;
            }

            return widths;
        }

        private static int SeparatorWidth(TableGlyphs glyphs)
            => CellSeparator.Length * 2 + glyphs.Vertical.Length;

        private static int Width(IReadOnlyList<InlineRun> runs)
        {
            int n = 0;
            foreach (InlineRun run in runs) n += run.Text.Length;
            return n;
        }

        private static IReadOnlyList<InlineRun> Embolden(IReadOnlyList<InlineRun> runs)
        {
            var bold = new List<InlineRun>(runs.Count);
            foreach (InlineRun run in runs)
                bold.Add(run.Style == InlineTextStyle.InlineCode
                    ? run
                    : new InlineRun(run.Text, InlineTextStyle.Bold));
            return bold;
        }

        private static List<TableRowLine> RenderRow(
            List<IReadOnlyList<InlineRun>> cells, int[] widths, TableAlign[] aligns, TableGlyphs glyphs)
        {
            int columns = widths.Length;

            // Wrap every cell first: the row is as tall as its tallest cell.
            var wrapped = new List<List<List<InlineRun>>>();
            int height = 1;
            for (int c = 0; c < columns; c++)
            {
                var cell = c < cells.Count ? cells[c] : (IReadOnlyList<InlineRun>)Array.Empty<InlineRun>();
                List<List<InlineRun>> cellLines = WrapRuns(cell, widths[c]);
                wrapped.Add(cellLines);
                height = Math.Max(height, cellLines.Count);
            }

            var result = new List<TableRowLine>(height);
            for (int line = 0; line < height; line++)
            {
                var row = new TableRowLine();
                for (int c = 0; c < columns; c++)
                {
                    if (c > 0)
                    {
                        row.Segments.Add(new InlineRun(CellSeparator, InlineTextStyle.Normal));
                        row.Segments.Add(new InlineRun(glyphs.Vertical, InlineTextStyle.Normal));
                        row.Segments.Add(new InlineRun(CellSeparator, InlineTextStyle.Normal));
                    }

                    List<InlineRun> content = line < wrapped[c].Count
                        ? wrapped[c][line]
                        : new List<InlineRun>();

                    AppendPadded(row.Segments, content, widths[c],
                                 c < aligns.Length ? aligns[c] : TableAlign.Left);
                }
                result.Add(row);
            }

            return result;
        }

        private static TableRowLine RenderRule(int[] widths, TableGlyphs glyphs)
        {
            var rule = new TableRowLine();
            for (int c = 0; c < widths.Length; c++)
            {
                if (c > 0)
                {
                    rule.Segments.Add(new InlineRun(glyphs.Horizontal, InlineTextStyle.Normal));
                    rule.Segments.Add(new InlineRun(glyphs.Cross, InlineTextStyle.Normal));
                    rule.Segments.Add(new InlineRun(glyphs.Horizontal, InlineTextStyle.Normal));
                }
                rule.Segments.Add(new InlineRun(Repeat(glyphs.Horizontal, widths[c]), InlineTextStyle.Normal));
            }
            return rule;
        }

        private static string Repeat(string s, int count)
        {
            var sb = new StringBuilder(s.Length * count);
            for (int i = 0; i < count; i++) sb.Append(s);
            return sb.ToString();
        }

        private static void AppendPadded(
            List<InlineRun> into, List<InlineRun> content, int width, TableAlign align)
        {
            int used = 0;
            foreach (InlineRun run in content) used += run.Text.Length;
            int pad = Math.Max(0, width - used);

            int left = align == TableAlign.Right ? pad
                     : align == TableAlign.Center ? pad / 2
                     : 0;
            int right = pad - left;

            if (left > 0) into.Add(new InlineRun(new string(' ', left), InlineTextStyle.Normal));
            foreach (InlineRun run in content) into.Add(run);
            if (right > 0) into.Add(new InlineRun(new string(' ', right), InlineTextStyle.Normal));
        }

        /// <summary>
        /// Word-wrap a cell's runs to a column width, preserving each character's style.
        /// <para>
        /// A token longer than the column is hard-broken rather than truncated. A comparison
        /// table with a silently shortened cell is worse than an ugly one: the reader has no
        /// way to know a value was cut, and the whole point of the table is that the values
        /// can be compared.
        /// </para>
        /// </summary>
        public static List<List<InlineRun>> WrapRuns(IReadOnlyList<InlineRun> runs, int width)
        {
            var lines = new List<List<InlineRun>>();
            if (width < 1) width = 1;

            // Flatten to styled characters: a wrap point can fall inside a run, and tracking
            // that across run boundaries is the whole difficulty.
            var chars = new List<(char C, InlineTextStyle S)>();
            foreach (InlineRun run in runs)
                foreach (char c in run.Text)
                    chars.Add((c, run.Style));

            if (chars.Count == 0)
            {
                lines.Add(new List<InlineRun>());
                return lines;
            }

            var current = new List<(char C, InlineTextStyle S)>();
            int i2 = 0;
            while (i2 < chars.Count)
            {
                // Measure the next word (to the next space) to decide whether it fits.
                int wordEnd = i2;
                while (wordEnd < chars.Count && chars[wordEnd].C != ' ') wordEnd++;
                int wordLen = wordEnd - i2;

                if (wordLen == 0)   // a space
                {
                    if (current.Count > 0 && current.Count < width) current.Add(chars[i2]);
                    i2++;
                    continue;
                }

                if (wordLen > width)
                {
                    // Longer than the column will ever be: fill this line and hard-break.
                    while (i2 < wordEnd)
                    {
                        if (current.Count == width) { lines.Add(Coalesce(current)); current = new List<(char, InlineTextStyle)>(); }
                        current.Add(chars[i2]);
                        i2++;
                    }
                    continue;
                }

                if (current.Count + wordLen > width)
                {
                    lines.Add(Coalesce(TrimTrailingSpaces(current)));
                    current = new List<(char, InlineTextStyle)>();
                }

                for (; i2 < wordEnd; i2++) current.Add(chars[i2]);
            }

            lines.Add(Coalesce(TrimTrailingSpaces(current)));
            return lines;
        }

        private static List<(char C, InlineTextStyle S)> TrimTrailingSpaces(List<(char C, InlineTextStyle S)> chars)
        {
            int end = chars.Count;
            while (end > 0 && chars[end - 1].C == ' ') end--;
            return chars.GetRange(0, end);
        }

        private static List<InlineRun> Coalesce(List<(char C, InlineTextStyle S)> chars)
        {
            var runs = new List<InlineRun>();
            if (chars.Count == 0) return runs;

            var sb = new StringBuilder();
            InlineTextStyle style = chars[0].S;
            foreach (var (c, s) in chars)
            {
                if (s != style)
                {
                    runs.Add(new InlineRun(sb.ToString(), style));
                    sb.Clear();
                    style = s;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) runs.Add(new InlineRun(sb.ToString(), style));
            return runs;
        }
    }
}
