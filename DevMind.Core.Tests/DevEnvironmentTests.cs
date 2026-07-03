// File: DevEnvironmentTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the GUI-launch environment bootstrap. Serialized (env vars are
// process-global) via xUnit's default single-threaded collection behavior per class.

using Xunit;

namespace DevMind.Core.Tests
{
    public class DevEnvironmentTests
    {
        [Fact]
        public void EnrichProcessEnvironment_IsIdempotent_AndSetsShellTimeoutWhenUnset()
        {
            string? priorPath = Environment.GetEnvironmentVariable("PATH");
            string? priorTimeout = Environment.GetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT");
            try
            {
                Environment.SetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT", null);

                DevEnvironment.EnrichProcessEnvironment();
                string pathAfterFirst = Environment.GetEnvironmentVariable("PATH")!;
                Assert.Equal(
                    DevEnvironment.DefaultShellTimeoutSeconds.ToString(),
                    Environment.GetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT"));

                // Second run must be a no-op: nothing re-appended, timeout untouched.
                string? secondChanges = DevEnvironment.EnrichProcessEnvironment();
                Assert.Null(secondChanges);
                Assert.Equal(pathAfterFirst, Environment.GetEnvironmentVariable("PATH"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", priorPath);
                Environment.SetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT", priorTimeout);
            }
        }

        [Fact]
        public void EnrichProcessEnvironment_RestoresPathExtWhenMissing()
        {
            // Regression: Desktop-launched MCP server had no PATHEXT — PowerShell then
            // treats .exe files as documents (detached launch, empty stdout, blank
            // $LASTEXITCODE), which burned a live delegation's entire depth cap.
            string? priorPathExt = Environment.GetEnvironmentVariable("PATHEXT");
            string? priorPath = Environment.GetEnvironmentVariable("PATH");
            string? priorTimeout = Environment.GetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT");
            try
            {
                Environment.SetEnvironmentVariable("PATHEXT", null);
                string? changes = DevEnvironment.EnrichProcessEnvironment();

                string? pathExt = Environment.GetEnvironmentVariable("PATHEXT");
                Assert.NotNull(pathExt);
                Assert.Contains(".EXE", pathExt);
                Assert.Contains(".CMD", pathExt);
                Assert.NotNull(changes);
                Assert.Contains("PATHEXT", changes);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATHEXT", priorPathExt);
                Environment.SetEnvironmentVariable("PATH", priorPath);
                Environment.SetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT", priorTimeout);
            }
        }

        [Fact]
        public void EnrichProcessEnvironment_RespectsExistingShellTimeout()
        {
            string? priorTimeout = Environment.GetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT");
            string? priorPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT", "45");
                DevEnvironment.EnrichProcessEnvironment();
                Assert.Equal("45", Environment.GetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", priorPath);
                Environment.SetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT", priorTimeout);
            }
        }

        [Fact]
        public void EnrichProcessEnvironment_AddsMissingExistingDirectory()
        {
            string? priorPath = Environment.GetEnvironmentVariable("PATH");
            string? priorTimeout = Environment.GetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT");
            try
            {
                string dotnetDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
                if (!Directory.Exists(dotnetDir))
                    return; // machine without dotnet in Program Files — nothing to assert

                // Strip the dotnet dir from PATH, then enrich — it must come back.
                var kept = (priorPath ?? "").Split(Path.PathSeparator)
                    .Where(s => !string.Equals(s.TrimEnd('\\'), dotnetDir, StringComparison.OrdinalIgnoreCase));
                Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, kept));

                string? changes = DevEnvironment.EnrichProcessEnvironment();

                Assert.NotNull(changes);
                Assert.Contains(dotnetDir, changes);
                Assert.Contains(dotnetDir, Environment.GetEnvironmentVariable("PATH"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", priorPath);
                Environment.SetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT", priorTimeout);
            }
        }
    }
}
