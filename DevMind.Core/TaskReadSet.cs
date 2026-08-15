// File: TaskReadSet.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shared write-guard state for the agentic hosts (BufferedAgenticHost,
// TuiAgenticHost). Tracks which files were read (READ/GREP/patch auto-read)
// or write-approved during the current user turn, so the write guard can
// require approval for files the model has never seen.
//
// Same shared-helper pattern as FilePathResolver: both hosts delegate here
// instead of carrying their own copy of the logic.

using System;
using System.Collections.Generic;
using System.IO;

namespace DevMind
{
    /// <summary>
    /// Per-task set of files the model has READ (or had write-approved) this turn.
    ///
    /// Keys are RESOLVED FULL PATHS — normalized via <see cref="NormalizePath"/> and
    /// compared with <see cref="StringComparer.OrdinalIgnoreCase"/>. Keying on the bare
    /// file name let the guard be waved through on a shared name: reading
    /// DevMind.Cli/Program.cs marked "Program.cs" as known, and a later patch of
    /// DevMind.TUI/Program.cs — a different file that was never read — passed the
    /// check because both reduce to the same bare name. The guard check now runs
    /// AFTER path resolution and against the path that will actually be written.
    ///
    /// Thread-safe: tool execution is sequential per turn, but host output can be
    /// pumped from UI callbacks, so all access is lock-guarded (same contract as
    /// the action journal).
    /// </summary>
    public sealed class TaskReadSet
    {
        private readonly HashSet<string> _paths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        /// <summary>
        /// True when the resolved <paramref name="fullPath"/> was read this turn —
        /// or when NOTHING has been read yet (the set is empty).
        ///
        /// The empty-set clause is preserved deliberately: it makes the FIRST write
        /// of every turn allowed without the read-first requirement (the guard only
        /// bites from the second write onward). Lineage: the original guard (41afb6e)
        /// also allowed writes to files "mentioned in the user's prompt" via a
        /// _pendingResubmitPrompt check that no longer exists in this class; the
        /// clause as it stands was carried into the current shape by ba4cdbe.
        /// Whether the empty-set allowance is the deliberate remainder of that branch
        /// or an accident of the rewrite is UNRESOLVED — treat it as intentional until
        /// the author says otherwise.
        /// </summary>
        public bool IsKnown(string fullPath)
        {
            lock (_lock)
            {
                string key = NormalizePath(fullPath);
                return _paths.Count == 0 || _paths.Contains(key);
            }
        }

        /// <summary>Records <paramref name="fullPath"/> as read (or write-approved) this turn.</summary>
        public void MarkKnown(string fullPath)
        {
            lock (_lock)
                _paths.Add(NormalizePath(fullPath));
        }

        /// <summary>Called at the start of each user-initiated turn and on session reset.</summary>
        public void Clear()
        {
            lock (_lock)
                _paths.Clear();
        }

        /// <summary>
        /// Canonical key: <see cref="Path.GetFullPath"/> (absolute, separators
        /// normalized, '.'/'..' segments resolved) when the path is well-formed,
        /// otherwise the path as-is. Case-insensitivity is handled by the set's
        /// comparer, not by this method.
        /// </summary>
        internal static string NormalizePath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }
    }
}
