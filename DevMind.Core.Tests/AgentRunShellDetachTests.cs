// File: AgentRunShellDetachTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The agent-side run_shell `detach` argument (watchlist H-24) must travel the whole path:
// tool call -> ToolCallMapper (ResponseBlock.ShellDetach) -> AgenticExecutor ->
// IAgenticHost.RunShellAsync(detach) -> ShellRunner.ExecuteAsync(detach).
//
//   * Executor wiring: a mapped run_shell call reaches the host with its detach flag
//     (LoopDriverTestEnv.FakeHost records it).
//   * BufferedAgenticHost -> ShellRunner: the host holds a concrete ShellRunner, so the
//     observable effect is checked instead — a Start-Process child survives the call
//     with detach=true and is terminated without it (ShellRunnerDetachTests' contract).

using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    [Collection("ShellRunnerJobObject")]
    public class AgentRunShellDetachTests : IDisposable
    {
        private const string StartChild =
            "$p = Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 30' " +
            "-WindowStyle Hidden -PassThru; Write-Output \"CHILD=$($p.Id)\"";

        private readonly string _dir;
        private readonly DateTime _startedAt = DateTime.Now.AddSeconds(-1);

        public AgentRunShellDetachTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_agentdetach_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData(null, false)]
        public async Task MappedRunShell_ReachesHostWithDetachFlag(string? detachArg, bool expected)
        {
            var args = new Dictionary<string, string> { ["command"] = "echo hi" };
            if (detachArg != null) args["detach"] = detachArg;
            var blocks = ToolCallMapper.Map(
                new List<ToolCallResult> { new ToolCallResult { Name = "run_shell", Arguments = args } },
                buildCommand: "dotnet build");

            var host = new FakeHost(_dir);
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);
            await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild }, new ResponseOutcome(blocks));

            Assert.Equal(expected, host.LastShellDetach);
        }

        [Fact]
        public async Task BufferedHost_DetachTrue_ChildOutlivesTheCall()
        {
            if (!OperatingSystem.IsWindows() || !ShellRunner.IsPowerShellAvailable())
                return; // job objects and Start-Process are Windows-only

            IAgenticHost host = new BufferedAgenticHost(_dir);
            var (exitCode, output) = await host.RunShellAsync(StartChild, timeoutSeconds: 60, detach: true);
            Assert.True(exitCode == 0, $"exit {exitCode}: {output}");
            int pid = ChildPid(output);

            try
            {
                await Task.Delay(2_000);
                Assert.True(IsOurChildAlive(pid), $"child PID {pid} was terminated despite detach: true");
            }
            finally
            {
                KillIfOurs(pid);
            }
        }

        [Fact]
        public async Task BufferedHost_DetachOmitted_ChildIsTerminated()
        {
            if (!OperatingSystem.IsWindows() || !ShellRunner.IsPowerShellAvailable())
                return; // job objects and Start-Process are Windows-only

            IAgenticHost host = new BufferedAgenticHost(_dir);
            var (exitCode, output) = await host.RunShellAsync(StartChild, timeoutSeconds: 60);
            Assert.True(exitCode == 0, $"exit {exitCode}: {output}");
            int pid = ChildPid(output);

            try
            {
                var sw = Stopwatch.StartNew();
                while (IsOurChildAlive(pid) && sw.ElapsedMilliseconds < 5_000)
                    await Task.Delay(100);
                Assert.False(IsOurChildAlive(pid), $"child PID {pid} outlived the call without detach");
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
