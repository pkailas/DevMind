// File: NewFileLineEndingToolTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-30: the MCP tools' NEW-file branches (create_file, write_file on a missing path,
// append_file on a missing path) write the line ending the folder uses, not the model's LF
// (job-1714/1715 created LF files in a CRLF repo). Each case runs in a temp folder seeded with
// one existing file of the given ending; the temp root holds an empty .git folder so no
// .gitattributes above it is read.

using System.Text;
using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class NewFileLineEndingToolTests : IDisposable
    {
        private readonly string _dir;
        private readonly McpServices _svc;
        private readonly DevMindTools _tools;

        public NewFileLineEndingToolTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcp_eol_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(_dir, ".git"));
            _svc = new McpServices(_dir);
            _tools = new DevMindTools(_svc);
        }

        public void Dispose()
        {
            _svc.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private void SeedFolder(string ending) =>
            File.WriteAllBytes(Path.Combine(_dir, "Existing.cs"),
                new UTF8Encoding(false).GetBytes("class E" + ending + "{" + ending + "}" + ending));

        private static string Expected(string ending) => "class N" + ending + "{" + ending + "}" + ending;

        private string OnDisk(string name) => new UTF8Encoding(false).GetString(File.ReadAllBytes(Path.Combine(_dir, name)));

        [Theory]
        [InlineData("\r\n")]
        [InlineData("\n")]
        public async Task CreateFile_NewFile_TakesTheFolderEnding(string ending)
        {
            SeedFolder(ending);
            string r = await _tools.CreateFile(Path.Combine(_dir, "New.cs"), "class N\n{\r\n}\n");
            Assert.StartsWith("create_file: created", r);
            Assert.Equal(Expected(ending), OnDisk("New.cs"));
        }

        [Theory]
        [InlineData("\r\n")]
        [InlineData("\n")]
        public async Task WriteFile_NewFile_TakesTheFolderEnding(string ending)
        {
            SeedFolder(ending);
            await _tools.WriteFile(Path.Combine(_dir, "New.cs"), "class N\n{\n}\n");
            Assert.Equal(Expected(ending), OnDisk("New.cs"));
        }

        [Theory]
        [InlineData("\r\n")]
        [InlineData("\n")]
        public async Task AppendFile_NewFile_TakesTheFolderEnding(string ending)
        {
            SeedFolder(ending);
            string r = await _tools.AppendFile(Path.Combine(_dir, "New.cs"), "class N\n{\n}\n");
            Assert.StartsWith("append_file: created", r);
            Assert.Equal(Expected(ending), OnDisk("New.cs"));
        }

        [Fact]
        public async Task CreateFile_NewFile_StillHasNoBom()
        {
            SeedFolder("\r\n");
            await _tools.CreateFile(Path.Combine(_dir, "New.cs"), "class N\n{\n}\n");
            byte[] b = File.ReadAllBytes(Path.Combine(_dir, "New.cs"));
            Assert.False(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF, "a new file got a BOM");
        }
    }
}
