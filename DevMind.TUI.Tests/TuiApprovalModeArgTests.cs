// File: TuiApprovalModeArgTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// --mode is how you start a session already in manual approval, rather than starting in auto
// and remembering to switch before the first write — which is the one moment the mode is
// actually for.
//
// TuiOptions.ApprovalMode is settable, unlike the interface member, because /mode flips it on
// the live options object the executor re-reads each dispatch. That is the same mechanism
// /think uses for ShowLlmThinking, and pinning it here is what stops a later refactor making
// it get-only and silently breaking the runtime toggle.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class TuiApprovalModeArgTests
    {
        [Fact]
        public void TheDefaultIsAuto()
        {
            Assert.Equal(ApprovalMode.Auto, new TuiOptions().ApprovalMode);
        }

        [Theory]
        [InlineData("manual", ApprovalMode.Manual)]
        [InlineData("auto", ApprovalMode.Auto)]
        [InlineData("MANUAL", ApprovalMode.Manual)]
        public void TheArgumentSetsTheMode(string text, ApprovalMode expected)
        {
            Assert.Equal(expected, TuiOptions.FromArgs(new[] { "--mode", text }).ApprovalMode);
        }

        // A typo starts in the safe default rather than refusing to start. Someone who types
        // "--mode manaul" wanting manual gets auto, which is the pre-existing behaviour and
        // visible in the status bar — not a process that will not launch.
        [Theory]
        [InlineData("yolo")]
        [InlineData("manaul")]
        [InlineData("")]
        public void AnUnrecognisedValue_LeavesTheDefault(string text)
        {
            Assert.Equal(ApprovalMode.Auto, TuiOptions.FromArgs(new[] { "--mode", text }).ApprovalMode);
        }

        // A trailing --mode with no value must not throw or consume past the end.
        [Fact]
        public void ADanglingArgument_IsIgnored()
        {
            Assert.Equal(ApprovalMode.Auto, TuiOptions.FromArgs(new[] { "--mode" }).ApprovalMode);
        }

        // Settable at runtime — this is what /mode writes to.
        [Fact]
        public void TheModeIsSettableOnTheLiveOptions()
        {
            var opts = new TuiOptions();
            opts.ApprovalMode = ApprovalMode.Manual;

            Assert.Equal(ApprovalMode.Manual, opts.ApprovalMode);
            Assert.Equal(ApprovalMode.Manual, ((ILlmOptions)opts).ApprovalMode);
        }
    }
}
