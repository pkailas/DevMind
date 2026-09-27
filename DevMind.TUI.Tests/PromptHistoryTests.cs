// File: PromptHistoryTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// PromptHistory: shell-style Up/Down prompt recall with JSON-Lines
// persistence. Pure class (no Terminal.Gui dependency) — a temp file per
// test exercises the real persistence path without touching %APPDATA%.

using System.IO;
using DevMind;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class PromptHistoryTests : IDisposable
    {
        private readonly string _tempFile;

        public PromptHistoryTests()
        {
            _tempFile = Path.Combine(Path.GetTempPath(),
                "prompt-history-" + Guid.NewGuid().ToString("N") + ".jsonl");
        }

        public void Dispose()
        {
            try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
        }

        [Fact]
        public void Add_DedupeConsecutive_WhitespaceIgnored()
        {
            var h = new PromptHistory(null);

            h.Add("first");
            h.Add("first");      // identical to most recent — skipped
            h.Add("  ");         // whitespace — ignored
            h.Add(null!);        // null — ignored
            h.Add("second");
            h.Add("first");      // not consecutive to "second" — recorded

            Assert.True(h.TryPrevious("draft", out string a));
            Assert.Equal("first", a);
            Assert.True(h.TryPrevious("draft", out string b));
            Assert.Equal("second", b);
            Assert.True(h.TryPrevious("draft", out string c));
            Assert.Equal("first", c);
            Assert.False(h.TryPrevious("draft", out string _));
        }

        [Fact]
        public void PreviousNext_WalkWithDraftRestore()
        {
            var h = new PromptHistory(null);

            h.Add("one");
            h.Add("two");
            h.Add("three");

            // Down when not browsing — nothing to do.
            Assert.False(h.TryNext(out _));

            Assert.True(h.TryPrevious("my draft", out string t));
            Assert.Equal("three", t);
            Assert.True(h.IsBrowsing);

            Assert.True(h.TryPrevious("my draft", out t));
            Assert.Equal("two", t);

            Assert.True(h.TryPrevious("my draft", out t));
            Assert.Equal("one", t);

            // At the oldest — stay put.
            Assert.False(h.TryPrevious("my draft", out t));
            Assert.Equal("one", t);

            // Walk forward.
            Assert.True(h.TryNext(out t));
            Assert.Equal("two", t);
            Assert.True(h.IsBrowsing);

            Assert.True(h.TryNext(out t));
            Assert.Equal("three", t);
            Assert.True(h.IsBrowsing);

            // Past the newest — restore the saved draft and end browsing.
            Assert.True(h.TryNext(out t));
            Assert.Equal("my draft", t);
            Assert.False(h.IsBrowsing);

            // Not browsing again.
            Assert.False(h.TryNext(out t));
            Assert.True(h.TryPrevious("other", out t));
            Assert.Equal("three", t);
        }

        [Fact]
        public void TryPrevious_AtOldest_ReturnsFalse()
        {
            var h = new PromptHistory(null);

            h.Add("only");

            Assert.True(h.TryPrevious("d", out string t));
            Assert.Equal("only", t);
            Assert.False(h.TryPrevious("d", out t));
            Assert.Equal("only", t);
        }

        [Fact]
        public void TryNext_WhenNotBrowsing_ReturnsFalse()
        {
            var h = new PromptHistory(null);

            h.Add("one");

            Assert.False(h.TryNext(out string t));
            Assert.Equal(string.Empty, t);
            Assert.False(h.IsBrowsing);
        }

        [Fact]
        public void Cap_TrimOldest_FileMatchesAfterReload()
        {
            var h = new PromptHistory(_tempFile, cap: 3);
            for (int i = 1; i <= 5; i++)
                h.Add($"entry{i}");

            // Only the three newest survive, oldest order preserved.
            var reloaded = new PromptHistory(_tempFile);
            Assert.True(reloaded.TryPrevious("d", out string t));
            Assert.Equal("entry5", t);
            Assert.True(reloaded.TryPrevious("d", out t));
            Assert.Equal("entry4", t);
            Assert.True(reloaded.TryPrevious("d", out t));
            Assert.Equal("entry3", t);
            Assert.False(reloaded.TryPrevious("d", out t));

            // The file holds exactly the three kept entries.
            string[] lines = File.ReadAllLines(_tempFile);
            Assert.Equal(3, lines.Length);
        }

        [Fact]
        public void MultiLineEntry_RoundTrips()
        {
            string multi = "line one\nline two\nline three";
            var h = new PromptHistory(_tempFile);
            h.Add(multi);
            h.Add("plain");

            var reloaded = new PromptHistory(_tempFile);
            Assert.True(reloaded.TryPrevious("d", out string t));
            Assert.Equal("plain", t);
            Assert.True(reloaded.TryPrevious("d", out t));
            Assert.Equal(multi, t);
        }

        [Fact]
        public void CorruptLine_Skipped()
        {
            // One good JSON line, one garbage line, one more good line.
            File.WriteAllLines(_tempFile,
            [
                "\"good-one\"",
                "this is not json",
                "\"good-two\"",
            ]);

            var h = new PromptHistory(_tempFile);
            Assert.True(h.TryPrevious("d", out string t));
            Assert.Equal("good-two", t);
            Assert.True(h.TryPrevious("d", out t));
            Assert.Equal("good-one", t);
            Assert.False(h.TryPrevious("d", out t));
        }

        [Fact]
        public void NullPath_InMemoryOnly()
        {
            var h = new PromptHistory(null);
            h.Add("ephemeral");

            // A second instance with the same null path sees nothing.
            var h2 = new PromptHistory(null);
            Assert.False(h2.IsBrowsing);
            Assert.False(h2.TryPrevious("d", out string t));
            Assert.Equal("d", t);
            Assert.False(h2.TryNext(out t));
        }

        [Fact]
        public void MissingFile_LoadsEmpty_NoThrow()
        {
            // A path that does not exist must load as empty, not throw.
            var h = new PromptHistory(_tempFile);
            Assert.False(h.TryPrevious("d", out string t));
            Assert.Equal("d", t);
        }
    }
}
