// File: SelfReportVerificationGateTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-31: a phrase-list hit in the final answer is weak evidence; green harness verification
// outweighs it. job-1714 ("The full solution suite was not run by me — the harness verifies
// it.", 1763/1763 green) and job-1715 ("- Did not run the TUI; did not commit." — both
// forbidden by the brief; build + 1849/1849 green) ended stopped_incomplete on the phrase alone.
//
// The rule, as a table:
//   STRONG (INCOMPLETE: marker)   → stopped_incomplete, whatever the harness measured
//   WEAK   (phrase hit only)      → done + self_report_note when the harness build passed AND
//                                   (tests not requested OR test verification passed);
//                                   stopped_incomplete otherwise — including verify_build off,
//                                   the H-01 shape the phrase list exists for
//   NONE                          → unaffected

using System.Diagnostics;
using System.Text.Json;
using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class SelfReportVerificationGateTests : IDisposable
    {
        private const string StrongAnswer = "Patched the parser.\nINCOMPLETE: the migration is not written";
        private const string WeakAnswer = "Patched the parser.\nThe core defect is NOT fixed yet.";
        private const string NoneAnswer = "Patched the parser. Build 0/0, suite green.";

        // Verbatim final-answer lines from the two H-31 jobs.
        private const string Job1714Answer =
            "Added ResolveTimeout(command, explicit) and IsLongRunningCommand with a table test.\n" +
            "The full solution suite was not run by me — the harness verifies it.";
        private const string Job1715Answer =
            "Added the H-31 detector split and the job-state table.\n" +
            "- Did not run the TUI; did not commit.";

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
            "weak" => WeakAnswer,
            "none" => NoneAnswer,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        [Theory]
        // strong: always incomplete
        [InlineData("strong", "build-ok+tests-ok", true)]
        [InlineData("strong", "build-ok+tests-not-requested", true)]
        [InlineData("strong", "build-failed", true)]
        [InlineData("strong", "build-not-run", true)]
        [InlineData("strong", "tests-failed", true)]
        // weak: incomplete only when the harness did not positively verify
        [InlineData("weak", "build-ok+tests-ok", false)]
        [InlineData("weak", "build-ok+tests-not-requested", false)]
        [InlineData("weak", "build-failed", true)]
        [InlineData("weak", "build-not-run", true)]
        [InlineData("weak", "tests-failed", true)]
        [InlineData("weak", "tests-requested-not-run", true)]
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

            bool selfReasonExpected = selfReport == "strong" || (selfReport == "weak" && expectIncomplete);
            Assert.Equal(selfReasonExpected, reasons.Contains(SelfReportedIncompleteDetector.Reason));

            // The weak line is never lost: it is either an incomplete reason or the note.
            bool noteExpected = selfReport == "weak" && !expectIncomplete;
            Assert.Equal(noteExpected, job.SelfReportNote != null);
            if (selfReport == "weak")
                Assert.True(noteExpected
                    ? job.SelfReportNote == "The core defect is NOT fixed yet."
                    : reasons.Contains("The core defect is NOT fixed yet."));
        }

        [Fact]
        public void Job1714_VerifiedGreen_EndsDone_WithTheLineAsANote()
        {
            var job = Job(Job1714Answer, "build-ok+tests-ok");

            Assert.False(job.IsIncomplete);
            Assert.Empty(job.IncompleteReasons());
            Assert.Equal("The full solution suite was not run by me — the harness verifies it.", job.SelfReportNote);
        }

        [Fact]
        public void Job1715_VerifiedGreen_EndsDone_WithTheLineAsANote()
        {
            var job = Job(Job1715Answer, "build-ok+tests-ok");

            Assert.False(job.IsIncomplete);
            Assert.Empty(job.IncompleteReasons());
            Assert.Equal("- Did not run the TUI; did not commit.", job.SelfReportNote);
        }

        [Fact]
        public void H01Shape_NothingVerified_StillEndsStoppedIncomplete()
        {
            // The H-01 jobs ran with verify_build off: the phrase list is the only signal.
            var job = Job("The core defect is NOT fixed — the caller must not report LT-04 as fixed.", "build-not-run");

            Assert.True(job.IsIncomplete);
            Assert.Contains(SelfReportedIncompleteDetector.Reason, job.IncompleteReasons());
            Assert.Null(job.SelfReportNote);
        }

        [Fact]
        public void APhraseAboveAMarker_IsStillStrong_EvenWhenVerifiedGreen()
        {
            var job = Job("- Did not run the TUI.\nINCOMPLETE: the migration is not written", "build-ok+tests-ok");

            Assert.True(job.IsIncomplete);
            Assert.Contains("INCOMPLETE: the migration is not written", job.IncompleteReasons());
            Assert.Null(job.SelfReportNote);
        }

        [Fact]
        public void ANonDoneJob_HasNoNote()
        {
            var job = Job(Job1715Answer, "build-ok+tests-ok");
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
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"), Job1715Answer);
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = new AgentJobManager();
            mgr.BuildRunnerOverride = (_, _) => Task.FromResult<BuildVerification?>(Run(true));

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: true, verifyTests: false);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 30_000 && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(20);
            Assert.Equal(AgentJobState.Done, job.State);
            Assert.Contains("Did not run the TUI", job.Result!.Answer);

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
                Assert.Equal("- Did not run the TUI; did not commit.", root.GetProperty("self_report_note").GetString());
            }
        }
    }
}
