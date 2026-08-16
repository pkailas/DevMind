// File: ReadFileNotFoundFixTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tool-level regression test for the crash asymmetry found in probe A:
// the MCP not-found wrapper normalized the filename with an UNGUARDED
// Path.GetFileName, while the first-resolve helper (ResolveFilePath) tolerated
// the same input (IsNullOrWhiteSpace guard). A filename that the first resolve
// returned null for gracefully (e.g. a hallucinated drive root "C:\") then made
// the SECOND call throw, and ReadFile's catch turned what should have been a
// clean "file not found" into "[read_file error] C:\ : The filename or extension
// is too long." — an exception where the first call had succeeded.
//
// The fix guards the wrapper's normalization (shared 3-arg
// FilePathResolver.BuildFileNotFoundMessage) so both calls tolerate the input
// and the not-found path is reached. The tool's catch format makes the test
// fail without the change: "[read_file error] C:\ : The filename or extension
// is too long." does not start with the not-found prefix.

using DevMind;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public class ReadFileNotFoundFixTests : IDisposable
    {
        private readonly string _dir;
        private readonly McpServices _svc;
        private readonly DevMindTools _tools;

        public ReadFileNotFoundFixTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcp_nof_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _svc = new McpServices(_dir);
            _tools = new DevMindTools(_svc);
        }

        public void Dispose() => _svc.Dispose();

        [Fact]
        public async Task ReadFile_InvalidCharacterFilename_ReturnsCleanNotFound_NotError()
        {
            // Path.GetFileName("C:\") throws (invalid character). Pre-fix, the
            // wrapper threw and ReadFile's catch produced an "[read_file error]"
            // string; post-fix, both resolve calls tolerate the input and the
            // clean not-found path is reached.
            string result = await _tools.ReadFile("C:\\");

            Assert.StartsWith("read_file: file not found", result);
            Assert.DoesNotContain("[read_file error]", result);
            Assert.DoesNotContain("too long", result);
        }

        [Fact]
        public async Task ReadFile_EmptyFilename_ReturnsCleanNotFound_NotError()
        {
            // Empty filename — the first-resolve guard returns null; the wrapper
            // must also reach the not-found path rather than throw on the empty
            // normalization.
            string result = await _tools.ReadFile("");

            Assert.StartsWith("read_file: file not found", result);
            Assert.DoesNotContain("[read_file error]", result);
        }

        [Fact]
        public async Task ReadFile_PlainMissingFile_StillReturnsNotFound()
        {
            // Sanity: the ordinary not-found path is unchanged.
            string result = await _tools.ReadFile("Nope.cs");

            Assert.StartsWith("read_file: file not found", result);
            Assert.DoesNotContain("[read_file error]", result);
            Assert.Contains("absent", result);
        }
    }
}
