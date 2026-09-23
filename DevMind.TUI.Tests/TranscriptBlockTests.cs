// File: TranscriptBlockTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The indentation, and the one piece of state behind it.
//
// Four spaces is what says "this line came from the call above it" without spending a word
// on it. The state that produces it is a single flag, and every bug it can have is an
// ordering bug: output that nests under a call that already finished, output that fails to
// nest at all, or a chunk arriving with three lines in it that are treated as one.
//
// The chunking matters more than it looks. AppendOutput takes whatever the engine passes —
// a whole line, several, or half of one mid-stream — so the splitting is part of the
// behaviour rather than a detail of it.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class TranscriptBlockTests
    {
        private static string Run(TranscriptBlock block, params (string Text, OutputColor Color)[] chunks)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (text, color) in chunks)
                foreach (TranscriptLine line in block.Accept(text, color))
                    sb.Append(line.Text);
            return sb.ToString();
        }

        private static TranscriptBlock Quiet() => new TranscriptBlock(verbose: false);

        [Fact]
        public void OutputNestsUnderTheCallThatProducedIt()
        {
            string rendered = Run(Quiet(),
                ("[SHELL] > python patch.py\n", OutputColor.Dim),
                ("positional wv count before: 6\n", OutputColor.Normal),
                ("written\n", OutputColor.Normal));

            Assert.Equal(
                "› Shell python patch.py\n" +
                "    positional wv count before: 6\n" +
                "    written\n",
                rendered);
        }

        [Fact]
        public void ThreeLinesInOneChunk_AreThreeLines()
        {
            // The engine passes whatever it has; the splitting is this class's job.
            string rendered = Run(Quiet(),
                ("[SHELL] > ls\n", OutputColor.Dim),
                ("a\nb\nc\n", OutputColor.Normal));

            Assert.Equal("› Shell ls\n    a\n    b\n    c\n", rendered);
        }

        [Fact]
        public void AChunkThatEndsMidLine_IsNotHeldBack()
        {
            // Streaming output must not wait for its newline to appear on screen.
            var block = Quiet();
            Assert.Equal("› Shell ls\n", Run(block, ("[SHELL] > ls\n", OutputColor.Dim)));
            Assert.Equal("    partial", Run(block, ("partial", OutputColor.Normal)));
        }

        [Fact]
        public void ANewCallClosesThePreviousBlock()
        {
            string rendered = Run(Quiet(),
                ("[SHELL] > one\n", OutputColor.Dim),
                ("out\n", OutputColor.Normal),
                ("[FILE] Saved x.py (2 lines)\n", OutputColor.Success));

            Assert.Equal(
                "› Shell one\n" +
                "    out\n" +
                "✓ Write x.py (2 lines)\n",
                rendered);
        }

        [Fact]
        public void AStateLineEndsTheNesting()
        {
            // "[AGENTIC] Depth cap …" is followed by untagged advice lines. They belong to the
            // loop, not to whatever command ran last, and indenting them would say otherwise.
            string rendered = Run(Quiet(),
                ("[SHELL] > one\n", OutputColor.Dim),
                ("out\n", OutputColor.Normal),
                ("[AGENTIC] Depth cap reached (5).\n", OutputColor.Error),
                ("The edits made this run are on disk.\n", OutputColor.Dim));

            Assert.Equal(
                "› Shell one\n" +
                "    out\n" +
                "● Depth cap reached (5).\n" +
                "The edits made this run are on disk.\n",
                rendered);
        }

        [Fact]
        public void TheUserEchoEndsTheNesting_AndIsUntouched()
        {
            string rendered = Run(Quiet(),
                ("[SHELL] > one\n", OutputColor.Dim),
                ("out\n", OutputColor.Normal),
                ("\n> fix the build\n", OutputColor.Input));

            Assert.Equal(
                "› Shell one\n" +
                "    out\n" +
                "\n" +
                "> fix the build\n",
                rendered);
            Assert.False(Quiet().IsCallOpen);
        }

        [Fact]
        public void ProseClosesTheBlock()
        {
            // The model talking is never something a shell command produced.
            var block = Quiet();
            Run(block, ("[SHELL] > one\n", OutputColor.Dim));
            Assert.True(block.IsCallOpen);

            block.CloseBlock();

            Assert.False(block.IsCallOpen);
            Assert.Equal("stray\n", Run(block, ("stray\n", OutputColor.Normal)));
        }

        [Fact]
        public void ABlankLineDoesNotEndTheNesting()
        {
            // git and dotnet both print blank lines mid-output. De-indenting the rest of a
            // command's output at its first one would break the case the indent exists for.
            string rendered = Run(Quiet(),
                ("[SHELL] > git commit\n", OutputColor.Dim),
                ("[master abc1234] subject\n", OutputColor.Normal),
                ("\n", OutputColor.Normal),
                (" 3 files changed\n", OutputColor.Normal));

            Assert.Equal(
                "› Shell git commit\n" +
                "    [master abc1234] subject\n" +
                "\n" +
                "     3 files changed\n",
                rendered);
        }

        [Fact]
        public void OutputWithNoCallAboveIt_IsNotIndented()
        {
            Assert.Equal("orphan\n", Run(Quiet(), ("orphan\n", OutputColor.Normal)));
        }

        [Fact]
        public void VerboseReturnsTheChunkExactlyAsTheEngineWroteIt()
        {
            // DEVMIND_TUI_VERBOSE has meant "show me the engine raw" since the quiet filter
            // arrived. Translating under it would make it a differently-decorated view
            // instead of the escape hatch you reach for when you do not trust the view.
            var block = new TranscriptBlock(verbose: true);

            string rendered = Run(block,
                ("[SHELL] > python patch.py\n", OutputColor.Dim),
                ("written\n", OutputColor.Normal));

            Assert.Equal("[SHELL] > python patch.py\nwritten\n", rendered);
        }

        [Fact]
        public void AnEmptyChunkProducesNothing()
        {
            Assert.Empty(Quiet().Accept("", OutputColor.Normal));
            Assert.Empty(Quiet().Accept(null, OutputColor.Normal));
        }

        [Fact]
        public void TheColourSurvivesTheTranslation()
        {
            var block = Quiet();
            var lines = block.Accept("[FILE ERROR] access denied\n", OutputColor.Success);

            // The tag overrides the colour the emit site chose — an error is an error.
            Assert.Single(lines);
            Assert.Equal(OutputColor.Error, lines[0].Color);
        }
    }
}
