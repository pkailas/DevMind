// File: NewFileLineEndingTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-30: a NEW file takes the line ending its surroundings call for — .gitattributes eol=, then
// .sh → LF, then the dominant ending of neighbouring files (same extension, then any, then the
// parent folder), then CRLF on Windows — and the content is normalised fully. job-1714/1715
// created LF files in a CRLF repo.
//
// Every fixture root holds an empty ".git" folder, so the .gitattributes walk stops there and
// never reads anything above the temp directory.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class NewFileLineEndingTests : IDisposable
    {
        private const string CRLF = "\r\n";
        private const string LF = "\n";
        private readonly string _root;

        public NewFileLineEndingTests()
        {
            _root = Path.Combine(Path.GetTempPath(), $"devmind_eol_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(_root, ".git"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string Put(string rel, string text)
        {
            string path = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
            return path;
        }

        private string At(string rel) => Path.Combine(_root, rel);

        private static string Default => OperatingSystem.IsWindows() ? CRLF : LF;

        [Fact]
        public void FolderOfCrlfCs_Crlf()
        {
            Put("src/A.cs", "class A\r\n{\r\n}\r\n");
            Put("src/B.cs", "class B\r\n{\r\n}\r\n");
            Assert.Equal(CRLF, NewFileLineEnding.Choose(At("src/C.cs")));
        }

        [Fact]
        public void FolderOfLfCs_Lf()
        {
            Put("src/A.cs", "class A\n{\n}\n");
            Assert.Equal(LF, NewFileLineEnding.Choose(At("src/C.cs")));
        }

        [Fact]
        public void FolderOfLfSh_Lf()
        {
            Put("scripts/a.sh", "#!/bin/sh\necho a\n");
            Assert.Equal(LF, NewFileLineEnding.Choose(At("scripts/b.sh")));
        }

        [Fact]
        public void ShIsLf_EvenAmongCrlfNeighbours()
        {
            Put("scripts/a.cmd", "@echo off\r\necho a\r\n");
            Put("scripts/b.sh", "echo b\r\n");
            Assert.Equal(LF, NewFileLineEnding.Choose(At("scripts/c.sh")));
        }

        [Fact]
        public void EmptyFolder_PlatformDefault()
        {
            Directory.CreateDirectory(At("empty"));
            Assert.Equal(Default, NewFileLineEnding.Choose(At("empty/New.cs")));
        }

        [Fact]
        public void SameExtension_OutranksOtherFiles()
        {
            Put("mix/A.cs", "class A\n{\n}\n");
            Put("mix/a.md", "# a\r\ntext\r\n");
            Put("mix/b.md", "# b\r\ntext\r\n");
            Assert.Equal(LF, NewFileLineEnding.Choose(At("mix/C.cs")));
            Assert.Equal(CRLF, NewFileLineEnding.Choose(At("mix/notes.txt")));   // no .txt: any-file tier
        }

        [Fact]
        public void EmptySubfolder_UsesTheParent()
        {
            Put("lib/A.cs", "class A\n{\n}\n");
            Directory.CreateDirectory(At("lib/sub"));
            Assert.Equal(LF, NewFileLineEnding.Choose(At("lib/sub/B.cs")));
        }

        [Fact]
        public void BinaryAndSingleLineNeighbours_AreIgnored()
        {
            File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(At("bin")).FullName, "x.dat"),
                new byte[] { 0x0A, 0x00, 0x0A, 0x00 });
            Put("bin/one.cs", "class One {}");                               // no line break
            Assert.Equal(Default, NewFileLineEnding.Choose(At("bin/Two.cs")));
        }

        [Fact]
        public void GitAttributesEolLf_OutranksCrlfNeighbours()
        {
            Put(".gitattributes", "* text=auto\n*.cs text eol=lf\n");
            Put("src/A.cs", "class A\r\n{\r\n}\r\n");
            Assert.Equal(LF, NewFileLineEnding.Choose(At("src/B.cs")));
            // text=auto sets no eol: a .txt is not covered, so its CRLF neighbour decides.
            Assert.Equal(CRLF, NewFileLineEnding.Choose(At("src/readme.txt")));
        }

        [Fact]
        public void GitAttributes_DeeperFileAndLaterLineWin()
        {
            Put(".gitattributes", "* eol=lf\n");
            Put("win/.gitattributes", "*.ps1 eol=lf\n*.ps1 eol=crlf\n");
            Assert.Equal(CRLF, NewFileLineEnding.Choose(At("win/Install.ps1")));
            Assert.Equal(LF, NewFileLineEnding.Choose(At("win/other.txt")));
        }

        [Fact]
        public void GitAttributes_AnchoredPathPattern()
        {
            Put(".gitattributes", "/tools/*.cmd eol=crlf\ndocs/** eol=lf\n");
            Assert.Equal(CRLF, NewFileLineEnding.Choose(At("tools/run.cmd")));
            Assert.Equal(LF, NewFileLineEnding.Choose(At("docs/a/b/page.md")));
        }

        [Fact]
        public void GitAttributes_MinusText_WritesAsGiven()
        {
            Put(".gitattributes", "prompts/system-prompt.md -text\n");
            Assert.Null(NewFileLineEnding.Choose(At("prompts/system-prompt.md")));
            Assert.Equal("a\r\nb\nc", NewFileLineEnding.Apply(At("prompts/system-prompt.md"), "a\r\nb\nc"));
        }

        [Fact]
        public void GitAttributes_AboveTheRepoRoot_AreIgnored()
        {
            // The walk stops at the folder holding .git: an outer .gitattributes is another repo's.
            string outer = Path.Combine(Path.GetTempPath(), $"devmind_eol_outer_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(Path.Combine(outer, "repo", ".git"));
                File.WriteAllText(Path.Combine(outer, ".gitattributes"), "* eol=lf\n");
                Assert.Equal(Default, NewFileLineEnding.Choose(Path.Combine(outer, "repo", "New.cs")));
            }
            finally { try { Directory.Delete(outer, recursive: true); } catch { } }
        }

        [Theory]
        [InlineData("a\r\nb\nc\rd", "\r\n", "a\r\nb\r\nc\r\nd")]
        [InlineData("a\r\nb\nc\rd", "\n", "a\nb\nc\nd")]
        [InlineData("a\r\n\r\nb", "\n", "a\n\nb")]
        [InlineData("no breaks", "\r\n", "no breaks")]
        [InlineData("", "\r\n", "")]
        public void Normalize_MixedInput_Fully(string text, string newLine, string expected)
            => Assert.Equal(expected, NewFileLineEnding.Normalize(text, newLine));

        [Theory]
        [InlineData("*.cs", "src/A.cs", true)]
        [InlineData("*.cs", "A.csproj", false)]
        [InlineData("A.cs", "deep/er/A.cs", true)]
        [InlineData("/A.cs", "deep/A.cs", false)]
        [InlineData("src/*.cs", "src/A.cs", true)]
        [InlineData("src/*.cs", "src/x/A.cs", false)]
        [InlineData("src/**/*.cs", "src/x/y/A.cs", true)]
        [InlineData("**/bin/*", "a/bin/x.dll", true)]
        [InlineData("docs/", "docs", true)]
        public void PatternMatches(string pattern, string rel, bool expected)
            => Assert.Equal(expected, NewFileLineEnding.PatternMatches(pattern, rel));

        // ── The Buffered (headless) host's new-file paths ──

        [Theory]
        [InlineData("\r\n")]
        [InlineData("\n")]
        public async Task BufferedHost_SaveFile_NewFile_TakesTheFolderEnding(string folderEnding)
        {
            Put("a/Existing.cs", "class E" + folderEnding + "{" + folderEnding + "}" + folderEnding);
            IAgenticHost host = new BufferedAgenticHost(At("a"));

            Assert.NotNull(await host.SaveFileAsync("New.cs", "class N\n{\r\n}\n", fromToolCall: true));

            Assert.Equal("class N" + folderEnding + "{" + folderEnding + "}" + folderEnding, File.ReadAllText(At("a/New.cs")));
        }

        [Theory]
        [InlineData("\r\n")]
        [InlineData("\n")]
        public async Task BufferedHost_AppendFile_NewFile_TakesTheFolderEnding(string folderEnding)
        {
            Put("b/Existing.cs", "class E" + folderEnding + "{" + folderEnding + "}" + folderEnding);
            IAgenticHost host = new BufferedAgenticHost(At("b"));

            Assert.NotNull(await host.AppendFileAsync("New.cs", "class N\n{\n}\n"));

            Assert.Equal("class N" + folderEnding + "{" + folderEnding + "}" + folderEnding, File.ReadAllText(At("b/New.cs")));
        }
    }
}
