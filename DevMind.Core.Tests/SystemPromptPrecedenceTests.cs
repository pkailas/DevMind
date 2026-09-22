// File: SystemPromptPrecedenceTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Three comments in the tree said an explicit --system-prompt beat the authored
// system-prompt.md. The code did the opposite: both skins resolved
//
//     string basePrompt = filePrompt ?? options.SystemPrompt;
//
// so whenever the authored file existed — which on a machine that has one is always — the
// typed argument was silently dropped, while the CLI help text went on advertising
// "Override system prompt".
//
// The root cause is that options.SystemPrompt is ALWAYS populated: the built-in default, or
// devmind.json's systemPrompt, or the argument. By the time either call site read it, the
// three were indistinguishable, so "did the operator type one?" was not a question the code
// could answer. The fix gives the typed prompt its own field and puts the decision in one
// pure function rather than at two call sites that had already drifted apart.
//
// Precedence, top to bottom, is what these pin:
//   explicit --system-prompt   (a per-invocation decision — an operator who types it means it)
//   system-prompt.md           (authored, hot-reloaded)
//   options.SystemPrompt       (devmind.json, else the built-in default)
//
// The middle tier outranking devmind.json is deliberate: a configured prompt is standing
// configuration, not a decision made about this run.

using Xunit;

namespace DevMind.Core.Tests
{
    public class SystemPromptPrecedenceTests
    {
        private const string Explicit = "EXPLICIT-ARG-PROMPT";
        private const string FromFile = "AUTHORED-FILE-PROMPT";
        private const string Fallback = "CONFIGURED-OR-DEFAULT-PROMPT";

        [Fact]
        public void ExplicitPrompt_BeatsTheAuthoredFile()
        {
            Assert.Equal(Explicit, SystemPromptFile.Resolve(Explicit, FromFile, Fallback));
        }

        [Fact]
        public void NoExplicitPrompt_TheAuthoredFileWins()
        {
            Assert.Equal(FromFile, SystemPromptFile.Resolve(null, FromFile, Fallback));
        }

        // Absence of the file is a normal state, not an error — SystemPromptFile.Load
        // returns null for missing, empty and whitespace-only alike.
        [Fact]
        public void NoExplicitPromptAndNoFile_TheFallbackWins()
        {
            Assert.Equal(Fallback, SystemPromptFile.Resolve(null, null, Fallback));
        }

        [Fact]
        public void ExplicitPrompt_WinsEvenWithNoFile()
        {
            Assert.Equal(Explicit, SystemPromptFile.Resolve(Explicit, null, Fallback));
        }

        // An empty or whitespace-only argument is not an explicit choice. Treating it as one
        // would let `--system-prompt ""` blank the prompt entirely, which is a way to break a
        // session by accident rather than a capability anyone asked for.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\n  ")]
        public void WhitespaceOnlyExplicitPrompt_IsNotExplicit(string blank)
        {
            Assert.Equal(FromFile, SystemPromptFile.Resolve(blank, FromFile, Fallback));
            Assert.Equal(Fallback, SystemPromptFile.Resolve(blank, null, Fallback));
        }

        // The file is taken exactly as given once it is non-null. Load() has already trimmed
        // it and already mapped blank to null, so Resolve must not second-guess that: a file
        // whose content is meaningful only to the model is not Resolve's to judge.
        [Fact]
        public void AFilePromptIsUsedAsGiven()
        {
            Assert.Equal("x", SystemPromptFile.Resolve(null, "x", Fallback));
        }
    }
}
