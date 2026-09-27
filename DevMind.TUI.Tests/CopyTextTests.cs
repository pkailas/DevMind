// File: CopyTextTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// CopyText.Build is the decision behind "a copy of a selection is what the reader
// meant to copy" — the gutter numbers the transcript draws are display chrome and
// must not ride into the clipboard. These test the pure function: given a document,
// a range and a non-copyable predicate, what comes out.
//
// The document here is shaped like the transcript paints it: a fenced block whose
// lines each lead with a "  N  " gutter marked non-copyable, exactly the spans the
// host records when it emits the gutter.

using System;
using System.Collections.Generic;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class CopyTextTests
    {
        // Build a document like the transcript paints it: prose, then a gutter-numbered
        // code block, then prose. Returns the text and a predicate true inside gutters.
        private static (string Doc, Func<int, bool> IsNonCopyable) MakeDoc()
        {
            var sb = new System.Text.StringBuilder();
            var gutters = new List<(int Start, int Len)>();

            sb.Append("Intro prose.\n");                       // 0..12
            string block = "  1  alpha\n  2  beta\n  3  gamma\n";
            int blockStart = sb.Length;
            sb.Append(block);
            sb.Append("Outro prose.\n");

            // Each "  N  " prefix is one non-copyable span.
            foreach (string line in block.Split('\n'))
            {
                gutters.Add((blockStart, 5));
                blockStart += line.Length + 1;
            }

            string doc = sb.ToString();
            return (doc, off =>
            {
                foreach (var (s, l) in gutters)
                    if (off >= s && off < s + l) return true;
                return false;
            });
        }

        [Fact]
        public void ASelectionAcrossAGutteredBlockReturnsTheCodeWithoutNumbers()
        {
            var (doc, isNonCopyable) = MakeDoc();

            // The whole block, gutters and all.
            int start = doc.IndexOf("  1  ", StringComparison.Ordinal);
            int end = doc.IndexOf("Outro prose.", StringComparison.Ordinal);

            string copied = CopyText.Build(doc, start, end, isNonCopyable);

            Assert.Equal("alpha\nbeta\ngamma\n", copied);
        }

        [Fact]
        public void ASelectionStartingMidGutterSkipsTheRestOfTheGutter()
        {
            var (doc, isNonCopyable) = MakeDoc();

            // Start two characters into the first gutter ("1" of "  1  ") and run to
            // the end of the first line's code. The rule is per offset, so the partial
            // gutter is skipped the same way a whole one is.
            int gutterStart = doc.IndexOf("  1  ", StringComparison.Ordinal);
            int start = gutterStart + 2;
            int end = doc.IndexOf("alpha", StringComparison.Ordinal) + "alpha".Length;

            string copied = CopyText.Build(doc, start, end, isNonCopyable);

            Assert.Equal("alpha", copied);
        }

        [Fact]
        public void ASelectionCoveringProseAndCodeKeepsTheProse()
        {
            var (doc, isNonCopyable) = MakeDoc();

            // From the start of the document through the end of the first code line:
            // the prose stays, the gutter of line 1 goes.
            int end = doc.IndexOf("alpha", StringComparison.Ordinal) + "alpha".Length;

            string copied = CopyText.Build(doc, 0, end, isNonCopyable);

            Assert.Equal("Intro prose.\nalpha", copied);
        }

        [Fact]
        public void NoNonCopyableSpansLeavesTheTextUnchanged()
        {
            var (doc, _) = MakeDoc();

            // A predicate that never marks anything: the copy is the selection verbatim.
            string copied = CopyText.Build(doc, 0, doc.Length, _ => false);

            Assert.Equal(doc, copied);
        }

        [Fact]
        public void AnEmptySelectionCopiesNothing()
        {
            var (doc, isNonCopyable) = MakeDoc();

            Assert.Equal("", CopyText.Build(doc, 5, 5, isNonCopyable));
            Assert.Equal("", CopyText.Build(doc, 7, 5, isNonCopyable));
        }

        [Fact]
        public void OffsetsAreClampedToTheDocument()
        {
            var (doc, isNonCopyable) = MakeDoc();

            // An end past the document's end is clamped, not a throw: the copy runs to
            // the last character.
            string copied = CopyText.Build(doc, 0, doc.Length + 50, isNonCopyable);
            Assert.Equal(CopyText.Build(doc, 0, doc.Length, isNonCopyable), copied);
        }
    }
}
