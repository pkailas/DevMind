// File: ThreeWayMergeCheck.cs  v1.2
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// v1.1 (H-05): the result says HOW it was reached (MergeMode). base == current is the clean,
// normal case and is no longer reported as a fallback; MergeReport is the one place that
// turns a genuine fallback into a transcript label and a trace event.
//
// v1.2 (H-69): CreateMerge was called with chunker: null, which DiffPlex 1.9.0 rejects
// (ArgumentNullException) — so since the merge gate was added (3948341) no merge ever ran:
// every real divergence took the proposed text unmerged and no conflict was ever detected.
// The line chunker is passed now. MergeReport also owns the headless conflict refusal.

using System;
using System.Collections.Generic;
using System.Linq;
using DiffPlex;
using DiffPlex.Chunkers;

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

            // No base cache entry: nothing to merge against, so this is overwrite detection
            // only — a genuine fallback, reported as such by the caller.
            if (string.IsNullOrEmpty(baseText))
            {
                return new MergeCheckResult
                {
                    HasConflicts = false,
                    MergedText = proposedText,
                    Mode = MergeMode.NoBase
                };
            }

            // Base == current: nobody else changed the file since it was read, so the proposed
            // text IS the merge. The normal case — not a fallback, no label (H-05).
            if (string.Equals(normBase, normCurrent, StringComparison.Ordinal))
            {
                return new MergeCheckResult
                {
                    HasConflicts = false,
                    MergedText = proposedText,
                    Mode = MergeMode.CleanNoDivergence
                };
            }

            // True three-way merge via DiffPlex
            // API: CreateMerge(baseText, oldText, newText, ...)
            // "oldText" = our changes (proposed), "newText" = their changes (current on disk)
            try
            {
                DiffEngineHookForTest?.Invoke();
                var differ = new ThreeWayDiffer();
                var merge = differ.CreateMerge(normBase, normProposed, normCurrent,
                    ignoreWhiteSpace: false, ignoreCase: false, chunker: LineChunker.Instance);

                if (!merge.IsSuccessful && merge.ConflictBlocks != null && merge.ConflictBlocks.Count > 0)
                {
                    // Extract conflict blocks
                    var conflicts = new List<ConflictBlock>();
                    foreach (var block in merge.ConflictBlocks)
                    {
                        // MergedStart is an index into MergedPieces — approximate line number
                        int lineNumber = block.MergedStart + 1; // 1-based

                        var where = block.OriginalBlock;
                        conflicts.Add(new ConflictBlock
                        {
                            LineNumber = lineNumber,
                            BaseLine = (where?.BaseStart ?? block.MergedStart) + 1,
                            ProposedLine = (where?.OldStart ?? block.MergedStart) + 1,
                            CurrentLine = (where?.NewStart ?? block.MergedStart) + 1,
                            BaseText = string.Join("\n", block.BasePieces),
                            ProposedText = string.Join("\n", block.OldPieces),  // our side
                            CurrentText = string.Join("\n", block.NewPieces)    // their side
                        });
                    }

                    return new MergeCheckResult
                    {
                        HasConflicts = true,
                        Conflicts = conflicts,
                        Mode = MergeMode.ThreeWay
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
                    Mode = MergeMode.ThreeWay
                };
            }
            catch (Exception ex)
            {
                // If DiffPlex throws (edge cases), fall back to accepting proposed.
                // Safe path — better to accept than to hard-block — and reported as a fallback.
                return new MergeCheckResult
                {
                    HasConflicts = false,
                    MergedText = proposedText,
                    Mode = MergeMode.DiffEngineFailed,
                    DiffEngineError = ex.GetType().Name
                };
            }
        }

        /// <summary>Test seam: invoked just before DiffPlex runs; a test makes it throw to force
        /// the <see cref="MergeMode.DiffEngineFailed"/> path. Null in production.</summary>
        internal static Action DiffEngineHookForTest;

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

    /// <summary>H-69: a headless write refused because it conflicts with a change made on disk since the
    /// file was read. The message is <see cref="MergeReport.ConflictRefusal"/>.</summary>
    public sealed class MergeConflictRefusedException : InvalidOperationException
    {
        public MergeConflictRefusedException(string message) : base(message) { }
    }

    /// <summary>
    /// What a host says about a merge result: the transcript label and the trace event. One place,
    /// so every save / append / patch path in every host reports a fallback the same way.
    /// </summary>
    public static class MergeReport
    {
        /// <summary>The trace event name — a stable constant (it was once the message text).</summary>
        public const string FallbackTraceEvent = "merge_fallback";

        /// <summary>H-69: the journal kind and trace event for a write refused on a merge conflict
        /// in a headless job.</summary>
        public const string ConflictRefusedEvent = "merge_conflict_refused";

        /// <summary>Starts every headless conflict refusal — lets a caller tell it from other write failures.</summary>
        public const string ConflictRefusedMarker = "[MERGE-CONFLICT-REFUSED]";

        /// <summary>The instruction that ends every refusal.</summary>
        public const string ReReadInstruction =
            "The file changed since you read it. Re-read it and redo your edit against the current content.";

        private const int ConflictLinesShown = 4;
        private const int ConflictLineWidth = 160;

        /// <summary>
        /// H-69: the tool error for a write refused on a merge conflict in a headless job — every
        /// conflict block with base / proposed / on-disk text (a few numbered lines each), then the
        /// re-read instruction. Nothing was written and no state is kept: the next write is
        /// evaluated fresh.
        /// </summary>
        public static string ConflictRefusal(string fileName, MergeCheckResult merge)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(ConflictRefusedMarker).Append(' ').Append(fileName)
              .Append(": your edit overlaps a change made to the file after you read it — nothing was written.\n");
            int n = 0;
            foreach (ConflictBlock c in merge?.Conflicts ?? new List<ConflictBlock>())
            {
                sb.Append($"Conflict {++n}:\n");
                AppendSide(sb, "what you read (base)", c.BaseText, c.BaseLine);
                AppendSide(sb, "your version", c.ProposedText, c.ProposedLine);
                AppendSide(sb, "on disk now", c.CurrentText, c.CurrentLine);
            }
            sb.Append(ReReadInstruction);
            return sb.ToString();
        }

        private static void AppendSide(System.Text.StringBuilder sb, string label, string text, int firstLine)
        {
            sb.Append("  ").Append(label).Append(":\n");
            string[] lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 1 && lines[0].Length == 0)
            {
                sb.Append("    (nothing)\n");
                return;
            }
            for (int k = 0; k < lines.Length && k < ConflictLinesShown; k++)
            {
                string line = lines[k].Length <= ConflictLineWidth ? lines[k] : lines[k].Substring(0, ConflictLineWidth - 1) + "…";
                sb.Append("    ").Append((firstLine + k).ToString().PadLeft(5)).Append(": ").Append(line).Append('\n');
            }
            if (lines.Length > ConflictLinesShown)
                sb.Append($"    … {lines.Length - ConflictLinesShown} more line(s)\n");
        }

        /// <summary>Emits <see cref="ConflictRefusedEvent"/> for a refused write.</summary>
        public static void TraceConflictRefused(string site, string fileName, MergeCheckResult merge)
            => Trace.Event("info", ConflictRefusedEvent, new Dictionary<string, object>
            {
                ["site"] = site,
                ["file"] = fileName,
                ["conflicts"] = merge?.Conflicts?.Count ?? 0,
            });

        public const string NoBaseLabel = " [no base: overwrite check only]";
        public const string DiffEngineFailedLabel = " [merge engine failed: proposed text accepted]";

        /// <summary>The suffix for a write's transcript line: empty for a clean or real three-way
        /// merge, a label only for a genuine fallback.</summary>
        public static string TranscriptLabel(MergeCheckResult merge) => merge?.Mode switch
        {
            MergeMode.NoBase => NoBaseLabel,
            MergeMode.DiffEngineFailed => DiffEngineFailedLabel,
            _ => "",
        };

        /// <summary>Emits <see cref="FallbackTraceEvent"/> for a genuine fallback; nothing otherwise.</summary>
        /// <param name="site">The writing method, e.g. "SaveFileAsync" or "TUI ApplyResolvedPatchAsync".</param>
        public static void TraceFallback(string site, string fileName, MergeCheckResult merge)
        {
            if (merge == null || !merge.UsedFallback) return;
            string message = merge.Mode == MergeMode.NoBase
                ? $"{site}: two-way fallback for \"{fileName}\" — no base cache entry. Overwrite detection only, not true three-way merge."
                : $"{site}: merge engine failed ({merge.DiffEngineError}) for \"{fileName}\" — proposed text accepted unmerged.";
            Trace.Event("info", FallbackTraceEvent, new Dictionary<string, object>
            {
                ["site"] = site,
                ["file"] = fileName,
                ["mode"] = merge.Mode.ToString(),
                ["exception"] = merge.DiffEngineError,
                ["message"] = message,
            });
        }
    }
}
