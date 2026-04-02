// File: NearlineCache.cs  v7.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;

namespace DevMind
{
    /// <summary>
    /// In-memory cache for content trimmed from conversation context by MicroCompactToolResults.
    /// Allows aggressive context trimming while preserving instant recall if the model needs
    /// old content again. Single-user, single-thread, no locking needed.
    /// </summary>
    public sealed class NearlineCache
    {
        private readonly Dictionary<string, NearlineCacheEntry> _entries =
            new Dictionary<string, NearlineCacheEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stores content under the given key, replacing any existing entry.
        /// </summary>
        /// <param name="key">Cache key (e.g. "tool:{toolCallId}", "read:{filename}", "shell:{turn}").</param>
        /// <param name="content">The original content being trimmed from context.</param>
        /// <param name="breadcrumb">The compact replacement string left in context.</param>
        public void Store(string key, string content, string breadcrumb)
        {
            _entries[key] = new NearlineCacheEntry(content, breadcrumb, DateTime.UtcNow);
        }

        /// <summary>
        /// Returns the cached content, or null if not found.
        /// </summary>
        public string Retrieve(string key)
        {
            if (_entries.TryGetValue(key, out var entry))
                return entry.Content;
            return null;
        }

        /// <summary>
        /// Check if a key exists in the cache.
        /// </summary>
        public bool Contains(string key)
        {
            return _entries.ContainsKey(key);
        }

        /// <summary>
        /// Clear all entries (on session restart).
        /// </summary>
        public void Clear()
        {
            _entries.Clear();
        }

        /// <summary>
        /// Total cached content size in estimated tokens (for diagnostics).
        /// </summary>
        public int EstimatedTokens
        {
            get
            {
                int total = 0;
                foreach (var entry in _entries.Values)
                    total += (entry.Content?.Length ?? 0) / 4;
                return total;
            }
        }

        /// <summary>
        /// Number of entries currently in the cache.
        /// </summary>
        public int Count => _entries.Count;
    }

    /// <summary>
    /// Immutable entry in the nearline cache. Holds the original content, the breadcrumb
    /// left in context, and the time it was cached.
    /// </summary>
    public sealed class NearlineCacheEntry
    {
        public string Content { get; }
        public string Breadcrumb { get; }
        public DateTime CachedAt { get; }

        public NearlineCacheEntry(string content, string breadcrumb, DateTime cachedAt)
        {
            Content = content;
            Breadcrumb = breadcrumb;
            CachedAt = cachedAt;
        }
    }
}
