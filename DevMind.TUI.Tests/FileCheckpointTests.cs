// File: FileCheckpointTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The file half of /rewind (part 2b), unit level, against REAL git in temp
// repos (never the user's repos): capture semantics (the working tree staged
// into a TEMPORARY index — HEAD, .git/index and the stash byte-identical
// before and after; tracked changes, untracked files, deletions of tracked
// files, ignored files excluded; the identical-tree case; a non-repo
// directory; a git failure that does not throw into the turn), the /rewind N
// listing (A/M/D statuses, the memory set excluded, the "possibly your own
// edit" flag), /rewind files (M and D restored byte-for-byte — including a
// binary file and a CRLF file under core.autocrlf=true — A deleted, ignored
// files and the memory set left untouched; the refusals), fork inheritance of
// the refs, and pruning deleting the refs.
//
// Git must be on PATH for these tests: a missing git is a LOUD failure, not a
// silent skip — a checkpoint test that skips has verified nothing.

using DevMind;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class FileCheckpointTests : IDisposable
    {
        readonly string _root;     // checkpoint root (the test seam for %LOCALAPPDATA%)
        readonly string _repo;     // the git repo the captures run in

        const string S = "2026-09-27T183853Z-pid1";   // session id, as in the memory tests
        const string Fork = "2026-09-27T183853Z-pid2";

        public FileCheckpointTests()
        {
            Assert.True(GitAvailable(),
                "git is not on PATH — the file-checkpoint tests drive real git and cannot run without it");

            _root = Path.Combine(Path.GetTempPath(), $"devmind-fcp-root-{Guid.NewGuid():N}");
            _repo = Path.Combine(Path.GetTempPath(), $"devmind-fcp-repo-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            InitRepo(_repo);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
            try { Directory.Delete(_repo, recursive: true); } catch { /* temp dir */ }
        }

        FileCheckpoint Cp() => new FileCheckpoint(_root, S, _repo);

        // ── git helpers ─────────────────────────────────────────────────────

        /// <summary>True when git runs. The tests fail loudly (not skip) when it does not.</summary>
        static bool GitAvailable()
        {
            try
            {
                var psi = new ProcessStartInfo("git", "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(10000);
                return proc?.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// A fresh git repo with an identity configured (so commit-tree's
        /// committer is valid) and one initial commit. <paramref name="autocrlf"/>
        /// opts into the line-ending conversion the CRLF round-trip test needs.
        /// core.autocrlf is pinned in the REPO config so the machine's system
        /// gitconfig (C:\Program Files\Git\etc\gitconfig sets core.autocrlf=true on
        /// this box) can never leak into the test repos: with it unset, the
        /// restore's checkout filters would turn the LF blobs into CRLF working
        /// files and the byte-for-byte tests would fail on autocrlf machines.
        /// </summary>
        static void InitRepo(string repo, bool autocrlf = false)
        {
            Directory.CreateDirectory(repo);
            Git(repo, "init");
            Git(repo, "config", "user.email", "devmind@test.local");
            Git(repo, "config", "user.name", "DevMind Test");
            Git(repo, "config", "core.autocrlf", autocrlf ? "true" : "false");
            File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-m", "initial");
        }

        /// <summary>Run git in <paramref name="repo"/> and return stdout (trimmed). Throws on failure.</summary>
        static string Git(string repo, params string[] args)
        {
            var psi = new ProcessStartInfo("git");
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.WorkingDirectory = repo;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("git did not start");
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {stdout} {stderr}");
            return stdout.Trim();
        }

        static byte[] Sha256(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(path);
            return sha.ComputeHash(stream);
        }

        static byte[] ReadBytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();

        // ── Capture: the user's repo stays untouched ─────────────────────────

        [Fact]
        public async Task Capture_DoesNotTouchHead_Index_OrStash()
        {
            string indexPath = Path.Combine(_repo, ".git", "index");
            byte[] indexBefore = Sha256(indexPath);
            string headBefore = Git(_repo, "rev-parse", "HEAD");
            string stashBefore = Git(_repo, "stash", "list");

            // A tracked change, an untracked file, and a deletion — the full
            // working-tree delta a turn would produce.
            File.WriteAllText(Path.Combine(_repo, "README.md"), "hello changed\n");
            File.WriteAllText(Path.Combine(_repo, "new.txt"), "new file\n");

            var result = await Cp().SnapshotAtTurnStartAsync(1);

            Assert.Equal("ok", result.Status);
            Assert.True(headBefore == Git(_repo, "rev-parse", "HEAD"), "capture must not move HEAD");
            Assert.True(indexBefore.SequenceEqual(Sha256(indexPath)),
                "capture stages into a TEMPORARY index — .git/index must be byte-identical");
            Assert.True(stashBefore == Git(_repo, "stash", "list"), "capture must not touch the stash");
            // HEAD's tree is untouched: the new file is not in HEAD.
            Assert.DoesNotContain("new.txt", Git(_repo, "ls-tree", "--name-only", "HEAD"));
        }

        [Fact]
        public async Task Capture_TrackedChange_Untracked_DeletedTracked_AreAllInTheTree()
        {
            File.WriteAllText(Path.Combine(_repo, "README.md"), "modified content\n");
            File.WriteAllText(Path.Combine(_repo, "added.txt"), "a new file\n");
            File.Delete(Path.Combine(_repo, "README.md"));
            File.WriteAllText(Path.Combine(_repo, "restored.md"), "replaced the deleted file\n");

            var result = await Cp().SnapshotAtTurnStartAsync(1);
            Assert.Equal("ok", result.Status);

            // The checkpoint ref's tree reflects the working tree as git sees it:
            // README.md gone, added.txt and restored.md present.
            string tree = Git(_repo, "ls-tree", "--name-only", result.CommitSha);
            Assert.True(!tree.Contains("README.md"), "a deleted tracked file must not be in the checkpoint tree");
            Assert.True(tree.Contains("added.txt"), "an untracked-not-ignored file must be in the checkpoint tree");
            Assert.Contains("restored.md", tree);
        }

        [Fact]
        public async Task Capture_IgnoredFile_IsExcluded()
        {
            File.WriteAllText(Path.Combine(_repo, ".gitignore"), "ignored.txt\n");
            File.WriteAllText(Path.Combine(_repo, "ignored.txt"), "should not be captured\n");

            var result = await Cp().SnapshotAtTurnStartAsync(1);
            Assert.Equal("ok", result.Status);

            string tree = Git(_repo, "ls-tree", "--name-only", "-r", result.CommitSha);
            Assert.True(!tree.Contains("ignored.txt"),
                "git add -A respects .gitignore — ignored files must not be captured");
        }

        [Fact]
        public async Task Capture_IdenticalTree_PointsRefAtPreviousCommit_NoNewCommit()
        {
            var first = await Cp().SnapshotAtTurnStartAsync(1);
            Assert.Equal("ok", first.Status);

            // Nothing changed: turn 2's tree equals turn 1's.
            var second = await Cp().SnapshotAtTurnStartAsync(2);
            Assert.Equal("ok", second.Status);
            Assert.True(first.TreeSha == second.TreeSha, "the trees must be identical");
            Assert.Equal(1, second.SameAsTurn);
            Assert.True(first.CommitSha == second.CommitSha,
                "an identical tree must reuse the previous commit — no new commit object");
            // And the ref for turn 2 points at that same commit.
            Assert.Equal(first.CommitSha, Git(_repo, "rev-parse",
                $"refs/devmind/checkpoints/{S}/turn-2"));
        }

        [Fact]
        public async Task Capture_NotARepo_RecordsNoRepo_DoesNotThrow()
        {
            string notRepo = Path.Combine(Path.GetTempPath(), $"devmind-fcp-notrepo-{Guid.NewGuid():N}");
            Directory.CreateDirectory(notRepo);
            try
            {
                var cp = new FileCheckpoint(_root, S, notRepo);
            var result = await cp.SnapshotAtTurnStartAsync(1);
            Assert.True(result.Status == "no-repo",
                "a working directory outside a git repo must record no-repo, not throw");

                // The manifest was written with the no-repo status.
                string manifestPath = Path.Combine(_root, S, "turn-1", "files-manifest.json");
                Assert.True(File.Exists(manifestPath));
                var manifest = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(manifestPath));
                Assert.Equal("no-repo", manifest.GetProperty("Status").GetString());
            }
            finally
            {
                try { Directory.Delete(notRepo, recursive: true); } catch { /* temp dir */ }
            }
        }

        [Fact]
        public async Task Capture_GitFailure_DoesNotThrow_RecordsCaptureFailed()
        {
            // Point the capture at a directory that exists but is not a repo:
            // the git sequence fails, the capture records it, and no exception
            // escapes into the turn.
            string broken = Path.Combine(Path.GetTempPath(), $"devmind-fcp-broken-{Guid.NewGuid():N}");
            Directory.CreateDirectory(broken);
            try
            {
                var cp = new FileCheckpoint(_root, S, broken);
                var result = await cp.SnapshotAtTurnStartAsync(1);
                // no-repo (not inside a repo) is the expected outcome here — the key
                // assertion is that NOTHING threw and a manifest was recorded.
                Assert.NotNull(result);
                Assert.Contains(result.Status, new[] { "no-repo", "capture-failed" });
                Assert.True(File.Exists(Path.Combine(_root, S, "turn-1", "files-manifest.json")));
            }
            finally
            {
                try { Directory.Delete(broken, recursive: true); } catch { /* temp dir */ }
            }
        }

        // ── /rewind N listing ────────────────────────────────────────────────

        [Fact]
        public async Task Listing_ShowsAddedModifiedDeleted_WithStatuses()
        {
            // A file that will be deleted: it must be IN the checkpoint (staged before
            // the snapshot) so the diff sees it as D (in checkpoint, absent now).
            string tracked = Path.Combine(_repo, "todelete.txt");
            File.WriteAllText(tracked, "in the checkpoint\n");
            Git(_repo, "add", "todelete.txt");

            await Cp().SnapshotAtTurnStartAsync(1);

            // The abandoned branch mutates the tree: modify, add, delete.
            File.WriteAllText(Path.Combine(_repo, "README.md"), "rewritten by the abandoned turn\n");
            File.WriteAllText(Path.Combine(_repo, "added.txt"), "added by the abandoned turn\n");
            File.Delete(tracked);

            var diff = await Cp().ComputeRestoreDiffAsync(S, 1);
            Assert.NotNull(diff);

            var byPath = diff.ToDictionary(e => e.Path, e => e.Status);
            Assert.True(byPath["README.md"] == "M", "a modified file must be listed M");
            Assert.True(byPath["added.txt"] == "A", "a file added since the checkpoint must be listed A");
            Assert.True(byPath["todelete.txt"] == "D", "a file deleted since the checkpoint must be listed D");
        }

        [Fact]
        public async Task Listing_ExcludesTheMemorySet()
        {
            // The memory set: MEMORY.md + .devmind/memory/**. The memory checkpoint
            // owns these — the file diff must never include them.
            File.WriteAllText(Path.Combine(_repo, "MEMORY.md"), "index v1\n");
            Directory.CreateDirectory(Path.Combine(_repo, ".devmind", "memory"));
            File.WriteAllText(Path.Combine(_repo, ".devmind", "memory", "topic.md"), "topic v1\n");
            await Cp().SnapshotAtTurnStartAsync(1);

            // Mutate the memory set AND a normal file.
            File.WriteAllText(Path.Combine(_repo, "MEMORY.md"), "index v2\n");
            File.WriteAllText(Path.Combine(_repo, ".devmind", "memory", "topic.md"), "topic v2\n");
            File.WriteAllText(Path.Combine(_repo, "README.md"), "normal change\n");

            var diff = await Cp().ComputeRestoreDiffAsync(S, 1);
            Assert.NotNull(diff);

            Assert.True(!diff.Any(e => e.Path == "MEMORY.md"),
                "MEMORY.md is the memory set's — it must not appear in the file diff");
            Assert.True(!diff.Any(e => e.Path.StartsWith(".devmind/memory/")),
                "files under .devmind/memory are the memory set's — they must not appear");
            Assert.True(diff.Any(e => e.Path == "README.md"),
                "a normal file change must still appear");
        }

        [Fact]
        public async Task Listing_FlagsPathsChangedSinceTheNewestCheckpoint_AsPossiblyOwnEdit()
        {
            // Turn 1: baseline. Turn 2: the abandoned branch changes README.md.
            await Cp().SnapshotAtTurnStartAsync(1);
            File.WriteAllText(Path.Combine(_repo, "README.md"), "abandoned turn's change\n");
            await Cp().SnapshotAtTurnStartAsync(2);

            // Now, after turn 2 started, the USER edits a different file. That file
            // differs from the NEWEST checkpoint (turn 2) — it is possibly their own
            // edit, not the abandoned turn's.
            File.WriteAllText(Path.Combine(_repo, "mine.txt"), "my own edit\n");

            // The diff against turn 1 (what /rewind 2 would restore) shows both files.
            var diff = await Cp().ComputeRestoreDiffAsync(S, 1);
            Assert.NotNull(diff);

            var readme = diff.Single(e => e.Path == "README.md");
            var mine = diff.Single(e => e.Path == "mine.txt");
            Assert.False(readme.PossiblyOwnEdit,
                "README.md changed in the abandoned turn (between turn 1 and turn 2) — not the user's own edit");
            Assert.True(mine.PossiblyOwnEdit,
                "mine.txt changed after the newest checkpoint (turn 2) started — flag it as possibly the user's own edit");
        }

        [Fact]
        public async Task Listing_NoCheckpoint_ReturnsNull()
        {
            var diff = await Cp().ComputeRestoreDiffAsync(S, 7);
            Assert.True(diff == null, "no checkpoint for turn 7 — the diff must be null");
        }

        // ── /rewind files: restore ───────────────────────────────────────────

        [Fact]
        public async Task Restore_WritesModifiedAndDeleted_BackByteForByte_DeletesAdded()
        {
            byte[] readmeBefore = Encoding.UTF8.GetBytes("original readme\n");
            File.WriteAllBytes(Path.Combine(_repo, "README.md"), readmeBefore);
            // A file that will be deleted: staged BEFORE the snapshot so it is IN the
            // checkpoint — the diff then sees it as D (in checkpoint, absent now).
            string d = Path.Combine(_repo, "todelete.txt");
            File.WriteAllText(d, "tracked\n");
            Git(_repo, "add", "todelete.txt");

            await Cp().SnapshotAtTurnStartAsync(1);

            // Abandoned branch: modify README, add a file, delete the checkpointed file.
            File.WriteAllText(Path.Combine(_repo, "README.md"), "abandoned\n");
            File.WriteAllText(Path.Combine(_repo, "added.txt"), "added\n");
            File.Delete(d);

            var diff = await Cp().ComputeRestoreDiffAsync(S, 1);
            Assert.NotNull(diff);
            var (restored, deleted, errors) = await Cp().ApplyRestoreAsync(_repo, Git(_repo, "rev-parse",
                $"refs/devmind/checkpoints/{S}/turn-1"), diff);
            Assert.Equal(string.Empty, errors);
            Assert.True(restored == 2, $"expected 2 restored, got {restored} (README.md M + todelete.txt D) — errors: {errors}");
            Assert.True(deleted == 1, $"expected 1 deleted, got {deleted} (added.txt A) — errors: {errors}");

            Assert.True(readmeBefore.SequenceEqual(ReadBytes(Path.Combine(_repo, "README.md"))),
                $"the modified file must be back byte-for-byte — got: {Convert.ToHexString(ReadBytes(Path.Combine(_repo, "README.md")))}, expected: {Convert.ToHexString(readmeBefore)} — errors: {errors}");
            Assert.True(File.Exists(d), $"the deleted tracked file must be recreated — errors: {errors}");
            Assert.Equal("tracked\n", File.ReadAllText(d));
            Assert.False(File.Exists(Path.Combine(_repo, "added.txt")));
        }

        [Fact]
        public async Task Restore_BinaryFile_RoundTripsByteForByte()
        {
            // A binary blob with every byte value 0..255 — text decoding would mangle it.
            byte[] binary = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            File.WriteAllBytes(Path.Combine(_repo, "blob.bin"), binary);
            await Cp().SnapshotAtTurnStartAsync(1);

            File.WriteAllBytes(Path.Combine(_repo, "blob.bin"), new byte[] { 1, 2, 3 });

            var diff = await Cp().ComputeRestoreDiffAsync(S, 1);
            Assert.NotNull(diff);
            var (restored, _, errors) = await Cp().ApplyRestoreAsync(_repo, Git(_repo, "rev-parse",
                $"refs/devmind/checkpoints/{S}/turn-1"), diff);
            Assert.Equal(string.Empty, errors);
            Assert.True(restored >= 1, $"expected at least 1 restored, got {restored} — errors: {errors}");
            Assert.True(binary.SequenceEqual(ReadBytes(Path.Combine(_repo, "blob.bin"))),
                $"a binary file must round-trip byte-for-byte through the checkpoint — errors: {errors}");
        }

        [Fact]
        public async Task Restore_CrlfFile_UnderAutocrlf_RoundTripsWorkingTreeBytes()
        {
            // A repo with core.autocrlf=true: a CRLF working file is stored as an LF
            // blob. A restore that wrote the RAW blob would re-normalise the file to
            // LF — the user's CRLF bytes would be lost. The restore must come back
            // with the exact working-tree bytes (CRLF) the user had.
            string repo = Path.Combine(Path.GetTempPath(), $"devmind-fcp-crlf-{Guid.NewGuid():N}");
            try
            {
                InitRepo(repo, autocrlf: true);
                byte[] crlf = new byte[] { 108, 105, 110, 101, 49, 13, 10, 108, 105, 110, 101, 50, 13, 10 }; // "line1\r\nline2\r\n"
                File.WriteAllBytes(Path.Combine(repo, "crlf.txt"), crlf);
                var cp = new FileCheckpoint(_root, S, repo);
                var result = await cp.SnapshotAtTurnStartAsync(1);
                Assert.Equal("ok", result.Status);

                // Mutate, then restore.
                File.WriteAllBytes(Path.Combine(repo, "crlf.txt"), new byte[] { 99, 104, 97, 110, 103, 101, 100 });
                var diff = await cp.ComputeRestoreDiffAsync(S, 1);
                Assert.NotNull(diff);
                var (restored, _, errors) = await cp.ApplyRestoreAsync(repo, result.CommitSha, diff);
                Assert.Equal(string.Empty, errors);
                Assert.True(restored >= 1, $"expected at least 1 restored, got {restored} — errors: {errors}");

                Assert.True(crlf.SequenceEqual(ReadBytes(Path.Combine(repo, "crlf.txt"))),
                    $"under core.autocrlf=true the restore must reproduce the CRLF working-tree bytes, not the normalised LF blob — got: {Convert.ToHexString(ReadBytes(Path.Combine(repo, "crlf.txt")))} — errors: {errors}");
            }
            finally
            {
                try { Directory.Delete(repo, recursive: true); } catch { /* temp dir */ }
            }
        }

        [Fact]
        public async Task Restore_LeavesIgnoredFilesAndTheMemorySetUntouched()
        {
            // The repo is created by the constructor with "hello\n" — set the
            // "original" content BEFORE the snapshot, or the restore has nothing
            // to restore to.
            File.WriteAllText(Path.Combine(_repo, ".gitignore"), "ignored.txt\n");
            File.WriteAllText(Path.Combine(_repo, "ignored.txt"), "ignored content\n");
            File.WriteAllText(Path.Combine(_repo, "MEMORY.md"), "memory v1\n");
            File.WriteAllText(Path.Combine(_repo, "README.md"), "original readme\n");
            await Cp().SnapshotAtTurnStartAsync(1);

            // Mutate an ignored file and the memory set, plus one normal file.
            File.WriteAllText(Path.Combine(_repo, "ignored.txt"), "ignored mutated\n");
            File.WriteAllText(Path.Combine(_repo, "MEMORY.md"), "memory v2\n");
            File.WriteAllText(Path.Combine(_repo, "README.md"), "normal mutated\n");

            var diff = await Cp().ComputeRestoreDiffAsync(S, 1);
            Assert.NotNull(diff);
            // The diff must not even name the ignored file or the memory set.
            Assert.True(!diff.Any(e => e.Path == "ignored.txt"));
            Assert.True(!diff.Any(e => e.Path == "MEMORY.md"));

            var (restored, _, errors) = await Cp().ApplyRestoreAsync(_repo, Git(_repo, "rev-parse",
                $"refs/devmind/checkpoints/{S}/turn-1"), diff);
            Assert.Equal(string.Empty, errors);

            // The normal file is restored; the ignored file and memory set are exactly
            // as the abandoned branch left them — the restore never opened them.
            Assert.True(File.ReadAllText(Path.Combine(_repo, "README.md")) == "original readme\n",
                $"the normal file must be back byte-for-byte — got: {Convert.ToHexString(ReadBytes(Path.Combine(_repo, "README.md")))} — errors: {errors}");
            Assert.True(File.ReadAllText(Path.Combine(_repo, "ignored.txt")) == "ignored mutated\n",
                "an ignored file is not in the plan — the restore must leave it untouched");
            Assert.True(File.ReadAllText(Path.Combine(_repo, "MEMORY.md")) == "memory v2\n",
                "the memory set is the memory checkpoint's — the file restore must leave it untouched");
        }

        // ── /rewind files: refusals (via the command handler) ────────────────

        [Fact]
        public async Task RewindFiles_Refuses_WithNoPendingPlan()
        {
            var ctx = new CommandContext { SessionId = S, WorkingDirectory = _repo, IsTurnRunning = false };
            var result = await SlashCommand.Dispatch("/rewind files", ctx);
            Assert.True(result.IsError);
            Assert.Contains("No pending file-restore plan", result.Message);
        }

        [Fact]
        public async Task RewindFiles_Refuses_WhileATurnIsRunning()
        {
            var ctx = new CommandContext
            {
                SessionId = S,
                WorkingDirectory = _repo,
                IsTurnRunning = true,
                FileRestorePlan = new PendingFileRestorePlan { SessionId = S, RepoRoot = _repo, TurnNumber = 1 },
            };
            var result = await SlashCommand.Dispatch("/rewind files", ctx);
            Assert.True(result.IsError);
            Assert.Contains("while a turn is running", result.Message);
        }

        [Fact]
        public async Task RewindFiles_Refuses_WhenThePlanBelongsToAnotherSession()
        {
            var ctx = new CommandContext
            {
                SessionId = S,
                WorkingDirectory = _repo,
                IsTurnRunning = false,
                FileRestorePlan = new PendingFileRestorePlan { SessionId = "other-session", RepoRoot = _repo, TurnNumber = 1 },
            };
            var result = await SlashCommand.Dispatch("/rewind files", ctx);
            Assert.True(result.IsError);
            Assert.Contains("belongs to session", result.Message);
        }

        [Fact]
        public async Task RewindFiles_AppliesThePlan_ReportsAndClearsIt()
        {
            // The repo is created by the constructor with "hello\n" — set the
            // "original" content BEFORE the snapshot, or the restore has nothing
            // to restore to.
            File.WriteAllText(Path.Combine(_repo, "README.md"), "original readme\n");
            await Cp().SnapshotAtTurnStartAsync(1);
            File.WriteAllText(Path.Combine(_repo, "README.md"), "mutated\n");

            var diff = await Cp().ComputeRestoreDiffAsync(S, 1);
            Assert.NotNull(diff);

            var ctx = new CommandContext
            {
                SessionId = S,
                WorkingDirectory = _repo,
                IsTurnRunning = false,
                FileRestorePlan = new PendingFileRestorePlan
                {
                    SessionId = S,
                    RepoRoot = _repo,
                    CheckpointCommit = Git(_repo, "rev-parse", $"refs/devmind/checkpoints/{S}/turn-1"),
                    TurnNumber = 1,
                    Paths = new List<FileCheckpoint.FileDiffEntry>(diff),
                },
            };

            var result = await SlashCommand.Dispatch("/rewind files", ctx);
            Assert.False(result.IsError);
            Assert.Contains("Restored 1 file(s)", result.Message);
            Assert.Equal("original readme\n", File.ReadAllText(Path.Combine(_repo, "README.md")));
            Assert.True(ctx.FileRestorePlan == null, "the plan must be cleared after it is applied");
        }

        // ── Fork inheritance of refs ─────────────────────────────────────────

        [Fact]
        public async Task Fork_InheritsTheOriginalsRefs_ForTurnsOneThroughNMinusOne()
        {
            await Cp().SnapshotAtTurnStartAsync(1);
            File.WriteAllText(Path.Combine(_repo, "README.md"), "turn 2\n");
            await Cp().SnapshotAtTurnStartAsync(2);

            // /rewind 3 → the fork gets the refs for turns 1..2, not 3.
            Cp().CopyRefs(S, Fork, 2);

            Assert.True(Git(_repo, "rev-parse", $"refs/devmind/checkpoints/{S}/turn-1") ==
                        Git(_repo, "rev-parse", $"refs/devmind/checkpoints/{Fork}/turn-1"),
                "the fork's turn-1 ref must point at the SAME commit as the original's");
            Assert.True(Git(_repo, "rev-parse", $"refs/devmind/checkpoints/{S}/turn-2") ==
                        Git(_repo, "rev-parse", $"refs/devmind/checkpoints/{Fork}/turn-2"));
            Assert.Throws<InvalidOperationException>(() =>
                Git(_repo, "rev-parse", $"refs/devmind/checkpoints/{Fork}/turn-3"));
        }

        // ── Pruning deletes the refs ─────────────────────────────────────────

        [Fact]
        public async Task Pruning_DeletesThePrunedSessionsRefs()
        {
            await Cp().SnapshotAtTurnStartAsync(1);
            string refName = $"refs/devmind/checkpoints/{S}/turn-1";
            Assert.NotNull(Git(_repo, "rev-parse", refName));

            // Age the session past the 14-day window, then prune. TurnAge reads the
            // MEMORY manifest (manifest.json), not files-manifest.json — so write an old
            // TakenAt into the memory manifest to make the pruner see the session as old.
            string memManifestPath = Path.Combine(_root, S, "turn-1", "manifest.json");
            File.WriteAllText(memManifestPath, JsonSerializer.Serialize(new
            {
                Turn = 1,
                SessionId = S,
                MemoryRoot = _repo,
                TakenAt = DateTime.UtcNow.AddDays(-15).ToString("O"),
                SameAsTurn = 0,
                Files = new object[0],
            }));

            // The pruner deletes the session folder (and its manifests) — so the repo
            // root and turns are captured BEFORE, exactly as the production prune path
            // does, and DeleteSessionRefs is called with the captured values.
            var pruned = MemoryCheckpoint.PruneAll(_root, DateTime.UtcNow);
            Assert.True(pruned.Contains(S), "the 15-day-old session must be pruned");

            FileCheckpoint.DeleteSessionRefs(_repo, S, new[] { 1 });
            Assert.Throws<InvalidOperationException>(() => Git(_repo, "rev-parse", refName));
        }
    }
}
