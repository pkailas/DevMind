// File: PipeTableVerticalTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// When a grid stops being worth drawing.
//
// A grid is the better form right up until it is not, and the two things that break it are
// the two a reader notices: too little width for the columns to hold words, and cells so
// tall that a "row" has become a paragraph. Both thresholds are arithmetic, so both are
// testable — and worth testing, because the flip is the sort of thing that otherwise gets
// tuned by dragging a window until it looks right and is never written down.
//
// The record form deliberately aligns nothing across records. Alignment is what needs width,
// and this form is chosen exactly when there is not enough of it.

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class PipeTableVerticalTests
    {
        private static PipeTableModel Parse(params string[] lines) => PipeTable.TryParse(lines);

        private static IReadOnlyList<TableRowLine> Lay(PipeTableModel t, int width)
            => PipeTable.Layout(t, width, MarkdownInlineRenderer.Render, TableGlyphs.Unicode);

        private static TableForm Choose(PipeTableModel t, int width)
            => PipeTable.ChooseLayout(t, width, MarkdownInlineRenderer.Render);

        private static readonly PipeTableModel SixColumn = Parse(
            "| One | Two | Three | Four | Five | Six |",
            "|---|---|---|---|---|---|",
            "| a reasonably long cell | another one here | and a third | plus four | five | six |",
            "| short | short | short | short | short | short |");

        // Three columns, but one cell that will not fit on four lines in a narrow column.
        private static readonly PipeTableModel TallNotes = Parse(
            "| Option | Cost | Notes |",
            "|---|---|---|",
            "| Add a remote | low | " + string.Join(" ", Enumerable.Repeat("consideration", 40)) + " |",
            "| Leave local-only | none | " + string.Join(" ", Enumerable.Repeat("tradeoff", 40)) + " |");

        // ── Choosing ─────────────────────────────────────────────────────────────

        [Fact]
        public void AWideWindowKeepsTheGrid()
        {
            Assert.Equal(TableForm.Grid, Choose(SixColumn, 200));
        }

        [Fact]
        public void TooNarrowForTheColumnsMeansRecords()
        {
            // Six columns need 41 before the border and the margin are paid for.
            Assert.Equal(TableForm.Vertical, Choose(SixColumn, 40));
        }

        [Fact]
        public void TheThresholdIsTheOneTheConstantsDescribe()
        {
            // Stated as arithmetic rather than as a number someone tuned by eye. The cells
            // are one character each so the row-lines rule cannot also be deciding this —
            // the width threshold is what is under test.
            PipeTableModel tiny = Parse(
                "| a | b | c | d | e | f |",
                "|---|---|---|---|---|---|",
                "| 1 | 2 | 3 | 4 | 5 | 6 |");

            int columns = tiny.ColumnCount;
            int minHorizontal = System.Math.Max(
                PipeTable.AbsoluteMinHorizontalWidth,
                columns * PipeTable.MinColumnWidth + (1 + columns * 3) + PipeTable.SafetyMargin);

            Assert.Equal(TableForm.Vertical, Choose(tiny, minHorizontal - 1));
            Assert.Equal(TableForm.Grid, Choose(tiny, minHorizontal));
        }

        [Fact]
        public void ACellThatWrapsPastFourLinesMeansRecords_EvenWithRoomForColumns()
        {
            // Eighty columns is plenty of width for three columns; what breaks the grid here
            // is a row that has become a paragraph.
            Assert.Equal(TableForm.Vertical, Choose(TallNotes, 80));
        }

        [Fact]
        public void AnEmptyTableStaysAGrid()
        {
            // There are no records to make, and a lone header reads as a header.
            Assert.Equal(TableForm.Grid, Choose(Parse("| a | b |", "|---|---|"), 10));
        }

        // ── The record form ──────────────────────────────────────────────────────

        [Fact]
        public void EachRowBecomesOneLinePerColumn()
        {
            var lines = Lay(SixColumn, 40);

            // Six fields per record, two records, one rule between them. The wrapped values
            // add lines, so this is a floor rather than an equality.
            Assert.True(lines.Count >= 6 * 2 + 1, $"only {lines.Count} lines");
            Assert.Contains(lines, l => l.Text.StartsWith("One: "));
            Assert.Contains(lines, l => l.Text.StartsWith("Six: "));
        }

        [Fact]
        public void TheFieldNameCarriesTheHeadingStyle()
        {
            // The same emphasis a column header gets in the grid, so the two forms of the
            // same table read as the same table.
            var lines = Lay(SixColumn, 40);

            Assert.Contains(lines.SelectMany(l => l.Segments),
                s => s.Style == InlineTextStyle.Heading && s.Text.StartsWith("One:"));
        }

        [Fact]
        public void ARuleSeparatesRecords_AndIsNeverWiderThanFortyColumns()
        {
            // Measured at 120 columns, not at 40: a table can go vertical because its cells
            // are tall rather than because the window is narrow, and that is the only case
            // where an uncapped rule would be visibly wrong — a 119-character line ruling off
            // two short fields. At 40 an uncapped rule is under the cap anyway and the test
            // would pass either way.
            var lines = Lay(TallNotes, 120);

            var rules = lines.Where(l => l.Text.Trim().Length > 0
                                      && l.Text.Trim().All(c => c == '─')).ToList();

            Assert.Single(rules);              // two records, one rule between them
            Assert.True(rules[0].Text.Length <= PipeTable.MaxRecordRuleWidth,
                $"rule is {rules[0].Text.Length} wide, cap is {PipeTable.MaxRecordRuleWidth}");
        }

        [Fact]
        public void NoRecordLineExceedsTheWidth()
        {
            foreach (int width in new[] { 40, 32, 24 })
            {
                var lines = Lay(SixColumn, width);
                Assert.All(lines, l => Assert.True(l.Text.Length <= width,
                    $"{l.Text.Length} > {width}: \"{l.Text}\""));
            }
        }

        [Fact]
        public void AWrappedValueHangsUnderItself_NotUnderTheFieldName()
        {
            // A continuation starting at column zero would read as the next field.
            var lines = Lay(TallNotes, 30);

            int notes = -1;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Text.StartsWith("Notes: ")) { notes = i; break; }
            Assert.True(notes >= 0);
            Assert.True(notes + 1 < lines.Count);
            Assert.StartsWith(" ", lines[notes + 1].Text);
        }

        [Fact]
        public void NoValueIsLostToTheRecordForm()
        {
            string all = string.Concat(Lay(SixColumn, 40).Select(l => l.Text));

            Assert.Contains("reasonably", all);
            Assert.Contains("another", all);
            Assert.Contains("six", all);
        }
    }
}
