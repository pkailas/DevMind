// File: LanguageServerRouter.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Routes LSP tool calls to the correct language server (C# vs TypeScript) per file extension.
// v1.1: FindSymbolAsync — solution-wide workspace/symbol search, routed by language (no file
//   path); reuses the same per-(kind|root) host instance as the position-based tools.

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
        /// </summary>
        public async Task<string> FindSymbolAsync(
            string query,
            int maxResults,
            string language,
            CancellationToken cancellationToken = default)
        {
            var kind = ResolveLanguageKind(language);
            string root = kind == LanguageServerKind.CSharp
                ? (WorkspaceRootResolver.FindSolutionDirectory(_workingDirectory) ?? _workingDirectory)
                : (WorkspaceRootResolver.FindTypeScriptProjectDirectory(_workingDirectory) ?? _workingDirectory);

            return await GetOrCreateHost(kind, root)
                .FindSymbolAsync(query, maxResults, cancellationToken)
                .ConfigureAwait(false);
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
