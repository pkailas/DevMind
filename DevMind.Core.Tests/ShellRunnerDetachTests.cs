// File: ShellRunnerDetachTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Child-process lifetime for run_shell (watchlist H-24). Every command runs in a
// per-command job with KILL_ON_JOB_CLOSE, so a process it starts (Start-Process of a
// GUI to inspect in a later call) is killed when the call returns — jobs 1697/1698 and
// the driver lost iterations on "launch, then check -> NOT RUNNING". detach: true arms
// SILENT_BREAKAWAY_OK on that job: the shell stays contained, its children do not.
//
// Both tests start `powershell -Command Start-Sleep 30` via Start-Process and read its
// PID from the command output. The detach=true test kills the child itself.

using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    [Collection("ShellRunnerJobObject")]
    public class ShellRunnerDetachTests : IDisposable
    {
        private const string StartChild =
            "$p = Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 30' " +
            "-WindowStyle Hidden -PassThru; Write-Output \"CHILD=$($p.Id)\"";

        private readonly string _dir;
        private readonly DateTime _startedAt = DateTime.Now.AddSeconds(-1);

        public ShellRunnerDetachTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_shdetach_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public async Task WithoutDetach_ChildIsTerminatedWhenTheCallReturns()
        {
            if (!OperatingSystem.IsWindows() || !ShellRunner.IsPowerShellAvailable())
                return; // job objects and Start-Process are Windows-only

            var (output, exitCode) = await new ShellRunner(_dir).ExecuteAsync(StartChild, timeoutSeconds: 60);
            Assert.True(exitCode == 0, $"exit {exitCode}: {output}");
            int pid = ChildPid(output);

            try
            {
                // Closing the job is synchronous but termination is not — allow a short grace.
                var sw = Stopwatch.StartNew();
                while (IsOurChildAlive(pid) && sw.ElapsedMilliseconds < 5_000)
                    await Task.Delay(100);

                Assert.False(IsOurChildAlive(pid),
                    $"child PID {pid} outlived the run_shell call without detach");
            }
            finally
            {
                KillIfOurs(pid);
            }
        }

        [Fact]
        public async Task WithDetach_ChildOutlivesTheCall()
        {
            if (!OperatingSystem.IsWindows() || !ShellRunner.IsPowerShellAvailable())
                return; // job objects and Start-Process are Windows-only

            var (output, exitCode) = await new ShellRunner(_dir).ExecuteAsync(StartChild, timeoutSeconds: 60, detach: true);
            Assert.True(exitCode == 0, $"exit {exitCode}: {output}");
            int pid = ChildPid(output);

            try
            {
                // Same grace the negative test allows, so "alive" is not a race win.
                await Task.Delay(2_000);
                Assert.True(IsOurChildAlive(pid), $"child PID {pid} was terminated despite detach: true");
            }
            finally
            {
                KillIfOurs(pid);
            }
        }

        private static int ChildPid(string output)
        {
            var m = Regex.Match(output, @"CHILD=(\d+)");
            Assert.True(m.Success, $"no CHILD=<pid> line in output: {output}");
            return int.Parse(m.Groups[1].Value);
        }

        // Guards against PID reuse: only a powershell started during this test counts.
        private bool IsOurChildAlive(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited
                    && string.Equals(p.ProcessName, "powershell", StringComparison.OrdinalIgnoreCase)
                    && p.StartTime >= _startedAt;
            }
            catch (ArgumentException) { return false; }      // no such process
            catch (InvalidOperationException) { return false; } // exited between calls
        }

        private void KillIfOurs(int pid)
        {
            if (!IsOurChildAlive(pid)) return;
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5_000);
            }
            catch { /* best effort cleanup */ }
        }
    }
}
