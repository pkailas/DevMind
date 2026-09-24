// File: TranscriptModelTests.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The cap moved, and what it now bounds is calls rather than characters.
//
// The document used to be trimmed from the front: cut N characters, rebase the colour spans,
// carry on. That works on painted text and is nonsense on retained source — half a diff is
// not something that can be re-rendered, because the payload is an emitter's argument, not a
// run of characters. So the model drops whole entries, oldest first, and the document stays
// bounded because it is a projection of what survives.
//
// Order is the other thing worth pinning. A transcript is a sequence, and a model that
// reorders it would produce a rebuild that says the same things in the wrong order — which
// reads as a bug in the agent rather than in the renderer.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class TranscriptModelTests
    {
        private static TranscriptEntry Big(string tag, int size)
            => TranscriptEntry.Output(tag + new string('x', size), OutputColor.Normal);

        [Fact]
        public void EntriesComeBackInTheOrderTheyWentIn()
        {
            var model = new TranscriptModel();
            model.Append(TranscriptEntry.Output("first\n", OutputColor.Normal));
            model.Append(TranscriptEntry.Prose("second\n"));
            model.Append(TranscriptEntry.Code("third", "cs", false));

            Assert.Equal(3, model.Entries.Count);
            Assert.StartsWith("first", model.Entries[0].Text);
            Assert.StartsWith("second", model.Entries[1].Text);
            Assert.StartsWith("third", model.Entries[2].Text);
        }

        [Fact]
        public void UnderTheCapNothingIsDropped()
        {
            var model = new TranscriptModel();
            for (int i = 0; i < 10; i++) model.Append(Big("e", 100));

            Assert.Equal(0, model.Trim(100_000, 80_000));
            Assert.Equal(10, model.Entries.Count);
        }

        [Fact]
        public void OverTheCapTheOldestEntriesGo_AndTheNewestStay()
        {
            var model = new TranscriptModel();
            for (int i = 0; i < 50; i++) model.Append(Big($"e{i}:", 100));

            int dropped = model.Trim(2_000, 1_000);

            // The count is what the scroll anchor shifts by, so it has to be the real one:
            // the entries that are gone are exactly the ones no longer in the list.
            Assert.True(dropped > 0);
            Assert.Equal(50 - model.Entries.Count, dropped);

            Assert.True(model.Weight <= 1_000 + Big("e", 100).Weight);
            Assert.DoesNotContain(model.Entries, e => e.Text.StartsWith("e0:"));
            Assert.Contains(model.Entries, e => e.Text.StartsWith("e49:"));
        }

        [Fact]
        public void TrimNeverSplitsAnEntry()
        {
            // The payload is an emitter's argument. Half of it is not something that can be
            // rendered at all, let alone re-rendered at a new width.
            var model = new TranscriptModel();
            for (int i = 0; i < 20; i++)
                model.Append(TranscriptEntry.Diff("old-" + new string('a', 200), "new-" + new string('b', 200), "x.cs"));

            model.Trim(1_000, 500);

            Assert.All(model.Entries, e =>
            {
                Assert.StartsWith("old-", e.Text);
                Assert.StartsWith("new-", e.Second);
                Assert.Equal(204, e.Text.Length);
            });
        }

        [Fact]
        public void TheLastEntryIsNeverDropped()
        {
            // Trimming to nothing would leave a transcript that cannot show even the line
            // that just arrived.
            var model = new TranscriptModel();
            model.Append(Big("only:", 10_000));

            model.Trim(10, 5);

            Assert.Single(model.Entries);
        }

        [Fact]
        public void ClearEmptiesIt()
        {
            var model = new TranscriptModel();
            model.Append(TranscriptEntry.Prose("something\n"));

            model.Clear();

            Assert.Empty(model.Entries);
            Assert.Equal(0, model.Weight);
        }

        [Fact]
        public void ADiffsWeightCountsBothVersions()
        {
            // Otherwise the cap under-counts the single largest kind of entry there is.
            var model = new TranscriptModel();
            model.Append(TranscriptEntry.Diff(new string('a', 500), new string('b', 500), "x.cs"));

            Assert.True(model.Weight >= 1_000);
        }

        [Fact]
        public void ANullEntryIsIgnoredRatherThanStored()
        {
            var model = new TranscriptModel();
            model.Append(null);

            Assert.Empty(model.Entries);
        }
    }
}
