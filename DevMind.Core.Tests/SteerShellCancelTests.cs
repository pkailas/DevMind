// File: SteerShellCancelTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// An OVERRIDE steer must not wait out a long shell call at the iteration boundary
// (job-1713 sat ~4.5 min in a runaway PowerShell loop before an override could land):
//
//   * override during a running run_shell cancels THAT call — the turn returns within
//     seconds, the model's tool result says the steer cancelled it, the steer is folded
//     at the next boundary, and the job is NOT cancelled
//   * a suggestion never cancels anything; neither does an override the next boundary
//     would refuse (last iteration)
//   * a job cancel still yields the ordinary "[SHELL] Command cancelled." result
//   * CancelInFlightShell is a no-op with no call in flight
//   * a detach call's detached children survive the steer cancel
//
// The shell command drops a marker file before it sleeps, so the test enqueues the steer
// only once the call is provably running.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace DevMind.Core.Tests
{
    [Collection("ShellRunnerJobObject")]
    public class SteerShellCancelTests : IDisposable
    {
        private const string Marker = "shell-started.flag";
        private readonly string _dir;
        private readonly DateTime _startedAt = DateTime.Now.AddSeconds(-1);

        public SteerShellCancelTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_steer_shell_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static bool CanRunShell => OperatingSystem.IsWindows() && ShellRunner.IsPowerShellAvailable();

        private static string SleepCommand(int seconds) =>
            $"New-Item -ItemType File {Marker} | Out-Null; Start-Sleep {seconds}";

        private static string RunShellArgs(string command) =>
            "{\"command\":" + System.Text.Json.JsonSerializer.Serialize(command) + "}";

        private HeadlessSession NewSession(string endpoint, int maxDepth = 5) => new(
            new HeadlessOptions
            {
                RequestTimeoutMinutes = 1,
                FirstTokenTimeoutMinutes = 1,
                ManualContextSize = 32768,
                AgenticLoopMaxDepth = maxDepth,
            },
            endpoint, apiKey: null!,
            workingDirectory: _dir, buildCommand: "dotnet build",
            promptFilePath: Path.Combine(_dir, "nonexistent-prompt.md"));

        private async Task WaitForMarkerAsync(int timeoutMs = 20_000)
        {
            string path = Path.Combine(_dir, Marker);
            var sw = Stopwatch.StartNew();
            while (!File.Exists(path) && sw.ElapsedMilliseconds < timeoutMs)
                await Task.Delay(20);
            Assert.True(File.Exists(path), "the shell call never started");
        }

        // Runs a two-iteration turn (run_shell, then task_done), enqueueing the steer once
        // the shell call is running. Returns the result, the elapsed time, and the requests.
        private async Task<(HeadlessAgentResult result, TimeSpan elapsed, List<string> requests)>
            RunTurnWithSteerAsync(int sleepSeconds, string steer, SteerMode mode, int maxDepth = 5)
        {
            using var server = new GatedSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("run_shell", RunShellArgs(SleepCommand(sleepSeconds))));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = NewSession(server.BaseUrl, maxDepth);
                var sw = Stopwatch.StartNew();
                var turn = session.RunTurnAsync("Run the thing.");
                await WaitForMarkerAsync();
                Assert.True(session.EnqueueSteer(steer, mode).Accepted);
                var result = await turn;
                sw.Stop();
                List<string> requests;
                lock (server.RequestBodies) requests = server.RequestBodies.ToList();
                return (result, sw.Elapsed, requests);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        [Fact]
        public async Task OverrideSteer_CancelsInFlightShell_AndTheJobKeepsRunning()
        {
            if (!CanRunShell) return;

            var (result, elapsed, requests) = await RunTurnWithSteerAsync(
                30, "stop sleeping and summarize", SteerMode.Override);

            Assert.True(elapsed < TimeSpan.FromSeconds(15), $"turn took {elapsed} — the 30s sleep was not cancelled");
            Assert.Null(result.Error);
            Assert.False(result.Cancelled);
            Assert.Equal(2, result.Iterations);

            // The model is told why the call ended, and the steer follows at the boundary.
            Assert.Equal(2, requests.Count);
            Assert.Contains("Cancelled by the driver's override steer", requests[1]);
            Assert.Contains("[CALLER STEER — override]", requests[1]);
            Assert.Contains("stop sleeping and summarize", requests[1]);

            HostAction shell = Assert.Single(result.Actions, a => a.Kind == "shell");
            Assert.False(shell.Success);
            Assert.Contains("cancelled by override steer", shell.Detail);
            Assert.Single(result.Actions, a => a.Kind == "steer");
        }

        [Fact]
        public async Task SuggestSteer_DoesNotCancelTheShellCall()
        {
            if (!CanRunShell) return;

            var (result, elapsed, requests) = await RunTurnWithSteerAsync(
                4, "also note the timing", SteerMode.Suggest);

            Assert.True(elapsed >= TimeSpan.FromSeconds(3), $"turn took only {elapsed} — the sleep was cut short");
            Assert.Null(result.Error);
            Assert.False(result.Cancelled);
            Assert.DoesNotContain("Cancelled by the driver's override steer", requests[1]);
            Assert.Contains("[CALLER STEER — suggestion]", requests[1]);

            HostAction shell = Assert.Single(result.Actions, a => a.Kind == "shell");
            Assert.True(shell.Success, shell.Detail);
            Assert.DoesNotContain("override steer", shell.Detail);
        }

        [Fact]
        public async Task OverrideTheNextBoundaryWouldRefuse_DoesNotCancelTheShellCall()
        {
            if (!CanRunShell) return;

            // maxDepth 1: the boundary after this call is the last iteration, which refuses
            // an override — so cancelling the call for it would buy nothing.
            var (result, elapsed, requests) = await RunTurnWithSteerAsync(
                4, "abort", SteerMode.Override, maxDepth: 1);

            Assert.True(elapsed >= TimeSpan.FromSeconds(3), $"turn took only {elapsed} — the sleep was cut short");
            Assert.Null(result.Error);
            Assert.DoesNotContain("Cancelled by the driver's override steer", requests[1]);
            Assert.True(Assert.Single(result.Actions, a => a.Kind == "shell").Success);
            Assert.Single(result.Actions, a => a.Kind == "steer_rejected");
        }

        [Fact]
        public async Task JobCancel_DuringShell_YieldsTheOrdinaryCancelledResult()
        {
            if (!CanRunShell) return;

            using var server = new GatedSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("run_shell", RunShellArgs(SleepCommand(30))));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = NewSession(server.BaseUrl);
                using var jobCts = new CancellationTokenSource();
                var sw = Stopwatch.StartNew();
                var turn = session.RunTurnAsync("Run the thing.", ct: jobCts.Token);
                await WaitForMarkerAsync();
                jobCts.Cancel();
                var result = await turn;

                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"turn took {sw.Elapsed}");
                Assert.True(result.Cancelled);
                HostAction shell = Assert.Single(result.Actions, a => a.Kind == "shell");
                Assert.False(shell.Success);
                Assert.DoesNotContain("override steer", shell.Detail);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        [Fact]
        public async Task HostJobCancel_ReturnsCommandCancelled_NotTheSteerMessage()
        {
            if (!CanRunShell) return;

            var host = new BufferedAgenticHost(_dir);
            using var jobCts = new CancellationTokenSource();
            host.CancellationToken = jobCts.Token;
            var call = ((IAgenticHost)host).RunShellAsync(SleepCommand(30), timeoutSeconds: 60);
            await WaitForMarkerAsync();
            jobCts.Cancel();
            var (exitCode, output) = await call;

            Assert.Equal(-1, exitCode);
            Assert.Contains("[SHELL] Command cancelled.", output);
            Assert.DoesNotContain(BufferedAgenticHost.OverrideSteerCancelMessage, output);
        }

        [Fact]
        public async Task CancelInFlightShell_IsANoOp_WithNoCallInFlight()
        {
            var host = new BufferedAgenticHost(_dir);
            Assert.False(host.CancelInFlightShell("nothing running"));

            if (!CanRunShell) return;
            var (exitCode, _) = await ((IAgenticHost)host).RunShellAsync("Write-Output hi", timeoutSeconds: 30);
            Assert.Equal(0, exitCode);
            Assert.False(host.CancelInFlightShell("the call already ended"));
            Assert.DoesNotContain(host.GetActions(), a => a.Detail.Contains("override steer"));
        }

        [Fact]
        public async Task CancelInFlightShell_OnADetachCall_SparesTheDetachedChild()
        {
            if (!CanRunShell) return;

            const string command =
                "$p = Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 30' " +
                "-WindowStyle Hidden -PassThru; Write-Output \"CHILD=$($p.Id)\"; " +
                "New-Item -ItemType File " + Marker + " | Out-Null; Start-Sleep 30";

            var host = new BufferedAgenticHost(_dir);
            var call = ((IAgenticHost)host).RunShellAsync(command, timeoutSeconds: 60, detach: true);
            await WaitForMarkerAsync();
            Assert.True(host.CancelInFlightShell("override"));
            var (_, output) = await call;

            Assert.StartsWith(BufferedAgenticHost.OverrideSteerCancelMessage, output);
            var m = Regex.Match(output, @"CHILD=(\d+)");
            Assert.True(m.Success, $"no CHILD=<pid> line in output: {output}");
            int pid = int.Parse(m.Groups[1].Value);
            try
            {
                await Task.Delay(2_000);
                Assert.True(IsOurChildAlive(pid), $"detached child PID {pid} was killed by the steer cancel");
            }
            finally
            {
                KillIfOurs(pid);
            }
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
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
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
