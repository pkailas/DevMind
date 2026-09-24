// File: EditingWorkflowPromptTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// BuildToolUsePrompt forbade what it required. "## Editing Workflow" said "Do not call
// read_file on the same file multiple times"; "## Large File Strategy", two paragraphs
// later, prescribed read_file for the outline and then read_file again for a line range —
// a second call on the same file by construction. The model cannot satisfy both, and the
// corpus showed 4,397 re-reads of an already-read file across 223 of 317 jobs.
//
// The rule now forbids re-reading CONTENT the model already has, and names the
// outline-then-range sequence as the one expected second read; Large File Strategy
// cross-references back. Both edits, because the prompt is read once, in order, by a model
// that will not flip back: the earlier rule has to carve out the exception itself, and the
// later section has to confirm it is the exception the reader was told about.
//
// A test that two prose sections "do not contradict" cannot be written honestly. What is
// pinned here is the specific wording chosen and its placement. That is a weak test — it
// guards against the exact words being lost, not against a future reword reintroducing the
// conflict — and it is described as such rather than dressed up.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class EditingWorkflowPromptTests
    {
        private static string Prompt() =>
            LoopHelpers.BuildToolUsePrompt("dotnet build", projectNamespace: null,
                                           workingDirectory: @"C:\work\repo");

        private static string Section(string prompt, string header)
        {
            int start = prompt.IndexOf(header, StringComparison.Ordinal);
            Assert.True(start >= 0, $"prompt is missing '{header}'");
            int end = prompt.IndexOf("\n## ", start + header.Length, StringComparison.Ordinal);
            return end < 0 ? prompt.Substring(start) : prompt.Substring(start, end - start);
        }

        [Fact]
        public void TheBlanketProhibitionIsGone()
        {
            Assert.DoesNotContain("Do not call read_file on the same file multiple times", Prompt(), StringComparison.Ordinal);
        }

        [Fact]
        public void EditingWorkflow_ForbidsRereadingContentAlreadyHeld_AndNamesTheException()
        {
            string s = Section(Prompt(), "## Editing Workflow");

            // The intent survives: no redundant re-reads, act once you have what you need.
            Assert.Contains("Do not re-read content you already have", s, StringComparison.Ordinal);
            Assert.Contains("Act immediately.", s, StringComparison.Ordinal);

            // The rule itself carves out the outline→range read, by name, forward-referencing
            // the section the model has not reached yet.
            Assert.Contains("outline-then-range sequence in Large File Strategy", s, StringComparison.Ordinal);
            Assert.Contains("the first call returns only an outline", s, StringComparison.Ordinal);
        }

        [Fact]
        public void LargeFileStrategy_StillPrescribesTheSecondRead_AndPointsBack()
        {
            string s = Section(Prompt(), "## Large File Strategy");

            // The workflow is unchanged: outline, then a ranged read of the same file, then patch.
            Assert.Contains("1. First read_file gets the outline.", s, StringComparison.Ordinal);
            Assert.Contains("3. Call read_file with start_line and end_line", s, StringComparison.Ordinal);
            Assert.Contains("Work outline → range → patch.", s, StringComparison.Ordinal);

            // ...and it confirms this is the exception the earlier rule named.
            Assert.Contains("Editing Workflow allows a second read_file on the same file", s, StringComparison.Ordinal);
        }

        // Placement: the rule must come before the workflow it excepts, because the model
        // reads forward only.
        [Fact]
        public void TheRulePrecedesTheWorkflowItExcepts()
        {
            string p = Prompt();
            int rule = p.IndexOf("## Editing Workflow", StringComparison.Ordinal);
            int flow = p.IndexOf("## Large File Strategy", StringComparison.Ordinal);

            Assert.True(rule >= 0 && flow >= 0);
            Assert.True(rule < flow, "Editing Workflow must precede Large File Strategy — the exception has to be granted before the model reaches the sequence that needs it");
        }
    }
}
