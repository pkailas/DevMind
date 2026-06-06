// File: McpServices.cs  v2.5
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Diagnostic policy: all Console.Write / Console.WriteLine calls in this project
// MUST target Console.Error (stderr). stdout is reserved exclusively for the
// JSON-RPC message framing of the stdio MCP transport — any write to stdout
// outside the transport layer will corrupt the client connection.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DevMind;

namespace DevMind.McpServer
{
    /// <summary>
    /// Session-scoped DI container for MCP server state.
    /// One instance per server process (stdio = one client connection lifetime).
    /// Owns the ShellRunner, FileContentCache, MemoryManager, and session-level
    /// tracking dictionaries shared by all tool methods.
    /// </summary>
    internal sealed class McpServices : IDisposable
    {
        public string WorkingDirectory { get; }
        public MemoryManager          Memory    { get; }
        public FileContentCache       FileCache { get; }
        public ShellRunner            Shell     { get; }
        public LanguageServerRouter?  Lsp       { get; }

        /// <summary>
        /// Tracks filenames (basename only) read during this session.
        /// Controls outline-vs-full behaviour: a re-read file gets an outline
        /// unless force_full is set.
        /// </summary>
        public HashSet<string> FilesRead { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Tool dispatch: Channel-based FIFO queue ──────────────────────────────
        // The MCP SDK dispatches each tool/call on its own Task concurrently.
        // SemaphoreSlim(1,1) serializes execution but is not FIFO — the scheduler
        // picks an arbitrary waiter on release, producing out-of-order responses.
        //
        // Fix: an unbounded Channel<DispatchItem> with a single consumer task.
        // Channel writes happen in protocol-message order (the SDK reads stdin
        // sequentially, so Task starts are ordered; TryWrite on an unbounded
        // channel is synchronous and contention-free). The consumer drains FIFO,
        // executing one work item at a time, guaranteeing both serialization and
        // arrival-order responses.

        private sealed record DispatchItem(
            Func<Task<string>>              Work,
            TaskCompletionSource<string>    Tcs,
            CancellationToken               Ct);

        private readonly Channel<DispatchItem> _dispatchChannel =
            Channel.CreateUnbounded<DispatchItem>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        private readonly Task _consumerTask;

        // Session-baseline snapshots: absolute path → original content captured at first read
        // or first patch. Powers diff_file.
        private readonly Dictionary<string, string> _fileSnapshots =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
       private readonly object _snapshotLock = new object();

       public record SshHostConfig(string Host, string User, string Key, string? Fingerprint);
         public IReadOnlyDictionary<string, SshHostConfig> SshHosts { get; }
         public IReadOnlyDictionary<string, string> HttpEndpoints { get; }
         public IReadOnlyDictionary<string, string> DbConnections { get; }

        // Normalize a configured SSH host-key fingerprint for comparison against
        // SSH.NET's HostKeyEventArgs.FingerPrintSHA256, which is base64 with no padding
        // and no "SHA256:" prefix. Accepts the OpenSSH display form ("SHA256:<base64>")
        // or bare base64, with or without '=' padding.
        private static string? NormalizeFingerprint(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            string s = raw.Trim();
            if (s.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("SHA256:".Length).Trim();

            s = s.TrimEnd('=');
            return s.Length == 0 ? null : s;
        }

        public McpServices(string workingDirectory)
        {
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(workingDirectory);

            if (!Directory.Exists(WorkingDirectory))
                Console.Error.WriteLine(
                    $"[McpServer] Warning: working directory does not exist: {WorkingDirectory}");

            Memory    = new MemoryManager(WorkingDirectory);
            FileCache = new FileContentCache();
           Shell     = new ShellRunner(WorkingDirectory);

            var sshHostsJson = Environment.GetEnvironmentVariable("DEVMIND_SSH_HOSTS");
            if (!string.IsNullOrWhiteSpace(sshHostsJson))
            {
                try
                {
                    var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(sshHostsJson);
                    var hosts = new Dictionary<string, SshHostConfig>(StringComparer.OrdinalIgnoreCase);
                    if (raw != null)
                    {
                        foreach (var (alias, el) in raw)
                        {
                            string host = el.GetProperty("host").GetString() ?? alias;
                            string user = el.GetProperty("user").GetString() ?? "root";
                            string key  = el.TryGetProperty("key", out var kp) ? kp.GetString() ?? "~/.ssh/id_rsa" : "~/.ssh/id_rsa";
                            string? fingerprint = el.TryGetProperty("fingerprint", out var fp)
                                ? NormalizeFingerprint(fp.GetString())
                                : null;
                            hosts[alias] = new SshHostConfig(host, user, key, fingerprint);
                        }
                    }
                    SshHosts = hosts;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[McpServices] Warning: failed to parse DEVMIND_SSH_HOSTS: {ex.Message}");
                    SshHosts = new Dictionary<string, SshHostConfig>();
                }
            }
           else
            {
                SshHosts = new Dictionary<string, SshHostConfig>();
            }

            // ── DEVMIND_HTTP_ENDPOINTS ──────────────────────────────────────────
            var httpEndpointsJson = Environment.GetEnvironmentVariable("DEVMIND_HTTP_ENDPOINTS");
            if (!string.IsNullOrWhiteSpace(httpEndpointsJson))
            {
                try
                {
                    var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(httpEndpointsJson);
                    var endpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (raw != null)
                    {
                        foreach (var (alias, url) in raw)
                        {
                            if (!string.IsNullOrWhiteSpace(url))
                                endpoints[alias] = url;
                        }
                    }
                    HttpEndpoints = endpoints;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[McpServices] Warning: failed to parse DEVMIND_HTTP_ENDPOINTS: {ex.Message}");
                    HttpEndpoints = new Dictionary<string, string>();
                }
            }
           else
            {
                HttpEndpoints = new Dictionary<string, string>();
            }

            // ── DEVMIND_DB_CONNECTIONS ──────────────────────────────────────────
            var dbConnectionsJson = Environment.GetEnvironmentVariable("DEVMIND_DB_CONNECTIONS");
            if (!string.IsNullOrWhiteSpace(dbConnectionsJson))
            {
                try
                {
                    var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(dbConnectionsJson);
                    var connections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (raw != null)
                    {
                        foreach (var (alias, connStr) in raw)
                        {
                            if (!string.IsNullOrWhiteSpace(connStr))
                                connections[alias] = connStr;
                        }
                    }
                    DbConnections = connections;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[McpServices] Warning: failed to parse DEVMIND_DB_CONNECTIONS: {ex.Message}");
                    DbConnections = new Dictionary<string, string>();
                }
            }
            else
            {
                DbConnections = new Dictionary<string, string>();
            }

            Lsp       = LanguageServerRouter.IsEnabled()
                ? new LanguageServerRouter(
                    WorkingDirectory,
                    Environment.GetEnvironmentVariable("DEVMIND_LSP_SERVER_PATH"),
                    Environment.GetEnvironmentVariable("DEVMIND_LSP_TYPESCRIPT_PATH"))
                : null;

            _consumerTask = DrainChannelAsync();
        }

        public void Dispose()
        {
            _dispatchChannel.Writer.TryComplete();
            _consumerTask.Wait(TimeSpan.FromSeconds(2));
            Lsp?.Dispose();
        }

        // ── Dispatch helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Enqueues <paramref name="work"/> for strictly sequential FIFO execution
        /// by the single consumer task. Returns a Task that resolves when the work
        /// completes (or is cancelled / faulted).
        /// </summary>
        public async Task<string> EnqueueAsync(Func<Task<string>> work, CancellationToken ct)
        {
            var tcs  = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new DispatchItem(work, tcs, ct);

            // Register cancellation so a cancelled request does not block the consumer.
            var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            try
            {
                // TryWrite on an unbounded channel never fails.
                _dispatchChannel.Writer.TryWrite(item);
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                reg.Dispose();
            }
        }

        /// <summary>
        /// Single consumer: drains _dispatchChannel sequentially for the lifetime
        /// of the process. Each item executes completely before the next is read.
        /// </summary>
        private async Task DrainChannelAsync()
        {
            await foreach (var item in _dispatchChannel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // If the caller already cancelled before the consumer got here, skip the work.
                if (item.Ct.IsCancellationRequested)
                {
                    item.Tcs.TrySetCanceled(item.Ct);
                    continue;
                }

                try
                {
                    string result = await item.Work().ConfigureAwait(false);
                    item.Tcs.TrySetResult(result);
                }
                catch (OperationCanceledException ex)
                {
                    item.Tcs.TrySetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    item.Tcs.TrySetException(ex);
                }
            }
        }

        // ── Snapshot helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Captures the current on-disk content of <paramref name="fullPath"/> into
        /// _fileSnapshots only if it has not already been snapshotted this session.
        /// No-op if the file does not exist or cannot be read.
        /// </summary>
        public void TrySnapshot(string fullPath)
        {
            lock (_snapshotLock)
            {
                if (_fileSnapshots.ContainsKey(fullPath)) return;
            }

            string? content = null;
            try
            {
                if (File.Exists(fullPath))
                    content = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch { }

            lock (_snapshotLock)
            {
                if (!_fileSnapshots.ContainsKey(fullPath))
                    _fileSnapshots[fullPath] = content ?? string.Empty;
            }
        }

        /// <summary>
        /// Returns true and sets <paramref name="snapshot"/> if the file was snapshotted
        /// this session. Returns false if no snapshot exists.
        /// </summary>
        public bool TryGetSnapshot(string fullPath, out string snapshot)
        {
            lock (_snapshotLock)
            {
                return _fileSnapshots.TryGetValue(fullPath, out snapshot!);
            }
        }

        /// <summary>
        /// Moves a snapshot entry from <paramref name="oldPath"/> to <paramref name="newPath"/>.
        /// Called after rename_file to preserve diff history under the new name.
        /// </summary>
        public void MoveSnapshot(string oldPath, string newPath)
        {
            lock (_snapshotLock)
            {
                if (_fileSnapshots.TryGetValue(oldPath, out string? snap))
                {
                    _fileSnapshots.Remove(oldPath);
                    _fileSnapshots[newPath] = snap;
                }
            }
        }
    }
}
