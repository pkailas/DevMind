// File: TrainingCaptureInitializer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Disables training-corpus capture for every test in this assembly.
// Runs once before any test (module initializer) — no per-test authoring
// required. Prevents a test from writing into the operator's real configured
// corpus folder by picking up the ambient %APPDATA%\devmind\devmind.json.

using System.Runtime.CompilerServices;
using DevMind;

namespace DevMind.Core.Tests
{
    internal static class TrainingCaptureInitializer
    {
        [ModuleInitializer]
        internal static void Initialize() => TrainingCapture.Disabled = true;
    }
}
