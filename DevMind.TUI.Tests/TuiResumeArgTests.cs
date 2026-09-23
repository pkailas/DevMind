// File: TuiResumeArgTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The launch flags that reopen a session.
//
// A session id is the one argument nobody types from memory — it is copied out of /history,
// and a copy is where a trailing space or a truncated tail comes from. So what the parser
// does with the value matters more here than for a flag whose argument is a number.
//
// -c is a single-dash short flag, which this parser has none of otherwise; it is here
// because it is what Qwen Code uses and muscle memory is the point of a shortcut.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class TuiResumeArgTests
    {
        [Fact]
        public void ResumeTakesTheSessionId()
        {
            var opts = TuiOptions.FromArgs(new[] { "--resume", "2026-09-20T101500Z-pid4242" });

            Assert.Equal("2026-09-20T101500Z-pid4242", opts.ResumeSessionId);
            Assert.False(opts.ContinueLatest);
        }

        [Fact]
        public void ContinueAndItsShortForm_MeanTheSameThing()
        {
            Assert.True(TuiOptions.FromArgs(new[] { "--continue" }).ContinueLatest);
            Assert.True(TuiOptions.FromArgs(new[] { "-c" }).ContinueLatest);
        }

        [Fact]
        public void NeitherFlag_LeavesBothOff()
        {
            var opts = TuiOptions.FromArgs(new[] { "--dir", "." });

            Assert.Equal("", opts.ResumeSessionId);
            Assert.False(opts.ContinueLatest);
        }

        [Fact]
        public void BothFlags_AreBothRecorded_SoStartupCanRefuse()
        {
            // The parser does not decide; it reports. They name different sessions, and
            // picking one silently is how someone ends up appending to the wrong one — so
            // startup reads both being set and says why it is starting fresh.
            var opts = TuiOptions.FromArgs(new[] { "--resume", "abc", "--continue" });

            Assert.Equal("abc", opts.ResumeSessionId);
            Assert.True(opts.ContinueLatest);
        }

        [Fact]
        public void ResumeWithNoValue_IsIgnoredRatherThanConsumingTheNextFlag()
        {
            // "--resume" as the last argument has nothing to take. The `when i + 1 <
            // args.Length` guard is what stops it swallowing whatever follows.
            var opts = TuiOptions.FromArgs(new[] { "--thinking", "--resume" });

            Assert.Equal("", opts.ResumeSessionId);
            Assert.True(opts.ShowLlmThinking);
        }

        [Fact]
        public void ResumeDoesNotDisturbTheOtherFlags()
        {
            var opts = TuiOptions.FromArgs(new[]
            {
                "--resume", "abc", "--max-depth", "12", "--mode", "manual",
            });

            Assert.Equal("abc", opts.ResumeSessionId);
            Assert.Equal(12, opts.AgenticLoopMaxDepth);
            Assert.Equal(ApprovalMode.Manual, opts.ApprovalMode);
        }
    }
}
