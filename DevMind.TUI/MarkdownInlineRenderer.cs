// File: MarkdownInlineRenderer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Pure inline-markdown renderer for one completed prose line. Input: the line's
// text (no trailing newline). Output: an ordered list of (text, style) runs where
// markers are CONSUMED — "# Title" yields one Heading run "Title", "**bold**"
// yields one Bold run "bold", "`code`" one InlineCode run "code".
//
// Constructs (deliberately minimal — no Markdig, no multi-line state):
//   * Heading: 1-6 leading '#' (after optional whitespace) followed by ONE space;
//     the hashes + that space are consumed and the remainder is one Heading run,
//     with bold/code spans parsed inside it.
//   * Bold: a same-line ** … ** pair; both markers consumed, the enclosed text
//     rendered as a Bold run (nested code is still code).
//   * Inline code: a same-line ` … ` pair; both markers consumed, the enclosed
//     text is LITERAL (no parsing inside) — so `**x**` renders as code, not bold.
//
// Unmatched markers stay literal: a lone '*', a lone '**', or a lone backtick
// prints exactly as it arrived. Backslash escaping (\*, \`) is deliberately OUT
// of scope — the backslash prints literally.
//
// No Terminal.Gui dependency: the style is the InlineTextStyle enum below, which
// the host maps to Terminal.Gui.Drawing.Attribute. This keeps the renderer
// directly unit-testable.

using System;
using System.Collections.Generic;

namespace DevMind
{
    internal enum InlineTextStyle
    {
        Normal,
        Bold,
        InlineCode,
        Heading,
    }

    internal readonly struct InlineRun
    {
        public string Text { get; }
        public InlineTextStyle Style { get; }

        public InlineRun(string text, InlineTextStyle style)
        {
            Text = text;
            Style = style;
        }
    }

    internal static class MarkdownInlineRenderer
    {
        /// <summary>Render one completed prose line (without its trailing newline)
        /// into ordered runs with markers consumed.</summary>
        internal static List<InlineRun> Render(string line)
        {
            var runs = new List<InlineRun>();
            if (string.IsNullOrEmpty(line)) return runs;

            int i = 0;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;

            int hashes = 0;
            while (i + hashes < line.Length && line[i + hashes] == '#') hashes++;

            // 1-6 hashes then a space = heading. Consume hashes + the single space.
            if (hashes >= 1 && hashes <= 6 && i + hashes < line.Length && line[i + hashes] == ' ')
            {
                if (i > 0) runs.Add(new InlineRun(line.Substring(0, i), InlineTextStyle.Normal));
                RenderSpan(line, i + hashes + 1, line.Length, InlineTextStyle.Heading, runs);
                return runs;
            }

            RenderSpan(line, 0, line.Length, InlineTextStyle.Normal, runs);
            return runs;
        }

        // Scan [start,end) of text for same-line spans; plain text (including unmatched
        // markers) lands in a run with baseStyle.
        private static void RenderSpan(string text, int start, int end, InlineTextStyle baseStyle, List<InlineRun> runs)
        {
            int i = start;
            while (i < end)
            {
                char c = text[i];

                if (c == '`')
                {
                    int close = text.IndexOf('`', i + 1);
                    if (close >= 0 && close < end)
                    {
                        FlushBase(runs, text, start, i, baseStyle);
                        runs.Add(new InlineRun(text.Substring(i + 1, close - i - 1), InlineTextStyle.InlineCode));
                        i = close + 1;
                        start = i;
                        continue;
                    }
                    // Unmatched backtick — literal.
                    i++;
                    continue;
                }

                if (c == '*' && i + 1 < end && text[i + 1] == '*')
                {
                    int close = IndexOfPair(text, "**", i + 2, end);
                    if (close >= 0)
                    {
                        FlushBase(runs, text, start, i, baseStyle);
                        RenderSpan(text, i + 2, close, InlineTextStyle.Bold, runs);
                        i = close + 2;
                        start = i;
                        continue;
                    }
                    // Unmatched "**" — literal.
                    i++;
                    continue;
                }

                i++;
            }

            FlushBase(runs, text, start, i, baseStyle);
        }

        private static int IndexOfPair(string text, string pair, int from, int end)
        {
            for (int j = from; j + 1 < end; j++)
                if (text[j] == pair[0] && text[j + 1] == pair[1]) return j;
            return -1;
        }

        private static void FlushBase(List<InlineRun> runs, string text, int start, int end, InlineTextStyle style)
        {
            if (end > start) runs.Add(new InlineRun(text.Substring(start, end - start), style));
        }
    }
}
