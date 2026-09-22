// File: DiffRenderer.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Two shapes of the same diff, for two different readers.
//
// RenderUnifiedDiff produces TEXT, and that text is what the model, the CLI and the headless
// transcript read. It is not to be changed — it is DiffPlex's own unidiff renderer, and an
// agent that has learned to read one dialect of unified diff should not have to learn a
// second because a terminal wanted colours.
//
// Build produces a LINE MODEL, for a renderer that paints rather than prints. The TUI used to
// take the text and re-parse it by first character, which threw away the line numbers the
// diff engine had already computed and made a `-` at the start of a source line
// indistinguishable from a deletion. The model keeps what the text throws away.

using System;
using System.Collections.Generic;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DiffPlex.Renderer;

namespace DevMind
{
    /// <summary>What a line in a diff is.</summary>
    public enum DiffLineKind
    {
        /// <summary>Unchanged, shown for context around a change.</summary>
        Context,
        /// <summary>Present only in the new text.</summary>
        Added,
        /// <summary>Present only in the old text.</summary>
        Removed,
        /// <summary>The <c>@@ -a,b +c,d @@</c> boundary between two hunks.</summary>
        HunkHeader,
    }

    /// <summary>
    /// One line of a diff, with the line numbers the diff engine knew. <see cref="Text"/> is the
    /// source line itself — no leading <c>+</c>, <c>-</c> or space, because the marker is a
    /// rendering choice and a renderer that has to strip it back off is re-parsing again.
    /// </summary>
    public readonly struct DiffLine
    {
        public readonly DiffLineKind Kind;

        /// <summary>1-based line number in the old text; null for an added line.</summary>
        public readonly int? OldNo;

        /// <summary>1-based line number in the new text; null for a removed line.</summary>
        public readonly int? NewNo;

        public readonly string Text;

        public DiffLine(DiffLineKind kind, int? oldNo, int? newNo, string text)
        {
            Kind  = kind;
            OldNo = oldNo;
            NewNo = newNo;
            Text  = text ?? string.Empty;
        }
    }

    /// <summary>
    /// Renders unified diffs using DiffPlex. Replaces hand-rolled DiffHelper for
    /// /diff command output while keeping the existing DiffHelper for patch preview.
    /// </summary>
    public static class DiffRenderer
    {
        /// <summary>Unchanged lines kept either side of a change, as unified diffs conventionally do.</summary>
        public const int DefaultContextRadius = 3;

        /// <summary>
        /// The diff as a list of lines with their old/new numbers, grouped into hunks with
        /// <paramref name="contextRadius"/> unchanged lines either side. Returns an empty list
        /// when the two texts are identical.
        /// <para>
        /// The numbers are computed by walking the pieces — old advances on unchanged and
        /// removed, new on unchanged and added — rather than read from DiffPlex's
        /// <c>Position</c>, whose meaning differs between the inline and side-by-side models.
        /// A counter that is obviously right beats a field that is right in one of two models.
        /// </para>
        /// </summary>
        public static IReadOnlyList<DiffLine> Build(string oldText, string newText,
                                                    int contextRadius = DefaultContextRadius)
        {
            string normOld = Normalize(oldText);
            string normNew = Normalize(newText);

            if (string.Equals(normOld, normNew, StringComparison.Ordinal))
                return Array.Empty<DiffLine>();

            DiffPaneModel model;
            try
            {
                model = InlineDiffBuilder.Diff(normOld, normNew);
            }
            catch
            {
                // Same posture as the text renderer: a diff is a convenience, and a DiffPlex
                // edge case must not take down the caller that was only trying to show one.
                return Array.Empty<DiffLine>();
            }

            // Pass 1 — every line of the file, numbered.
            var all = new List<DiffLine>(model.Lines.Count);
            int oldNo = 0, newNo = 0;
            foreach (DiffPiece piece in model.Lines)
            {
                switch (piece.Type)
                {
                    case ChangeType.Inserted:
                        all.Add(new DiffLine(DiffLineKind.Added, null, ++newNo, piece.Text));
                        break;
                    case ChangeType.Deleted:
                        all.Add(new DiffLine(DiffLineKind.Removed, ++oldNo, null, piece.Text));
                        break;
                    case ChangeType.Imaginary:
                        // A placeholder for alignment in the side-by-side model; it is not a
                        // line of either file and must not advance either counter.
                        break;
                    default:
                        all.Add(new DiffLine(DiffLineKind.Context, ++oldNo, ++newNo, piece.Text));
                        break;
                }
            }

            return ToHunks(all, contextRadius);
        }

        // Keep only the changes and `radius` unchanged lines either side, merging runs that
        // overlap, and put a header in front of each surviving run. Without this the model is
        // the whole file, and a one-line edit to a 2,000-line file is a 2,000-line diff.
        private static IReadOnlyList<DiffLine> ToHunks(List<DiffLine> all, int radius)
        {
            if (radius < 0) radius = 0;

            var keep = new bool[all.Count];
            bool any = false;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Kind == DiffLineKind.Context) continue;
                any = true;
                int from = Math.Max(0, i - radius);
                int to   = Math.Min(all.Count - 1, i + radius);
                for (int j = from; j <= to; j++) keep[j] = true;
            }

            if (!any) return Array.Empty<DiffLine>();

            var result = new List<DiffLine>();
            int k = 0;
            while (k < all.Count)
            {
                if (!keep[k]) { k++; continue; }

                int start = k;
                while (k < all.Count && keep[k]) k++;
                int end = k - 1;   // inclusive

                result.Add(HunkHeader(all, start, end));
                for (int j = start; j <= end; j++) result.Add(all[j]);
            }

            return result;
        }

        private static DiffLine HunkHeader(List<DiffLine> all, int start, int end)
        {
            int oldStart = 0, oldCount = 0, newStart = 0, newCount = 0;
            for (int j = start; j <= end; j++)
            {
                DiffLine line = all[j];
                if (line.OldNo.HasValue)
                {
                    if (oldCount == 0) oldStart = line.OldNo.Value;
                    oldCount++;
                }
                if (line.NewNo.HasValue)
                {
                    if (newCount == 0) newStart = line.NewNo.Value;
                    newCount++;
                }
            }

            string text = $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@";
            return new DiffLine(DiffLineKind.HunkHeader, oldCount > 0 ? oldStart : (int?)null,
                                newCount > 0 ? newStart : (int?)null, text);
        }

        private static string Normalize(string text)
            => (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");

        /// <summary>
        /// Renders a unified (unidiff) diff between oldText and newText using DiffPlex.
        /// Capped at maxOutputLines (default 200). Returns "no changes" message when identical.
        /// </summary>
        public static string RenderUnifiedDiff(string filename, string oldText, string newText, int maxOutputLines = 200)
        {
            if (string.IsNullOrEmpty(filename)) filename = "unknown";

            // Normalize line endings
            oldText = oldText ?? string.Empty;
            newText = newText ?? string.Empty;
            string normOld = oldText.Replace("\r\n", "\n").Replace("\r", "\n");
            string normNew = newText.Replace("\r\n", "\n").Replace("\r", "\n");

            if (string.Equals(normOld, normNew, System.StringComparison.Ordinal))
                return $"DIFF: No changes detected in {filename}.";

            string unidiff;
            try
            {
                unidiff = UnidiffRenderer.GenerateUnidiff(
                    oldText: normOld,
                    newText: normNew,
                    oldFileName: $"{filename} (original)",
                    newFileName: $"{filename} (current)");
            }
            catch
            {
                // DiffPlex edge case — fall back to line count summary
                int oldLines = normOld.Split('\n').Length;
                int newLines = normNew.Split('\n').Length;
                return $"DIFF: {filename} changed ({oldLines} → {newLines} lines). Diff rendering failed.";
            }

            // Cap output
            string[] lines = unidiff.Split('\n');
            if (lines.Length <= maxOutputLines)
                return unidiff.TrimEnd('\r', '\n');

            // Truncate
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < maxOutputLines; i++)
                sb.AppendLine(lines[i]);
            sb.AppendLine($"... (truncated at {maxOutputLines} lines — use READ for full file)");
            return sb.ToString().TrimEnd('\r', '\n');
        }

        /// <summary>
        /// Overload accepting pre-split line arrays (for compatibility with existing callers).
        /// </summary>
        public static string RenderUnifiedDiff(string filename, string[] oldLines, string[] newLines, int maxOutputLines = 200)
        {
            string oldText = string.Join("\n", oldLines);
            string newText = string.Join("\n", newLines);
            return RenderUnifiedDiff(filename, oldText, newText, maxOutputLines);
        }
    }
}
