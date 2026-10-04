// File: ActiveJobMarkerTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Per-job active markers (H-66), the dead-server sweep (H-03), and the start/continue
// trace events (H-02).
//
// Several McpServer processes share one tasks folder. "Another process" is simulated the
// way it looks on disk: a marker written with the pid (and start time) of a real, live
// child process — cmd.exe /c pause — that this test owns and kills afterwards. A dead
// owner is a pid no process holds.
//
// Every test runs against a private DEVMIND_TASKS_DIR, restored afterwards, inside the
// serial ProcessEnvironment collection (H-44).

using System.Diagnostics;
using System.Text.Json;
using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    [Collection(ProcessEnvironmentCollection.Name)]
    public sealed class ActiveJobMarkerTests : IDisposable
    {
        // A pid no process holds: GetProcessById throws for it.
        private const int DeadPid = 2147483644;

        private readonly string _tasksDir;
        private readonly string _workDir;
        private readonly string? _priorTasksDir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;
        private readonly List<Process> _children = new();

        public ActiveJobMarkerTests()
        {
            _priorTasksDir = Environment.GetEnvironmentVariable("DEVMIND_TASKS_DIR");
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            _tasksDir = Path.Combine(Path.GetTempPath(), "devmind-test-tasks", $"markers_{Guid.NewGuid():N}");
            _workDir = Path.Combine(Path.GetTempPath(), $"devmind_markers_job_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tasksDir);
            Directory.CreateDirectory(_workDir);
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _tasksDir);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
        }

        public void Dispose()
        {
            foreach (var p in _children)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                p.Dispose();
            }
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _priorTasksDir);
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            try { Directory.Delete(_tasksDir, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private Process StartOtherProcess()
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true })!;
            _children.Add(p);
            return p;
        }

        private string MarkerFile(string jobId) => Path.Combine(_tasksDir, $"_active_{jobId}.json");
        private string LegacyMarkerFile => Path.Combine(_tasksDir, "_active.json");
        private string SidecarFile(string jobId) => Path.Combine(_tasksDir, $"{jobId}.result.json");

        private static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");

        private void WriteMarker(string path, string jobId, int pid, DateTime? processStartUtc = null,
            string? transcript = null)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                job_id = jobId,
                state = "running",
                pid,
                process_start_utc = processStartUtc.HasValue ? Utc(processStartUtc.Value) : null,
                working_dir = @"C:\work\other",
                started_at_utc = "2026-10-04 10:00:00",
                transcript,
            }));
        }

        private static async Task WaitForTerminal(AgentJob job, int timeoutMs = 30_000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(20);
            Assert.Equal(AgentJobState.Done, job.State);
        }

        // ── H-66: a job's end clears its own marker only ─────────────────────────

        [Fact]
        public async Task JobEnd_ClearsOnlyItsOwnMarker_NeverAnotherServersMarker()
        {
            var other = StartOtherProcess();
            string otherMarker = MarkerFile("job-other");
            WriteMarker(otherMarker, "job-other", other.Id, other.StartTime);

            using var server = new FakeLlmServer();   // task_done on every turn
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            var created = new List<string>();
            using var watcher = new FileSystemWatcher(_tasksDir, "_active*.json") { EnableRaisingEvents = true };
            watcher.Created += (_, e) => { lock (created) created.Add(e.Name!); };

            AgentJob job;
            using (var mgr = new AgentJobManager())
            {
                // A second manager starting up (its sweep) must not touch the live marker either.
                using (new AgentJobManager()) { }
                Assert.True(File.Exists(otherMarker));

                job = mgr.Start("p", _workDir, 5, 30, allowCommit: false, verifyBuild: false);
                await WaitForTerminal(job);

                Assert.True(File.Exists(otherMarker), "finishing a job deleted another process's marker");
                Assert.False(File.Exists(MarkerFile(job.Id)), "the finished job's own marker was left behind");
                Assert.True(AgentJobManager.IsJobActiveElsewhere());
            }

            // Graceful shutdown clears this manager's markers only.
            Assert.True(File.Exists(otherMarker), "shutdown deleted another process's marker");
            await Task.Delay(200);   // let the watcher deliver
            lock (created) Assert.Contains($"_active_{job.Id}.json", created);
        }

        // ── IsJobActiveElsewhere ─────────────────────────────────────────────────

        [Fact]
        public void IsJobActiveElsewhere_AnotherLiveProcessesMarker_IsTrue()
        {
            var other = StartOtherProcess();
            WriteMarker(MarkerFile("job-live"), "job-live", other.Id, other.StartTime);
            Assert.True(AgentJobManager.IsJobActiveElsewhere());
        }

        [Fact]
        public void IsJobActiveElsewhere_OwnProcessesMarker_IsFalse()
        {
            WriteMarker(MarkerFile("job-mine"), "job-mine", Environment.ProcessId, Process.GetCurrentProcess().StartTime);
            Assert.False(AgentJobManager.IsJobActiveElsewhere());
        }

        [Fact]
        public void IsJobActiveElsewhere_DeadProcessesMarker_IsFalse()
        {
            WriteMarker(MarkerFile("job-dead"), "job-dead", DeadPid);
            Assert.False(AgentJobManager.IsJobActiveElsewhere());
        }

        [Fact]
        public void APidReusedByAnUnrelatedProcess_CountsAsDead()
        {
            // The pid is alive, but the process holding it started long after the marker's
            // owner did: the owner crashed and the number was handed out again.
            var other = StartOtherProcess();
            WriteMarker(MarkerFile("job-reused"), "job-reused", other.Id, other.StartTime.AddHours(-3));

            Assert.False(AgentJobManager.IsJobActiveElsewhere());
            Assert.Contains("job-reused", ActiveJobMarkers.SweepDeadMarkers());
        }

        // ── Legacy _active.json ──────────────────────────────────────────────────

        [Fact]
        public void ALegacyMarker_IsRead_LikeAnyOther()
        {
            var other = StartOtherProcess();
            WriteMarker(LegacyMarkerFile, "job-legacy", other.Id);   // older servers wrote no start time
            Assert.True(AgentJobManager.IsJobActiveElsewhere());

            other.Kill(entireProcessTree: true);
            other.WaitForExit();
            Assert.False(AgentJobManager.IsJobActiveElsewhere());

            ActiveJobMarkers.SweepDeadMarkers();
            Assert.False(File.Exists(LegacyMarkerFile));
            Assert.True(File.Exists(SidecarFile("job-legacy")));
        }

        [Fact]
        public async Task ALegacyMarker_IsNeverWritten()
        {
            using var server = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            var created = new List<string>();
            using var watcher = new FileSystemWatcher(_tasksDir, "_active*.json") { EnableRaisingEvents = true };
            watcher.Created += (_, e) => { lock (created) created.Add(e.Name!); };
            watcher.Changed += (_, e) => { lock (created) created.Add(e.Name!); };

            using (var mgr = new AgentJobManager())
            {
                var job = mgr.Start("p", _workDir, 5, 30, allowCommit: false, verifyBuild: false);
                await WaitForTerminal(job);
            }
            await Task.Delay(200);

            Assert.False(File.Exists(LegacyMarkerFile));
            lock (created)
            {
                Assert.NotEmpty(created);
                Assert.DoesNotContain("_active.json", created);
            }
        }

        // ── H-03: the dead-server sweep ──────────────────────────────────────────

        [Fact]
        public void Sweep_DeadMarkerWithNoSidecar_WritesServerRestartSidecar_AndRemovesTheMarker()
        {
            string transcript = Path.Combine(_tasksDir, "job-died-20261004-100000.log");
            File.WriteAllText(transcript,
                "[AGENTIC] Iteration 1/40 — 1,234 / 262,144 (0%)\n" +
                "[FILE] Saved a.txt\n" +
                "The model said: Iteration 99 of my plan is next.\n" +
                "[AGENTIC] Iteration 7/40 — 45,678 / 262,144 (17%)\n" +
                "[SHELL] > dotnet build\n");
            WriteMarker(MarkerFile("job-died"), "job-died", DeadPid, transcript: transcript);

            using (new AgentJobManager()) { }   // the startup sweep

            Assert.False(File.Exists(MarkerFile("job-died")));
            using var doc = JsonDocument.Parse(File.ReadAllText(SidecarFile("job-died")));
            JsonElement r = doc.RootElement;
            Assert.Equal("stopped_incomplete", r.GetProperty("state").GetString());
            Assert.Equal(new[] { "server_restart" }, r.GetProperty("incomplete_reasons").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal($"server process {DeadPid} exited while the job was running", r.GetProperty("error").GetString());
            Assert.Equal(7, r.GetProperty("iterations").GetInt32());
            Assert.Equal("2026-10-04 10:00:00", r.GetProperty("started_at_utc").GetString());
            Assert.False(string.IsNullOrEmpty(r.GetProperty("ended_at_utc").GetString()));
            Assert.Contains("detected", r.GetProperty("ended_at_note").GetString());
            Assert.Equal(@"C:\work\other", r.GetProperty("working_dir").GetString());
            Assert.Equal(transcript, r.GetProperty("transcript_path").GetString());
        }

        [Fact]
        public void Sweep_NoReadableIterationLine_LeavesIterationsNull()
        {
            string transcript = Path.Combine(_tasksDir, "job-quiet.log");
            File.WriteAllText(transcript, "[job] test baseline: exit 0\nIteration 3 — not the driver's line\n");
            WriteMarker(MarkerFile("job-quiet"), "job-quiet", DeadPid, transcript: transcript);
            WriteMarker(MarkerFile("job-notranscript"), "job-notranscript", DeadPid);

            ActiveJobMarkers.SweepDeadMarkers();

            foreach (string id in new[] { "job-quiet", "job-notranscript" })
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SidecarFile(id)));
                Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("iterations").ValueKind);
            }
        }

        [Fact]
        public void Sweep_LeavesALiveProcessesMarkerUntouched()
        {
            var other = StartOtherProcess();
            WriteMarker(MarkerFile("job-running"), "job-running", other.Id, other.StartTime);
            string before = File.ReadAllText(MarkerFile("job-running"));

            using (new AgentJobManager()) { }

            Assert.Equal(before, File.ReadAllText(MarkerFile("job-running")));
            Assert.False(File.Exists(SidecarFile("job-running")));
        }

        [Fact]
        public void Sweep_DeadMarkerWithAnExistingSidecar_LeavesTheSidecar_AndRemovesTheMarker()
        {
            const string finished = "{\"job_id\":\"job-finished\",\"state\":\"done\",\"answer\":\"all good\"}";
            File.WriteAllText(SidecarFile("job-finished"), finished);
            WriteMarker(MarkerFile("job-finished"), "job-finished", DeadPid);

            ActiveJobMarkers.SweepDeadMarkers();

            Assert.False(File.Exists(MarkerFile("job-finished")));
            Assert.Equal(finished, File.ReadAllText(SidecarFile("job-finished")));
        }

        [Theory]
        [InlineData("[AGENTIC] Iteration 12/40 — 1.234 / 262.144 (0%)", 12)]   // another culture's separators
        [InlineData("[AGENTIC] Iteration 3 — 900 / 32,768 (2%)", 3)]          // uncapped label
        [InlineData("[AGENTIC] Iteration 5/40", null)]                         // not the full shape
        [InlineData("text [AGENTIC] Iteration 5/40 — 1 / 2 (50%)", null)]      // not at line start
        public void ParseLastIteration_RequiresTheDriversExactLine(string line, int? expected)
        {
            Assert.Equal(expected, ActiveJobMarkers.ParseLastIteration("before\n" + line + "\nafter"));
        }

        // ── devmind_task_status / _result for a job that died with its server ────

        [Fact]
        public async Task StatusAndResult_ForAJobThatDiedWithItsServer_ReportItFromTheSidecar()
        {
            using var server = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new AgentJobManager();
            var tools = new AgentTaskTools(mgr);

            // The marker appears after this process started — a server that died later.
            WriteMarker(MarkerFile("job-gone"), "job-gone", DeadPid);

            foreach (string json in new[]
                     {
                         await tools.TaskStatus("job-gone", null, CancellationToken.None),
                         await tools.TaskResult("job-gone", CancellationToken.None),
                     })
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                Assert.False(root.GetProperty("can_continue").GetBoolean());
                Assert.Contains("DIED WITH ITS SERVER", root.GetProperty("note").GetString());
                Assert.Contains("fresh task with a continuation brief", root.GetProperty("note").GetString());
                JsonElement result = root.GetProperty("result");
                Assert.Equal("stopped_incomplete", result.GetProperty("state").GetString());
                Assert.Equal("server_restart", result.GetProperty("incomplete_reasons")[0].GetString());
            }
            Assert.False(File.Exists(MarkerFile("job-gone")));
        }

        // ── H-02: start / continue trace events ──────────────────────────────────

        private sealed class TraceCapture : IDisposable
        {
            private readonly Action<string, string, IDictionary<string, object?>> _prior;
            public List<(string Name, IDictionary<string, object?> Data)> Events { get; } = new();

            public TraceCapture()
            {
                _prior = AgentTaskTools.TraceSink;
                AgentTaskTools.TraceSink = (_, name, data) => { lock (Events) Events.Add((name, data)); };
            }

            public void Dispose() => AgentTaskTools.TraceSink = _prior;
        }

        [Fact]
        public async Task StartAndContinue_EmitBeginAndEnd_OnSuccess()
        {
            using var server = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new AgentJobManager();
            var tools = new AgentTaskTools(mgr);
            using var trace = new TraceCapture();

            string started = await tools.TaskStart("Do the thing.", _workDir, max_depth: 5);
            string jobId = JsonDocument.Parse(started).RootElement.GetProperty("job_id").GetString()!;
            await WaitForTerminal(mgr.Get(jobId)!);

            string continued = await tools.TaskContinue(jobId, "and the next thing");
            string childId = JsonDocument.Parse(continued).RootElement.GetProperty("job_id").GetString()!;
            await WaitForTerminal(mgr.Get(childId)!);

            var events = trace.Events.ToList();
            Assert.Equal(new[] { "mcp.tool.begin", "mcp.tool.end", "mcp.tool.begin", "mcp.tool.end" },
                events.Select(e => e.Name));

            Assert.Equal("devmind_task_start", events[0].Data["tool"]);
            Assert.Equal(jobId, events[1].Data["job_id"]);
            Assert.Null(events[1].Data["error"]);
            Assert.IsType<long>(events[1].Data["elapsed_ms"]);

            Assert.Equal("devmind_task_continue", events[2].Data["tool"]);
            Assert.Equal(jobId, events[2].Data["job_id"]);           // begin carries the parent
            Assert.Equal(childId, events[3].Data["job_id"]);
            Assert.Equal(jobId, events[3].Data["parent_job_id"]);
            Assert.Null(events[3].Data["error"]);
        }

        [Fact]
        public async Task StartAndContinue_EmitBeginAndEnd_OnError()
        {
            using var server = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new AgentJobManager();
            var tools = new AgentTaskTools(mgr);
            using var trace = new TraceCapture();

            await tools.TaskStart("", _workDir);
            await tools.TaskContinue("job-nope", "continue");

            var events = trace.Events.ToList();
            Assert.Equal(4, events.Count);
            Assert.Equal("mcp.tool.end", events[1].Name);
            Assert.Equal("prompt is required.", events[1].Data["error"]);
            Assert.Null(events[1].Data["job_id"]);

            Assert.Equal("devmind_task_continue", events[3].Data["tool"]);
            Assert.Contains("job-nope", (string)events[3].Data["error"]!);
            Assert.Equal("job-nope", events[3].Data["parent_job_id"]);
        }
    }
}
