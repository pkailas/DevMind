// File: MemoryCheckpointTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The memory half of /rewind (part 2a), unit level: snapshot at turn start,
// identical-set detection ("same as turn j", no copy), empty/absent memory,
// restore semantics (delete added-since, revert changed byte-for-byte,
// recreate deleted-since, never touch non-memory paths), fork checkpoint
// copying, and pruning. Temp dirs only — the checkpoint root and memory root
// are injected, so the real %LOCALAPPDATA% and the repo are never touched.

using DevMind;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class MemoryCheckpointTests : IDisposable
    {
        readonly string _root;     // checkpoint root (the test seam for %LOCALAPPDATA%)
        readonly string _repo;     // memory root (the test seam for the working dir)

        const string S = "2026-09-27T183853Z-pid1";

        public MemoryCheckpointTests()
        {
            _root = Path.Combine(Path.GetTempPath(), $"devmind-memcp-root-{Guid.NewGuid():N}");
            _repo = Path.Combine(Path.GetTempPath(), $"devmind-memcp-repo-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_repo);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
            try { Directory.Delete(_repo, recursive: true); } catch { /* temp dir */ }
        }

        MemoryCheckpoint Cp() => new MemoryCheckpoint(_root, S, _repo);

        // ── Memory-set helpers ──────────────────────────────────────────────

        void WriteIndex(string content)
        {
            File.WriteAllText(Path.Combine(_repo, "MEMORY.md"),
                content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        void WriteTopic(string slug, string content)
        {
            string dir = Path.Combine(_repo, ".devmind", "memory");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, slug + ".md"),
                content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        string? ReadIndex() =>
            File.Exists(Path.Combine(_repo, "MEMORY.md"))
                ? File.ReadAllText(Path.Combine(_repo, "MEMORY.md"), Encoding.UTF8)
                : null;

        string? ReadTopic(string slug)
        {
            string p = Path.Combine(_repo, ".devmind", "memory", slug + ".md");
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
        }

        static byte[]? ReadBytes(string path) =>
            File.Exists(path) ? File.ReadAllBytes(path) : null;

        // ── Snapshot ────────────────────────────────────────────────────────

        [Fact]
        public void Snapshot_TurnStart_CapturesTheMemorySet_WithRelativePaths()
        {
            WriteIndex("index v1");
            WriteTopic("word-remembered", "remember APPLE");

            string dir = Cp().SnapshotAtTurnStart(1);

            Assert.NotNull(dir);
            // MEMORY.md and the topic file, copied under memory\ preserving relative paths.
            Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));
            Assert.Equal("index v1",
                File.ReadAllText(Path.Combine(dir, "memory", "MEMORY.md"), Encoding.UTF8));
            Assert.Equal("remember APPLE",
                File.ReadAllText(Path.Combine(dir, "memory", ".devmind", "memory", "word-remembered.md"),
                    Encoding.UTF8));
        }

        [Fact]
        public void Snapshot_IdenticalSet_IsSameAsPreviousTurn_WithoutACopy()
        {
            WriteIndex("index v1");
            WriteTopic("word-remembered", "remember APPLE");

            Cp().SnapshotAtTurnStart(1);

            // Nothing changed: turn 2 is a manifest that references turn 1 — the
            // files are NOT copied a second time.
            string dir2 = Cp().SnapshotAtTurnStart(2);
            Assert.NotNull(dir2);
            Assert.False(File.Exists(Path.Combine(dir2, "memory", "MEMORY.md")),
                "an identical set must not be re-copied — the manifest references the earlier turn");
            Assert.True(File.Exists(Path.Combine(dir2, "manifest.json")));

            // And restoring from the referencing snapshot still works.
            WriteIndex("index v2 (mutated after the snapshot)");
            var restored = Cp().Restore(S, 2);
            Assert.NotNull(restored);
            Assert.Equal("index v1", ReadIndex());
        }

        [Fact]
        public void Snapshot_ChangedSet_IsCopied_Fresh()
        {
            WriteIndex("index v1");
            Cp().SnapshotAtTurnStart(1);

            WriteIndex("index v2");
            string dir2 = Cp().SnapshotAtTurnStart(2);
            Assert.NotNull(dir2);
            Assert.Equal("index v2",
                File.ReadAllText(Path.Combine(dir2, "memory", "MEMORY.md"), Encoding.UTF8));
        }

        [Fact]
        public void Snapshot_NoMemoryAtAll_IsAValidEmptySnapshot()
        {
            // No MEMORY.md, no .devmind/memory — a valid, empty snapshot.
            string dir = Cp().SnapshotAtTurnStart(1);
            Assert.NotNull(dir);
            Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));
            Assert.False(Directory.Exists(Path.Combine(dir, "memory")),
                "an empty set has no files to copy");

            // And restoring an empty snapshot to a repo that now HAS memory deletes it.
            WriteIndex("index that should be gone");
            WriteTopic("word-remembered", "APPLE");
            var restored = Cp().Restore(S, 1);
            Assert.NotNull(restored);
            Assert.Null(ReadIndex());
            Assert.Null(ReadTopic("word-remembered"));
        }

        [Fact]
        public void Snapshot_NeverTouchesNonMemoryFiles()
        {
            // A sentinel next to the memory set — the snapshot must leave it alone.
            string sentinel = Path.Combine(_repo, "NOT-memory.txt");
            File.WriteAllText(sentinel, "sentinel");

            WriteIndex("index v1");
            string dir = Cp().SnapshotAtTurnStart(1);
            Assert.NotNull(dir);
            Assert.False(File.Exists(Path.Combine(dir, "memory", "NOT-memory.txt")),
                "the snapshot must not include non-memory files");
            Assert.Equal("sentinel", File.ReadAllText(sentinel, Encoding.UTF8));
        }

        // ── Restore ─────────────────────────────────────────────────────────

        [Fact]
        public void Restore_DeletesFilesAddedSince_RevertsChanged_ByteForByte()
        {
            WriteIndex("index v1");
            WriteTopic("alpha", "alpha v1");
            Cp().SnapshotAtTurnStart(2);

            // The abandoned branch mutates memory: a new topic, a changed one,
            // and a rewritten index.
            WriteIndex("index v2 with beta");
            WriteTopic("beta", "beta — added since the snapshot");
            WriteTopic("alpha", "alpha v2 — mutated");

            var restored = Cp().Restore(S, 2);
            Assert.NotNull(restored);
            Assert.Equal("index v1", ReadIndex());
            Assert.Equal("alpha v1", ReadTopic("alpha"));
            Assert.True(ReadTopic("beta") == null,
                "a file added since the snapshot must be deleted");
        }

        [Fact]
        public void Restore_ByteForByte_PreservesBomAndEncoding()
        {
            // Write the index WITH a BOM; the snapshot must come back byte-identical,
            // BOM included — a text round-trip would lose it.
            WriteIndex("index with BOM");
            byte[]? withBom = ReadBytes(Path.Combine(_repo, "MEMORY.md"));
            Assert.True(withBom != null && withBom.Length >= 3 &&
                        withBom[0] == 0xEF && withBom[1] == 0xBB && withBom[2] == 0xBF,
                "the index was written with a BOM — the test premise must hold");

            Cp().SnapshotAtTurnStart(1);

            // Mutate, then restore — the bytes must match the original exactly.
            File.WriteAllText(Path.Combine(_repo, "MEMORY.md"), "changed", Encoding.UTF8);
            Cp().Restore(S, 1);
            // The restored bytes must match the original exactly — BOM included.
            Assert.Equal(withBom, ReadBytes(Path.Combine(_repo, "MEMORY.md"))!);
        }

        [Fact]
        public void Restore_RecreatesFilesDeletedSince()
        {
            WriteIndex("index v1");
            WriteTopic("alpha", "alpha v1");
            Cp().SnapshotAtTurnStart(1);

            File.Delete(Path.Combine(_repo, ".devmind", "memory", "alpha.md"));
            Assert.Null(ReadTopic("alpha"));

            Cp().Restore(S, 1);
            Assert.Equal("alpha v1", ReadTopic("alpha"));
        }

        [Fact]
        public void Restore_NeverTouchesNonMemoryFiles_SentinelUnchanged()
        {
            // Sentinel: a file next to the memory set, and one OUTSIDE the repo root
            // entirely. Restore must leave both byte-for-byte.
            string sentinelInRepo = Path.Combine(_repo, "source.cs");
            File.WriteAllText(sentinelInRepo, "class C {}");
            string outside = Path.Combine(Path.GetTempPath(), $"devmind-memcp-outside-{Guid.NewGuid():N}.txt");
            File.WriteAllText(outside, "outside");

            WriteIndex("index v1");
            WriteTopic("alpha", "alpha v1");
            Cp().SnapshotAtTurnStart(1);

            // Abandoned branch touches the non-memory files too (in a real turn,
            // /rewind part 2b would handle those — part 2a must not).
            File.WriteAllText(sentinelInRepo, "class C { /* mutated */ }");
            File.WriteAllText(outside, "outside — mutated");

            Cp().Restore(S, 1);

            // Memory is back…
            Assert.Equal("index v1", ReadIndex());
            Assert.Equal("alpha v1", ReadTopic("alpha"));
            // …and the non-memory files are exactly as the abandoned branch left them.
            Assert.Equal("class C { /* mutated */ }", File.ReadAllText(sentinelInRepo, Encoding.UTF8));
            Assert.Equal("outside — mutated", File.ReadAllText(outside, Encoding.UTF8));

            try { File.Delete(outside); } catch { /* temp file */ }
        }

        [Fact]
        public void Restore_NoCheckpoint_LeavesMemoryUnchanged_AndReportsNull()
        {
            WriteIndex("index as-is");
            WriteTopic("alpha", "alpha as-is");

            var restored = Cp().Restore(S, 7); // no turn-7 snapshot exists

            Assert.Null(restored);
            Assert.Equal("index as-is", ReadIndex());
            Assert.Equal("alpha as-is", ReadTopic("alpha"));
        }

        [Fact]
        public void Restore_EmptySnapshot_DeletesEverythingInTheMemorySet()
        {
            // The turn-1 snapshot was empty (no memory at all).
            Cp().SnapshotAtTurnStart(1);

            WriteIndex("index added later");
            WriteTopic("alpha", "alpha added later");

            var restored = Cp().Restore(S, 1);
            Assert.NotNull(restored);
            Assert.Null(ReadIndex());
            Assert.Null(ReadTopic("alpha"));
        }

        // ── Fork support ────────────────────────────────────────────────────

        [Fact]
        public void CopyCheckpoints_GivesTheForkTurnsOneThroughNMinusOne_Only()
        {
            WriteIndex("index t1");
            Cp().SnapshotAtTurnStart(1);
            WriteIndex("index t2");
            Cp().SnapshotAtTurnStart(2);
            WriteIndex("index t3 — must not cross the fork boundary");
            Cp().SnapshotAtTurnStart(3);

            // /rewind 3 → the fork gets turns 1..2, NOT 3.
            Cp().CopyCheckpoints(S, "fork-1", 2);

            Assert.True(Directory.Exists(Path.Combine(_root, "fork-1", "turn-1")));
            Assert.True(Directory.Exists(Path.Combine(_root, "fork-1", "turn-2")));
            Assert.False(Directory.Exists(Path.Combine(_root, "fork-1", "turn-3")),
                "the fork must not inherit the turn it was rewound from");
            Assert.Equal("index t2",
                File.ReadAllText(Path.Combine(_root, "fork-1", "turn-2", "memory", "MEMORY.md"),
                    Encoding.UTF8));
        }

        [Fact]
        public void CopyCheckpoints_NoSource_DoesNothing()
        {
            // No snapshots at all — a fork before any turn has run.
            Cp().CopyCheckpoints("ghost-session", "fork-1", 5);
            Assert.False(Directory.Exists(Path.Combine(_root, "fork-1")));
        }

        // ── Pruning ─────────────────────────────────────────────────────────

        void MakeTurn(int k, DateTime takenAtUtc)
        {
            string dir = Path.Combine(_root, S, $"turn-{k}");
            Directory.CreateDirectory(dir);
            // PruneAll ages a turn by its manifest's TakenAt (the snapshot time) — NOT the
            // file's mtime, which is the machine clock at write time and meaningless for a
            // checkpoint taken by an earlier run. The test writes the clock it wants to test
            // straight into the manifest, the same way SnapshotAtTurnStart does in production.
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                $"{{\"Turn\":{k},\"TakenAt\":\"{takenAtUtc:O}\",\"Files\":[]}}");
        }

        [Fact]
        public void Prune_DropsSessionsOlderThan14Days()
        {
            DateTime now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

            MakeTurn(1, now.AddDays(-15));   // old session
            MemoryCheckpoint.PruneAll(_root, now);
            Assert.False(Directory.Exists(Path.Combine(_root, S)),
                "a session whose newest turn is 15 days old must be pruned");

            // A fresh session survives.
            MakeTurn(1, now.AddDays(-1));
            MemoryCheckpoint.PruneAll(_root, now);
            Assert.True(Directory.Exists(Path.Combine(_root, S, "turn-1")));
        }

        [Fact]
        public void Prune_KeepsOnlyThe100MostRecentTurnsPerSession()
        {
            DateTime now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

            // All 105 turns are RECENT (inside the 14-day window) and strictly ordered:
            // turn-105 is the newest, turn-1 the oldest. Future timestamps would fail the
            // session-age check before the per-turn cap is ever consulted.
            for (int k = 1; k <= 105; k++)
                MakeTurn(k, now.AddMinutes(k - 105));

            MemoryCheckpoint.PruneAll(_root, now);

            var remaining = Directory.GetDirectories(Path.Combine(_root, S));
            Assert.Equal(100, remaining.Length);
            Assert.True(Directory.Exists(Path.Combine(_root, S, "turn-105")),
                "the newest turn must survive");
            Assert.False(Directory.Exists(Path.Combine(_root, S, "turn-5")),
                "the oldest turns beyond the 100 most recent must be pruned");
        }

        [Fact]
        public void Prune_MissingRoot_DoesNotThrow()
        {
            MemoryCheckpoint.PruneAll(
                Path.Combine(_root, "does-not-exist"),
                DateTime.UtcNow);
        }
    }
}
