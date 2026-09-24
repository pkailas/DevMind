// File: WriteEchoTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// What a write is allowed to hide.
//
// The transcript said "✓ Write numbers.py" for a file that had gone to an absolute path the
// model invented, three directories away from the folder the operator was watching. The line
// was true and useless: the same words appear when the write lands exactly where it should,
// and the only visible difference was a folder that stayed empty.
//
// The TUI does not refuse such a write — it is the trusting skin, and headless and MCP have
// the sandbox. Trusting is not the same as silent, so the line now carries the full path and
// the warning colour, which brief 12's vocabulary turns into ⚠.
//
// The fallback badge is the same category of problem: "[two-way fallback]" is an internal
// phrase for "there was no base cache entry, so this was overwrite detection only" and it
// was shown to the operator verbatim.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class WriteEchoTests
    {
        private const string Work = @"C:\work\repo";

        [Fact]
        public void AWriteInsideTheWorkingDirectoryIsTheShortNameAndASuccess()
        {
            var (detail, color) = WriteEcho.Describe(
                "numbers.py", @"C:\work\repo\numbers.py", Work, usedFallback: false, "(16 lines)");

            Assert.Equal("numbers.py (16 lines)", detail);
            Assert.Equal(OutputColor.Success, color);
        }

        [Fact]
        public void AWriteOutsideItCarriesTheFullPathAndTheWarning()
        {
            var (detail, color) = WriteEcho.Describe(
                "x.txt", @"C:\Windows\Temp\x.txt", Work, usedFallback: false, "(2 lines)");

            Assert.Equal(@"C:\Windows\Temp\x.txt (2 lines) " + WriteEcho.OutsideNote, detail);
            Assert.Equal(OutputColor.Warning, color);
        }

        [Fact]
        public void TheShortNameIsNotShownWhenItWouldMislead()
        {
            // The bare name is exactly what made a misplaced write look like a correct one.
            var (detail, _) = WriteEcho.Describe(
                "numbers.py", @"C:\Users\x\AppData\Local\Temp\devmind\session1\numbers.py",
                Work, usedFallback: false);

            Assert.StartsWith(@"C:\Users\x\AppData\Local\Temp\devmind\session1\numbers.py", detail);
        }

        [Fact]
        public void TheFallbackBadgeSaysWhatItMeans_NotWhatTheCodeCallsIt()
        {
            var (detail, color) = WriteEcho.Describe(
                "numbers.py", @"C:\work\repo\numbers.py", Work, usedFallback: true, "(16 lines)");

            Assert.Equal("numbers.py (16 lines) " + WriteEcho.FuzzyNote, detail);
            Assert.Equal(OutputColor.Warning, color);
            Assert.DoesNotContain("two-way", detail, StringComparison.Ordinal);
        }

        [Fact]
        public void BothProblemsAtOnceAreBothStated()
        {
            var (detail, color) = WriteEcho.Describe(
                "x.txt", @"C:\elsewhere\x.txt", Work, usedFallback: true);

            Assert.Contains(WriteEcho.OutsideNote, detail, StringComparison.Ordinal);
            Assert.Contains(WriteEcho.FuzzyNote, detail, StringComparison.Ordinal);
            Assert.Equal(OutputColor.Warning, color);
        }

        // ── Containment ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData(@"C:\work\repo\a.txt")]
        [InlineData(@"C:\work\repo\sub\deep\a.txt")]
        [InlineData(@"C:\work\repo")]
        [InlineData(@"C:\work\repo\")]
        public void PathsUnderTheRootAreInside(string path)
        {
            Assert.False(WriteEcho.IsOutside(path, Work));
        }

        [Fact]
        public void ASiblingWithASharedPrefixIsOutside()
        {
            // The separator is the whole test: without it "C:\workshop" counts as inside
            // "C:\work", which is the classic way a containment check silently passes.
            Assert.True(WriteEcho.IsOutside(@"C:\workshop\a.txt", @"C:\work"));
            Assert.True(WriteEcho.IsOutside(@"C:\work-other\a.txt", @"C:\work"));
        }

        [Fact]
        public void ATraversalOutOfTheRootIsOutside()
        {
            Assert.True(WriteEcho.IsOutside(@"C:\work\repo\..\..\elsewhere\a.txt", Work));
        }

        [Fact]
        public void CaseDoesNotMakeAPathForeignOnWindows()
        {
            Assert.False(WriteEcho.IsOutside(@"c:\WORK\Repo\a.txt", Work));
        }

        [Theory]
        [InlineData(null, @"C:\work")]
        [InlineData(@"C:\work\a.txt", null)]
        [InlineData("", "")]
        public void AnUnanswerableComparisonIsNotAFinding(string path, string root)
        {
            // This drives a label, not a gate. Crying "outside" over a path that could not be
            // compared would teach the operator to ignore the word.
            Assert.False(WriteEcho.IsOutside(path, root));
        }
    }
}
