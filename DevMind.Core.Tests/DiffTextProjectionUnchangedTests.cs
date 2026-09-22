// File: DiffTextProjectionUnchangedTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The diff TEXT must not move.
//
// RenderUnifiedDiff is what the model reads back from diff_file, what the CLI prints, and
// what a headless transcript records. Adding a line model for the TUI to paint from put a
// second consumer next to it, and the obvious tidy-up — make the text a projection of the
// model — would have re-implemented DiffPlex's own unidiff renderer by hand, at which point
// "byte-for-byte unchanged" becomes a claim about hunk headers and context radii rather than
// a fact.
//
// The text renderer was therefore left exactly as it was, and this fixture is what says so.
// It is not a specification of what a unified diff ought to look like; it is a record of
// what this one currently emits, captured before the model was added. If it fails, either
// the renderer changed or the DiffPlex version did, and both are things an agent reading the
// output downstream deserves to have noticed.

using Xunit;

namespace DevMind.Core.Tests
{
    public class DiffTextProjectionUnchangedTests
    {
        private const string Expected =
            "--- Widget.cs (original)\n" +
            "+++ Widget.cs (current)\n" +
            "@@ -4,7 +4,7 @@\n" +
            " {\n" +
            "     public class Widget\n" +
            "     {\n" +
            "-        public int Count;\n" +
            "+        public int Count = 1;\n" +
            " \n" +
            "         public void Reset()\n" +
            "         {\n" +
            "@@ -13,6 +13,7 @@\n" +
            " \n" +
            "         public void Bump()\n" +
            "         {\n" +
            "+            // keep it positive\n" +
            "             Count++;\n" +
            "         }\n" +
            "     }";

        [Fact]
        public void TheUnifiedDiffTextIsByteForByteWhatItWas()
        {
            string actual = DiffRenderer.RenderUnifiedDiff("Widget.cs", DiffFixture.Old, DiffFixture.New);

            Assert.Equal(Expected.Replace("\n", "\r\n"), actual.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        }

        [Fact]
        public void NoChangesStillReportsNoChanges()
        {
            Assert.Equal("DIFF: No changes detected in Widget.cs.",
                DiffRenderer.RenderUnifiedDiff("Widget.cs", DiffFixture.Old, DiffFixture.Old));
        }

        [Fact]
        public void TheModelAndTheTextAgreeAboutWhereTheHunksAre()
        {
            // The two renderers are independent — that is the point of leaving the text alone —
            // so this pins the one thing they must not disagree about. A model that split the
            // change into different hunks than the text would make the painted diff and the
            // diff the model reads describe the same edit differently.
            string text = DiffRenderer.RenderUnifiedDiff("Widget.cs", DiffFixture.Old, DiffFixture.New);
            var lines = DiffRenderer.Build(DiffFixture.Old, DiffFixture.New);

            foreach (DiffLine line in lines)
            {
                if (line.Kind != DiffLineKind.HunkHeader) continue;
                Assert.Contains(line.Text, text);
            }
        }
    }
}
