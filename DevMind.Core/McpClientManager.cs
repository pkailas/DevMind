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
    /// The one call the agent loop makes on external MCP servers — the seam AgenticExecutor
    /// depends on, so it can be tested without launching a server. Implementations return
    /// every failure as text ("[MCP ERROR] ...") and throw only on the caller's cancellation.
    /// </summary>
    public interface IMcpToolInvoker
    {
        Task<string> CallToolAsync(string server, string tool, JObject? args, CancellationToken ct);
    }

    /// <summary>Lifecycle of one configured server, as /mcp and devmind_task_result report it.</summary>
    public enum McpServerState { Stopped, Starting, Ready, Failed }

    /// <summary>A point-in-time view of one configured server.</summary>
    public sealed class McpServerStatus
    {
        public string Name { get; init; } = "";
        public McpServerState State { get; init; }
        /// <summary>Tools currently exposed to the model (after the allowlist); 0 unless Ready.</summary>
        public int ToolCount { get; init; }
        /// <summary>True when the entry has a "tools" allowlist.</summary>
        public bool HasAllowlist { get; init; }
        public bool AutoStart { get; init; }
        /// <summary>Why it is Failed: the short reason, without the stderr tail. Null otherwise.</summary>
        public string? Error { get; init; }
    }

    public enum McpNoticeKind
    {
        /// <summary>The server was relaunched — after its process died, or on an explicit restart.</summary>
        Restarted,
        /// <summary>The server failed twice in a row, so automatic relaunching has stopped.</summary>
        NotRestarted,
    }

    /// <summary>
    /// A lifecycle event the manager raises on its own, where no caller would otherwise see it:
    /// an automatic relaunch happens inside a tool call. Hosts route <see cref="Text"/> to the
    /// same place as their start lines (TUI transcript, headless transcript and journal).
    /// </summary>
    public sealed class McpNotice
    {
        public string Server { get; init; } = "";
        public McpNoticeKind Kind { get; init; }
        /// <summary>Why: "exit code 1", "the server process exited", "restart requested", ...</summary>
        public string Reason { get; init; } = "";

        /// <summary>The host line, without a trailing newline.</summary>
        public string Text => Kind == McpNoticeKind.Restarted
            ? (Reason == RestartRequested
                ? $"[MCP] {Server} restarted ({Reason})"
                : $"[MCP] {Server} restarted (previous process exited: {Reason})")
            : $"[MCP] {Server} not restarted: failed twice in a row; it stays down until an explicit restart ({Reason})";

        /// <summary>The reason an operator restart (/mcp restart) carries.</summary>
        public const string RestartRequested = "restart requested";
    }

    /// <summary>
    /// What a host holds: the tool invoker plus lifecycle and the exposed-tool snapshot. The
    /// TUI, headless jobs and LlmClient depend on this rather than on McpClientManager, so
    /// tests can substitute a fake (a start that never completes, a scripted failure).
    /// </summary>
    public interface IMcpClientManager : IMcpToolInvoker, IAsyncDisposable
    {
        IReadOnlyList<string> ServerNames { get; }

        /// <summary>Cached OpenAI tool objects of started servers. Synchronous, no I/O.</summary>
        IReadOnlyList<JObject> GetExposedTools();

        IReadOnlyList<McpServerStatus> GetStatuses();

        /// <summary>Starts the server if it is not running. Null on success, else the error text. Throws only on cancellation.</summary>
        Task<string?> StartAsync(string server, CancellationToken ct = default);

        /// <summary>Stops the server if running, clears its failure budget, starts it again. Null on success, else the error text.</summary>
        Task<string?> RestartAsync(string server, CancellationToken ct = default);

        /// <summary>CallToolAsync invocations so far, successful or not.</summary>
        int CallCount { get; }

        /// <summary>
        /// Raised once per relaunch (automatic or explicit) and once when automatic relaunching
        /// stops. Raised outside the manager's locks, on whatever thread noticed the change.
        /// </summary>
        event Action<McpNotice> Notice;
    }

    /// <summary>The one system-prompt line about MCP tools — present only while some are exposed.</summary>
    public static class McpPrompt
    {
        public const string Line =
            "External tools prefixed mcp__<server>__ come from MCP servers; their results may be long — prefer narrow queries.";

        /// <summary>A blank line then <see cref="Line"/> when <paramref name="clients"/> exposes at least one tool; "" otherwise.</summary>
        public static string Note(IMcpClientManager? clients) =>
            clients != null && clients.GetExposedTools().Count > 0 ? "\n\n" + Line : "";
    }

    /// <summary>
    /// Owns one lazily-started stdio session per configured external MCP server.
    /// Thread-safe; dispose to kill every server it started.
    /// </summary>
    public sealed class McpClientManager : IMcpClientManager
    {
        /// <summary>OpenAI function-name limit: ^[a-zA-Z0-9_-]{1,64}$.</summary>
        internal const int MaxFunctionNameLength = 64;

        private readonly Dictionary<string, ServerSlot> _slots;
        private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);
        private int _disposed;
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public event Action<McpNotice>? Notice;

        /// <summary>
        /// Where <see cref="RestartAsync"/> re-reads a server's entry, so an edited devmind.json
        /// (a changed "tools" allowlist, command, env) takes effect on /mcp restart. Returns null
        /// when the server is no longer configured. Null (the default) = restart with the entry
        /// captured at construction; headless jobs leave it unset because each job builds a
        /// fresh manager from the config anyway.
        /// </summary>
        public Func<string, McpServerConfig?>? ConfigReloader { get; set; }

        private void Raise(McpNotice? notice)
        {
            if (notice == null) return;
            DevMindLog.Write(notice.Text);
            try { Notice?.Invoke(notice); }
            catch (Exception ex) { DevMindLog.Write($"[MCP] notice handler failed: {ex.Message}"); }
        }

        /// <summary>
        /// Counts one more consecutive failure. Returns the "not restarted" notice exactly when
        /// this failure is the one that stops automatic relaunching (the second in a row), so
        /// it is raised once however many calls are refused afterwards.
        /// </summary>
        private static McpNotice? CountFailure(ServerSlot slot)
        {
            slot.ConsecutiveFailures++;
            if (slot.ConsecutiveFailures != 2) return null;
            slot.PendingRestartReason = null;
            return new McpNotice { Server = slot.Config.Name, Kind = McpNoticeKind.NotRestarted, Reason = slot.LastReason ?? "failed" };
        }

        private static string ExitReason(ServerSession session, string fallback) =>
            session.ExitCode is int code ? $"exit code {code}" : fallback;

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
                var list = await ListAllowedToolsAsync(slot, session, ct).ConfigureAwait(false);
                slot.ExposedTools = BuildExposedTools(list, WarnOnce);
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
            Interlocked.Increment(ref _callCount);
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
                return $"[MCP ERROR] server '{server}': tool '{tool}' {McpJournal.TimeoutPhrase} {slot.Config.CallTimeoutSeconds}s " +
                       "(raise \"callTimeoutSeconds\" for this server in devmind.json if it legitimately runs longer).";
            }
            catch (Exception ex)
            {
                return await FailAsync(slot, session, $"tool '{tool}' failed", ex).ConfigureAwait(false);
            }
        }

        public IReadOnlyList<McpServerStatus> GetStatuses()
        {
            var list = new List<McpServerStatus>(_slots.Count);
            foreach (var slot in _slots.Values)
            {
                var session = slot.Session;
                bool alive = session != null && session.IsAlive;
                McpServerState state =
                    slot.Starting ? McpServerState.Starting
                    : alive ? McpServerState.Ready
                    : session != null ? McpServerState.Failed        // died; reaped on the next call
                    : slot.ConsecutiveFailures > 0 ? McpServerState.Failed
                    : McpServerState.Stopped;
                list.Add(new McpServerStatus
                {
                    Name         = slot.Config.Name,
                    State        = state,
                    ToolCount    = state == McpServerState.Ready ? slot.ExposedTools.Count : 0,
                    HasAllowlist = slot.Config.Tools != null,
                    AutoStart    = slot.Config.AutoStart,
                    Error        = state != McpServerState.Failed ? null
                                 : session != null ? "the server process exited"
                                 : slot.LastReason,
                });
            }
            return list;
        }

        /// <summary>
        /// Stops <paramref name="server"/> if it is running, forgets its failure budget (an
        /// operator restart is not an automatic retry), and starts it again.
        /// </summary>
        public async Task<string?> RestartAsync(string server, CancellationToken ct = default)
        {
            if (!_slots.TryGetValue(server ?? "", out var slot))
                return UnknownServer(server);

            // Re-read the entry first: an edited allowlist, command or env is the usual reason
            // to restart. Reading one file starts nothing, so it is safe before the gate.
            McpServerConfig? fresh = null;
            if (ConfigReloader != null)
            {
                fresh = ConfigReloader(server!);
                if (fresh == null)
                    return $"[MCP ERROR] server '{server}' is no longer in \"mcpServers\" in devmind.json; not restarted.";
            }

            await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                slot.ExposedTools = Array.Empty<JObject>();
                if (slot.Session != null)
                    await slot.Session.DisposeAsync().ConfigureAwait(false);
                slot.Session = null;
                slot.ConsecutiveFailures = 0;
                slot.LastError = null;
                slot.LastReason = null;
                slot.PendingRestartReason = null;
                if (fresh != null)
                    slot.Config = fresh;
            }
            finally
            {
                slot.Gate.Release();
            }

            string? err = await StartAsync(server!, ct).ConfigureAwait(false);
            if (err == null)
                Raise(new McpNotice { Server = server!, Kind = McpNoticeKind.Restarted, Reason = McpNotice.RestartRequested });
            return err;
        }

        /// <summary>Test hook: the PID of the running server process, or null.</summary>
        internal int? GetServerProcessId(string server) =>
            _slots.TryGetValue(server, out var slot) ? slot.Session?.ProcessId : null;

        /// <summary>
        /// The cause in a manager error, for one-line status output: the first line, without
        /// the "[MCP ERROR] " tag or a leading "server '&lt;name&gt;' failed to start: " (the
        /// caller's line already names the server and says it failed). The stderr tail, on the
        /// lines after, is dropped.
        /// </summary>
        public static string ShortReason(string error)
        {
            string line = (error ?? "").Split('\n')[0].Trim();
            const string Tag = "[MCP ERROR] ";
            if (line.StartsWith(Tag, StringComparison.Ordinal))
                line = line.Substring(Tag.Length);
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^server '[^']*'(?: failed to start:|:)\s*");
            return m.Success && m.Length < line.Length ? line.Substring(m.Length) : line;
        }

        /// <summary>Test hook: fills a server's exposed-tool cache as a start would, without launching it.</summary>
        internal void SeedExposedToolsForTest(string server, IEnumerable<McpToolInfo> tools) =>
            _slots[server].ExposedTools = BuildExposedTools(
                tools.Where(t => _slots[server].Config.AllowsTool(t.Name)), WarnOnce);

        /// <summary>
        /// OpenAI-format function objects for every tool of every STARTED server, honouring
        /// each allowlist: <c>{"type":"function","function":{"name":"mcp__s__t","description":
        /// "[s MCP] ...","parameters":{...}}}</c>. A snapshot filled when a server starts,
        /// restarts or is re-listed, and emptied when it dies. No I/O here: this runs on every
        /// request build. Callers must not mutate the objects;
        /// <see cref="ToolRegistry.BuildToolsArray(IEnumerable{JObject})"/> clones them.
        /// </summary>
        public IReadOnlyList<JObject> GetExposedTools()
        {
            List<JObject>? all = null;
            foreach (var slot in _slots.Values)
            {
                var tools = slot.ExposedTools;
                if (tools.Count > 0)
                    (all ??= new List<JObject>()).AddRange(tools);
            }
            return (IReadOnlyList<JObject>?)all ?? Array.Empty<JObject>();
        }

        /// <summary>
        /// Formats listed tools as OpenAI function objects. A tool whose qualified name is longer
        /// than <see cref="MaxFunctionNameLength"/> or has a character outside [A-Za-z0-9_-]
        /// cannot be called through the chat API, so it is skipped with a warning.
        /// </summary>
        internal static IReadOnlyList<JObject> BuildExposedTools(IEnumerable<McpToolInfo> tools, Action<string>? warn)
        {
            var result = new List<JObject>();
            foreach (var t in tools)
            {
                string name = t.QualifiedName;
                if (name.Length > MaxFunctionNameLength || !IsFunctionNameSafe(name))
                {
                    warn?.Invoke($"[MCP] server '{t.Server}': tool '{t.Name}' not exposed: '{name}' is not a valid " +
                                 $"function name (at most {MaxFunctionNameLength} chars of [A-Za-z0-9_-]).");
                    continue;
                }
                var parameters = t.InputSchema.Count > 0
                    ? (JObject)t.InputSchema.DeepClone()
                    : new JObject { ["type"] = "object", ["properties"] = new JObject() };
                result.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"]        = name,
                        ["description"] = $"[{t.Server} MCP] {t.Description}".TrimEnd(),
                        ["parameters"]  = parameters,
                    },
                });
            }
            return result;
        }

        private static bool IsFunctionNameSafe(string name)
        {
            foreach (char c in name)
                if (!(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'))
                    return false;
            return true;
        }

        /// <summary>Logs each distinct warning once per manager (the cache is rebuilt on every restart).</summary>
        private void WarnOnce(string message)
        {
            lock (_warned)
            {
                if (!_warned.Add(message)) return;
            }
            DevMindLog.Write(message);
        }

        private static async Task<List<McpToolInfo>> ListAllowedToolsAsync(ServerSlot slot, ServerSession session, CancellationToken ct)
        {
            var tools = await session.Client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
            var list = new List<McpToolInfo>();
            foreach (var t in tools)
            {
                if (!slot.Config.AllowsTool(t.Name)) continue;
                list.Add(new McpToolInfo
                {
                    Server        = slot.Config.Name,
                    Name          = t.Name,
                    QualifiedName = McpToolName.Build(slot.Config.Name, t.Name),
                    Description   = t.Description ?? "",
                    InputSchema   = ToJObject(t.ProtocolTool.InputSchema),
                });
            }
            return list;
        }

        /// <summary>
        /// Fills the exposed-tool cache right after a (re)start, so the tools appear in the next
        /// request. A listing failure leaves the server running with nothing exposed (calls
        /// still work and report their own errors) and is logged.
        /// </summary>
        private async Task RefreshExposedToolsAsync(ServerSlot slot, ServerSession session, CancellationToken ct)
        {
            try
            {
                slot.ExposedTools = BuildExposedTools(
                    await ListAllowedToolsAsync(slot, session, ct).ConfigureAwait(false), WarnOnce);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                slot.ExposedTools = Array.Empty<JObject>();
                DevMindLog.Write($"[MCP] server '{slot.Config.Name}': tools/list after start failed; no tools exposed: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var slot in _slots.Values)
            {
                await slot.Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    slot.ExposedTools = Array.Empty<JObject>();
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
            /// <summary>Replaced (under the gate) when RestartAsync re-reads devmind.json.</summary>
            public McpServerConfig Config { get; set; }
            /// <summary>Set when a live session is lost; the next successful start reports it as a restart.</summary>
            public string? PendingRestartReason;
            public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);
            public ServerSession? Session;
            /// <summary>Start failures / crashes since the last success. At 2, no further relaunch.</summary>
            public int ConsecutiveFailures;
            public string? LastError;
            /// <summary>Short form of <see cref="LastError"/> for status lines: the cause only, no stderr tail.</summary>
            public string? LastReason;
            /// <summary>True while a start is in flight (read without the gate by GetStatuses).</summary>
            public volatile bool Starting;
            /// <summary>Cached OpenAI tool objects; empty whenever there is no live session. Swapped whole, never mutated.</summary>
            public volatile IReadOnlyList<JObject> ExposedTools = Array.Empty<JObject>();
        }

        private async Task<(ServerSession? session, string? error)> AcquireSessionAsync(string server, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return (null, $"[MCP ERROR] server '{server}': the MCP client manager has been disposed.");
            if (!_slots.TryGetValue(server ?? "", out var slot))
                return (null, UnknownServer(server));

            // A relaunch, or the end of relaunching, is announced once — after the gate is
            // released, so a slow notice handler never holds up the next caller.
            McpNotice? notice = null;
            await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (slot.Session != null && slot.Session.IsAlive)
                    return (slot.Session, null);

                if (slot.Session != null)
                {
                    // Died between calls: count it, keep its stderr for the message, reap it.
                    slot.LastError = DeadMessage(slot.Config.Name, "the server process exited", null, slot.Session);
                    slot.LastReason = "the server process exited";
                    slot.PendingRestartReason = ExitReason(slot.Session, "the server process exited");
                    notice = CountFailure(slot);
                    DevMindLog.Write(slot.LastError);
                    slot.ExposedTools = Array.Empty<JObject>();
                    await slot.Session.DisposeAsync().ConfigureAwait(false);
                    slot.Session = null;
                }

                // Initial start + ONE automatic restart attempt; after that, report without relaunching.
                if (slot.ConsecutiveFailures >= 2)
                    return (null, (slot.LastError ?? $"[MCP ERROR] server '{server}' is unavailable.") +
                                  " (automatic restart already attempted; not relaunching)");

                slot.Starting = true;
                try
                {
                    slot.Session = await ServerSession.StartAsync(slot.Config, ct).ConfigureAwait(false);
                    await RefreshExposedToolsAsync(slot, slot.Session, ct).ConfigureAwait(false);
                    slot.LastError = null;
                    slot.LastReason = null;
                    if (slot.PendingRestartReason != null)
                    {
                        notice = new McpNotice { Server = slot.Config.Name, Kind = McpNoticeKind.Restarted, Reason = slot.PendingRestartReason };
                        slot.PendingRestartReason = null;
                    }
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
                    slot.LastReason = (ex.InnerException ?? ex).Message;
                    notice = CountFailure(slot);
                    DevMindLog.Write(slot.LastError);
                    if (partial != null)
                        await partial.DisposeAsync().ConfigureAwait(false);
                    return (null, slot.LastError);
                }
            }
            finally
            {
                slot.Starting = false;
                slot.Gate.Release();
                Raise(notice);
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

            McpNotice? notice = null;
            await slot.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                string msg = DeadMessage(slot.Config.Name, what + " — the server died", ex, session);
                if (ReferenceEquals(slot.Session, session))
                {
                    slot.LastError = msg;
                    slot.LastReason = what + " — the server died";
                    slot.PendingRestartReason = ExitReason(session, "the server died during a call");
                    notice = CountFailure(slot);
                    DevMindLog.Write(msg);
                    slot.ExposedTools = Array.Empty<JObject>();
                    slot.Session = null;
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                return msg + (slot.ConsecutiveFailures >= 2
                    ? " Automatic restart already attempted; not relaunching."
                    : " It will be restarted on the next call.");
            }
            finally
            {
                slot.Gate.Release();
                Raise(notice);
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
