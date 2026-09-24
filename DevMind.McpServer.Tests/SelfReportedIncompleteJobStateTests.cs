// File: SelfReportedIncompleteJobStateTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-01: a job whose own final answer says the work is unfinished must not end `done`.
//
// Five jobs in two days (1652, 1654, 1667, 1668, 1670) ended state=done with
// incomplete_reasons=null while their final message said "the caller must not report
// LT-04 as fixed" and "no test run was performed". Every OTHER incompleteness signal is
// harness-measured — the depth cap, a failed build, a failed test run — so a job that
// skipped the build and never ran a test had no signal at all.
//
// `IsIncomplete` IS the terminal state as the caller sees it: both call sites that emit a
// state string (AgentTaskTools.DisplayState and AgentJobManager.WriteResultSidecar) read
// `job.IsIncomplete ? "stopped_incomplete" : job.State`. Asserting the flag and the reasons
// is asserting the transition; re-deriving the string here would only restate the ternary.

using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class SelfReportedIncompleteJobStateTests
    {
        private static AgentJob JobEndingWith(string answer,
            AgentJobState state = AgentJobState.Done) => new()
        {
            Id = "job-h01",
            Prompt = "p",
            WorkingDirectory = @"C:\temp\hermetic",
            State = state,
            Result = new HeadlessAgentResult { Answer = answer },
        };

        [Fact]
        public void AnAnswerDeclaringItselfUnfinished_EndsStoppedIncomplete()
        {
            var job = JobEndingWith(
                "Patched the parser and rebuilt clean.\nINCOMPLETE: tests not run");

            Assert.True(job.IsIncomplete);
            Assert.Contains(SelfReportedIncompleteDetector.Reason, job.IncompleteReasons());
            // The line that tripped it travels with the reason: a caller reading
            // incomplete_reasons should not have to re-read the answer to find out why.
            Assert.Contains("INCOMPLETE: tests not run", job.IncompleteReasons());
        }

        [Fact]
        public void TheFullAnswerIsKeptExactlyAsBefore()
        {
            // The classifier reads the answer; it does not edit it.
            const string answer = "The core defect is NOT fixed — the caller must not report LT-04 as fixed.";
            var job = JobEndingWith(answer);

            Assert.True(job.IsIncomplete);
            Assert.Equal(answer, job.Result!.Answer);
        }

        [Fact]
        public void ACleanAnswerStillEndsDone()
        {
            var job = JobEndingWith(
                "Added the handler and two tests. Full rebuild: 0 errors / 0 warnings. Suite 1398 green.");

            Assert.False(job.IsIncomplete);
            Assert.Equal(AgentJobState.Done, job.State);
            Assert.Empty(job.IncompleteReasons());
        }

        // A failed or cancelled job already reports its own terminal state, and this must
        // not relabel it: the guard is on State == Done in both members. Four facts rather
        // than a [Theory] because AgentJobState is internal and an xUnit test method's
        // parameters cannot be less accessible than the public test class.
        private static void AssertUntouched(AgentJobState state)
        {
            var job = JobEndingWith("INCOMPLETE: tests not run", state);

            Assert.False(job.IsIncomplete);
            Assert.Empty(job.IncompleteReasons());
        }

        [Fact]
        public void AFailedJobIsUntouched() => AssertUntouched(AgentJobState.Failed);

        [Fact]
        public void ACancelledJobIsUntouched() => AssertUntouched(AgentJobState.Cancelled);

        [Fact]
        public void ARunningJobIsUntouched() => AssertUntouched(AgentJobState.Running);

        [Fact]
        public void AQueuedJobIsUntouched() => AssertUntouched(AgentJobState.Queued);
    }
}
