// File: DapClient.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Debug Adapter Protocol client over stdio, targeting netcoredbg
// (`netcoredbg --interpreter=vscode`). UI-agnostic: emits structured
// DebugResult types and raises callbacks the integration layer routes
// through AppendOutput. Roslyn LS has no DAP endpoint, so debugging runs
// as a separate adapter process (see DapClient.ResolveAdapterPath).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevMind
{
    /// <summary>
    /// Manages a netcoredbg DAP session: initialize/launch/attach/disconnect,
    /// request/response correlation by seq, and asynchronous event dispatch
    /// (stopped, output, terminated, exited).
    /// </summary>
    public sealed class DapClient : IDisposable
    {
        private readonly string _adapterPath;
        private readonly object _writeGate = new object();
        private readonly object _pendingGate = new object();
        private readonly Dictionary<int, TaskCompletionSource<JObject>> _pending =
            new Dictionary<int, TaskCompletionSource<JObject>>();

        private Process _proc;
        private Stream _stdin;
        private Stream _stdout;
        private Thread _reader;
        private int _seq;
        private volatile bool _closed;
        private JObject _capabilities;

        public DapSession Session { get; } = new DapSession();

        /// <summary>Capabilities reported by the adapter (initialize response body).</summary>
        public JObject Capabilities => _capabilities;

        /// <summary>True while the adapter process is running.</summary>
        public bool IsActive => _proc != null && !_closed;

        // -- Callbacks (UI-agnostic; the integration layer subscribes) ------------
        public Action<BreakpointHit> OnStopped;
        public Action<DebugOutput> OnOutput;
        public Action<int?> OnTerminated;   // exit code when known, else null
        public Action<string> OnLog;        // adapter stderr / internal diagnostics

        public DapClient(string adapterPath)
        {
            _adapterPath = string.IsNullOrWhiteSpace(adapterPath) ? "netcoredbg" : adapterPath;
        }

        /// <summary>
        /// Resolve the netcoredbg executable: DEVMIND_DAP_PATH override, then the
        /// per-user install under %LOCALAPPDATA%\netcoredbg, then PATH.
        /// </summary>
        public static string ResolveAdapterPath()
        {
            var env = Environment.GetEnvironmentVariable("DEVMIND_DAP_PATH");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

            string local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "netcoredbg", "netcoredbg", "netcoredbg.exe");
            if (File.Exists(local)) return local;

            return "netcoredbg";
        }

        // -- Lifecycle ------------------------------------------------------------

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            StartProcess();
            Session.SetState(DebugSessionState.Initializing);
            var resp = await SendRequestAsync("initialize", new JObject
            {
                ["clientID"] = "devmind",
                ["clientName"] = "DevMind",
                ["adapterID"] = "netcoredbg",
                ["locale"] = "en-US",
                ["linesStartAt1"] = true,
                ["columnsStartAt1"] = true,
                ["pathFormat"] = "path",
                ["supportsRunInTerminalRequest"] = false,
            }, ct).ConfigureAwait(false);
            _capabilities = resp?["body"] as JObject;
        }

        /// <summary>Launch a built program (dll/exe) under the debugger. Returns null on success, else an error message.</summary>
        public async Task<string> LaunchAsync(string programPath, string cwd, string[] args, bool stopAtEntry, CancellationToken ct = default)
        {
            Session.IsLaunch = true;
            Session.Program = programPath;
            var launchArgs = new JObject
            {
                ["name"] = "DevMind Launch",
                ["type"] = "coreclr",
                ["request"] = "launch",
                ["program"] = programPath,
                ["cwd"] = string.IsNullOrEmpty(cwd) ? Path.GetDirectoryName(programPath) : cwd,
                ["stopAtEntry"] = stopAtEntry,
                ["console"] = "internalConsole",
            };
            if (args != null && args.Length > 0) launchArgs["args"] = new JArray(args);
            return await RequestOkAsync("launch", launchArgs, ct).ConfigureAwait(false);
        }

        /// <summary>Attach to a running process by id. Returns null on success, else an error message.</summary>
        public async Task<string> AttachAsync(int pid, CancellationToken ct = default)
        {
            Session.IsLaunch = false;
            Session.ProcessId = pid;
            var attachArgs = new JObject
            {
                ["name"] = "DevMind Attach",
                ["type"] = "coreclr",
                ["request"] = "attach",
                ["processId"] = pid,
            };
            return await RequestOkAsync("attach", attachArgs, ct).ConfigureAwait(false);
        }

        public async Task DisconnectAsync(bool terminateDebuggee, CancellationToken ct = default)
        {
            try
            {
                await SendRequestAsync("disconnect", new JObject { ["terminateDebuggee"] = terminateDebuggee }, ct)
                    .ConfigureAwait(false);
            }
            catch { /* adapter may already be gone */ }
            Close();
        }

        // -- Breakpoints ----------------------------------------------------------

        public async Task SetBreakpointAsync(string file, int line, CancellationToken ct = default)
        {
            Session.AddBreakpoint(file, line);
            if (IsActive) await SendSetBreakpointsAsync(file, ct).ConfigureAwait(false);
        }

        public async Task ClearBreakpointAsync(string file, int line, CancellationToken ct = default)
        {
            Session.RemoveBreakpoint(file, line);
            if (IsActive) await SendSetBreakpointsAsync(file, ct).ConfigureAwait(false);
        }

        private async Task SendSetBreakpointsAsync(string file, CancellationToken ct = default)
        {
            int[] lines = Session.GetBreakpoints(file);
            var bps = new JArray(lines.Select(l => (JToken)new JObject { ["line"] = l }));
            await SendRequestAsync("setBreakpoints", new JObject
            {
                ["source"] = new JObject { ["path"] = file, ["name"] = Path.GetFileName(file) },
                ["breakpoints"] = bps,
            }, ct).ConfigureAwait(false);
        }

        // -- Execution control ----------------------------------------------------

        public Task ContinueAsync(CancellationToken ct = default) => ResumeAsync("continue", ct);
        public Task StepOverAsync(CancellationToken ct = default) => ResumeAsync("next", ct);
        public Task StepInAsync(CancellationToken ct = default) => ResumeAsync("stepIn", ct);
        public Task StepOutAsync(CancellationToken ct = default) => ResumeAsync("stepOut", ct);

        private async Task ResumeAsync(string command, CancellationToken ct)
        {
            int threadId = Session.GetStoppedThread() ?? 1;
            Session.SetRunning();
            await SendRequestAsync(command, new JObject { ["threadId"] = threadId }, ct).ConfigureAwait(false);
        }

        // -- Inspection -----------------------------------------------------------

        public async Task<StackTraceResult> StackTraceAsync(int threadId, int levels, CancellationToken ct = default)
        {
            var resp = await SendRequestAsync("stackTrace", new JObject
            {
                ["threadId"] = threadId,
                ["startFrame"] = 0,
                ["levels"] = levels,
            }, ct).ConfigureAwait(false);

            var result = new StackTraceResult();
            if (resp?["body"]?["stackFrames"] is JArray frames)
            {
                foreach (var f in frames)
                {
                    result.Frames.Add(new DebugStackFrame
                    {
                        Id = f["id"]?.Value<int>() ?? 0,
                        Name = f["name"]?.ToString(),
                        File = f["source"]?["path"]?.ToString(),
                        Line = f["line"]?.Value<int>() ?? 0,
                        Column = f["column"]?.Value<int>() ?? 0,
                    });
                }
            }
            result.TotalFrames = resp?["body"]?["totalFrames"]?.Value<int>() ?? result.Frames.Count;
            return result;
        }

        public async Task<List<DebugScope>> ScopesAsync(int frameId, CancellationToken ct = default)
        {
            var resp = await SendRequestAsync("scopes", new JObject { ["frameId"] = frameId }, ct).ConfigureAwait(false);
            var scopes = new List<DebugScope>();
            if (resp?["body"]?["scopes"] is JArray arr)
            {
                foreach (var s in arr)
                    scopes.Add(new DebugScope
                    {
                        Name = s["name"]?.ToString(),
                        VariablesReference = s["variablesReference"]?.Value<int>() ?? 0,
                    });
            }
            return scopes;
        }

        public async Task<List<DebugVariable>> VariablesAsync(int variablesReference, CancellationToken ct = default)
        {
            var resp = await SendRequestAsync("variables", new JObject { ["variablesReference"] = variablesReference }, ct)
                .ConfigureAwait(false);
            var vars = new List<DebugVariable>();
            if (resp?["body"]?["variables"] is JArray arr)
            {
                foreach (var v in arr)
                    vars.Add(new DebugVariable
                    {
                        Name = v["name"]?.ToString(),
                        Value = v["value"]?.ToString(),
                        Type = v["type"]?.ToString(),
                        VariablesReference = v["variablesReference"]?.Value<int>() ?? 0,
                    });
            }
            return vars;
        }

        /// <summary>Find a variable by name in the current frame's scopes; falls back to evaluate().</summary>
        public async Task<VariableInspection> InspectVariableAsync(string name, CancellationToken ct = default)
        {
            var inspection = new VariableInspection { Name = name, Found = false };
            int? frame = Session.GetCurrentFrame();
            if (frame == null) return inspection;

            foreach (var scope in await ScopesAsync(frame.Value, ct).ConfigureAwait(false))
            {
                if (scope.VariablesReference <= 0) continue;
                var vars = await VariablesAsync(scope.VariablesReference, ct).ConfigureAwait(false);
                var match = vars.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal));
                if (match != null)
                {
                    inspection.Found = true;
                    inspection.Value = match.Value;
                    inspection.Type = match.Type;
                    inspection.VariablesReference = match.VariablesReference;
                    if (match.VariablesReference > 0)
                        inspection.Children = await VariablesAsync(match.VariablesReference, ct).ConfigureAwait(false);
                    return inspection;
                }
            }

            var ev = await EvaluateAsync(name, ct).ConfigureAwait(false);
            if (ev.Success)
            {
                inspection.Found = true;
                inspection.Value = ev.Result;
                inspection.Type = ev.Type;
                inspection.VariablesReference = ev.VariablesReference;
            }
            return inspection;
        }

        public async Task<EvaluateResult> EvaluateAsync(string expression, CancellationToken ct = default)
        {
            var result = new EvaluateResult { Expression = expression };
            var args = new JObject { ["expression"] = expression, ["context"] = "repl" };
            int? frame = Session.GetCurrentFrame();
            if (frame != null) args["frameId"] = frame.Value;

            var resp = await SendRequestAsync("evaluate", args, ct).ConfigureAwait(false);
            if (resp?["success"]?.Value<bool>() == true)
            {
                result.Success = true;
                result.Result = resp["body"]?["result"]?.ToString();
                result.Type = resp["body"]?["type"]?.ToString();
                result.VariablesReference = resp["body"]?["variablesReference"]?.Value<int>() ?? 0;
            }
            else
            {
                result.Success = false;
                result.Error = resp?["message"]?.ToString() ?? "evaluate failed";
            }
            return result;
        }

        // -- Transport ------------------------------------------------------------

        private void StartProcess()
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adapterPath,
                Arguments = "--interpreter=vscode",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _proc = Process.Start(psi)
                ?? throw new IOException($"Failed to start debug adapter: {_adapterPath}");
            _stdin = _proc.StandardInput.BaseStream;
            _stdout = _proc.StandardOutput.BaseStream;

            _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "dap-reader" };
            _reader.Start();

            _ = Task.Run(async () =>
            {
                try
                {
                    string line;
                    while ((line = await _proc.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                        OnLog?.Invoke("[netcoredbg] " + line);
                }
                catch { /* stderr closed */ }
            });
        }

        private async Task<string> RequestOkAsync(string command, JObject args, CancellationToken ct)
        {
            var resp = await SendRequestAsync(command, args, ct).ConfigureAwait(false);
            if (resp?["success"]?.Value<bool>() == true) return null;
            return resp?["message"]?.ToString() ?? $"{command} failed";
        }

        private async Task<JObject> SendRequestAsync(string command, JObject arguments, CancellationToken ct = default)
        {
            if (_closed) throw new IOException("DAP session is not active.");

            int seq = Interlocked.Increment(ref _seq);
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingGate) _pending[seq] = tcs;

            var msg = new JObject { ["seq"] = seq, ["type"] = "request", ["command"] = command };
            if (arguments != null) msg["arguments"] = arguments;

            try
            {
                WriteMessage(msg);
            }
            catch (Exception ex)
            {
                lock (_pendingGate) _pending.Remove(seq);
                throw new IOException($"DAP write failed: {ex.Message}", ex);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            using (timeoutCts.Token.Register(() =>
            {
                lock (_pendingGate)
                {
                    if (_pending.Remove(seq)) tcs.TrySetCanceled();
                }
            }))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }

        private void WriteMessage(JObject msg)
        {
            string body = msg.ToString(Formatting.None);
            byte[] payload = Encoding.UTF8.GetBytes(body);
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
            lock (_writeGate)
            {
                _stdin.Write(header, 0, header.Length);
                _stdin.Write(payload, 0, payload.Length);
                _stdin.Flush();
            }
        }

        private void ReaderLoop()
        {
            try
            {
                while (!_closed)
                {
                    var msg = ReadFrame();
                    if (msg == null) break;
                    Dispatch(msg);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke("[dap] reader error: " + ex.Message);
            }
            finally
            {
                HandleClosed();
            }
        }

        private JObject ReadFrame()
        {
            var header = new List<byte>(64);
            var one = new byte[1];
            while (true)
            {
                int n = _stdout.Read(one, 0, 1);
                if (n <= 0) return null;
                header.Add(one[0]);
                int c = header.Count;
                if (c >= 4 && header[c - 1] == 10 && header[c - 2] == 13 && header[c - 3] == 10 && header[c - 4] == 13)
                    break; // \r\n\r\n
            }

            int contentLength = -1;
            foreach (var line in Encoding.ASCII.GetString(header.ToArray())
                         .Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int idx = line.IndexOf(':');
                if (idx > 0 && line.Substring(0, idx).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line.Substring(idx + 1).Trim(), out contentLength);
            }
            if (contentLength < 0) return null;

            var bodyBytes = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = _stdout.Read(bodyBytes, read, contentLength - read);
                if (n <= 0) return null;
                read += n;
            }

            try { return JObject.Parse(Encoding.UTF8.GetString(bodyBytes)); }
            catch { return null; }
        }

        private void Dispatch(JObject msg)
        {
            string type = msg["type"]?.ToString();
            if (type == "response")
            {
                int rseq = msg["request_seq"]?.Value<int>() ?? -1;
                TaskCompletionSource<JObject> tcs = null;
                lock (_pendingGate)
                {
                    if (_pending.TryGetValue(rseq, out tcs)) _pending.Remove(rseq);
                }
                tcs?.TrySetResult(msg);
            }
            else if (type == "event")
            {
                HandleEvent(msg["event"]?.ToString(), msg["body"] as JObject);
            }
        }

        private void HandleEvent(string evt, JObject body)
        {
            switch (evt)
            {
                case "initialized":
                    _ = ConfigureAsync();
                    break;

                case "stopped":
                    HandleStopped(body);
                    break;

                case "continued":
                    Session.SetRunning();
                    break;

                case "output":
                    OnOutput?.Invoke(new DebugOutput
                    {
                        Category = body?["category"]?.ToString() ?? "console",
                        Text = body?["output"]?.ToString() ?? "",
                    });
                    break;

                case "exited":
                    OnTerminated?.Invoke(body?["exitCode"]?.Value<int>());
                    break;

                case "terminated":
                    Session.SetState(DebugSessionState.Terminated);
                    OnTerminated?.Invoke(null);
                    break;
            }
        }

        // On the 'initialized' event, push the desired breakpoints then signal
        // configurationDone so the adapter resumes the debuggee.
        private async Task ConfigureAsync()
        {
            try
            {
                foreach (var file in Session.GetBreakpointFiles())
                    await SendSetBreakpointsAsync(file).ConfigureAwait(false);
                await SendRequestAsync("configurationDone", new JObject()).ConfigureAwait(false);
                Session.SetRunning();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke("[dap] configuration failed: " + ex.Message);
            }
        }

        private void HandleStopped(JObject body)
        {
            int threadId = body?["threadId"]?.Value<int>() ?? 0;
            string reason = body?["reason"]?.ToString() ?? "stopped";
            string description = body?["description"]?.ToString() ?? body?["text"]?.ToString();
            Session.SetStopped(threadId, reason);

            // Enrich with the top frame off the reader thread so the consumer gets
            // a location without an extra round-trip.
            _ = Task.Run(async () =>
            {
                var hit = new BreakpointHit { Reason = reason, ThreadId = threadId, Description = description };
                try
                {
                    var stack = await StackTraceAsync(threadId, 1).ConfigureAwait(false);
                    var top = stack.Frames.FirstOrDefault();
                    if (top != null)
                    {
                        hit.TopFrame = top;
                        hit.File = top.File;
                        hit.Line = top.Line;
                        Session.SetCurrentFrame(top.Id);
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke("[dap] stop enrich failed: " + ex.Message);
                }
                OnStopped?.Invoke(hit);
            });
        }

        private void HandleClosed()
        {
            if (_closed) { FailPending(); return; }
            _closed = true;
            FailPending();
            Session.SetState(DebugSessionState.Terminated);
        }

        private void FailPending()
        {
            lock (_pendingGate)
            {
                foreach (var kv in _pending) kv.Value.TrySetException(new IOException("DAP session closed."));
                _pending.Clear();
            }
        }

        private void Close()
        {
            if (_closed) return;
            _closed = true;
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(true); }
            catch { /* best effort */ }
            FailPending();
            Session.SetState(DebugSessionState.Terminated);
        }

        public void Dispose() => Close();
    }
}
