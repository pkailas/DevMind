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
//   3. First *.slnx/*.sln in the working directory or one level up → dotnet build
//      for SDK-style projects, MSBuild for classic .NET Framework projects
//      (classic = root <Project> has no Sdk attribute, or contains
//      <TargetFrameworkVersion>). Falls back to dotnet build when MSBuild
//      cannot be located outside a VS environment.
//   4. First *.csproj/*.vbproj in the working directory → dotnet build for
//      SDK-style projects, MSBuild for classic .NET Framework projects
//      (same detection as rule 3). Falls back to dotnet build when MSBuild
//      cannot be located outside a VS environment.
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

            // Detect Node/TypeScript: package.json in the working directory — but only
            // when it actually DEFINES a "build" script. A dependencies-only
            // package.json (e.g. stray npm-install debris at a .NET solution root, as
            // found live in Parsely) must not hijack detection: "npm run build" would
            // just fail with "Missing script", masking the real dotnet build.
            string packageJsonPath = Path.Combine(wd, "package.json");
            if (File.Exists(packageJsonPath) && HasNpmBuildScript(packageJsonPath))
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

            // .NET project — dotnet build for SDK-style projects, MSBuild for
            // classic .NET Framework projects. Classic detection: root <Project>
            // has no Sdk attribute, or contains <TargetFrameworkVersion>.
            string sln = FindSolutionFile(wd);
            if (sln != null)
            {
                if (IsClassicFramework(sln))
                    return BuildMsbuildCommand(sln, warn);
                return $"dotnet build \"{sln}\"";
            }

            // Lone project file in the working directory (.csproj or .vbproj).
            try
            {
                var projects = Directory.GetFiles(wd, "*.csproj", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(wd, "*.vbproj", SearchOption.TopDirectoryOnly))
                    .ToArray();
                if (projects.Length > 0)
                {
                    string proj = projects[0];
                    if (IsClassicFramework(proj))
                        return BuildMsbuildCommand(proj, warn);
                    return $"dotnet build \"{proj}\"";
                }
            }
            catch { }

            return null;
        }

        /// <summary>True when package.json parses and its "scripts" object contains a
        /// "build" entry. Unparseable files count as no-build-script (detection moves
        /// on rather than committing to a command that cannot work).</summary>
        internal static bool HasNpmBuildScript(string packageJsonPath)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(packageJsonPath));
                return doc.RootElement.TryGetProperty("scripts", out var scripts)
                    && scripts.ValueKind == System.Text.Json.JsonValueKind.Object
                    && scripts.TryGetProperty("build", out _);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Builds an MSBuild command for <paramref name="target"/>, reusing the same
        /// discovery logic as the VSIX branch. Falls back to dotnet build when MSBuild
        /// cannot be located outside a VS environment.
        /// </summary>
        private static string BuildMsbuildCommand(string target, Action<string> warn)
        {
            string msbuild = LoopHelpers.FindMSBuildPath();
            string invoke = msbuild.Contains(" ") ? $"& \"{msbuild}\"" : msbuild;

            // If MSBuild was only found as the bare "msbuild" fallback and this is a
            // non-VS environment (no VSINSTALLDIR, no vswhere hit), fall back to
            // dotnet build with an explanatory note rather than a silent failure.
            if (msbuild == "msbuild" &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VSINSTALLDIR")))
            {
                warn?.Invoke(
                    "Classic .NET Framework project detected but MSBuild not found via VSINSTALLDIR or vswhere. " +
                    "Falling back to dotnet build (may fail for classic projects).");
                return $"dotnet build \"{target}\"";
            }

            return $"{invoke} \"{target}\" /t:Build /v:minimal";
        }

        /// <summary>
        /// Returns true when the solution/project references at least one classic
        /// .NET Framework project (root &lt;Project&gt; has no Sdk attribute, or
        /// contains &lt;TargetFrameworkVersion&gt;). For .sln files, parses the
        /// Project("...") lines to discover project paths. For .slnx files, parses
        /// the XML for project references. Missing or unreadable project files are
        /// silently treated as non-classic.
        /// </summary>
        internal static bool IsClassicFramework(string solutionOrProject)
        {
            if (string.IsNullOrEmpty(solutionOrProject))
                return false;

            string ext = Path.GetExtension(solutionOrProject)?.ToLowerInvariant();

            // Direct project file — check it directly.
            if (ext == ".csproj" || ext == ".vbproj" || ext == ".fsproj")
                return IsProjectClassic(solutionOrProject);

            // .sln — parse Project("...") lines to find project paths.
            if (ext == ".sln")
                return SolutionHasClassicProject(slnPath: solutionOrProject, slnxPath: null);

            // .slnx — parse XML for project references.
            if (ext == ".slnx")
                return SolutionHasClassicProject(slnPath: null, slnxPath: solutionOrProject);

            return false;
        }

        /// <summary>
        /// Parses a .sln file for Project("GUID", "Name", "Path") lines and checks
        /// whether any referenced project is classic. Returns false if the file
        /// cannot be read.
        /// </summary>
        private static bool SolutionHasClassicProject(string slnPath, string slnxPath)
        {
            try
            {
                if (slnPath != null)
                {
                    string slnDir = Path.GetDirectoryName(Path.GetFullPath(slnPath));
                    string[] lines = File.ReadAllLines(slnPath);
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        // Match lines like: Project("{GUID}", "Name", "path\to\proj.csproj") = "Name"
                        if (trimmed.StartsWith("Project("))
                        {
                            string projPath = ExtractSlnProjectPath(trimmed);
                            if (projPath != null)
                            {
                                string absolute = Path.IsPathRooted(projPath)
                                    ? projPath
                                    : Path.Combine(slnDir, projPath);
                                if (File.Exists(absolute) && IsProjectClassic(absolute))
                                    return true;
                            }
                        }
                    }
                }
                else if (slnxPath != null)
                {
                    // .slnx is XML-based — look for project references.
                    string slnxDir = Path.GetDirectoryName(Path.GetFullPath(slnxPath));
                    string content = File.ReadAllText(slnxPath);
                    // Simple string-based extraction of project file paths from XML.
                    // .slnx typically contains elements like <Project Path="..." />
                    foreach (string projPath in ExtractSlnxProjectPaths(content))
                    {
                        string absolute = Path.IsPathRooted(projPath)
                            ? projPath
                            : Path.Combine(slnxDir, projPath);
                        if (File.Exists(absolute) && IsProjectClassic(absolute))
                            return true;
                    }
                }
            }
            catch
            {
                // Unreadable solution file — treat as non-classic.
            }

            return false;
        }

        /// <summary>
        /// Extracts the project path from a .sln Project line.
        /// Format: Project("{TypeGuid}") = "Name", "relative\path\proj.vbproj", "{ProjectGuid}"
        /// Returns the first quoted substring ending in .csproj, .vbproj, or .fsproj.
        /// </summary>
        private static string ExtractSlnProjectPath(string line)
        {
            try
            {
                // Collect all quoted substrings and return the first one
                // ending with a project file extension.
                int pos = 0;
                while (pos < line.Length)
                {
                    int start = line.IndexOf('"', pos);
                    if (start < 0) break;
                    int end = line.IndexOf('"', start + 1);
                    if (end < 0) break;
                    string quoted = line.Substring(start + 1, end - start - 1);
                    if (quoted.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        || quoted.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
                        || quoted.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                    {
                        return quoted;
                    }
                    pos = end + 1;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts project paths from .slnx XML content by looking for
        /// Path="..." attributes on elements.
        /// </summary>
        private static string[] ExtractSlnxProjectPaths(string xmlContent)
        {
            var paths = new System.Collections.Generic.List<string>();
            try
            {
                int pos = 0;
                while (pos < xmlContent.Length)
                {
                    int attrStart = xmlContent.IndexOf("Path=\"", pos, StringComparison.OrdinalIgnoreCase);
                    if (attrStart < 0) break;
                    int valueStart = attrStart + 6; // length of 'Path="'
                    int valueEnd = xmlContent.IndexOf('"', valueStart);
                    if (valueEnd < 0) break;
                    string path = xmlContent.Substring(valueStart, valueEnd - valueStart);
                    if (!string.IsNullOrEmpty(path))
                        paths.Add(path);
                    pos = valueEnd + 1;
                }
            }
            catch
            {
                // Return whatever was collected so far.
            }
            return paths.ToArray();
        }

        /// <summary>
        /// Returns true when a single project file is a classic .NET Framework project:
        /// root &lt;Project&gt; element has no Sdk attribute, OR the file contains
        /// a &lt;TargetFrameworkVersion&gt; element. Missing/unreadable files return false.
        /// </summary>
        private static bool IsProjectClassic(string projPath)
        {
            try
            {
                if (!File.Exists(projPath))
                    return false;

                string content = File.ReadAllText(projPath);

                // Check for TargetFrameworkVersion — definitive classic marker.
                if (content.Contains("<TargetFrameworkVersion"))
                    return true;

                // Check root <Project> element for Sdk attribute.
                // Find the first <Project (case-insensitive) and check if it has Sdk=.
                int projectTagStart = IndexOfTag(content, "Project");
                if (projectTagStart >= 0)
                {
                    // Find the closing > of the opening tag.
                    int tagEnd = content.IndexOf('>', projectTagStart);
                    if (tagEnd > 0)
                    {
                        string tagContent = content.Substring(projectTagStart, tagEnd - projectTagStart + 1);
                        // If the root Project tag has no Sdk attribute, it's classic.
                        if (!tagContent.Contains("Sdk="))
                            return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Finds the index of the first occurrence of an XML opening tag
        /// &lt;tagName (case-insensitive) in the given content.
        /// </summary>
        private static int IndexOfTag(string content, string tagName)
        {
            string search = $"<{tagName}";
            int lowerContentIndex = 0;
            string lowerContent = content.ToLowerInvariant();
            string lowerSearch = search.ToLowerInvariant();
            while (true)
            {
                int idx = lowerContent.IndexOf(lowerSearch, lowerContentIndex);
                if (idx < 0) return -1;
                // Verify it's a real tag start (preceded by whitespace, start of string, or <).
                if (idx == 0 || char.IsWhiteSpace(content[idx - 1]) || content[idx - 1] == '<')
                    return idx;
                lowerContentIndex = idx + 1;
            }
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
