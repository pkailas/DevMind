// File: FilePathResolver.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shared filename → full-path resolution used by the MCP server (DevMindTools),
// the headless host (BufferedAgenticHost) and the TUI skin (TuiAgenticHost).
// Single source of truth — extracted from the three private copies so the
// resolution rules (and the not-found message) cannot diverge between skins.
//
// Resolution order:
//   1. Already absolute and exists → use directly.
//   2. Hint joined to the working directory → use if it exists.
//   3. Bare basename joined to the working directory top level → use if it exists.
//   4. Git-root fallback: 2–3 repeated against the git root when the working
//      directory is a subdirectory of a repo. Fixes the live failure where the
//      model cited a repo-root-relative hint ("QuantEval/ByteSizeParser.cs")
//      from a subdirectory working_dir and the resolver reported "not found".
//   5. Recursive basename search under the working directory (plus the git root,
//      deduplicated, noise paths excluded):
//        - exactly one match → use it.
//        - multiple + hint carries a directory → candidates whose full path ends
//          with the hint's directory+name suffix; exactly one → use it.
//        - otherwise → NOT FOUND. We never return an arbitrary same-named file —
//          a silently wrong file is worse than "not found" (the caller can
//          recover via run_shell / a more specific path).
//
// The not-found message states the resolution SCOPE (which directories were
// searched recursively) and lists same-named candidates — it must never
// present the working directory's top-level files as the project, which the
// model has been observed reading as ground truth ("the working tree contains
// only the test file — there is no source project").

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DevMind
{
    /// <summary>
    /// Outcome of <see cref="FilePathResolver.Resolve"/>. When <see cref="Path"/> is
    /// null the file could not be resolved; <see cref="Candidates"/> holds every
    /// non-noise file whose basename matched (empty = genuinely absent, more than
    /// one = refused to guess) and <see cref="SearchedRoots"/> lists the directories
    /// the recursive search covered.
    /// </summary>
    public sealed class FileResolution
    {
        public FileResolution(string path, IReadOnlyList<string> candidates, IReadOnlyList<string> searchedRoots)
        {
            Path = path;
            Candidates = candidates;
            SearchedRoots = searchedRoots;
        }

        /// <summary>The resolved absolute path, or null when the file could not be resolved.</summary>
        public string Path { get; }

        /// <summary>
        /// Non-noise files whose basename matched the hint anywhere under
        /// <see cref="SearchedRoots"/>. Empty when the name matched nothing.
        /// </summary>
        public IReadOnlyList<string> Candidates { get; }

        /// <summary>Directories the recursive search covered, in the order searched.</summary>
        public IReadOnlyList<string> SearchedRoots { get; }
    }

    public static class FilePathResolver
    {
        // Test seam: counts every entry to <see cref="Resolve"/> so a fix that must
        // eliminate a redundant second resolution (the not-found message builder used
        // to re-resolve after the caller's first resolve — re-running git-root
        // discovery, the AllDirectories scan and the O(m²) dedup) can be verified by
        // count, not just message text.
        internal static int ResolveCallCount;

        public static FileResolution Resolve(string fileNameOnly, string hintPath, string workingDirectory)
        {
            System.Threading.Interlocked.Increment(ref ResolveCallCount);
            // 1. Absolute path that exists — use directly.
            if (!string.IsNullOrWhiteSpace(hintPath)
                && Path.IsPathRooted(hintPath) && File.Exists(hintPath))
                return new FileResolution(hintPath, Array.Empty<string>(), Array.Empty<string>());

            // Normalize the hint for suffix matching: forward slashes, strip leading
            // "./", strip leading "/". A hint that is just "./" or whitespace normalizes
            // to "" and contributes no directory information.
            string normalized = (hintPath ?? "").Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized.Substring(2);
            normalized = normalized.TrimStart('/');

            var searched = new List<string>();
            if (!string.IsNullOrWhiteSpace(workingDirectory))
                searched.Add(workingDirectory);

            // 2 + 3. Join against the working directory (hint as-is, then bare name).
            string joined = JoinIfExists(workingDirectory, normalized, fileNameOnly);
            if (joined != null)
                return new FileResolution(joined, Array.Empty<string>(), searched);

            // 4. Git-root fallback: the model routinely cites repo-root-relative hints
            //    when the working directory is a subdirectory of the repo — a live
            //    failure the wd-only join kept reporting as "file not found".
            string gitRoot = searched.Count > 0
                ? ContextEngine.FindGitRoot(searched[0])
                : null;
            if (!string.IsNullOrWhiteSpace(gitRoot)
                && !string.Equals(gitRoot, workingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                searched.Add(gitRoot);
                joined = JoinIfExists(gitRoot, normalized, fileNameOnly);
                if (joined != null)
                    return new FileResolution(joined, Array.Empty<string>(), searched);
            }

            // 5. Directory-aware recursive fallback. We never return an arbitrary
            //    same-named file — a silently wrong file is worse than "not found".
            if (string.IsNullOrEmpty(fileNameOnly) || searched.Count == 0)
                return new FileResolution(null, Array.Empty<string>(), searched);

            // Roots are searched IN ORDER (working directory first, git root second):
            // a unique match under the working directory is authoritative — we do not
            // widen to the git root just to add candidates that would turn a clean
            // unique resolution into a refusal. The git root is consulted only when
            // the working directory search matched nothing (the repo-root-relative-hint
            // case). Deduplication makes the nested wd ⊂ git-root overlap harmless.
            var found = new List<string>();
            try
            {
                foreach (string root in searched)
                {
                    foreach (string f in Directory.GetFiles(root, fileNameOnly, SearchOption.AllDirectories))
                    {
                        if (ContextEngine.IsNoisePath(f)) continue;
                        if (!found.Any(p => string.Equals(p, f, StringComparison.OrdinalIgnoreCase)))
                            found.Add(f);
                    }
                    if (found.Count == 1) return new FileResolution(found[0], found, searched);
                }
            }
            catch { }

            if (found.Count == 1)
                return new FileResolution(found[0], found, searched);

            if (found.Count > 1 && normalized.Contains('/'))
            {
                // Match candidates whose path ENDS WITH the hint's directory+name suffix.
                // Plain (substring) EndsWith so a partial/abbreviated directory in the
                // hint still matches — e.g. "TestHarness/Program.cs" matches a real
                // directory named ".../VLink.PSCPConnector.TestHarness/Program.cs".
                string[] suffixMatches = found
                    .Where(f => f.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                // Exactly one → use it. Zero (directory portion matched nothing) or more
                // than one (still ambiguous) → not found; never guess.
                if (suffixMatches.Length == 1)
                    return new FileResolution(suffixMatches[0], found, searched);
            }

            return new FileResolution(null, found, searched);
        }

        /// <summary>
        /// Builds the "file not found" tool response from a failed resolution.
        /// States the search SCOPE (which directories were searched recursively,
        /// including the git root) and lists same-named candidates so the caller
        /// can disambiguate — instead of dumping the working directory's top-level
        /// .cs files, which the model reads as "the whole project" and concludes
        /// source files are missing (live failure, job-471 transcript).
        /// </summary>
        public static string BuildFileNotFoundMessage(
            string directive, string filename, FileResolution resolution)
        {
            const int MaxCandidates = 10;
            var sb = new StringBuilder();
            sb.AppendLine($"{directive}: file not found — {filename}");

            foreach (string root in resolution.SearchedRoots)
                sb.AppendLine($"Searched recursively (excluding bin/obj/.git/node_modules etc.): {root}");

            if (resolution.Candidates.Count > 1)
            {
                string baseName;
                try { baseName = Path.GetFileName(filename.Replace('\\', '/')); }
                catch { baseName = filename; }
                sb.AppendLine($"Files named \"{baseName}\" exist at:");
                int shown = Math.Min(resolution.Candidates.Count, MaxCandidates);
                for (int i = 0; i < shown; i++) sb.AppendLine($"  {resolution.Candidates[i]}");
                if (resolution.Candidates.Count > MaxCandidates)
                    sb.AppendLine($"  ... and {resolution.Candidates.Count - MaxCandidates} more");
                sb.AppendLine(
                    "The hint did not uniquely identify a file — retry with the full path " +
                    "relative to the working directory (or use run_shell to locate it).");
            }
            else if (resolution.Candidates.Count == 1)
            {
                // Unique basename match. State only what is true:
                //  - the hint carried a directory portion → that portion matched
                //    nothing; the directory is likely wrong, not the file.
                //  - the hint was a bare name → a unique file of that name exists;
                //    the old wording claimed "the directory portion did not match"
                //    even though there was no directory portion to match.
                string baseName;
                try { baseName = Path.GetFileName(filename.Replace('\\', '/')); }
                catch { baseName = filename; }
                string dirProbe = (filename ?? "").Replace('\\', '/');
                while (dirProbe.StartsWith("./", StringComparison.Ordinal))
                    dirProbe = dirProbe.Substring(2);
                dirProbe = dirProbe.TrimStart('/');

                sb.AppendLine($"A file named \"{baseName}\" exists at: {resolution.Candidates[0]}");
                if (dirProbe.Contains('/'))
                    sb.AppendLine("The directory portion of the hint did not match it — check the path and retry.");
                else
                    sb.AppendLine("A unique file of that name exists — use that path.");
            }
            else
            {
                sb.AppendLine("No file with that name exists in the searched directories — it is absent from this project.");
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        /// <summary>
        /// Re-resolving convenience for callers whose resolution helper returns only
        /// the path (e.g. the MCP tool surface, where threading the
        /// <see cref="FileResolution"/> through would touch every tool call site).
        /// Normalizes the filename with the same guard as the first-resolve helpers
        /// — an empty or invalid-character name must produce a clean "not found"
        /// message, not throw (the wrappers used an unguarded <see cref="Path.GetFileName"/>
        /// where the first-resolve helpers tolerated such input, so the two calls
        /// diverged by throwing).
        /// </summary>
        public static string BuildFileNotFoundMessage(string directive, string filename, string workingDirectory)
        {
            string fileNameOnly;
            try { fileNameOnly = Path.GetFileName((filename ?? "").Replace('\\', '/')); }
            catch { fileNameOnly = filename ?? ""; }
            var resolution = Resolve(fileNameOnly, filename ?? "", workingDirectory);
            return BuildFileNotFoundMessage(directive, filename ?? "", resolution);
        }

        /// <summary>
        /// Joins <paramref name="hint"/> (then <paramref name="fileNameOnly"/>) to
        /// <paramref name="dir"/> and returns the first that exists; null otherwise.
        /// </summary>
        private static string JoinIfExists(string dir, string hint, string fileNameOnly)
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;

            if (!string.IsNullOrEmpty(hint))
            {
                string byHint = Path.Combine(dir, hint.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(byHint)) return byHint;
            }

            if (!string.IsNullOrEmpty(fileNameOnly) && fileNameOnly != hint)
            {
                string byName = Path.Combine(dir, fileNameOnly);
                if (File.Exists(byName)) return byName;
            }

            return null;
        }
    }
}
