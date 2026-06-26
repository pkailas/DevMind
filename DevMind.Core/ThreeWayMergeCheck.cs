// File: ThreeWayMergeCheck.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;
using System.Linq;
using DiffPlex;

namespace DevMind
{
    /// <summary>
    /// Three-way merge gate using DiffPlex. Lives in Core as pure static helpers —
    /// the host implementations decide when to call and how to report conflicts.
    /// </summary>
    public static class ThreeWayMergeCheck
    {
        /// <summary>
        /// Runs a three-way merge between base, proposed, and current content.
        /// Returns a <see cref="MergeCheckResult"/> with either the merged text
        /// or conflict blocks.
        /// </summary>
        /// <param name="baseText">
        /// Content of the file at the time it was last read (from FileContentCache).
        /// If null or equal to currentText, falls back to two-way comparison
        /// (overwrite detection only — NOT a true three-way merge).
        /// </param>
        /// <param name="proposedText">Content the LLM wants to write (our side).</param>
        /// <param name="currentText">Current content on disk (their side).</param>
        /// <returns>Result with merged text or conflicts.</returns>
        public static MergeCheckResult CheckAndMerge(string baseText, string proposedText, string currentText)
        {
            // Normalize line endings for comparison
            string normBase = Normalize(baseText);
            string normProposed = Normalize(proposedText);
            string normCurrent = Normalize(currentText);

            // Fallback detection: when no base cache entry exists, base == current,
            // meaning we cannot distinguish "others changed it" from "nothing changed".
            // This is effectively overwrite detection only — not a true three-way merge.
            // The caller SHOULD log a warning when UsedFallback is true.
            bool usedFallback = string.IsNullOrEmpty(baseText) || string.Equals(normBase, normCurrent, System.StringComparison.Ordinal);

            if (usedFallback)
            {
                // Two-way fallback: no base available so we accept the proposed text directly.
                // This is NOT conflict-free merging — it's blind acceptance with a flag.
                return new MergeCheckResult
                {
                    HasConflicts = false,
                    MergedText = proposedText,
                    UsedFallback = true
                };
            }

            // True three-way merge via DiffPlex
            // API: CreateMerge(baseText, oldText, newText, ...)
            // "oldText" = our changes (proposed), "newText" = their changes (current on disk)
            try
            {
                var differ = new ThreeWayDiffer();
                var merge = differ.CreateMerge(normBase, normProposed, normCurrent,
                    ignoreWhiteSpace: false, ignoreCase: false, chunker: null);

                if (!merge.IsSuccessful && merge.ConflictBlocks != null && merge.ConflictBlocks.Count > 0)
                {
                    // Extract conflict blocks
                    var conflicts = new List<ConflictBlock>();
                    foreach (var block in merge.ConflictBlocks)
                    {
                        // MergedStart is an index into MergedPieces — approximate line number
                        int lineNumber = block.MergedStart + 1; // 1-based

                        conflicts.Add(new ConflictBlock
                        {
                            LineNumber = lineNumber,
                            BaseText = string.Join("\n", block.BasePieces),
                            ProposedText = string.Join("\n", block.OldPieces),  // our side
                            CurrentText = string.Join("\n", block.NewPieces)    // their side
                        });
                    }

                    return new MergeCheckResult
                    {
                        HasConflicts = true,
                        Conflicts = conflicts,
                        UsedFallback = false
                    };
                }

                // No conflicts — rejoin merged pieces
                string mergedText = string.Join("\n", merge.MergedPieces);
                // Preserve original line ending style of proposed text
                if (proposedText.IndexOf("\r\n") >= 0)
                    mergedText = mergedText.Replace("\n", "\r\n");

                return new MergeCheckResult
                {
                    HasConflicts = false,
                    MergedText = mergedText,
                    UsedFallback = false
                };
            }
            catch (System.Exception)
            {
                // If DiffPlex throws (edge cases), fall back to accepting proposed.
                // Safe path — better to accept than to hard-block.
                return new MergeCheckResult
                {
                    HasConflicts = false,
                    MergedText = proposedText,
                    UsedFallback = true
                };
            }
        }

        private static string Normalize(string text)
        {
            if (text == null) return string.Empty;
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// <summary>Truncate text to first line, maxChars for display.</summary>
        public static string Truncate(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            string firstLine = text.Split('\n')[0].Trim();
            if (firstLine.Length <= maxChars) return firstLine;
            return firstLine.Substring(0, maxChars) + "...";
        }
    }
}
