// File: NearlineCache.cs  v8.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DevMind
{
    /// <summary>
    /// Two-tier cache for content trimmed from conversation context by MicroCompactToolResults.
    /// Tier 1 is a small in-memory LRU (hot); when it overflows, entries spill to Tier 2, a
    /// per-session temp-file backend (cold). The public surface (Store/Retrieve/Contains/Clear/
    /// Count/Keys/EstimatedTokens) is unchanged from the single-tier version, so callers don't change.
    /// Single-user, single-threaded usage (driven from the LLM turn); no locking.
    /// </summary>
    public sealed class NearlineCache
    {
        // Tier 1 — in-memory hot cache.
        private const int MaxMemoryEntries = 50;

        // Tier 2 — disk cold cache limits (memory + disk combined).
        private const int MaxTotalEntries = 1_000;
        private const long MaxDiskBytes = 500L * 1024 * 1024; // 500 MB

        private readonly Dictionary<string, NearlineCacheEntry> _memory =
            new Dictionary<string, NearlineCacheEntry>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, DiskEntry> _disk =
            new Dictionary<string, DiskEntry>(StringComparer.OrdinalIgnoreCase);

        // Maps a short, model-referenceable handle ("nl-N") to the cache key it was minted for, so
        // the model can recall a compacted entry by its breadcrumb handle via the recall_cache tool.
        private readonly Dictionary<string, string> _handleToKey =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private long _handleCounter; // monotonic; "nl-{N}" starts at 1 per session

        private long _useCounter;   // monotonic stamp source for memory-tier LRU
        private long _diskBytes;    // running sum of spilled file sizes
        private string _sessionDir; // captured on first spill so Clear() deletes the right folder

        // Cumulative session counters (surfaced via CacheStats / the /cache command).
        private long _memoryHits;
        private long _diskHits;
        private long _evictionsToDisk;
        private long _diskEvictions;
        private long _diskWriteFailures;

        /// <summary>Snapshot of cache gauges and cumulative counters for diagnostics and /cache.</summary>
        public NearlineCacheStats CacheStats => new NearlineCacheStats(
            _memory.Count, _disk.Count, _diskBytes, _handleToKey.Count,
            _memoryHits, _diskHits, _evictionsToDisk, _diskEvictions, _diskWriteFailures);

        /// <summary>
        /// Stores content under the given key, replacing any existing entry. New entries land in
        /// the memory tier; if that exceeds the cap, the least-recently-used entry spills to disk.
        /// Mints a short handle ("nl-N") for the entry, records it in the handle index, and returns
        /// it so the caller can embed it in the breadcrumb for later recall via recall_cache.
        /// </summary>
        /// <param name="key">Cache key (e.g. "tool:{toolCallId}", "read:{filename}", "shell:{turn}").</param>
        /// <param name="content">The original content being trimmed from context.</param>
        /// <param name="breadcrumb">The compact replacement string left in context.</param>
        /// <returns>The minted handle (e.g. "nl-7").</returns>
        public string Store(string key, string content, string breadcrumb)
        {
            // Re-store supersedes any prior disk copy of the same key.
            if (_disk.TryGetValue(key, out var stale))
            {
                DeleteDiskFile(stale);
                _disk.Remove(key);
            }

            _memory[key] = new NearlineCacheEntry(key, content, breadcrumb, DateTime.UtcNow)
            {
                LastUse = ++_useCounter
            };

            string handle = "nl-" + (++_handleCounter);
            _handleToKey[handle] = key;

            if (_memory.Count > MaxMemoryEntries)
                EvictLruToDisk();

            return handle;
        }

        /// <summary>Resolves a breadcrumb handle ("nl-N") to its cache key, or null if unknown.</summary>
        public string GetKeyForHandle(string handle)
        {
            if (handle != null && _handleToKey.TryGetValue(handle, out string key))
                return key;
            return null;
        }

        /// <summary>
        /// Returns the cached content, or null if not found. Checks the memory tier first, then disk.
        /// Disk hits are returned directly with NO promotion back into the memory tier.
        /// </summary>
        public string Retrieve(string key)
        {
            if (_memory.TryGetValue(key, out var mem))
            {
                mem.LastUse = ++_useCounter; // touch for LRU
                _memoryHits++;
                return mem.Content;
            }

            if (_disk.TryGetValue(key, out var disk))
            {
                try
                {
                    string content = File.ReadAllText(disk.Path, Encoding.UTF8);
                    _diskHits++;
                    return content;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NearlineCache] disk read failed for '{key}': {ex.Message}");
                    // Drop the broken index entry so we don't keep trying a dead file.
                    _diskBytes -= disk.SizeBytes;
                    if (_diskBytes < 0) _diskBytes = 0;
                    _disk.Remove(key);
                    return null;
                }
            }

            return null;
        }

        /// <summary>Check if a key exists in either tier.</summary>
        public bool Contains(string key) => _memory.ContainsKey(key) || _disk.ContainsKey(key);

        /// <summary>
        /// Clear both tiers, the counters, and delete this session's spill folder.
        /// Called on session reset (/new, /restart). NOT called on brainwash.
        /// </summary>
        public void Clear()
        {
            _memory.Clear();
            _disk.Clear();
            _handleToKey.Clear();
            _handleCounter = 0;
            _diskBytes = 0;
            _useCounter = 0;
            _memoryHits = 0;
            _diskHits = 0;
            _evictionsToDisk = 0;
            _diskEvictions = 0;
            _diskWriteFailures = 0;

            // Delete the captured session folder. Use the captured path (not a fresh SessionId.Get())
            // because /new resets the session id before this runs — we must delete the OLD folder.
            if (_sessionDir != null)
            {
                try
                {
                    if (Directory.Exists(_sessionDir))
                        Directory.Delete(_sessionDir, recursive: true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NearlineCache] session dir delete failed: {ex.Message}");
                }
                _sessionDir = null;
            }
        }

        /// <summary>Total entries across both tiers (for diagnostics and context summaries).</summary>
        public int Count => _memory.Count + _disk.Count;

        /// <summary>
        /// Rough total cached size in tokens (for diagnostics): in-memory content chars plus
        /// on-disk byte sizes (≈ chars for UTF-8 text), divided by 4.
        /// </summary>
        public int EstimatedTokens
        {
            get
            {
                long chars = 0;
                foreach (var entry in _memory.Values)
                    chars += entry.Content?.Length ?? 0;
                chars += _diskBytes;
                return (int)(chars / 4);
            }
        }

        /// <summary>All cache keys across both tiers (for diagnostic and summarization context).</summary>
        public IEnumerable<string> Keys
        {
            get
            {
                foreach (var k in _memory.Keys) yield return k;
                foreach (var k in _disk.Keys) yield return k;
            }
        }

        // ── internals ────────────────────────────────────────────────────────

        private void EvictLruToDisk()
        {
            string lruKey = null;
            long min = long.MaxValue;
            foreach (var kvp in _memory)
            {
                if (kvp.Value.LastUse < min)
                {
                    min = kvp.Value.LastUse;
                    lruKey = kvp.Key;
                }
            }
            if (lruKey == null) return;

            var victim = _memory[lruKey];
            if (TrySpillToDisk(victim))
            {
                _memory.Remove(lruKey);
                _evictionsToDisk++;
                EnforceDiskLimits();
            }
            // On spill failure the entry stays in memory (per spec); memory may briefly exceed the cap.
        }

        private bool TrySpillToDisk(NearlineCacheEntry entry)
        {
            try
            {
                string dir = EnsureSessionDir();
                string path = Path.Combine(dir, HashKey(entry.Key) + ".bin");
                File.WriteAllText(path, entry.Content ?? "", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                long size = new FileInfo(path).Length;
                _disk[entry.Key] = new DiskEntry(entry.Key, entry.Breadcrumb, entry.CachedAt, path, size);
                _diskBytes += size;
                return true;
            }
            catch (Exception ex)
            {
                _diskWriteFailures++;
                System.Diagnostics.Debug.WriteLine($"[NearlineCache] spill write failed for '{entry.Key}': {ex.Message}");
                return false;
            }
        }

        private void EnforceDiskLimits()
        {
            // Evict oldest-by-CachedAt disk entries until within BOTH the total-entry and disk-byte caps.
            while (_disk.Count > 0 && (Count > MaxTotalEntries || _diskBytes > MaxDiskBytes))
            {
                string oldestKey = null;
                DateTime oldest = DateTime.MaxValue;
                foreach (var kvp in _disk)
                {
                    if (kvp.Value.CachedAt < oldest)
                    {
                        oldest = kvp.Value.CachedAt;
                        oldestKey = kvp.Key;
                    }
                }
                if (oldestKey == null) break;

                DeleteDiskFile(_disk[oldestKey]);
                _disk.Remove(oldestKey);
                _diskEvictions++;
            }
        }

        private void DeleteDiskFile(DiskEntry entry)
        {
            try
            {
                if (File.Exists(entry.Path))
                    File.Delete(entry.Path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NearlineCache] file delete failed for '{entry.Key}': {ex.Message}");
            }
            _diskBytes -= entry.SizeBytes;
            if (_diskBytes < 0) _diskBytes = 0;
        }

        private string EnsureSessionDir()
        {
            if (_sessionDir == null)
                _sessionDir = SessionDirFor(SessionId.Get());
            Directory.CreateDirectory(_sessionDir);
            return _sessionDir;
        }

        private static string SessionDirFor(string sessionId)
            => Path.Combine(Path.GetTempPath(), "DevMind", sessionId, "nearline");

        // Raw keys ("tool:call_x", "read:C:\\path") aren't filesystem-safe, so spill files are named by
        // the SHA-256 of the key. The real key→path mapping lives in the in-memory disk index, so
        // retrieval never needs to reverse the filename.
        private static string HashKey(string key)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key ?? ""));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Best-effort startup cleanup: deletes the <c>nearline</c> spill folder under
        /// <c>%TEMP%\DevMind\{sessionId}\</c> for any session id NOT in <paramref name="keepSessionIds"/>
        /// (i.e. not resumable from history, and not the current session). Synchronous and never throws —
        /// intended to be called from a fire-and-forget background task at launch. Only touches
        /// <c>nearline</c> subfolders, so other temp data (patch backups, McpServer, SQL output) is left alone.
        /// </summary>
        public static void CleanupOrphaned(IEnumerable<string> keepSessionIds)
        {
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "DevMind");
                if (!Directory.Exists(root)) return;

                var keep = new HashSet<string>(keepSessionIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

                foreach (string sessionDir in Directory.GetDirectories(root))
                {
                    try
                    {
                        string nearlineDir = Path.Combine(sessionDir, "nearline");
                        if (!Directory.Exists(nearlineDir)) continue; // not a nearline session folder

                        string sessionId = Path.GetFileName(sessionDir);
                        if (keep.Contains(sessionId)) continue; // resumable or current — preserve

                        Directory.Delete(nearlineDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NearlineCache] cleanup of '{sessionDir}' failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NearlineCache] cleanup scan failed: {ex.Message}");
            }
        }

        /// <summary>Disk-tier index entry — metadata only; the content lives in the spill file.</summary>
        private readonly record struct DiskEntry(string Key, string Breadcrumb, DateTime CachedAt, string Path, long SizeBytes);
    }

    /// <summary>
    /// In-memory (hot-tier) entry. Holds the original content, the breadcrumb left in context, and
    /// the time it was cached. <see cref="LastUse"/> is a mutable LRU stamp used only by the cache.
    /// </summary>
    public sealed class NearlineCacheEntry
    {
        public string Key { get; }
        public string Content { get; }
        public string Breadcrumb { get; }
        public DateTime CachedAt { get; }

        /// <summary>Monotonic last-use stamp for memory-tier LRU eviction (internal bookkeeping).</summary>
        public long LastUse { get; set; }

        public NearlineCacheEntry(string key, string content, string breadcrumb, DateTime cachedAt)
        {
            Key = key;
            Content = content;
            Breadcrumb = breadcrumb;
            CachedAt = cachedAt;
        }
    }

    /// <summary>Readonly snapshot of NearlineCache gauges and cumulative session counters.</summary>
    public readonly record struct NearlineCacheStats(
        int MemoryEntries,
        int DiskEntries,
        long DiskBytes,
        int HandleEntries,
        long MemoryHits,
        long DiskHits,
        long EvictionsToDisk,
        long DiskEvictions,
        long DiskWriteFailures);
}
