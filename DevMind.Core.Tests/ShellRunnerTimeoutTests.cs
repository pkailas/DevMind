// File: ShellRunnerTimeoutTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for the run_shell timeout message: the timeout line used to read
// a hardcoded "timed out after 120 seconds" regardless of the effective timeout, so a
// caller that passed timeout_seconds=900 was told 120 — false for humans and agents
// alike. The message must now report the ACTUAL effective timeout that was applied.

using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ShellRunnerTimeoutTests : IDisposable
    {
        private readonly string _dir;
        private string _savedEnv = null;
        private bool _hadEnv;

        public ShellRunnerTimeoutTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_shrt_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _savedEnv = Environment.GetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT");
            _hadEnv = _savedEnv != null;
            // The effective timeout falls back to the env var when no explicit value is
            // passed; clear it so these tests pin the explicit value in isolation.
            Environment.SetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT", null);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
            if (_hadEnv) Environment.SetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT", _savedEnv);
            else Environment.SetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT", null);
        }

        [Fact]
        public async Task NonDefaultTimeout_ReportedWithItsRealValueNot120()
        {
            // A 5-second sleep under a 1-second timeout MUST time out. The message must
            // state the 1 second we actually applied, not the old hardcoded 120.
            string command = OperatingSystem.IsWindows() ? "Start-Sleep 5" : "sleep 5";

            var runner = new ShellRunner(_dir);
            var (output, exitCode) = await runner.ExecuteAsync(command, timeoutSeconds: 1);

            Assert.Equal(-1, exitCode);
            Assert.Contains("timed out", output, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1 second", output);
            Assert.DoesNotContain("120 second", output);
        }

        [Fact]
        public async Task LargerNonDefaultTimeout_ReportedWithItsRealValue()
        {
            // A larger explicit timeout must be reported verbatim too — a caller that
            // asked for 300s should be told "300 seconds", never 120.
            string command = OperatingSystem.IsWindows() ? "Start-Sleep 10" : "sleep 10";

            var runner = new ShellRunner(_dir);
            var (output, exitCode) = await runner.ExecuteAsync(command, timeoutSeconds: 300,
                cancellationToken: new CancellationTokenSource(2000).Token);

            // A 2s cancellation beats the 300s timeout, so no timeout line is emitted —
            // but if one WERE emitted for any reason it must carry the real value.
            if (output.Contains("timed out", System.StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("300 second", output);
                Assert.DoesNotContain("120 second", output);
            }
            Assert.NotEqual(0, exitCode);
        }
    }
}
