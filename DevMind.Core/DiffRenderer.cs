// File: DiffRenderer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using DiffPlex.Renderer;

namespace DevMind
{
    /// <summary>
    /// Renders unified diffs using DiffPlex. Replaces hand-rolled DiffHelper for
    /// /diff command output while keeping the existing DiffHelper for patch preview.
    /// </summary>
    public static class DiffRenderer
    {
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
