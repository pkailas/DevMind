// File: FileCheckpoint.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The file half of /rewind (part 2b): GIT FILE CHECKPOINTS.
//
// The memory half (part 2a) snapshots the repo-memory set as copied files. This
// class is its file-tree TWIN: at the start of every user turn the working
// tree — tracked changes, untracked-not-ignored files, deletions of tracked
// files — is captured as a detached git commit that the user's repo does not
// know about, so /rewind N can list the files the abandoned turns touched and
// /rewind files can put the tree back.
//
// What it has to get right:
//
//  * THE USER'S REPO. The capture must never touch HEAD, the index, the stash,
//    or the working tree. It stages into a TEMPORARY index file
//    (GIT_INDEX_FILE=<temp>), writes a tree from it, commit-roots the tree into
//    a detached commit, and points refs/devmind/checkpoints/<session>/turn-<k>
//    at it. The real .git/index is byte-identical before and after — that is a
//    test, not a hope.
//
//  * THE BYTES. Restore must reproduce the working-tree bytes the user had,
//    not the normalised blob bytes. With core.autocrlf=true a CRLF working
//    file is stored as LF, so a restore that writes the raw blob would
//    silently re-normalise the user's file. Restores therefore read through
//    the checkout filters (git cat-file --filters blob <sha>), which are the
//    exact bytes git would write to the working tree for that path.
//
//  * THE SCOPE. Restore touches ONLY the paths the diff named — M and D paths
//    are written back, paths added since the checkpoint are deleted, and
//    everything else (ignored files, the memory set, untracked files outside
//    the plan, .git) is never opened. The memory set (MEMORY.md +
//    .devmind/memory/**) is excluded from the diff entirely: it is the memory
//    checkpoint's, and restoring it here would double-apply.
//
//  * THE FAILURES. Capture never blocks or breaks a turn: the whole sequence
//    is time-boxed (20 s) and any git failure is recorded as "capture
//    failed" in the manifest and swallowed. A failed capture means a later
//    /rewind says "no file checkpoint for turn N — unchanged", nothing worse.
//
// Layout: the per-turn manifest (files-manifest.json) lives next to the memory
// manifest in <checkpointRoot>\<session>\turn-<k>\ — repo root, commit sha,
// tree sha, and the capture status. The git objects themselves live in the
// repo's .git, reachable only through the refs/devmind/checkpoints refs.
//
// This class shells out to git through FileCheckpointGit (an injected seam:
// the TUI and the tests both drive real git in temp repos, the tests only
// differ in which repo they point at).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// The outcome of a turn-start file capture: the repo state the checkpoint
    /// ref points at. <see cref="Status"/> is "ok", "no-repo", or
    /// "capture-failed".
    /// </summary>
    public sealed class FileCheckpointResult
    {
        public string Status { get; }
        public string RepoRoot { get; }
        public string CommitSha { get; }
        public string TreeSha { get; }
        /// <summary>Set when the tree was identical to a previous turn's — the ref points
        /// at that turn's commit without a new one.</summary>
        public int SameAsTurn { get; }

        internal FileCheckpointResult(string status, string repoRoot, string commitSha, string treeSha, int sameAsTurn)
        {
            Status = status;
            RepoRoot = repoRoot ?? string.Empty;
            CommitSha = commitSha ?? string.Empty;
            TreeSha = treeSha ?? string.Empty;
            SameAsTurn = sameAsTurn;
        }

        internal static FileCheckpointResult NoRepo() => new FileCheckpointResult("no-repo", null, null, null, 0);
        internal static FileCheckpointResult Failed() => new FileCheckpointResult("capture-failed", null, null, null, 0);
    }

    /// <summary>
    /// Git file checkpoints per session and turn: capture the working tree at
    /// the start of a turn as a detached commit under
    /// refs/devmind/checkpoints/&lt;session&gt;/turn-&lt;k&gt;, and restore the
    /// working tree to a checkpoint on /rewind. The git plumbing lives in
    /// <see cref="FileCheckpointGit"/> (injected, so tests drive temp repos);
    /// this class owns the manifest, the diff, and the restore plan.
    /// </summary>
    public sealed class FileCheckpoint
    {
        /// <summary>The whole capture sequence is time-boxed so a slow repo (huge
        /// untracked sets, network drives) cannot stall a turn.</summary>
        public const int CaptureTimeoutSeconds = 20;

        readonly string _checkpointRoot;
        readonly string _sessionId;
        readonly string _workingDirectory;
        readonly FileCheckpointGit _git;

        /// <param name="checkpointRoot">Where all sessions' checkpoint folders live
        /// (default <see cref="MemoryCheckpoint.DefaultRoot"/> — the file manifest lands
        /// next to the memory manifest in the same turn folder).</param>
        /// <param name="sessionId">The session this instance snapshots for.</param>
        /// <param name="workingDirectory">The TUI's working directory — the directory
        /// that must be inside a git repo for capture to do anything.</param>
        /// <param name="git">The git seam. Null uses the default (ShellRunner in the
        /// working directory); tests inject one pointed at a temp repo.</param>
        public FileCheckpoint(string checkpointRoot, string sessionId, string workingDirectory, FileCheckpointGit git = null)
        {
            _checkpointRoot = checkpointRoot ?? MemoryCheckpoint.DefaultRoot();
            _sessionId = sessionId ?? string.Empty;
            _workingDirectory = workingDirectory ?? string.Empty;
            _git = git ?? new FileCheckpointGit(_workingDirectory);
        }

        // ── Capture ───────────────────────────────────────────────────────────

        /// <summary>
        /// Snapshot the working tree as turn <paramref name="turnNumber"/>.
        /// Never throws and never blocks past the time-box: a non-repo working
        /// directory, a timed-out or failing git sequence, and any unexpected
        /// fault all produce a manifest with a status line and let the turn
        /// proceed. Returns the capture outcome (null only when the manifest
        /// itself could not be written — the turn is unaffected either way).
        /// </summary>
        public async Task<FileCheckpointResult> SnapshotAtTurnStartAsync(int turnNumber)
        {
            FileCheckpointResult result;
            try
            {
                if (turnNumber < 1) return FileCheckpointResult.Failed();

                string repoRoot = _git.RepoRoot();
                if (repoRoot == null)
                {
                    result = FileCheckpointResult.NoRepo();
                }
                else
                {
                    using var cts = new CancellationTokenSource(
                        TimeSpan.FromSeconds(CaptureTimeoutSeconds));
                var captured = await CaptureAsync(repoRoot, turnNumber, cts.Token);
                result = captured ?? FileCheckpointResult.Failed();
                }
            }
            catch
            {
                // Capture must never break a turn — a failed manifest is the
                // worst the next /rewind can see, and it says so.
                result = FileCheckpointResult.Failed();
            }

            WriteFilesManifest(turnNumber, result);
            return result;
        }

        async Task<FileCheckpointResult> CaptureAsync(string repoRoot, int turnNumber, CancellationToken cancellationToken)
        {
            string tempIndex = Path.Combine(
                Path.GetTempPath(), $"devmind-fcp-index-{Guid.NewGuid():N}");
            try
            {
                // Seed a TEMPORARY index from HEAD (empty when there is no HEAD):
                // the user's .git/index is never opened for write by any of this.
                if (!_git.TryReadTree(repoRoot, tempIndex, out _)) return null;

                // Stage the working tree — tracked changes, deletions of tracked
                // files, and untracked-not-ignored files — into the temp index.
                if (!_git.TryAddAll(repoRoot, tempIndex, out _)) return null;

                string treeSha = _git.TryWriteTree(repoRoot, tempIndex, out _);
                if (treeSha == null) return null;

                // Identical-tree case: point the ref at the previous turn's commit
                // and skip the commit entirely — the objects are already in the
                // repo, so the checkpoint is one update-ref.
                int sameAs = FindIdenticalTurn(treeSha, turnNumber);
                string commitSha;
                if (sameAs > 0)
                {
                    commitSha = _git.ReadRef(repoRoot, RefName(sameAs));
                    if (commitSha == null)
                    {
                        // The previous turn's ref is gone (pruned?) — fall through
                        // to a fresh commit so this turn is not left uncheckpointed.
                        commitSha = _git.TryCommitTree(repoRoot, treeSha,
                            $"devmind checkpoint {_sessionId} turn {turnNumber}", out _);
                        sameAs = 0;
                    }
                }
                else
                {
                    commitSha = _git.TryCommitTree(repoRoot, treeSha,
                        $"devmind checkpoint {_sessionId} turn {turnNumber}", out _);
                }
                if (commitSha == null) return null;

                if (!_git.TryUpdateRef(repoRoot, RefName(turnNumber), commitSha, out _))
                    return null;

                return new FileCheckpointResult("ok", repoRoot, commitSha, treeSha, sameAs);
            }
            finally
            {
                try { if (File.Exists(tempIndex)) File.Delete(tempIndex); }
                catch { /* temp file — nothing to do */ }
            }
        }

        /// <summary>
        /// The newest earlier turn (newest first, this turn excluded) whose tree
        /// equals <paramref name="treeSha"/> — 0 when none matches.
        /// </summary>
        int FindIdenticalTurn(string treeSha, int currentTurn)
        {
            string sessionDir = Path.Combine(_checkpointRoot, _sessionId);
            if (!Directory.Exists(sessionDir)) return 0;

            int best = 0;
            foreach (var dir in Directory.GetDirectories(sessionDir))
            {
                string name = Path.GetFileName(dir);
                if (!name.StartsWith("turn-")) continue;
                if (!int.TryParse(name.Substring("turn-".Length), out int k) || k >= currentTurn) continue;
                FileCheckpointFilesManifest m;
                try { m = LoadFilesManifest(dir); } catch { continue; }
                if (m == null || m.Status != "ok" || !string.Equals(m.TreeSha, treeSha, StringComparison.Ordinal))
                    continue;
                if (k > best) best = k;
            }
            return best;
        }

        // ── Restore ───────────────────────────────────────────────────────────

        /// <summary>One line of the /rewind file listing / restore plan.</summary>
        public sealed class FileDiffEntry
        {
            /// <summary>A / M / D as git diff-tree reports it.</summary>
            public string Status { get; }
            /// <summary>Path relative to the repo root, forward slashes.</summary>
            public string Path { get; }
            /// <summary>True when the path ALSO differs between the newest checkpoint
            /// and now — it changed after the last turn started, so the diff may be
            /// the user's own edit, not the abandoned turn's.</summary>
            public bool PossiblyOwnEdit { get; }

            internal FileDiffEntry(string status, string path, bool possiblyOwnEdit)
            {
                Status = status;
                Path = path;
                PossiblyOwnEdit = possiblyOwnEdit;
            }
        }

        /// <summary>
        /// The working-file differences between NOW and a session's turn-
        /// <paramref name="turnNumber"/> checkpoint, with the memory set excluded
        /// and each path that also differs from the newest checkpoint flagged as
        /// possibly the user's own edit. Null when there is no file checkpoint
        /// for that turn (the working tree is left exactly as it is).
        /// </summary>
        public async Task<IReadOnlyList<FileDiffEntry>> ComputeRestoreDiffAsync(string sessionId, int turnNumber)
        {
            var manifest = LoadStoredFilesManifest(sessionId, turnNumber);
            if (manifest == null || manifest.Status != "ok"
                || string.IsNullOrEmpty(manifest.RepoRoot)
                || string.IsNullOrEmpty(manifest.TreeSha))
                return null;

            string repoRoot = manifest.RepoRoot;
            if (!Directory.Exists(Path.Combine(repoRoot, ".git"))) return null;

            var git = new FileCheckpointGit(repoRoot);
            string tempIndex = Path.Combine(
                Path.GetTempPath(), $"devmind-fcp-index-{Guid.NewGuid():N}");
            try
            {
                if (!git.TryReadTree(repoRoot, tempIndex, out _)) return null;
                if (!git.TryAddAll(repoRoot, tempIndex, out _)) return null;
                string currentTree = git.TryWriteTree(repoRoot, tempIndex, out _);
                if (currentTree == null) return null;

                var lines = git.DiffTrees(repoRoot, manifest.TreeSha, currentTree);
                if (lines == null) return null;

                // The "possibly your own edit" flag: paths that ALSO differ between
                // the newest checkpoint and now. The newest checkpoint is the
                // highest-numbered ok manifest in this session's folder.
                var newest = NewestOkManifest(sessionId);
                HashSet<string> recentPaths;
                if (newest != null &&
                    !string.Equals(newest.TreeSha, manifest.TreeSha, StringComparison.Ordinal))
                {
                    var recentLines = git.DiffTrees(repoRoot, newest.TreeSha, currentTree);
                    recentPaths = new HashSet<string>(
                        (recentLines ?? Array.Empty<string>())
                            .Select(l => l.Split('\t', 3)[1])
                            .Where(p => !IsMemoryPath(p)),
                        StringComparer.Ordinal);
                }
                else
                {
                    recentPaths = new HashSet<string>(StringComparer.Ordinal);
                }

                var entries = new List<FileDiffEntry>();
                foreach (var raw in lines)
                {
                    // git diff-tree --name-status: "STATUS\tPATH" (renames add a third
                    // field, "R100\told\tnew") — two parts is the normal case.
                    var parts = raw.Split('\t');
                    if (parts.Length < 2) continue;
                    string status = parts[0];
                    string path = parts[1];
                    if (status == "R" || status == "C") continue; // renames: not restored
                    if (IsMemoryPath(path)) continue;             // the memory checkpoint owns this
                    if (path.StartsWith(".git/", StringComparison.Ordinal)) continue;
                    entries.Add(new FileDiffEntry(status, path, recentPaths.Contains(path)));
                }
                return entries;
            }
            finally
            {
                try { if (File.Exists(tempIndex)) File.Delete(tempIndex); }
                catch { /* temp file */ }
            }
        }

        /// <summary>
        /// Apply a restore plan to the working tree: paths the checkpoint had and
        /// the tree now does not (D, and M for completeness) are written back
        /// byte-for-byte through the checkout filters; paths the tree has that the
        /// checkpoint did not (A) are deleted. Only the given paths are ever
        /// touched — ignored files, the memory set, and anything not in the list
        /// are left alone. Returns (restored, deleted, errors).
        /// </summary>
        public async Task<(int restored, int deleted, string errors)> ApplyRestoreAsync(
            string repoRoot, string checkpointCommit, IReadOnlyList<FileDiffEntry> plan)
        {
            if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(Path.Combine(repoRoot, ".git")))
                return (0, 0, "no repo at the recorded root");
            if (string.IsNullOrEmpty(checkpointCommit))
                return (0, 0, "no checkpoint commit in the plan");
            if (plan == null || plan.Count == 0)
                return (0, 0, "empty plan");

            var git = new FileCheckpointGit(repoRoot);
            int restored = 0, deleted = 0;
            var problems = new List<string>();

            foreach (var entry in plan)
            {
                if (entry.Status == "A")
                {
                    // Added since the checkpoint: the checkpoint has no blob for it —
                    // delete the file (and its now-empty directories).
                    string full = Path.Combine(repoRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        if (File.Exists(full))
                        {
                            File.Delete(full);
                            deleted++;
                            PruneEmptyAncestors(full, repoRoot);
                        }
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"{entry.Path}: {ex.Message}");
                    }
                }
                else // M or D: write the checkpoint's bytes back.
                {
                    byte[] bytes = git.ReadBlobWithFilters(repoRoot, checkpointCommit, entry.Path);
                    if (bytes == null)
                    {
                        problems.Add($"{entry.Path}: blob read failed");
                        continue;
                    }
                    string full = Path.Combine(repoRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        string parent = Path.GetDirectoryName(full) ?? repoRoot;
                        Directory.CreateDirectory(parent);
                        File.WriteAllBytes(full, bytes);
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"{entry.Path}: {ex.Message}");
                    }
                }
            }

            return (restored, deleted,
                problems.Count > 0 ? string.Join("; ", problems) : string.Empty);
        }

        /// <summary>
        /// True when the /rewind file plan for this session and turn is usable:
        /// a stored ok manifest exists and its repo is still a repo.
        /// </summary>
        public bool HasCheckpoint(string sessionId, int turnNumber)
        {
            var m = LoadStoredFilesManifest(sessionId, turnNumber);
            return m != null && m.Status == "ok" &&
                   !string.IsNullOrEmpty(m.RepoRoot) &&
                   Directory.Exists(Path.Combine(m.RepoRoot, ".git"));
        }

        // ── Fork support ──────────────────────────────────────────────────────

        /// <summary>
        /// Give a fork session the original's file checkpoint refs for turns
        /// 1..<paramref name="upTo"/> (inclusive): the refs point at the SAME
        /// commits (the objects are already in the repo), so a /rewind inside
        /// the fork restores from them. Manifests are copied alongside by
        /// <see cref="MemoryCheckpoint.CopyCheckpoints"/>. Never throws.
        /// </summary>
        public void CopyRefs(string sourceSessionId, string targetSessionId, int upTo)
        {
            try
            {
                if (upTo < 1) return;
                string sourceDir = Path.Combine(_checkpointRoot, sourceSessionId);
                if (!Directory.Exists(sourceDir)) return;

                string repoRoot = null;
                for (int k = 1; k <= upTo && repoRoot == null; k++)
                {
                    try
                    {
                        var m = LoadFilesManifest(Path.Combine(sourceDir, $"turn-{k}"));
                        if (m != null && m.Status == "ok") repoRoot = m.RepoRoot;
                    }
                    catch { /* keep looking */ }
                }
                if (repoRoot == null || !Directory.Exists(Path.Combine(repoRoot, ".git"))) return;

                var git = new FileCheckpointGit(repoRoot);
                for (int k = 1; k <= upTo; k++)
                {
                    try
                    {
                        var m = LoadFilesManifest(Path.Combine(sourceDir, $"turn-{k}"));
                        if (m == null || m.Status != "ok" || string.IsNullOrEmpty(m.CommitSha))
                            continue;
                        git.TryUpdateRef(repoRoot, RefNameFor(targetSessionId, k), m.CommitSha, out _);
                    }
                    catch { /* a missing turn ref is a later "no checkpoint" — no worse */ }
                }
            }
            catch
            {
                // A fork without file refs can only mean a later /rewind inside it
                // reports "no file checkpoint" — never worse than not having this.
            }
        }

        // ── Pruning ───────────────────────────────────────────────────────────

        /// <summary>
        /// When a session's checkpoint folder is pruned, delete its refs too:
        /// refs/devmind/checkpoints/&lt;session&gt;/turn-<k> in every repo the
        /// session's manifests recorded. Best-effort — a leftover ref is dead
        /// weight, not a hazard, so a failure here is nothing.
        /// </summary>
        public static void DeleteRefsForSession(string checkpointRoot, string sessionId)
        {
            try
            {
                string sessionDir = Path.Combine(checkpointRoot, sessionId);
                if (!Directory.Exists(sessionDir)) return;

                var repoRoots = new HashSet<string>(StringComparer.Ordinal);
                var turns = new List<int>();
                foreach (var dir in Directory.GetDirectories(sessionDir))
                {
                    string name = Path.GetFileName(dir);
                    if (!name.StartsWith("turn-")) continue;
                    if (!int.TryParse(name.Substring("turn-".Length), out int k) || k < 1) continue;
                    turns.Add(k);
                    try
                    {
                        var m = LoadFilesManifest(dir);
                        if (m != null && m.Status == "ok" && !string.IsNullOrEmpty(m.RepoRoot))
                            repoRoots.Add(m.RepoRoot);
                    }
                    catch { /* unreadable manifest — no repo recorded */ }
                }

                foreach (var repoRoot in repoRoots)
                    DeleteSessionRefs(repoRoot, sessionId, turns);
            }
            catch
            {
                // Pruning is best-effort housekeeping — never a crash path.
            }
        }

        /// <summary>
        /// Delete a session's checkpoint refs from a KNOWN repo root and turn list —
        /// the form the prune path uses, because <see cref="MemoryCheckpoint.PruneAll"/>
        /// has already deleted the session folder (and its manifests) by the time the
        /// refs are deleted, so the repo root and turns must be captured beforehand.
        /// </summary>
        public static void DeleteSessionRefs(string repoRoot, string sessionId, IEnumerable<int> turns)
        {
            if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(Path.Combine(repoRoot, ".git"))) return;
            var git = new FileCheckpointGit(repoRoot);
            foreach (int k in turns)
            {
                try { git.TryDeleteRef(repoRoot, RefNameFor(sessionId, k), out _); }
                catch { /* the ref may not exist — nothing to do */ }
            }
        }

        // ── Internals ─────────────────────────────────────────────────────────

        sealed class FileCheckpointFilesManifest
        {
            public int Turn { get; set; }
            public string SessionId { get; set; }
            public string RepoRoot { get; set; }
            public string CommitSha { get; set; }
            public string TreeSha { get; set; }
            public string Status { get; set; }
            public string TakenAt { get; set; }
            public int SameAsTurn { get; set; }
        }

        const string FilesManifestName = "files-manifest.json";

        static string RefNameFor(string sessionId, int turn)
            => $"refs/devmind/checkpoints/{sessionId}/turn-{turn}";

        string RefName(int turn) => RefNameFor(_sessionId, turn);

        void WriteFilesManifest(int turn, FileCheckpointResult result)
        {
            try
            {
                string dir = Path.Combine(_checkpointRoot, _sessionId, $"turn-{turn}");
                Directory.CreateDirectory(dir);
                var manifest = new FileCheckpointFilesManifest
                {
                    Turn = turn,
                    SessionId = _sessionId,
                    RepoRoot = result.RepoRoot,
                    CommitSha = result.CommitSha,
                    TreeSha = result.TreeSha,
                    Status = result.Status,
                    TakenAt = DateTime.UtcNow.ToString("O"),
                    SameAsTurn = result.SameAsTurn,
                };
                File.WriteAllText(Path.Combine(dir, FilesManifestName),
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // A missing manifest means /rewind says "no file checkpoint" —
                // the turn itself is already over and unaffected.
            }
        }

        static FileCheckpointFilesManifest LoadFilesManifest(string turnDir)
        {
            string path = Path.Combine(turnDir, FilesManifestName);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<FileCheckpointFilesManifest>(json);
        }

        FileCheckpointFilesManifest LoadStoredFilesManifest(string sessionId, int turn)
        {
            try
            {
                string dir = Path.Combine(_checkpointRoot, sessionId, $"turn-{turn}");
                if (!Directory.Exists(dir)) return null;
                return LoadFilesManifest(dir);
            }
            catch { return null; }
        }

        FileCheckpointFilesManifest NewestOkManifest(string sessionId)
        {
            string sessionDir = Path.Combine(_checkpointRoot, sessionId);
            if (!Directory.Exists(sessionDir)) return null;

            FileCheckpointFilesManifest best = null;
            int bestTurn = -1;
            foreach (var dir in Directory.GetDirectories(sessionDir))
            {
                string name = Path.GetFileName(dir);
                if (!name.StartsWith("turn-")) continue;
                if (!int.TryParse(name.Substring("turn-".Length), out int k) || k <= bestTurn) continue;
                try
                {
                    var m = LoadFilesManifest(dir);
                    if (m != null && m.Status == "ok")
                    {
                        best = m;
                        bestTurn = k;
                    }
                }
                catch { /* unreadable — skip */ }
            }
            return best;
        }

        /// <summary>
        /// The memory set, as a repo-relative path: MEMORY.md at the repo root,
        /// or anything under .devmind/memory. The memory checkpoint owns these —
        /// the file diff and restore must never include them (double-restore).
        /// </summary>
        static bool IsMemoryPath(string repoRelative)
        {
            if (string.IsNullOrEmpty(repoRelative)) return false;
            string p = repoRelative.Replace('\\', '/');
            return p == "MEMORY.md"
                || p.StartsWith(".devmind/memory/", StringComparison.Ordinal);
        }

        /// <summary>
        /// After deleting a file, drop the empty directories it left behind —
        /// down to (not including) the repo root.
        /// </summary>
        static void PruneEmptyAncestors(string deletedFile, string repoRoot)
        {
            string root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string dir = Path.GetDirectoryName(Path.GetFullPath(deletedFile));
            while (dir != null && dir.Length > root.Length &&
                   dir.StartsWith(root, StringComparison.Ordinal))
            {
                try
                {
                    if (Directory.EnumerateFiles(dir).Any() || Directory.EnumerateDirectories(dir).Any())
                        break;
                    Directory.Delete(dir);
                }
                catch { break; /* leave it — housekeeping only */ }
                dir = Path.GetDirectoryName(dir);
            }
        }

        // ── Git plumbing ──────────────────────────────────────────────────────
    }

    /// <summary>
    /// The git sequence behind the file checkpoints, one method per command.
    /// Every method is failure-tolerant: a non-zero exit, a timeout, or a
    /// missing git yields null/false and an <c>error</c> out-param, never an
    /// exception — the capture path is on a turn's critical path and must not
    /// break it. The child process gets a normal environment (ProcessStartInfo
    /// inherits the parent's — PATH, SystemDrive, ProgramData, all of it) plus
    /// GIT_TERMINAL_PROMPT=0 so no credential prompt can ever hang a turn.
    /// </summary>
    public sealed class FileCheckpointGit
    {
        /// <param name="workingDirectory">The directory <see cref="RepoRoot"/> resolves
        /// the repo from. Every other method takes its repo root as an argument, so this
        /// is only the starting point for the toplevel lookup.</param>
        public FileCheckpointGit(string workingDirectory)
        {
            _workingDirectory = workingDirectory ?? Environment.CurrentDirectory;
        }

        readonly string _workingDirectory;

        /// <summary>
        /// The repo root containing <see cref="_workingDirectory"/> — the answer to
        /// <c>git rev-parse --show-toplevel</c> — or null when the directory is not
        /// inside a git repo (or git is missing).
        /// </summary>
        public string RepoRoot()
        {
            var (output, exitCode) = Run(_workingDirectory, "rev-parse", "--show-toplevel");
            if (exitCode != 0) return null;
            string root = output.Trim();
            return string.IsNullOrEmpty(root) ? null : root;
        }

        /// <summary>
        /// <c>git read-tree HEAD</c> into <paramref name="indexFile"/> — seeding
        /// the temporary index from HEAD. A repo with no HEAD yet (no commits)
        /// is a success with an empty index: the tree the capture then writes
        /// is purely the untracked-not-ignored set, which is exactly what a
        /// first-turn checkpoint should be.
        /// </summary>
        public bool TryReadTree(string repoRoot, string indexFile, out string error)
        {
            var (output, exitCode) = RunWithIndex(repoRoot, indexFile, "read-tree", "HEAD");
            // "fatal: Not a valid object name: HEAD" is the no-HEAD case — an
            // empty index is the correct seed for it, not a failure.
            if (exitCode == 0 || output.Contains("Not a valid object name: HEAD"))
            {
                error = null;
                return true;
            }
            error = output;
            return false;
        }

        /// <summary>
        /// <c>git add -A</c> from the repo root into <paramref name="indexFile"/>:
        /// stages tracked changes, deletions of tracked files, and
        /// untracked-not-ignored files. Respects .gitignore.
        /// </summary>
        public bool TryAddAll(string repoRoot, string indexFile, out string error)
        {
            var (output, exitCode) = RunWithIndex(repoRoot, indexFile, "add", "-A");
            if (exitCode == 0)
            {
                error = null;
                return true;
            }
            error = output;
            return false;
        }

        /// <summary>
        /// <c>git write-tree</c> from <paramref name="indexFile"/> — the tree id
        /// of the staged contents, or null on failure.
        /// </summary>
        public string TryWriteTree(string repoRoot, string indexFile, out string error)
        {
            var (output, exitCode) = RunWithIndex(repoRoot, indexFile, "write-tree");
            if (exitCode != 0)
            {
                error = output;
                return null;
            }
            error = null;
            return output.Trim();
        }

        /// <summary>
        /// <c>git commit-tree &lt;tree&gt; -m &lt;msg&gt;</c> — a detached commit
        /// (no parent, no branch moved), or null on failure.
        /// </summary>
        public string TryCommitTree(string repoRoot, string treeSha, string message, out string error)
        {
            var (output, exitCode) = Run(repoRoot, "commit-tree", treeSha, "-m", message);
            if (exitCode != 0)
            {
                error = output;
                return null;
            }
            error = null;
            return output.Trim();
        }

        /// <summary>
        /// <c>git update-ref &lt;ref&gt; &lt;commit&gt;</c> — point the checkpoint
        /// ref at the commit. True on success.
        /// </summary>
        public bool TryUpdateRef(string repoRoot, string refName, string commitSha, out string error)
        {
            var (output, exitCode) = Run(repoRoot, "update-ref", refName, commitSha);
            if (exitCode == 0)
            {
                error = null;
                return true;
            }
            error = output;
            return false;
        }

        /// <summary>
        /// <c>git update-ref -d &lt;ref&gt;</c> — delete a checkpoint ref.
        /// Missing refs are a success.
        /// </summary>
        public bool TryDeleteRef(string repoRoot, string refName, out string error)
        {
            var (output, exitCode) = Run(repoRoot, "update-ref", "-d", refName);
            if (exitCode == 0)
            {
                error = null;
                return true;
            }
            error = output;
            return false;
        }

        /// <summary>
        /// <c>git rev-parse &lt;ref&gt;^{commit}</c> — the commit a checkpoint ref
        /// points at, or null when the ref does not exist.
        /// </summary>
        public string ReadRef(string repoRoot, string refName)
        {
            var (output, exitCode) = Run(repoRoot, "rev-parse", refName + "^{commit}");
            if (exitCode != 0) return null;
            string sha = output.Trim();
            return string.IsNullOrEmpty(sha) ? null : sha;
        }

        /// <summary>
        /// <c>git diff-tree -r --name-status &lt;treeA&gt; &lt;treeB&gt;</c> — one
        /// "STATUS\tPATH" line per differing path, or null on failure. Paths
        /// with spaces are tab-delimited (git quotes paths with special
        /// characters; a tab inside a quoted path would need -z, but a path
        /// with a tab is not a file the restore can address anyway).
        /// </summary>
        public string[] DiffTrees(string repoRoot, string treeA, string treeB)
        {
            var (output, exitCode) = Run(repoRoot, "diff-tree", "-r", "--name-status", treeA, treeB);
            if (exitCode != 0) return null;
            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToArray();
        }

        /// <summary>
        /// The working-tree bytes git would write for <c>&lt;commit&gt;:&lt;path&gt;</c>:
        /// <c>git cat-file --filters &lt;commit&gt;:&lt;path&gt;</c>. This is the
        /// checkout-filtered content — with core.autocrlf=true a CRLF working file
        /// round-trips as CRLF, not the LF normalised blob. (The <c>--path=&lt;path&gt;
        /// &lt;commit&gt;</c> form dumps the COMMIT object, not the blob — the rev:path
        /// form is the one that resolves to the path's blob and runs the filters on it.)
        /// Run directly (not through the line-buffered ShellRunner) so binary bytes
        /// survive byte-for-byte. Null on any failure.
        /// </summary>
        public byte[] ReadBlobWithFilters(string repoRoot, string commitSha, string path)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git");
            psi.ArgumentList.Add("cat-file");
            psi.ArgumentList.Add("--filters");
            psi.ArgumentList.Add(commitSha + ":" + path);
            psi.WorkingDirectory = repoRoot;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GIT_REDIRECT_STDIN"] = "off";
            psi.EnvironmentVariables["GIT_REDIRECT_STDERR"] = "2>&1";

            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = psi;
            try
            {
                if (!proc.Start()) return null;
                using var ms = new MemoryStream();
                proc.StandardOutput.BaseStream.CopyTo(ms);
                string stderr = proc.StandardError.ReadToEnd();
                var timeout = TimeSpan.FromSeconds(FileCheckpoint.CaptureTimeoutSeconds);
                if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    return null;
                }
                if (proc.ExitCode != 0) return null;
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        (string output, int exitCode) Run(string repoRoot, params string[] args)
            => RunCore(repoRoot, null, args);

        (string output, int exitCode) RunWithIndex(string repoRoot, string indexFile, params string[] args)
            => RunCore(repoRoot, indexFile, args);

        (string output, int exitCode) RunCore(string repoRoot, string indexFile, string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git");
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.WorkingDirectory = repoRoot;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GIT_REDIRECT_STDIN"] = "off";
            psi.EnvironmentVariables["GIT_REDIRECT_STDERR"] = "2>&1";
            if (indexFile != null)
                psi.EnvironmentVariables["GIT_INDEX_FILE"] = indexFile;

            try
            {
                using var proc = new System.Diagnostics.Process();
                proc.StartInfo = psi;
                if (!proc.Start()) return ("git did not start", 127);
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                var timeout = TimeSpan.FromSeconds(FileCheckpoint.CaptureTimeoutSeconds);
                if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    return ("timed out", 124);
                }
                // stderr is folded into the output: the capture path only uses it
                // as an error message, and a non-zero exit is the real signal.
                return (stdout + (stderr.Length > 0 ? "\n" + stderr : string.Empty), proc.ExitCode);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // git not on PATH — the "fail loudly" contract is the test's;
                // in the TUI a missing git means "capture failed", not a crash.
                return ("git not found on PATH", 127);
            }
            catch
            {
                return ("git spawn failed", 127);
            }
        }
    }
}
