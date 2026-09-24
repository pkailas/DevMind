// File: ProseWrap.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Wrapping the model's prose here, instead of letting the Editor do it.
//
// The Editor soft-wraps anything wider than the view, and it wraps to column zero. For a
// paragraph that is merely untidy; for a list it destroys the shape — a bullet's second line
// starts at the left margin, level with the marker, so a three-item list reads as six
// unrelated lines and the ◆ block loses its own gutter the moment a sentence is long.
//
// Brief 09 accepted that as an Editor limitation, which it was at the time. It stopped being
// one when 14 built a width-aware wrapper that preserves run styles and a width read that is
// taken at layout time. This is the same wrapper pointed at prose: decide the break points
// before the Editor gets the chance, and give every continuation line the indent that keeps
// the block's shape.
//
// Pure, so the break points and the indents are assertions rather than something to squint
// at in a terminal.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>One laid-out prose line: what to indent it by, and what to draw.</summary>
    internal readonly struct ProseLine
    {
        /// <summary>Spaces to emit before the runs. Empty on the first line — the caller
        /// has already drawn the block's ◆ or its hanging indent there.</summary>
        public readonly string Indent;

        public readonly IReadOnlyList<InlineRun> Runs;

        public ProseLine(string indent, IReadOnlyList<InlineRun> runs)
        {
            Indent = indent ?? string.Empty;
            Runs = runs ?? Array.Empty<InlineRun>();
        }
    }

    /// <summary>Breaks one completed prose line to the view width. Pure.</summary>
    internal static class ProseWrap
    {
        /// <summary>Assumed width when the view cannot report one. Same rule as tables.</summary>
        public const int FallbackWidth = PipeTable.FallbackWidth;

        /// <summary>Narrowest text column worth wrapping into.</summary>
        public const int MinTextWidth = 20;

        // "- ", "* ", "+ ", "1. ", "12) " — with any leading indentation of a nested item.
        private static readonly Regex ListMarker =
            new Regex(@"^(?<lead>\s*)(?<marker>[-*+]|\d{1,3}[.)])\s+", RegexOptions.Compiled);

        /// <summary>
        /// Lay one completed prose line out.
        /// </summary>
        /// <param name="line">The line, without its trailing newline.</param>
        /// <param name="blockIndent">
        /// The prose block's hanging indent — what a continuation line starts with. The same
        /// width as the ◆ lead, so continuations sit under the first line's text.
        /// </param>
        /// <param name="width">Columns available in the view.</param>
        public static IReadOnlyList<ProseLine> Plan(string line, string blockIndent, int width)
        {
            blockIndent = blockIndent ?? string.Empty;
            string text = (line ?? string.Empty).TrimEnd('\r', '\n');

            IReadOnlyList<InlineRun> runs = MarkdownInlineRenderer.Render(text);
            var single = new[] { new ProseLine(string.Empty, runs) };

            if (runs.Count == 0) return single;

            // A heading is short by nature and is the one line whose shape is the message.
            // Breaking it mid-phrase to satisfy a narrow window loses more than it saves.
            if (runs[0].Style == InlineTextStyle.Heading) return single;

            if (width <= 0) width = FallbackWidth;

            // A list continuation hangs under the item's TEXT, not under its marker — that is
            // the whole difference between a wrapped list and six unrelated lines.
            string continuation = blockIndent + new string(' ', MarkerWidth(text));

            // One width for every line of the block, and it is the narrower one. The first
            // line could fit a few more characters, but a wrap computed against two different
            // widths is a wrap that is right on line one and wrong on line two — and for a
            // paragraph, which has no marker, the two widths are the same anyway.
            int textWidth = width - continuation.Length;
            if (textWidth < MinTextWidth) textWidth = MinTextWidth;

            int used = 0;
            foreach (InlineRun run in runs) used += run.Text.Length;
            if (used <= textWidth) return single;

            List<List<InlineRun>> wrapped = PipeTable.WrapRuns(runs, textWidth);

            var planned = new List<ProseLine>(wrapped.Count);
            for (int i = 0; i < wrapped.Count; i++)
                planned.Add(new ProseLine(i == 0 ? string.Empty : continuation, wrapped[i]));

            return planned;
        }

        /// <summary>
        /// Columns a list marker occupies, including its trailing space — "- " is 2, "12. "
        /// is 4, and a nested item's own leading spaces count too. Zero when the line is not
        /// a list item.
        /// </summary>
        public static int MarkerWidth(string line)
        {
            if (string.IsNullOrEmpty(line)) return 0;

            Match m = ListMarker.Match(line);
            return m.Success ? m.Value.Length : 0;
        }
    }
}
