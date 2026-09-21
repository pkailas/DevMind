// File: HeadlessAddendumShellContentTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The headless addendum had rules for C# discipline, TypeScript, repeated errors, repo
// knowledge, write confinement and scratch files — and nothing about constructing file
// content inside a shell command, which is the habit that costs the most when it goes
// wrong. One bad quote, backtick or $ inside a here-string does not fail where it was
// made: it writes a file that looks written, the next build fails somewhere that looks
// unrelated, and the agent then patches against content that is not what it thinks it is.
// Several iterations burned on damage done by the first character.
//
// That cascade is the reason the rule exists, so the rule has to carry it — a bare
// prohibition reads as arbitrary and gets reasoned around. These tests pin the reason as
// well as the prohibition, and pin that the rule is its own block rather than a sentence
// buried mid-paragraph in a long prompt, because a buried rule does not fire.

using Xunit;

namespace DevMind.Core.Tests
{
    public class HeadlessAddendumShellContentTests
    {
        private const string Addendum = HeadlessAgent.HeadlessAddendum;

        [Fact]
        public void Addendum_ForbidsBuildingFileContentInTheShell()
        {
            Assert.Contains("File content never goes through the shell", Addendum, StringComparison.Ordinal);
            Assert.Contains("NEVER build a file with run_shell", Addendum, StringComparison.Ordinal);

            // The specific shapes, because "don't use the shell for files" is easy to read
            // past when the model is already composing one of them.
            Assert.Contains("here-string", Addendum, StringComparison.Ordinal);
            Assert.Contains("Out-File", Addendum, StringComparison.Ordinal);
        }

        [Fact]
        public void Addendum_NamesTheAlternativeTools_Explicitly()
        {
            Assert.Contains("create_file", Addendum, StringComparison.Ordinal);
            Assert.Contains("write_file", Addendum, StringComparison.Ordinal);
            Assert.Contains("patch_file", Addendum, StringComparison.Ordinal);
        }

        // The rule without its reason is a rule that gets reasoned around. What makes this
        // one stick is that the failure is DELAYED and MISATTRIBUTED — that is the part a
        // model cannot derive for itself from "don't do that".
        [Fact]
        public void Addendum_CarriesTheCascade_NotJustTheProhibition()
        {
            // Flattened: the block is hard-wrapped for the prompt, and which word the wrap
            // lands on is incidental formatting this guard must not be coupled to.
            string rule = ShellContentBlock().Replace('\n', ' ');

            Assert.Contains("LOOKS written", rule, StringComparison.Ordinal);
            Assert.Contains("looks unrelated", rule, StringComparison.Ordinal);
            Assert.Contains("patch against content that is not what you think it is", rule, StringComparison.Ordinal);
            Assert.Contains("iterations", rule, StringComparison.Ordinal);
        }

        // Placement: its own blank-line-delimited block, like "C# discipline:" and
        // "TypeScript discipline:", and ahead of the language-specific blocks because it
        // governs every file write regardless of language.
        [Fact]
        public void TheRuleIsItsOwnBlock_AheadOfTheLanguageSpecificOnes()
        {
            string[] blocks = Addendum.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

            int ruleBlock = Array.FindIndex(blocks,
                b => b.Contains("File content never goes through the shell", StringComparison.Ordinal));
            Assert.True(ruleBlock >= 0, "the shell-content rule is not a block of its own — it has been buried mid-paragraph");

            // It is the whole block, not one sentence inside a block about something else.
            Assert.StartsWith("File content never goes through the shell",
                blocks[ruleBlock].TrimStart(), StringComparison.Ordinal);

            int csharp = Array.FindIndex(blocks, b => b.StartsWith("C# discipline:", StringComparison.Ordinal));
            int typescript = Array.FindIndex(blocks, b => b.StartsWith("TypeScript discipline:", StringComparison.Ordinal));
            Assert.True(csharp >= 0 && typescript >= 0, "the discipline blocks moved — this guard needs revisiting");

            Assert.True(ruleBlock < csharp, "the shell-content rule now sits after the C# block");
            Assert.True(ruleBlock < typescript, "the shell-content rule now sits after the TypeScript block");
        }

        // The addendum's existing rails must survive the insertion. This duplicates nothing
        // that HeadlessGuardrailTests asserts — it checks the neighbours of the new block,
        // which is where an insertion actually does damage.
        [Fact]
        public void TheSurroundingRulesAreIntact()
        {
            Assert.Contains("Write operations (create, patch, delete, rename, append) are confined",
                Addendum, StringComparison.Ordinal);
            Assert.Contains("summary of what you changed and why.", Addendum, StringComparison.Ordinal);
            Assert.Contains("C# discipline: NEVER guess an API shape.", Addendum, StringComparison.Ordinal);
            Assert.Contains("ask_caller", Addendum, StringComparison.Ordinal);
        }

        /// <summary>The blank-line-delimited block holding the shell-content rule.</summary>
        private static string ShellContentBlock()
        {
            string[] blocks = Addendum.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            string? block = blocks.FirstOrDefault(
                b => b.Contains("File content never goes through the shell", StringComparison.Ordinal));
            Assert.True(block != null, "the shell-content rule is missing from the addendum entirely");
            return block!;
        }
    }
}
