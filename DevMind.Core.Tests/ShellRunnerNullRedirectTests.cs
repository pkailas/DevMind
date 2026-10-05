// File: ShellRunnerNullRedirectTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-73: job-2167's agent ran
//   try { [Reflection.Assembly]::LoadWithPartialName("Microsoft.Extensions.Configuration") > null; "ok" } catch { "no" }
// In PowerShell `> null` redirects to a FILE named "null" — with the Out-File utf8 default
// the empty output became a 3-byte BOM-only file in the repo root. A redirect to a bare
// null / nul is now rewritten to $null in code spans (same tokenizer as %VAR% and &&).

using Xunit;

namespace DevMind.Core.Tests
{
    public class ShellRunnerNullRedirectTests
    {
        [Theory]
        [InlineData("x > null", "x > $null")]
        [InlineData("x >null; y", "x > $null; y")]
        [InlineData("x 2> null", "x 2> $null")]
        [InlineData("x 2>nul", "x 2> $null")]
        [InlineData("x *> NULL", "x *> $null")]
        [InlineData("x >> null", "x >> $null")]
        [InlineData("try { [Reflection.Assembly]::LoadWithPartialName(\"A.B\") > null; \"ok\" } catch { \"no\" }",
                    "try { [Reflection.Assembly]::LoadWithPartialName(\"A.B\") > $null; \"ok\" } catch { \"no\" }")]
        public void BareNullTargetBecomesDollarNull(string command, string expected)
            => Assert.Equal(expected, ShellRunner.RewriteNullRedirects(command));

        [Theory]
        [InlineData("x > $null")]
        [InlineData("x 2>$null")]
        [InlineData("x > null.txt")]
        [InlineData("x > nullable")]
        [InlineData("x > nul\\out.txt")]
        [InlineData("x > null-out")]
        [InlineData("x > 'null'")]
        [InlineData("x 2>&1")]
        [InlineData("Write-Output 'a > null'")]
        [InlineData("Write-Output \"a > null\"")]
        [InlineData("# x > null")]
        [InlineData("$v = $null")]
        public void EverythingElseIsUntouched(string command)
            => Assert.Equal(command, ShellRunner.RewriteNullRedirects(command));

        [Fact]
        public async Task RealRun_LeavesNoNullOrNulFileInTheWorkingDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"devmind_h73_null_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var runner = new ShellRunner(dir);
                var (output, _) = await runner.ExecuteAsync(
                    "[Reflection.Assembly]::LoadWithPartialName(\"No.Such.Assembly\") > null; 'quiet' 2>nul; 'x' >> null; \"done\"",
                    CancellationToken.None, 60);

                Assert.Contains("done", output);
                Assert.DoesNotContain("device", output);
                Assert.Empty(Directory.GetFileSystemEntries(dir));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
