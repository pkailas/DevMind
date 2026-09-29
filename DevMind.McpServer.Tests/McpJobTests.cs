// File: McpJobTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// MCP client, part 3, headless side: devmind_task_start's mcp_servers. The servers a job
// names are resolved against devmind.json at start time (a typo is rejected with the
// configured names), started and awaited before the first LLM request, recorded in the
// result's "mcp" section, inherited by continuations, and disposed when the job reaches ANY
// terminal state. A server that fails to start never fails the job.
//
// Real jobs run through the real AgentJobManager against stub LLM servers. The MCP manager
// itself is scripted through the McpManagerFactory seam, so disposal can be counted and a
// start can be made to fail, hang or succeed on cue.

using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.McpServer.Tests
{
    /// <summary>Scripted IMcpClientManager: each server's start is a delegate; disposals are counted.</summary>
    internal sealed class ScriptedMcpClients : IMcpClientManager
    {
        private readonly List<string> _names;
        private readonly HashSet<string> _started = new(StringComparer.Ordinal);

        public ScriptedMcpClients(IEnumerable<string> names) => _names = names.ToList();

        /// <summary>Per-server start behaviour; absent = succeeds at once.</summary>
        public Dictionary<string, Func<CancellationToken, Task<string?>>> Starts { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposed;
        public int Calls;

        public IReadOnlyList<string> ServerNames => _names;

        public IReadOnlyList<JObject> GetExposedTools()
        {
            lock (_started)
                return _started.Select(s => new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject { ["name"] = $"mcp__{s}__which", ["description"] = "d", ["parameters"] = new JObject { ["type"] = "object" } },
                }).ToList();
        }

        public IReadOnlyList<McpServerStatus> GetStatuses()
        {
            lock (_started)
                return _names.Select(n => new McpServerStatus
                {
                    Name = n,
                    State = _started.Contains(n) ? McpServerState.Ready : McpServerState.Stopped,
                    ToolCount = _started.Contains(n) ? 1 : 0,
                }).ToList();
        }

        public async Task<string?> StartAsync(string server, CancellationToken ct = default)
        {
            StartEntered.TrySetResult();
            string? err = Starts.TryGetValue(server, out var start) ? await start(ct) : null;
            if (err == null)
                lock (_started) _started.Add(server);
            return err;
        }

        public Task<string?> RestartAsync(string server, CancellationToken ct = default) => StartAsync(server, ct);
        public int CallCount => Calls;
        public event Action<McpNotice> Notice { add { } remove { } }

        public Task<string> CallToolAsync(string server, string tool, JObject? args, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult("ok");
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposed);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class McpJobTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;

        public McpJobTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcpjob_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static McpServerConfig Cfg(string name) => new McpServerConfig { Name = name, Command = name + ".exe" };

        /// <summary>A manager whose factory hands out scripted clients and keeps them for inspection.</summary>
        private static (AgentJobManager mgr, List<ScriptedMcpClients> made) Manager(Action<ScriptedMcpClients>? script = null)
        {
            var made = new List<ScriptedMcpClients>();
            var mgr = new AgentJobManager
            {
                McpManagerFactory = servers =>
                {
                    var c = new ScriptedMcpClients(servers.Select(s => s.Name));
                    script?.Invoke(c);
                    lock (made) made.Add(c);
                    return c;
                },
            };
            return (mgr, made);
        }

        private AgentJob Start(AgentJobManager mgr, params string[] mcp) =>
            mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: false, verifyTests: false,
                runTestBaseline: false, mcpServers: mcp.Select(Cfg).ToList());

        /// <summary>Waits for the worker's finally to finish — EndedAtUtc is set after the MCP dispose.</summary>
        private static async Task WaitForEnd(AgentJob job, int seconds = 30)
        {
            var sw = Stopwatch.StartNew();
            while (job.EndedAtUtc == null)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(seconds), $"{job.Id} still {job.State} after {seconds}s");
                await Task.Delay(25);
            }
        }

        // ── Resolution ───────────────────────────────────────────────────────

        [Fact]
        public void Resolve_absent_or_empty_is_no_servers()
        {
            var configured = new[] { Cfg("comfy") };
            Assert.Empty(AgentJobManager.ResolveMcpServers(null, configured, out var e1)!);
            Assert.Null(e1);
            Assert.Empty(AgentJobManager.ResolveMcpServers(Array.Empty<string>(), configured, out var e2)!);
            Assert.Null(e2);
        }

        [Fact]
        public void Resolve_unknown_name_rejects_and_lists_the_configured_names()
        {
            var configured = new[] { Cfg("comfy"), Cfg("blender") };
            Assert.Null(AgentJobManager.ResolveMcpServers(new[] { "comfy", "comfyui" }, configured, out string? error));
            Assert.Equal("mcp_servers: unknown server 'comfyui' — configured: comfy, blender.", error);

            Assert.Null(AgentJobManager.ResolveMcpServers(new[] { "x" }, Array.Empty<McpServerConfig>(), out string? none));
            Assert.Contains("none are configured", none);
            Assert.Contains("mcpServers", none);
        }

        [Fact]
        public void Resolve_known_names_return_their_configs_once()
        {
            var configured = new[] { Cfg("comfy"), Cfg("blender") };
            var resolved = AgentJobManager.ResolveMcpServers(new[] { "blender", "comfy", "blender" }, configured, out var error);
            Assert.Null(error);
            Assert.Equal(new[] { "blender", "comfy" }, resolved!.Select(c => c.Name));
        }

        // ── Absent = zero behaviour change ───────────────────────────────────

        [Fact]
        public async Task No_mcp_servers_creates_no_manager()
        {
            using var llm = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", llm.BaseUrl);
            var (mgr, made) = Manager();
            using (mgr)
            {
                var job = Start(mgr);
                await WaitForEnd(job);
                Assert.Equal(AgentJobState.Done, job.State);
                Assert.Empty(made);
                Assert.Null(job.Mcp);
                Assert.Null(McpJobReport.Payload(job.Mcp));
            }
        }

        // ── Start failures never fail the job ────────────────────────────────

        [Fact]
        public async Task A_server_that_fails_or_hangs_is_recorded_and_the_job_runs_on()
        {
            using var llm = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", llm.BaseUrl);
            var (mgr, made) = Manager(c =>
            {
                c.Starts["broken"] = _ => Task.FromResult<string?>("[MCP ERROR] server 'broken' failed to start: file not found.\nstderr tail:\nboom");
                c.Starts["hung"] = ct => new TaskCompletionSource<string?>().Task;   // ignores its token
            });
            using (mgr)
            {
                mgr.McpStartTimeout = TimeSpan.FromMilliseconds(300);
                var job = Start(mgr, "good", "broken", "hung");
                await WaitForEnd(job);

                Assert.Equal(AgentJobState.Done, job.State);
                var report = job.Mcp!;
                Assert.Equal(new[] { "good", "broken", "hung" }, report.Requested);
                Assert.Equal(new[] { "good" }, report.Started);
                Assert.Equal("file not found.", report.Failed["broken"]);
                Assert.StartsWith("did not start within", report.Failed["hung"]);
                Assert.Contains("[job] mcp: broken failed to start", job.GetTail());
                Assert.Equal(1, Assert.Single(made).Disposed);
            }
        }

        // ── Continuations ────────────────────────────────────────────────────

        [Fact]
        public async Task A_continuation_inherits_the_list_and_gets_a_fresh_manager()
        {
            using var llm = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", llm.BaseUrl);
            var (mgr, made) = Manager();
            using (mgr)
            {
                var job = Start(mgr, "comfy");
                await WaitForEnd(job);
                Assert.Equal(1, made[0].Disposed);

                var cont = mgr.Continue(job.Id, "keep going", 5, 30, verifyBuild: false, out string error,
                    verifyTests: false, runTestBaseline: false);
                Assert.Null(error);
                Assert.Equal(new[] { "comfy" }, cont!.McpServers.Select(s => s.Name));
                await WaitForEnd(cont);

                Assert.Equal(AgentJobState.Done, cont.State);
                Assert.Equal(2, made.Count);                 // the parent's was disposed, so a new one
                Assert.NotSame(made[0], made[1]);
                Assert.Equal(new[] { "comfy" }, cont.Mcp!.Started);
                Assert.Equal(1, made[1].Disposed);
            }
        }

        // ── Disposal on every terminal state ─────────────────────────────────

        [Fact]
        public async Task Disposed_when_done()
        {
            using var llm = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", llm.BaseUrl);
            var (mgr, made) = Manager();
            using (mgr)
            {
                var job = Start(mgr, "comfy");
                await WaitForEnd(job);
                Assert.Equal(AgentJobState.Done, job.State);
                Assert.False(job.IsIncomplete);
                Assert.Equal(1, Assert.Single(made).Disposed);
            }
        }

        [Fact]
        public async Task Disposed_when_failed()
        {
            // A port nothing listens on: the turn fails on the first request.
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", $"http://127.0.0.1:{port}/v1");

            var (mgr, made) = Manager();
            using (mgr)
            {
                var job = Start(mgr, "comfy");
                await WaitForEnd(job, 60);
                Assert.Equal(AgentJobState.Failed, job.State);
                Assert.Equal(1, Assert.Single(made).Disposed);
            }
        }

        [Fact]
        public async Task Disposed_when_cancelled_while_its_servers_start()
        {
            using var llm = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", llm.BaseUrl);
            ScriptedMcpClients? clients = null;
            var (mgr, made) = Manager(c =>
            {
                clients = c;
                c.Starts["comfy"] = async ct => { await Task.Delay(Timeout.Infinite, ct); return null; };
            });
            using (mgr)
            {
                var job = Start(mgr, "comfy");
                var sw = Stopwatch.StartNew();
                while (clients == null) { Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10)); await Task.Delay(10); }
                await clients.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.True(mgr.Cancel(job.Id));
                await WaitForEnd(job);

                Assert.Equal(AgentJobState.Cancelled, job.State);
                Assert.Equal("job cancelled while the server was starting", job.Mcp!.Failed["comfy"]);
                Assert.Equal(1, Assert.Single(made).Disposed);
            }
        }

        [Fact]
        public async Task Disposed_when_stopped_incomplete()
        {
            using var llm = new ScriptedLlmServer((post, stream, ct) => ScriptedLlmServer.WriteSseAsync(stream,
                ScriptedLlmServer.ToolCallEvent("task_done", "{\"summary\":\"INCOMPLETE: the render was not verified\"}", $"call_{post}")));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", llm.BaseUrl);
            var (mgr, made) = Manager();
            using (mgr)
            {
                var job = Start(mgr, "comfy");
                await WaitForEnd(job);
                Assert.Equal(AgentJobState.Done, job.State);
                Assert.True(job.IsIncomplete);   // displayed as stopped_incomplete
                Assert.Equal(1, Assert.Single(made).Disposed);
            }
        }

        [Fact]
        public void Result_payload_shape()
        {
            var report = new McpJobReport { Requested = new[] { "a", "b" }, Calls = 3 };
            report.Started.Add("a");
            report.Failed["b"] = "why";
            var json = JObject.Parse(System.Text.Json.JsonSerializer.Serialize(McpJobReport.Payload(report)));
            Assert.True(JToken.DeepEquals(JObject.Parse("""
                { "requested": ["a","b"], "started": ["a"], "failed": [ { "server": "b", "reason": "why" } ], "calls": 3 }
                """), json));
        }
    }

    /// <summary>Skips unless DEVMIND_MCP_SMOKE=1 (needs the real comfy-mcp install and "comfy" in devmind.json).</summary>
    public sealed class McpJobSmokeFactAttribute : FactAttribute
    {
        public McpJobSmokeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DEVMIND_MCP_SMOKE") != "1")
                Skip = "Live MCP smoke test: set DEVMIND_MCP_SMOKE=1 to run.";
        }
    }

    public class McpJobSmokeTests
    {
        [McpJobSmokeFact]
        public async Task A_job_scoped_comfy_manager_starts_exposes_tools_and_disposes_cleanly()
        {
            // The path a job with mcp_servers ["comfy"] takes, minus the LLM: resolve against the
            // real devmind.json, start with the job's timeout, expose, dispose.
            var resolved = AgentJobManager.ResolveMcpServers(new[] { "comfy" }, McpServerConfig.Load(), out string? error);
            Assert.True(resolved != null, error);

            var clients = new McpClientManager(resolved!);
            var tail = new System.Text.StringBuilder();
            var report = await AgentJobManager.StartJobMcpServersAsync(clients, new[] { "comfy" },
                TimeSpan.FromSeconds(60), s => tail.Append(s), CancellationToken.None);

            Assert.Equal(new[] { "comfy" }, report.Started);
            Assert.Empty(report.Failed);
            Assert.NotEmpty(clients.GetExposedTools());
            Assert.Contains("[job] mcp: comfy started (", tail.ToString());
            int pid = clients.GetServerProcessId("comfy") ?? throw new Xunit.Sdk.XunitException("no server PID");

            await clients.DisposeAsync();

            Assert.Empty(clients.GetExposedTools());
            bool alive;
            try { using var p = Process.GetProcessById(pid); alive = !p.HasExited; }
            catch (ArgumentException) { alive = false; }
            Assert.False(alive, $"comfy-mcp {pid} survived dispose");
        }
    }
}
