// File: ShellRunnerChainOperatorTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for watchlist H-21 (job-1697): the PowerShell `&&` -> `;` rewrite
// was a blind string Replace, so C# `wpid == pid && IsWindowVisible(h)` inside an
// Add-Type here-string reached PowerShell as `wpid == pid; IsWindowVisible(h)`.
// Statement-level `&&` must still be rewritten — Windows PowerShell 5.1 rejects it.

using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ShellRunnerChainOperatorTests : IDisposable
    {
        private readonly string _dir;

        public ShellRunnerChainOperatorTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_shchain_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void StatementLevelAndAndBecomesSemicolon()
        {
            Assert.Equal("cd src; dotnet build",
                ShellRunner.TranslateChainOperators("cd src && dotnet build"));
        }

        [Fact]
        public void AndAndInsideDoubleQuotedStringIsUntouched()
        {
            string command = "Write-Output \"a && b\" && Write-Output \"c `\" && d\"";
            Assert.Equal("Write-Output \"a && b\"; Write-Output \"c `\" && d\"",
                ShellRunner.TranslateChainOperators(command));
        }

        [Fact]
        public void AndAndInsideSingleQuotedStringIsUntouched()
        {
            Assert.Equal("git commit -m 'x && y'; git log -1",
                ShellRunner.TranslateChainOperators("git commit -m 'x && y' && git log -1"));
        }

        [Theory]
        [InlineData("@\"")]
        [InlineData("@'")]
        public void AndAndInsideHereStringIsUntouched(string opener)
        {
            char quote = opener[1];
            string command =
                "$src = " + opener + "\n" +
                "if (wpid == pid && IsWindowVisible(h)) { return; }\n" +
                quote + "@\n" +
                "Write-Output one && Write-Output two\n";

            string translated = ShellRunner.TranslateChainOperators(command);

            Assert.Contains("if (wpid == pid && IsWindowVisible(h))", translated);
            Assert.Contains("Write-Output one; Write-Output two", translated);
        }

        [Fact]
        public void AndAndInsideCommentsIsUntouched()
        {
            string command = "<# a && b #> Write-Output x && Write-Output y # c && d";
            Assert.Equal("<# a && b #> Write-Output x; Write-Output y # c && d",
                ShellRunner.TranslateChainOperators(command));
        }

        [Fact]
        public async Task AddTypeHereStringWithAndAndCompiles()
        {
            if (!OperatingSystem.IsWindows() || !ShellRunner.IsPowerShellAvailable())
                return; // requires Windows PowerShell

            // The job-1697 shape end to end: C# with && in an Add-Type here-string,
            // followed by a statement-level && that PowerShell 5.1 cannot parse raw.
            string command =
                "Add-Type -TypeDefinition @\"\n" +
                "public static class ChainProbe {\n" +
                "    public static bool Both(int a, int b) { return a == b && a > 0; }\n" +
                "}\n" +
                "\"@\n" +
                "Write-Output \"both=$([ChainProbe]::Both(2, 2))\" && Write-Output \"second && ran\"\n";

            var runner = new ShellRunner(_dir);
            var (output, exitCode) = await runner.ExecuteAsync(command, timeoutSeconds: 60);

            Assert.True(exitCode == 0, $"exit {exitCode}: {output}");
            Assert.Contains("both=True", output);
            Assert.Contains("second && ran", output);
        }
    }
}
