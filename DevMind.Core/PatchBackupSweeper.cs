// File: PatchBackupSweeper.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.IO;

namespace DevMind
{
    /// <summary>
    /// Best-effort startup sweep for PATCH backup files left behind in
    /// <c>%TEMP%\DevMind</c>. A host drops its own backups when its turn ends, but a
    /// process that is force-killed never gets to — its backups are orphaned with no
    /// owner left to delete them, and they accumulate without bound. This is the only
    /// thing that reclaims them.
    /// </summary>
    public static class PatchBackupSweeper
    {
        /// <summary>
        /// How old a backup must be before the sweep will touch it. A backup belonging
        /// to a turn that is still running must never be deleted, and the filename
        /// carries no owner — only a timestamp — so age is the only thing the sweep can
        /// judge by. The longest a turn can legitimately hold one is the maximum job
        /// wall-clock timeout (4 hours) plus its build and test verification; a day is
        /// several times that, and costs nothing, because the sweep runs at every
        /// startup and orphans are otherwise permanent.
        /// </summary>
        public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(24);

        /// <summary>
        /// Deletes backup files in <paramref name="backupDir"/> (default
        /// <c>%TEMP%\DevMind</c>) last written more than <paramref name="maxAge"/> ago.
        /// Synchronous and never throws — intended to be called from a fire-and-forget
        /// background task at launch. Each file is deleted in its own try/catch, so one
        /// that is locked or already gone cannot abort the rest of the sweep.
        /// </summary>
        public static void CleanupStale(TimeSpan maxAge, string backupDir = null)
        {
            try
            {
                string root = backupDir ?? Path.Combine(Path.GetTempPath(), "DevMind");
                if (!Directory.Exists(root)) return;

                DateTime cutoff = DateTime.Now - maxAge;

                // Top level only: PatchEngine writes backups flat in this folder, while
                // the subfolders under it are the nearline cache's, which has its own
                // cleanup and must not be touched here.
                foreach (string path in Directory.GetFiles(root, "*.bak", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        // GetFiles' pattern matching is looser than it looks on Windows;
                        // confirm the extension before deleting anything.
                        if (!path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.GetLastWriteTime(path) > cutoff) continue;

                        File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PatchBackupSweeper] delete of '{path}' failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PatchBackupSweeper] sweep failed: {ex.Message}");
            }
        }
    }
}
