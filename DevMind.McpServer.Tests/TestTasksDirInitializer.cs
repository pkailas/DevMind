// File: TestTasksDirInitializer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Redirects AgentJobManager.TranscriptDir for this test assembly (2026-08-18).
//
// This assembly spins REAL agent jobs through the REAL AgentJobManager against
// stub LLM servers (EditThenDoneLlmServer, FakeLlmServer). Without this, those
// jobs write their job-*.log transcripts, _active.json, _jobcounter.txt and
// .result.json sidecars into the GLOBAL %TEMP%\devmind\tasks folder — the same
// one the live devmind job runner writes to. Consequences: dm-watch (which
// follows the newest job-*.log by LastWriteTime) got yanked off the real job's
// 250KB+ transcript onto 0-334-byte test stubs, its BUSY header showed test
// job ids, and the global job-id counter burned ~340 ids/hour, mostly tests.
//
// A [ModuleInitializer] is the right hook: the C# compiler guarantees it runs
// before any type in the assembly is loaded, so DEVMIND_TASKS_DIR is set before
// any test touches TranscriptDir. Setting it from individual test constructors
// is racy and easy to miss — the first job in an assembly might already have
// written to the global folder before a later fixture set the var.
//
// The directory is unique per run (%TEMP%\devmind-test-tasks\<guid>\) so
// concurrent test runs cannot collide, and cleanup is deliberately skipped —
// it is a small dir under %TEMP% and Windows reaps temp dirs; building a
// custom fixture/lifecycle just to delete it is not worth it.

using System.Runtime.CompilerServices;

namespace DevMind.McpServer.Tests
{
    internal static class TestTasksDirInitializer
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            string dir = Path.Combine(Path.GetTempPath(), "devmind-test-tasks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", dir);
        }
    }
}
