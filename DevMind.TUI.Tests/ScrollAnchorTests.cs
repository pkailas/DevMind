// File: ScrollAnchorTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Not losing the reader's place when the transcript is redrawn.
//
// A rebuild moves every offset in the document. Someone at the bottom does not notice —
// the bottom is still the bottom. Someone who scrolled up two screens to read something
// notices immediately: the naive rebuild drops them at the top, or leaves them at a
// character index that is now a different paragraph.
//
// So the anchor is an ENTRY, not an offset, and the arithmetic is the two lookups below.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class ScrollAnchorTests
    {
        //                                        e0  e1  e2   e3   e4
        private static readonly int[] Before = {   0, 40, 120, 300, 640 };
        private static readonly int[] After  = {   0, 60, 190, 450, 900 };

        [Fact]
        public void AnOffsetInsideAnEntryFindsThatEntry()
        {
            Assert.Equal(0, ScrollAnchor.Find(Before, 0));
            Assert.Equal(0, ScrollAnchor.Find(Before, 39));
            Assert.Equal(1, ScrollAnchor.Find(Before, 40));
            Assert.Equal(1, ScrollAnchor.Find(Before, 119));
            Assert.Equal(2, ScrollAnchor.Find(Before, 120));
            Assert.Equal(3, ScrollAnchor.Find(Before, 301));
        }

        [Fact]
        public void AnOffsetPastTheEndBelongsToTheNewestEntry()
        {
            // The reader is looking at the latest thing there is; that is what to keep them on.
            Assert.Equal(4, ScrollAnchor.Find(Before, 5_000));
        }

        [Fact]
        public void AnOffsetBeforeTheStartIsTheFirstEntry()
        {
            Assert.Equal(0, ScrollAnchor.Find(Before, -10));
        }

        [Fact]
        public void TheSameEntryComesBackAtItsNewOffset()
        {
            Assert.Equal(190, ScrollAnchor.Restore(After, 2));
            Assert.Equal(0, ScrollAnchor.Restore(After, 0));
            Assert.Equal(900, ScrollAnchor.Restore(After, 4));
        }

        [Fact]
        public void TheRoundTripKeepsTheReaderOnTheParagraphTheyWereReading()
        {
            // Mid-way through entry 3 before the rebuild → the start of entry 3 after it.
            int entry = ScrollAnchor.Find(Before, 350);

            Assert.Equal(3, entry);
            Assert.Equal(450, ScrollAnchor.Restore(After, entry));
        }

        [Fact]
        public void TheDeltaIsHowFarThatEntryMoved()
        {
            Assert.Equal(190 - 120, ScrollAnchor.Delta(Before, After, 150));
            Assert.Equal(0, ScrollAnchor.Delta(Before, After, 0));
        }

        [Fact]
        public void AnAnchorThatNoLongerExistsGoesToTheTop()
        {
            // The trim dropped it while the reader was looking at it. The top is the honest
            // answer; an invented offset would put them somewhere they never were.
            int[] shorter = { 0, 50 };

            Assert.Equal(0, ScrollAnchor.Restore(shorter, -1));
        }

        [Fact]
        public void AnEntryPastTheEndClampsToTheLastOne()
        {
            int[] shorter = { 0, 50 };

            Assert.Equal(50, ScrollAnchor.Restore(shorter, 9));
        }

        [Fact]
        public void NothingToAnchorToIsNotAnError()
        {
            Assert.Equal(-1, ScrollAnchor.Find(new int[0], 10));
            Assert.Equal(-1, ScrollAnchor.Find(null, 10));
            Assert.Equal(0, ScrollAnchor.Restore(new int[0], 3));
            Assert.Equal(0, ScrollAnchor.Delta(null, After, 10));
        }
    }
}
