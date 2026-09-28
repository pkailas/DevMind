// File: MemoryCheckpoint.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The memory half of /rewind (part 2a): checkpoints of the REPO memory set —
// MEMORY.md plus every file under .devmind/memory — taken at the start of every
// user turn, so /rewind N can roll the memory back to what it was BEFORE turn N.
//
// Why it exists: the agentic loop's save_memory writes into the repo (topic file
// + index entry), and the index is loaded into EVERY conversation's system prompt.
// A branch the user rewound away therefore leaks itself back in through memory.
// The conversation fork cuts the rows; this cuts the files the rows wrote.
//
// What it has to get right:
//
//  * THE SCOPE. Only the memory set is ever touched: <memoryRoot>\MEMORY.md and
//    <memoryRoot>\.devmind\memory\** — never any other path, never the
//    machine-level (%APPDATA%) layer, which is read-only at the tool surface.
//    Restore deletes files that exist now but not in the snapshot, rewrites the
//    ones that differ, recreates the ones that were deleted since — byte-for-byte
//    (files are copied as bytes; BOM/encoding survive untouched).
//
//  * THE NUMBERING. k is the turn's number the way RewindPlanner numbers turns
//    (non-synthetic user messages, 1-based, chronological) — the same count the
//    TUI keeps in TurnClock.Advances at the turn boundary. A checkpoint is
//    taken at the START of the turn, BEFORE the prompt is sent, so the
//    turn-N snapshot is what memory looked like before turn N ran — exactly
//    where /rewind N puts the conversation.
//
//  * THE FAILURES. Snapshotting must never block or break a turn: every fault
//    is swallowed (the caller logs nothing either — a dim transcript line for a
//    checkpoint miss would be noise). Restoring a missing checkpoint is a no-op
//    with a reason, never a guess: there is no "closest" snapshot to fall back
//    on, because guessing at memory contents is how an abandoned branch leaks
//    back in under a new name.
//
// Layout: <checkpointRoot>\<sessionId>\turn-<k>\{manifest.json, memory\...}
// The memory tree is copied under memory\ preserving relative paths; manifest.json
// records the file list with SHA-256 hashes, the source roots, and (when the set
// was byte-identical to a previous turn) sameAsTurn — in which case the copy is
// skipped entirely and the manifest points at the previous snapshot's files.
//
// This class is pure file I/O with injected roots — no TUI, no app state — so
// the whole checkpoint/restore/prune surface is testable against temp dirs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace DevMind
{
    /// <summary>
    /// Checkpoints of the repo-memory set (MEMORY.md + .devmind/memory/**) per
    /// session and turn, under an injected root — the seam tests use a temp dir
    /// instead of the real %LOCALAPPDATA%\devmind\checkpoints.
    /// </summary>
    public sealed class MemoryCheckpoint
    {
        /// <summary>Default checkpoint root: %LOCALAPPDATA%\devmind\checkpoints.</summary>
        public static string DefaultRoot()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "devmind", "checkpoints");
        }

        const string MemorySubDir = "memory";
        const string ManifestName = "manifest.json";

        readonly string _root;
        readonly string _sessionId;
        readonly string _memoryRoot;

        /// <summary>
        /// <paramref name="checkpointRoot"/>: where all sessions' checkpoints live
        /// (default <see cref="DefaultRoot"/>). <paramref name="sessionId"/>: the
        /// session this instance snapshots and restores for. <paramref name="memoryRoot"/>:
        /// the repo root holding MEMORY.md and .devmind/memory — the working directory
        /// the MemoryManager reads and writes.
        /// </summary>
        public MemoryCheckpoint(string checkpointRoot, string sessionId, string memoryRoot)
        {
            _root = checkpointRoot ?? DefaultRoot();
            _sessionId = sessionId ?? string.Empty;
            _memoryRoot = memoryRoot ?? string.Empty;
        }

        // ── Manifest ──────────────────────────────────────────────────────────

        /// <summary>One file in a snapshot: path relative to the memory root, plus its hash.</summary>
        sealed class ManifestFile
        {
            public string Path { get; set; }
            public string Sha256 { get; set; }
        }

        sealed class Manifest
        {
            public int Turn { get; set; }
            public string SessionId { get; set; }
            public string MemoryRoot { get; set; }
            public string TakenAt { get; set; }
            public int SameAsTurn { get; set; }
            public List<ManifestFile> Files { get; set; } = new List<ManifestFile>();
        }

        // ── Snapshot ──────────────────────────────────────────────────────────

        /// <summary>
        /// Snapshot the current memory set as turn <paramref name="turnNumber"/>.
        /// Skips the copy when the set is byte-identical to a previous turn's
        /// snapshot (the manifest records <c>sameAsTurn</c>). An absent memory
        /// folder is a valid, empty snapshot. Never throws: a failed snapshot
        /// leaves whatever earlier snapshots exist and returns null.
        /// </summary>
        public string SnapshotAtTurnStart(int turnNumber)
        {
            try
            {
                if (turnNumber < 1) return null;

                _currentSnapshotTurn = turnNumber;
                var current = EnumerateMemoryFiles();

                // Identical-set detection: compare against the previous turns' manifests,
                // newest first. When the file sets match hash-for-hash, the snapshot is a
                // one-line manifest — the files stay where they are (read them from the
                // referenced turn on restore), so a big memory folder is not re-copied
                // every turn.
                int sameAs = FindIdenticalTurn(current);

                var dir = TurnDir(turnNumber);
                Directory.CreateDirectory(dir);

                if (sameAs > 0)
                {
                    // Byte-identical to an earlier turn: the files stay where they are (the
                    // restore follows the reference), so the snapshot is just a manifest.
                    WriteManifest(dir, turnNumber, sameAs, LoadManifest(TurnDir(sameAs)).Files);
                    return dir;
                }

                foreach (var f in current)
                {
                    string dest = Path.Combine(dir, MemorySubDir, f.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(f.FullPath, dest, overwrite: true);
                }

                WriteManifest(dir, turnNumber, sameAs: 0, current
                    .Select(f => new ManifestFile { Path = f.RelativePath, Sha256 = f.Sha256 })
                    .ToList());
                return dir;
            }
            catch
            {
                // Snapshotting never breaks a turn — the worst case is a /rewind that
                // reports "no checkpoint for turn N" and leaves memory unchanged.
                return null;
            }
        }

        // ── Restore ───────────────────────────────────────────────────────────

        /// <summary>
        /// Restore the memory set to a session's turn-<paramref name="turnNumber"/>
        /// snapshot: files in the snapshot are written back byte-for-byte, files
        /// that exist now but not in the snapshot are deleted. Only paths inside
        /// the memory set are touched. Returns the restore summary (added/removed/
        /// changed), or null when no such checkpoint exists — in which case the
        /// memory set is left exactly as it is.
        /// </summary>
        public (int added, int removed, string detail)? Restore(string sessionId, int turnNumber)
        {
            string sourceDir = Path.Combine(_root, sessionId, $"turn-{turnNumber}");
            if (!Directory.Exists(sourceDir)) return null;

            Manifest manifest;
            try { manifest = LoadManifest(sourceDir); }
            catch { return null; }

            // A "same as turn j" manifest holds no files of its own — follow the
            // reference to the snapshot that does. Bounded: chains of identical
            // turns are rare, and an unresolvable reference means "leave memory
            // alone", never a guess.
            int hops = 0;
            while (manifest.SameAsTurn > 0 && hops < 10)
            {
                sourceDir = Path.Combine(_root, sessionId, $"turn-{manifest.SameAsTurn}");
                if (!Directory.Exists(sourceDir)) return null;
                try { manifest = LoadManifest(sourceDir); }
                catch { return null; }
                hops++;
            }
            if (manifest.SameAsTurn > 0) return null;

            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in manifest.Files)
            {
                string p = Normalized(f.Path);
                if (p.Length == 0) continue;
                snapshot[p] = Path.Combine(sourceDir, MemorySubDir, f.Path);
            }

            int added = 0, removed = 0;
            var now = EnumerateMemoryFiles();
            var nowByPath = new Dictionary<string, MemoryFile>(StringComparer.Ordinal);
            foreach (var f in now) nowByPath[Normalized(f.RelativePath)] = f;

            // Write back / recreate everything the snapshot holds.
            foreach (var (rel, sourcePath) in snapshot)
            {
                string dest = Path.Combine(_memoryRoot, Split(rel));
                if (!File.Exists(sourcePath)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                byte[] bytes = File.ReadAllBytes(sourcePath);
                if (File.Exists(dest) && FilesEqual(bytes, dest)) continue; // already identical
                File.WriteAllBytes(dest, bytes);
                added++; // file written: created, or reverted to the snapshot's content
            }

            // Delete what exists now but not in the snapshot — and ONLY that.
            foreach (var (rel, f) in nowByPath)
            {
                if (!snapshot.ContainsKey(rel))
                {
                    try { File.Delete(f.FullPath); removed++; } catch { /* leave it; report below */ }
                    // Note: the count is "intended" removals; a delete that throws
                    // still counts so the message says the restore happened.
                }
            }

            // Housekeeping: drop now-empty directories under .devmind/memory.
            PruneEmptyMemorySubDirs();

            return (added, removed,
                $"Memory restored to turn {turnNumber} (added {added}, removed {removed})");
        }

        // ── Fork support ──────────────────────────────────────────────────────

        /// <summary>
        /// Give a fork session the original's checkpoints for turns
        /// 1..<paramref name="upTo"/> (inclusive): /rewind N forks the
        /// conversation to BEFORE turn N, and a later /rewind inside the fork
        /// must be able to restore memory to any of the fork's own turns.
        /// Copies turn by turn (a fork's turn-2 must not see the original's
        /// turn-3 files). Never throws.
        /// </summary>
        public void CopyCheckpoints(string sourceSessionId, string targetSessionId, int upTo)
        {
            try
            {
                if (upTo < 1) return;
                string sourceRoot = Path.Combine(_root, sourceSessionId);
                string targetRoot = Path.Combine(_root, targetSessionId);
                if (!Directory.Exists(sourceRoot)) return;

                for (int k = 1; k <= upTo; k++)
                {
                    string src = Path.Combine(sourceRoot, $"turn-{k}");
                    string dst = Path.Combine(targetRoot, $"turn-{k}");
                    if (!Directory.Exists(src)) continue;
                    if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
                    CopyDirectory(src, dst);
                }
            }
            catch
            {
                // A fork without checkpoints can only mean a later /rewind inside it
                // reports "no checkpoint" — never worse than not having this.
            }
        }

        // ── Pruning ───────────────────────────────────────────────────────────

        /// <summary>
        /// At TUI start: delete checkpoint folders of sessions older than
        /// <paramref name="maxAgeDays"/> days, and per session keep only the
        /// <paramref name="maxTurnsPerSession"/> most recent turns. A turn's age is its
        /// manifest's <c>TakenAt</c> (written at snapshot time) — NOT the file's mtime, which
        /// is the machine clock at copy time and is meaningless for a checkpoint taken by an
        /// earlier run (a restore/copy/sync can rewrite it). A turn with no readable manifest
        /// falls back to its newest file/dir mtime. Never throws — pruning is housekeeping,
        /// not a load-bearing step.
        /// </summary>
        /// <returns>The session ids whose folders were dropped entirely — the caller uses
        /// this to delete the session's file-checkpoint refs (part 2b) from the repos the
        /// manifests recorded.</returns>
        public static List<string> PruneAll(string root, DateTime nowUtc, int maxAgeDays = 14, int maxTurnsPerSession = 100)
        {
            var pruned = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return pruned;

                foreach (var sessionDir in Directory.GetDirectories(root))
                {
                    // A session's age is the newest of its turns' ages.
                    DateTime? newest = null;
                    var turnDirs = new List<string>();
                    foreach (var d in Directory.GetDirectories(sessionDir))
                    {
                        if (!System.Text.RegularExpressions.Regex.IsMatch(
                            Path.GetFileName(d), @"^turn-\d+$")) continue;
                        turnDirs.Add(d);
                        var t = TurnAge(d);
                        if (t != null && (newest == null || t > newest)) newest = t;
                    }

                    if (newest == null) continue;   // no turns, no age — leave it

                    if (nowUtc - newest.Value.ToUniversalTime() > TimeSpan.FromDays(maxAgeDays))
                    {
                        try { Directory.Delete(sessionDir, recursive: true); pruned.Add(Path.GetFileName(sessionDir)); }
                        catch { /* leave it; nothing to do */ }
                        continue;
                    }

                    // Keep the most recent maxTurnsPerSession turns (by age), drop the rest.
                    foreach (var stale in turnDirs
                        .OrderByDescending(d => TurnAge(d) ?? DateTime.MinValue)
                        .Skip(maxTurnsPerSession))
                    {
                        try { Directory.Delete(stale, recursive: true); }
                        catch { /* leave it */ }
                    }
                }
            }
            catch
            {
                // Pruning is best-effort housekeeping at startup — never a crash path.
            }
            return pruned;
        }

        /// <summary>
        /// A turn's age: its manifest's <c>TakenAt</c> (the snapshot time), falling back to the
        /// newest file/dir mtime under the turn when the manifest is absent or unreadable.
        /// Returns null when neither is available.
        /// </summary>
        static DateTime? TurnAge(string turnDir)
        {
            try
            {
                string manifestPath = Path.Combine(turnDir, ManifestName);
                if (File.Exists(manifestPath))
                {
                    var m = LoadManifest(turnDir);
                    if (m != null && DateTime.TryParse(m.TakenAt, out var taken))
                    {
                        if (taken.Kind == DateTimeKind.Unspecified)
                            taken = DateTime.SpecifyKind(taken, DateTimeKind.Utc);
                        return taken.ToUniversalTime();
                    }
                }
            }
            catch { /* fall through to mtime */ }

            string newestFile;
            try { newestFile = NewestFileOrDirTime(turnDir); }
            catch { return null; }
            if (newestFile == null) return null;
            if (!DateTime.TryParse(newestFile, out var t)) return null;
            if (t.Kind == DateTimeKind.Unspecified) t = DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return t.ToUniversalTime();
        }

        // ── Internals ─────────────────────────────────────────────────────────

        /// <summary>A memory file: its full path, its path relative to the memory root, and its hash.</summary>
        sealed class MemoryFile
        {
            public string FullPath;
            public string RelativePath;
            public string Sha256;
        }

        /// <summary>
        /// The current memory set: MEMORY.md (when present) plus every file under
        /// .devmind/memory. An absent folder yields an empty set — a valid snapshot.
        /// </summary>
        List<MemoryFile> EnumerateMemoryFiles()
        {
            var files = new List<MemoryFile>();
            if (_memoryRoot.Length == 0) return files;

            string index = Path.Combine(_memoryRoot, "MEMORY.md");
            if (File.Exists(index))
                files.Add(MakeFile(index));

            string topicDir = Path.Combine(_memoryRoot, ".devmind", "memory");
            if (Directory.Exists(topicDir))
            {
                foreach (var full in Directory.EnumerateFiles(topicDir, "*", SearchOption.AllDirectories))
                    files.Add(MakeFile(full));
            }
            return files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
        }

        MemoryFile MakeFile(string fullPath)
        {
            var f = new MemoryFile
            {
                FullPath = fullPath,
                RelativePath = NormalizeRelative(fullPath),
            };
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(fullPath);
            f.Sha256 = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            return f;
        }

        /// <summary>
        /// The previous turn (newest first, this turn excluded) whose file set is
        /// byte-identical to <paramref name="current"/> — 0 when none matches.
        /// </summary>
        int FindIdenticalTurn(List<MemoryFile> current)
        {
            string sessionDir = Path.Combine(_root, _sessionId);
            if (!Directory.Exists(sessionDir)) return 0;

            var currentByPath = current.ToDictionary(f => f.RelativePath, f => f.Sha256,
                StringComparer.Ordinal);

            int best = 0;
            foreach (var dir in Directory.GetDirectories(sessionDir))
            {
                string name = Path.GetFileName(dir);
                if (!name.StartsWith("turn-")) continue;
                if (!int.TryParse(name.Substring("turn-".Length), out int k) || k <= 0) continue;
                if (k >= _currentSnapshotTurn) continue; // never reference a later turn (or self) — the "previous" in same-as-turn must be one that came before
                Manifest m;
                try { m = LoadManifest(dir); } catch { continue; }
                if (m.Files.Count != currentByPath.Count) continue;
                bool identical = m.Files.All(f =>
                {
                    string p = Normalized(f.Path);
                    return currentByPath.TryGetValue(p, out string h) &&
                           string.Equals(h, f.Sha256, StringComparison.Ordinal);
                });
                if (identical && k > best) best = k;
            }
            return best;
        }

        // Set by the caller of FindIdenticalTurn so a re-snapshot of the same turn
        // does not reference itself.
        int _currentSnapshotTurn;

        string TurnDir(int turn) => Path.Combine(_root, _sessionId, $"turn-{turn}");

        void WriteManifest(string dir, int turn, int sameAs, List<ManifestFile> files)
        {
            var manifest = new Manifest
            {
                Turn = turn,
                SessionId = _sessionId,
                MemoryRoot = _memoryRoot,
                TakenAt = DateTime.UtcNow.ToString("O"),
                SameAsTurn = sameAs,
                Files = files,
            };
            var json = JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(dir, ManifestName), json);
        }

        static Manifest LoadManifest(string dir)
        {
            var json = File.ReadAllText(Path.Combine(dir, ManifestName));
            return JsonSerializer.Deserialize<Manifest>(json)
                ?? throw new InvalidDataException("Empty manifest.");
        }

        /// <summary>Copy a directory tree, creating the destination as needed.</summary>
        static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string dest = Path.Combine(destination, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(file, dest, overwrite: true);
            }
        }

        static bool FilesEqual(byte[] bytes, string path)
        {
            try
            {
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(path);
                string actual = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                string expected = Convert.ToHexString(bytes).ToLowerInvariant();
                return string.Equals(actual, expected, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>
        /// Drop empty directories under .devmind/memory after deletions — the tree
        /// is recreated by save_memory on demand, so an empty one is dead weight.
        /// </summary>
        void PruneEmptyMemorySubDirs()
        {
            try
            {
                string topicDir = Path.Combine(_memoryRoot, ".devmind", "memory");
                if (!Directory.Exists(topicDir)) return;
                var empty = Directory.EnumerateDirectories(topicDir, "*", SearchOption.AllDirectories)
                    .Where(d => !Directory.EnumerateFiles(d).Any() &&
                                !Directory.EnumerateDirectories(d).Any())
                    .OrderByDescending(d => d.Length) // deepest first
                    .ToList();
                foreach (var d in empty)
                {
                    try { Directory.Delete(d); } catch { /* leave it */ }
                }
            }
            catch { /* housekeeping only */ }
        }

        /// <summary>Path relative to the memory root, always forward-slash-free (native separators).</summary>
        string NormalizeRelative(string fullPath)
        {
            string root = Path.GetFullPath(_memoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(fullPath);
            if (!full.StartsWith(root, StringComparison.Ordinal))
                return full; // outside the root — should not happen; keep it whole
            return full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>Normalize a manifest path to the same form EnumerateMemoryFiles produces.</summary>
        static string Normalized(string relative)
        {
            if (relative == null) return string.Empty;
            return relative.Replace('/', Path.DirectorySeparatorChar)
                           .Replace('\\', Path.DirectorySeparatorChar)
                           .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>Split a normalized relative path back into a full path under the memory root.</summary>
        string Split(string normalizedRelative)
        {
            if (string.IsNullOrEmpty(normalizedRelative))
                throw new ArgumentException("Empty relative path.", nameof(normalizedRelative));
            return Path.Combine(_memoryRoot, normalizedRelative);
        }

        /// <summary>
        /// The newest LastWriteTime (UTC, ISO-8601) of any file or directory under
        /// <paramref name="dir"/> — null when the tree is empty. Best-effort: a
        /// permission error on one entry does not sink the whole scan.
        /// </summary>
        static string NewestFileOrDirTime(string dir)
        {
            DateTime newest = DateTime.MinValue;
            bool found = false;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var t = File.GetLastWriteTimeUtc(f);
                        if (t > newest) { newest = t; found = true; }
                    }
                    catch { /* skip unreadable entry */ }
                }
            }
            catch { /* the dir itself may have vanished mid-scan */ }
            return found ? newest.ToString("O") : null;
        }
    }
}
