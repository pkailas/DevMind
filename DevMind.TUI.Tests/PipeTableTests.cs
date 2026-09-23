// File: PipeTableTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Columns, and the width they have to fit into.
//
// The width is not a detail here, it is the feature. The output Editor word-wraps, so a
// laid-out row one character too wide is re-broken by the Editor at whatever column it likes
// and the alignment dissolves — leaving something that looks like a table that went wrong
// rather than like the pipe source it replaced. Every layout test therefore asserts the
// rendered width, not just the content.
//
// The other thing worth guarding is that nothing is silently lost. A comparison table exists
// so values can be compared; a cell quietly truncated to fit is a table that misleads, which
// is worse than one that is ugly. So a long token hard-breaks, and the tests say so.

using System.Linq;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class PipeTableTests
    {
        private static readonly string[] Fixture =
        {
            "| Option | Cost | Risk |",
            "|---|---|---|",
            "| Add a remote | low | none |",
            "| Leave local-only | none | history lives on one disk |",
        };

        private static PipeTableModel Parse(params string[] lines) => PipeTable.TryParse(lines);

        private static System.Collections.Generic.IReadOnlyList<TableRowLine> Lay(
            PipeTableModel table, int width)
            => PipeTable.Layout(table, width, MarkdownInlineRenderer.Render, TableGlyphs.Unicode);

        // ── Parsing ──────────────────────────────────────────────────────────────

        [Fact]
        public void TheFixtureParsesAsThreeColumnsAndTwoRows()
        {
            PipeTableModel table = Parse(Fixture);

            Assert.NotNull(table);
            Assert.Equal(3, table.ColumnCount);
            Assert.Equal(new[] { "Option", "Cost", "Risk" }, table.Header);
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal(new[] { "Add a remote", "low", "none" }, table.Rows[0]);
            Assert.Equal("history lives on one disk", table.Rows[1][2]);
        }

        [Fact]
        public void AlignmentColonsAreHonoured()
        {
            PipeTableModel table = Parse(
                "| L | C | R |",
                "|:---|:---:|---:|",
                "| a | b | c |");

            Assert.Equal(new[] { TableAlign.Left, TableAlign.Center, TableAlign.Right }, table.Aligns);
        }

        [Fact]
        public void APipeLineWithNoSeparatorIsNotATable()
        {
            // Prose containing pipes is prose. Swallowing it would delete what the model wrote.
            Assert.Null(Parse("| this is just a sentence | with a pipe in it"));
            Assert.Null(Parse("| a | b |", "not a separator", "| c | d |"));
            Assert.Null(Parse("| a | b |"));
        }

        [Fact]
        public void AnEscapedPipeStaysInTheCell()
        {
            string[] cells = PipeTable.SplitCells(@"| a \| b | c |");

            Assert.Equal(new[] { "a | b", "c" }, cells);
        }

        [Fact]
        public void APipeInsideBackticksStaysInTheCell()
        {
            // The model writes `a|b` in a cell and means it; the inline renderer already knows
            // a code span, so the splitter has to as well.
            string[] cells = PipeTable.SplitCells("| `grep a|b` | matches either |");

            Assert.Equal(2, cells.Length);
            Assert.Equal("`grep a|b`", cells[0]);
        }

        [Fact]
        public void AShortRowIsPaddedAndALongOneIsTrimmedToTheHeader()
        {
            // A ragged row would otherwise put a cell under no column at all.
            PipeTableModel table = Parse(
                "| a | b | c |",
                "|---|---|---|",
                "| 1 |",
                "| 1 | 2 | 3 | 4 |");

            Assert.All(table.Rows, row => Assert.Equal(3, row.Length));
            Assert.Equal(new[] { "1", "", "" }, table.Rows[0]);
            Assert.Equal(new[] { "1", "2", "3" }, table.Rows[1]);
        }

        // ── Layout ───────────────────────────────────────────────────────────────

        [Fact]
        public void AtEightyColumnsTheTableFitsAtItsNaturalWidths()
        {
            var lines = Lay(Parse(Fixture), 80);

            // Header, rule, and one line per row — nothing needed wrapping.
            Assert.Equal(4, lines.Count);
            Assert.All(lines, l => Assert.True(l.Text.Length <= 80, $"too wide: \"{l.Text}\""));

            // Widest cells set the columns: "Leave local-only" (16) and the 25-char risk.
            Assert.Contains("Option", lines[0].Text);
            Assert.Contains("history lives on one disk", lines[3].Text);
        }

        [Fact]
        public void AtFortyColumnsTheWidestColumnWraps_AndNothingExceedsTheWidth()
        {
            var lines = Lay(Parse(Fixture), 40);

            Assert.All(lines, l => Assert.True(l.Text.Length <= 40,
                $"{l.Text.Length} > 40: \"{l.Text}\""));

            // It did not fit on one line per row, so something wrapped rather than overflowed.
            Assert.True(lines.Count > 4);

            // And the text survived the wrap — no cell was dropped to make room.
            string all = string.Concat(lines.Select(l => l.Text));
            Assert.Contains("history", all);
            Assert.Contains("disk", all);
        }

        [Fact]
        public void ATokenLongerThanItsColumnIsHardBroken_NotTruncated()
        {
            var runs = new[] { new InlineRun(new string('x', 30), InlineTextStyle.Normal) };

            var wrapped = PipeTable.WrapRuns(runs, 10);

            Assert.Equal(3, wrapped.Count);
            Assert.All(wrapped, line => Assert.Equal(10, line.Sum(r => r.Text.Length)));

            // Every character is still there. Truncation would be silent data loss in a table
            // whose whole purpose is comparing the values.
            string joined = string.Concat(wrapped.SelectMany(l => l).Select(r => r.Text));
            Assert.Equal(new string('x', 30), joined);
        }

        [Fact]
        public void WordWrapBreaksAtSpaces_WhenItCan()
        {
            var runs = new[] { new InlineRun("history lives on one disk", InlineTextStyle.Normal) };

            var wrapped = PipeTable.WrapRuns(runs, 12);

            Assert.All(wrapped, line => Assert.True(line.Sum(r => r.Text.Length) <= 12));
            Assert.Equal("history", string.Concat(wrapped[0].Select(r => r.Text)).Trim());
        }

        [Fact]
        public void AStyleSurvivesBeingWrappedAcrossLines()
        {
            // The wrap point can fall inside a run, which is the whole difficulty.
            var runs = new[] { new InlineRun("aaa bbb ccc", InlineTextStyle.InlineCode) };

            var wrapped = PipeTable.WrapRuns(runs, 4);

            Assert.All(wrapped, line => Assert.All(line, r => Assert.Equal(InlineTextStyle.InlineCode, r.Style)));
        }

        [Fact]
        public void TheHeaderIsBold()
        {
            var lines = Lay(Parse(Fixture), 80);

            Assert.Contains(lines[0].Segments,
                s => s.Style == InlineTextStyle.Bold && s.Text.Contains("Option"));
        }

        [Fact]
        public void ACodeSpanInACellKeepsItsStyle()
        {
            PipeTableModel table = Parse(
                "| Flag | Meaning |",
                "|---|---|",
                "| `--resume` | reopen a session |");

            var lines = Lay(table, 80);

            Assert.Contains(lines.SelectMany(l => l.Segments),
                s => s.Style == InlineTextStyle.InlineCode && s.Text.Contains("--resume"));
        }

        [Fact]
        public void BoldMarkersAreMeasuredAsWhatTheyDraw_NotAsTheirSource()
        {
            // "**yes**" is three columns wide, not seven. Measuring the source would leave
            // every row short and the rule too long.
            PipeTableModel table = Parse(
                "| A |",
                "|---|",
                "| **yes** |");

            var lines = Lay(table, 80);

            Assert.Equal(lines[0].Text.Length, lines[2].Text.Length);
            Assert.DoesNotContain("**", lines[2].Text);
        }

        [Fact]
        public void AlignmentPadsOnTheCorrectSide()
        {
            PipeTableModel table = Parse(
                "| left | centre | right |",
                "|:---|:---:|---:|",
                "| a | b | c |");

            var lines = Lay(table, 80);
            string row = lines[2].Text;

            Assert.StartsWith("a", row);          // left: content first
            Assert.EndsWith("c", row);            // right: content last
            Assert.Contains("  b  ", row);        // centre: padded both sides
        }

        [Fact]
        public void TheRuleSitsUnderTheHeaderAndMatchesItsWidth()
        {
            var lines = Lay(Parse(Fixture), 80);

            Assert.Equal(lines[0].Text.Length, lines[1].Text.Length);
            Assert.Contains("─", lines[1].Text);   // ─
            Assert.Contains("┼", lines[1].Text);   // ┼
        }

        [Fact]
        public void TheAsciiGlyphsAreAvailableForATerminalWithoutBoxDrawing()
        {
            var lines = PipeTable.Layout(Parse(Fixture), 80,
                MarkdownInlineRenderer.Render, TableGlyphs.Ascii);

            Assert.Contains("-", lines[1].Text);
            Assert.Contains("+", lines[1].Text);
            Assert.DoesNotContain("─", lines[1].Text);
            Assert.All(lines, l => Assert.All(l.Text, c => Assert.True(c < 128)));
        }

        [Fact]
        public void ColumnsAreNeverSqueezedBelowTheMinimum()
        {
            // An absurd width cannot produce zero-wide columns; it produces a narrow table
            // that the Editor will wrap, which is a better failure than an empty one.
            var lines = Lay(Parse(Fixture), 5);

            Assert.NotEmpty(lines);
            string all = string.Concat(lines.Select(l => l.Text));
            Assert.Contains("low", all);
        }

        [Fact]
        public void AnUnknownWidthFallsBackRatherThanCollapsing()
        {
            var lines = Lay(Parse(Fixture), 0);

            Assert.All(lines, l => Assert.True(l.Text.Length <= PipeTable.FallbackWidth));
            Assert.Contains("history lives on one disk", lines[3].Text);
        }
    }
}
