// File: ShellRunnerReapTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for shell-timeout process-reap observability and the
// MSBuild node-reuse runaway (a timed-out `dotnet test` left a persistent
// MSBuild worker that grew to 64 GB):
//   CHANGE 1: every spawned shell must carry MSBUILDDISABLENODEREUSE=1 so
//             dotnet build/test does not leave a worker pool behind.
//   CHANGE 2: taskkill exit code 128 ("process not found" — the target already
//             exited) must NOT be classified as a reap failure.
//   CHANGE 3 (a process still alive 5s after the reap): not tested here — it
//             requires a child that genuinely survives `taskkill /F /T` (a
//             double-forked / detached daemon), and spawning one risks exactly
//             the runaway this change exists to make visible. Classified as
//             safe-to-skip; the ClassifyReapResult cases below cover the
//             classification logic CHANGE 3 relies on.

using System;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ShellRunnerReapTests : IDisposable
    {
        private readonly string _dir;
        private string? _savedEnv;
        private bool _hadEnv;

        public ShellRunnerReapTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_shrp_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            // The spawned shell INHERITS the test-host process environment. If
            // MSBUILDDISABLENODEREUSE were set here, the test would pass even
            // without the ShellRunner change — clear it so only the change
            // itself can make the assertion hold.
            _savedEnv = Environment.GetEnvironmentVariable("MSBUILDDISABLENODEREUSE");
            _hadEnv = _savedEnv != null;
            Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", null);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
            if (_hadEnv) Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", _savedEnv!);
            else Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", null);
        }

        [Fact]
        public async Task SpawnedShell_EnvironmentContainsMsbDisableNodeReuse()
        {
            // CHANGE 1: the env var must be present in the spawned shell's
            // environment. `cmd /c set` prints the full environment of a child
            // cmd.exe — works whether the runner chose PowerShell or cmd.exe,
            // and is unaffected by host inheritance (cleared in ctor). With the
            // fix the output contains MSBUILDDISABLENODEREUSE=1; without it the
            // variable is absent and the test FAILS.
            var runner = new ShellRunner(_dir);
            var (output, exitCode) = await runner.ExecuteAsync("cmd /c set", timeoutSeconds: 30);

            Assert.True(exitCode == 0, $"exit {exitCode}: {output}");
            Assert.Contains("MSBUILDDISABLENODEREUSE=1", output);
        }

        [Fact]
        public void ClassifyReap_Exit128_AlreadyExited_IsNotAReapFailure()
        {
            // taskkill exit 128 = "process not found". Normal when the target
            // already exited — must NOT surface as a failed reap (CHANGE 2).
            var result = ShellRunner.ClassifyReapResult(128);
            Assert.True(result.Succeeded, $"128 reported as failure: {result.Reason}");
        }

        [Fact]
        public void ClassifyReap_Exit0_Killed_IsASuccess()
        {
            var result = ShellRunner.ClassifyReapResult(0);
            Assert.True(result.Succeeded, $"0 reported as failure: {result.Reason}");
        }

        [Fact]
        public void ClassifyReap_Exit1_IsAReapFailure()
        {
            // taskkill exit 1 = "process not found" is 128; exit 1 is a real
            // error (e.g. syntax). Must be surfaced so a survivor is visible.
            var result = ShellRunner.ClassifyReapResult(1);
            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrEmpty(result.Reason));
        }

        [Fact]
        public void ClassifyReap_Exception_IsAReapFailure()
        {
            var result = ShellRunner.ClassifyReapResult(null, new Exception("boom"));
            Assert.False(result.Succeeded);
            Assert.Contains("boom", result.Reason);
        }

        [Fact]
        public void ClassifyReap_NullExitCode_IsAReapFailure()
        {
            // taskkill never produced an exit code — that is not a success and
            // must not be silently swallowed (the old bare catch did exactly that).
            var result = ShellRunner.ClassifyReapResult(null);
            Assert.False(result.Succeeded);
        }
    }
}
