// File: DiffPainter.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Paints a diff as coloured spans: gutter, marker, and the source line still syntax-
// highlighted, on a tinted background.
//
// The TUI used to colour a diff by looking at each line's first character — green if it
// started with '+', red if '-', blue for "@@", dim otherwise. That reads the rendering and
// guesses back at the meaning, which loses the line numbers the diff engine had already
// computed and cannot tell a deleted line from a source line that happens to begin with a
// minus. It also flattened the code to one colour per line, so a changed line was the one
// place in the transcript where C# stopped looking like C#.
//
// This takes the line model instead and emits spans. It holds no view: the sink is a
// delegate, so the span sequence — what text, which foreground, which background — is
// exactly what a test can assert, which is the only way the colour decisions are checkable
// at all without a terminal.

using System;
using System.Collections.Generic;
using System.Globalization;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;

namespace DevMind
{
    /// <summary>
    /// The colours a diff is painted in. The foreground of the code itself comes from the
    /// syntax highlighter, so it is supplied as a function rather than copied.
    /// </summary>
    public sealed class DiffPalette
    {
        /// <summary>Token foreground, normally the host's VS Code Dark+ mapping.</summary>
        public Func<TokenKind, TgColor> Foreground { get; set; }

        /// <summary>Line-number gutter and the hunk ellipsis.</summary>
        public TgColor Gutter { get; set; }

        /// <summary>Background for unchanged context lines — the transcript's own.</summary>
        public TgColor ContextBg { get; set; }

        /// <summary>Background tint for removed lines.</summary>
        public TgColor RemovedBg { get; set; }

        /// <summary>Background tint for added lines.</summary>
        public TgColor AddedBg { get; set; }
    }

    /// <summary>
    /// Turns a <see cref="DiffLine"/> list into coloured spans. Pure: no view, no document,
    /// no I/O — it calls the sink and returns what it could not fit.
    /// </summary>
    public static class DiffPainter
    {
        /// <summary>Transcript lines one diff may occupy before it is capped.</summary>
        public const int MaxDiffLines = 80;

        /// <summary>Narrowest the line-number gutter ever gets, so short files still line up.</summary>
        public const int MinGutterWidth = 3;

        /// <summary>
        /// Paint <paramref name="lines"/> into <paramref name="sink"/>.
        /// </summary>
        /// <param name="path">Used only to pick the syntax-highlighting language.</param>
        /// <param name="maxLines">Transcript lines to spend; 0 or less is uncapped.</param>
        /// <returns>The cap notice to append, or null when everything fit.</returns>
        public static string Paint(IReadOnlyList<DiffLine> lines, string path, DiffPalette palette,
                                   Action<string, TgAttribute> sink, int maxLines = MaxDiffLines)
        {
            if (lines == null || lines.Count == 0) return null;
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            if (palette == null) throw new ArgumentNullException(nameof(palette));

            string language = SyntaxHighlighter.LanguageFromExtension(path);
            int gutterWidth = GutterWidth(lines);

            var budgets = Budgets(lines, maxLines);

            int hunk = -1;
            int spent = 0;
            int omittedChanged = 0;
            int truncatedHunks = 0;
            bool hunkTruncated = false;

            foreach (DiffLine line in lines)
            {
                if (line.Kind == DiffLineKind.HunkHeader)
                {
                    hunk++;
                    spent = 0;
                    hunkTruncated = false;
                    // The header's numbers are already in the gutter of the lines below it, so
                    // it is drawn as an ellipsis rather than as "@@ -45,7 +45,7 @@" — a break in
                    // the file, which is what it means, instead of an arithmetic statement.
                    // A hunk that starts at line 1 has no break before it to show.
                    bool fromTheTop = hunk == 0 && line.OldNo.GetValueOrDefault(1) <= 1
                                                && line.NewNo.GetValueOrDefault(1) <= 1;
                    if (!fromTheTop)
                        sink(Pad("…", gutterWidth) + "\n", new TgAttribute(palette.Gutter, palette.ContextBg));
                    continue;
                }

                int budget = hunk >= 0 && hunk < budgets.Count ? budgets[hunk] : int.MaxValue;
                if (spent >= budget)
                {
                    if (line.Kind != DiffLineKind.Context) omittedChanged++;
                    if (!hunkTruncated) { hunkTruncated = true; truncatedHunks++; }
                    continue;
                }

                spent++;
                PaintLine(line, gutterWidth, language, palette, sink);
            }

            if (omittedChanged == 0) return null;

            return $"  … {omittedChanged:N0} more changed line{(omittedChanged == 1 ? "" : "s")} " +
                   $"in {truncatedHunks:N0} hunk{(truncatedHunks == 1 ? "" : "s")}";
        }

        /// <summary>
        /// Lines each hunk may spend. An even share, because the alternative — first come,
        /// first served — spends the whole cap on hunk 1 and shows nothing of hunk 2, and the
        /// second hunk is exactly as much of the change as the first.
        /// </summary>
        public static IReadOnlyList<int> Budgets(IReadOnlyList<DiffLine> lines, int maxLines)
        {
            int hunks = 0;
            foreach (DiffLine line in lines)
                if (line.Kind == DiffLineKind.HunkHeader) hunks++;
            if (hunks == 0) hunks = 1;

            var budgets = new List<int>(hunks);
            if (maxLines <= 0)
            {
                for (int i = 0; i < hunks; i++) budgets.Add(int.MaxValue);
                return budgets;
            }

            // The headers come out of the budget too — they are transcript lines like any other.
            int forLines = Math.Max(hunks, maxLines - hunks);
            int share = Math.Max(1, forLines / hunks);
            int remainder = forLines - share * hunks;

            for (int i = 0; i < hunks; i++)
                budgets.Add(share + (i < remainder ? 1 : 0));

            return budgets;
        }

        /// <summary>Gutter width: digits of the largest number in the diff, never below three.</summary>
        public static int GutterWidth(IReadOnlyList<DiffLine> lines)
        {
            int largest = 0;
            foreach (DiffLine line in lines)
            {
                if (line.OldNo.HasValue && line.OldNo.Value > largest) largest = line.OldNo.Value;
                if (line.NewNo.HasValue && line.NewNo.Value > largest) largest = line.NewNo.Value;
            }

            int digits = largest <= 0 ? 1 : (int)Math.Floor(Math.Log10(largest)) + 1;
            return Math.Max(MinGutterWidth, digits);
        }

        private static void PaintLine(DiffLine line, int gutterWidth, string language,
                                      DiffPalette palette, Action<string, TgAttribute> sink)
        {
            TgColor bg = line.Kind == DiffLineKind.Removed ? palette.RemovedBg
                       : line.Kind == DiffLineKind.Added   ? palette.AddedBg
                       : palette.ContextBg;

            // A removed line is numbered in the file it was removed FROM; everything else in
            // the file as it now stands. Numbering a deletion by the new file would point at
            // whatever happens to sit there afterwards.
            int? number = line.Kind == DiffLineKind.Removed ? line.OldNo : line.NewNo;
            string gutter = Pad(number.HasValue ? number.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                                gutterWidth);

            char marker = line.Kind == DiffLineKind.Added ? '+'
                        : line.Kind == DiffLineKind.Removed ? '-'
                        : ' ';

            var gutterAttr = new TgAttribute(palette.Gutter, bg);
            sink(gutter + " " + marker + " ", gutterAttr);

            foreach (SyntaxToken token in Tokenize(line.Text, language))
                sink(token.Text, new TgAttribute(palette.Foreground(token.Kind), bg));

            // The newline carries the line's own tint so the terminal does not paint the break
            // in the previous colour on a rewrapped row.
            sink("\n", new TgAttribute(palette.Gutter, bg));
        }

        // "text" is the highlighter's answer for anything it has no lexer for. Running the
        // generic lexer over it would colour the word "class" inside a log file or a README,
        // which is a confident lie about a file nobody claimed was code.
        private static IEnumerable<SyntaxToken> Tokenize(string text, string language)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<SyntaxToken>();

            if (string.Equals(language, "text", StringComparison.Ordinal))
                return new[] { new SyntaxToken(text, TokenKind.Plain) };

            return SyntaxHighlighter.Highlight(text, language);
        }

        private static string Pad(string s, int width)
            => s.Length >= width ? s : new string(' ', width - s.Length) + s;
    }
}
