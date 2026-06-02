// File: WorkspaceRootResolver.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Resolves the best LSP workspace root for a file (solution dir, tsconfig dir).

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
