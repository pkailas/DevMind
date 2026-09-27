// File: VerificationBuildTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-32 / H-13: the harness verifies a plain `dotnet build` as a full rebuild so its warning
// count is real, and leaves everything else (an override, another build system, a custom
// target, DEVMIND_VERIFY_REBUILD=0) as resolved and unverified.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class VerificationBuildTests : IDisposable
    {
        private readonly string? _priorBuildCommand = Environment.GetEnvironmentVariable("DEVMIND_BUILD_COMMAND");
        private readonly string? _priorRebuild = Environment.GetEnvironmentVariable(VerificationBuild.RebuildEnvVar);

        public VerificationBuildTests()
        {
            Environment.SetEnvironmentVariable("DEVMIND_BUILD_COMMAND", null);
            Environment.SetEnvironmentVariable(VerificationBuild.RebuildEnvVar, null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_BUILD_COMMAND", _priorBuildCommand);
            Environment.SetEnvironmentVariable(VerificationBuild.RebuildEnvVar, _priorRebuild);
        }

        [Theory]
        // plain dotnet build (solution or project) → rebuild
        [InlineData("dotnet build \"C:\\r\\DevMind.slnx\"", "dotnet build \"C:\\r\\DevMind.slnx\" -t:Rebuild", true)]
        [InlineData("dotnet build x.csproj -c Release", "dotnet build x.csproj -c Release -t:Rebuild", true)]
        [InlineData("dotnet build \"C:\\r\\x.sln\" /p:DeployExtension=false", "dotnet build \"C:\\r\\x.sln\" /p:DeployExtension=false -t:Rebuild", true)]
        [InlineData("DOTNET.EXE Build x.slnx", "DOTNET.EXE Build x.slnx -t:Rebuild", true)]
        // already a full rebuild → unchanged, verified
        [InlineData("dotnet build x.slnx -t:Rebuild", "dotnet build x.slnx -t:Rebuild", true)]
        [InlineData("dotnet build x.slnx /t:rebuild", "dotnet build x.slnx /t:rebuild", true)]
        [InlineData("dotnet build x.slnx -target:Clean;Rebuild", "dotnet build x.slnx -target:Clean;Rebuild", true)]
        [InlineData("dotnet build x.slnx --no-incremental", "dotnet build x.slnx --no-incremental", true)]
        [InlineData("dotnet build x.slnx -p:DefineConstants=A;B", "dotnet build x.slnx -p:DefineConstants=A;B -t:Rebuild", true)]
        [InlineData("dotnet build x.slnx; dotnet test", "dotnet build x.slnx; dotnet test", false)]
        // another target of its own → unchanged, unverified
        [InlineData("dotnet build x.slnx -t:Pack", "dotnet build x.slnx -t:Pack", false)]
        // not a single dotnet build → unchanged, unverified
        [InlineData("npm run build", "npm run build", false)]
        [InlineData("& \"C:\\VS\\MSBuild.exe\" \"x.sln\" /p:DeployExtension=false /verbosity:minimal", "& \"C:\\VS\\MSBuild.exe\" \"x.sln\" /p:DeployExtension=false /verbosity:minimal", false)]
        [InlineData("dotnet build x.slnx && dotnet test", "dotnet build x.slnx && dotnet test", false)]
        [InlineData("dotnet test x.slnx", "dotnet test x.slnx", false)]
        [InlineData("dotnet build x.slnx -- --extra", "dotnet build x.slnx -- --extra", false)]
        public void Rewrite(string resolved, string expectedCommand, bool expectFullRebuild)
        {
            var plan = VerificationBuild.For(resolved, fromOverride: false, rebuildEnabled: true);
            Assert.Equal(expectedCommand, plan.Command);
            Assert.Equal(expectFullRebuild, plan.FullRebuild);
        }

        [Fact]
        public void BuildCommandOverride_RunsUnchanged_Unverified()
        {
            // DEVMIND_BUILD_COMMAND may be a script — or a dotnet build the user chose on purpose.
            Environment.SetEnvironmentVariable("DEVMIND_BUILD_COMMAND", ".\\build.ps1");
            var plan = VerificationBuild.For(".\\build.ps1");
            Assert.Equal(".\\build.ps1", plan.Command);
            Assert.False(plan.FullRebuild);

            Environment.SetEnvironmentVariable("DEVMIND_BUILD_COMMAND", "dotnet build x.slnx");
            plan = VerificationBuild.For("dotnet build x.slnx");
            Assert.Equal("dotnet build x.slnx", plan.Command);
            Assert.False(plan.FullRebuild);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("false")]
        [InlineData("OFF")]
        public void EnvVarOff_Incremental_Unverified(string value)
        {
            Environment.SetEnvironmentVariable(VerificationBuild.RebuildEnvVar, value);
            var plan = VerificationBuild.For("dotnet build x.slnx");
            Assert.Equal("dotnet build x.slnx", plan.Command);
            Assert.False(plan.FullRebuild);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("1")]
        [InlineData("true")]
        public void EnvVarUnsetOrOn_Rebuild(string? value)
        {
            Environment.SetEnvironmentVariable(VerificationBuild.RebuildEnvVar, value);
            var plan = VerificationBuild.For("dotnet build x.slnx");
            Assert.Equal("dotnet build x.slnx -t:Rebuild", plan.Command);
            Assert.True(plan.FullRebuild);
        }

        // Real `dotnet build W.slnx -t:Rebuild` output (redirected, .NET 10 SDK) from a scratch
        // solution with one CS0168 — verbatim except the scratch path, shortened to C:\src\warnsln.
        // The same solution's second INCREMENTAL build printed "0 Warning(s)" (the H-13 defect).
        private const string RealRebuildWithOneWarning =
            "  Determining projects to restore...\r\n" +
            "  All projects are up-to-date for restore.\r\n" +
            "C:\\src\\warnsln\\Warn\\Program.cs(1,5): warning CS0168: The variable 'x' is declared but never used [C:\\src\\warnsln\\Warn\\Warn.csproj]\r\n" +
            "  Warn -> C:\\src\\warnsln\\Warn\\bin\\Debug\\net10.0\\Warn.dll\r\n" +
            "\r\n" +
            "Build succeeded.\r\n" +
            "\r\n" +
            "C:\\src\\warnsln\\Warn\\Program.cs(1,5): warning CS0168: The variable 'x' is declared but never used [C:\\src\\warnsln\\Warn\\Warn.csproj]\r\n" +
            "    1 Warning(s)\r\n" +
            "    0 Error(s)\r\n" +
            "\r\n" +
            "Time Elapsed 00:00:00.69\r\n";

        // Tail of the real `dotnet build DevMind.slnx -t:Rebuild` run on 2026-09-27.
        private const string RealDevMindRebuildTail =
            "  DevMind.TUI.Tests -> C:\\Users\\pkailas\\source\\repos\\DevMind\\DevMind.TUI.Tests\\bin\\Debug\\net10.0\\DevMind.TUI.Tests.dll\n" +
            "\n" +
            "Build succeeded.\n" +
            "    0 Warning(s)\n" +
            "    0 Error(s)\n" +
            "\n" +
            "Time Elapsed 00:00:07.95\n";

        [Fact]
        public void ParseWarningCount_RealRebuildOutput()
        {
            Assert.Equal(1, VerificationBuild.ParseWarningCount(RealRebuildWithOneWarning));
            Assert.Equal(0, VerificationBuild.ParseWarningCount(RealDevMindRebuildTail));
        }

        [Fact]
        public void ExtractWarningLines_RealOutput_DedupedAndProjectSuffixStripped()
        {
            // MSBuild prints the CS0168 twice (inline and in the summary): quoted once.
            var lines = VerificationBuild.ExtractWarningLines(RealRebuildWithOneWarning);
            Assert.Equal(new[] { "C:\\src\\warnsln\\Warn\\Program.cs(1,5): warning CS0168: The variable 'x' is declared but never used" }, lines);
        }

        [Fact]
        public void ExtractWarningLines_CappedInFirstSeenOrder_IgnoresSummaryLines()
        {
            string output = string.Join("\n", Enumerable.Range(1, 8).Select(i =>
                    $"C:\\s\\F{i}.cs({i},1): warning CS0{100 + i}: w{i} [C:\\s\\P.csproj]"))
                + "\nC:\\s\\P.csproj : warning NU1903: Package 'X' has a known vulnerability [C:\\s\\P.csproj]"
                + "\n    9 Warning(s)\n    0 Error(s)\nBuild succeeded with 9 warning(s) in 2s\n";

            var lines = VerificationBuild.ExtractWarningLines(output, max: 5);

            Assert.Equal(5, lines.Count);
            Assert.Equal("C:\\s\\F1.cs(1,1): warning CS0101: w1", lines[0]);
            Assert.Equal("C:\\s\\F5.cs(5,1): warning CS0105: w5", lines[4]);
            Assert.DoesNotContain(lines, l => l.Contains("Warning(s)") || l.Contains("warning(s)"));
        }

        [Fact]
        public void ExtractWarningLines_NoWarnings_Empty()
            => Assert.Empty(VerificationBuild.ExtractWarningLines(RealDevMindRebuildTail));

        [Theory]
        [InlineData("Build succeeded with 3 warning(s) in 4.2s", 3)]
        [InlineData("Build failed with 2 error(s) and 5 warning(s) in 1.1s", 5)]
        [InlineData("    12 Warning(s)\n    1 Error(s)\nBuild FAILED.", 12)]
        [InlineData("no summary here", null)]
        [InlineData("", null)]
        public void ParseWarningCount_Forms(string output, int? expected)
            => Assert.Equal(expected, VerificationBuild.ParseWarningCount(output));
    }
}
