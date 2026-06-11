// File: LspToolService.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shared host-side facade over LanguageServerRouter for the five LSP tools
// (get_diagnostics, go_to_definition, find_references, hover, find_symbol).
// Used by the skin hosts (ConsoleAgenticHost, TuiAgenticHost); McpServer reaches
// the same LanguageServerRouter through McpServices.Lsp. The facade centralizes
// the DEVMIND_LSP_ENABLED gating and error-to-string wrapping so every caller
// returns the same messages — keeping the tool surface from diverging again.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// Lazily creates one <see cref="LanguageServerRouter"/> per working directory and
    /// exposes the LSP tools with uniform disabled/error messaging. All failures return
    /// a "[tool error] …" string rather than throwing, so callers can feed the message
    /// straight back to the model.
    /// </summary>
    public sealed class LspToolService : IDisposable
    {
        private readonly object _gate = new object();
        private LanguageServerRouter _router;
        private string _workingDirectory;
        private bool _disposed;

        public LspToolService(string workingDirectory)
        {
            _workingDirectory = workingDirectory;
        }

        /// <summary>
        /// Points the service at a new working directory (used by the TUI /dir command).
        /// The current router is disposed; a new one is created lazily on next use.
        /// </summary>
        public void SetWorkingDirectory(string workingDirectory)
        {
            lock (_gate)
            {
                if (string.Equals(_workingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase))
                    return;
                _workingDirectory = workingDirectory;
                _router?.Dispose();
                _router = null;
            }
        }

        public Task<string> GetDiagnosticsAsync(string fullPath, CancellationToken cancellationToken = default)
            => InvokeAsync("get_diagnostics", fullPath,
                r => r.GetDiagnosticsAsync(fullPath, cancellationToken));

        public Task<string> GoToDefinitionAsync(string fullPath, int line, int character,
            CancellationToken cancellationToken = default)
            => InvokeAsync("go_to_definition", fullPath,
                r => r.GoToDefinitionAsync(fullPath, line, character, cancellationToken));

        public Task<string> FindReferencesAsync(string fullPath, int line, int character,
            CancellationToken cancellationToken = default)
            => InvokeAsync("find_references", fullPath,
                r => r.FindReferencesAsync(fullPath, line, character, cancellationToken));

        public Task<string> HoverAsync(string fullPath, int line, int character,
            CancellationToken cancellationToken = default)
            => InvokeAsync("hover", fullPath,
                r => r.HoverAsync(fullPath, line, character, cancellationToken));

        public Task<string> FindSymbolAsync(string query, int maxResults, string language,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Task.FromResult("[find_symbol] Provide a non-empty symbol name to search for.");

            int cap = Math.Min(maxResults > 0 ? maxResults : 50, 100);
            return InvokeAsync("find_symbol", null,
                r => r.FindSymbolAsync(query, cap, language ?? "csharp", cancellationToken));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _router?.Dispose();
                _router = null;
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private async Task<string> InvokeAsync(string toolName, string label,
            Func<LanguageServerRouter, Task<string>> call)
        {
            if (!LanguageServerRouter.IsEnabled())
                return $"[{toolName}] LSP is disabled (set DEVMIND_LSP_ENABLED=true to enable).";

            try
            {
                return await call(GetRouter()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string prefix = string.IsNullOrEmpty(label) ? "" : $"{label}: ";
                return $"[{toolName} error] {prefix}{ex.Message}";
            }
        }

        private LanguageServerRouter GetRouter()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(LspToolService));
                return _router ??= new LanguageServerRouter(
                    _workingDirectory,
                    Environment.GetEnvironmentVariable("DEVMIND_LSP_SERVER_PATH"),
                    Environment.GetEnvironmentVariable("DEVMIND_LSP_TYPESCRIPT_PATH"));
            }
        }
    }
}
