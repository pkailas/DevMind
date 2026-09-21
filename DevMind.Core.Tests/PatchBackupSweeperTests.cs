// File: PatchBackupSweeperTests.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// A host deletes its own PATCH backups when its turn ends, but a force-killed
// process never reaches that — its backups stay in %TEMP%\DevMind with no owner
// left to remove them. PatchBackupSweeper is the only thing that reclaims those,
// and it decides by age alone: the filename carries a timestamp but no owner, so
// a backup young enough to belong to a turn that is still running must survive.
//
// Every test points the sweep at a private directory. The real %TEMP%\DevMind is
// shared with live sessions, so a test must never sweep it.

using Xunit;

namespace DevMind.Core.Tests
{
    public class PatchBackupSweeperTests : IDisposable
    {
        private readonly string _dir;

        public PatchBackupSweeperTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_baksweep_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        /// <summary>Writes a backup file whose last-write time is <paramref name="ageHours"/>
        /// hours in the past, named the way PatchEngine names one.</summary>
        private string MakeBackup(string originalName, double ageHours)
        {
            DateTime stamp = DateTime.Now.AddHours(-ageHours);
            string path = Path.Combine(_dir, $"{originalName}.{stamp:yyyyMMdd_HHmmss_fff}.bak");
            File.WriteAllText(path, "backup contents");
            File.SetLastWriteTime(path, stamp);
            return path;
        }

        [Fact]
        public void CleanupStale_RemovesBackupOlderThanMaxAge()
        {
            string stale = MakeBackup("notes.txt", ageHours: 48);

            PatchBackupSweeper.CleanupStale(TimeSpan.FromHours(24), _dir);

            Assert.False(File.Exists(stale), "a backup past the age threshold must be deleted");
        }

        [Fact]
        public void CleanupStale_KeepsBackupYoungerThanMaxAge()
        {
            // A turn that is still running owns backups this young — the sweep has no
            // way to tell them from finished ones, so age is what protects them.
            string fresh = MakeBackup("notes.txt", ageHours: 1);

            PatchBackupSweeper.CleanupStale(TimeSpan.FromHours(24), _dir);

            Assert.True(File.Exists(fresh), "a backup inside the age threshold must survive");
        }

        [Fact]
        public void CleanupStale_OneLockedFile_DoesNotStopTheRest()
        {
            string first = MakeBackup("a.txt", ageHours: 48);
            string locked = MakeBackup("b.txt", ageHours: 48);
            string last = MakeBackup("c.txt", ageHours: 48);

            // An open exclusive handle makes this one delete throw. The sweep must
            // absorb it and still reclaim the files either side of it.
            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                PatchBackupSweeper.CleanupStale(TimeSpan.FromHours(24), _dir);
            }

            Assert.False(File.Exists(first), "a stale backup before the locked one must be deleted");
            Assert.False(File.Exists(last), "a stale backup after the locked one must be deleted");
            Assert.True(File.Exists(locked), "the locked backup could not be deleted this run");
        }

        [Fact]
        public void CleanupStale_LeavesNonBackupFilesAndSubdirectories()
        {
            // %TEMP%\DevMind is shared: the nearline cache keeps per-session subfolders
            // under it and other temp data sits beside them. Only flat .bak files are
            // this sweep's to remove.
            string other = Path.Combine(_dir, "session.log");
            File.WriteAllText(other, "not a backup");
            File.SetLastWriteTime(other, DateTime.Now.AddHours(-48));

            string nested = Path.Combine(_dir, "abc123", "nearline");
            Directory.CreateDirectory(nested);
            string spill = Path.Combine(nested, "spill.bak");
            File.WriteAllText(spill, "someone else's");
            File.SetLastWriteTime(spill, DateTime.Now.AddHours(-48));

            PatchBackupSweeper.CleanupStale(TimeSpan.FromHours(24), _dir);

            Assert.True(File.Exists(other), "a non-.bak file must not be swept");
            Assert.True(File.Exists(spill), "a .bak file in a subfolder must not be swept");
        }

        [Fact]
        public void CleanupStale_MissingDirectory_NeverThrows()
        {
            string absent = Path.Combine(_dir, "no-such-folder");

            PatchBackupSweeper.CleanupStale(TimeSpan.FromHours(24), absent); // must not throw
        }

        [Fact]
        public void DefaultMaxAge_ExceedsTheLongestJobATurnCanRun()
        {
            // The job wall-clock timeout is clamped at 240 minutes, and build plus test
            // verification run after it. The threshold has to clear that with room to
            // spare or the sweep could delete a backup still in use.
            Assert.True(PatchBackupSweeper.DefaultMaxAge > TimeSpan.FromHours(4),
                $"DefaultMaxAge ({PatchBackupSweeper.DefaultMaxAge}) must exceed the longest possible job");
        }
    }
}
