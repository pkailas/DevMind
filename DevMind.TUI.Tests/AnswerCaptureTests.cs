// File: AnswerCaptureTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// What a resumed session remembers of its last answer.
//
// The history row for a tool-driven turn was empty: the answer lived in task_done's
// summary, rendered after the row was written. These pin the small thing that fixes it —
// that what was drawn as an answer is held until it is taken, once, and that nothing is
// held when nothing was drawn, so no empty row is ever added on top of the empty one.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class AnswerCaptureTests
    {
        [Fact]
        public void WhatWasDrawnIsWhatIsTaken()
        {
            var capture = new AnswerCapture();
            capture.Append("Renamed total to sum_of and added the docstring.\n");

            Assert.Equal("Renamed total to sum_of and added the docstring.", capture.Take());
        }

        [Fact]
        public void TakingClearsIt_SoTheNextTurnStartsEmpty()
        {
            var capture = new AnswerCapture();
            capture.Append("first");
            capture.Take();

            Assert.Null(capture.Take());
        }

        [Fact]
        public void NothingDrawnIsNull_NotAnEmptyRow()
        {
            var capture = new AnswerCapture();
            capture.Append("   \n");

            Assert.Null(capture.Take());
        }

        [Fact]
        public void TwoAnswersInOneTurnAreKeptInOrder_AsParagraphs()
        {
            // ask_caller's questions followed by a task_done summary in the same turn read
            // as two paragraphs, the way the pairing joins a segment's rows.
            var capture = new AnswerCapture();
            capture.Append("Which file?\n");
            capture.Append("Done.\n");

            Assert.Equal("Which file?\n\nDone.", capture.Take());
        }

        [Fact]
        public void ClearForgetsWithoutReturning()
        {
            var capture = new AnswerCapture();
            capture.Append("stale");
            capture.Clear();

            Assert.Null(capture.Take());
        }
    }
}
