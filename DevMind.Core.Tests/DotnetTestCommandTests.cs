// File: DotnetTestCommandTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-11: run_tests must build before testing — the --no-build line (job-1672's transcript) made
// it test whatever was built last. H-08: every run carries the blame-hang guard.
// DotnetTestCommand is the one command line BufferedAgenticHost, TuiAgenticHost and the MCP
// run_tests tool all use.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class DotnetTestCommandTests
    {
        private const string BlameHang = "--blame-hang --blame-hang-timeout 45s --blame-hang-dump-type none";

        [Theory]
        [InlineData(@"C:\repo\tests\Foo.Tests.csproj", null)]
        [InlineData(@"C:\my repo\tests\Foo.Tests.csproj", "FullyQualifiedName~Bar")]
        [InlineData(null, null)]
        public void TheCommandLine_NeverSkipsTheBuild_AndCarriesBlameHang(string? project, string? filter)
        {
            string cmd = DotnetTestCommand.CommandLine(project!, filter!);

            Assert.DoesNotContain("--no-build", cmd);
            Assert.Contains(BlameHang, cmd);
            Assert.StartsWith("dotnet test", cmd);
        }

        [Fact]
        public void TheCommandLine_QuotesAPathWithSpaces_AndTheFilter()
        {
            string cmd = DotnetTestCommand.CommandLine(@"C:\my repo\Foo.Tests.csproj", "\"FullyQualifiedName~Bar\"");

            Assert.Equal(
                "dotnet test \"C:\\my repo\\Foo.Tests.csproj\" --verbosity normal " + BlameHang +
                " --filter \"FullyQualifiedName~Bar\"",
                cmd);
        }

        [Fact]
        public void TheCommandLine_WithoutProjectOrFilter()
        {
            Assert.Equal("dotnet test --verbosity normal " + BlameHang, DotnetTestCommand.CommandLine(null!, null!));
        }

        [Fact]
        public void TheArgv_NeverSkipsTheBuild_AndCarriesBlameHang()
        {
            var args = DotnetTestCommand.Arguments(@"C:\repo\Foo.Tests.csproj", "Category=Fast");

            Assert.DoesNotContain("--no-build", args);
            Assert.Equal(
                new[]
                {
                    "test", @"C:\repo\Foo.Tests.csproj", "--verbosity", "normal",
                    "--blame-hang", "--blame-hang-timeout", "45s", "--blame-hang-dump-type", "none",
                    "--filter", "Category=Fast",
                },
                args);
        }

        [Fact]
        public void BothHostsBuildTheirRunTestsCommandHere()
        {
            // The headless and TUI hosts used to each spell out their own --no-build line.
            // Pin that neither does any more, so a copy cannot reappear in one of them.
            string root = FindRepoRoot();
            foreach (string rel in new[] { @"DevMind.Core\BufferedAgenticHost.cs", @"DevMind.TUI\TuiAgenticHost.cs", @"DevMind.McpServer\DevMindTools.cs" })
            {
                string src = System.IO.File.ReadAllText(System.IO.Path.Combine(root, rel));
                Assert.DoesNotContain("--no-build", src);
                Assert.Contains("DotnetTestCommand.", src);
            }
        }

        private static string FindRepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "DevMind.slnx")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
