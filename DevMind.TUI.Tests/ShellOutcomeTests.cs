// File: ShellOutcomeTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The line that says whether the command worked.
//
// It exists because DevMind streams. A runner that executes and then prints can put the
// outcome glyph on the call line; here the call line and its output are already on screen
// when the exit code arrives, and going back to edit a line that is no longer the document's
// tail is the one edit this transcript deliberately cannot do.
//
// So the verdict is its own line, and the thing worth pinning is that a non-zero exit says
// so. A transcript that reports every command as ✓ is worse than one that reports none,
// because it is believed.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class ShellOutcomeTests
    {
        [Fact]
        public void ZeroSucceeds()
        {
            var (line, color) = ShellOutcome.Line(0, TimeSpan.FromSeconds(2.14));

            Assert.Equal("✓ exit 0 · 2.1s", line);
            Assert.Equal(OutputColor.Success, color);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(255)]
        [InlineData(-1)]
        public void AnythingElseFails_AndNamesTheCode(int exitCode)
        {
            var (line, color) = ShellOutcome.Line(exitCode, TimeSpan.FromSeconds(1));

            Assert.StartsWith("x exit " + exitCode, line);
            Assert.Equal(OutputColor.Error, color);
        }

        [Fact]
        public void ACancelledOrThrownCommand_ReportsFailureRatherThanSilence()
        {
            // The host seeds the exit code at -1 and only overwrites it on a clean return, so
            // a command that threw or was cancelled still closes its block with a verdict.
            var (line, color) = ShellOutcome.Line(-1, TimeSpan.FromSeconds(0.4));

            Assert.Equal("x exit -1 · 0.4s", line);
            Assert.Equal(OutputColor.Error, color);
        }

        [Theory]
        [InlineData(0.04, "0.0s")]
        [InlineData(2.14, "2.1s")]
        [InlineData(59.9, "59.9s")]
        public void UnderAMinute_ReadsInTenths(double seconds, string expected)
        {
            Assert.Equal(expected, ShellOutcome.Duration(TimeSpan.FromSeconds(seconds)));
        }

        [Theory]
        [InlineData(60, "1m 00s")]
        [InlineData(242, "4m 02s")]
        [InlineData(3600, "60m 00s")]
        public void OverAMinute_ReadsInMinutesAndSeconds(double seconds, string expected)
        {
            // 242.0s is a number you have to decode; 4m 02s is one you read.
            Assert.Equal(expected, ShellOutcome.Duration(TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void ANegativeElapsedCannotProduceNonsense()
        {
            Assert.Equal("0.0s", ShellOutcome.Duration(TimeSpan.FromSeconds(-5)));
        }

        [Fact]
        public void TheOutcomeNestsWithTheOutputItConcludes()
        {
            // It goes through the same door as the output, so the block indents it — which is
            // what puts the verdict at the foot of its own block rather than in the margin.
            var block = new TranscriptBlock(verbose: false);
            block.Accept("[SHELL] > dotnet build\n", OutputColor.Dim);
            block.Accept("Build succeeded.\n", OutputColor.Normal);

            var (line, color) = ShellOutcome.Line(0, TimeSpan.FromSeconds(2.1));
            var rendered = block.Accept(line + "\n", color);

            Assert.Single(rendered);
            Assert.Equal("    ✓ exit 0 · 2.1s\n", rendered[0].Text);
        }
    }
}
