// File: FileContentCache.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;

namespace DevMind
{
    public class FileContentCache
    {
        /// <summary>
        /// Maximum number of files held in the cache. When full, inserting a new file evicts the
        /// least-recently-accessed entry so a long session's distinct-file working set stays bounded.
        /// </summary>
        private const int MaxEntries = 200;

        private readonly Dictionary<string, CachedFile> _cache = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);

        public void Store(string filename, string content)
        {
            // At capacity and adding a genuinely new key → drop the least-recently-used entry first.
            // (Overwriting an existing key doesn't grow the cache, so no eviction needed there.)
            if (_cache.Count >= MaxEntries && !_cache.ContainsKey(filename))
                EvictLeastRecentlyUsed();

            _cache[filename] = new CachedFile(content);
        }

        public string GetLineRange(string filename, int startLine, int endLine)
        {
            if (!_cache.TryGetValue(filename, out var cached)) return null;
            cached.Touch();
            string[] lines = cached.Lines;
            int start = Math.Max(1, startLine) - 1;          // convert to 0-based
            int end   = Math.Min(lines.Length, endLine) - 1; // clamp to actual line count
            if (start > end || start >= lines.Length) return null;
            return string.Join("\n", lines, start, end - start + 1);
        }

        public string GetFull(string filename)
        {
            if (!_cache.TryGetValue(filename, out var cached)) return null;
            cached.Touch();
            return cached.Content;
        }

        public int GetLineCount(string filename)
        {
            if (!_cache.TryGetValue(filename, out var cached)) return -1;
            cached.Touch();
            return cached.Lines.Length;
        }

        public bool Contains(string filename)
        {
            if (!_cache.TryGetValue(filename, out var cached)) return false;
            cached.Touch();
            return true;
        }

        public void Invalidate(string filename)
        {
            _cache.Remove(filename);
        }

        public void InvalidateAll()
        {
            _cache.Clear();
        }

        /// <summary>
        /// Removes the entry with the oldest <see cref="CachedFile.LastAccess"/>. Linear scan over
        /// at most <see cref="MaxEntries"/> entries — cheap and avoids a parallel ordering structure.
        /// </summary>
        private void EvictLeastRecentlyUsed()
        {
            string oldestKey = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (var kvp in _cache)
            {
                if (kvp.Value.LastAccess < oldest)
                {
                    oldest = kvp.Value.LastAccess;
                    oldestKey = kvp.Key;
                }
            }
            if (oldestKey != null)
                _cache.Remove(oldestKey);
        }

        private sealed class CachedFile
        {
            public string Content { get; }
            public string[] Lines { get; }

            /// <summary>Last time this entry was stored or read — drives LRU eviction.</summary>
            public DateTime LastAccess { get; private set; }

            public CachedFile(string content)
            {
                Content = content;
                Lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                LastAccess = DateTime.UtcNow;
            }

            public void Touch() => LastAccess = DateTime.UtcNow;
        }
    }
}
