// File: DiffRendererModelTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The line model a painted diff is drawn from.
//
// The TUI used to derive all of this by looking at each rendered line's first character,
// which cannot recover what the diff engine already knew: which line of which file this is.
// A gutter can only be right if the numbers are right, and the place they go wrong is after
// an insertion — from there on every new-file number runs one ahead of its old-file number,
// and a model that resynchronises them looks perfectly plausible until you try to open the
// file at the number it printed.
//
// So the fixture puts its insertion in the SECOND hunk, and the assertions below carry both
// numbers for every line.

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class DiffRendererModelTests
    {
        private static string[] Project(IReadOnlyList<DiffLine> lines)
            => lines.Select(l => $"{l.Kind}|{l.OldNo?.ToString() ?? "-"}|{l.NewNo?.ToString() ?? "-"}|{l.Text}")
                    .ToArray();

        [Fact]
        public void TwoHunks_WithTheirOldAndNewLineNumbers()
        {
            var lines = DiffRenderer.Build(DiffFixture.Old, DiffFixture.New);

            Assert.Equal(new[]
            {
                @"HunkHeader|4|4|@@ -4,7 +4,7 @@",
                @"Context|4|4|{",
                @"Context|5|5|    public class Widget",
                @"Context|6|6|    {",
                @"Removed|7|-|        public int Count;",
                @"Added|-|7|        public int Count = 1;",
                @"Context|8|8|",
                @"Context|9|9|        public void Reset()",
                @"Context|10|10|        {",
                @"HunkHeader|13|13|@@ -13,6 +13,7 @@",
                @"Context|13|13|",
                @"Context|14|14|        public void Bump()",
                @"Context|15|15|        {",
                @"Added|-|16|            // keep it positive",
                @"Context|16|17|            Count++;",
                @"Context|17|18|        }",
                @"Context|18|19|    }",
            }, Project(lines));
        }

        [Fact]
        public void AfterAnInsertion_TheNewNumbersRunAheadOfTheOldOnes()
        {
            // The property the literal list above encodes, stated on its own so a failure
            // says WHICH invariant broke rather than only that the list differs.
            var lines = DiffRenderer.Build(DiffFixture.Old, DiffFixture.New);

            DiffLine afterInsert = lines.Last(l => l.Kind == DiffLineKind.Context);
            Assert.Equal(18, afterInsert.OldNo);
            Assert.Equal(19, afterInsert.NewNo);
        }

        [Fact]
        public void ARemovedLineIsNumberedInTheOldFileOnly_AndAnAddedLineInTheNewFileOnly()
        {
            var lines = DiffRenderer.Build(DiffFixture.Old, DiffFixture.New);

            DiffLine removed = lines.First(l => l.Kind == DiffLineKind.Removed);
            Assert.Equal(7, removed.OldNo);
            Assert.Null(removed.NewNo);

            DiffLine added = lines.First(l => l.Kind == DiffLineKind.Added);
            Assert.Null(added.OldNo);
            Assert.Equal(7, added.NewNo);
        }

        [Fact]
        public void TheTextIsTheSourceLine_WithNoDiffMarker()
        {
            // The marker is the renderer's business. A model that carried "+" would force
            // every consumer to strip it back off — which is the re-parsing this replaced.
            var lines = DiffRenderer.Build(DiffFixture.Old, DiffFixture.New);

            Assert.Equal("        public int Count = 1;", lines.First(l => l.Kind == DiffLineKind.Added).Text);
            Assert.Equal("        public int Count;", lines.First(l => l.Kind == DiffLineKind.Removed).Text);
        }

        [Fact]
        public void IdenticalTexts_ProduceNothing()
        {
            Assert.Empty(DiffRenderer.Build(DiffFixture.Old, DiffFixture.Old));
            Assert.Empty(DiffRenderer.Build("", ""));
            Assert.Empty(DiffRenderer.Build(null, null));
        }

        [Fact]
        public void LineEndingsDoNotMakeADifference()
        {
            // A file read as CRLF and a patch result built as LF are not a change, and
            // reporting one would send the reader looking for an edit nobody made.
            string crlf = DiffFixture.Old.Replace("\n", "\r\n");

            Assert.Empty(DiffRenderer.Build(crlf, DiffFixture.Old));
        }

        [Fact]
        public void OnlyChangedLinesAndTheirContextSurvive()
        {
            // The fixture is 19 lines; a whole-file model would return all of them. Hunks are
            // what keeps a one-line edit to a long file from being a long diff.
            var lines = DiffRenderer.Build(DiffFixture.Old, DiffFixture.New);

            Assert.DoesNotContain(lines, l => l.Text == "using System;");
            Assert.DoesNotContain(lines, l => l.Text == "            Count = 0;");
        }

        [Fact]
        public void TheContextRadiusIsThreeLinesEitherSide()
        {
            var lines = DiffRenderer.Build(DiffFixture.Old, DiffFixture.New);

            // Three context lines lead the first hunk's change and three follow it.
            var firstHunk = lines.SkipWhile(l => l.Kind != DiffLineKind.HunkHeader).Skip(1)
                                 .TakeWhile(l => l.Kind != DiffLineKind.HunkHeader).ToArray();

            Assert.Equal(3, firstHunk.TakeWhile(l => l.Kind == DiffLineKind.Context).Count());
            Assert.Equal(3, firstHunk.Reverse().TakeWhile(l => l.Kind == DiffLineKind.Context).Count());
        }
    }
}
