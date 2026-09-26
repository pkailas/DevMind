// File: EditPreservesFormatTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The MCP write_file / append_file / patch_file tools write an EXISTING file back with the BOM
// (or lack of one) and dominant line ending it had. They used to force UTF-8 without BOM on
// .ps1/.cmd/.bat/.sh (stripping the BOM Windows PowerShell 5.1 needs — job-1676) and UTF-8
// WITH a BOM on everything else (adding one to files that never had it). New files are UTF-8
// without BOM whatever the extension (H-22: a BOM'd commit-message file put U+FEFF in the subject).

using System.Text;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class EditPreservesFormatTests : IDisposable
    {
        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };
        private readonly string _dir;
        private readonly McpServices _svc;
        private readonly DevMindTools _tools;

        public EditPreservesFormatTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcp_fmt_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _svc = new McpServices(_dir);
            _tools = new DevMindTools(_svc);
        }

        public void Dispose()
        {
            _svc.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string WriteBytes(string name, string text, bool bom)
        {
            string path = Path.Combine(_dir, name);
            byte[] body = new UTF8Encoding(false).GetBytes(text);
            File.WriteAllBytes(path, bom ? Bom.Concat(body).ToArray() : body);
            return path;
        }

        private static bool HasBom(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            return b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
        }

        private static string Text(string path) => new UTF8Encoding(false).GetString(File.ReadAllBytes(path)).TrimStart('﻿');

        [Fact]
        public async Task WriteFile_OverBomCrlfPs1_KeepsBomAndCrlf()
        {
            string path = WriteBytes("Install.ps1", "# old\r\n", bom: true);

            string r = await _tools.WriteFile(path, "# new\nWrite-Host 'Grüße'\n");

            Assert.StartsWith("write_file: overwrote", r);
            Assert.True(HasBom(path), "write_file stripped the BOM from a .ps1");
            Assert.Equal("# new\r\nWrite-Host 'Grüße'\r\n", Text(path));
        }

        [Fact]
        public async Task WriteFile_OverNoBomCs_AddsNoBom()
        {
            string path = WriteBytes("A.cs", "class A {}\n", bom: false);

            await _tools.WriteFile(path, "class B {}\n");

            Assert.False(HasBom(path), "write_file added a BOM to a file that had none");
            Assert.Equal("class B {}\n", Text(path));
        }

        [Fact]
        public async Task WriteFile_OverBomCs_KeepsBom()
        {
            string path = WriteBytes("Legacy.cs", "class A {}\n", bom: true);

            await _tools.WriteFile(path, "class B {}\n");

            Assert.True(HasBom(path), "write_file stripped the BOM from a file that had one");
            Assert.Equal("class B {}\n", Text(path));
        }

        [Theory]
        [InlineData("COMMIT_MSG.txt")]
        [InlineData("New.cs")]
        [InlineData("new.ps1")]
        public async Task WriteFile_NewFile_HasNoBom(string name)
        {
            string path = Path.Combine(_dir, name);

            string r = await _tools.WriteFile(path, "fix: subject\n");

            Assert.StartsWith("write_file: created", r);
            Assert.False(HasBom(path), "write_file put a BOM on a new file");
        }

        [Fact]
        public async Task CreateFile_NewFile_HasNoBom()
        {
            // H-22 repro: a commit-message file written by the tool, then `git commit -F`,
            // gave a subject starting with U+FEFF.
            string path = Path.Combine(_dir, "COMMIT_MSG.txt");

            string r = await _tools.CreateFile(path, "fix: subject\n");

            Assert.StartsWith("create_file: created", r);
            Assert.False(HasBom(path), "create_file put a BOM on a new file");
            Assert.Equal("fix: subject\n", Text(path));
        }

        [Fact]
        public async Task AppendFile_NewFile_HasNoBom()
        {
            string path = Path.Combine(_dir, "notes.md");

            string r = await _tools.AppendFile(path, "# notes\n");

            Assert.StartsWith("append_file: created", r);
            Assert.False(HasBom(path), "append_file put a BOM on a new file");
        }

        [Fact]
        public async Task AppendFile_BomFile_NeverInsertsBomMidFile()
        {
            string path = WriteBytes("Log.cs", "// one\n", bom: true);

            await _tools.AppendFile(path, "// two\n");
            await _tools.AppendFile(path, "// three\n");

            byte[] b = File.ReadAllBytes(path);
            Assert.True(HasBom(path), "append_file stripped the leading BOM");
            Assert.Equal(-1, IndexOfBom(b, from: 3));
            Assert.Equal("// one\n// two\n// three\n", Text(path));
        }

        private static int IndexOfBom(byte[] b, int from)
        {
            for (int i = from; i + 2 < b.Length; i++)
                if (b[i] == 0xEF && b[i + 1] == 0xBB && b[i + 2] == 0xBF) return i;
            return -1;
        }

        [Fact]
        public async Task AppendFile_BomCrlfPs1_KeepsBom_AppendedTextIsCrlf()
        {
            string path = WriteBytes("Tasks.ps1", "$a = 1\r\n", bom: true);

            string r = await _tools.AppendFile(path, "$b = 2\n");

            Assert.StartsWith("append_file: appended", r);
            Assert.True(HasBom(path), "append_file stripped the BOM from a .ps1");
            Assert.Equal("$a = 1\r\n$b = 2\r\n", Text(path));
        }

        [Fact]
        public async Task AppendFile_NoBomCs_AddsNoBom()
        {
            string path = WriteBytes("B.cs", "// one\n", bom: false);

            await _tools.AppendFile(path, "// two\n");

            Assert.False(HasBom(path), "append_file added a BOM to a file that had none");
            Assert.Equal("// one\n// two\n", Text(path));
        }

        [Fact]
        public async Task PatchFile_BomCrlfPs1_KeepsBomAndCrlf()
        {
            string path = WriteBytes("Setup.ps1", "$x = 1\r\n$y = 2\r\n", bom: true);

            string r = await _tools.PatchFile(path, find: "$x = 1", replace: "$x = 10");

            Assert.StartsWith("patch_file: applied", r);
            Assert.True(HasBom(path), "patch_file stripped the BOM from a .ps1");
            Assert.Equal("$x = 10\r\n$y = 2\r\n", Text(path));
        }

        [Fact]
        public async Task PatchFile_NoBomFile_AddsNoBom()
        {
            string path = WriteBytes("C.cs", "int x = 1;\n", bom: false);

            await _tools.PatchFile(path, find: "int x = 1;", replace: "int x = 2;");

            Assert.False(HasBom(path));
            Assert.Equal("int x = 2;\n", Text(path));
        }
    }
}
