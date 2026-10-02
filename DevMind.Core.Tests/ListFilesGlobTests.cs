// File: ListFilesGlobTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-60: list_files "**/ConfigPages/*.cs" returned every .cs in the tree (job-2112: 200+ for a
// pattern with 24 matches). The directory part was treated as a literal path; "**/ConfigPages"
// never exists, so it fell back to the root with "*.cs" alone. "**" must match zero or more
// directories and the remaining segments must still filter. find_in_files shares the split.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class ListFilesGlobTests : IDisposable
    {
        private readonly string _dir;
        private readonly FileReadTools _tools;

        public ListFilesGlobTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h60_{Guid.NewGuid():N}");
            foreach (string rel in new[]
                     {
                         "a/y/X/one.cs", "a/X/two.cs", "a/X/two.txt", "a/X/deep/three.cs",
                         "b/X/four.cs", "top.cs", "a/other/five.cs", "bin/X/noise.cs",
                     })
            {
                string full = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "class C { }\n");
            }
            _tools = new FileReadTools(FileReadPolicy.Agent, _dir, new FileContentCache(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), new FileSnapshotStore(capacity: 8), shell: null);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private string[] List(string glob, bool recursive = true)
        {
            string text = _tools.List(glob, recursive, root: null, CancellationToken.None).Text;
            if (text == "[no matches]") return Array.Empty<string>();
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => Path.GetRelativePath(_dir, l.Trim()).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
        }

        [Fact]
        public void LeadingDoubleStar_MatchesOnlyFilesDirectlyUnderAnyXFolder()
        {
            Assert.Equal(new[] { "a/X/two.cs", "a/y/X/one.cs", "b/X/four.cs" }, List("**/X/*.cs"));
        }

        [Fact]
        public void DoubleStarUnderAPrefix_IsLimitedToThatPrefix()
        {
            Assert.Equal(new[] { "a/X/two.cs", "a/y/X/one.cs" }, List("a/**/X/*.cs"));
        }

        [Fact]
        public void DoubleStarSlashStarCs_MatchesEveryCsFile_NoiseExcluded()
        {
            Assert.Equal(
                new[] { "a/X/deep/three.cs", "a/X/two.cs", "a/other/five.cs", "a/y/X/one.cs", "b/X/four.cs", "top.cs" },
                List("**/*.cs"));
        }

        [Fact]
        public void SingleStarDirectorySegment_MatchesExactlyOneLevel()
        {
            Assert.Equal(new[] { "a/y/X/one.cs" }, List("a/*/X/*.cs"));
        }

        [Fact]
        public void BackslashesAndADotPrefix_AreNormalised()
        {
            Assert.Equal(new[] { "a/X/two.cs", "a/y/X/one.cs" }, List(@".\a\**\X\*.cs"));
        }

        [Fact]
        public void AMissingLiteralPrefix_MatchesNothing_NotTheWholeTree()
        {
            Assert.Empty(List("nope/**/X/*.cs"));
        }

        [Fact]
        public void DoubleStarIgnoresTheRecursiveFlag_ItSpellsOutItsOwnDepth()
        {
            Assert.Equal(new[] { "a/X/two.cs", "a/y/X/one.cs", "b/X/four.cs" }, List("**/X/*.cs", recursive: false));
        }

        [Fact]
        public void AnAbsoluteDoubleStarGlob_Works()
        {
            Assert.Equal(new[] { "a/X/two.cs", "a/y/X/one.cs" }, List(Path.Combine(_dir, "a") + "/**/X/*.cs"));
        }

        // ── Patterns without a wildcard directory keep their behaviour ──

        [Fact]
        public void FileNameOnlyPattern_IsUnchanged()
        {
            Assert.Equal(6, List("*.cs").Length);
            Assert.Equal(new[] { "top.cs" }, List("*.cs", recursive: false));
        }

        [Fact]
        public void LiteralDirectoryPattern_IsUnchanged_RecursiveUnderThatDirectory()
        {
            Assert.Equal(new[] { "a/X/deep/three.cs", "a/X/two.cs" }, List("a/X/*.cs"));
        }

        [Fact]
        public void FindInFiles_HonoursTheSameGlob()
        {
            string text = _tools.Find("class", "**/X/*.cs", root: null, startLine: null, endLine: null).Text;

            Assert.Contains("one.cs", text);
            Assert.Contains("two.cs", text);
            Assert.Contains("four.cs", text);
            Assert.DoesNotContain("three.cs", text);
            Assert.DoesNotContain("five.cs", text);
            Assert.DoesNotContain("top.cs", text);
        }
    }
}
