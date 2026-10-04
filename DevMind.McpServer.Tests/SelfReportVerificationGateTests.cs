// File: SelfReportVerificationGateTests.cs  v1.2
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-43: green harness test verification outweighs an explicit INCOMPLETE: marker. job-2118
// ended stopped_incomplete on "INCOMPLETE: full solution test suite (1308 + 11 new) not run by
// me — harness verifies it." while the harness's own test run was green (1319, +11).
//
// H-64: the phrase list (H-31's weak hits — job-1714, job-1715, job-2143) is gone. Wording
// without the marker is not a self-report at all, whatever the verification.
//
// The rule, as a table:
//   INCOMPLETE: declaration       → done + self_report_note ONLY when the harness TEST run was
//                                   green (verify_tests on, build and test verification both
//                                   passed); stopped_incomplete otherwise — a green build alone
//                                   proves nothing was fixed, and verify_build off is the H-01
//                                   shape the self-report exists for
//   NONE (incl. phrase wording)   → unaffected

using System.Diagnostics;
using System.Text.Json;
using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class SelfReportVerificationGateTests : IDisposable
    {
        private const string StrongAnswer = "Patched the parser.\nINCOMPLETE: the migration is not written";
        private const string NoneAnswer = "Patched the parser. Build 0/0, suite green.";

        // Verbatim final line from job-2143 (H-64): the brief had said "Do not run the service".
        private const string Job2143Line =
            "- No test projects exist, so per the brief I ran no tests and did not run the service. " +
            "The only verification is the 0/0 rebuild plus read-back of the changed file.";

        private static BuildVerification Run(bool ok) => new()
        {
            Command = "dotnet build x.slnx",
            ExitCode = ok ? 0 : 1,
            OutputTail = ok ? "Build succeeded." : "error CS0103",
        };

        private static TestVerification Tests(bool ok) => new() { Raw = Run(ok), Total = 10 };

        // Verification scenario names are strings: AgentJob / AgentJobState are internal, and an
        // xUnit theory's parameters cannot be less accessible than the public test class.
        private static AgentJob Job(string answer, string verification)
        {
            bool verifyTests = verification is "build-ok+tests-ok" or "tests-failed" or "tests-requested-not-run";
            var job = new AgentJob
            {
                Id = "job-h31",
                Prompt = "p",
                WorkingDirectory = @"C:\temp\hermetic",
                VerifyBuild = verification != "build-not-run",
                VerifyTests = verifyTests,
                State = AgentJobState.Done,
                Result = new HeadlessAgentResult { Answer = answer },
            };
            switch (verification)
            {
                case "build-ok+tests-ok":         job.Build = Run(true);  job.Tests = Tests(true);  break;
                case "build-ok+tests-not-requested": job.Build = Run(true);                         break;
                case "build-failed":              job.Build = Run(false);                           break;
                case "build-not-run":                                                               break;
                case "tests-failed":              job.Build = Run(true);  job.Tests = Tests(false); break;
                case "tests-requested-not-run":   job.Build = Run(true);                            break;
                default: throw new ArgumentOutOfRangeException(nameof(verification), verification, null);
            }
            return job;
        }

        private static string Answer(string kind) => kind switch
        {
            "strong" => StrongAnswer,
            "none" => NoneAnswer,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        [Theory]
        // strong: incomplete only when the harness did not positively verify (H-43)
        [InlineData("strong", "build-ok+tests-ok", false)]
        [InlineData("strong", "build-ok+tests-not-requested", true)]
        [InlineData("strong", "build-failed", true)]
        [InlineData("strong", "build-not-run", true)]
        [InlineData("strong", "tests-failed", true)]
        [InlineData("strong", "tests-requested-not-run", true)]
        // none: only the harness's own failures count
        [InlineData("none", "build-ok+tests-ok", false)]
        [InlineData("none", "build-ok+tests-not-requested", false)]
        [InlineData("none", "build-failed", true)]
        [InlineData("none", "build-not-run", false)]
        [InlineData("none", "tests-failed", true)]
        public void SelfReport_x_Verification(string selfReport, string verification, bool expectIncomplete)
        {
            var job = Job(Answer(selfReport), verification);
            string[] reasons = job.IncompleteReasons();

            Assert.Equal(expectIncomplete, job.IsIncomplete);

            bool selfReasonExpected = selfReport != "none" && expectIncomplete;
            Assert.Equal(selfReasonExpected, reasons.Contains(SelfReportedIncompleteDetector.Reason));

            // The self-reported line is never lost: it is either an incomplete reason or the note.
            bool noteExpected = selfReport != "none" && !expectIncomplete;
            Assert.Equal(noteExpected, job.SelfReportNote != null);
            if (selfReport != "none")
            {
                const string line = "INCOMPLETE: the migration is not written";
                Assert.True(noteExpected ? job.SelfReportNote == line : reasons.Contains(line));
            }
        }

        // Verbatim from job-2118 (H-43).
        private const string Job2118Line =
            "INCOMPLETE: full solution test suite (1308 + 11 new) not run by me — harness verifies it. Nothing else outstanding. No commit made.";

        [Fact]
        public void Job2118_MarkerWithTestsVerifiedGreen_EndsDone_WithTheLineAsANote()
        {
            var job = Job("Moved Server > Security onto the shared form layout.\n" + Job2118Line, "build-ok+tests-ok");

            Assert.False(job.IsIncomplete);
            Assert.Empty(job.IncompleteReasons());
            Assert.Equal(Job2118Line, job.SelfReportNote);
        }

        [Theory]
        [InlineData("tests-failed")]
        [InlineData("build-ok+tests-not-requested")]   // verify_tests off
        [InlineData("build-not-run")]
        public void Job2118_MarkerWithoutAGreenTestRun_EndsStoppedIncomplete(string verification)
        {
            var job = Job("Moved Server > Security onto the shared form layout.\n" + Job2118Line, verification);

            Assert.True(job.IsIncomplete);
            Assert.Contains(SelfReportedIncompleteDetector.Reason, job.IncompleteReasons());
            Assert.Contains(Job2118Line, job.IncompleteReasons());
            Assert.Null(job.SelfReportNote);
        }

        [Theory]
        [InlineData("INCOMPLETE: none.")]
        [InlineData("INCOMPLETE: Nothing")]
        [InlineData("INCOMPLETE: n/a.")]
        [InlineData("INCOMPLETE: none. Only the TotalAgility page changed; other config pages untouched.")] // job-2104
        public void ANoneMarker_IsDone_WithNoNote_EvenWithoutTestVerification(string line)
        {
            var job = Job("Patched the parser.\n" + line, "build-ok+tests-not-requested");

            Assert.False(job.IsIncomplete);
            Assert.Empty(job.IncompleteReasons());
            Assert.Null(job.SelfReportNote);
        }

        [Fact]
        public void Job2143_BuildVerifiedNoTestProject_EndsDone_WithNoNote()
        {
            // job-2143's shape: harness build green, no tests to verify, and a final line that
            // describes what the brief forbade. Not a self-report: no reason, no note.
            var job = Job("Removed the v1 mailbox-path code.\n" + Job2143Line, "build-ok+tests-not-requested");

            Assert.False(job.IsIncomplete);
            Assert.Empty(job.IncompleteReasons());
            Assert.Null(job.SelfReportNote);
        }

        [Theory]
        [InlineData("build-ok+tests-not-requested")]
        [InlineData("build-not-run")]
        public void UnfinishedWordingWithoutTheMarker_EndsDone(string verification)
        {
            var job = Job("Patched the parser.\nThe migration is not done.\nDid not run the TUI.", verification);

            Assert.False(job.IsIncomplete);
            Assert.DoesNotContain(SelfReportedIncompleteDetector.Reason, job.IncompleteReasons());
            Assert.Null(job.SelfReportNote);
        }

        [Fact]
        public void APhraseAboveAMarker_QuotesTheMarker()
        {
            // The marker outranks the earlier phrase hit: it is the line a driver must read,
            // whichever field it lands in.
            const string answer = "- Did not run the TUI.\nINCOMPLETE: the migration is not written";

            var verified = Job(answer, "build-ok+tests-ok");
            Assert.False(verified.IsIncomplete);
            Assert.Equal("INCOMPLETE: the migration is not written", verified.SelfReportNote);

            var unverified = Job(answer, "build-ok+tests-not-requested");
            Assert.True(unverified.IsIncomplete);
            Assert.Contains("INCOMPLETE: the migration is not written", unverified.IncompleteReasons());
            Assert.Null(unverified.SelfReportNote);
        }

        [Fact]
        public void ANonDoneJob_HasNoNote()
        {
            var job = Job(StrongAnswer, "build-ok+tests-ok");
            job.State = AgentJobState.Failed;

            Assert.Null(job.SelfReportNote);
        }

        // ── The real devmind_task_status / devmind_task_result payloads ──

        private readonly string _dir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;

        public SelfReportVerificationGateTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h31_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public async Task StatusAndResultPayloads_CarryTheNote_AndStateDone()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"), StrongAnswer);
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = new AgentJobManager();
            mgr.BuildRunnerOverride = (_, _) => Task.FromResult<BuildVerification?>(Run(true));
            mgr.TestRunnerOverride = (_, _) => Task.FromResult(Tests(true));

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: true, verifyTests: true,
                runTestBaseline: false);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 30_000 && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(20);
            Assert.Equal(AgentJobState.Done, job.State);
            Assert.True(job.Tests is { Succeeded: true }, "the harness test run did not happen");
            Assert.Contains("INCOMPLETE: the migration is not written", job.Result!.Answer);

            var tools = new AgentTaskTools(mgr);
            foreach (string json in new[]
                     {
                         await tools.TaskStatus(job.Id, null, CancellationToken.None),
                         await tools.TaskResult(job.Id, CancellationToken.None),
                     })
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                Assert.Equal("done", root.GetProperty("state").GetString());
                Assert.Equal(JsonValueKind.Null, root.GetProperty("incomplete_reasons").ValueKind);
                Assert.Equal("INCOMPLETE: the migration is not written", root.GetProperty("self_report_note").GetString());
            }
        }

        [Fact]
        public async Task StatusDuringTestVerification_IsRunning_ThenDoneWithTheNote()
        {
            // H-43: job-2118's status read stopped_incomplete with "[job] test verification:
            // running..." as the tail's last line. The verdict must wait for the test run, and the
            // tail must say when it finished.
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"), "Patched it.\n" + Job2118Line);
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            var testsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseTests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var mgr = new AgentJobManager();
            mgr.BuildRunnerOverride = (_, _) => Task.FromResult<BuildVerification?>(Run(true));
            mgr.TestRunnerOverride = async (_, _) =>
            {
                testsStarted.TrySetResult();
                await releaseTests.Task;
                return Tests(true);
            };

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: true, verifyTests: true,
                runTestBaseline: false);
            var tools = new AgentTaskTools(mgr);

            await testsStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using (var during = JsonDocument.Parse(await tools.TaskStatus(job.Id, null, CancellationToken.None)))
            {
                Assert.Equal("running", during.RootElement.GetProperty("state").GetString());
                Assert.Equal(JsonValueKind.Null, during.RootElement.GetProperty("incomplete_reasons").ValueKind);
                Assert.Equal(JsonValueKind.Null, during.RootElement.GetProperty("self_report_note").ValueKind);
            }

            releaseTests.SetResult();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 30_000 && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(20);

            using var after = JsonDocument.Parse(await tools.TaskStatus(job.Id, null, CancellationToken.None));
            Assert.Equal("done", after.RootElement.GetProperty("state").GetString());
            Assert.Equal(Job2118Line, after.RootElement.GetProperty("self_report_note").GetString());
            Assert.Contains("[job] test verification: passed (10 tests)", job.GetTail());
        }
    }
}
