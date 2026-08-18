// File: TranscriptDirOverrideTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests pinning AgentJobManager.TranscriptDir's DEVMIND_TASKS_DIR
// override (added 2026-08-18 so the test suite stops writing stub-job
// transcripts into the GLOBAL %TEMP%\devmind\tasks folder — see
// TestTasksDirInitializer.cs for the full story: dm-watch got yanked off the
// live job's transcript onto test stubs, its BUSY header showed test job ids,
// and the global job-id counter burned ~340 ids/hour, mostly tests).
//
// Three behaviours are pinned:
//   * When DEVMIND_TASKS_DIR is set, TranscriptDir returns its value (NOT the
//     default) — the override that makes the tests hermetic. Re-evaluated per
//     access (a second set is picked up without caching).
//   * When it is unset, TranscriptDir falls back to the production default
//     %TEMP%\devmind\tasks — so production behaviour is unchanged. Whitespace-
//     only counts as unset (a blank path would be meaningless).
//   * The per-run dir the ModuleInitializer set actually receives the stub
//     job artifacts: after the suite runs, it holds job-*.log transcripts and
//     a _jobcounter.txt, and the GLOBAL %TEMP%\devmind\tasks folder's
//     _jobcounter.txt is NOT the one the jobs bumped — i.e. the artifacts were
//     REDIRECTED, not merely not created.
//
// The env var is saved and restored around each test (same pattern as
// NoExecuteInheritanceTests for DEVMIND_ENDPOINT / DEVMIND_SERVER_TYPE) so it
// cannot leak into other tests. NOTE: this assembly's TestTasksDirInitializer
// [ModuleInitializer] has already set the var for the whole test run — that is
// exactly the value we save here; the "unset" case clears it for the duration
// of the assertion, then restores the per-run value.

using System.Threading.Tasks;
using Xunit;
// Note: FakeLlmServer (internal) lives in NoExecuteInheritanceTests.cs — same
// assembly, so the stub-job setup below can reuse it directly.

namespace DevMind.McpServer.Tests
{
    public sealed class TranscriptDirOverrideTests : IDisposable
    {
        private readonly string? _prior;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;

        public TranscriptDirOverrideTests()
        {
            _prior = Environment.GetEnvironmentVariable("DEVMIND_TASKS_DIR");
            // AgentJobManager reads the endpoint at construction; LlmClient reads
            // DEVMIND_SERVER_TYPE per request. Saved/restored around each test the
            // same way NoExecuteInheritanceTests does.
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
        }

        public void Dispose()
        {
            // Restore exactly what this assembly's ModuleInitializer set (or null
            // if it had not — defensive; the initializer always sets it).
            if (_prior == null)
                Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", null);
            else
                Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _prior);
            if (_priorEndpoint == null)
                Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", null);
            else
                Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            if (_priorServerType == null)
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", null);
            else
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
        }

        [Fact]
        public void TranscriptDir_ReturnsOverride_WhenSet()
        {
            string custom = Path.Combine(Path.GetTempPath(), "devmind-test-tasks", "override-verify");
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", custom);
            try
            {
                // The override is returned verbatim — the test suite relies on this
                // to redirect stub-job artifacts to a private per-run folder.
                Assert.Equal(custom, AgentJobManager.TranscriptDir);

                // Re-evaluated per access: a SECOND change must be picked up without
                // any caching (the property reads the env var on every access).
                string custom2 = Path.Combine(Path.GetTempPath(), "devmind-test-tasks", "override-verify-2");
                Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", custom2);
                Assert.Equal(custom2, AgentJobManager.TranscriptDir);
            }
            finally
            {
                if (_prior == null)
                    Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", null);
                else
                    Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _prior);
            }
        }

        [Fact]
        public void TranscriptDir_ReturnsDefault_WhenUnset()
        {
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", null);
            try
            {
                // The production default, unchanged: %TEMP%\devmind\tasks.
                string expected = Path.Combine(Path.GetTempPath(), "devmind", "tasks");
                Assert.Equal(expected, AgentJobManager.TranscriptDir);
            }
            finally
            {
                if (_prior == null)
                    Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", null);
                else
                    Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _prior);
            }
        }

        [Fact]
        public void TranscriptDir_TreatsWhitespaceOnlyAsUnset()
        {
            // A blank value must NOT be treated as an override — it would point
            // transcripts at an empty path. Same guard as the property's
            // IsNullOrWhiteSpace check.
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", "   ");
            try
            {
                string expected = Path.Combine(Path.GetTempPath(), "devmind", "tasks");
                Assert.Equal(expected, AgentJobManager.TranscriptDir);
            }
            finally
            {
                if (_prior == null)
                    Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", null);
                else
                    Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _prior);
            }
        }

        [Fact]
        public async Task StubJobs_WroteArtifactsToPrivateDir_NotTheGlobalFolder()
        {
            // The acceptance check that the override is not vacuous: a stub job
            // driven through this process's AgentJobManager MUST have written its
            // transcript and id counter into the per-run dir (DEVMIND_TASKS_DIR),
            // not into %TEMP%\devmind\tasks.
            //
            // SELF-CONTAINED (fixed 2026-08-18): the earlier version of this test
            // only asserted that the per-run dir ALREADY held job-*.log and
            // _jobcounter.txt — artifacts produced by OTHER stub-job tests in this
            // assembly (AgentJobVerificationRaceTests, TestCountDeltaTests,
            // NoExecuteInheritanceTests). The claim in its old comment that it
            // "does not depend on ordering between methods" was wrong: this
            // assembly runs serially (xunit.runner.json parallelizeTestCollections
            // false), so whenever the xUnit scheduler happened to place this test
            // BEFORE the first stub-job test, the files did not exist yet and the
            // test failed. It was a test-ordering dependency, not a parallel race.
            // The fix: spawn the job HERE, in this test, and assert on ITS
            // artifacts — the assertion is unchanged in strength, but the
            // precondition no longer requires a sibling test to have run first.
            string? envDir = Environment.GetEnvironmentVariable("DEVMIND_TASKS_DIR");
            Assert.False(string.IsNullOrWhiteSpace(envDir),
                "DEVMIND_TASKS_DIR should be set by TestTasksDirInitializer for the whole run");
            string perRun = envDir!; // non-null, per the assert above

            // ── Drive the code path that writes the artifacts ────────────────
            // One real job against the stub LLM server (task_done immediately —
            // nothing is executed, nothing can hang). Start() allocates the id
            // through NextJobId (persists _jobcounter.txt into TranscriptDir);
            // the worker writes the transcript (job-*.log) into TranscriptDir.
            using var server = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            // Force the llama request template so the local stub server speaks the
            // same shape (same convention NoExecuteInheritanceTests uses).
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            string workDir = Path.Combine(Path.GetTempPath(), $"devmind_transcriptdir_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);
            using var mgr = new AgentJobManager();
            var job = mgr.Start("transcript dir self-test", workDir, 5, 1,
                allowCommit: false, verifyBuild: false, verifyTests: false);

            // The job must finish — its id allocation and transcript write happen
            // before the state flips out of Running, so once it is Done the
            // artifacts are on disk. (The stub answers with a single tool call +
            // task_done, so this is a single fast round-trip.)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 20000
                   && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(50);
            Assert.Equal(AgentJobState.Done, job.State);

            // The job's own id counter write must have hit the PER-RUN dir.
            string perRunCounter = Path.Combine(perRun, "_jobcounter.txt");
            Assert.True(File.Exists(perRunCounter),
                $"expected _jobcounter.txt in the per-run test dir {perRun} — " +
                "the job's id allocation did not write there; the override is not redirecting");
            int counter = int.Parse(File.ReadAllText(perRunCounter).Trim());
            Assert.True(counter > 0,
                $"per-run _jobcounter.txt should hold a positive count (got {counter})");

            // Transcripts: THIS job's job-*.log must have landed in the per-run dir.
            // (The stub LLM server answers with a single tool call + task_done, so a
            // job's transcript is small — but it IS written, which is what the
            // global-folder monitoring tooling relies on to follow a job.)
            var logs = new DirectoryInfo(perRun).GetFiles($"{job.Id}-*.log");
            Assert.True(logs.Length > 0,
                $"expected job {job.Id}'s transcript (job-*.log) in the per-run test dir {perRun} — " +
                "the transcript did not land there; the override is not redirecting");

            // And the GLOBAL folder must NOT have received THIS job's artifacts:
            // its _jobcounter.txt is the production counter (or absent in a fresh
            // environment) — either way, the job we just ran bumped the PER-RUN
            // counter, not this one, and wrote its transcript to the per-run dir,
            // not this folder.
            string globalDir = Path.Combine(Path.GetTempPath(), "devmind", "tasks");
            string globalCounter = Path.Combine(globalDir, "_jobcounter.txt");
            if (File.Exists(globalCounter))
            {
                int globalCounterVal = int.TryParse(File.ReadAllText(globalCounter).Trim(), out int g) ? g : -1;
                Assert.True(counter != globalCounterVal,
                    $"the per-run and global counters must be independent (per-run={counter}, " +
                    $"global={globalCounterVal}) — if they agree, the jobs wrote to the GLOBAL folder");
            }
            Assert.False(Directory.Exists(globalDir) &&
                         new DirectoryInfo(globalDir).GetFiles($"{job.Id}-*.log").Length > 0,
                $"job {job.Id}'s transcript must NOT have been written to the GLOBAL folder {globalDir}");
        }
    }
}
