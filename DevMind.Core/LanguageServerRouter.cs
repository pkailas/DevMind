// File: LanguageServerRouter.cs  v1.2
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Routes LSP tool calls to the correct language server (C# vs TypeScript) per file extension.
// v1.1: FindSymbolAsync — solution-wide workspace/symbol search, routed by language (no file
//   path); reuses the same per-(kind|root) host instance as the position-based tools.
// v1.2: FindSymbolAsync refuses to search when no solution/project encloses the resolved
//   start directory. Falling back to that directory started a server with nothing opened,
//   whose empty result then claimed a solution that never existed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// Lazy multi-server facade: one <see cref="LanguageServerHost"/> per <see cref="LanguageServerKind"/>.
    /// </summary>
    public sealed class LanguageServerRouter : IDisposable
    {
        private readonly string _workingDirectory;
        private readonly string _csharpServerPathOverride;
        private readonly string _typescriptServerPathOverride;
        private readonly object _gate = new object();
        private readonly Dictionary<string, LanguageServerHost> _hosts =
            new Dictionary<string, LanguageServerHost>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public LanguageServerRouter(
            string workingDirectory,
            string csharpServerPathOverride = null,
            string typescriptServerPathOverride = null)
        {
            _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(workingDirectory);
            _csharpServerPathOverride = csharpServerPathOverride;
            _typescriptServerPathOverride = typescriptServerPathOverride;
        }

        public static bool IsEnabled()
        {
            return LanguageServerHost.IsEnabled();
        }

        public async Task<string> GetDiagnosticsAsync(string fullPath, CancellationToken cancellationToken = default)
        {
            return await GetHostForPath(fullPath)
                .GetDiagnosticsAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<string> GoToDefinitionAsync(
            string fullPath,
            int line,
            int character,
            CancellationToken cancellationToken = default)
        {
            return await GetHostForPath(fullPath)
                .GoToDefinitionAsync(fullPath, line, character, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<string> FindReferencesAsync(
            string fullPath,
            int line,
            int character,
            CancellationToken cancellationToken = default)
        {
            return await GetHostForPath(fullPath)
                .FindReferencesAsync(fullPath, line, character, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<string> HoverAsync(
            string fullPath,
            int line,
            int character,
            CancellationToken cancellationToken = default)
        {
            return await GetHostForPath(fullPath)
                .HoverAsync(fullPath, line, character, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Solution-wide symbol search (workspace/symbol). Has no file to derive the language
        /// from, so it routes by <paramref name="language"/> ("csharp" default, "typescript").
        /// The host is resolved per (kind|solution-root) exactly like the position-based tools,
        /// so it reuses the already-warm server rather than spawning a second one.
        /// <paramref name="pathHint"/> is the stand-in for the file the position-based tools
        /// get: any file or directory inside the solution to search. Omitted, the search falls
        /// back to the session working directory — which on the MCP surface is fixed at
        /// startup and may well be a different repository.
        /// </summary>
        public async Task<string> FindSymbolAsync(
            string query,
            int maxResults,
            string language,
            CancellationToken cancellationToken = default,
            string pathHint = null)
        {
            var kind = ResolveLanguageKind(language);
            string startDir = ResolveSymbolSearchStartDirectory(pathHint, _workingDirectory);

            // No solution/project at or above the start directory means the server would
            // start with nothing opened and its "no symbols" would be a false negative that
            // names a solution nobody loaded. Say what actually happened instead — and do
            // not spawn a server to say it.
            string root = TryFindEnclosingProjectRoot(kind, startDir);
            if (root == null)
                return BuildNoEnclosingProjectMessage(kind, pathHint, startDir, _workingDirectory);

            return await GetOrCreateHost(kind, root)
                .FindSymbolAsync(query, maxResults, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Workspace root for a symbol search. With no hint this is the session working
        /// directory's enclosing solution (the historical behaviour). With a hint it is the
        /// solution enclosing that file or directory, so a caller can search a repository the
        /// server was not started in. A hint that does not exist throws rather than silently
        /// falling back: searching the wrong solution and reporting "not found" is exactly the
        /// failure this parameter exists to remove.
        /// </summary>
        public static string ResolveSymbolSearchRoot(
            LanguageServerKind kind, string pathHint, string workingDirectory)
        {
            string startDir = ResolveSymbolSearchStartDirectory(pathHint, workingDirectory);
            return TryFindEnclosingProjectRoot(kind, startDir) ?? startDir;
        }

        /// <summary>
        /// The solution directory (C#) or project directory (TypeScript) enclosing
        /// <paramref name="startDir"/>, walking upward only — or null when none does. Null is
        /// the whole point: it is the state a caller must not paper over with a fallback.
        /// </summary>
        internal static string TryFindEnclosingProjectRoot(LanguageServerKind kind, string startDir)
        {
            return kind == LanguageServerKind.CSharp
                ? WorkspaceRootResolver.FindSolutionDirectory(startDir)
                : WorkspaceRootResolver.FindTypeScriptProjectDirectory(startDir);
        }

        /// <summary>
        /// The message returned when nothing encloses <paramref name="startDir"/>. Three
        /// shapes: with no hint, either the working directory is a folder of repositories
        /// (say which ones) or it is simply outside any solution; with a hint, the hint is
        /// the thing to correct. Wording follows the kind, so a TypeScript miss never
        /// mentions *.sln.
        /// </summary>
        internal static string BuildNoEnclosingProjectMessage(
            LanguageServerKind kind, string pathHint, string startDir, string workingDirectory)
        {
            string language = LanguageServerProfile.ForKind(kind).DisplayName;
            string unit = kind == LanguageServerKind.CSharp ? "solution" : "project";
            string markers = kind == LanguageServerKind.CSharp
                ? "no *.sln or *.slnx at or above it"
                : "no tsconfig.json, jsconfig.json or package.json at or above it";

            if (!string.IsNullOrWhiteSpace(pathHint))
            {
                return "find_symbol: path '" + pathHint + "' resolved to " + startDir +
                       ", which is not inside a " + language + " " + unit + " (" + markers +
                       "), so nothing was searched. Pass a path inside the " + unit + " you mean.";
            }

            string session = string.IsNullOrWhiteSpace(workingDirectory)
                ? startDir
                : Path.GetFullPath(workingDirectory);

            if (WorkspaceRootResolver.LooksLikeRepositoryContainer(session))
            {
                string[] repos = WorkspaceRootResolver.EnumerateChildRepositories(session, 5);
                string example = repos.Length > 0 ? Path.Combine(session, repos[0]) : "<one child repo>";
                string listing = repos.Length > 0
                    ? " Repositories found here include: " + string.Join(", ", repos) + "."
                    : "";
                return "find_symbol: the session working directory " + session +
                       " is a folder of repositories, not a " + unit + ", so there is nothing to search. " +
                       "Pass path=<a file or directory inside the repository you mean> \u2014 for example path=" +
                       example + "." + listing;
            }

            return "find_symbol: the session working directory " + session + " is not inside a " +
                   language + " " + unit + " (" + markers + "), so no " + unit +
                   " was loaded and nothing was searched. Pass path=<a file or directory inside the " +
                   unit + " to search>.";
        }

        /// <summary>
        /// Where the upward walk begins: the hint's directory (a file's parent, or the
        /// directory itself) resolved against the session, or the session working directory
        /// when there is no hint. A hint that does not exist throws — see
        /// <see cref="ResolveSymbolSearchRoot"/>.
        /// </summary>
        private static string ResolveSymbolSearchStartDirectory(string pathHint, string workingDirectory)
        {
            string session = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(workingDirectory);

            string startDir = session;
            if (!string.IsNullOrWhiteSpace(pathHint))
            {
                string full;
                try
                {
                    full = Path.GetFullPath(pathHint, session);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "path '" + pathHint + "' is not a usable path (" + ex.Message +
                        "). Pass a file or a directory inside the solution to search.");
                }

                if (File.Exists(full))
                    startDir = Path.GetDirectoryName(full) ?? session;
                else if (Directory.Exists(full))
                    startDir = full;
                else
                    throw new InvalidOperationException(
                        "path '" + pathHint + "' does not exist (resolved to '" + full +
                        "'). Pass an existing file or directory inside the solution to search.");
            }

            return startDir;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_gate)
            {
                foreach (var host in _hosts.Values)
                {
                    host.Dispose();
                }

                _hosts.Clear();
            }
        }

        private static LanguageServerKind ResolveLanguageKind(string language)
        {
            if (!string.IsNullOrWhiteSpace(language))
            {
                var l = language.Trim();
                if (l.Equals("typescript", StringComparison.OrdinalIgnoreCase) ||
                    l.Equals("ts", StringComparison.OrdinalIgnoreCase) ||
                    l.Equals("javascript", StringComparison.OrdinalIgnoreCase) ||
                    l.Equals("js", StringComparison.OrdinalIgnoreCase))
                    return LanguageServerKind.TypeScript;
            }
            return LanguageServerKind.CSharp;
        }

        private LanguageServerHost GetHostForPath(string fullPath)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LanguageServerRouter));

            fullPath = Path.GetFullPath(fullPath);
            var kind = LanguageServerProfile.KindForPath(fullPath);
            if (kind == LanguageServerKind.Unknown)
            {
                throw new InvalidOperationException(
                    "LSP does not support this file type. Supported: " +
                    LanguageServerProfile.SupportedExtensionsHelp() +
                    ". Got: " + Path.GetFileName(fullPath));
            }

            string projectRoot = WorkspaceRootResolver.Resolve(kind, fullPath, _workingDirectory);
            return GetOrCreateHost(kind, projectRoot);
        }

        /// <summary>Get-or-create the single host instance for a (kind|projectRoot) pair.</summary>
        private LanguageServerHost GetOrCreateHost(LanguageServerKind kind, string projectRoot)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LanguageServerRouter));

            string hostKey = kind + "|" + projectRoot;

            lock (_gate)
            {
                if (_hosts.TryGetValue(hostKey, out var existing))
                {
                    return existing;
                }

                var profile = LanguageServerProfile.ForKind(kind);
                string pathOverride = kind == LanguageServerKind.CSharp
                    ? _csharpServerPathOverride
                    : _typescriptServerPathOverride;

                var host = new LanguageServerHost(projectRoot, profile, pathOverride);
                _hosts[hostKey] = host;
                return host;
            }
        }
    }
}
