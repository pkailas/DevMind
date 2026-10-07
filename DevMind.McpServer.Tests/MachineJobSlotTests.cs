// File: MachineJobSlotTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-74: one headless job on the model at a time across every McpServer process.
//
// "Another process" is simulated two ways:
//   - a second AgentJobManager in this test process pointed at the SAME lock path — the
//     slot is a file handle, and two handles conflict the same way within one process as
//     across two;
//   - the slot taken directly with MachineJobSlot.TryAcquire (a foreign holder), or by a
//     real child process (powershell.exe) for the crash-release check.
// Cross-process visibility is checked through state files written with the pid of a real,
// live child process (cmd.exe /c pause), as ActiveJobMarkerTests does for markers.
//
// Every test runs against a private DEVMIND_TASKS_DIR and private lock paths, inside the
// serial ProcessEnvironment collection (H-44).

using System.Diagnostics;
using System.Text.Json;
using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    [Collection(ProcessEnvironmentCollection.Name)]
    public sealed class MachineJobSlotTests : IDisposable
    {
        private const int DeadPid = 2147483644;

        private readonly string _tasksDir;
        private readonly string _workDir;
        private readonly string _slotDir;
        private readonly string? _priorTasksDir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;
        private readonly List<Process> _children = new();

        public MachineJobSlotTests()
        {
            _priorTasksDir = Environment.GetEnvironmentVariable("DEVMIND_TASKS_DIR");
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            string root = Path.Combine(Path.GetTempPath(), "devmind-test-tasks", $"slot_{Guid.NewGuid():N}");
            _tasksDir = Path.Combine(root, "tasks");
            _slotDir = Path.Combine(root, "slots");
            _workDir = Path.Combine(Path.GetTempPath(), $"devmind_slot_job_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tasksDir);
            Directory.CreateDirectory(_slotDir);
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
            try { Directory.Delete(Path.GetDirectoryName(_tasksDir)!, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
        }

        private string LockPath(string name) => Path.Combine(_slotDir, name + ".lock");

        private static AgentJobManager NewManager(string lockPath, TimeSpan? poll = null) => new AgentJobManager
        {
            SlotLockPath = lockPath,
            SlotPollInterval = poll ?? TimeSpan.FromMilliseconds(50),
        };

        private AgentJob StartJob(AgentJobManager mgr, string prompt = "p") =>
            mgr.Start(prompt, _workDir, 10, 10, allowCommit: false, verifyBuild: false);

        /// <summary>A fake model server: the requests whose number is in <paramref name="gated"/>
        /// wait for <paramref name="gate"/>; every request answers task_done.</summary>
        private static ScriptedLlmServer TaskDoneServer(Task gate, params int[] gated) =>
            new ScriptedLlmServer(async (post, stream, ct) =>
            {
                if (gated.Contains(post)) await gate.WaitAsync(ct);
                await ScriptedLlmServer.WriteSseAsync(stream,
                    ScriptedLlmServer.ToolCallEvent("task_done", "{\"summary\":\"done\"}", $"call_{post}"));
            });

        private static JobSlotOwner ForeignOwner(string jobId) => new JobSlotOwner
        {
            JobId = jobId,
            Pid = Environment.ProcessId,
            ProcessStartUtc = JobProcess.OwnStartUtc,
            PromptSnippet = "foreign",
            WorkingDir = "C:\\elsewhere",
        };

        // ── Independence and exclusion ───────────────────────────────────────

        [Fact]
        public async Task TwoManagers_SeparateLockPaths_RunAtTheSameTime()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = TaskDoneServer(gate.Task, 1, 2);
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr1 = NewManager(LockPath("one"));
            using var mgr2 = NewManager(LockPath("two"));
            var a = StartJob(mgr1);
            var b = StartJob(mgr2);

            // Both reach Running while the first requests are still parked at the model.
            await WaitUntil(() => a.State == AgentJobState.Running && b.State == AgentJobState.Running);
            Assert.Null(b.WaitingFor);

            gate.SetResult();
            await WaitForTerminal(a);
            await WaitForTerminal(b);
            Assert.Equal(AgentJobState.Done, a.State);
            Assert.Equal(AgentJobState.Done, b.State);
        }

        [Fact]
        public async Task TwoManagers_SharedLockPath_SecondWaitsUntilTheFirstReleases_ThenRuns()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = TaskDoneServer(gate.Task, 1);
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            string shared = LockPath("shared");
            using var mgr1 = NewManager(shared);
            using var mgr2 = NewManager(shared);
            var a = StartJob(mgr1, "first job");
            await WaitUntil(() => a.State == AgentJobState.Running);
            var b = StartJob(mgr2, "second job");

            await WaitUntil(() => b.WaitingFor != null);
            Assert.Equal(AgentJobState.Queued, b.State);
            Assert.Equal("machine_slot_held", b.WaitingFor!.Reason);
            Assert.Equal(a.Id, b.WaitingFor.JobId);
            Assert.Equal(Environment.ProcessId, b.WaitingFor.Pid);
            Assert.NotNull(b.WaitingFor.Since);

            // The lock file names its holder.
            JobSlotOwner? holder = MachineJobSlot.ReadOwner(shared);
            Assert.Equal(a.Id, holder?.JobId);
            Assert.Equal("first job", holder?.PromptSnippet);

            // devmind_task_status: queued, position 0 in its own process, waiting_for the holder.
            using (var status = JsonDocument.Parse(await new AgentTaskTools(mgr2).TaskStatus(b.Id)))
            {
                JsonElement root = status.RootElement;
                Assert.Equal("queued", root.GetProperty("state").GetString());
                Assert.Equal(0, root.GetProperty("queue_position").GetInt32());
                JsonElement waiting = root.GetProperty("waiting_for");
                Assert.Equal(a.Id, waiting.GetProperty("job_id").GetString());
                Assert.Equal(Environment.ProcessId, waiting.GetProperty("pid").GetInt32());
                Assert.Equal("machine_slot_held", waiting.GetProperty("reason").GetString());
            }

            // The waiting job's state file carries the same, for other processes.
            JobStateRecord? state = JobStateFiles.Read(b.Id);
            Assert.Equal("queued", state?.State);
            Assert.Equal(a.Id, state?.WaitingFor?.GetProperty("job_id").GetString());

            Assert.Null(b.SlotAcquiredUtc);
            gate.SetResult();
            await WaitForTerminal(a);
            await WaitForTerminal(b);

            Assert.Equal(AgentJobState.Done, b.State);
            Assert.Null(b.WaitingFor);
            Assert.True(b.StartedAtUtc >= a.EndedAtUtc, "the second job started before the first released the slot");
            // The state file and the release follow the in-memory Done (the job's finally block).
            await WaitUntil(() => JobStateFiles.Read(b.Id)?.State == "done", 5000);
            await WaitUntil(() => MachineJobSlot.ReadOwner(shared) == null, 5000);   // released: record cleared
        }

        [Fact]
        public void TryAcquire_IsExclusive_AndReleaseFreesIt()
        {
            string path = LockPath("exclusive");
            using (var first = MachineJobSlot.TryAcquire(path, ForeignOwner("job-a")))
            {
                Assert.NotNull(first);
                Assert.Null(MachineJobSlot.TryAcquire(path, ForeignOwner("job-b")));
                Assert.Equal("job-a", MachineJobSlot.ReadOwner(path)?.JobId);
            }
            using var second = MachineJobSlot.TryAcquire(path, ForeignOwner("job-b"));
            Assert.NotNull(second);
        }

        // ── VERIFY: the OS releases the lock when the holder is killed ───────

        [Fact]
        public async Task AKilledChildProcessHoldingTheLock_LeavesTheSlotFree()
        {
            string path = LockPath("killed");
            string script =
                $"$f = [System.IO.File]::Open('{path}', 'OpenOrCreate', 'ReadWrite', 'Read'); " +
                "[Console]::Out.WriteLine('held'); [Console]::Out.Flush(); Start-Sleep -Seconds 120";
            var child = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            })!;
            _children.Add(child);

            string? line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("held", line);
            Assert.Null(MachineJobSlot.TryAcquire(path, ForeignOwner("job-x")));   // the child holds it

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            MachineJobSlot? slot = null;
            await WaitUntil(() => (slot = MachineJobSlot.TryAcquire(path, ForeignOwner("job-x"))) != null, 5000);
            slot!.Dispose();
        }

        // ── Waiting: stall clock, cancel ─────────────────────────────────────

        [Fact]
        public async Task AJobWaitingLongerThanItsStallWindow_IsNotStalled_AndRunsOnceTheSlotFrees()
        {
            using var server = TaskDoneServer(Task.CompletedTask);
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            string path = LockPath("stall");
            var foreign = MachineJobSlot.TryAcquire(path, ForeignOwner("job-foreign"))!;
            using var mgr = NewManager(path);
            mgr.StallWindowOverride = TimeSpan.FromMilliseconds(300);
            mgr.WatchdogPollOverride = TimeSpan.FromMilliseconds(10);

            var job = StartJob(mgr);
            await WaitUntil(() => job.WaitingFor != null);
            Assert.Equal("job-foreign", job.WaitingFor!.JobId);

            await Task.Delay(1500);   // five stall windows
            Assert.Equal(AgentJobState.Queued, job.State);
            Assert.Null(job.StallReason);

            foreign.Dispose();
            await WaitForTerminal(job);
            Assert.Equal(AgentJobState.Done, job.State);
            Assert.Null(job.StallReason);
            Assert.Null(job.Error);
        }

        [Fact]
        public async Task CancelWhileWaiting_EndsTheWait_AndNeverTakesTheSlot()
        {
            int posts = 0;
            using var server = new ScriptedLlmServer(async (post, stream, ct) =>
            {
                Interlocked.Increment(ref posts);
                await ScriptedLlmServer.WriteSseAsync(stream,
                    ScriptedLlmServer.ToolCallEvent("task_done", "{\"summary\":\"done\"}", $"call_{post}"));
            });
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            string path = LockPath("cancel");
            var foreign = MachineJobSlot.TryAcquire(path, ForeignOwner("job-foreign"))!;
            using var mgr = NewManager(path);
            var tools = new AgentTaskTools(mgr);

            var job = StartJob(mgr);
            await WaitUntil(() => job.WaitingFor != null);

            using (var cancel = JsonDocument.Parse(await tools.TaskCancel(job.Id)))
                Assert.True(cancel.RootElement.GetProperty("cancelled").GetBoolean());

            Assert.Equal(AgentJobState.Cancelled, job.State);
            await Task.Delay(300);   // several polls: the worker must have left the wait
            Assert.Null(job.SlotAcquiredUtc);
            Assert.Null(job.StartedAtUtc);
            Assert.Null(job.WaitingFor);
            Assert.Equal(0, Volatile.Read(ref posts));
            Assert.Equal("cancelled", JobStateFiles.Read(job.Id)?.State);
            Assert.Equal("job-foreign", MachineJobSlot.ReadOwner(path)?.JobId);   // still the foreign holder's
            Assert.Empty(Directory.GetFiles(MachineJobSlot.TicketsDir(path)));   // its ticket went with it

            // The worker is not stuck: the next job runs once the slot frees.
            foreign.Dispose();
            var next = StartJob(mgr);
            await WaitForTerminal(next);
            Assert.Equal(AgentJobState.Done, next.State);
        }

        // ── Fairness ─────────────────────────────────────────────────────────

        [Fact]
        public async Task AFreedSlot_GoesToTheEarliestQueuedWaiter_NotBackToTheReleasingProcess()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = TaskDoneServer(gate.Task, 1);
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            string shared = LockPath("fair");
            using var mgr1 = NewManager(shared, TimeSpan.FromMilliseconds(20));
            // The other process polls slowly: without tickets, mgr1's next job — which tries the
            // instant its first job releases — would always win the race.
            using var mgr2 = NewManager(shared, TimeSpan.FromMilliseconds(500));

            var a1 = StartJob(mgr1, "a1");
            await WaitUntil(() => a1.State == AgentJobState.Running);
            var b = StartJob(mgr2, "b");
            await WaitUntil(() => b.WaitingFor != null);
            await Task.Delay(20);
            var a2 = StartJob(mgr1, "a2");   // queued after b

            gate.SetResult();
            await WaitForTerminal(a1);

            // a2 is now at its process's head, and waits behind b's earlier ticket.
            await WaitUntil(() => a2.WaitingFor != null || a2.State != AgentJobState.Queued);
            await WaitForTerminal(b);
            await WaitForTerminal(a2);

            Assert.True(b.StartedAtUtc < a2.StartedAtUtc,
                $"b (queued first) started {b.StartedAtUtc:O}, a2 started {a2.StartedAtUtc:O}");
            Assert.Equal(AgentJobState.Done, b.State);
            Assert.Equal(AgentJobState.Done, a2.State);
        }

        // ── Visibility across processes ──────────────────────────────────────

        private Process StartOtherProcess()
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true })!;
            _children.Add(p);
            return p;
        }

        private void WriteStateFile(string jobId, int pid, DateTime? processStartUtc, string state,
            object? waitingFor = null, string? transcript = null)
        {
            File.WriteAllText(Path.Combine(_tasksDir, jobId + ".state.json"), JsonSerializer.Serialize(new
            {
                job_id = jobId,
                pid,
                process_start_utc = processStartUtc?.ToString("yyyy-MM-dd HH:mm:ss"),
                state,
                queued_utc = DateTime.UtcNow.AddMinutes(-3).ToString("yyyy-MM-dd HH:mm:ss"),
                started_utc = state == "running" ? DateTime.UtcNow.AddMinutes(-2).ToString("yyyy-MM-dd HH:mm:ss") : null,
                ended_utc = (string?)null,
                prompt_snippet = $"brief for {jobId}",
                working_dir = "C:\\repos\\Other",
                transcript_path = transcript,
                waiting_for = waitingFor,
            }));
        }

        [Fact]
        public async Task TaskList_ShowsAnotherProcessesJobs_AndADeadOwnersQueuedJobAsOrphaned()
        {
            Process other = StartOtherProcess();
            DateTime otherStart = other.StartTime.ToUniversalTime();
            string transcript = Path.Combine(_tasksDir, "job-2210-20261007-101500.log");
            File.WriteAllText(transcript, "[AGENTIC] working on it\n");
            WriteStateFile("job-2210", other.Id, otherStart, "running", transcript: transcript);
            WriteStateFile("job-2211", other.Id, otherStart, "queued",
                waitingFor: new { reason = "machine_slot_held", job_id = "job-2210", pid = other.Id, since = "2026-10-07 10:15:00", ahead = 0 });
            WriteStateFile("job-2212", DeadPid, null, "queued");
            WriteStateFile("job-2213", DeadPid, null, "done");   // finished under a dead server: not listed

            using var mgr = NewManager(LockPath("list"));
            var tools = new AgentTaskTools(mgr);

            using var doc = JsonDocument.Parse(await tools.TaskList());
            var jobs = doc.RootElement.GetProperty("jobs").EnumerateArray()
                .ToDictionary(j => j.GetProperty("job_id").GetString()!, j => j.Clone());

            Assert.Equal("running", jobs["job-2210"].GetProperty("state").GetString());
            Assert.True(jobs["job-2210"].GetProperty("other_process").GetBoolean());
            Assert.Equal(other.Id, jobs["job-2210"].GetProperty("owner_pid").GetInt32());
            Assert.Equal("brief for job-2210", jobs["job-2210"].GetProperty("prompt_snippet").GetString());
            Assert.Equal("job-2210", jobs["job-2211"].GetProperty("waiting_for").GetProperty("job_id").GetString());
            Assert.Equal("orphaned", jobs["job-2212"].GetProperty("state").GetString());
            Assert.False(jobs.ContainsKey("job-2213"));
            Assert.Contains("other_process", doc.RootElement.GetProperty("note").GetString());
        }

        [Fact]
        public async Task StatusResultAndCancel_ForAnotherProcessesJob_AnswerReadOnly_NotUnknownJob()
        {
            Process other = StartOtherProcess();
            string transcript = Path.Combine(_tasksDir, "job-2210-20261007-101500.log");
            File.WriteAllText(transcript, "[AGENTIC] Iteration 3/40 — tail line\n");
            WriteStateFile("job-2210", other.Id, other.StartTime.ToUniversalTime(), "running", transcript: transcript);
            WriteStateFile("job-2212", DeadPid, null, "queued");

            using var mgr = NewManager(LockPath("status"));
            var tools = new AgentTaskTools(mgr);

            using (var status = JsonDocument.Parse(await tools.TaskStatus("job-2210")))
            {
                JsonElement root = status.RootElement;
                Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
                Assert.Equal("running", root.GetProperty("state").GetString());
                Assert.True(root.GetProperty("other_process").GetBoolean());
                Assert.Equal(other.Id, root.GetProperty("owner_pid").GetInt32());
                Assert.Contains("tail line", root.GetProperty("transcript_tail").GetString());
            }

            using (var orphan = JsonDocument.Parse(await tools.TaskStatus("job-2212")))
                Assert.Equal("orphaned", orphan.RootElement.GetProperty("state").GetString());

            using (var result = JsonDocument.Parse(await tools.TaskResult("job-2210")))
                Assert.Contains("still running in another DevMind server process", result.RootElement.GetProperty("error").GetString());

            using (var cancel = JsonDocument.Parse(await tools.TaskCancel("job-2210")))
                Assert.Contains("belongs to another DevMind server process", cancel.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task OwnJobs_WriteAStateFile_ThroughTheirLifetime()
        {
            using var server = TaskDoneServer(Task.CompletedTask);
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = NewManager(LockPath("own"));

            var job = StartJob(mgr, "line one\nline two");
            await WaitForTerminal(job);
            // Written in the job's finally block, just after the in-memory state is published.
            await WaitUntil(() => JobStateFiles.Read(job.Id)?.State == "done", 5000);

            JobStateRecord? state = JobStateFiles.Read(job.Id);
            Assert.NotNull(state);
            Assert.Equal("done", state!.State);
            Assert.Equal(Environment.ProcessId, state.Pid);
            Assert.Equal("line one line two", state.PromptSnippet);
            Assert.Equal(_workDir, state.WorkingDir);
            Assert.NotNull(state.StartedUtc);
            Assert.NotNull(state.EndedUtc);
            Assert.Equal(job.TranscriptPath, state.TranscriptPath);
            Assert.False(state.IsOtherProcess);
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 30000)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(sw.ElapsedMilliseconds < timeoutMs, "condition not reached in time");
                await Task.Delay(10);
            }
        }

        private static Task WaitForTerminal(AgentJob job) =>
            WaitUntil(() => job.State is not (AgentJobState.Queued or AgentJobState.Running));
    }
}
