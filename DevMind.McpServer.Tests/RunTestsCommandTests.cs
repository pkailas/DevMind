// File: RunTestsCommandTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-11 / H-08 at the MCP run_tests tool: the argv it hands to `dotnet` builds before testing
// (no --no-build — that made run_tests test whatever was built last) and carries the
// blame-hang guard. The project is still resolved under the working directory.

using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class RunTestsCommandTests : IDisposable
    {
        private readonly string _dir;
        private readonly McpServices _svc;
        private readonly DevMindTools _tools;

        public RunTestsCommandTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcp_rt_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(_dir, "tests"));
            File.WriteAllText(Path.Combine(_dir, "tests", "Foo.Tests.csproj"), "<Project />");
            _svc = new McpServices(_dir);
            _tools = new DevMindTools(_svc);
        }

        public void Dispose()
        {
            _svc.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void RunTestsArgv_BuildsFirst_AndCarriesBlameHang()
        {
            List<string> args = _tools.BuildTestArgs("Foo.Tests.csproj", "FullyQualifiedName~Bar");

            Assert.DoesNotContain("--no-build", args);
            Assert.Equal(
                new[]
                {
                    "test", Path.Combine(_dir, "tests", "Foo.Tests.csproj"), "--verbosity", "normal",
                    "--blame-hang", "--blame-hang-timeout", "45s", "--blame-hang-dump-type", "none",
                    "--filter", "FullyQualifiedName~Bar",
                },
                args);
        }

        [Fact]
        public void RunTestsArgv_WithoutAProject()
        {
            List<string> args = _tools.BuildTestArgs(null, null);

            Assert.DoesNotContain("--no-build", args);
            Assert.Equal(
                new[] { "test", "--verbosity", "normal", "--blame-hang", "--blame-hang-timeout", "45s", "--blame-hang-dump-type", "none" },
                args);
        }
    }
}
