// File: AgentJobVerificationRaceTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for the in-memory devmind_task_result race that the 22 test-count
// tests could not catch: the worker used to publish State = Done the instant the
// turn ended, THEN run build/test verification. devmind_task_result serves the
// in-memory job the moment State is no longer Queued/Running, so a concurrent reader
// could observe a Done job whose test_verification was still null (build populated,
// tests not yet run) — the exact shape a live delegation reported.
//
// The fix holds the job in Running through verification and publishes the terminal
// state only once build+test have settled. The invariant under test:
//   A job observed as Done never carries a verification field that is still going
//   to change. If verify_build/verify_tests was requested, the corresponding field
//   is populated (or explicitly null with a stated reason) before the caller sees Done.
//
// These two tests are the ones that were missing from the suite:
//   1. A CONCURRENT reader — models what devmind_task_result actually does (read the
//      job at an arbitrary moment), with a real Task.Delay so the verification window
//      has non-zero measure. The old end-to-end tests only waited for Done and asserted
//      final fields, so they collapsed the window to microseconds and never landed in it.
//   2. An end-to-end run with verify_build TRUE — the old tests all set it false, which
//      is precisely why the caller-visible shape (build populated, tests null) could not
//      occur in the suite.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class AgentJobVerificationRaceTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;

        public AgentJobVerificationRaceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_verifyrace_job_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            // llama request template — the shape EditThenDoneLlmServer speaks.
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
        }

        public void Dispose()
        {
            if (_priorEndpoint == null) Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", null);
            else Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            if (_priorServerType == null) Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", null);
            else Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        // ── 1. The concurrent reader ─────────────────────────────────────────
        // Models devmind_task_result faithfully: it SERVES the job the moment State
        // is no longer Queued/Running. We run that reader on a separate task while the
        // worker is mid-verification (the after-run test takes a real delay, so the
        // window has non-zero measure — the shape that let a live caller see a Done
        // job with test_verification still null). The invariant: whenever the reader
        // could have served a Done job that requested test verification, job.Tests must
        // NOT be null. The pre-fix code published Done ~400ms before Tests was set, so
        // this would have registered a violation; the fix publishes Done only after.
        [Fact]
        public async Task DoneJob_NeverServesTestVerificationNull_MidVerification()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = new AgentJobManager();
            // After-run test suite takes real time so the verification window is wide
            // enough for a concurrent reader to land in it. (runTestBaseline:false so
            // there is exactly one suite run — the one the reader must never see as
            // "Done with Tests==null".)
            mgr.TestRunnerOverride = async (_, _) =>
            {
                await Task.Delay(400);
                return TestVerification.FromRun("dotnet test", 0,
                    "Test run for X\n" + CannedTestOutput.McpPass37 + "\n");
            };

            var job = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false, verifyTests: true, runTestBaseline: false);

            // A concurrent devmind_task_result reader: poll aggressively, and at every
            // moment the job is servable (not Queued/Running) check the invariant.
            var violations = new ConcurrentBag<string>();
            var reader = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 30000)
                {
                    var state = job.State;
                    if (state is not (AgentJobState.Queued or AgentJobState.Running))
                    {
                        if (state == AgentJobState.Done && job.VerifyTests && job.Tests == null)
                            violations.Add($"Done observed at {sw.ElapsedMilliseconds}ms with Tests==null");
                        // Terminal: one confirming re-read (the job is final, so a second
                        // read must agree), then stop — never run the full 30s.
                        if (state is AgentJobState.Done or AgentJobState.Failed or AgentJobState.Cancelled)
                        {
                            await Task.Delay(50);
                            var state2 = job.State;
                            if (state2 == AgentJobState.Done && job.VerifyTests && job.Tests == null)
                                violations.Add($"Done re-observed at {sw.ElapsedMilliseconds}ms with Tests==null");
                            break;
                        }
                    }
                    await Task.Delay(5);
                }
            });

            await WaitForDone(job);
            await reader;

            Assert.Equal(AgentJobState.Done, job.State);
            Assert.Empty(violations); // the invariant held at every serve-opportunity
            Assert.NotNull(job.Tests);
            Assert.Equal(37, job.Tests.Total);
        }

        // ── 2. End-to-end with verify_build TRUE ─────────────────────────────
        // The three existing end-to-end tests all set verifyBuild:false, which is
        // precisely why the caller-visible shape (build populated, tests null) could
        // not occur in the suite. This one turns it ON: the build verification runs
        // first and must settle before the after-run test verification runs, and both
        // fields must be present on the Done job with the delta intact.
        [Fact]
        public async Task VerifyBuildTrue_BuildSettlesBeforeTests_AndBothAreFinal()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            var order = new List<string>();
            using var mgr = new AgentJobManager();
            // Build verification (seam): records its call and succeeds.
            mgr.BuildRunnerOverride = async (_, _) =>
            {
                order.Add("build");
                await Task.Delay(100);
                return new BuildVerification
                {
                    Command = "dotnet build",
                    ExitCode = 0,
                    OutputTail = "Build succeeded.",
                };
            };
            // Test suite (seam): baseline reports 37, after-run reports 38.
            int calls = 0;
            mgr.TestRunnerOverride = async (_, _) =>
            {
                calls++;
                order.Add("test");
                await Task.Delay(100);
                string summary = calls == 1
                    ? CannedTestOutput.McpPass37
                    : "Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 4 s - DevMind.McpServer.Tests.dll (net10.0)";
                return TestVerification.FromRun("dotnet test", 0, "Test run for X\n" + summary + "\n");
            };

            var job = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: true, verifyTests: true); // baseline on

            await WaitForDone(job);

            // Both verification fields are present and final on the Done job.
            Assert.NotNull(job.Build);
            Assert.True(job.Build.Succeeded);
            Assert.NotNull(job.Tests);
            Assert.NotNull(job.BaselineTests);
            Assert.Equal(37, job.BaselineTests.Total);
            Assert.Equal(38, job.Tests.Total);

            // Ordering: exactly one build, two tests (baseline + after). The build must
            // settle BETWEEN them — after the baseline (which runs before the agent) and
            // before the after-run test.
            Assert.Equal(1, order.Count(s => s == "build"));
            Assert.Equal(2, order.Count(s => s == "test"));
            int buildIdx = order.IndexOf("build");
            int firstTestIdx = order.IndexOf("test");
            int lastTestIdx = order.LastIndexOf("test");
            Assert.True(firstTestIdx < buildIdx, $"baseline test must precede build (order: {string.Join(",", order)})");
            Assert.True(lastTestIdx > buildIdx, $"after-run test must follow build (order: {string.Join(",", order)})");

            // The delta is intact: 37 -> 38 = +1.
            var p = JsonSerializer.SerializeToElement(TestVerificationPayload.Create(job)!);
            Assert.Equal(37, p.GetProperty("baseline_total").GetInt32());
            Assert.Equal(38, p.GetProperty("total").GetInt32());
            Assert.Equal(1, p.GetProperty("delta").GetInt32());
        }

        // ── 3. The warning-count honesty fix ─────────────────────────────────
        // The post-run build is incremental, so up-to-date projects do not re-emit their
        // warnings and the "0 Warning(s)" in the summary is meaningless (a hand -t:Rebuild on
        // the same tree reports dozens). Rather than pay a full rebuild per job to make the
        // count real, the build_verification payload now labels the count as not-verified so a
        // reader cannot mistake the tail for a warning check that actually happened. This drives
        // the REAL devmind_task_result serialization path and asserts the label is present and
        // false while the error/exit-code result (succeeded) is still reported.
        [Fact]
        public async Task VerifyBuildTrue_ResultPayload_LabelsWarningCountUnverified()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = new AgentJobManager();
            // Simulate the misleading incremental output: passing exit code + "0 Warning(s)"
            // summary, even though the tree genuinely has warnings.
            mgr.BuildRunnerOverride = async (_, _) =>
            {
                await Task.Delay(50);
                return new BuildVerification
                {
                    Command = "dotnet build \"DevMind.slnx\"",
                    ExitCode = 0,
                    OutputTail = "Build succeeded.\n    0 Warning(s)\n    0 Error(s)",
                };
            };

            var job = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: true, verifyTests: false);

            await WaitForDone(job);
            Assert.NotNull(job.Build);
            Assert.True(job.Build.Succeeded);

            // Exercise the real devmind_task_result serialization path.
            var tools = new AgentTaskTools(mgr);
            string json = await tools.TaskResult(job.Id, CancellationToken.None);
            using var doc = JsonDocument.Parse(json);
            var bv = doc.RootElement.GetProperty("build_verification");

            // Error/exit-code result is still reported and is the reliable signal.
            Assert.True(bv.GetProperty("succeeded").GetBoolean());
            Assert.Equal(0, bv.GetProperty("exit_code").GetInt32());
            // The raw tail is preserved (real warnings it did emit are not discarded)…
            Assert.Contains("0 Warning(s)", bv.GetProperty("output_tail").GetString());
            // …but the count is explicitly labeled NOT verified — nothing implies a warning
            // check happened when it did not.
            Assert.False(bv.GetProperty("warning_count_verified").GetBoolean());
        }

        // Polls until the job leaves Queued/Running. With the fix, returning here means
        // verification is FINAL (not merely "the turn ended"), so the final-field
        // assertions below it are meaningful — they read a Done job that is already
        // fully settled.
        private static async Task<AgentJob> WaitForDone(AgentJob job, int timeoutMs = 30000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs
                   && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(20);
            Assert.Equal(AgentJobState.Done, job.State);
            return job;
        }
    }
}
