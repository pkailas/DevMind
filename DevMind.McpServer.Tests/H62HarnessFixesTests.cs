// File: H62HarnessFixesTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-62, the McpServer-side items:
//   * a job's harness steps (baseline build, baseline `dotnet test`, after-run) leave no
//     file named null / nul in the working directory — through the TestRunnerOverride seam
//     AND through the real ShellRunner path (job-2167's stray "null" turned out to be the
//     agent's own `> null`, fixed in ShellRunner.RewriteNullRedirects; this pins that the
//     harness never adds one);
//   * the result sidecar persists think and reasoning_effort, so devmind_task_result
//     served from disk matches the live result;
//   * failed_tests_truncated / baseline_failed_tests_truncated (follow-up to H-61).

using System.Text.Json;
using Xunit;

namespace DevMind.McpServer.Tests
{
    [Collection(ProcessEnvironmentCollection.Name)]
    public sealed class H62HarnessFixesTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _work;
        private readonly string _tasksDir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;
        private readonly string? _priorTasksDir;

        public H62HarnessFixesTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h62_{Guid.NewGuid():N}");
            _work = Path.Combine(_dir, "work");
            _tasksDir = Path.Combine(_dir, "tasks");
            Directory.CreateDirectory(_work);
            Directory.CreateDirectory(_tasksDir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            _priorTasksDir = Environment.GetEnvironmentVariable("DEVMIND_TASKS_DIR");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _tasksDir);
        }

        public void Dispose()
        {
            // Restore, never clear (see ResultSidecarTokenUsageTests).
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _priorTasksDir);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static async Task WaitForEnd(AgentJob job, int timeoutMs = 120_000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(50);
            Assert.Equal(AgentJobState.Done, job.State);
        }

        // Lists the directory rather than File.Exists: "nul" is a device name on Windows.
        private void AssertNoNullFiles()
        {
            Assert.DoesNotContain(Directory.GetFileSystemEntries(_work).Select(Path.GetFileName),
                n => n!.Equals("null", StringComparison.OrdinalIgnoreCase) || n.Equals("nul", StringComparison.OrdinalIgnoreCase));
        }

        // ── 2. no stray null / nul from the harness's own steps ─────────────────

        [Fact]
        public async Task BaselineAndAfterRun_ThroughTheOverride_LeaveNoNullFile()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_work, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            int calls = 0;
            using var mgr = new AgentJobManager();
            mgr.TestRunnerOverride = (_, _) =>
            {
                calls++;
                return Task.FromResult(TestVerification.FromRun("dotnet test", 0, FailedTestFixtures.PassingOutput));
            };

            var job = mgr.Start("p", _work, 5, 30, allowCommit: false, verifyBuild: false, verifyTests: true);
            await WaitForEnd(job);
            Assert.Equal(2, calls);
            AssertNoNullFiles();
        }

        [Fact]
        public async Task BaselineAndAfterRun_ThroughTheRealShellRunner_LeaveNoNullFile()
        {
            // No override: the baseline build resolver, the baseline `dotnet test` and the
            // after-run all go through the production ShellRunner path. An empty working
            // dir makes `dotnet test` fail fast (no project) — the runs still happen.
            using var server = new EditThenDoneLlmServer(Path.Combine(_work, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = new AgentJobManager();
            var job = mgr.Start("p", _work, 5, 30, allowCommit: false, verifyBuild: false, verifyTests: true);
            await WaitForEnd(job);

            Assert.NotNull(job.BaselineTests);
            Assert.NotNull(job.Tests);
            AssertNoNullFiles();
        }

        // ── 3. think / reasoning_effort persisted in the sidecar ────────────────

        [Fact]
        public async Task Sidecar_PersistsThinkAndReasoningEffort_ServedByTaskResult()
        {
            var job = new AgentJob
            {
                Id = "job-h62think",
                Prompt = "p",
                WorkingDirectory = @"C:\temp\hermetic",
                State = AgentJobState.Done,
                Think = true,
                ReasoningEffort = "medium",
                Result = new HeadlessAgentResult { Answer = "done" },
            };
            AgentJobManager.WriteResultSidecar(job);

            // The job is not in this manager, so devmind_task_result serves the sidecar.
            using var mgr = new AgentJobManager();
            using var doc = JsonDocument.Parse(await new AgentTaskTools(mgr).TaskResult(job.Id, CancellationToken.None));
            var result = doc.RootElement.GetProperty("result");
            Assert.True(result.GetProperty("think").GetBoolean());
            Assert.Equal("medium", result.GetProperty("reasoning_effort").GetString());
        }

        [Fact]
        public void Sidecar_ThinkingOff_ThinkFalseEffortNull()
        {
            var job = new AgentJob
            {
                Id = "job-h62nothink",
                Prompt = "p",
                WorkingDirectory = @"C:\temp\hermetic",
                State = AgentJobState.Done,
                Result = new HeadlessAgentResult { Answer = "done" },
            };
            AgentJobManager.WriteResultSidecar(job);

            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(AgentJobManager.TranscriptDir, "job-h62nothink.result.json")));
            Assert.False(doc.RootElement.GetProperty("think").GetBoolean());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("reasoning_effort").ValueKind);
        }

        // ── 4. truncation counts ────────────────────────────────────────────────

        [Fact]
        public void TruncationCounts_ThirtyFailures_FiveCut_InPayloadAndSidecar()
        {
            var job = new AgentJob
            {
                Id = "job-h62trunc",
                Prompt = "p",
                WorkingDirectory = @"C:\temp\hermetic",
                State = AgentJobState.Done,
                Tests = TestVerification.FromRun("dotnet test", 1, FailedTestFixtures.Thirty),
                BaselineTests = TestVerification.FromRun("dotnet test", 1, FailedTestFixtures.Thirty),
                Result = new HeadlessAgentResult { Answer = "done" },
            };

            var p = JsonSerializer.SerializeToElement(TestVerificationPayload.Create(job)!);
            Assert.Equal(5, p.GetProperty("failed_tests_truncated").GetInt32());
            Assert.Equal(5, p.GetProperty("baseline_failed_tests_truncated").GetInt32());

            AgentJobManager.WriteResultSidecar(job);
            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(AgentJobManager.TranscriptDir, "job-h62trunc.result.json")));
            var tv = doc.RootElement.GetProperty("test_verification");
            Assert.Equal(5, tv.GetProperty("failed_tests_truncated").GetInt32());
            Assert.Equal(5, tv.GetProperty("baseline_failed_tests_truncated").GetInt32());
        }

        [Fact]
        public void TruncationCounts_NothingCut_ZeroAndNoBaselineNull()
        {
            var job = new AgentJob
            {
                Id = "job-h62trunc0",
                Prompt = "p",
                WorkingDirectory = @"C:\temp\hermetic",
                State = AgentJobState.Done,
                Tests = TestVerification.FromRun("dotnet test", 1, FailedTestFixtures.Single),
            };
            var p = JsonSerializer.SerializeToElement(TestVerificationPayload.Create(job)!);
            Assert.Equal(0, p.GetProperty("failed_tests_truncated").GetInt32());
            Assert.Equal(JsonValueKind.Null, p.GetProperty("baseline_failed_tests_truncated").ValueKind);
        }
    }
}
