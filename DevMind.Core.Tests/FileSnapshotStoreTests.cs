// File: FileSnapshotStoreTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// diff_file's session baselines — one store for the TUI host, BufferedAgenticHost and the MCP
// server (they used to keep three dictionaries with three different rules).

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class FileSnapshotStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"devmind_snapstore_{Guid.NewGuid():N}");

        public FileSnapshotStoreTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private string NewFile(string name, string content)
        {
            string p = Path.Combine(_dir, name);
            File.WriteAllText(p, content);
            return p;
        }

        [Fact]
        public void FirstCaptureWins()
        {
            var store = new FileSnapshotStore();
            string p = NewFile("a.txt", "one");
            store.Capture(p);
            File.WriteAllText(p, "two");
            store.Capture(p);

            Assert.True(store.TryGet(p, out string baseline));
            Assert.Equal("one", baseline);
        }

        [Fact]
        public void MissingFile_IsCapturedAsEmptyBaseline()
        {
            var store = new FileSnapshotStore();
            string p = Path.Combine(_dir, "new.txt");
            store.Capture(p);
            Assert.True(store.TryGet(p, out string baseline));
            Assert.Equal("", baseline);
        }

        [Fact]
        public void BomIsNotPartOfTheBaseline()
        {
            var store = new FileSnapshotStore();
            string p = Path.Combine(_dir, "bom.txt");
            File.WriteAllBytes(p, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("x")).ToArray());
            store.Capture(p);
            Assert.True(store.TryGet(p, out string baseline));
            Assert.Equal("x", baseline);
        }

        [Fact]
        public void KeysAreFullPathsWhateverTheSpelling()
        {
            var store = new FileSnapshotStore();
            string p = NewFile("a.txt", "one");
            store.Capture(Path.Combine(_dir, ".", "a.txt"));
            Assert.True(store.TryGet(p.ToUpperInvariant(), out _));
        }

        // DRIFT fix: the TUI's 20-entry LRU made diff_file on an evicted file claim it "has not
        // been modified this session". Eviction is now remembered and reported as such.
        [Fact]
        public void Eviction_IsLeastRecentlyUsed_AndRemembered()
        {
            var store = new FileSnapshotStore(capacity: 2);
            string a = NewFile("a.txt", "a"), b = NewFile("b.txt", "b"), c = NewFile("c.txt", "c");
            store.Capture(a);
            store.Capture(b);
            store.TryGet(a, out _);   // a is now more recent than b
            store.Capture(c);         // evicts b

            Assert.True(store.TryGet(a, out _));
            Assert.False(store.TryGet(b, out _));
            Assert.True(store.WasEvicted(b));
            Assert.False(store.WasEvicted(a));
        }

        [Fact]
        public void Diff_OfAnEvictedFile_SaysSo_NotUnmodified()
        {
            var store = new FileSnapshotStore(capacity: 1);
            string a = NewFile("a.txt", "a"), b = NewFile("b.txt", "b");
            var tools = new FileReadTools(FileReadPolicy.Agent, _dir, new FileContentCache(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), store, shell: null);

            tools.Read(a, null, null, forceFull: false);
            File.WriteAllText(a, "a changed");
            tools.Read(b, null, null, forceFull: false); // evicts a's baseline

            Assert.Equal(
                $"DIFF: No diff available — the session baseline for {a} was evicted (only the 1 most recently used files keep one).",
                tools.Diff(a).Text);
        }

        [Fact]
        public void Move_CarriesTheBaselineToTheNewPath()
        {
            var store = new FileSnapshotStore();
            string a = NewFile("a.txt", "a");
            store.Capture(a);
            string moved = Path.Combine(_dir, "moved.txt");
            store.Move(a, moved);
            Assert.False(store.TryGet(a, out _));
            Assert.True(store.TryGet(moved, out string baseline));
            Assert.Equal("a", baseline);
        }
    }
}
