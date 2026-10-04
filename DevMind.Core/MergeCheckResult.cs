// File: MergeCheckResult.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// v1.1 (H-05): the single UsedFallback bool said "fallback" for three different things — no
// base cache entry, base == current (the NORMAL clean case: nobody else touched the file),
// and DiffPlex throwing — so every clean patch printed "[two-way fallback]" and readers
// learned to ignore it. Mode now says which; UsedFallback is computed and true only for a
// genuine fallback.

using System.Collections.Generic;

namespace DevMind
{
    /// <summary>How a merge check reached its result.</summary>
    public enum MergeMode
    {
        /// <summary>A real three-way merge ran (base, proposed and current all differed).</summary>
        ThreeWay,
        /// <summary>The base matches what is on disk: nobody else changed the file, so the
        /// proposed text is exactly right. The normal case — not a fallback.</summary>
        CleanNoDivergence,
        /// <summary>No base cache entry: the write is checked for overwrite only.</summary>
        NoBase,
        /// <summary>The diff engine threw; the proposed text was accepted unmerged.</summary>
        DiffEngineFailed,
    }

    /// <summary>
    /// Result of a three-way merge check. If <see cref="HasConflicts"/> is true,
    /// the caller must present conflict blocks to the user. Otherwise the merged
    /// text is ready to write.
    /// </summary>
    public sealed class MergeCheckResult
    {
        /// <summary>True when the merge produced conflicts that require user resolution.</summary>
        public bool HasConflicts { get; set; }

        /// <summary>Merged text ready to write (populated when <see cref="HasConflicts"/> is false).</summary>
        public string MergedText { get; set; } = string.Empty;

        /// <summary>Conflict regions (populated when <see cref="HasConflicts"/> is true).</summary>
        public List<ConflictBlock> Conflicts { get; set; } = new List<ConflictBlock>();

        /// <summary>How the result was reached.</summary>
        public MergeMode Mode { get; set; }

        /// <summary>The exception type name when <see cref="Mode"/> is
        /// <see cref="MergeMode.DiffEngineFailed"/>; null otherwise.</summary>
        public string DiffEngineError { get; set; }

        /// <summary>
        /// True only for a genuine fallback — no base entry, or the diff engine failed. A clean
        /// write (base == current) is not a fallback.
        /// </summary>
        public bool UsedFallback => Mode is MergeMode.NoBase or MergeMode.DiffEngineFailed;
    }
}
