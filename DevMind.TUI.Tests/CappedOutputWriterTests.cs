// File: CappedOutputWriterTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The head/tail cap on tool output echoed into the transcript.
//
// Two properties matter and both are easy to break silently. The first is that the cap is a
// DISPLAY cap: whatever is hidden is still handed over whole, in order, or /expand would show
// a mangled version of what the model actually saw. The second is that the marker names the
// count — a bare "…" tells the reader something was hidden but not whether it was three lines
// or three hundred, which is the difference between ignoring it and expanding it.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class CappedOutputWriterTests
    {
        private static (CappedOutputWriter writer, List<TranscriptLine> emitted) Make(int cap)
        {
            var emitted = new List<TranscriptLine>();
            return (new CappedOutputWriter(cap, line => emitted.Add(line)), emitted);
        }

        private static void WriteLines(CappedOutputWriter writer, int count, string prefix = "line")
        {
            for (int i = 1; i <= count; i++)
                writer.Write($"{prefix} {i}", OutputColor.Normal);
        }

        [Fact]
        public void TheDefaultCapIsTwelve_SplitFourHeadEightTail()
        {
            // Tail-heavy: the verdict of a build or a test run is on its last lines, and the
            // head only has to show enough to recognise what is running.
            Assert.Equal(12, CappedOutputWriter.DefaultCap);
            Assert.Equal((4, 8), CappedOutputWriter.Split(CappedOutputWriter.DefaultCap));
        }

        [Fact]
        public void EveryCapKeepsAtLeastOneHeadLine()
        {
            // Without a floor a small cap would show the tail and nothing else, and a tool
            // call whose output is only its ending is unreadable — the reader cannot tell
            // which command produced it.
            Assert.Equal((1, 0), CappedOutputWriter.Split(1));
            Assert.Equal((1, 1), CappedOutputWriter.Split(2));
            Assert.Equal((1, 2), CappedOutputWriter.Split(3));
            Assert.Equal((2, 4), CappedOutputWriter.Split(6));
        }

        [Fact]
        public void UnderTheCap_EverythingShows_AndNoMarkerAppears()
        {
            var (writer, emitted) = Make(12);
            WriteLines(writer, 12);
            writer.Flush();

            Assert.Equal(12, emitted.Count);
            Assert.Empty(writer.Hidden);
            Assert.DoesNotContain(emitted, l => l.Text.Contains("hidden"));
            Assert.Equal("line 1", emitted[0].Text);
            Assert.Equal("line 12", emitted[11].Text);
        }

        [Fact]
        public void OverTheCap_ShowsHeadThenMarkerThenTail()
        {
            var (writer, emitted) = Make(12);
            WriteLines(writer, 49);          // 4 head + 37 hidden + 8 tail
            writer.Flush();

            Assert.Equal(13, emitted.Count); // 12 output lines + the marker

            for (int i = 0; i < 4; i++)
                Assert.Equal($"line {i + 1}", emitted[i].Text);

            Assert.Equal(CappedOutputWriter.Marker(37), emitted[4].Text);
            Assert.Equal(OutputColor.Dim, emitted[4].Color);

            Assert.Equal("line 42", emitted[5].Text);
            Assert.Equal("line 49", emitted[12].Text);
        }

        [Fact]
        public void TheMarkerNamesTheHiddenCount()
        {
            var (writer, emitted) = Make(12);
            WriteLines(writer, 49);
            writer.Flush();

            TranscriptLine marker = emitted[4];
            Assert.Contains("37", marker.Text);
            Assert.Contains("/expand", marker.Text);
        }

        [Fact]
        public void TheHiddenRemainder_IsHandedOverIntactAndInOrder()
        {
            var (writer, _) = Make(12);
            WriteLines(writer, 49);
            writer.Flush();

            Assert.Equal(37, writer.Hidden.Count);
            Assert.Equal("line 5", writer.Hidden[0].Text);
            Assert.Equal("line 41", writer.Hidden[36].Text);
            for (int i = 0; i < writer.Hidden.Count; i++)
                Assert.Equal($"line {i + 5}", writer.Hidden[i].Text);
        }

        [Fact]
        public void ErrorLinesKeepTheirColourThroughTheCap()
        {
            var (writer, emitted) = Make(4);   // 1 head + 3 tail
            writer.Write("bad", OutputColor.Error);
            WriteLines(writer, 10, "noise");
            writer.Write("last", OutputColor.Error);
            writer.Flush();

            // Through the head…
            Assert.Equal("bad", emitted[0].Text);
            Assert.Equal(OutputColor.Error, emitted[0].Color);

            // …and through the held tail, which is the half that matters: stderr on the last
            // line of a failing command must not come back as ordinary output.
            Assert.Equal("last", emitted[emitted.Count - 1].Text);
            Assert.Equal(OutputColor.Error, emitted[emitted.Count - 1].Color);
        }

        [Fact]
        public void ZeroIsUncapped_NothingIsEverHidden()
        {
            var (writer, emitted) = Make(0);
            WriteLines(writer, 500);
            writer.Flush();

            Assert.Equal(500, emitted.Count);
            Assert.Empty(writer.Hidden);
            Assert.DoesNotContain(emitted, l => l.Text.Contains("hidden"));
        }

        [Fact]
        public void ExactlyAtTheCap_IsStillMarkerFree()
        {
            var (writer, emitted) = Make(12);
            WriteLines(writer, 13);
            writer.Flush();

            Assert.Equal(13, emitted.Count);              // one line over: 4 head + marker + 8 tail
            Assert.Equal(CappedOutputWriter.Marker(1), emitted[4].Text);

            var (exact, exactEmitted) = Make(12);
            WriteLines(exact, 12);
            exact.Flush();
            Assert.Equal(12, exactEmitted.Count);          // exactly at the cap: none
        }

        [Fact]
        public void ACapOfOne_ShowsTheFirstLineAndHidesTheRest()
        {
            var (writer, emitted) = Make(1);
            WriteLines(writer, 5);
            writer.Flush();

            Assert.Equal("line 1", emitted[0].Text);
            Assert.Equal(CappedOutputWriter.Marker(4), emitted[1].Text);
            Assert.Equal(2, emitted.Count);
            Assert.Equal(4, writer.Hidden.Count);
        }
    }
}
