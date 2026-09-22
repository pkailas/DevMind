// File: TuiExplicitSystemPromptTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// ExplicitSystemPrompt exists to answer a question options.SystemPrompt cannot: did the
// operator type --system-prompt, or is this a default? It is only useful if exactly one
// thing sets it. These pin both halves — the parser does, and construction does not.
//
// Deliberately narrow: TuiOptions.FromArgs is exercised only for the flag under test, and
// the default case constructs TuiOptions directly, because FromArgs reads the operator's
// real devmind.json and an assertion that depends on that is an assertion about this
// machine (same reasoning as TuiDefaultSystemPromptTests).

using Xunit;

namespace DevMind.TUI.Tests
{
    public class TuiExplicitSystemPromptTests
    {
        [Fact]
        public void TheArgumentParser_RecordsAnExplicitPrompt()
        {
            var opts = TuiOptions.FromArgs(new[] { "--system-prompt", "X" });

            Assert.Equal("X", opts.ExplicitSystemPrompt);

            // Still assigned to SystemPrompt too, so every existing reader keeps working.
            Assert.Equal("X", opts.SystemPrompt);
        }

        [Fact]
        public void WithoutTheArgument_NothingIsExplicit()
        {
            Assert.Null(new TuiOptions().ExplicitSystemPrompt);
        }

        // The whole point of the separate field: SystemPrompt is populated either way, so it
        // cannot distinguish these two cases and ExplicitSystemPrompt must.
        [Fact]
        public void SystemPrompt_AloneCannotDistinguishTypedFromDefault()
        {
            var typed = TuiOptions.FromArgs(new[] { "--system-prompt", "X" });
            var plain = new TuiOptions();

            Assert.False(string.IsNullOrWhiteSpace(typed.SystemPrompt));
            Assert.False(string.IsNullOrWhiteSpace(plain.SystemPrompt));

            Assert.NotNull(typed.ExplicitSystemPrompt);
            Assert.Null(plain.ExplicitSystemPrompt);
        }
    }
}
