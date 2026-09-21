// File: PatchBackupDrainTests.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The PATCH undo stack writes a timestamped backup file into %TEMP%\DevMind for
// every applied patch. The stack is session-scoped, so the backup files must go
// with the session: drained on /restart (ResetSession) and when a headless job's
// HeadlessSession is disposed. Without the drain, every session end orphans up
// to PatchBackupStackLimit files in %TEMP%\DevMind.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    public class PatchBackupDrainTests : IDisposable
    {
        private readonly string _dir;

        public PatchBackupDrainTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_patchdrain_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        private static string PatchDir => Path.Combine(Path.GetTempPath(), "DevMind");

        private static BufferedAgenticHost NewHost(string dir) =>
            new BufferedAgenticHost(dir, outputSink: (text, _) => { });

        /// <summary>
        /// Writes one fresh throwaway file in the test dir and applies ONE patch
        /// to it through the production path (ApplyResolvedPatchAsync). That is
        /// the whole trick of this fixture: a FIND that matches only the file's
        /// original bytes must be applied ONCE — re-applying it to the same file
        /// after the first patch succeeds finds nothing and silently produces no
        /// backup. One patch per fresh file is exactly one successful apply and
        /// therefore exactly one backup on the undo stack. Returns the target's
        /// file name — its backup (if written) carries that name, so tests can
        /// find exactly their own backups. A rejected patch fails the test
        /// loudly; it must never slide through as a missing backup.
        /// </summary>
        private async Task<string> PatchOneFreshFileAsync(BufferedAgenticHost host)
        {
            string target = Path.Combine(_dir, $"patchdrain_{Guid.NewGuid():N}.txt");
            string content = "line one\nline two\nline three\n";
            File.WriteAllText(target, content);

            var sb = new StringBuilder();
            var resolved = PatchEngine.ResolvePairs(
                new List<(string, string)> { ("line two", "MARKER") },
                target, Path.GetFileName(target), content, Encoding.UTF8,
                (text, _) => sb.Append(text));
            Assert.True(resolved != null, $"patch did not resolve. Reporter said: {sb}");

            var (fullPath, failureReason) =
                await ((IAgenticHost)host).ApplyResolvedPatchAsync(resolved);
            Assert.Null(failureReason);
            Assert.Equal(target, fullPath);
            return Path.GetFileName(target);
        }

        /// <summary>Applies <paramref name="count"/> patches, each to its own fresh
        /// file, so the host's undo stack holds exactly <paramref name="count"/>
        /// backups. Returns the target file names in patch order.</summary>
        private async Task<List<string>> PushBackupsAsync(BufferedAgenticHost host, int count)
        {
            var files = new List<string>();
            for (int i = 0; i < count; i++)
                files.Add(await PatchOneFreshFileAsync(host));
            return files;
        }

        private static List<string> BackupsFor(params string[] filePatterns)
        {
            var backups = new List<string>();
            foreach (string pattern in filePatterns)
                backups.AddRange(Directory.EnumerateFiles(PatchDir, pattern));
            return backups;
        }

        // ── The drain itself ────────────────────────────────────────────────────────

        [Fact]
        public async Task Drain_DeletesAllBackups_AndEmptiesStack()
        {
            var host = NewHost(_dir);
            List<string> files = await PushBackupsAsync(host, 3);

            // Positive precondition: the patch count applied equals the stack depth,
            // and every expected backup file exists on disk.
            Assert.Equal(3, host.PatchBackupCount);
            List<string> backups = BackupsFor(files.Select(f => $"{f}*.bak").ToArray());
            Assert.True(backups.Count > 0, "expected at least one backup file on disk");
            foreach (string b in backups)
                Assert.True(File.Exists(b));

            host.DrainPatchBackups();

            Assert.Equal(0, host.PatchBackupCount);
            foreach (string b in backups)
                Assert.False(File.Exists(b), $"backup was not deleted: {b}");
        }

        [Fact]
        public void Drain_EmptyStack_IsNoOp()
        {
            var host = NewHost(_dir);
            host.DrainPatchBackups(); // must not throw
            Assert.Equal(0, host.PatchBackupCount);
        }

        [Fact]
        public async Task ResetSession_DrainsBackups()
        {
            var host = NewHost(_dir);
            List<string> files = await PushBackupsAsync(host, 2);

            Assert.Equal(2, host.PatchBackupCount);
            List<string> backups = BackupsFor(files.Select(f => $"{f}*.bak").ToArray());
            Assert.True(backups.Count > 0, "expected at least one backup file on disk");

            host.ResetSession();

            Assert.Equal(0, host.PatchBackupCount);
            foreach (string b in backups)
                Assert.False(File.Exists(b), $"backup survived ResetSession: {b}");
        }

        // ── Failure isolation: one bad backup must not stop the rest ───────────────

        [Fact]
        public async Task Drain_AlreadyDeletedBackup_DoesNotStopTheRest()
        {
            var host = NewHost(_dir);
            List<string> files = await PushBackupsAsync(host, 3);

            Assert.Equal(3, host.PatchBackupCount);
            List<string> backups = BackupsFor(files.Select(f => $"{f}*.bak").ToArray());
            Assert.True(backups.Count > 0, "expected at least one backup file on disk");

            // Delete one backup BEFORE the drain: the stack entry then points at a
            // missing file — the same shape as a file that vanished or is locked
            // behind the drain. The other backups must still be removed.
            string gone = backups[0];
            File.Delete(gone);

            host.DrainPatchBackups(); // must not throw

            Assert.Equal(0, host.PatchBackupCount);
            foreach (string b in backups)
                if (!string.Equals(b, gone, StringComparison.OrdinalIgnoreCase))
                    Assert.False(File.Exists(b), $"backup survived the drain: {b}");
        }

        [Fact]
        public async Task Drain_GhostPaths_NeverThrows()
        {
            // Backups are built from real patches, then the files are removed out
            // from under the stack: every individual delete in the drain fails,
            // and the drain must still walk the stack to empty without throwing —
            // it runs on a disposal path.
            var host = NewHost(_dir);
            List<string> files = await PushBackupsAsync(host, 2);

            Assert.Equal(2, host.PatchBackupCount);
            List<string> backups = BackupsFor(files.Select(f => $"{f}*.bak").ToArray());
            Assert.True(backups.Count > 0, "expected at least one backup file on disk");
            foreach (string b in backups)
                File.Delete(b);

            host.DrainPatchBackups(); // must not throw
            host.DrainPatchBackups(); // idempotent
            Assert.Equal(0, host.PatchBackupCount);
        }

        // ── HeadlessSession.Dispose drains the host's backups ──────────────────────

        [Fact]
        public async Task HeadlessSession_Dispose_DrainsPatchBackups()
        {
            using var session = new HeadlessSession(
                new HeadlessOptions
                {
                    RequestTimeoutMinutes = 1,
                    FirstTokenTimeoutMinutes = 1,
                    ManualContextSize = 32768, // skip context probes
                    AgenticLoopMaxDepth = 5,
                },
                "http://127.0.0.1:9/v1", apiKey: null!, // nothing ever calls it — no turn runs
                workingDirectory: _dir, buildCommand: "dotnet build",
                noExecute: false,
                promptFilePath: Path.Combine(_dir, "nonexistent-prompt.md"));

            // The session owns its host privately; reach it through the concrete
            // field — no cast, no interface change.
            var field = typeof(HeadlessSession).GetField("_host",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var host = (BufferedAgenticHost)field.GetValue(session)!;

            // Two patches, each on its own fresh file.
            List<string> files = await PushBackupsAsync(host, 2);

            Assert.Equal(2, host.PatchBackupCount);
            List<string> backups = BackupsFor(files.Select(f => $"{f}*.bak").ToArray());
            Assert.True(backups.Count > 0, "expected at least one backup file on disk");
            foreach (string b in backups)
                Assert.True(File.Exists(b));

            session.Dispose();

            Assert.Equal(0, host.PatchBackupCount);
            foreach (string b in backups)
                Assert.False(File.Exists(b), $"HeadlessSession.Dispose did not drain the backup: {b}");
        }
    }
}
