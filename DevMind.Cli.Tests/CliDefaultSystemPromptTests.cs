// File: CliDefaultSystemPromptTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// CliOptions used to carry its own copy of the fallback system prompt, one of four
// identical literals across the skins. It now points at DefaultPrompts.System in Core.
//
// This lives here rather than in DevMind.Core.Tests because CliOptions is a CLI type and
// Core cannot see it: DevMind.Cli references DevMind.Core one-way. DefaultSystemPromptTests
// in Core.Tests holds the source-derived guard that no second literal reappears anywhere in
// the production tree, which is what covers this file's type from the other direction.

using Xunit;

namespace DevMind.Cli.Tests
{
    public class CliDefaultSystemPromptTests
    {
        [Fact]
        public void CliOptions_DefaultsToTheSharedConstant()
        {
            Assert.Equal(DefaultPrompts.System, new CliOptions().SystemPrompt);
        }

        // The point of sharing the constant is the formatting guidance travelling with it:
        // a CLI run on a machine with no authored prompt is exactly the case that used to
        // get the bare role sentence.
        [Fact]
        public void CliDefault_CarriesTheFormattingGuidance()
        {
            Assert.Contains("markdown", new CliOptions().SystemPrompt, StringComparison.Ordinal);
            Assert.Contains("fenced", new CliOptions().SystemPrompt, StringComparison.Ordinal);
        }

        // --system-prompt overwrites the default, unchanged by the move to a shared constant.
        // It is applied in FromArgs' third pass, after the config file and environment, so
        // this assertion does not depend on what either of those hold on this machine.
        [Fact]
        public void ExplicitArgument_StillOverridesTheDefault()
        {
            var opts = CliOptions.FromArgs(new[] { "--system-prompt", "custom prompt text" });

            Assert.Equal("custom prompt text", opts.SystemPrompt);
        }
    }
}
