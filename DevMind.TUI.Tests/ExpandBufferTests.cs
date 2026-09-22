// File: ExpandBufferTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// What /expand is allowed to say it has.
//
// Hiding text is only honest if the hidden text is one command away, so the failure worth
// guarding is the quiet one: /expand reporting success and appending nothing, or appending
// the wrong one of the two kinds. Bare /expand answering "whichever just happened" is the
// whole ergonomic point — a user who has just watched a marker appear types four characters,
// not fourteen.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class ExpandBufferTests
    {
        private static TranscriptLine[] Lines(params string[] texts)
        {
            var result = new TranscriptLine[texts.Length];
            for (int i = 0; i < texts.Length; i++)
                result[i] = new TranscriptLine(texts[i], OutputColor.Normal);
            return result;
        }

        [Fact]
        public void WithNothingParked_BareExpandSaysSo_AndIsNotAnError()
        {
            var buffer = new ExpandBuffer();
            ExpandResult result = buffer.Resolve("");

            Assert.Empty(result.Lines);
            Assert.False(result.IsError);
            Assert.Contains("Nothing to expand", result.Message);
        }

        [Fact]
        public void ParkedOutput_ComesBackIntactAndInOrder()
        {
            var buffer = new ExpandBuffer();
            buffer.ParkOutput(Lines("one", "two", "three"));

            ExpandResult result = buffer.Resolve("output");

            Assert.Equal(3, result.Lines.Count);
            Assert.Equal("one", result.Lines[0].Text);
            Assert.Equal("three", result.Lines[2].Text);
            Assert.False(result.IsError);
        }

        [Fact]
        public void ParkedThought_ComesBackInTheThinkingColour()
        {
            var buffer = new ExpandBuffer();
            buffer.ParkThought("the whole reasoning");

            ExpandResult result = buffer.Resolve("thought");

            Assert.Single(result.Lines);
            Assert.Equal("the whole reasoning", result.Lines[0].Text);
            Assert.Equal(OutputColor.Thinking, result.Lines[0].Color);
        }

        [Fact]
        public void BareExpand_TakesWhicheverWasParkedLast()
        {
            var buffer = new ExpandBuffer();
            buffer.ParkThought("reasoning");
            buffer.ParkOutput(Lines("shell line"));

            Assert.Equal("shell line", buffer.Resolve("").Lines[0].Text);

            buffer.ParkThought("newer reasoning");
            Assert.Equal("newer reasoning", buffer.Resolve("").Lines[0].Text);
        }

        [Fact]
        public void ExpandingDoesNotConsume()
        {
            var buffer = new ExpandBuffer();
            buffer.ParkOutput(Lines("line"));

            Assert.Single(buffer.Resolve("output").Lines);
            Assert.Single(buffer.Resolve("output").Lines);
        }

        [Fact]
        public void ParkingNothing_ClearsTheSlot()
        {
            var buffer = new ExpandBuffer();
            buffer.ParkOutput(Lines("line"));
            buffer.ParkOutput(Lines());

            ExpandResult result = buffer.Resolve("output");
            Assert.Empty(result.Lines);
            Assert.Contains("No hidden output", result.Message);
        }

        [Fact]
        public void AskingForTheKindThatIsNotThere_ReportsThatKind()
        {
            var buffer = new ExpandBuffer();
            buffer.ParkOutput(Lines("line"));

            ExpandResult result = buffer.Resolve("thought");

            Assert.Empty(result.Lines);
            Assert.Contains("No hidden thought", result.Message);
            Assert.False(result.IsError);
        }

        [Fact]
        public void AnUnknownArgument_IsAUsageError()
        {
            var buffer = new ExpandBuffer();
            buffer.ParkOutput(Lines("line"));

            ExpandResult result = buffer.Resolve("everything");

            Assert.True(result.IsError);
            Assert.Empty(result.Lines);
            Assert.Contains("Usage:", result.Message);
        }

        [Fact]
        public void ANewSession_HasNothingToExpand()
        {
            var buffer = new ExpandBuffer();
            buffer.ParkThought("reasoning");
            buffer.ParkOutput(Lines("line"));
            buffer.Clear();

            Assert.Contains("Nothing to expand", buffer.Resolve("").Message);
        }

        [Fact]
        public void TheWriterListIsCopied_NotAliased()
        {
            var buffer = new ExpandBuffer();
            var writer = new CappedOutputWriter(1, _ => { });
            writer.Write("shown", OutputColor.Normal);
            writer.Write("hidden", OutputColor.Normal);
            writer.Flush();

            buffer.ParkOutput(writer.Hidden);
            ExpandResult before = buffer.Resolve("output");

            Assert.Single(before.Lines);
            Assert.Equal("hidden", before.Lines[0].Text);
        }
    }
}
