// File: TuiDefaultSystemPromptTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// TuiOptions used to carry its own copy of the fallback system prompt, one of four
// identical literals across the skins. It now points at DefaultPrompts.System in Core.
//
// This lives here rather than in DevMind.Core.Tests because TuiOptions is a TUI type and
// Core cannot see it: DevMind.TUI references DevMind.Core one-way. DefaultSystemPromptTests
// in Core.Tests holds the source-derived guard that no second literal reappears anywhere in
// the production tree, which is what covers this file's type from the other direction.
//
// Deliberately does not construct TuiOptions via FromArgs: that reads the global
// devmind.json and would make the assertion depend on the operator's machine. The default
// initializer is the thing under test.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class TuiDefaultSystemPromptTests
    {
        [Fact]
        public void TuiOptions_DefaultsToTheSharedConstant()
        {
            Assert.Equal(DefaultPrompts.System, new TuiOptions().SystemPrompt);
        }

        // The point of sharing the constant is the formatting guidance travelling with it:
        // a TUI run on a machine with no authored prompt is exactly the case that used to
        // get the bare role sentence.
        [Fact]
        public void TuiDefault_CarriesTheFormattingGuidance()
        {
            Assert.Contains("markdown", new TuiOptions().SystemPrompt, StringComparison.Ordinal);
            Assert.Contains("fenced", new TuiOptions().SystemPrompt, StringComparison.Ordinal);
        }
    }
}
