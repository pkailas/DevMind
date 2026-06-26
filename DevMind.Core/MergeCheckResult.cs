// File: MergeCheckResult.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;

namespace DevMind
{
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

        /// <summary>
        /// True when no base content was available so the check fell back to
        /// comparing proposed vs. current only (overwrite detection, not true
        /// three-way merge).
        /// </summary>
        public bool UsedFallback { get; set; }
    }
}
