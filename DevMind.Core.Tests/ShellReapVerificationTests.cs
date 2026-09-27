// File: ShellReapVerificationTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-29: "[SHELL] Failed to reap process tree (PID 103952): taskkill exited with code 255" (job-1713)
// was a false alarm. Every cancel ran TWO taskkills at once — a "best-effort early" kill registered
// on the cancel token and the reap — and the loser hit a tree already dying: exit 255 / 1 / 128 with
// "There is no running instance of the task" or "Access is denied". Measured over 18 runs of every
// cancel route; no child ever survived.
//
// Now: one reap per call, the job object (TerminateJobObject) is the primary kill, taskkill is the
// fallback / tree walk, and the verdict is what is still ALIVE (the job's live PID list plus the
// root) — never taskkill's exit code on its own.

using System.Diagnostics;
using Xunit;

namespace DevMind.Core.Tests
{
    [Collection("ShellRunnerJobObject")]
    public sealed class ShellReapVerificationTests : IDisposable
    {
        private readonly string _dir;
        private readonly DateTime _startedAt = DateTime.Now.AddSeconds(-1);

        public ShellReapVerificationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_reapv_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        // ── ClassifyReapResult, by what survived ──

        public static IEnumerable<object?[]> Rows => new[]
        {
            // exit, stderr, survivors, succeeded, reason must contain
            new object?[] { 0,   "",                                                                   new int[0],        true,  "process tree gone" },
            new object?[] { 128, "ERROR: The process \"113224\" not found.",                          new int[0],        true,  "taskkill exit 128" },
            new object?[] { 255, "ERROR: The process with PID 53488 (child process of PID 107656) could not be terminated.\nReason: There is no running instance of the task.", new int[0], true, "not a survivor" },
            new object?[] { 1,   "ERROR: The process with PID 102748 could not be terminated.\nReason: Access is denied.", new int[0], true, "taskkill exit 1" },
            new object?[] { 0,   "",                                                                   new[] { 4242 },    false, "still alive after the reap: PID 4242" },
            new object?[] { 255, "ERROR: The process with PID 77 (child process of PID 42) could not be terminated.\nReason: Access is denied.", new[] { 77, 42 }, false, "PID 42, 77; taskkill exit 255: ERROR: The process with PID 77" },
            new object?[] { 1,   "ERROR: The process with PID 9 could not be terminated.\nReason: Access is denied.", new[] { 9 }, false, "taskkill exit 1" },
            new object?[] { 128, "ERROR: The process \"9\" not found.",                               new[] { 9 },       false, "still alive after the reap: PID 9" },
        };

        [Theory]
        [MemberData(nameof(Rows))]
        public void Classify_BySurvivors(int? exit, string stderr, int[] survivors, bool succeeded, string reasonContains)
        {
            var r = ShellRunner.ClassifyReapResult(exit, stderr, survivors);
            Assert.Equal(succeeded, r.Succeeded);
            Assert.Contains(reasonContains, r.Reason);
        }

        [Fact]
        public void Classify_TaskkillThrew_ButTreeGone_Succeeds()
        {
            var r = ShellRunner.ClassifyReapResult(null, null, Array.Empty<int>(), new Exception("boom"));
            Assert.True(r.Succeeded);
            Assert.Contains("taskkill threw: boom", r.Reason);
        }

        [Fact]
        public void Classify_SurvivorsUnknown_FallsBackToTheExitCodeRule()
        {
            Assert.True(ShellRunner.ClassifyReapResult(128, "not found", null).Succeeded);
            Assert.False(ShellRunner.ClassifyReapResult(255, "boom", null).Succeeded);
        }

        // ── Real processes, one per cancel route ──

        private static bool CanRunShell => OperatingSystem.IsWindows() && ShellRunner.IsPowerShellAvailable();

        private const string NestedChild =
            "powershell -NoProfile -Command \"$PID | Out-File -Encoding ascii child.pid; Start-Sleep 60\"";

        private sealed class Lines : IProgress<ShellOutputLine>
        {
            public readonly List<string> All = new();
            public void Report(ShellOutputLine v) { lock (All) All.Add(v.Line); }
        }

        private bool IsOurs(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return !p.HasExited && p.StartTime >= _startedAt; }
            catch { return false; }
        }

        private async Task<int> ChildPidAsync()
        {
            string pidFile = Path.Combine(_dir, "child.pid");
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 20_000)
            {
                try
                {
                    if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid)) return pid;
                }
                catch (IOException) { /* still being written */ }
                await Task.Delay(50);
            }
            throw new TimeoutException("the nested child never started");
        }

        // Runs the nested child through `route`, twice (the old race lost roughly one run in three),
        // and asserts every child is gone and no reap failure was reported.
        private async Task AssertCleanReapAsync(string route, bool detach)
        {
            for (int rep = 0; rep < 2; rep++)
            {
                File.Delete(Path.Combine(_dir, "child.pid"));
                using var job = new CancellationTokenSource();
                using var interrupt = new CancellationTokenSource();
                var lines = new Lines();
                var run = new ShellRunner(_dir).ExecuteAsync(
                    NestedChild, job.Token, route == "timeout" ? 3 : 60, lines, detach, interrupt.Token);

                int child = await ChildPidAsync();
                if (route == "job-cancel") job.Cancel();
                else if (route == "interrupt") interrupt.Cancel();
                var (output, exitCode) = await run;

                try
                {
                    Assert.Equal(-1, exitCode);
                    Assert.DoesNotContain("Failed to reap", output);
                    Assert.DoesNotContain(lines.All, l => l.Contains("Failed to reap"));
                    Assert.DoesNotContain("still alive", output);
                    Assert.False(IsOurs(child), $"{route} (detach={detach}) left child PID {child} running");
                }
                finally
                {
                    if (IsOurs(child)) { try { Process.GetProcessById(child).Kill(); } catch { } }
                }
            }
        }

        [Fact]
        public async Task JobCancel_ReapsTheTree_NoFalseFailure()
        {
            if (!CanRunShell) return;
            await AssertCleanReapAsync("job-cancel", detach: false);
        }

        [Fact]
        public async Task Timeout_ReapsTheTree_NoFalseFailure()
        {
            if (!CanRunShell) return;
            await AssertCleanReapAsync("timeout", detach: false);
        }

        [Fact]
        public async Task OverrideSteerInterrupt_ReapsTheTree_NoFalseFailure()
        {
            if (!CanRunShell) return;
            await AssertCleanReapAsync("interrupt", detach: false);
        }

        [Fact]
        public async Task DetachCall_JobCancel_StillReapsTheWholeTree_NoFalseFailure()
        {
            // A job cancel is a full reap even for detach=true (the documented contract).
            if (!CanRunShell) return;
            await AssertCleanReapAsync("job-cancel", detach: true);
        }

        [Fact]
        public async Task DetachCall_Interrupt_NoFalseFailure()
        {
            // The interrupt kills only the shell of a detach call (its detached children survive —
            // pinned by SteerShellCancelTests); here: the reap reports no false failure.
            if (!CanRunShell) return;
            using var interrupt = new CancellationTokenSource();
            var lines = new Lines();
            var run = new ShellRunner(_dir).ExecuteAsync(
                "New-Item -ItemType File started.flag | Out-Null; Start-Sleep 60", CancellationToken.None, 60, lines, detach: true, interruptToken: interrupt.Token);
            var sw = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(_dir, "started.flag")) && sw.ElapsedMilliseconds < 20_000) await Task.Delay(50);
            interrupt.Cancel();
            var (output, _) = await run;

            Assert.DoesNotContain("Failed to reap", output);
            Assert.DoesNotContain(lines.All, l => l.Contains("Failed to reap"));
        }
    }
}
