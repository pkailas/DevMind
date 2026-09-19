// File: SystemPromptFile.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Loads the user-editable global system prompt from
// %APPDATA%\devmind\system-prompt.md (same folder as devmind.json).
//
// Design:
//   * Read on EVERY call — no caching, no FileSystemWatcher. The system prompt
//     is rebuilt every turn, so re-reading gives hot-reload for free: editing
//     the file in another editor takes effect on the next turn.
//   * Absence is a normal state. Load() returns null when the file is missing,
//     empty, or whitespace-only. Callers fall back to options.SystemPrompt.
//   * All IO exceptions are swallowed — the prompt is best-effort and must
//     never throw into prompt-assembly paths.
//   * The file is NOT created here. If it does not exist the user has not
//     authored one, and there is nothing to load.

using System;
using System.IO;

namespace DevMind
{
    public static class SystemPromptFile
    {
        /// <summary>
        /// Name of the global system-prompt file inside the per-user DevMind
        /// state directory (%APPDATA%\devmind\).
        /// </summary>
        public const string FileName = "system-prompt.md";

        /// <summary>
        /// Absolute path to the global system-prompt file. Derived from
        /// <see cref="DevMindPaths.GlobalDir"/> (single source of truth for
        /// the %APPDATA%\devmind state directory).
        /// </summary>
        public static string Path => System.IO.Path.Combine(DevMindPaths.GlobalDir, FileName);

        /// <summary>
        /// Loads the global system prompt from the default path
        /// (%APPDATA%\devmind\system-prompt.md). Returns the file contents
        /// trimmed, or <c>null</c> if the file is absent, empty, or
        /// whitespace-only. Never throws.
        /// </summary>
        public static string Load() => LoadFrom(Path);

        /// <summary>
        /// Loads the global system prompt from an explicit path. Exposed for
        /// tests; production code calls <see cref="Load()"/> which reads the
        /// default path. Returns the file contents trimmed, or <c>null</c> if
        /// the file is absent, empty, or whitespace-only. Never throws.
        /// </summary>
        public static string LoadFrom(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                    return null;

                string content = System.IO.File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(content))
                    return null;

                return content.Trim();
            }
            catch
            {
                // IO errors (access denied, in-use, corrupt, etc.) are swallowed:
                // the prompt is best-effort and must never throw into prompt assembly.
                return null;
            }
        }
    }
}
