// File: ShellRunnerMultiLineTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for the run_shell newline fix (MCP complaint #1): multi-line
// PowerShell commands and here-strings previously arrived flattened to one line
// (SanitizeCommand collapses newlines inside quoted spans) and died on parse
// errors — callers ended up smuggling files through base64 one-liners. Multi-line
// commands now go through -EncodedCommand and reach PowerShell verbatim.

using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ShellRunnerMultiLineTests : IDisposable
    {
        private readonly string _dir;

        public ShellRunnerMultiLineTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_shml_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public async Task MultiLineHereStringSurvivesVerbatim()
        {
            if (!OperatingSystem.IsWindows() || !ShellRunner.IsPowerShellAvailable())
                return; // requires Windows PowerShell

            var runner = new ShellRunner(_dir);
            string target = Path.Combine(_dir, "hs.txt");

            // A here-string whose terminator MUST sit at column 0 on its own line —
            // the exact construct the old flattening destroyed.
            string command =
                "$content = @'\n" +
                "line one\n" +
                "line \"two\" with quotes\n" +
                "'@\n" +
                $"Set-Content -LiteralPath '{target}' -Value $content\n";

            var (output, exitCode) = await runner.ExecuteAsync(command, timeoutSeconds: 60);

            Assert.True(exitCode == 0, $"exit {exitCode}: {output}");
            string written = File.ReadAllText(target);
            Assert.Contains("line one", written);
            Assert.Contains("line \"two\" with quotes", written);
        }

        [Fact]
        public async Task MultiLineScriptRunsEachStatement()
        {
            if (!OperatingSystem.IsWindows() || !ShellRunner.IsPowerShellAvailable())
                return; // requires Windows PowerShell

            var runner = new ShellRunner(_dir);
            string command = "Write-Output 'alpha'\nWrite-Output 'beta'\n";

            var (output, exitCode) = await runner.ExecuteAsync(command, timeoutSeconds: 60);

            Assert.True(exitCode == 0, $"exit {exitCode}: {output}");
            Assert.Contains("alpha", output);
            Assert.Contains("beta", output);
        }

        [Fact]
        public void SingleLineCommandsStillSanitized()
        {
            // The legacy path (quoted-span newline rescue) must remain for single-line
            // commands whose ARGUMENTS carry stray newlines/tabs inside quotes.
            string sanitized = ShellRunner.SanitizeCommand("git commit -m \"first\nsecond\"");
            Assert.Equal("git commit -m \"first second\"", sanitized);
        }
    }
}
