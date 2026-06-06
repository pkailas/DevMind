// File: LspClient.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Low-level LSP JSON-RPC over stdio with Content-Length framing.
// One reader task dispatches responses to pending requests and handles
// server-initiated notifications (e.g. textDocument/publishDiagnostics).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevMind
{
    /// <summary>Compiler diagnostic surfaced from LSP publishDiagnostics.</summary>
    public sealed class LspDiagnostic
    {
        public int Line { get; }
        public int Character { get; }
        public string Severity { get; }
        public string Message { get; }
        public string Source { get; }

        public LspDiagnostic(int line, int character, string severity, string message, string source)
        {
            Line = line;
            Character = character;
            Severity = severity;
            Message = message;
            Source = source;
        }
    }

    /// <summary>
    /// Speaks LSP over a child process's stdin/stdout. Not thread-safe for
    /// concurrent callers — LanguageServerHost serializes access.
    /// </summary>
    public sealed class LspClient : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _stdin;
        private readonly Stream _stdout;
        private readonly object _writeLock = new object();
        private readonly object _pendingLock = new object();
        private readonly Dictionary<int, TaskCompletionSource<JToken>> _pending =
            new Dictionary<int, TaskCompletionSource<JToken>>();
        private readonly Dictionary<string, List<LspDiagnostic>> _diagnosticsByUri =
            new Dictionary<string, List<LspDiagnostic>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _diagLock = new object();

        // Notification-arrival tracking, used to await one-shot server
        // notifications such as workspace/projectInitializationComplete.
        private readonly object _notifyLock = new object();
        private readonly HashSet<string> _seenNotifications =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TaskCompletionSource<bool>>> _notifyWaiters =
            new Dictionary<string, List<TaskCompletionSource<bool>>>(StringComparer.Ordinal);

        private int _nextId = 1;
        private readonly Task _readerTask;
        private bool _disposed;

        public LspClient(Process process)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            if (_process.StandardInput?.BaseStream == null || _process.StandardOutput == null)
                throw new ArgumentException("Process must have redirected stdin and stdout.");

            _stdin = _process.StandardInput.BaseStream;
            _stdout = _process.StandardOutput.BaseStream;
            _readerTask = Task.Run((Func<Task>)ReadLoopAsync);
        }

        public bool HasExited => _process.HasExited;

        public IReadOnlyList<LspDiagnostic> GetCachedDiagnostics(string documentUri)
        {
            lock (_diagLock)
            {
                if (_diagnosticsByUri.TryGetValue(documentUri, out var list))
                    return list;
                return Array.Empty<LspDiagnostic>();
            }
        }

        public void ClearDiagnostics(string documentUri)
        {
            lock (_diagLock)
            {
                _diagnosticsByUri.Remove(documentUri);
            }
        }

        /// <summary>
        /// Wait until publishDiagnostics arrives for <paramref name="documentUri"/>
        /// or <paramref name="timeout"/> elapses.
        /// </summary>
        public async Task<IReadOnlyList<LspDiagnostic>> WaitForDiagnosticsAsync(
            string documentUri,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_diagLock)
                {
                    if (_diagnosticsByUri.ContainsKey(documentUri))
                        return _diagnosticsByUri[documentUri];
                }
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            return GetCachedDiagnostics(documentUri);
        }

        /// <summary>
        /// Wait until the server sends a one-shot notification with the given
        /// <paramref name="method"/> (e.g. workspace/projectInitializationComplete),
        /// or the timeout elapses. Returns true if the notification arrived
        /// (including if it arrived before this call), false on timeout.
        /// </summary>
        public async Task<bool> WaitForNotificationAsync(
            string method,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool> tcs;
            lock (_notifyLock)
            {
                if (_seenNotifications.Contains(method)) return true;
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_notifyWaiters.TryGetValue(method, out var list))
                {
                    list = new List<TaskCompletionSource<bool>>();
                    _notifyWaiters[method] = list;
                }
                list.Add(tcs);
            }

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(timeout);
                using (cts.Token.Register(() => tcs.TrySetResult(false)))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Pull-model diagnostics (Roslyn LS): sends a textDocument/diagnostic
        /// request and parses the returned report. The document must already be
        /// open (didOpen) on the server.
        /// </summary>
        public async Task<IReadOnlyList<LspDiagnostic>> RequestPullDiagnosticsAsync(
            string documentUri,
            CancellationToken cancellationToken = default,
            int timeoutMs = 30_000)
        {
            var result = await RequestAsync(
                "textDocument/diagnostic",
                new JObject { ["textDocument"] = new JObject { ["uri"] = documentUri } },
                cancellationToken,
                timeoutMs).ConfigureAwait(false);

            // Report shape: { kind: "full"|"unchanged", items: [ ...diagnostics ] }.
            // "unchanged" carries no items (nothing changed since last resultId).
            var items = (result as JObject)?["items"] as JArray;
            return ParseDiagnostics(items);
        }

        public async Task<JToken> RequestAsync(
            string method,
            JObject parameters,
            CancellationToken cancellationToken = default,
            int timeoutMs = 30_000)
        {
            int id;
            TaskCompletionSource<JToken> tcs;
            lock (_pendingLock)
            {
                id = _nextId++;
                tcs = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[id] = tcs;
            }

            var payload = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JObject(),
            };

            WriteMessage(payload);

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(timeoutMs);
                var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
                try
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
                finally
                {
                    reg.Dispose();
                    lock (_pendingLock)
                        _pending.Remove(id);
                }
            }
        }

        public void Notify(string method, JObject parameters)
        {
            var payload = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters ?? new JObject(),
            };
            WriteMessage(payload);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (!_process.HasExited)
                {
                    try
                    {
                        RequestAsync("shutdown", new JObject(), CancellationToken.None, 2000)
                            .GetAwaiter().GetResult();
                        Notify("exit", null);
                    }
                    catch { /* best effort */ }

                    if (!_process.WaitForExit(3000))
                        KillProcessTree(_process);
                }
            }
            catch { }

            try { _stdin.Dispose(); } catch { }
            try { _stdout.Dispose(); } catch { }
            try { _process.Dispose(); } catch { }
        }

        private void WriteMessage(JObject payload)
        {
            var json = payload.ToString(Formatting.None);
            var bytes = Encoding.UTF8.GetBytes(json);
            var header = "Content-Length: " + bytes.Length + "\r\n\r\n";
            lock (_writeLock)
            {
                var headerBytes = Encoding.UTF8.GetBytes(header);
                _stdin.Write(headerBytes, 0, headerBytes.Length);
                _stdin.Write(bytes, 0, bytes.Length);
                _stdin.Flush();
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_disposed && !_process.HasExited)
                {
                    var message = await ReadMessageAsync().ConfigureAwait(false);
                    if (message == null) break;
                    DispatchMessage(message);
                }
            }
            catch
            {
                // Reader exit — fail pending requests
                lock (_pendingLock)
                {
                    foreach (var tcs in _pending.Values)
                        tcs.TrySetException(new IOException("LSP reader exited."));
                    _pending.Clear();
                }
            }
        }

        private void DispatchMessage(JObject message)
        {
            var method = message["method"]?.ToString();
            var idToken = message["id"];
            bool hasId = idToken != null && idToken.Type != JTokenType.Null;

            if (method != null)
            {
                // Server-initiated message. With an id it's a REQUEST we must
                // answer — Roslyn LS crashes its read loop if workspace/configuration,
                // client/registerCapability, workspace/diagnostic/refresh, etc.
                // go unanswered. Without an id it's a notification.
                if (hasId)
                    HandleServerRequest(method, idToken, message["params"] as JObject);
                else
                    HandleNotification(message);
                return;
            }

            // Otherwise it's a response to a request we issued.
            if (!hasId || idToken.Type != JTokenType.Integer) return;
            int id = idToken.Value<int>();

            TaskCompletionSource<JToken> tcs = null;
            lock (_pendingLock)
                _pending.TryGetValue(id, out tcs);

            if (tcs == null) return;

            if (message["error"] != null)
            {
                var err = message["error"];
                tcs.TrySetException(new InvalidOperationException(
                    "LSP error " + err?["code"] + ": " + err?["message"]));
                return;
            }

            tcs.TrySetResult(message["result"] ?? JValue.CreateNull());
        }

        /// <summary>
        /// Answer a server→client request. We are a headless host with no user
        /// settings, so the safe universal reply is "defaults": null for most
        /// requests, and an array of nulls for workspace/configuration (one per
        /// requested item). This keeps Roslyn LS's JSON-RPC loop alive.
        /// </summary>
        private void HandleServerRequest(string method, JToken id, JObject parameters)
        {
            JToken result;
            if (string.Equals(method, "workspace/configuration", StringComparison.Ordinal))
            {
                var items = parameters?["items"] as JArray;
                var arr = new JArray();
                int n = items?.Count ?? 0;
                for (int i = 0; i < n; i++) arr.Add(JValue.CreateNull());
                result = arr;
            }
            else
            {
                // client/registerCapability, client/unregisterCapability,
                // workspace/diagnostic/refresh, window/workDoneProgress/create,
                // workspace/semanticTokens/refresh, ... — acknowledge with null.
                result = JValue.CreateNull();
            }

            SendResponse(id, result);
        }

        private void SendResponse(JToken id, JToken result)
        {
            WriteMessage(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result ?? JValue.CreateNull(),
            });
        }

        private void HandleNotification(JObject message)
        {
            var method = message["method"]?.ToString();
            if (method == null) return;

            // Record arrival + release any waiters (e.g. projectInitializationComplete).
            lock (_notifyLock)
            {
                _seenNotifications.Add(method);
                if (_notifyWaiters.TryGetValue(method, out var waiters))
                {
                    foreach (var w in waiters) w.TrySetResult(true);
                    _notifyWaiters.Remove(method);
                }
            }

            // Push-model diagnostics (csharp-ls). Roslyn uses pull instead.
            if (method != "textDocument/publishDiagnostics") return;

            var p = message["params"] as JObject;
            if (p == null) return;

            var uri = p["uri"]?.ToString();
            if (string.IsNullOrEmpty(uri)) return;

            lock (_diagLock)
                _diagnosticsByUri[uri] = ParseDiagnostics(p["diagnostics"] as JArray);
        }

        /// <summary>
        /// Convert an LSP diagnostics JArray (from either a publishDiagnostics
        /// notification or a textDocument/diagnostic pull report) into the
        /// host's diagnostic model. Lines/characters are converted 0→1-based.
        /// Roslyn diagnostics often omit "source" but carry a "code" (e.g.
        /// IDE0090 / CS0168); fall back to that so the rule id is visible.
        /// </summary>
        public static List<LspDiagnostic> ParseDiagnostics(JArray diags)
        {
            var list = new List<LspDiagnostic>();
            if (diags == null) return list;

            foreach (var d in diags)
            {
                if (!(d is JObject diag)) continue;
                var range = diag["range"] as JObject;
                var start = range?["start"] as JObject;
                int line = start?["line"]?.Value<int>() ?? 0;
                int character = start?["character"]?.Value<int>() ?? 0;
                int severity = diag["severity"]?.Value<int>() ?? 3;
                string sev = severity <= 1 ? "error" : severity == 2 ? "warning" : "info";
                string msg = diag["message"]?.ToString() ?? "";
                string src = diag["source"]?.ToString();
                if (string.IsNullOrEmpty(src))
                    src = diag["code"]?.ToString() ?? "";
                list.Add(new LspDiagnostic(line + 1, character + 1, sev, msg, src));
            }

            return list;
        }

        private async Task<JObject> ReadMessageAsync()
        {
            int? contentLength = null;
            var headerBuilder = new StringBuilder();

            while (true)
            {
                int b = _stdout.ReadByte();
                if (b < 0) return null;
                headerBuilder.Append((char)b);
                if (headerBuilder.Length >= 4)
                {
                    var s = headerBuilder.ToString();
                    if (s.EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        break;
                }
            }

            foreach (var line in headerBuilder.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                const string prefix = "Content-Length: ";
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(line.Substring(prefix.Length).Trim(), out int len))
                        contentLength = len;
                }
            }

            if (!contentLength.HasValue || contentLength.Value <= 0)
                return null;

            var buffer = new byte[contentLength.Value];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await _stdout.ReadAsync(buffer, read, buffer.Length - read).ConfigureAwait(false);
                if (n <= 0) return null;
                read += n;
            }

            return JObject.Parse(Encoding.UTF8.GetString(buffer, 0, read));
        }

        private static void KillProcessTree(Process process)
        {
            try
            {
                if (process.Id <= 0) return;
                var kill = new ProcessStartInfo("taskkill", "/F /T /PID " + process.Id)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                using (var p = Process.Start(kill))
                    p?.WaitForExit(3000);
            }
            catch { }
        }
    }
}
