// File: TestDiagnosticLogInitializer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Keeps a test run out of the operator's real diagnostic log.
//
// DevMindLog is deliberately ON by default — a log that is off when something breaks
// unattended is worth nothing. But this assembly drives the real engine: every
// LlmClient.Configure(), every NearlineCache spill, every PatchBackupSweeper pass now
// emits through it, and with no override that lands in the operator's real
// %APPDATA%\devmind\logs alongside the lines from their actual sessions. Retention
// bounds it, so it is not a leak — it is noise in the file someone reads when they are
// trying to work out what went wrong in production.
//
// Same hook and same reasoning as TestTasksDirInitializer in DevMind.McpServer.Tests: a
// [ModuleInitializer] is guaranteed to run before any type in the assembly loads, so the
// variable is set before the first test can log. Setting it from individual fixtures is
// racy and easy to miss.
//
// DevMindLogTests turns logging back on for itself (its constructor clears the variable)
// against a DEVMIND_GLOBAL_DIR temp directory, and restores this "off" on the way out.

using System.Runtime.CompilerServices;

namespace DevMind.Core.Tests
{
    internal static class TestDiagnosticLogInitializer
    {
        [ModuleInitializer]
        internal static void Initialize()
            => Environment.SetEnvironmentVariable("DEVMIND_LOG", "off");
    }
}
