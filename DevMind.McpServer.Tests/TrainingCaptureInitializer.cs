// File: TrainingCaptureInitializer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Disables training-corpus capture for every test in this assembly.
// Runs once before any test (module initializer) — no per-test authoring
// required. This is the root fix for the 610-file corpus pollution:
// AgentJobManager tests construct real sessions with real job ids and the
// HeadlessSession constructor used to pick up the operator's ambient
// devmind.json (trainingLogEnabled true, folder G:\DevMind_Tracing) and
// write real training files.

using System.Runtime.CompilerServices;
using DevMind;

namespace DevMind.McpServer.Tests
{
    internal static class TrainingCaptureInitializer
    {
        [ModuleInitializer]
        internal static void Initialize() => TrainingCapture.Disabled = true;
    }
}
