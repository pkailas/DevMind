// File: DevEnvironment.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Process-environment bootstrap for hosts launched OUTSIDE a developer shell.
// Claude Desktop (and other GUI MCP clients) spawn DevMind.McpServer with a minimal
// PATH: `dotnet` was unresolvable and `dotnet ef` (a global tool in ~\.dotnet\tools)
// failed with "command not found" during live delegations. Every ShellRunner child
// inherits this process's environment, so enriching it once at startup fixes
// run_shell, run_build, run_tests, AND delegated headless agents in one place.

using System;
using System.IO;
using System.Linq;

namespace DevMind
{
    /// <summary>One-shot environment enrichment for GUI-launched hosts.</summary>
    public static class DevEnvironment
    {
        /// <summary>Default DEVMIND_SHELL_TIMEOUT (seconds) applied when unset: solution
        /// builds routinely exceed ShellRunner's 120 s baseline (a live Parsely build
        /// timed out mid-delegation).</summary>
        public const int DefaultShellTimeoutSeconds = 300;

        /// <summary>
        /// Ensures common developer tool directories are on PATH and a build-friendly
        /// shell timeout is set. Idempotent; only appends directories that exist and
        /// are missing. Returns a human-readable summary of what changed (for stderr
        /// diagnostics), or null when nothing needed changing.
        /// </summary>
        public static string EnrichProcessEnvironment()
        {
            var notes = new System.Text.StringBuilder();

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            var segments = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.TrimEnd(Path.DirectorySeparatorChar))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string candidate in DeveloperPathCandidates())
            {
                if (!Directory.Exists(candidate)) continue;
                if (segments.Contains(candidate.TrimEnd(Path.DirectorySeparatorChar))) continue;
                path = path.TrimEnd(Path.PathSeparator) + Path.PathSeparator + candidate;
                notes.Append($"PATH += {candidate}; ");
            }
            if (notes.Length > 0)
                Environment.SetEnvironmentVariable("PATH", path);

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT")))
            {
                Environment.SetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT",
                    DefaultShellTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                notes.Append($"DEVMIND_SHELL_TIMEOUT = {DefaultShellTimeoutSeconds}; ");
            }

            return notes.Length > 0 ? notes.ToString().TrimEnd(' ', ';') : null;
        }

        private static string[] DeveloperPathCandidates()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new[]
            {
                Path.Combine(programFiles, "dotnet"),                  // dotnet CLI
                Path.Combine(userProfile, ".dotnet", "tools"),         // dotnet global tools (dotnet-ef)
                Path.Combine(programFiles, "Git", "cmd"),              // git
                Path.Combine(programFiles, "nodejs"),                  // node/npm
            };
        }
    }
}
