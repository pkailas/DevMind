// File: LanguageServerHost.cs  v2.2
// v2.2: advertise workspace/symbol + FindSymbolAsync (solution-wide semantic symbol
//   search); empty results route through the same A11 readiness path (no false-empty).
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Session-scoped language server host for one profile (C# or TypeScript).
// v2.1 (A11): three-state readiness (NotReady/Ready/Degraded) replaces the silent
//   "serve anyway" path. Tool results report not-ready/degraded honestly instead of
//   collapsing into a false "no issues"/"no results". Late projectInitializationComplete
//   upgrades DEGRADED→READY via a pull-based poll on the dispatch thread.

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
    /// Readiness of the underlying language server's workspace/index.
    /// NotReady = initializing / projectInitializationComplete not yet received;
    /// Ready = index confirmed loaded (notification received, or a push server that
    /// needs no project-init gate); Degraded = the 120s wait elapsed and we are serving
    /// anyway without confirmation (results may be empty-because-unindexed, not absent).
    /// </summary>
    public enum LspReadiness { NotReady, Ready, Degraded }

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

        // Readiness state. All reads/writes are under _gate. Writes happen only on the
        // dispatch (consumer) thread: at init, and via the pull-based RefreshReadiness()
        // upgrade — never from the LspClient reader thread, so there is no second writer.
        private LspReadiness _readiness = LspReadiness.NotReady;
        // One-shot guard so the "serving DEGRADED" stderr line is emitted at most once.
        private bool _degradedServeLogged;

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
            RefreshReadiness();
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

            var state = SnapshotReadiness();

            if (diags.Count == 0)
            {
                // A genuine clean is reported ONLY when the index is confirmed READY and the
                // pull actually returned zero. NotReady/Degraded never yields "no issues".
                if (state == LspReadiness.Ready)
                    return "get_diagnostics: no issues reported for " + Path.GetFileName(fullPath) +
                           " (" + _profile.DisplayName + ")";

                if (state == LspReadiness.Degraded)
                {
                    MaybeLogDegradedServe("get_diagnostics");
                    return "get_diagnostics: project indexing did not complete within the timeout " +
                           "(degraded) — diagnostics are not reliable and may be incomplete. Do NOT " +
                           "treat this as clean; retry shortly, or use run_build to confirm.";
                }

                return "get_diagnostics: project is still indexing (language server not ready) — " +
                       "diagnostics are not yet available. This is NOT a clean result; retry in a few seconds.";
            }

            var lines = diags.Take(50).Select(d =>
                $"{Path.GetFileName(fullPath)}:{d.Line}:{d.Character}: {d.Severity}" +
                (string.IsNullOrEmpty(d.Source) ? "" : $" ({d.Source})") +
                $": {d.Message}");
            var suffix = diags.Count > 50 ? $"\n... and {diags.Count - 50} more" : "";
            var body = string.Join("\n", lines) + suffix;

            // Real diagnostics are always surfaced; flag when the index is degraded so the
            // model knows the list may be partial.
            if (state == LspReadiness.Degraded)
            {
                MaybeLogDegradedServe("get_diagnostics");
                body += "\n(note: project indexing is degraded — this list may be incomplete)";
            }
            return body;
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

            return FinishLocationResult("go_to_definition", FormatLocations(result));
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

            return FinishLocationResult("find_references", FormatLocations(result));
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

            RefreshReadiness();
            string hover = ExtractHoverText(result);
            var state = SnapshotReadiness();

            if (!string.IsNullOrWhiteSpace(hover))
            {
                if (state == LspReadiness.Degraded)
                {
                    MaybeLogDegradedServe("hover");
                    return hover + "\n(note: project indexing is degraded — information may be incomplete)";
                }
                return hover;
            }

            // No hover info: a genuine "no information" only when READY; otherwise honest.
            if (state == LspReadiness.Ready)
                return "hover: (no information at this position)";
            return NavNotReadyMessage("hover", state);
        }

        /// <summary>
        /// Extract hover text from an LSP hover result, or null/empty when the result
        /// carries no information. Empties are reinterpreted by readiness in the caller.
        /// </summary>
        private static string ExtractHoverText(JToken result)
        {
            if (result == null || result.Type == JTokenType.Null) return null;

            var contents = result["contents"];
            if (contents == null) return null;

            if (contents.Type == JTokenType.String)
                return contents.ToString();

            if (contents is JObject marked)
                return marked["value"]?.ToString();

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
                return parts.Count == 0 ? null : string.Join("\n\n", parts);
            }

            return contents.ToString();
        }

        /// <summary>
        /// Solution-wide semantic symbol search (workspace/symbol). No file/position — takes a
        /// name query and returns matching types/members across the loaded solution. Empty
        /// results route through the same readiness path as the navigation tools, so a false
        /// "no symbols" is impossible when the index is NotReady/Degraded.
        /// </summary>
        public async Task<string> FindSymbolAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken = default)
        {
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

            var result = await _client.RequestAsync(
                "workspace/symbol",
                new JObject { ["query"] = query ?? "" },
                cancellationToken).ConfigureAwait(false);

            string readyEmpty =
                "find_symbol: ran the search but found no symbols matching \"" + query +
                "\" in the loaded " + _profile.DisplayName + " solution.";
            return FinishLocationResult("find_symbol", FormatSymbols(result, maxResults), readyEmpty);
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

            // CONCURRENCY INVARIANT: every entry to this host (model tool calls AND the
            // detached startup pre-warm) is funnelled through McpServices.EnqueueAsync,
            // whose single-reader channel runs work items strictly one at a time. That
            // serialization — not this method — is what prevents two callers from both
            // observing not-initialized and both running StartAndInitializeAsync (the A3
            // double-init race). The await below is intentionally OUTSIDE _gate, so this
            // host is NOT self-safe under concurrent entry: if a future refactor lets two
            // callers reach here concurrently (bypassing the channel), single-flight must
            // be added here (e.g. an init Task latch) or the race returns.
            lock (_gate)
            {
                if (_initialized && _client != null && !_client.HasExited)
                    return;
            }

            await StartAndInitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Current readiness snapshot (lock-guarded).</summary>
        private LspReadiness SnapshotReadiness()
        {
            lock (_gate) return _readiness;
        }

        /// <summary>
        /// Pull-based late upgrade: if we are serving DEGRADED and the server has since
        /// emitted projectInitializationComplete, flip to READY. Runs on the dispatch
        /// (consumer) thread at each tool entry — the only writer besides init — so no
        /// cross-thread write race with the LspClient reader thread.
        /// </summary>
        private void RefreshReadiness()
        {
            lock (_gate)
            {
                if (_readiness == LspReadiness.Degraded &&
                    _client != null &&
                    !string.IsNullOrEmpty(_server.ReadyNotification) &&
                    _client.HasSeenNotification(_server.ReadyNotification))
                {
                    _readiness = LspReadiness.Ready;
                }
            }
        }

        /// <summary>Emit the "serving DEGRADED" stderr line at most once per init.</summary>
        private void MaybeLogDegradedServe(string toolName)
        {
            bool log;
            lock (_gate)
            {
                log = !_degradedServeLogged;
                _degradedServeLogged = true;
            }
            if (log)
                Console.Error.WriteLine(
                    "[LSP] Serving in DEGRADED state (" + _server.ReadyNotification +
                    " not received within 120s) — " + toolName + " and other LSP results are " +
                    "reported as not-yet-reliable until indexing completes.");
        }

        /// <summary>
        /// Honest not-ready/degraded message for the navigation tools (definition /
        /// references / hover). Includes the framework-symbol fallback pointer, since
        /// docs/web ARE legitimate alternatives for framework/library symbol questions.
        /// </summary>
        private string NavNotReadyMessage(string toolName, LspReadiness state)
        {
            if (state == LspReadiness.Degraded)
            {
                MaybeLogDegradedServe(toolName);
                return toolName + ": language server indexing did not complete (degraded) — " +
                       "results may be incomplete, not necessarily absent. Retry, or for framework/BCL/" +
                       "library symbols use microsoft.docs.mcp (API docs) or web_search / web_fetch.";
            }
            return toolName + ": language server is still indexing (not ready) — results are not yet " +
                   "available, not necessarily absent. Retry shortly. For framework/BCL or third-party " +
                   "library symbols, you can also consult microsoft.docs.mcp (API docs) or web_search / web_fetch.";
        }

        /// <summary>
        /// Finalize a location-list tool result (definition / references). Real results are
        /// always returned (with a degraded caveat appended when serving DEGRADED); an empty
        /// result yields a genuine "no results" ONLY when READY, otherwise the honest signal.
        /// </summary>
        private string FinishLocationResult(string toolName, string formattedOrNull, string readyEmptyMessage = null)
        {
            RefreshReadiness();
            var state = SnapshotReadiness();
            if (formattedOrNull != null)
            {
                if (state == LspReadiness.Degraded)
                {
                    MaybeLogDegradedServe(toolName);
                    return formattedOrNull + "\n(note: project indexing is degraded — results may be incomplete)";
                }
                return formattedOrNull;
            }

            // Empty result: a genuine empty ONLY when READY (caller may supply a tool-specific
            // message, e.g. find_symbol echoes the query); otherwise the honest not-ready signal.
            if (state == LspReadiness.Ready)
                return readyEmptyMessage ?? (toolName + ": no results");
            return NavNotReadyMessage(toolName, state);
        }

        private async Task StartAndInitializeAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _client?.Dispose();
                _client = null;
                _initialized = false;
                _readiness = LspReadiness.NotReady;
                _degradedServeLogged = false;
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
                        // Advertise solution-wide symbol search so Roslyn registers
                        // workspaceSymbolProvider. Minimal cap — Roslyn returns full
                        // Location, so no resolveSupport/symbolKind value-set needed.
                        ["symbol"] = new JObject
                        {
                            ["dynamicRegistration"] = true,
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
            //
            // Push servers (csharp-ls / TypeScript: no SolutionOpenUri, no
            // ReadyNotification) have no project-init gate and are READY on init — the
            // three-state honesty is a Roslyn-pull concern only.
            LspReadiness initialReadiness = LspReadiness.Ready;

            if (!string.IsNullOrEmpty(_server.SolutionOpenUri))
            {
                client.Notify("solution/open", new JObject
                {
                    ["solution"] = _server.SolutionOpenUri,
                });

                if (!string.IsNullOrEmpty(_server.ReadyNotification))
                {
                    int initTimeoutMs = ResolveProjectInitTimeoutMs();
                    bool ready = await client
                        .WaitForNotificationAsync(
                            _server.ReadyNotification,
                            TimeSpan.FromMilliseconds(initTimeoutMs),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (ready)
                    {
                        initialReadiness = LspReadiness.Ready;
                    }
                    else
                    {
                        // Serve anyway, but track DEGRADED so empty results are reported
                        // honestly rather than as a false clean. A late notification can
                        // still upgrade this to READY via RefreshReadiness().
                        initialReadiness = LspReadiness.Degraded;
                        Console.Error.WriteLine(
                            "[LSP] Roslyn project load did not signal " +
                            _server.ReadyNotification + " within " + initTimeoutMs +
                            "ms; serving anyway (DEGRADED).");
                    }
                }
            }

            lock (_gate)
            {
                _client = client;
                _initialized = true;
                _readiness = initialReadiness;
            }
        }

        /// <summary>
        /// Timeout (ms) to await projectInitializationComplete before serving DEGRADED.
        /// Default 120000. Overridable via DEVMIND_LSP_PROJECT_INIT_TIMEOUT_MS (large
        /// solutions may need longer; tests use a short value to force DEGRADED).
        /// </summary>
        private static int ResolveProjectInitTimeoutMs()
        {
            var v = Environment.GetEnvironmentVariable("DEVMIND_LSP_PROJECT_INIT_TIMEOUT_MS");
            if (!string.IsNullOrEmpty(v) && int.TryParse(v, out var ms) && ms > 0)
                return ms;
            return 120_000;
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

        /// <summary>
        /// Format an LSP location result as path:line:col lines, or return null when the
        /// result carries no locations — the caller (FinishLocationResult) decides whether
        /// an empty result is a genuine "no results" (READY) or an honest not-ready signal.
        /// </summary>
        private static string FormatLocations(JToken result)
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
                return null;

            return string.Join("\n", locations.Take(100)) +
                   (locations.Count > 100 ? $"\n... and {locations.Count - 100} more" : "");
        }

        /// <summary>
        /// Format a workspace/symbol result (SymbolInformation[] / WorkspaceSymbol[]) as a
        /// readable "Kind Name — path:line:col (container)" list, or null when empty (the
        /// readiness wrapper decides the empty message). Capped at maxResults.
        /// </summary>
        private static string FormatSymbols(JToken result, int maxResults)
        {
            if (!(result is JArray arr) || arr.Count == 0) return null;

            var lines = new List<string>();
            foreach (var item in arr)
            {
                if (!(item is JObject sym)) continue;
                string name = sym["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                string kind = SymbolKindName(sym["kind"]?.Value<int>() ?? 0);
                string container = sym["containerName"]?.ToString();

                string where = "";
                if (sym["location"] is JObject loc)
                {
                    string uri = loc["uri"]?.ToString();
                    if (uri != null)
                    {
                        string path = UriToPath(uri);
                        if ((loc["range"] as JObject)?["start"] is JObject start)
                        {
                            int line = (start["line"]?.Value<int>() ?? 0) + 1;
                            int col = (start["character"]?.Value<int>() ?? 0) + 1;
                            where = " — " + path + ":" + line + ":" + col;
                        }
                        else
                        {
                            where = " — " + path;
                        }
                    }
                }

                string suffix = string.IsNullOrEmpty(container) ? "" : " (" + container + ")";
                lines.Add(kind + " " + name + where + suffix);
            }

            if (lines.Count == 0) return null;

            int cap = maxResults > 0 ? maxResults : 50;
            if (lines.Count > cap)
                return string.Join("\n", lines.GetRange(0, cap)) +
                       "\n... and " + (lines.Count - cap) + " more";
            return string.Join("\n", lines);
        }

        /// <summary>
        /// LSP SymbolKind (1..26 per the spec) → readable name, with a safe fallback so an
        /// unexpected/unmapped kind renders readably (never blank or throwing).
        /// </summary>
        private static string SymbolKindName(int kind)
        {
            switch (kind)
            {
                case 1:  return "File";
                case 2:  return "Module";
                case 3:  return "Namespace";
                case 4:  return "Package";
                case 5:  return "Class";
                case 6:  return "Method";
                case 7:  return "Property";
                case 8:  return "Field";
                case 9:  return "Constructor";
                case 10: return "Enum";
                case 11: return "Interface";
                case 12: return "Function";
                case 13: return "Variable";
                case 14: return "Constant";
                case 15: return "String";
                case 16: return "Number";
                case 17: return "Boolean";
                case 18: return "Array";
                case 19: return "Object";
                case 20: return "Key";
                case 21: return "Null";
                case 22: return "EnumMember";
                case 23: return "Struct";
                case 24: return "Event";
                case 25: return "Operator";
                case 26: return "TypeParameter";
                default: return kind > 0 ? "Symbol(" + kind + ")" : "Symbol";
            }
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
