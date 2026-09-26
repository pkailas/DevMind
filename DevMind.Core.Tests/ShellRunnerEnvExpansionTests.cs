// File: ShellRunnerEnvExpansionTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for watchlist H-27: run_shell expanded cmd-style %VAR% everywhere in
// the command, so %NAME% inside a single-quoted literal, here-string or comment was
// corrupted whenever NAME was a real environment variable. %VAR% is sugar for $env:VAR
// and now follows PowerShell interpolation rules via ShellRunner.TokenizeShellSpans (the
// same tokenizer as the && rewrite): expanded at statement level and inside "...", left
// alone inside '...', here-strings and comments.

using Xunit;

namespace DevMind.Core.Tests
{
    public class ShellRunnerEnvExpansionTests : IDisposable
    {
        private const string Value = @"C:\dm-h27";
        private readonly string _name = $"DEVMIND_H27_{Guid.NewGuid():N}";

        public ShellRunnerEnvExpansionTests()
        {
            Environment.SetEnvironmentVariable(_name, Value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, null);
        }

        private string Var => "%" + _name + "%";

        [Fact]
        public void StatementLevelVarStillExpands()
        {
            Assert.Equal($@"dotnet build -p:OutDir={Value}\out",
                ShellRunner.ExpandCmdStyleEnvironmentVariables($@"dotnet build -p:OutDir={Var}\out"));
        }

        [Fact]
        public void UnknownVarIsLeftAlone()
        {
            string command = "echo %DEVMIND_H27_NO_SUCH_VARIABLE%";
            Assert.Equal(command, ShellRunner.ExpandCmdStyleEnvironmentVariables(command));
        }

        [Fact]
        public void VarInsideDoubleQuotedStringExpands()
        {
            Assert.Equal($"Write-Output \"{Value}\\x\"",
                ShellRunner.ExpandCmdStyleEnvironmentVariables($"Write-Output \"{Var}\\x\""));
        }

        [Fact]
        public void VarInsideSingleQuotedStringIsUntouched()
        {
            string command = $"Write-Output '{Var}'";
            Assert.Equal(command, ShellRunner.ExpandCmdStyleEnvironmentVariables(command));
        }

        [Theory]
        [InlineData("@\"")]
        [InlineData("@'")]
        public void VarInsideHereStringIsUntouched(string opener)
        {
            string command =
                "$src = " + opener + "\n" +
                $"string s = string.Format(\"{Var}\", x);\n" +
                opener[1] + "@\n" +
                "Write-Output $src\n";
            Assert.Equal(command, ShellRunner.ExpandCmdStyleEnvironmentVariables(command));
        }

        [Fact]
        public void VarInsideCommentsIsUntouched()
        {
            string command = $"<# {Var} #> Write-Output x # uses {Var}";
            Assert.Equal(command, ShellRunner.ExpandCmdStyleEnvironmentVariables(command));
        }

        [Fact]
        public void MixedLineExpandsCodeAndDoubleQuotesOnly()
        {
            string command = $"Copy-Item {Var}\\a.txt \"{Var}\\b.txt\" && Write-Output '{Var}' # {Var}";

            string translated = ShellRunner.ExpandCmdStyleEnvironmentVariables(
                ShellRunner.TranslateChainOperators(command));

            Assert.Equal($"Copy-Item {Value}\\a.txt \"{Value}\\b.txt\"; Write-Output '{Var}' # {Var}", translated);
        }
    }
}
