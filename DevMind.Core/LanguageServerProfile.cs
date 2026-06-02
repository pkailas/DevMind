// File: LanguageServerProfile.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Per-language LSP server launch and document languageId mapping.

using System;
using System.Collections.Generic;
using System.IO;
namespace DevMind
{
    public enum LanguageServerKind
    {
        Unknown = 0,
        CSharp = 1,
        TypeScript = 2,
    }

    public sealed class LanguageServerProfile
    {
        private readonly HashSet<string> _extensions;

        public LanguageServerKind Kind { get; }
        public string DisplayName { get; }

        private LanguageServerProfile(
            LanguageServerKind kind,
            string displayName,
            IEnumerable<string> extensions)
        {
            Kind = kind;
            DisplayName = displayName;
            _extensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        }

        public bool SupportsPath(string fullPath)
        {
            var ext = Path.GetExtension(fullPath);
            return !string.IsNullOrEmpty(ext) && _extensions.Contains(ext);
        }

        public string LanguageIdForPath(string fullPath)
        {
            var ext = Path.GetExtension(fullPath);
            if (Kind == LanguageServerKind.TypeScript)
            {
                if (string.Equals(ext, ".tsx", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".jsx", StringComparison.OrdinalIgnoreCase))
                {
                    return "typescriptreact";
                }

                if (string.Equals(ext, ".js", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".mjs", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".cjs", StringComparison.OrdinalIgnoreCase))
                {
                    return "javascript";
                }

                return "typescript";
            }

            return "csharp";
        }

        public void ResolveServer(string workingDirectory, string serverPathOverride, out string command, out string args)
        {
            if (Kind == LanguageServerKind.CSharp)
            {
                ResolveCSharpServer(workingDirectory, serverPathOverride, out command, out args);
                return;
            }

            ResolveTypeScriptServer(serverPathOverride, out command, out args);
        }

        public static LanguageServerKind KindForPath(string fullPath)
        {
            if (CSharpProfile.SupportsPath(fullPath)) return LanguageServerKind.CSharp;
            if (TypeScriptProfile.SupportsPath(fullPath)) return LanguageServerKind.TypeScript;
            return LanguageServerKind.Unknown;
        }

        public static LanguageServerProfile ForKind(LanguageServerKind kind)
        {
            return kind switch
            {
                LanguageServerKind.CSharp => CSharpProfile,
                LanguageServerKind.TypeScript => TypeScriptProfile,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported language server kind."),
            };
        }

        public static string SupportedExtensionsHelp()
        {
            return ".cs (C#), .ts/.tsx/.js/.jsx (TypeScript/JavaScript)";
        }

        public static readonly LanguageServerProfile CSharpProfile = new LanguageServerProfile(
            LanguageServerKind.CSharp,
            "C#",
            new[] { ".cs" });

        public static readonly LanguageServerProfile TypeScriptProfile = new LanguageServerProfile(
            LanguageServerKind.TypeScript,
            "TS",
            new[] { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs" });

        private static void ResolveCSharpServer(string workingDirectory, string serverPathOverride, out string command, out string args)
        {
            string solutionArg = "";
            string solutionDir = WorkspaceRootResolver.FindSolutionDirectory(workingDirectory);
            if (solutionDir != null)
            {
                string solution = FindSolutionFileInDir(solutionDir);
                if (solution != null)
                {
                    solutionArg = " --solution \"" + solution.Replace("\"", "\\\"") + "\"";
                }
            }

            if (!string.IsNullOrWhiteSpace(serverPathOverride))
            {
                command = serverPathOverride;
                args = "--loglevel warning" + solutionArg;
                return;
            }

            var envPath = Environment.GetEnvironmentVariable("DEVMIND_LSP_SERVER_PATH");
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                command = envPath;
                args = "--loglevel warning" + solutionArg;
                return;
            }

            command = "csharp-ls";
            args = "--loglevel warning" + solutionArg;
        }

        private static void ResolveTypeScriptServer(string serverPathOverride, out string command, out string args)
        {
            args = "--stdio";

            if (!string.IsNullOrWhiteSpace(serverPathOverride))
            {
                command = serverPathOverride;
                return;
            }

            var envPath = Environment.GetEnvironmentVariable("DEVMIND_LSP_TYPESCRIPT_PATH");
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                command = envPath;
                return;
            }

            var resolved = NpmGlobalToolResolver.Resolve("typescript-language-server");
            command = resolved ?? "typescript-language-server";
        }

        private static string FindSolutionFileInDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            foreach (var pattern in new[] { "*.slnx", "*.sln" })
            {
                try
                {
                    var hits = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
                    if (hits.Length > 0) return hits[0];
                }
                catch
                {
                    // ignore
                }
            }

            return null;
        }
    }
}
