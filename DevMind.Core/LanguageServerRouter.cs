// File: LanguageServerRouter.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Routes LSP tool calls to the correct language server (C# vs TypeScript) per file extension.

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
