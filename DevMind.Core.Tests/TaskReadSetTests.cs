// File: TaskReadSetTests.cs  v1.0
//
// Covers TaskReadSet — the shared write-guard state for the agentic hosts.
// The central property under test is that the set is keyed on RESOLVED FULL
// PATHS, not bare filenames. Every test below that asserts FALSE would
// incorrectly pass under the old bare-name keying (the defect this class
// was created to fix).

using Xunit;

namespace DevMind.Core.Tests
{
    public class TaskReadSetTests : IDisposable
    {
        private readonly string _tmpRoot;

        public TaskReadSetTests()
        {
            _tmpRoot = Path.Combine(Path.GetTempPath(), "devmind-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmpRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tmpRoot, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        /// <summary>
        /// Case 1 (the bug): two files sharing a bare name in different
        /// directories are DIFFERENT files. Marking one read must NOT
        /// satisfy the guard for the other. Under the old bare-name keying
        /// (HashSet&lt;string&gt; of Path.GetFileName) this assertion failed:
        /// "Program.cs" was already "known" from the first read.
        /// </summary>
        [Fact]
        public void IsKnown_Collision_SharedBareName_DifferentDirectory_IsNotKnown()
        {
            // Arrange: <tmp>/A/Program.cs and <tmp>/B/Program.cs — same bare name.
            string fileA = Path.Combine(_tmpRoot, "A", "Program.cs");
            string fileB = Path.Combine(_tmpRoot, "B", "Program.cs");

            var set = new TaskReadSet();
            set.MarkKnown(fileA);

            // Act: guard check on a DIFFERENT file that shares the bare name.
            bool known = set.IsKnown(fileB);

            // Assert: must be FALSE — the write guard must prompt for this file.
            Assert.False(known);
            // ... while the actually-read file stays known.
            Assert.True(set.IsKnown(fileA));
        }

        /// <summary>
        /// Case 2a: forward vs backslash separators for the SAME file must
        /// normalize to the same key — no false re-prompt.
        /// </summary>
        [Fact]
        public void IsKnown_SameFile_ForwardVsBackslash_IsKnown()
        {
            string file = Path.Combine(_tmpRoot, "A", "Program.cs");
            string forward = file.Replace('\\', '/');
            string backslash = file.Replace('/', '\\');

            // The two spellings are genuinely different strings on this platform...
            // (Path.Combine yields '\' on Windows, so at least one form differs.)
            Assert.NotEqual(forward, backslash);

            var set = new TaskReadSet();
            set.MarkKnown(forward);

            Assert.True(set.IsKnown(backslash),
                "A path with the opposite separator convention must hit the same key.");
        }

        /// <summary>
        /// Case 2b: a path containing ".." that resolves to the same file
        /// must normalize to the same key.
        /// </summary>
        [Fact]
        public void IsKnown_SameFile_DotDotSegment_IsKnown()
        {
            string file = Path.Combine(_tmpRoot, "A", "Program.cs");
            string withDotDot = Path.Combine(_tmpRoot, "A", "..", "A", "Program.cs");

            // Sanity: the spellings differ as raw strings.
            Assert.NotEqual(file, withDotDot);

            var set = new TaskReadSet();
            set.MarkKnown(file);

            Assert.True(set.IsKnown(withDotDot),
                "A '..' segment that resolves to the same file must hit the same key.");
            Assert.True(set.IsKnown(file), "Reverse direction: clean form stays known.");
        }

        /// <summary>
        /// Case 3: same file differing only in case must be known — the set
        /// uses OrdinalIgnoreCase.
        /// </summary>
        [Fact]
        public void IsKnown_SameFile_DifferentCase_IsKnown()
        {
            string file = Path.Combine(_tmpRoot, "A", "Program.cs");
            string upper = file.ToUpperInvariant(); // e.g. C:\...\A\PROGRAM.CS

            Assert.NotEqual(file, upper);

            var set = new TaskReadSet();
            set.MarkKnown(file);

            Assert.True(set.IsKnown(upper),
                "Case differences in the path must not cause a false re-prompt.");
        }

        /// <summary>
        /// Case 4: the empty-set clause. On a FRESH set, IsKnown returns TRUE
        /// for everything (the first write of a turn is always allowed). The
        /// moment ANY file is marked, that allowance closes for other paths.
        /// This pair pins the clause precisely so it cannot be silently deleted.
        /// </summary>
        [Fact]
        public void IsKnown_FreshSet_IsKnownForEverything()
        {
            var set = new TaskReadSet();

            Assert.True(set.IsKnown(Path.Combine(_tmpRoot, "A", "Program.cs")),
                "Fresh set: empty-set clause must allow the first write.");
        }

        [Fact]
        public void IsKnown_AfterOneMarkKnown_OtherPath_IsNotKnown()
        {
            var set = new TaskReadSet();
            set.MarkKnown(Path.Combine(_tmpRoot, "A", "Program.cs"));

            Assert.True(set.IsKnown(Path.Combine(_tmpRoot, "A", "Program.cs")));
            Assert.False(set.IsKnown(Path.Combine(_tmpRoot, "B", "Other.cs")),
                "Once any file is read, the empty-set allowance is gone for others.");
        }

        /// <summary>
        /// Case 5: Clear resets to the fresh-set state — so IsKnown is TRUE
        /// again (by the empty-set clause of case 4, NOT because the cleared
        /// file is remembered). The test name exists because "returns true
        /// after Clear" looks wrong without it.
        /// </summary>
        [Fact]
        public void IsKnown_AfterClear_EmptySetClauseAppliesAgain()
        {
            var set = new TaskReadSet();
            set.MarkKnown(Path.Combine(_tmpRoot, "A", "Program.cs"));
            Assert.False(set.IsKnown(Path.Combine(_tmpRoot, "B", "Other.cs")),
                "Precondition: guard is active before the reset.");

            set.Clear();

            // Fresh-set state restored: the empty-set clause applies again.
            Assert.True(set.IsKnown(Path.Combine(_tmpRoot, "A", "Program.cs")));
            Assert.True(set.IsKnown(Path.Combine(_tmpRoot, "B", "Other.cs")),
                "After Clear the set is empty — IsKnown is true for everything again.");
        }
    }
}
