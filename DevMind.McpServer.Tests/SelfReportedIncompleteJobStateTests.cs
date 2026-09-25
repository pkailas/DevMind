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
        public void Job1674sRealFinalAnswer_EndsStoppedIncomplete()
        {
            // job-1674 ended state=done, incomplete_reasons=null after b1df22d was deployed and
            // loaded. Its final answer — verbatim from job-1674.result.json, the same `answer`
            // devmind_task_result returns and the same Result.Answer the classifier reads —
            // put the declaration under a "**INCOMPLETE:**" header as five "- INCOMPLETE:"
            // bullets, and v1.0 only matched a line opening with the bare word.
            string answer = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "job-1674.answer.md"));
            Assert.Contains("\n- INCOMPLETE: Full `Service.Tests` and `Core.Tests` runs", answer.Replace("\r\n", "\n"));

            var job = JobEndingWith(answer);

            Assert.True(job.IsIncomplete);
            string[] reasons = job.IncompleteReasons();
            Assert.Contains(SelfReportedIncompleteDetector.Reason, reasons);
            // The header carries no content, so the first bullet under it is what is quoted.
            Assert.Contains(reasons, r => r.StartsWith(
                "- INCOMPLETE: `AdminInputWidthTests` re-run after the `ElementWith` fix is unexecuted", StringComparison.Ordinal));
        }

        [Fact]
        public void Job1686sRealFinalAnswer_EndsDone()
        {
            // job-1686 finished (473 green, clean build) and ended stopped_incomplete with reason
            // "## Caller must know" — a notes-section HEADER. Verbatim from job-1686.result.json.
            string answer = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "job-1686.answer.md"));
            Assert.Contains("\n## Caller must know\n", answer.Replace("\r\n", "\n"));

            var job = JobEndingWith(answer);

            Assert.False(job.IsIncomplete);
            Assert.Empty(job.IncompleteReasons());
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

        // ── H-20: done with no final answer ──────────────────────────────────────────
        // job-1693 ended state=done, incomplete_reasons=[] after an implicit-done fallback
        // stopped it mid-research. Its answer was only harness status lines — nothing an
        // H-01 classifier could read — so the empty answer itself is the signal.

        [Theory]
        [InlineData("")]
        [InlineData("   \n\t ")]
        // job-1693's answer, verbatim from job-1693.result.json.
        [InlineData("[CONTEXT] 72,356 / 262,144 (27%) | Avg delta: 777 | Safe ceiling: 259,813\n\n[TOOL_USE] Processing tool call(s)...")]
        public void ADoneJobWithNoFinalAnswer_EndsStoppedIncomplete(string answer)
        {
            var job = JobEndingWith(answer);

            Assert.True(job.IsIncomplete);
            Assert.Contains("no_final_answer", job.IncompleteReasons());
        }

        [Fact]
        public void ARealAnswerAfterStatusLines_IsNotNoFinalAnswer()
        {
            var job = JobEndingWith("[CONTEXT] 1,000 / 262,144 (0%)\nAdded the handler; build 0/0.");

            Assert.False(job.IsIncomplete);
            Assert.DoesNotContain("no_final_answer", job.IncompleteReasons());
        }

        [Fact]
        public void AFailedJobWithNoAnswer_IsUntouched()
        {
            var job = JobEndingWith("", AgentJobState.Failed);

            Assert.False(job.IsIncomplete);
            Assert.Empty(job.IncompleteReasons());
        }
    }
}
