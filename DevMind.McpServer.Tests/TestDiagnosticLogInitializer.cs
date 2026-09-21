// File: TestDiagnosticLogInitializer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Keeps a test run out of the operator's real diagnostic log.
//
// DevMindLog is deliberately ON by default — a log that is off when something breaks
// unattended is worth nothing. But this assembly spins REAL agent jobs through the REAL
// AgentJobManager, so every LlmClient.Configure() and every swallowed failure inside them
// now emits through it, and with no override that lands in the operator's real
// %APPDATA%\devmind\logs alongside the lines from their actual sessions. Retention bounds
// it, so it is not a leak — it is noise in the file someone reads when they are trying to
// work out what went wrong in production.
//
// Same hook and same reasoning as TestTasksDirInitializer beside it: a [ModuleInitializer]
// is guaranteed to run before any type in the assembly loads, so the variable is set before
// the first job can log.

using System.Runtime.CompilerServices;

namespace DevMind.McpServer.Tests
{
    internal static class TestDiagnosticLogInitializer
    {
        [ModuleInitializer]
        internal static void Initialize()
            => Environment.SetEnvironmentVariable("DEVMIND_LOG", "off");
    }
}
