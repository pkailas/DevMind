// File: WriteRootPolicy.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Reloadable allowed-write-roots policy for file-mutating tools.
//
// Two tiers of roots:
//   Baseline — the working directory plus any --dir / DEVMIND_ALLOWED_WRITE_ROOTS
//              roots fixed at process start. A reload can NEVER remove these.
//   Config   — the allowedWriteRoots array in the user's devmind.json, re-read on
//              each Reload call. Only a human editing that file can grant new roots;
//              no code path accepts a root as a parameter.
//
// Concurrency: the effective root list is published as an immutable snapshot behind
// a volatile field. Containment checks read the snapshot once and iterate it; Reload
// builds a fresh list and swaps the reference atomically. No list is ever mutated
// in place after publication.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace DevMind
{
    /// <summary>
    /// Holds the set of directories file-mutating tools may write under, with a
    /// permanent startup baseline and a reloadable config-sourced extension.
    /// </summary>
    public sealed class WriteRootPolicy
    {
        private readonly IReadOnlyList<string> _baseline;
        private volatile IReadOnlyList<string> _current;

        /// <summary>Current effective roots (immutable snapshot; safe to iterate).</summary>
        public IReadOnlyList<string> Current => _current;

        /// <summary>
        /// Builds the permanent baseline from the working directory and any startup
        /// roots (--dir / env). Invalid or blank entries are skipped; entries are
        /// normalized via Path.GetFullPath and de-duplicated case-insensitively.
        /// </summary>
        public WriteRootPolicy(string workingDirectory, IEnumerable<string>? startupRoots = null)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();

            void Add(string? root)
            {
                if (string.IsNullOrWhiteSpace(root)) return;
                try
                {
                    string normalized = Path.GetFullPath(root);
                    if (set.Add(normalized))
                        ordered.Add(normalized);
                }
                catch
                {
                    // Skip invalid paths silently (validation happens at parse time).
                }
            }

            Add(workingDirectory);
            if (startupRoots != null)
                foreach (var root in startupRoots)
                    Add(root);

            _baseline = ordered.AsReadOnly();
            _current  = _baseline;
        }

        /// <summary>
        /// Recomputes the effective root set as baseline + the given config roots and
        /// atomically publishes it. Config entries must be absolute paths to existing
        /// directories (or files); offenders are skipped with a message to
        /// <paramref name="warn"/>. Passing null or an empty sequence resets the
        /// effective set to the baseline — the baseline itself can never be removed.
        /// Returns the new effective root list.
        /// </summary>
        public IReadOnlyList<string> Reload(IEnumerable<string>? configRoots, Action<string>? warn = null)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();

            foreach (var root in _baseline)
            {
                if (set.Add(root))
                    ordered.Add(root);
            }

            if (configRoots != null)
            {
                foreach (var entry in configRoots)
                {
                    if (string.IsNullOrWhiteSpace(entry))
                        continue;

                    if (!Path.IsPathRooted(entry))
                    {
                        warn?.Invoke($"allowedWriteRoots entry is not absolute, skipping: '{entry}'");
                        continue;
                    }

                    string normalized;
                    try
                    {
                        normalized = Path.GetFullPath(entry);
                    }
                    catch (Exception ex)
                    {
                        warn?.Invoke($"allowedWriteRoots entry is invalid, skipping: '{entry}' ({ex.Message})");
                        continue;
                    }

                    if (!Directory.Exists(normalized) && !File.Exists(normalized))
                    {
                        warn?.Invoke($"allowedWriteRoots entry does not exist, skipping: '{normalized}'");
                        continue;
                    }

                    if (set.Add(normalized))
                        ordered.Add(normalized);
                }
            }

            var snapshot = (IReadOnlyList<string>)ordered.AsReadOnly();
            _current = snapshot;
            return snapshot;
        }

        /// <summary>
        /// Ensures a resolved path stays within one of the current allowed write roots.
        /// Normalizes traversals (..\) and absolute paths via Path.GetFullPath, then
        /// verifies the result is under at least one current root; throws
        /// InvalidOperationException if it escapes all of them. Returns the normalized
        /// full path. Reads a single snapshot, so a concurrent Reload cannot change the
        /// list mid-check.
        /// </summary>
        public string EnsureContained(string resolvedFullPath)
        {
            string fullPath = Path.GetFullPath(resolvedFullPath);
            var roots = _current;

            foreach (var root in roots)
            {
                string rootWithSep = EnsureTrailingSeparator(root);
                if (fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                    return fullPath;
            }

            string rootsList = string.Join("; ", roots);
            throw new InvalidOperationException(
                $"Path containment violation: '{fullPath}' is outside the allowed write roots: {rootsList}. " +
                "File create/modify/delete/rename is restricted to these directories. " +
                "To grant access, the user must add the directory to \"allowedWriteRoots\" in " +
                "%APPDATA%\\devmind\\devmind.json, then call reload_write_roots to pick up the " +
                "change without restarting.");
        }

        /// <summary>
        /// Returns the path with a trailing directory separator appended (if not already
        /// present) so "C:\WorkDir" does not match "C:\WorkDir2" during prefix checks.
        /// </summary>
        private static string EnsureTrailingSeparator(string path)
        {
            if (path.Length > 0
                && path[path.Length - 1] != Path.DirectorySeparatorChar
                && path[path.Length - 1] != Path.AltDirectorySeparatorChar)
            {
                return path + Path.DirectorySeparatorChar;
            }
            return path;
        }
    }
}
