// File: TableBufferTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Holding lines back, and the promise to give them all up.
//
// A table cannot be drawn until it has ended, so its lines have to be withheld — and text
// withheld from a transcript is text the model wrote that the reader may never see. Every
// test here is about that promise: a pipe line that turns out to be prose comes straight
// back, the line that closes a table is not swallowed with it, and a flush releases whatever
// is still held.
//
// The last one is the one that would hurt. A table at the very end of a reply has no line
// after it to close it, so without the flush the most common table in practice — the one the
// model finishes its answer with — would simply never appear.

using System.Linq;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class TableBufferTests
    {
        [Fact]
        public void OrdinaryProsePassesStraightThrough()
        {
            var buffer = new TableBuffer();

            TableBufferResult result = buffer.Feed("Just a sentence.\n");

            Assert.Equal(TableBufferAction.Prose, result.Action);
            Assert.Equal(new[] { "Just a sentence.\n" }, result.Prose);
            Assert.False(buffer.IsHolding);
        }

        [Fact]
        public void APipeLineIsHeldUntilTheNextLineDecidesWhatItWas()
        {
            var buffer = new TableBuffer();

            Assert.Equal(TableBufferAction.Hold, buffer.Feed("| a | b |\n").Action);
            Assert.True(buffer.IsHolding);
        }

        [Fact]
        public void APipeLineFollowedByProseIsReleasedAsProse_InOrder()
        {
            var buffer = new TableBuffer();
            buffer.Feed("| not a table | really |\n");

            TableBufferResult result = buffer.Feed("and here is the next sentence.\n");

            Assert.Equal(TableBufferAction.Prose, result.Action);
            Assert.Equal(
                new[] { "| not a table | really |\n", "and here is the next sentence.\n" },
                result.Prose);
            Assert.False(buffer.IsHolding);
        }

        [Fact]
        public void HeaderSeparatorAndRowsBecomeOneTable_ClosedByABlankLine()
        {
            var buffer = new TableBuffer();
            buffer.Feed("| Option | Cost |\n");
            buffer.Feed("|---|---|\n");
            buffer.Feed("| Add a remote | low |\n");

            TableBufferResult result = buffer.Feed("\n");

            Assert.Equal(TableBufferAction.Table, result.Action);
            Assert.NotNull(result.Table);
            Assert.Equal(2, result.Table.ColumnCount);
            Assert.Single(result.Table.Rows);

            // The blank line that closed it is still printed — it is the paragraph break.
            Assert.Equal(new[] { "\n" }, result.Prose);
        }

        [Fact]
        public void TheLineThatClosesATableIsNotSwallowed()
        {
            var buffer = new TableBuffer();
            buffer.Feed("| a | b |\n");
            buffer.Feed("|---|---|\n");
            buffer.Feed("| 1 | 2 |\n");

            TableBufferResult result = buffer.Feed("Some prose right after.\n");

            Assert.Equal(TableBufferAction.Table, result.Action);
            Assert.Equal(new[] { "Some prose right after.\n" }, result.Prose);
        }

        [Fact]
        public void ATableAtTheEndOfAResponseIsEmittedOnFlush()
        {
            // Nothing follows it, so nothing closes it. Without the flush this table — the
            // one a model most often ends an answer with — would never be drawn at all.
            var buffer = new TableBuffer();
            buffer.Feed("| a | b |\n");
            buffer.Feed("|---|---|\n");
            buffer.Feed("| 1 | 2 |\n");

            TableBufferResult result = buffer.Flush();

            Assert.Equal(TableBufferAction.Table, result.Action);
            Assert.Single(result.Table.Rows);
            Assert.False(buffer.IsHolding);
        }

        [Fact]
        public void AHeldPipeLineAtTheEndIsReleasedAsProseOnFlush()
        {
            var buffer = new TableBuffer();
            buffer.Feed("| dangling pipe line\n");

            TableBufferResult result = buffer.Flush();

            Assert.Equal(TableBufferAction.Prose, result.Action);
            Assert.Equal(new[] { "| dangling pipe line\n" }, result.Prose);
        }

        [Fact]
        public void AHeaderAndSeparatorWithNoRowsIsStillATable()
        {
            var buffer = new TableBuffer();
            buffer.Feed("| a | b |\n");
            buffer.Feed("|---|---|\n");

            TableBufferResult result = buffer.Flush();

            Assert.Equal(TableBufferAction.Table, result.Action);
            Assert.Empty(result.Table.Rows);
        }

        [Fact]
        public void FlushingWithNothingHeldDoesNothing()
        {
            var buffer = new TableBuffer();

            TableBufferResult result = buffer.Flush();

            Assert.Equal(TableBufferAction.Hold, result.Action);
            Assert.Empty(result.Prose);
            Assert.Null(result.Table);
        }

        [Fact]
        public void TwoTablesInOneResponseAreTwoTables()
        {
            var buffer = new TableBuffer();
            buffer.Feed("| a |\n");
            buffer.Feed("|---|\n");
            buffer.Feed("| 1 |\n");
            Assert.Equal(TableBufferAction.Table, buffer.Feed("\n").Action);

            buffer.Feed("| b |\n");
            buffer.Feed("|---|\n");
            buffer.Feed("| 2 |\n");
            TableBufferResult second = buffer.Flush();

            Assert.Equal(TableBufferAction.Table, second.Action);
            Assert.Equal("b", second.Table.Header.Single());
        }

        [Fact]
        public void NoLineIsEverLost()
        {
            // The property the whole class exists to keep. Feed a mixture, collect everything
            // that comes back out, and account for every line that went in.
            string[] fed =
            {
                "intro\n",
                "| a | b |\n",
                "|---|---|\n",
                "| 1 | 2 |\n",
                "outro\n",
                "| lone pipe\n",
            };

            var buffer = new TableBuffer();
            var prose = new System.Collections.Generic.List<string>();
            int tables = 0;

            foreach (string line in fed)
            {
                TableBufferResult r = buffer.Feed(line);
                if (r.Table != null) tables++;
                prose.AddRange(r.Prose);
            }
            TableBufferResult tail = buffer.Flush();
            if (tail.Table != null) tables++;
            prose.AddRange(tail.Prose);

            Assert.Equal(1, tables);
            Assert.Equal(new[] { "intro\n", "outro\n", "| lone pipe\n" }, prose);
        }
    }
}
