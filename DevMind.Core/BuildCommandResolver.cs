// File: BuildCommandResolver.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shared build-command detection used by the McpServer run_build tool and the
// skin hosts (DevMind.Cli, DevMind.TUI). Single source of truth — extracted from
// DevMindTools.DetectBuildCommand so the skins and the MCP server cannot diverge.
//
// Detection order (ported from the MCP server, which itself mirrors the VSIX
// original's .vsixmanifest sibling check):
//   0. DEVMIND_BUILD_COMMAND env var — explicit override, no detection.
//   1. *.vsixmanifest anywhere under the working directory → MSBuild
//      (/p:DeployExtension=false), falling back to dotnet build when MSBuild
//      cannot be located outside a VS environment.
//   2. package.json → "npm run build" (or "bun run build" when bun markers exist).
//   3. First *.slnx/*.sln in the working directory or one level up → dotnet build.
//   4. First *.csproj in the working directory → dotnet build.
//   5. null — no build system detected.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace DevMind
{
    /// <summary>
    /// Auto-detects the correct build command for a working directory.
    /// Results are cached per directory (detection walks the tree looking for a
    /// .vsixmanifest, which is too expensive to repeat every agentic iteration).
    /// Returns null when no build system can be detected.
    /// </summary>
    public static class BuildCommandResolver
    {
        private static readonly ConcurrentDictionary<string, string> _cache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Resolves the build command for <paramref name="workingDirectory"/>.
        /// The DEVMIND_BUILD_COMMAND environment variable overrides detection.
        /// <paramref name="warn"/> receives a message when detection has to fall
        /// back (e.g. VSIX project but MSBuild not found).
        /// </summary>
        public static string Resolve(string workingDirectory, Action<string> warn = null)
        {
            string env = Environment.GetEnvironmentVariable("DEVMIND_BUILD_COMMAND");
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();

            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                return null;

            return _cache.GetOrAdd(Path.GetFullPath(workingDirectory), wd => Detect(wd, warn));
        }

        // ── Detection (ported verbatim from DevMindTools.DetectBuildCommand) ──────

        private static string Detect(string wd, Action<string> warn)
        {
            // Detect VSIX: search for *.vsixmanifest under the working directory.
            // SafeEnumerateFilesGlob prunes noise directories (bin, obj, .git,
            // node_modules, _archive, …) during the walk, so a retired VSIX under
            // _archive/ cannot hijack detection and the scan never descends into
            // .git or node_modules trees.
            string vsixManifest = null;
            try
            {
                vsixManifest = ContextEngine.SafeEnumerateFilesGlob(wd, "*.vsixmanifest")
                    .FirstOrDefault(f => !ContextEngine.IsNoisePath(f));
            }
            catch { }

            if (vsixManifest != null)
            {
                // VSIX project — use MSBuild.
                string solution = FindSolutionFile(wd);
                string msbuild  = LoopHelpers.FindMSBuildPath();
                string invoke   = msbuild.Contains(" ") ? $"& \"{msbuild}\"" : msbuild;

                // If MSBuild was only found as the bare "msbuild" fallback and this is a
                // non-VS environment (no VSINSTALLDIR, no vswhere hit), fall back to
                // dotnet build with an explanatory note rather than a silent failure.
                if (msbuild == "msbuild" &&
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VSINSTALLDIR")))
                {
                    warn?.Invoke(
                        "VSIX project detected but MSBuild not found via VSINSTALLDIR or vswhere. " +
                        "Falling back to dotnet build (may fail for VSIX).");
                    return solution != null
                        ? $"dotnet build \"{solution}\" /p:DeployExtension=false"
                        : $"dotnet build \"{wd}\" /p:DeployExtension=false";
                }

                return solution != null
                    ? $"{invoke} \"{solution}\" /p:DeployExtension=false /verbosity:minimal"
                    : $"{invoke} \"{wd}\" /p:DeployExtension=false /verbosity:minimal";
            }

            // Detect Node/TypeScript: package.json in the working directory.
            string packageJsonPath = Path.Combine(wd, "package.json");
            if (File.Exists(packageJsonPath))
            {
                bool isBun = File.Exists(Path.Combine(wd, "bun.lockb")) ||
                             File.Exists(Path.Combine(wd, "bunfig.toml"));

                if (!isBun)
                {
                    try
                    {
                        string content = File.ReadAllText(packageJsonPath);
                        if (content.Contains("\"packageManager\": \"bun@"))
                            isBun = true;
                    }
                    catch { }
                }

                return isBun ? "bun run build" : "npm run build";
            }

            // .NET project — dotnet build against the first solution found, else a
            // lone .csproj in the working directory.
            string sln = FindSolutionFile(wd);
            if (sln != null)
                return $"dotnet build \"{sln}\"";

            try
            {
                var csprojs = Directory.GetFiles(wd, "*.csproj", SearchOption.TopDirectoryOnly);
                if (csprojs.Length > 0)
                    return $"dotnet build \"{csprojs[0]}\"";
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Searches <paramref name="dir"/> for a *.slnx or *.sln file, then one level up.
        /// Returns the first match, or null if none found.
        /// </summary>
        public static string FindSolutionFile(string dir)
        {
            foreach (string pattern in new[] { "*.slnx", "*.sln" })
            {
                try
                {
                    var hits = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
                    if (hits.Length > 0) return hits[0];
                }
                catch { }
            }

            // One level up.
            string parent = Path.GetDirectoryName(dir);
            if (parent != null && parent != dir)
            {
                foreach (string pattern in new[] { "*.slnx", "*.sln" })
                {
                    try
                    {
                        var hits = Directory.GetFiles(parent, pattern, SearchOption.TopDirectoryOnly);
                        if (hits.Length > 0) return hits[0];
                    }
                    catch { }
                }
            }

            return null;
        }
    }
}
