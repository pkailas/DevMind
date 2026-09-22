// File: WorkspaceRootResolver.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Resolves the best LSP workspace root for a file (solution dir, tsconfig dir).
// v1.1: LooksLikeRepositoryContainer / EnumerateChildRepositories — the "folder of
//   repositories, not a repository" check, moved here from the MCP task tools so the
//   LSP router can ask the same question without duplicating the logic.

using System;
using System.IO;

namespace DevMind
{
    public static class WorkspaceRootResolver
    {
        /// <summary>
        /// Directory the language server should use as workspace root for <paramref name="fullPath"/>.
        /// </summary>
        public static string Resolve(LanguageServerKind kind, string fullPath, string sessionRoot)
        {
            fullPath = Path.GetFullPath(fullPath);
            var session = NormalizeDir(sessionRoot);

            return kind switch
            {
                LanguageServerKind.CSharp =>
                    FindSolutionDirectory(Path.GetDirectoryName(fullPath) ?? session) ?? session,
                LanguageServerKind.TypeScript =>
                    FindTypeScriptProjectDirectory(Path.GetDirectoryName(fullPath) ?? session) ?? session,
                _ => session,
            };
        }

        public static string FindTypeScriptProjectDirectory(string startDir)
        {
            var dir = NormalizeDir(startDir);
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "tsconfig.json")))
                    return dir;
                if (File.Exists(Path.Combine(dir, "jsconfig.json")))
                    return dir;
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return dir;

                var parent = Path.GetDirectoryName(dir);
                if (parent == null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                    break;
                dir = parent;
            }

            return null;
        }

        public static string FindSolutionDirectory(string startDir)
        {
            var dir = NormalizeDir(startDir);
            while (!string.IsNullOrEmpty(dir))
            {
                foreach (var pattern in new[] { "*.slnx", "*.sln" })
                {
                    try
                    {
                        var hits = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
                        if (hits.Length > 0)
                            return dir;
                    }
                    catch
                    {
                        // ignore
                    }
                }

                var parent = Path.GetDirectoryName(dir);
                if (parent == null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                    break;
                dir = parent;
            }

            return null;
        }

        /// <summary>
        /// True when <paramref name="dir"/> is itself not a git repository but at least one
        /// direct child is — the shape of a folder that holds repositories rather than being
        /// one. Keys on .git rather than *.sln so a Python or TypeScript repository rooted the
        /// same way is not refused. Worktrees and submodules carry .git as a FILE, so both
        /// forms count. Unreadable directories return false: this is a convenience guard
        /// against a mistyped root, not a security boundary, so when it cannot tell it lets
        /// the caller proceed.
        /// </summary>
        public static bool LooksLikeRepositoryContainer(string dir)
        {
            try
            {
                if (HasGitMarker(dir)) return false;

                foreach (string child in Directory.EnumerateDirectories(dir))
                {
                    if (HasGitMarker(child)) return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Names (not paths) of up to <paramref name="max"/> direct children of
        /// <paramref name="dir"/> that carry a .git marker, in enumeration order. Used to
        /// make a "pick a repository" message concrete. Best effort — an unreadable
        /// directory yields an empty list.
        /// </summary>
        public static string[] EnumerateChildRepositories(string dir, int max)
        {
            var found = new System.Collections.Generic.List<string>();
            if (max <= 0) return found.ToArray();
            try
            {
                foreach (string child in Directory.EnumerateDirectories(dir))
                {
                    if (!HasGitMarker(child)) continue;
                    found.Add(Path.GetFileName(child));
                    if (found.Count >= max) break;
                }
            }
            catch
            {
                // best effort
            }
            return found.ToArray();
        }

        /// <summary>True when <paramref name="dir"/> holds a .git directory or file.</summary>
        public static bool HasGitMarker(string dir)
        {
            string marker = Path.Combine(dir, ".git");
            return Directory.Exists(marker) || File.Exists(marker);
        }

        private static string NormalizeDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return Environment.CurrentDirectory;
            try
            {
                return Path.GetFullPath(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                return dir;
            }
        }
    }
}
