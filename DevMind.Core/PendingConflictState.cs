// File: PendingConflictState.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

namespace DevMind
{
    /// <summary>
    /// Captures the state of a pending merge conflict so the host can store it
    /// and later resolve it via /resolve without blocking the input loop.
    /// </summary>
    public sealed class PendingConflictState
    {
        /// <summary>Full path of the file with the conflict.</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>Content of the file from the cache (before any proposed change).</summary>
        public string BaseContent { get; set; } = string.Empty;

        /// <summary>Content the LLM wants to write.</summary>
        public string ProposedContent { get; set; } = string.Empty;

        /// <summary>Current content on disk (read at conflict detection time).</summary>
        public string CurrentContent { get; set; } = string.Empty;

        /// <summary>Conflict blocks for display to the user.</summary>
        public MergeCheckResult MergeResult { get; set; }

        /// <summary>True when only two-way fallback was available (no base cache entry).</summary>
        public bool UsedFallback { get; set; }
    }
}
