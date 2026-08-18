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

using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class TranscriptDirOverrideTests : IDisposable
    {
        private readonly string? _prior;

        public TranscriptDirOverrideTests()
        {
            _prior = Environment.GetEnvironmentVariable("DEVMIND_TASKS_DIR");
        }

        public void Dispose()
        {
            // Restore exactly what this assembly's ModuleInitializer set (or null
            // if it had not — defensive; the initializer always sets it).
            if (_prior == null)
                Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", null);
            else
                Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _prior);
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
        public void StubJobs_WroteArtifactsToPrivateDir_NotTheGlobalFolder()
        {
            // The acceptance check that the override is not vacuous: the stub jobs
            // this assembly ran (AgentJobVerificationRaceTests, TestCountDeltaTests,
            // NoExecuteInheritanceTests) MUST have written their transcripts and id
            // counter into the per-run dir, not into %TEMP%\devmind\tasks.
            //
            // xunit runs test methods in an assembly in parallel by default; the
            // per-run dir was created by the ModuleInitializer BEFORE any of them
            // started, and this test only asserts on the state of the directory at
            // read time — it does not depend on ordering between methods, so it is
            // safe regardless of which stub-job tests have already completed.
            string? perRun = Environment.GetEnvironmentVariable("DEVMIND_TASKS_DIR");
            Assert.False(string.IsNullOrWhiteSpace(perRun),
                "DEVMIND_TASKS_DIR should be set by TestTasksDirInitializer for the whole run");

            // The stub jobs allocated real ids through NextJobId, which persists the
            // counter — so the per-run dir must hold a _jobcounter.txt.
            string perRunCounter = Path.Combine(perRun!, "_jobcounter.txt");
            Assert.True(File.Exists(perRunCounter),
                $"expected _jobcounter.txt in the per-run test dir {perRun} — " +
                "stub jobs did not write there; the override is not redirecting");
            int counter = int.Parse(File.ReadAllText(perRunCounter).Trim());
            Assert.True(counter > 0,
                $"per-run _jobcounter.txt should hold a positive count (got {counter})");

            // Transcripts: at least one job-*.log must have landed in the per-run dir.
            // (The stub LLM servers answer with a single tool call + task_done, so a
            // job's transcript is small — but it IS written, which is what the
            // global-folder monitoring tooling relies on to follow a job.)
            var logs = new DirectoryInfo(perRun!).GetFiles("job-*.log");
            Assert.True(logs.Length > 0,
                $"expected at least one job-*.log in the per-run test dir {perRun} — " +
                "stub job transcripts did not land there; the override is not redirecting");

            // And the GLOBAL folder must NOT have been written to by THIS run: its
            // _jobcounter.txt is the production counter (or absent in a fresh
            // environment) — either way, the jobs we just ran bumped the PER-RUN
            // counter, not this one.
            string globalDir = Path.Combine(Path.GetTempPath(), "devmind", "tasks");
            string globalCounter = Path.Combine(globalDir, "_jobcounter.txt");
            if (File.Exists(globalCounter))
            {
                int globalCounterVal = int.TryParse(File.ReadAllText(globalCounter).Trim(), out int g) ? g : -1;
                Assert.True(counter != globalCounterVal,
                    $"the per-run and global counters must be independent (per-run={counter}, " +
                    $"global={globalCounterVal}) — if they agree, the jobs wrote to the GLOBAL folder");
            }
        }
    }
}
