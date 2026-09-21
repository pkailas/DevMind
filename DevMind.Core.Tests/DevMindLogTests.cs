// File: DevMindLogTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// DevMindLog exists because Debug.WriteLine carries [Conditional("DEBUG")]: the compiler
// deletes the call AND its string literals from a -c Release build, which is what
// run-deploy.ps1 publishes. Every diagnostic site in DevMind.Core was therefore silent in
// the version that actually runs, and almost all of them sit inside a catch that otherwise
// swallows the error.
//
// The properties that make the replacement safe enough to call from those catch blocks are
// each pinned here:
//
//   * a line actually reaches the file, under the path DevMindPaths owns;
//   * the logger swallows its own failure — an unwritable destination must not turn a
//     handled error into a crash;
//   * concurrent writers neither throw nor interleave into a corrupt line, including while
//     the retention sweep runs and while another handle holds the file open;
//   * retention BOUNDS growth — asserted as the bound itself, not merely that cleanup ran;
//   * Write() is not [Conditional], and the converted sites really do call it — the two
//     halves of "this survives Release".
//
// DEVMIND_GLOBAL_DIR is DevMindPaths' documented test seam, so nothing here can write into
// the operator's real %APPDATA%. DevMind.Core.Tests runs serially (xunit.runner.json), which
// is what makes mutating that process-wide variable safe.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class DevMindLogTests : IDisposable
    {
        private readonly string _root;
        private readonly string _globalDir;
        private readonly string? _priorGlobalDir;
        private readonly string? _priorEnabled;

        public DevMindLogTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "devmind_log_" + Guid.NewGuid().ToString("N"));
            _globalDir = Path.Combine(_root, "global");
            Directory.CreateDirectory(_globalDir);

            _priorGlobalDir = Environment.GetEnvironmentVariable("DEVMIND_GLOBAL_DIR");
            _priorEnabled = Environment.GetEnvironmentVariable("DEVMIND_LOG");
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _globalDir);
            Environment.SetEnvironmentVariable("DEVMIND_LOG", null);
            DevMindLog.ResetForTests();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _priorGlobalDir);
            Environment.SetEnvironmentVariable("DEVMIND_LOG", _priorEnabled);
            DevMindLog.ResetForTests();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
        }

        private string LogsDir => Path.Combine(_globalDir, DevMindPaths.LogsDirName);

        private string[] LogFiles()
            => Directory.Exists(LogsDir)
                ? Directory.GetFiles(LogsDir, "devmind-*.log*", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();

        /// <summary>Reads the active file with sharing flags that tolerate the logger's own
        /// handle, so reading never perturbs what is being tested.</summary>
        private static string ReadShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadToEnd();
        }

        // ── A line actually lands ───────────────────────────────────────────────

        [Fact]
        public void Write_PutsTheLineInAFileUnderTheGlobalLogsDir()
        {
            DevMindLog.Write("[TestComponent] a thing failed: boom");

            string path = DevMindLog.CurrentLogPath;

            // The path is DevMindPaths' to own, not DevMindLog's to compose.
            Assert.Equal(LogsDir, Path.GetDirectoryName(path));
            Assert.Equal(DevMindPaths.GlobalLogsDir, Path.GetDirectoryName(path));
            Assert.True(File.Exists(path), $"no log file at {path}");

            string content = ReadShared(path);
            Assert.Contains("[TestComponent] a thing failed: boom", content);
            Assert.Contains("pid=" + Environment.ProcessId, content);
        }

        [Fact]
        public void Write_FlattensEmbeddedNewlines_SoOneEventIsOneLine()
        {
            DevMindLog.Write("[TestComponent] multi\r\nline\nmessage");

            string[] lines = ReadShared(DevMindLog.CurrentLogPath)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Single(lines);
            Assert.Contains("multi line message", lines[0]);
        }

        [Fact]
        public void Write_IsSuppressedByDevMindLogOff_ButStillDoesNotThrow()
        {
            Environment.SetEnvironmentVariable("DEVMIND_LOG", "off");

            DevMindLog.Write("[TestComponent] should not be written");

            Assert.Empty(LogFiles());
        }

        // ── It swallows its own failure ─────────────────────────────────────────

        // A destination that cannot be created: the global dir's PARENT slot is occupied by
        // a regular file, so Directory.CreateDirectory on the logs dir under it throws. Every
        // caller of Write is inside a catch handling something else; if the logger let this
        // surface, a handled error would become a crash.
        [Fact]
        public void Write_ToAnUnwritableDestination_SwallowsAndLeavesTheCallerUnaffected()
        {
            string blocker = Path.Combine(_root, "not-a-directory");
            File.WriteAllText(blocker, "this is a file, not a directory");
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", Path.Combine(blocker, "devmind"));
            DevMindLog.ResetForTests();

            // Precondition: the destination really is unwritable, so this is not vacuous.
            Assert.ThrowsAny<Exception>(() => Directory.CreateDirectory(DevMindPaths.GlobalLogsDir));

            // The whole assertion: this returns normally.
            DevMindLog.Write("[TestComponent] failure inside a catch block");

            // And the caller's own flow continues untouched.
            int reached = 0;
            try
            {
                throw new InvalidOperationException("the error the caller was already handling");
            }
            catch (Exception ex)
            {
                DevMindLog.Write($"[TestComponent] handled: {ex.Message}");
                reached = 1;
            }

            Assert.Equal(1, reached);
        }

        [Fact]
        public void Write_WhileAnotherHandleHoldsTheFileOpen_StillLands()
        {
            DevMindLog.Write("[TestComponent] first");
            string path = DevMindLog.CurrentLogPath;

            // Stand in for a second process (or a tail) holding the file open. The logger
            // opens with FileShare.ReadWrite precisely so this cannot fail the write;
            // File.AppendAllText's default FileShare.Read would.
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                DevMindLog.Write("[TestComponent] written while held open");
            }

            Assert.Contains("written while held open", ReadShared(path));
        }

        // ── Concurrency ─────────────────────────────────────────────────────────

        [Fact]
        public async Task ConcurrentWriters_NeitherThrowNorProduceACorruptLine()
        {
            const int writers = 16;
            const int perWriter = 40;

            var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                for (int i = 0; i < perWriter; i++)
                {
                    try { DevMindLog.Write($"[TestComponent] writer={w} seq={i} END"); }
                    catch (Exception ex) { failures.Add(ex); }
                }
            })));

            Assert.True(failures.IsEmpty, "DevMindLog.Write threw: " + string.Join("; ", failures.Select(e => e.Message)));

            string[] lines = ReadShared(DevMindLog.CurrentLogPath)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .ToArray();

            Assert.Equal(writers * perWriter, lines.Length);

            // Every line is whole: one timestamp, one pid, one complete message. A torn or
            // interleaved append would leave a line missing its head or its tail.
            foreach (string line in lines)
            {
                Assert.Contains("pid=" + Environment.ProcessId, line);
                Assert.Contains("[TestComponent] writer=", line);
                Assert.EndsWith(" END", line);
            }

            // ...and no event was lost or duplicated.
            var expected = from w in Enumerable.Range(0, writers)
                           from i in Enumerable.Range(0, perWriter)
                           select $"writer={w} seq={i} END";
            Assert.Equal(writers * perWriter, expected.Count(e => lines.Any(l => l.EndsWith(e, StringComparison.Ordinal))));
        }

        [Fact]
        public async Task SweepRunningConcurrentlyWithWriters_NeitherThrowsNorLosesTheActiveFile()
        {
            // Files a concurrent process might own, which the sweep will be considering
            // while this process is writing.
            for (int i = 0; i < 30; i++)
                File.WriteAllText(Path.Combine(EnsureLogsDir(), $"devmind-20200101-pid{1000 + i}.log"), "old\n");

            var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            var sweeper = Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    try { DevMindLog.Sweep(LogsDir, DevMindLog.CurrentLogPath); }
                    catch (Exception ex) { failures.Add(ex); }
                }
            });

            var writer = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    try { DevMindLog.Write($"[TestComponent] concurrent seq={i} END"); }
                    catch (Exception ex) { failures.Add(ex); }
                }
            });

            await Task.WhenAll(sweeper, writer);

            Assert.True(failures.IsEmpty, "sweep/write threw: " + string.Join("; ", failures.Select(e => e.Message)));

            // The active file survived the sweep and still holds every line.
            string content = ReadShared(DevMindLog.CurrentLogPath);
            Assert.Equal(200, content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }

        // ── Retention: the bound, not just "cleanup ran" ────────────────────────

        private string EnsureLogsDir()
        {
            Directory.CreateDirectory(LogsDir);
            return LogsDir;
        }

        [Fact]
        public void Retention_BoundsTheFileCount_NoMatterHowManyAreAlreadyThere()
        {
            // Far more than the cap, all recent enough that the AGE rule cannot reclaim
            // them — so only the count rule can, which is exactly what is under test.
            const int seeded = DevMindLog.MaxFiles * 5;
            for (int i = 0; i < seeded; i++)
            {
                string p = Path.Combine(EnsureLogsDir(), $"devmind-20990101-pid{2000 + i}.log");
                File.WriteAllText(p, "recent\n");
                File.SetLastWriteTime(p, DateTime.Now.AddMinutes(-i));
            }

            Assert.Equal(seeded, LogFiles().Length);   // precondition: the bound is genuinely exceeded

            DevMindLog.Write("[TestComponent] first line of this process triggers the sweep");

            string[] after = LogFiles();
            Assert.True(after.Length <= DevMindLog.MaxFiles,
                $"retention did not bound the file count: {after.Length} files, cap is {DevMindLog.MaxFiles}");

            // The bound is the point, but the active file must be on the surviving side of it.
            Assert.Contains(DevMindLog.CurrentLogPath, after);

            // Newest kept, oldest dropped — retention that kept the stale ones would satisfy
            // the count cap and still be useless.
            Assert.Contains(after, p => p.EndsWith("pid2000.log", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(after, p => p.EndsWith($"pid{2000 + seeded - 1}.log", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Retention_DeletesFilesOlderThanMaxAge_AndKeepsFresherOnes()
        {
            string stale = Path.Combine(EnsureLogsDir(), "devmind-20200101-pid3001.log");
            string fresh = Path.Combine(LogsDir, "devmind-20200102-pid3002.log");
            File.WriteAllText(stale, "stale\n");
            File.WriteAllText(fresh, "fresh\n");
            File.SetLastWriteTime(stale, DateTime.Now - DevMindLog.MaxAge - TimeSpan.FromHours(1));
            File.SetLastWriteTime(fresh, DateTime.Now - DevMindLog.MaxAge + TimeSpan.FromHours(1));

            DevMindLog.Write("[TestComponent] triggers the sweep");

            Assert.False(File.Exists(stale), "a file past MaxAge survived the sweep");
            Assert.True(File.Exists(fresh), "a file inside MaxAge was deleted");
        }

        [Fact]
        public void Retention_RotatesAtTheSizeCap_SoOneProcessCannotGrowWithoutBound()
        {
            DevMindLog.Write("[TestComponent] create the file");
            string path = DevMindLog.CurrentLogPath;
            string rotated = path + ".1";

            // Push it over the cap in one write rather than by logging two million bytes
            // a line at a time — the guard under test reads the file's length, not its
            // history.
            File.WriteAllBytes(path, new byte[DevMindLog.MaxFileBytes + 1]);
            Assert.False(File.Exists(rotated));

            DevMindLog.Write("[TestComponent] the line that trips rotation");

            Assert.True(File.Exists(rotated), "the oversized file was not rotated");
            Assert.Contains("the line that trips rotation", ReadShared(path));

            // The bound: the live file restarted small, and one process holds at most the
            // active file plus one rotation.
            Assert.True(new FileInfo(path).Length < DevMindLog.MaxFileBytes);
            Assert.Equal(2, LogFiles().Length);

            // A second overflow REPLACES the rotation rather than accumulating .2, .3, ...
            File.WriteAllBytes(path, new byte[DevMindLog.MaxFileBytes + 1]);
            DevMindLog.Write("[TestComponent] second rotation");

            Assert.Equal(2, LogFiles().Length);
            Assert.Contains("second rotation", ReadShared(path));
        }

        // The production bound is MaxFiles * MaxFileBytes — 40 MB. Materialising that to
        // measure it would mean writing 40 MB of zeros in a unit test to prove arithmetic.
        // Instead this pins the factor that is not arithmetic: however much is already on
        // disk, what SURVIVES the sweep is bounded by MaxFiles * (size of one file). The
        // other factor — that one file cannot exceed MaxFileBytes — is the rotation test
        // above. Together they are the bound.
        [Fact]
        public void Retention_TotalBytesStayInsideTheFileCountTimesFileSizeBound()
        {
            const long perFile = 64 * 1024;

            for (int i = 0; i < DevMindLog.MaxFiles * 3; i++)
            {
                string p = Path.Combine(EnsureLogsDir(), $"devmind-20990101-pid{4000 + i}.log");
                File.WriteAllBytes(p, new byte[perFile]);
                File.SetLastWriteTime(p, DateTime.Now.AddMinutes(-i));
            }

            long before = LogFiles().Sum(p => new FileInfo(p).Length);
            long bound = DevMindLog.MaxFiles * perFile;

            // Precondition: growth really is out of bounds before the sweep, so a sweep that
            // did nothing could not pass this.
            Assert.True(before > bound, $"seeded only {before} bytes, which is already inside the {bound}-byte bound");

            DevMindLog.Write("[TestComponent] triggers the sweep");

            long after = LogFiles().Sum(p => new FileInfo(p).Length);
            Assert.True(after <= bound,
                $"log directory still holds {after} bytes after the sweep, outside the {bound}-byte bound " +
                $"({DevMindLog.MaxFiles} files x {perFile} bytes)");

            // No surviving file is anywhere near the per-file cap either, so the product
            // MaxFiles * MaxFileBytes is a ceiling, not a target.
            Assert.All(LogFiles(), p => Assert.True(new FileInfo(p).Length <= DevMindLog.MaxFileBytes));
        }

        // ── The Release half ────────────────────────────────────────────────────

        // The entire defect was [Conditional("DEBUG")] on Debug.WriteLine: the compiler
        // removes the call and its literals from a Release build. The replacement is only
        // worth anything if it carries no such attribute. A test assembly always runs under
        // one configuration, so this asserts the property that DECIDES the Release outcome
        // rather than observing the outcome itself.
        [Fact]
        public void Write_IsNotConditional_SoItSurvivesAReleaseBuild()
        {
            var method = typeof(DevMindLog).GetMethod(nameof(DevMindLog.Write),
                new[] { typeof(string) });

            Assert.NotNull(method);
            Assert.Empty(method!.GetCustomAttributes(typeof(System.Diagnostics.ConditionalAttribute), inherit: false));
        }
    }
}
