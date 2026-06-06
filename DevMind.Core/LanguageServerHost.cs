// File: LanguageServerHost.cs  v2.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Session-scoped language server host for one profile (C# or TypeScript).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DevMind
{
    /// <summary>
    /// Manages a long-lived language-server child process and document sync for one workspace.
    /// </summary>
    public sealed class LanguageServerHost : IDisposable
    {
        private readonly string _workingDirectory;
        private readonly LanguageServerProfile _profile;
        private readonly ResolvedLanguageServer _server;
        private readonly object _gate = new object();
        private readonly HashSet<string> _openDocuments =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private LspClient _client;
        private bool _initialized;
        private bool _disposed;

        public LanguageServerHost(
            string workingDirectory,
            LanguageServerProfile profile,
            string serverPathOverride = null)
        {
            _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(workingDirectory);
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _server = _profile.ResolveServer(_workingDirectory, serverPathOverride);
        }

        public static bool IsEnabled()
        {
            var env = Environment.GetEnvironmentVariable("DEVMIND_LSP_ENABLED");
            if (string.IsNullOrEmpty(env)) return true;
            return !string.Equals(env, "false", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(env, "0", StringComparison.OrdinalIgnoreCase);
        }

        public static string PathToFileUri(string fullPath)
        {
            var normalized = Path.GetFullPath(fullPath).Replace('\\', '/');
            if (normalized.Length > 1 && normalized[1] == ':')
                return "file:///" + normalized;
            return "file://" + normalized;
        }

        public async Task<string> GetDiagnosticsAsync(
            string fullPath,
            CancellationToken cancellationToken = default)
        {
            EnsureSupportedFile(fullPath);
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            string uri = PathToFileUri(fullPath);

            IReadOnlyList<LspDiagnostic> diags;
            if (_server.UsesPullDiagnostics)
            {
                // Roslyn LS: open the document, then pull diagnostics on demand.
                await SyncDocumentAsync(fullPath, cancellationToken).ConfigureAwait(false);
                diags = await _client
                    .RequestPullDiagnosticsAsync(uri, cancellationToken, 30_000)
                    .ConfigureAwait(false);
            }
            else
            {
                // csharp-ls / TypeScript: push model — wait for publishDiagnostics.
                lock (_gate)
                {
                    _client.ClearDiagnostics(uri);
                }

                await SyncDocumentAsync(fullPath, cancellationToken).ConfigureAwait(false);

                diags = await _client.WaitForDiagnosticsAsync(
                    uri, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            }

            if (diags.Count == 0)
            {
                return "get_diagnostics: no issues reported for " + Path.GetFileName(fullPath) +
                       " (" + _profile.DisplayName + ")";
            }

            var lines = diags.Take(50).Select(d =>
                $"{Path.GetFileName(fullPath)}:{d.Line}:{d.Character}: {d.Severity}" +
                (string.IsNullOrEmpty(d.Source) ? "" : $" ({d.Source})") +
                $": {d.Message}");
            var suffix = diags.Count > 50 ? $"\n... and {diags.Count - 50} more" : "";
            return string.Join("\n", lines) + suffix;
        }

        public async Task<string> GoToDefinitionAsync(
            string fullPath,
            int line,
            int character,
            CancellationToken cancellationToken = default)
        {
            EnsureSupportedFile(fullPath);
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            await SyncDocumentAsync(fullPath, cancellationToken).ConfigureAwait(false);

            var result = await _client.RequestAsync(
                "textDocument/definition",
                new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = PathToFileUri(fullPath) },
                    ["position"] = ToLspPosition(line, character),
                },
                cancellationToken).ConfigureAwait(false);

            return FormatLocations("go_to_definition", result);
        }

        public async Task<string> FindReferencesAsync(
            string fullPath,
            int line,
            int character,
            CancellationToken cancellationToken = default)
        {
            EnsureSupportedFile(fullPath);
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            await SyncDocumentAsync(fullPath, cancellationToken).ConfigureAwait(false);

            var result = await _client.RequestAsync(
                "textDocument/references",
                new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = PathToFileUri(fullPath) },
                    ["position"] = ToLspPosition(line, character),
                    ["context"] = new JObject { ["includeDeclaration"] = true },
                },
                cancellationToken).ConfigureAwait(false);

            return FormatLocations("find_references", result);
        }

        public async Task<string> HoverAsync(
            string fullPath,
            int line,
            int character,
            CancellationToken cancellationToken = default)
        {
            EnsureSupportedFile(fullPath);
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            await SyncDocumentAsync(fullPath, cancellationToken).ConfigureAwait(false);

            var result = await _client.RequestAsync(
                "textDocument/hover",
                new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = PathToFileUri(fullPath) },
                    ["position"] = ToLspPosition(line, character),
                },
                cancellationToken).ConfigureAwait(false);

            if (result == null || result.Type == JTokenType.Null)
                return "hover: (no information at this position)";

            var contents = result["contents"];
            if (contents == null)
                return "hover: (empty)";

            if (contents.Type == JTokenType.String)
                return contents.ToString();

            if (contents is JObject marked)
            {
                var value = marked["value"]?.ToString() ?? "";
                return string.IsNullOrWhiteSpace(value) ? "hover: (empty)" : value;
            }

            if (contents is JArray arr)
            {
                var parts = new List<string>();
                foreach (var item in arr)
                {
                    if (item.Type == JTokenType.String)
                        parts.Add(item.ToString());
                    else if (item is JObject m)
                        parts.Add(m["value"]?.ToString() ?? "");
                }
                return parts.Count == 0 ? "hover: (empty)" : string.Join("\n\n", parts);
            }

            return contents.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_gate)
            {
                _client?.Dispose();
                _client = null;
                _initialized = false;
                _openDocuments.Clear();
            }
        }

        private async Task EnsureReadyAsync(CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LanguageServerHost));

            lock (_gate)
            {
                if (_initialized && _client != null && !_client.HasExited)
                    return;
            }

            await StartAndInitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task StartAndInitializeAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _client?.Dispose();
                _client = null;
                _initialized = false;
                _openDocuments.Clear();
            }

            var psi = new ProcessStartInfo(_server.Command, _server.Args)
            {
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("Failed to start language server: " + _server.Command);

            // Drain stderr so the pipe does not fill (diagnostics only).
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Console.Error.WriteLine("[LSP] " + e.Data);
            };
            process.BeginErrorReadLine();

            var client = new LspClient(process);
            string rootUri = PathToFileUri(_workingDirectory);

            var initParams = new JObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = rootUri,
                ["capabilities"] = new JObject
                {
                    ["textDocument"] = new JObject
                    {
                        ["publishDiagnostics"] = new JObject(),
                        // Advertise pull diagnostics so Roslyn LS dynamically
                        // registers textDocument/diagnostic. Harmless to csharp-ls.
                        ["diagnostic"] = new JObject
                        {
                            ["dynamicRegistration"] = true,
                        },
                        ["hover"] = new JObject
                        {
                            ["contentFormat"] = new JArray("markdown", "plaintext"),
                        },
                        ["definition"] = new JObject(),
                        ["references"] = new JObject(),
                    },
                    ["workspace"] = new JObject
                    {
                        ["workspaceFolders"] = true,
                        // Roslyn LS issues workspace/configuration requests on
                        // startup; declare support (LspClient answers them).
                        ["configuration"] = true,
                        ["diagnostics"] = new JObject
                        {
                            ["refreshSupport"] = true,
                        },
                    },
                    ["window"] = new JObject
                    {
                        ["workDoneProgress"] = true,
                    },
                },
                ["workspaceFolders"] = new JArray
                {
                    new JObject
                    {
                        ["uri"] = rootUri,
                        ["name"] = Path.GetFileName(_workingDirectory.TrimEnd(Path.DirectorySeparatorChar)),
                    },
                },
            };

            await client.RequestAsync("initialize", initParams, cancellationToken, 60_000)
                .ConfigureAwait(false);
            client.Notify("initialized", new JObject());

            // Roslyn LS: load the solution explicitly (no --solution CLI arg) and
            // wait for the project graph to finish loading before serving requests,
            // so the first get_diagnostics isn't racing an empty workspace.
            if (!string.IsNullOrEmpty(_server.SolutionOpenUri))
            {
                client.Notify("solution/open", new JObject
                {
                    ["solution"] = _server.SolutionOpenUri,
                });

                if (!string.IsNullOrEmpty(_server.ReadyNotification))
                {
                    bool ready = await client
                        .WaitForNotificationAsync(
                            _server.ReadyNotification,
                            TimeSpan.FromSeconds(120),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!ready)
                    {
                        Console.Error.WriteLine(
                            "[LSP] Roslyn project load did not signal " +
                            _server.ReadyNotification + " within 120s; serving anyway.");
                    }
                }
            }

            lock (_gate)
            {
                _client = client;
                _initialized = true;
            }
        }

        private async Task SyncDocumentAsync(string fullPath, CancellationToken cancellationToken)
        {
            string uri = PathToFileUri(fullPath);
            string text = File.ReadAllText(fullPath, Encoding.UTF8);
            int version = 1;

            bool needsOpen;
            lock (_gate)
            {
                needsOpen = !_openDocuments.Contains(uri);
                if (needsOpen)
                    _openDocuments.Add(uri);
            }

            void DidOpen()
            {
                _client.Notify("textDocument/didOpen", new JObject
                {
                    ["textDocument"] = new JObject
                    {
                        ["uri"] = uri,
                        ["languageId"] = _profile.LanguageIdForPath(fullPath),
                        ["version"] = version,
                        ["text"] = text,
                    },
                });
            }

            if (needsOpen)
            {
                DidOpen();
            }
            else if (_server.UsesPullDiagnostics)
            {
                // Roslyn LS throws a NullReferenceException on a full-document
                // didChange (its handler expects ranged incremental edits). For
                // our read-from-disk model, re-open the document instead: close
                // then open with the current text. Robust and cheap.
                _client.Notify("textDocument/didClose", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = uri },
                });
                DidOpen();
            }
            else
            {
                _client.Notify("textDocument/didChange", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = uri, ["version"] = version + 1 },
                    ["contentChanges"] = new JArray
                    {
                        new JObject { ["text"] = text },
                    },
                });
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        private static JObject ToLspPosition(int line1Based, int character1Based)
        {
            return new JObject
            {
                ["line"] = Math.Max(0, line1Based - 1),
                ["character"] = Math.Max(0, character1Based - 1),
            };
        }

        private static string FormatLocations(string toolName, JToken result)
        {
            var locations = new List<string>();

            void AddLocation(JObject loc)
            {
                var uri = loc["uri"]?.ToString();
                var range = loc["range"] as JObject;
                var start = range?["start"] as JObject;
                if (uri == null || start == null) return;
                int line = (start["line"]?.Value<int>() ?? 0) + 1;
                int col = (start["character"]?.Value<int>() ?? 0) + 1;
                string path = UriToPath(uri);
                locations.Add($"{path}:{line}:{col}");
            }

            if (result is JArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JObject o) AddLocation(o);
                }
            }
            else if (result is JObject single)
            {
                if (single["uri"] != null)
                    AddLocation(single);
                else if (single["targetUri"] != null)
                {
                    var target = new JObject
                    {
                        ["uri"] = single["targetUri"],
                        ["range"] = single["targetRange"] ?? single["range"],
                    };
                    AddLocation(target);
                }
            }

            if (locations.Count == 0)
                return toolName + ": no results";

            return string.Join("\n", locations.Take(100)) +
                   (locations.Count > 100 ? $"\n... and {locations.Count - 100} more" : "");
        }

        private static string UriToPath(string uri)
        {
            if (uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                return uri.Substring(8).Replace('/', Path.DirectorySeparatorChar);
            if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return uri.Substring(7).Replace('/', Path.DirectorySeparatorChar);
            return uri;
        }

        private void EnsureSupportedFile(string fullPath)
        {
            if (!_profile.SupportsPath(fullPath))
            {
                throw new InvalidOperationException(
                    "LSP (" + _profile.DisplayName + ") does not support this file. Supported: " +
                    LanguageServerProfile.SupportedExtensionsHelp() +
                    ". Got: " + Path.GetFileName(fullPath));
            }

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("File not found: " + fullPath);
            }
        }
    }
}
