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
            if (message["method"] != null && message["id"] == null)
            {
                HandleNotification(message);
                return;
            }

            if (message["id"] != null)
            {
                var idToken = message["id"];
                if (idToken == null || idToken.Type != JTokenType.Integer) return;
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
        }

        private void HandleNotification(JObject message)
        {
            var method = message["method"]?.ToString();
            if (method != "textDocument/publishDiagnostics") return;

            var p = message["params"] as JObject;
            if (p == null) return;

            var uri = p["uri"]?.ToString();
            if (string.IsNullOrEmpty(uri)) return;

            var list = new List<LspDiagnostic>();
            var diags = p["diagnostics"] as JArray;
            if (diags != null)
            {
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
                    string src = diag["source"]?.ToString() ?? "";
                    list.Add(new LspDiagnostic(line + 1, character + 1, sev, msg, src));
                }
            }

            lock (_diagLock)
                _diagnosticsByUri[uri] = list;
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
