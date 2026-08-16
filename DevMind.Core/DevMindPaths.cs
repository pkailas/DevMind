// File: DevMindPaths.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Single source of truth for DevMind's machine-level (per-operating-user) state
// directories under %APPDATA%\devmind\ (ApplicationData). Every consumer of the
// global config (TuiConfig), the global memory layer (MemoryManager), or any
// other global state MUST derive its path from here so the location can never
// drift between consumers.

using System;
using System.IO;

namespace DevMind
{
    public static class DevMindPaths
    {
        /// <summary>
        /// Name of the per-user DevMind state directory under ApplicationData
        /// (%APPDATA% on Windows).
        /// </summary>
        public const string ConfigDirName = "devmind";

        /// <summary>
        /// Name of the directory holding the machine-level (global) memory layer
        /// — topic files shared across every repo this operator works in.
        /// </summary>
        public const string MemoryDirName = "memory";

        /// <summary>
        /// Absolute path to the per-user DevMind state directory
        /// (%APPDATA%\devmind). This is the same directory that holds
        /// devmind.json; it is intentionally NOT created here — callers decide
        /// when (and if) their own subtree needs to exist.
        /// </summary>
        public static string GlobalDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ConfigDirName);

        /// <summary>
        /// Absolute path to the machine-level memory directory
        /// (%APPDATA%\devmind\memory). An absent directory is a normal state
        /// (feature degrades to repo-only memory) — readers must treat it as an
        /// empty layer, never as an error.
        /// </summary>
        public static string GlobalMemoryDir =>
            Path.Combine(GlobalDir, MemoryDirName);
    }
}
