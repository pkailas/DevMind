// File: TrainingCapture.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Global gate for training-corpus capture. Test assemblies set Disabled = true
// via [ModuleInitializer] so that NO test can write into a real configured
// corpus folder (e.g. G:\DevMind_Tracing) by accidentally picking up the
// operator's ambient %APPDATA%\devmind\devmind.json.
//
// This is an explicit, unambiguous opt-out — not a heuristic. It does not
// sniff for loopback endpoints or prompt content. Once set, it is permanent
// for the process lifetime.

namespace DevMind
{
    /// <summary>
    /// Process-wide gate for training-corpus capture in headless sessions.
    /// When <see cref="Disabled"/> is true, <c>HeadlessSession</c> will not
    /// construct a <see cref="JsonlTrainingLogger"/> regardless of the
    /// operator's devmind.json settings.
    /// </summary>
    public static class TrainingCapture
    {
        /// <summary>
        /// Set to true by test assemblies via <c>[ModuleInitializer]</c> to
        /// prevent any test from writing to a real corpus folder.
        /// Once true, never resets (process lifetime).
        /// </summary>
        public static bool Disabled { get; set; }
    }
}
