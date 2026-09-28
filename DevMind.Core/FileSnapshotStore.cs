// File: FileSnapshotStore.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Session baselines for diff_file: the content a file had when this session first touched
// it. One store for every surface — the TUI host, BufferedAgenticHost (CLI + headless jobs)
// and the MCP server each used to keep their own dictionary with different rules (LRU 20,
// unbounded, unbounded-with-empty-baseline-for-new-files).
//
// Rules:
//   * First capture wins; later captures only refresh the LRU stamp.
//   * A path that does not exist yet is captured as "" — a file created this session diffs
//     against empty (the MCP server's behaviour; the hosts silently captured nothing).
//   * Bounded LRU (each entry is a whole file). An evicted path is remembered, so diff_file
//     can say the baseline was evicted instead of the false "not modified this session".
//   * Thread-safe: the MCP server's snapshot calls may come from any dispatcher thread.

using System;
using System.Collections.Generic;
using System.IO;

namespace DevMind
{
    public sealed class FileSnapshotStore
    {
        /// <summary>Default capacity (whole-file entries) for every surface.</summary>
        public const int DefaultCapacity = 100;

        private readonly int _capacity;
        private readonly Dictionary<string, (string content, long use)> _entries =
            new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _evicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private long _useCounter;

        public FileSnapshotStore(int capacity = DefaultCapacity)
        {
            _capacity = Math.Max(1, capacity);
        }

        public int Capacity => _capacity;

        /// <summary>
        /// Captures <paramref name="fullPath"/>'s current content as its session baseline unless
        /// one exists (then only its LRU stamp is refreshed). A missing file is captured as "";
        /// an unreadable one is not captured.
        /// </summary>
        public void Capture(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            fullPath = Normalize(fullPath);
            lock (_lock)
            {
                if (_entries.TryGetValue(fullPath, out var existing))
                {
                    _entries[fullPath] = (existing.content, ++_useCounter);
                    return;
                }
            }

            string content;
            try
            {
                content = File.Exists(fullPath)
                    ? PatchEngine.ReadFilePreservingEncoding(fullPath).content
                    : string.Empty;
            }
            catch
            {
                return; // unreadable: no baseline rather than a wrong one
            }

            lock (_lock)
            {
                if (_entries.ContainsKey(fullPath)) return; // raced: first capture wins
                if (_entries.Count >= _capacity) EvictLeastRecentlyUsed();
                _entries[fullPath] = (content, ++_useCounter);
                _evicted.Remove(fullPath);
            }
        }

        /// <summary>The baseline for <paramref name="fullPath"/> (refreshes its LRU stamp).</summary>
        public bool TryGet(string fullPath, out string content)
        {
            content = null;
            if (string.IsNullOrEmpty(fullPath)) return false;
            fullPath = Normalize(fullPath);
            lock (_lock)
            {
                if (!_entries.TryGetValue(fullPath, out var entry)) return false;
                _entries[fullPath] = (entry.content, ++_useCounter);
                content = entry.content;
                return true;
            }
        }

        /// <summary>True when a baseline for <paramref name="fullPath"/> existed and was evicted.</summary>
        public bool WasEvicted(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            fullPath = Normalize(fullPath);
            lock (_lock) return _evicted.Contains(fullPath);
        }

        /// <summary>Moves a baseline to a renamed path (rename_file keeps the diff history).</summary>
        public void Move(string oldPath, string newPath)
        {
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;
            oldPath = Normalize(oldPath);
            newPath = Normalize(newPath);
            lock (_lock)
            {
                if (!_entries.TryGetValue(oldPath, out var entry)) return;
                _entries.Remove(oldPath);
                _entries[newPath] = entry;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _evicted.Clear();
                _useCounter = 0;
            }
        }

        /// <summary>Keys are full paths (case-insensitive) whatever spelling the caller used.</summary>
        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        private void EvictLeastRecentlyUsed()
        {
            string lru = null;
            long min = long.MaxValue;
            foreach (var kvp in _entries)
            {
                if (kvp.Value.use < min) { min = kvp.Value.use; lru = kvp.Key; }
            }
            if (lru == null) return;
            _entries.Remove(lru);
            _evicted.Add(lru);
        }
    }
}
