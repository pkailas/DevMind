// File: TextFileFormatTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// An edit writes an EXISTING file back the way it found it: same BOM (or none), same dominant
// line ending. job-1676's edits (and a driver-side patch 2026-09-24) stripped the UTF-8 BOM from
// installer .ps1 files that Windows PowerShell 5.1 needs. New files keep each tool's behaviour.
//
// The host tests drive the real BufferedAgenticHost (the headless jobs' host) through
// patch_file, create_file-overwrite and append_file, and assert on the BYTES on disk.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class TextFileFormatTests : IDisposable
    {
        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };
        private readonly string _dir;

        public TextFileFormatTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_fmt_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
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

        private static int BareLf(string s) => s.Split('\n').Length - 1 - (s.Split("\r\n").Length - 1);

        private async Task<IAgenticHost> HostWithRead(params string[] names)
        {
            var host = new BufferedAgenticHost(_dir) { RestrictWritesToWorkingDirectory = true };
            IAgenticHost agenticHost = host;
            foreach (string n in names)
                await agenticHost.LoadFileContentAsync(n, forceFullRead: true);
            return agenticHost;
        }

        private static async Task Patch(IAgenticHost host, string file, string find, string replace)
        {
            var (resolved, why) = await host.ResolvePatchAsync($"PATCH {file}\nFIND:\n{find}\nREPLACE:\n{replace}\nEND_PATCH", fromToolCall: true);
            Assert.True(resolved != null, why);
            var (patched, why2) = await host.ApplyResolvedPatchAsync(resolved!);
            Assert.True(patched != null, why2);
        }

        // ── Detection ────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("a\r\nb\r\nc\n", "\r\n")]
        [InlineData("a\nb\nc\r\n", "\n")]
        [InlineData("a\r\nb\n", "\n")]      // a tie is LF
        [InlineData("single line", null)]
        [InlineData("", null)]
        public void DominantNewLine(string content, string? expected)
        {
            Assert.Equal(expected, TextFileFormat.DominantNewLine(content));
        }

        [Fact]
        public void NormalizeLineEndings_ConvertsToTheFilesEnding()
        {
            string crlf = WriteBytes("c.txt", "x\r\ny\r\n", bom: false);
            Assert.Equal("1\r\n2\r\n3", TextFileFormat.Detect(crlf).NormalizeLineEndings("1\n2\r\n3"));

            string lf = WriteBytes("l.txt", "x\ny\n", bom: false);
            Assert.Equal("1\n2\n3", TextFileFormat.Detect(lf).NormalizeLineEndings("1\r\n2\n3"));
        }

        [Fact]
        public void WritePreserving_NewFile_IsWrittenAsGiven_WithoutBom()
        {
            string path = Path.Combine(_dir, "new.ps1");
            TextFileFormat.WritePreserving(path, "a\nb\n");

            Assert.False(HasBom(path));
            Assert.Equal("a\nb\n", Text(path));
        }

        // ── patch_file (the job-1676 path) ────────────────────────────────────────

        [Fact]
        public async Task Patch_BomPs1_KeepsBom()
        {
            string path = WriteBytes("Install.ps1", "Write-Host 'Grüße'\r\n$x = 1\r\n", bom: true);
            var host = await HostWithRead("Install.ps1");

            await Patch(host, "Install.ps1", "$x = 1", "$x = 2");

            Assert.True(HasBom(path), "patch_file stripped the BOM from a .ps1");
            Assert.Equal("Write-Host 'Grüße'\r\n$x = 2\r\n", Text(path));
        }

        [Fact]
        public async Task Patch_NoBomFile_GetsNoBom()
        {
            string path = WriteBytes("Plain.cs", "class A\n{\n    int x = 1;\n}\n", bom: false);
            var host = await HostWithRead("Plain.cs");

            await Patch(host, "Plain.cs", "int x = 1;", "int x = 2;");

            Assert.False(HasBom(path));
            Assert.Equal("class A\n{\n    int x = 2;\n}\n", Text(path));
        }

        [Fact]
        public async Task Patch_CrlfFile_StaysCrlf()
        {
            string path = WriteBytes("Crlf.cs", "class A\r\n{\r\n    int x = 1;\r\n}\r\n", bom: false);
            var host = await HostWithRead("Crlf.cs");

            await Patch(host, "Crlf.cs", "    int x = 1;", "    int x = 1;\n    int y = 2;");

            string text = Text(path);
            Assert.Equal("class A\r\n{\r\n    int x = 1;\r\n    int y = 2;\r\n}\r\n", text);
            Assert.Equal(0, BareLf(text));
        }

        // ── create_file over an existing file ──────────────────────────────────────

        [Fact]
        public async Task Overwrite_BomCrlfPs1_KeepsBomAndCrlf()
        {
            string path = WriteBytes("Setup.ps1", "# old\r\nWrite-Host 'old'\r\n", bom: true);
            var host = await HostWithRead("Setup.ps1");

            // The model sends LF content, as it almost always does.
            Assert.NotNull(await host.SaveFileAsync("Setup.ps1", "# new\nWrite-Host 'neu – ü'\n", fromToolCall: true));

            Assert.True(HasBom(path), "overwrite stripped the BOM");
            Assert.Equal("# new\r\nWrite-Host 'neu – ü'\r\n", Text(path));
        }

        [Fact]
        public async Task Overwrite_NoBomLfFile_GetsNoBom_StaysLf()
        {
            string path = WriteBytes("notes.md", "old\nlines\n", bom: false);
            var host = await HostWithRead("notes.md");

            Assert.NotNull(await host.SaveFileAsync("notes.md", "new\r\nlines\r\n", fromToolCall: true));

            Assert.False(HasBom(path));
            Assert.Equal("new\nlines\n", Text(path));
        }

        [Fact]
        public async Task CreateNewFile_Unchanged_NoBom_AsGiven()
        {
            var host = await HostWithRead();
            Assert.NotNull(await host.SaveFileAsync("brand-new.ps1", "a\r\nb\n", fromToolCall: true));

            string path = Path.Combine(_dir, "brand-new.ps1");
            Assert.False(HasBom(path));
            Assert.Equal("a\r\nb\n", Text(path));
        }

        // ── append_file ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Append_BomCrlfFile_KeepsBom_AppendedTextIsCrlf()
        {
            string path = WriteBytes("Tasks.ps1", "$a = 1\r\n$b = 2", bom: true);   // no trailing newline
            var host = await HostWithRead("Tasks.ps1");

            Assert.NotNull(await host.AppendFileAsync("Tasks.ps1", "$c = 3\n$d = 4\n"));

            Assert.True(HasBom(path), "append stripped the BOM");
            string text = Text(path);
            Assert.Equal("$a = 1\r\n$b = 2\r\n$c = 3\r\n$d = 4\r\n", text);
            Assert.Equal(0, BareLf(text));
        }

        [Fact]
        public async Task Append_NoBomFile_GetsNoBom()
        {
            string path = WriteBytes("log.txt", "one\n", bom: false);
            var host = await HostWithRead("log.txt");

            Assert.NotNull(await host.AppendFileAsync("log.txt", "two\n"));

            Assert.False(HasBom(path));
            Assert.Equal("one\ntwo\n", Text(path));
        }
    }
}
