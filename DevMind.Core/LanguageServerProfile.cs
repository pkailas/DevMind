// File: LanguageServerProfile.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Per-language LSP server launch and document languageId mapping.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
namespace DevMind
{
    public enum LanguageServerKind
    {
        Unknown = 0,
        CSharp = 1,
        TypeScript = 2,
    }

    /// <summary>Which C# language-server implementation to launch.</summary>
    public enum CSharpServerImpl
    {
        Roslyn = 0,
        CSharpLs = 1,
    }

    /// <summary>
    /// Fully-resolved launch + protocol descriptor for a language server.
    /// Carries everything LanguageServerHost needs that differs between
    /// csharp-ls (push diagnostics, --solution CLI arg) and Roslyn LS
    /// (pull diagnostics, solution/open notification, project-init wait).
    /// </summary>
    public sealed class ResolvedLanguageServer
    {
        public string Command { get; set; }
        public string Args { get; set; }

        /// <summary>Roslyn: file URI to send via solution/open after initialize. Null = none.</summary>
        public string SolutionOpenUri { get; set; }

        /// <summary>Notification to await before serving the first request (roslyn). Null = none.</summary>
        public string ReadyNotification { get; set; }

        /// <summary>True for Roslyn LS (textDocument/diagnostic pull); false for push servers.</summary>
        public bool UsesPullDiagnostics { get; set; }
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

        public ResolvedLanguageServer ResolveServer(string workingDirectory, string serverPathOverride)
        {
            if (Kind == LanguageServerKind.CSharp)
                return ResolveCSharpServer(workingDirectory, serverPathOverride);

            ResolveTypeScriptServer(serverPathOverride, out var command, out var args);
            return new ResolvedLanguageServer
            {
                Command = command,
                Args = args,
                UsesPullDiagnostics = false,
            };
        }

        /// <summary>
        /// Pick the C# server implementation. Explicit DEVMIND_LSP_SERVER
        /// (roslyn|csharp-ls) wins; otherwise infer from the configured server
        /// path's filename; otherwise default to Roslyn.
        /// </summary>
        public static CSharpServerImpl SelectCSharpImpl(string serverPathOverride)
        {
            var sel = Environment.GetEnvironmentVariable("DEVMIND_LSP_SERVER")?.Trim();
            if (!string.IsNullOrEmpty(sel))
            {
                if (sel.Equals("csharp-ls", StringComparison.OrdinalIgnoreCase) ||
                    sel.Equals("csharpls", StringComparison.OrdinalIgnoreCase))
                    return CSharpServerImpl.CSharpLs;
                if (sel.Equals("roslyn", StringComparison.OrdinalIgnoreCase) ||
                    sel.Equals("roslyn-language-server", StringComparison.OrdinalIgnoreCase))
                    return CSharpServerImpl.Roslyn;
            }

            string probe = !string.IsNullOrWhiteSpace(serverPathOverride)
                ? serverPathOverride
                : Environment.GetEnvironmentVariable("DEVMIND_LSP_SERVER_PATH");
            if (!string.IsNullOrWhiteSpace(probe))
            {
                var name = Path.GetFileName(probe);
                if (name.IndexOf("csharp-ls", StringComparison.OrdinalIgnoreCase) >= 0)
                    return CSharpServerImpl.CSharpLs;
            }

            return CSharpServerImpl.Roslyn;
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

        private static ResolvedLanguageServer ResolveCSharpServer(string workingDirectory, string serverPathOverride)
        {
            // Discover the nearest solution (walk up for *.slnx / *.sln).
            string solutionDir = WorkspaceRootResolver.FindSolutionDirectory(workingDirectory);
            string solutionFile = solutionDir != null ? FindSolutionFileInDir(solutionDir) : null;

            var impl = SelectCSharpImpl(serverPathOverride);

            if (impl == CSharpServerImpl.CSharpLs)
            {
                string solutionArg = solutionFile != null
                    ? " --solution \"" + solutionFile.Replace("\"", "\\\"") + "\""
                    : "";
                return new ResolvedLanguageServer
                {
                    Command = ResolveCSharpLsCommand(serverPathOverride),
                    Args = "--loglevel warning" + solutionArg,
                    UsesPullDiagnostics = false,
                };
            }

            // Roslyn LS: stdio transport; solution is loaded via solution/open
            // (not a CLI arg) and diagnostics are pull-model.
            string exe = ResolveRoslynExe(serverPathOverride);
            string logDir = Path.Combine(Path.GetTempPath(), "devmind-roslyn-ls");
            var args = new StringBuilder("--stdio --logLevel Information");
            args.Append(" --extensionLogDirectory \"").Append(logDir).Append("\"");
            try { args.Append(" --clientProcessId ").Append(Environment.ProcessId); }
            catch { /* clientProcessId is best-effort */ }

            return new ResolvedLanguageServer
            {
                Command = exe,
                Args = args.ToString(),
                SolutionOpenUri = solutionFile != null
                    ? LanguageServerHost.PathToFileUri(solutionFile)
                    : null,
                ReadyNotification = "workspace/projectInitializationComplete",
                UsesPullDiagnostics = true,
            };
        }

        private static string ResolveCSharpLsCommand(string serverPathOverride)
        {
            string candidate = !string.IsNullOrWhiteSpace(serverPathOverride)
                ? serverPathOverride
                : Environment.GetEnvironmentVariable("DEVMIND_LSP_SERVER_PATH");

            // If the configured path is clearly the Roslyn server (e.g. shell.json
            // still points at roslyn-language-server.cmd), ignore it so that
            // DEVMIND_LSP_SERVER=csharp-ls alone is enough to fall back.
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var name = Path.GetFileName(candidate);
                bool looksRoslyn =
                    name.IndexOf("roslyn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Microsoft.CodeAnalysis.LanguageServer", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!looksRoslyn) return candidate;
            }

            return "csharp-ls";
        }

        /// <summary>
        /// Resolve the Roslyn LS executable. Accepts a direct .exe path, a
        /// dotnet-tool .cmd shim (parsed to the underlying exe), or nothing
        /// (defaults to the global roslyn-language-server.cmd shim). A path that
        /// is clearly csharp-ls is ignored so an explicit DEVMIND_LSP_SERVER=roslyn
        /// still launches Roslyn.
        /// </summary>
        private static string ResolveRoslynExe(string serverPathOverride)
        {
            string candidate = !string.IsNullOrWhiteSpace(serverPathOverride)
                ? serverPathOverride
                : Environment.GetEnvironmentVariable("DEVMIND_LSP_SERVER_PATH");

            if (!string.IsNullOrWhiteSpace(candidate) &&
                Path.GetFileName(candidate).IndexOf("csharp-ls", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                candidate = null; // wrong server for roslyn impl — fall back to default shim
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                string toolsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".dotnet", "tools");
                candidate = Path.Combine(toolsDir, "roslyn-language-server.cmd");
            }

            if (candidate.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
            {
                var exe = ExtractExeFromCmdShim(candidate);
                if (exe != null) return exe;
            }

            return candidate; // assume already an exe, or resolvable on PATH
        }

        /// <summary>
        /// Read a dotnet-tool .cmd shim and return the absolute path of the
        /// .exe it launches. The shim looks like:
        ///   "%~dp0.store\...\Microsoft.CodeAnalysis.LanguageServer.exe" %*
        /// Returns null if it cannot be parsed/resolved.
        /// </summary>
        private static string ExtractExeFromCmdShim(string cmdPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(cmdPath)) + Path.DirectorySeparatorChar;
                foreach (var raw in File.ReadAllLines(cmdPath))
                {
                    var line = raw.Trim();
                    int q1 = line.IndexOf('"');
                    if (q1 < 0) continue;
                    int q2 = line.IndexOf('"', q1 + 1);
                    if (q2 < 0) continue;

                    string inner = line.Substring(q1 + 1, q2 - q1 - 1);
                    if (inner.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    inner = inner.Replace("%~dp0", dir);
                    inner = Environment.ExpandEnvironmentVariables(inner);
                    string full = Path.GetFullPath(inner);
                    if (File.Exists(full)) return full;
                }
            }
            catch { /* fall through to null */ }

            return null;
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
