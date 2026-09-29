// File: McpClientManager.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// MCP CLIENT side: launches the external servers named in devmind.json "mcpServers"
// (McpServerConfig) over stdio, lists their tools and calls them. Part 1 of the MCP
// client work — nothing in the agent loop uses this yet.
//
// PROCESS OWNERSHIP: we start the server process ourselves and hand its stdin/stdout to
// the SDK's StreamClientTransport, instead of letting StdioClientTransport start it. The
// SDK transport never exposes its Process, so it could not be put in a job object; owning
// the Process gives us (a) the WindowsJobObject kill-on-close containment ShellRunner uses
// — the server and anything it spawns die with the host, even on a crash of the host —
// (b) the child's full environment, and (c) the stderr tail for error messages.
//
// ENVIRONMENT: the child gets the host's FULL environment (ProcessStartInfo's default
// copy) plus the entry's "env" overrides. Deliberately NOT ShellRunner's stripped child
// environment — missing OS / ProgramData broke tools there (known H-item).
//
// FAILURE CONTRACT: CallToolAsync never throws into the agent loop except for the
// caller's own cancellation. A server that fails to start or dies returns an error string
// naming the server plus its stderr tail; the next call makes ONE automatic restart
// attempt. A second consecutive failure leaves the server failed (errors returned without
// relaunching) until a call succeeds again or the manager is recreated.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevMind
{
    /// <summary>A tool offered by an external MCP server, in DevMind's (Newtonsoft) terms.</summary>
    public sealed class McpToolInfo
    {
        public string Server { get; init; } = "";
        public string Name { get; init; } = "";
        /// <summary><c>mcp__server__tool</c> — see <see cref="McpToolName"/>.</summary>
        public string QualifiedName { get; init; } = "";
        public string Description { get; init; } = "";
        public JObject InputSchema { get; init; } = new JObject();
    }

    /// <summary>Result of <see cref="McpClientManager.ListToolsAsync"/>: the tools, or an error string.</summary>
    public sealed class McpToolListing
    {
        public IReadOnlyList<McpToolInfo> Tools { get; init; } = Array.Empty<McpToolInfo>();
        /// <summary>Null on success; otherwise a message naming the server (and its stderr tail).</summary>
        public string? Error { get; init; }
    }

    /// <summary>
    /// Owns one lazily-started stdio session per configured external MCP server.
    /// Thread-safe; dispose to kill every server it started.
    /// </summary>
    public sealed class McpClientManager : IAsyncDisposable
    {
        private readonly Dictionary<string, ServerSlot> _slots;
        private int _disposed;

        public McpClientManager(IEnumerable<McpServerConfig> servers)
        {
            _slots = new Dictionary<string, ServerSlot>(StringComparer.Ordinal);
            foreach (var s in servers ?? Enumerable.Empty<McpServerConfig>())
                _slots[s.Name] = new ServerSlot(s);
        }

        /// <summary>Builds a manager from the global devmind.json. Reads config only — starts nothing.</summary>
        public static McpClientManager FromGlobalConfig(Action<string>? warn = null) =>
            new McpClientManager(McpServerConfig.Load(warn));

        /// <summary>False when no server is configured: the feature is off.</summary>
        public bool IsEnabled => _slots.Count > 0;

        public IReadOnlyList<string> ServerNames => _slots.Keys.ToList();

        /// <summary>
        /// Starts <paramref name="server"/> if it is not already running. Returns null on
        /// success or an error string. Called implicitly by <see cref="ListToolsAsync"/> and
        /// <see cref="CallToolAsync"/>.
        /// </summary>
        public async Task<string?> StartAsync(string server, CancellationToken ct = default)
        {
            var (_, error) = await AcquireSessionAsync(server, ct).ConfigureAwait(false);
            return error;
        }

        /// <summary>Lists the server's tools, filtered by the entry's allowlist. Never throws except on caller cancellation.</summary>
        public async Task<McpToolListing> ListToolsAsync(string server, CancellationToken ct = default)
        {
            var (session, error) = await AcquireSessionAsync(server, ct).ConfigureAwait(false);
            if (session == null)
                return new McpToolListing { Error = error };

            var slot = _slots[server];
            try
            {
                var tools = await session.Client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
                var list = new List<McpToolInfo>();
                foreach (var t in tools)
                {
                    if (!slot.Config.AllowsTool(t.Name)) continue;
                    list.Add(new McpToolInfo
                    {
                        Server        = server,
                        Name          = t.Name,
                        QualifiedName = McpToolName.Build(server, t.Name),
                        Description   = t.Description ?? "",
                        InputSchema   = ToJObject(t.ProtocolTool.InputSchema),
                    });
                }
                return new McpToolListing { Tools = list };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new McpToolListing { Error = await FailAsync(slot, session, "tools/list failed", ex).ConfigureAwait(false) };
            }
        }

        /// <summary>
        /// Calls <paramref name="tool"/> on <paramref name="server"/> with the entry's
        /// per-call timeout and returns the result as text (see <see cref="FormatResult"/>).
        /// Every failure comes back as a string naming the server; only the caller's own
        /// cancellation throws.
        /// </summary>
        public async Task<string> CallToolAsync(string server, string tool, JObject? args, CancellationToken ct = default)
        {
            if (!_slots.TryGetValue(server ?? "", out var slot))
                return UnknownServer(server);
            if (!slot.Config.AllowsTool(tool))
                return $"[MCP ERROR] server '{server}': tool '{tool}' is not in this server's \"tools\" allowlist in devmind.json.";

            var (session, error) = await AcquireSessionAsync(server!, ct).ConfigureAwait(false);
            if (session == null)
                return error!;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(slot.Config.CallTimeoutSeconds));
            try
            {
                var result = await session.Client.CallToolAsync(
                    tool, ToArguments(args), progress: null, options: null, cancellationToken: timeout.Token).ConfigureAwait(false);
                slot.ConsecutiveFailures = 0;
                return FormatResult(server!, result, McpTempDir(server!));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // The server may still be working; the SDK has sent notifications/cancelled. The
                // session stays up — a slow tool is not a dead server.
                return $"[MCP ERROR] server '{server}': tool '{tool}' timed out after {slot.Config.CallTimeoutSeconds}s " +
                       "(raise \"callTimeoutSeconds\" for this server in devmind.json if it legitimately runs longer).";
            }
            catch (Exception ex)
            {
                return await FailAsync(slot, session, $"tool '{tool}' failed", ex).ConfigureAwait(false);
            }
        }

        /// <summary>Test hook: the PID of the running server process, or null.</summary>
        internal int? GetServerProcessId(string server) =>
            _slots.TryGetValue(server, out var slot) ? slot.Session?.ProcessId : null;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var slot in _slots.Values)
            {
                await slot.Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (slot.Session != null)
                        await slot.Session.DisposeAsync().ConfigureAwait(false);
                    slot.Session = null;
                }
                finally { slot.Gate.Release(); }
            }
        }

        // ── Session lifecycle ────────────────────────────────────────────────────────

        private sealed class ServerSlot
        {
            public ServerSlot(McpServerConfig config) => Config = config;
            public McpServerConfig Config { get; }
            public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);
            public ServerSession? Session;
            /// <summary>Start failures / crashes since the last success. At 2, no further relaunch.</summary>
            public int ConsecutiveFailures;
            public string? LastError;
        }

        private async Task<(ServerSession? session, string? error)> AcquireSessionAsync(string server, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return (null, $"[MCP ERROR] server '{server}': the MCP client manager has been disposed.");
            if (!_slots.TryGetValue(server ?? "", out var slot))
                return (null, UnknownServer(server));

            await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (slot.Session != null && slot.Session.IsAlive)
                    return (slot.Session, null);

                if (slot.Session != null)
                {
                    // Died between calls: count it, keep its stderr for the message, reap it.
                    slot.LastError = DeadMessage(slot.Config.Name, "the server process exited", null, slot.Session);
                    slot.ConsecutiveFailures++;
                    DevMindLog.Write(slot.LastError);
                    await slot.Session.DisposeAsync().ConfigureAwait(false);
                    slot.Session = null;
                }

                // Initial start + ONE automatic restart attempt; after that, report without relaunching.
                if (slot.ConsecutiveFailures >= 2)
                    return (null, (slot.LastError ?? $"[MCP ERROR] server '{server}' is unavailable.") +
                                  " (automatic restart already attempted; not relaunching)");

                try
                {
                    slot.Session = await ServerSession.StartAsync(slot.Config, ct).ConfigureAwait(false);
                    return (slot.Session, null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var partial = (ex as SessionStartException)?.Session;
                    slot.LastError = DeadMessage(slot.Config.Name, "failed to start", ex.InnerException ?? ex, partial);
                    slot.ConsecutiveFailures++;
                    DevMindLog.Write(slot.LastError);
                    if (partial != null)
                        await partial.DisposeAsync().ConfigureAwait(false);
                    return (null, slot.LastError);
                }
            }
            finally
            {
                slot.Gate.Release();
            }
        }

        /// <summary>
        /// A request threw. If the session is dead (process exited / transport closed) it is
        /// torn down and counted toward the restart budget; otherwise it was a protocol-level
        /// error (unknown tool, bad arguments) and the session stays up.
        /// </summary>
        private async Task<string> FailAsync(ServerSlot slot, ServerSession session, string what, Exception ex)
        {
            if (session.IsAlive)
                return $"[MCP ERROR] server '{slot.Config.Name}': {what}: {ex.Message}";

            await slot.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                string msg = DeadMessage(slot.Config.Name, what + " — the server died", ex, session);
                if (ReferenceEquals(slot.Session, session))
                {
                    slot.LastError = msg;
                    slot.ConsecutiveFailures++;
                    DevMindLog.Write(msg);
                    slot.Session = null;
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                return msg + " It will be restarted on the next call.";
            }
            finally
            {
                slot.Gate.Release();
            }
        }

        private static string UnknownServer(string? server) =>
            $"[MCP ERROR] no MCP server named '{server}' is configured in \"mcpServers\" in devmind.json.";

        private static string DeadMessage(string server, string what, Exception? ex, ServerSession? session)
        {
            var sb = new StringBuilder($"[MCP ERROR] server '{server}' {what}");
            if (ex != null) sb.Append(": ").Append(ex.Message);
            if (session?.ExitCode is int code) sb.Append($" (exit code {code})");
            sb.Append('.');
            string tail = session?.StderrTail() ?? "";
            sb.Append(tail.Length > 0 ? "\nstderr tail:\n" + tail : " (no stderr output)");
            return sb.ToString();
        }

        /// <summary>Thrown by <see cref="ServerSession.StartAsync"/> after the process started, carrying it so its stderr can be reported.</summary>
        private sealed class SessionStartException : Exception
        {
            public SessionStartException(ServerSession session, Exception inner) : base(inner.Message, inner) => Session = session;
            public ServerSession Session { get; }
        }

        /// <summary>One running server: the process, its job object, the SDK client, and a stderr ring buffer.</summary>
        private sealed class ServerSession : IAsyncDisposable
        {
            private const int StderrTailLines = 30;
            private readonly Process _process;
            private readonly IntPtr _job;
            private readonly Queue<string> _stderr = new Queue<string>();
            private McpClient? _client;
            private int _disposed;

            private ServerSession(Process process, IntPtr job)
            {
                _process = process;
                _job = job;
            }

            public McpClient Client => _client!;
            public int? ProcessId { get { try { return _process.Id; } catch { return null; } } }

            public int? ExitCode
            {
                get { try { return _process.HasExited ? _process.ExitCode : null; } catch { return null; } }
            }

            public bool IsAlive
            {
                get
                {
                    try { return !_process.HasExited && _client != null && !_client.Completion.IsCompleted; }
                    catch { return false; }
                }
            }

            public string StderrTail()
            {
                lock (_stderr) return string.Join("\n", _stderr);
            }

            public static async Task<ServerSession> StartAsync(McpServerConfig config, CancellationToken ct)
            {
                var psi = BuildStartInfo(config);
                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.Start();   // throws (e.g. file not found) before any session exists

                // Contain it immediately: kill-on-close means the server (and its children)
                // die with the host even if DisposeAsync never runs. Degrades to Kill(tree).
                var (job, _) = WindowsJobObject.TryCreateAndAssignJob(process);
                var session = new ServerSession(process, job);

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (session._stderr)
                    {
                        session._stderr.Enqueue(e.Data);
                        while (session._stderr.Count > StderrTailLines) session._stderr.Dequeue();
                    }
                };
                process.BeginErrorReadLine();

                try
                {
                    var transport = new StreamClientTransport(
                        process.StandardInput.BaseStream, process.StandardOutput.BaseStream, loggerFactory: null);
                    var options = new McpClientOptions
                    {
                        ClientInfo = new Implementation { Name = "DevMind", Version = HostVersion },
                    };
                    session._client = await McpClient.CreateAsync(transport, options, loggerFactory: null, ct).ConfigureAwait(false);
                    return session;
                }
                catch (Exception ex)
                {
                    // Give a crashing server a moment to flush stderr so the message can show why.
                    try { process.WaitForExit(500); } catch { }
                    throw new SessionStartException(session, ex);
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                // Graceful first: disposing the client closes the server's stdin, and a stdio
                // MCP server exits on EOF. Bounded — a wedged server must not stall teardown.
                if (_client != null)
                {
                    try { await _client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch { }
                }
                try { _process.StandardInput.Close(); } catch { }
                try { _process.WaitForExit(2000); } catch { }

                // Then authoritative: terminate the whole job (server + anything it spawned).
                if (_job != IntPtr.Zero)
                {
                    WindowsJobObject.TryTerminate(_job);
                    for (int i = 0; i < 40; i++)
                    {
                        var live = WindowsJobObject.ActiveProcessIds(_job);
                        if (live == null || live.Length == 0) break;
                        await Task.Delay(50).ConfigureAwait(false);
                    }
                    WindowsJobObject.ReleaseJob(_job);
                }
                else
                {
                    try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
                }
                try { _process.WaitForExit(2000); } catch { }
                try { _process.Dispose(); } catch { }
            }

            private static ProcessStartInfo BuildStartInfo(McpServerConfig config)
            {
                var psi = new ProcessStartInfo
                {
                    UseShellExecute        = false,
                    RedirectStandardInput  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    StandardInputEncoding  = new UTF8Encoding(false),
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding  = new UTF8Encoding(false),
                };

                // CreateProcess only finds "<name>.exe" on PATH, so a bare "npx" or a .cmd shim
                // (the usual claude_desktop_config.json entries) goes through cmd.exe /c — the
                // same thing the SDK's own stdio transport does.
                string ext = Path.GetExtension(config.Command);
                bool direct = !OperatingSystem.IsWindows()
                    || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".com", StringComparison.OrdinalIgnoreCase);
                if (direct)
                {
                    psi.FileName = config.Command;
                }
                else
                {
                    psi.FileName = "cmd.exe";
                    psi.ArgumentList.Add("/c");
                    psi.ArgumentList.Add(config.Command);
                }
                foreach (var a in config.Args)
                    psi.ArgumentList.Add(a);

                // psi.Environment starts as a copy of the host's full environment.
                foreach (var kv in config.Env)
                {
                    if (kv.Value == null) psi.Environment.Remove(kv.Key);
                    else psi.Environment[kv.Key] = kv.Value;
                }
                return psi;
            }
        }

        private static readonly string HostVersion =
            typeof(McpClientManager).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "1.0.0";

        // ── Conversion (static, unit-testable) ───────────────────────────────────────

        /// <summary>%TEMP%\devmind\mcp\&lt;server&gt; — where binary tool output is written.</summary>
        internal static string McpTempDir(string server) =>
            Path.Combine(Path.GetTempPath(), "devmind", "mcp", server);

        internal static JObject ToJObject(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object) return new JObject();
            return JObject.Parse(schema.GetRawText());
        }

        internal static IReadOnlyDictionary<string, object?>? ToArguments(JObject? args)
        {
            if (args == null) return null;
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in args.Properties())
            {
                using var doc = JsonDocument.Parse(prop.Value.ToString(Formatting.None));
                dict[prop.Name] = doc.RootElement.Clone();
            }
            return dict;
        }

        /// <summary>
        /// Renders a tool result as text for a text-only worker model: text blocks joined by
        /// newlines; images / audio / binary resources written under <paramref name="tempDir"/>
        /// and returned as "[image saved: path]" lines; structured content as pretty JSON when
        /// no text block already carries it (servers usually mirror it into a text block).
        /// An error result is prefixed "[MCP TOOL ERROR]".
        /// </summary>
        internal static string FormatResult(string server, CallToolResult result, string tempDir)
        {
            var lines = new List<string>();
            bool hasText = false;

            foreach (var block in result.Content ?? (IList<ContentBlock>)Array.Empty<ContentBlock>())
            {
                switch (block)
                {
                    case TextContentBlock t:
                        lines.Add(t.Text);
                        hasText = true;
                        break;
                    case ImageContentBlock img:
                        lines.Add($"[image saved: {SaveBinary(tempDir, img.DecodedData, img.MimeType)}]");
                        break;
                    case AudioContentBlock audio:
                        lines.Add($"[audio saved: {SaveBinary(tempDir, audio.DecodedData, audio.MimeType)}]");
                        break;
                    case EmbeddedResourceBlock res when res.Resource is TextResourceContents tr:
                        lines.Add(tr.Text);
                        hasText = true;
                        break;
                    case EmbeddedResourceBlock res when res.Resource is BlobResourceContents br:
                        lines.Add($"[resource saved: {SaveBinary(tempDir, br.DecodedData, br.MimeType)} ({br.Uri})]");
                        break;
                    case ResourceLinkBlock link:
                        lines.Add($"[resource link: {link.Uri}{(string.IsNullOrEmpty(link.Name) ? "" : " — " + link.Name)}]");
                        break;
                    default:
                        lines.Add($"[unsupported content block: {block.Type}]");
                        break;
                }
            }

            if (result.StructuredContent is JsonElement structured && !hasText)
                lines.Add(JToken.Parse(structured.GetRawText()).ToString(Formatting.Indented));

            string text = lines.Count == 0 ? $"(server '{server}' returned no content)" : string.Join("\n", lines);
            return result.IsError == true ? "[MCP TOOL ERROR] " + text : text;
        }

        private static string SaveBinary(string dir, ReadOnlyMemory<byte> data, string? mimeType)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"{Guid.NewGuid():N}.{ExtensionFor(mimeType)}");
                File.WriteAllBytes(path, data.ToArray());
                return path;
            }
            catch (Exception ex)
            {
                return $"<could not save {data.Length} bytes of {mimeType}: {ex.Message}>";
            }
        }

        internal static string ExtensionFor(string? mimeType)
        {
            switch ((mimeType ?? "").Split(';')[0].Trim().ToLowerInvariant())
            {
                case "image/png":  return "png";
                case "image/jpeg": return "jpg";
                case "image/gif":  return "gif";
                case "image/webp": return "webp";
                case "image/bmp":  return "bmp";
                case "image/svg+xml": return "svg";
                case "audio/wav":
                case "audio/x-wav": return "wav";
                case "audio/mpeg": return "mp3";
                case "audio/ogg":  return "ogg";
                case "video/mp4":  return "mp4";
                case "application/pdf":  return "pdf";
                case "application/json": return "json";
                default: return "bin";
            }
        }
    }
}
