// File: ShellTimeoutPolicyTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The 300s shell budget exists for solution builds, but every command used to get it, so a
// hung non-build command burned five minutes. ResolveTimeout now gives the long budget
// (DEVMIND_SHELL_TIMEOUT, default 300) only to build/test/restore/install commands
// (IsLongRunningCommand) and everything else DEVMIND_SHELL_TIMEOUT_SHORT (default 60). A
// timed-out call tells the model how to ask for more.

using Xunit;

namespace DevMind.Core.Tests
{
    public class ShellTimeoutPolicyTests : IDisposable
    {
        private const string LongVar = "DEVMIND_SHELL_TIMEOUT";
        private const string ShortVar = "DEVMIND_SHELL_TIMEOUT_SHORT";

        private readonly string? _priorLong = Environment.GetEnvironmentVariable(LongVar);
        private readonly string? _priorShort = Environment.GetEnvironmentVariable(ShortVar);
        private readonly string _dir;

        public ShellTimeoutPolicyTests()
        {
            Environment.SetEnvironmentVariable(LongVar, null);
            Environment.SetEnvironmentVariable(ShortVar, null);
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_shtp_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(LongVar, _priorLong);
            Environment.SetEnvironmentVariable(ShortVar, _priorShort);
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Theory]
        [InlineData("dotnet build")]
        [InlineData("dotnet test DevMind.slnx --filter X")]
        [InlineData("DOTNET Restore")]
        [InlineData("dotnet publish -c Release")]
        [InlineData("dotnet pack")]
        [InlineData("dotnet run --project App")]
        [InlineData("dotnet ef database update")]
        [InlineData("dotnet.exe build x.sln")]
        [InlineData("cd C:\\x; dotnet build \"a.slnx\" -t:Rebuild")]
        [InlineData("cd C:\\x && dotnet test")]
        [InlineData("Set-Location src\ndotnet build")]
        [InlineData("& \"C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild\\Current\\Bin\\MSBuild.exe\" x.sln")]
        [InlineData("&\"C:\\VS\\MSBuild.exe\" x.sln /t:Rebuild")]
        [InlineData("msbuild x.csproj")]
        [InlineData("npm ci")]
        [InlineData("npm install")]
        [InlineData("npm run build")]
        [InlineData("pnpm test")]
        [InlineData("yarn build")]
        [InlineData("nuget restore x.sln")]
        [InlineData("vstest.console.exe tests.dll")]
        [InlineData("\"C:\\tools\\vstest.console.exe\" tests.dll")]
        [InlineData("dotnet build 2>&1 | Select-String error")]
        public void IsLongRunningCommand_Positives(string command)
            => Assert.True(ShellRunner.IsLongRunningCommand(command), command);

        [Theory]
        [InlineData("Select-String -Path *.cs -Pattern 'dotnet build'")]
        [InlineData("Get-ChildItem -Recurse")]
        [InlineData("git status")]
        [InlineData("while ($true) { Start-Sleep 1 }")]
        [InlineData("echo \"dotnet build\"")]
        [InlineData("Write-Output 'npm ci'")]
        [InlineData("dotnet --info")]
        [InlineData("dotnet")]
        [InlineData("npm view react version")]
        [InlineData("nuget list")]
        [InlineData("# dotnet build\nGet-Date")]
        [InlineData("")]
        [InlineData("   ")]
        public void IsLongRunningCommand_Negatives(string command)
            => Assert.False(ShellRunner.IsLongRunningCommand(command), command);

        [Fact]
        public void ResolveTimeout_Defaults_LongForBuilds_ShortOtherwise()
        {
            Assert.Equal(300, ShellRunner.ResolveTimeout("dotnet build"));
            Assert.Equal(60, ShellRunner.ResolveTimeout("git status"));
            Assert.Equal(60, ShellRunner.ResolveTimeout(null!));
        }

        [Fact]
        public void ResolveTimeout_ExplicitPositive_WinsOverBothEnvVars()
        {
            Environment.SetEnvironmentVariable(LongVar, "900");
            Environment.SetEnvironmentVariable(ShortVar, "15");
            Assert.Equal(42, ShellRunner.ResolveTimeout("dotnet build", 42));
            Assert.Equal(42, ShellRunner.ResolveTimeout("git status", 42));
        }

        [Fact]
        public void ResolveTimeout_EnvVars_ApplyToTheirOwnClassOnly()
        {
            Environment.SetEnvironmentVariable(LongVar, "900");
            Environment.SetEnvironmentVariable(ShortVar, "15");
            Assert.Equal(900, ShellRunner.ResolveTimeout("dotnet build"));
            Assert.Equal(15, ShellRunner.ResolveTimeout("git status"));
            // 0 / negative explicit means "use the default".
            Assert.Equal(900, ShellRunner.ResolveTimeout("dotnet build", 0));
            Assert.Equal(15, ShellRunner.ResolveTimeout("git status", -1));
            Assert.Equal(900, ShellRunner.ResolveLongTimeout());
        }

        [Fact]
        public void ResolveTimeout_InvalidEnvVars_FallBackToDefaults()
        {
            Environment.SetEnvironmentVariable(LongVar, "abc");
            Environment.SetEnvironmentVariable(ShortVar, "0");
            Assert.Equal(300, ShellRunner.ResolveTimeout("dotnet build"));
            Assert.Equal(60, ShellRunner.ResolveTimeout("git status"));
        }

        [Fact]
        public void RunTestsCommandLine_GetsTheLongBudget()
            => Assert.Equal(300, ShellRunner.ResolveTimeout(DotnetTestCommand.CommandLine("My.Tests.csproj", "X")));

        [Fact]
        public void RunBuildToolCall_CarriesTheLongBudget_EvenForAnUnrecognisedBuildScript()
        {
            var blocks = ToolCallMapper.Map(
                new List<ToolCallResult> { new ToolCallResult { Name = "run_build", Arguments = new Dictionary<string, string>() } },
                buildCommand: ".\\build.ps1");
            Assert.Equal(300, Assert.Single(blocks).ShellTimeoutSeconds);
        }

        [Fact]
        public async Task TimedOutCommand_OutputCarriesTheRetryHint()
        {
            string command = OperatingSystem.IsWindows() ? "Start-Sleep 5" : "sleep 5";
            var (output, exitCode) = await new ShellRunner(_dir).ExecuteAsync(command, timeoutSeconds: 1);

            Assert.Equal(-1, exitCode);
            Assert.Contains("[SHELL] Command timed out after 1 seconds.", output);
            Assert.Contains(ShellRunner.TimeoutHint, output);
        }

        [Fact]
        public async Task CompletedCommand_HasNoRetryHint()
        {
            string command = OperatingSystem.IsWindows() ? "Write-Output hi" : "echo hi";
            var (output, exitCode) = await new ShellRunner(_dir).ExecuteAsync(command, timeoutSeconds: 30);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain(ShellRunner.TimeoutHint, output);
        }
    }
}
