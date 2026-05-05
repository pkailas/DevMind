// File: DiffHelper.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Text;

namespace DevMind
{
    /// <summary>
    /// Produces a simple unified-style diff between two sets of lines.
    /// Used by the DIFF directive to show per-conversation file changes.
    /// </summary>
    public static class DiffHelper
    {
        private const int Context    = 3;
        private const int MaxOutput  = 200;
        private const long LcsSizeLimit = 2_000_000L; // m*n threshold for LCS vs. positional diff

        public static string GenerateUnifiedDiff(string filename, string[] oldLines, string[] newLines)
        {
            var edits = ComputeEditScript(oldLines, newLines);

            var sb = new StringBuilder();
            sb.AppendLine($"--- {filename} (original)");
            sb.AppendLine($"+++ {filename} (current)");

            // Annotate each edit with absolute 1-based old/new line numbers
            var annotated = new List<(char op, string text, int oldLn, int newLn)>();
            int oldLn = 1, newLn = 1;
            foreach (var (op, text) in edits)
            {
                if      (op == ' ') { annotated.Add((op, text, oldLn, newLn)); oldLn++; newLn++; }
                else if (op == '-') { annotated.Add((op, text, oldLn,      0)); oldLn++; }
                else                { annotated.Add((op, text,      0, newLn)); newLn++; }
            }

            // Mark changed indices
            bool[] changed = new bool[annotated.Count];
            bool anyChange = false;
            for (int i = 0; i < annotated.Count; i++)
            {
                changed[i] = annotated[i].op != ' ';
                if (changed[i]) anyChange = true;
            }

            if (!anyChange)
                return $"DIFF: No changes detected in {filename}.";

            int outputLines = 2; // header lines already appended
            bool truncated  = false;

            int idx = 0;
            while (idx < annotated.Count && !truncated)
            {
                if (!changed[idx]) { idx++; continue; }

                // Expand hunk: merge consecutive change regions within 2*Context gap
                int hunkStart = Math.Max(0, idx - Context);
                int hunkEnd   = idx;
                while (hunkEnd < annotated.Count)
                {
                    int nextChange = hunkEnd + 1;
                    while (nextChange < annotated.Count && !changed[nextChange]) nextChange++;
                    if (nextChange < annotated.Count && nextChange - hunkEnd <= 2 * Context + 1)
                        hunkEnd = nextChange;
                    else
                        break;
                }
                hunkEnd = Math.Min(annotated.Count - 1, hunkEnd + Context);

                // Hunk separator
                if (outputLines < MaxOutput) { sb.AppendLine("---"); outputLines++; }

                for (int i = hunkStart; i <= hunkEnd; i++)
                {
                    if (outputLines >= MaxOutput) { truncated = true; break; }
                    var (op, text, oln, nln) = annotated[i];
                    string lineNum = (op == '+') ? nln.ToString() : oln.ToString();
                    sb.AppendLine($"{op} {lineNum,5}: {text}");
                    outputLines++;
                }

                idx = hunkEnd + 1;
            }

            string result = sb.ToString().TrimEnd('\r', '\n');
            if (truncated)
                result += $"\n... (truncated at {MaxOutput} lines — use READ for full file)";
            return result;
        }

        // ── Edit script computation ───────────────────────────────────────────────

        private static List<(char op, string text)> ComputeEditScript(string[] a, string[] b)
        {
            int m = a.Length, n = b.Length;

            // For very large file pairs, fall back to positional comparison to avoid OOM
            if ((long)m * n > LcsSizeLimit)
                return ComputePositionalEditScript(a, b);

            // Standard LCS DP (bottom-up, suffix direction)
            int[,] dp = new int[m + 1, n + 1];
            for (int i = m - 1; i >= 0; i--)
                for (int j = n - 1; j >= 0; j--)
                    dp[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                        ? dp[i + 1, j + 1] + 1
                        : Math.Max(dp[i + 1, j], dp[i, j + 1]);

            // Backtrack
            var result = new List<(char, string)>(m + n);
            int ai = 0, bi = 0;
            while (ai < m && bi < n)
            {
                if (string.Equals(a[ai], b[bi], StringComparison.Ordinal))
                    { result.Add((' ', a[ai])); ai++; bi++; }
                else if (dp[ai + 1, bi] >= dp[ai, bi + 1])
                    { result.Add(('-', a[ai])); ai++; }
                else
                    { result.Add(('+', b[bi])); bi++; }
            }
            while (ai < m) { result.Add(('-', a[ai])); ai++; }
            while (bi < n) { result.Add(('+', b[bi])); bi++; }

            return result;
        }

        private static List<(char op, string text)> ComputePositionalEditScript(string[] a, string[] b)
        {
            var result = new List<(char, string)>(Math.Max(a.Length, b.Length));
            int maxLen = Math.Max(a.Length, b.Length);
            for (int i = 0; i < maxLen; i++)
            {
                if (i < a.Length && i < b.Length)
                {
                    if (string.Equals(a[i], b[i], StringComparison.Ordinal))
                        result.Add((' ', a[i]));
                    else
                        { result.Add(('-', a[i])); result.Add(('+', b[i])); }
                }
                else if (i < a.Length) result.Add(('-', a[i]));
                else                   result.Add(('+', b[i]));
            }
            return result;
        }
    }
}
