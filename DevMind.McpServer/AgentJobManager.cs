// File: AgentJobManager.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Background job engine for the devmind_task_* MCP tools: Claude Code (or any MCP
// client) delegates whole coding tasks; each job runs HeadlessAgent on the local
// model. Jobs execute STRICTLY ONE AT A TIME — the single llama-server GPU
// serializes anyway, and a queue beats KV-cache thrash. This queue is deliberately
// separate from McpServices' tool-dispatch channel: a task runs for minutes, and
// parking it on the shared dispatcher would block every other tool call.
//
// Diagnostic policy: stdout belongs to the MCP JSON-RPC transport — any diagnostics
// here go to Console.Error only, and HeadlessAgent itself never touches Console.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DevMind.McpServer
{
    internal enum AgentJobState { Queued, Running, Done, Failed, Cancelled }

    internal sealed class AgentJob
    {
        public required string Id { get; init; }
        public required string Prompt { get; init; }
        public required string WorkingDirectory { get; init; }
        public int MaxDepth { get; init; }
        public int TimeoutMinutes { get; init; }
        public bool AllowCommit { get; init; }
        public bool VerifyBuild { get; init; }
        /// <summary>Run `dotnet test` after a successful build verification and attach
        /// test_verification to the result (default false — tests can be slow).</summary>
        public bool VerifyTests { get; init; }
        /// <summary>Enable model reasoning (think blocks) for this task. Default false:
        /// briefed mechanical tasks iterate faster without unbounded thinking.</summary>
        public bool Think { get; init; }

        public AgentJobState State;
        public HeadlessAgentResult? Result;
        public string? Error;

        /// <summary>Post-run build verification outcome (null when skipped: no file
        /// changes, no resolvable build command, or verify_build false).</summary>
        public BuildVerification? Build;

        /// <summary>Post-run test verification outcome (null when skipped: verify_tests
        /// false, no file changes, or the build verification failed).</summary>
        public BuildVerification? Tests;

        /// <summary>
        /// True when the job technically finished but its work is NOT trustworthy as-is:
        /// it hit the iteration cap mid-task, or the post-run build/test verification
        /// failed. Surfaced as state "stopped_incomplete" — field lesson: "done" read as
        /// done, and broken-build results were built upon.
        /// </summary>
        public bool IsIncomplete =>
            State == AgentJobState.Done
            && ((Result?.HitDepthCap ?? false)
                || Build is { Succeeded: false }
                || Tests is { Succeeded: false });

        /// <summary>Why the job is incomplete (empty when it isn't).</summary>
        public string[] IncompleteReasons()
        {
            if (State != AgentJobState.Done) return Array.Empty<string>();
            var reasons = new List<string>(2);
            if (Result?.HitDepthCap ?? false) reasons.Add("hit_depth_cap");
            if (Build is { Succeeded: false }) reasons.Add("build_verification_failed");
            if (Tests is { Succeeded: false }) reasons.Add("test_verification_failed");
            return reasons.ToArray();
        }

        /// <summary>The live conversation, retained after completion so
        /// devmind_task_continue can resume it. Transferred to the continuation job
        /// (this becomes null); disposed on idle expiry, eviction, or shutdown.</summary>
        public HeadlessSession? Session;

        /// <summary>Set when this job is a continuation of an earlier one.</summary>
        public string? ParentJobId { get; init; }

        /// <summary>Set when a later job continued this one (its session moved there).</summary>
        public string? ContinuedByJobId;
        public DateTime QueuedAtUtc = DateTime.UtcNow;
        public DateTime? StartedAtUtc;
        public DateTime? EndedAtUtc;
        public readonly CancellationTokenSource Cts = new CancellationTokenSource();

        // Rolling tail of the live transcript for devmind_task_status. Bounded so a
        // chatty model can't grow server memory; the FULL transcript goes to a file.
        private const int TailCapChars = 4_000;
        private readonly StringBuilder _tail = new StringBuilder();
        private readonly object _tailLock = new object();

        public void AppendTail(string chunk)
        {
            lock (_tailLock)
            {
                _tail.Append(chunk);
                if (_tail.Length > TailCapChars * 2)
                    _tail.Remove(0, _tail.Length - TailCapChars);
            }
        }

        public string GetTail()
        {
            lock (_tailLock)
            {
                string s = _tail.ToString();
                return s.Length <= TailCapChars ? s : s.Substring(s.Length - TailCapChars);
            }
        }
    }

    /// <summary>Outcome of the job runner's own post-agent build check.</summary>
    internal sealed class BuildVerification
    {
        public required string Command { get; init; }
        public int ExitCode { get; init; }
        /// <summary>Last ~2 KB of build output — enough for the error summary.</summary>
        public required string OutputTail { get; init; }
        public bool Succeeded => ExitCode == 0;
    }

    /// <summary>One-at-a-time headless-agent job queue with bounded result retention.</summary>
    internal sealed class AgentJobManager : IDisposable
    {
        /// <summary>Completed jobs retained for devmind_task_result (oldest evicted past this).</summary>
        private const int RetainedJobs = 20;

        private readonly Dictionary<string, AgentJob> _jobs = new Dictionary<string, AgentJob>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _jobOrder = new List<string>(); // insertion order, for eviction + queue position
        private readonly object _lock = new object();
        private int _nextId;

        private readonly Channel<AgentJob> _queue =
            Channel.CreateUnbounded<AgentJob>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly Task _workerTask;
        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();

        public string EndpointUrl { get; }
        public string ApiKey { get; }

        // Invoked after every job finishes (any outcome). Wired by Program.cs to
        // invalidate McpServices' session file cache: a task agent may have rewritten
        // any file, and stale cached content produced false reads/greps in the field.
        private readonly Action _onJobFinished;

        public AgentJobManager(Action? onJobFinished = null)
        {
            _onJobFinished = onJobFinished ?? (() => { });
            // DEVMIND_ENDPOINT / DEVMIND_API_KEY is the established env convention
            // (see LlmClient); default is the local llama-server.
            string? endpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT")?.Trim();
            EndpointUrl = string.IsNullOrEmpty(endpoint) ? "http://127.0.0.1:8080/v1" : endpoint;
            ApiKey = Environment.GetEnvironmentVariable("DEVMIND_API_KEY") ?? "";

            _nextId = LoadPersistedIdCounter();
            _workerTask = Task.Run(WorkerLoopAsync);
        }

        // ── Persistent job identity ──────────────────────────────────────────
        // Job ids used to reset to job-1 on every server restart, so a stale id from
        // an earlier process silently matched a NEW job's transcript. The counter is
        // persisted so ids stay unique across restarts (sessions still die with the
        // process — only identity and on-disk artifacts survive).

        private static string IdCounterPath => Path.Combine(TranscriptDir, "_jobcounter.txt");

        private static int LoadPersistedIdCounter()
        {
            try
            {
                if (File.Exists(IdCounterPath)
                    && int.TryParse(File.ReadAllText(IdCounterPath).Trim(), out int persisted)
                    && persisted > 0)
                {
                    return persisted;
                }
            }
            catch { /* best effort — worst case ids restart at 1 as before */ }
            return 0;
        }

        private string NextJobId()
        {
            int n = Interlocked.Increment(ref _nextId);
            try
            {
                Directory.CreateDirectory(TranscriptDir);
                File.WriteAllText(IdCounterPath, n.ToString());
            }
            catch { /* best effort */ }
            return $"job-{n}";
        }

        public AgentJob Start(string prompt, string workingDirectory, int maxDepth, int timeoutMinutes,
            bool allowCommit, bool verifyBuild, bool think = false, bool verifyTests = false)
        {
            var job = new AgentJob
            {
                Id = NextJobId(),
                Prompt = prompt,
                WorkingDirectory = workingDirectory,
                MaxDepth = maxDepth,
                TimeoutMinutes = timeoutMinutes,
                AllowCommit = allowCommit,
                VerifyBuild = verifyBuild,
                Think = think,
                VerifyTests = verifyTests,
                State = AgentJobState.Queued,
            };

            Register(job);
            _queue.Writer.TryWrite(job);
            return job;
        }

        /// <summary>
        /// Resumes a finished job's conversation as a new job: the session (LlmClient
        /// history, caches, scratchpad) transfers to the continuation, so a bare
        /// "continue" picks up exactly where the parent stopped. Returns null with a
        /// user-presentable error when the parent cannot be continued.
        /// </summary>
        public AgentJob? Continue(string parentJobId, string prompt, int maxDepth, int timeoutMinutes,
            bool verifyBuild, out string error, bool verifyTests = false)
        {
            error = null!;
            AgentJob parent;
            HeadlessSession session;

            lock (_lock)
            {
                if (!_jobs.TryGetValue(parentJobId, out parent!))
                {
                    error = $"Unknown job_id: {parentJobId}.";
                    return null;
                }
                if (parent.State is AgentJobState.Queued or AgentJobState.Running)
                {
                    error = $"Job {parentJobId} is still {parent.State.ToString().ToLowerInvariant()} — wait for it to finish before continuing.";
                    return null;
                }
                if (parent.ContinuedByJobId != null)
                {
                    error = $"Job {parentJobId} was already continued by {parent.ContinuedByJobId} — continue THAT job instead (a conversation is a chain; always continue its newest link).";
                    return null;
                }
                if (parent.Session == null)
                {
                    error = $"Job {parentJobId}'s conversation is no longer available (expired, evicted, or the job predates continuation support). Start a fresh task with a continuation brief instead.";
                    return null;
                }

                // Transfer session ownership to the continuation.
                session = parent.Session;
                parent.Session = null;
            }

            var job = new AgentJob
            {
                Id = NextJobId(),
                Prompt = prompt,
                WorkingDirectory = parent.WorkingDirectory,
                MaxDepth = maxDepth,
                TimeoutMinutes = timeoutMinutes,
                AllowCommit = parent.AllowCommit,
                VerifyBuild = verifyBuild,
                VerifyTests = verifyTests,
                Think = parent.Think, // continuation inherits the parent's reasoning mode
                State = AgentJobState.Queued,
                ParentJobId = parentJobId,
                Session = session,
            };
            parent.ContinuedByJobId = job.Id;

            Register(job);
            _queue.Writer.TryWrite(job);
            return job;
        }

        /// <summary>Session idle expiry: a conversation untouched this long is disposed
        /// (the KV of a dead conversation is pure memory cost).</summary>
        private static readonly TimeSpan SessionIdleExpiry = TimeSpan.FromMinutes(60);

        private void Register(AgentJob job)
        {
            lock (_lock)
            {
                _jobs[job.Id] = job;
                _jobOrder.Add(job.Id);

                // Expire idle sessions (best-effort, piggybacked on job creation).
                foreach (var j in _jobs.Values)
                {
                    if (j.Session != null
                        && j.State is AgentJobState.Done or AgentJobState.Failed or AgentJobState.Cancelled
                        && DateTime.UtcNow - j.Session.LastActivityUtc > SessionIdleExpiry)
                    {
                        try { j.Session.Dispose(); } catch { }
                        j.Session = null;
                    }
                }

                // Evict oldest FINISHED jobs past the retention cap (never evict live ones).
                while (_jobOrder.Count > RetainedJobs)
                {
                    string? evictId = _jobOrder.FirstOrDefault(id =>
                        _jobs[id].State is AgentJobState.Done or AgentJobState.Failed or AgentJobState.Cancelled);
                    if (evictId == null) break;
                    try { _jobs[evictId].Session?.Dispose(); } catch { }
                    _jobOrder.Remove(evictId);
                    _jobs.Remove(evictId);
                }
            }
        }

        public AgentJob? Get(string jobId)
        {
            lock (_lock) return _jobs.TryGetValue(jobId, out var job) ? job : null;
        }

        /// <summary>All known jobs, newest first. Snapshot — safe to enumerate.</summary>
        public List<AgentJob> List()
        {
            lock (_lock)
            {
                var snapshot = new List<AgentJob>(_jobOrder.Count);
                for (int i = _jobOrder.Count - 1; i >= 0; i--)
                    snapshot.Add(_jobs[_jobOrder[i]]);
                return snapshot;
            }
        }

        /// <summary>Where job transcripts are written. Transcripts OUTLIVE the server
        /// process — the disk-fallback in devmind_task_result depends on this path.</summary>
        public static string TranscriptDir =>
            Path.Combine(Path.GetTempPath(), "devmind", "tasks");

        /// <summary>0 when running/next up; N when N jobs are ahead of it in the queue.</summary>
        public int QueuePosition(AgentJob job)
        {
            lock (_lock)
            {
                return _jobOrder
                    .Select(id => _jobs[id])
                    .Where(j => j.State == AgentJobState.Queued)
                    .TakeWhile(j => !ReferenceEquals(j, job))
                    .Count();
            }
        }

        public bool Cancel(string jobId)
        {
            var job = Get(jobId);
            if (job == null) return false;
            if (job.State is AgentJobState.Done or AgentJobState.Failed or AgentJobState.Cancelled)
                return false;
            job.Cts.Cancel();
            // A queued (not yet started) job is finalized here; a running one is
            // finalized by the worker when RunAsync observes the cancellation.
            lock (_lock)
            {
                if (job.State == AgentJobState.Queued)
                {
                    job.State = AgentJobState.Cancelled;
                    job.EndedAtUtc = DateTime.UtcNow;
                }
            }
            return true;
        }

        private async Task WorkerLoopAsync()
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(_shutdownCts.Token).ConfigureAwait(false))
            {
                if (job.Cts.IsCancellationRequested)
                    continue; // cancelled while queued — already finalized by Cancel()

                job.State = AgentJobState.Running;
                job.StartedAtUtc = DateTime.UtcNow;
                job.Cts.CancelAfter(TimeSpan.FromMinutes(job.TimeoutMinutes));

                string transcriptPath = Path.Combine(
                    TranscriptDir,
                    $"{job.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

                WriteActiveMarker(job, transcriptPath);

                try
                {
                    // Fresh task → new session; continuation → the parent's session
                    // (conversation intact). Sessions are RETAINED on the job after the
                    // turn so devmind_task_continue can resume them.
                    HeadlessSession? session = job.Session;
                    if (session == null)
                    {
                        var options = new HeadlessOptions
                        {
                            AgenticLoopMaxDepth = job.MaxDepth,
                            // ShowLlmThinking doubles as the enable_thinking template switch
                            // (see LlmClient.BuildRequestJson) — false = the model does not
                            // generate think blocks at all for this session.
                            ShowLlmThinking = job.Think,
                        };
                        session = new HeadlessSession(options, EndpointUrl, ApiKey,
                            job.WorkingDirectory, buildCommand: null, allowCommit: job.AllowCommit);
                        job.Session = session;
                    }
                    else
                    {
                        session.SetMaxDepth(job.MaxDepth);
                    }

                    var result = await session.RunTurnAsync(
                        job.Prompt,
                        transcriptPath: transcriptPath,
                        progress: job.AppendTail,
                        ct: job.Cts.Token).ConfigureAwait(false);

                    job.Result = result;
                    job.State = result.Cancelled ? AgentJobState.Cancelled
                        : result.Error != null ? AgentJobState.Failed
                        : AgentJobState.Done;
                    job.Error = result.Error;

                    // Post-agent build verification: the job runner checks the build so
                    // agents don't burn iterations fighting shell timeouts/PATH to do it
                    // themselves (two live delegations lost most of their depth to this).
                    // Only when the agent actually changed files, and never on cancel.
                    if (job.VerifyBuild && job.State == AgentJobState.Done && HasFileChanges(result))
                        job.Build = await VerifyBuildAsync(job).ConfigureAwait(false);

                    // Test verification (opt-in): only when the build verification did
                    // not already fail — red tests on a broken build are noise.
                    if (job.VerifyTests && job.State == AgentJobState.Done && HasFileChanges(result)
                        && job.Build is not { Succeeded: false })
                    {
                        job.Tests = await VerifyTestsAsync(job).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    // RunAsync reports task-level failures in the result; reaching here
                    // means something unexpected — never let it kill the worker loop.
                    job.State = AgentJobState.Failed;
                    job.Error = ex.Message;
                    Console.Error.WriteLine($"[AgentJobManager] {job.Id} crashed: {ex}");
                }
                finally
                {
                    job.EndedAtUtc = DateTime.UtcNow;
                    ClearActiveMarker();
                    WriteResultSidecar(job);
                    try { _onJobFinished(); } catch { /* never kill the worker */ }
                }
            }
        }

        // ── Active-job marker ────────────────────────────────────────────────
        // %TEMP%\devmind\tasks\_active.json exists exactly while a job is
        // executing — a positive "DM is busy" signal for external tooling
        // (dm-watch, deploy scripts). Transcript silence and CPU load both lie
        // (think blocks are transcript-silent; generation is GPU-bound): a
        // deploy killed a live job on those heuristics. Check the marker, and
        // verify its pid is alive before trusting a leftover after a crash.

        private static string ActiveMarkerPath => Path.Combine(TranscriptDir, "_active.json");

        private static void WriteActiveMarker(AgentJob job, string transcriptPath)
        {
            try
            {
                Directory.CreateDirectory(TranscriptDir);
                File.WriteAllText(ActiveMarkerPath, System.Text.Json.JsonSerializer.Serialize(new
                {
                    job_id = job.Id,
                    state = "running",
                    pid = Environment.ProcessId,
                    working_dir = job.WorkingDirectory,
                    started_at_utc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    transcript = transcriptPath,
                }));
            }
            catch { /* marker is best-effort — never fail the job over it */ }
        }

        private static void ClearActiveMarker()
        {
            try { File.Delete(ActiveMarkerPath); } catch { }
        }

        private static bool HasFileChanges(HeadlessAgentResult result)
            => result.Actions.Any(a =>
                a.Kind is "save" or "append" or "patch" or "delete" or "rename");

        /// <summary>Runs `dotnet test` in the working directory (opt-in via verify_tests).
        /// Never throws — a verification failure is data.</summary>
        private static async Task<BuildVerification?> VerifyTestsAsync(AgentJob job)
        {
            const int TestTimeoutSeconds = 900;
            const int TailChars = 2_000;
            const string command = "dotnet test";

            try
            {
                var runner = new ShellRunner(job.WorkingDirectory);
                var (output, exitCode) = await runner.ExecuteAsync(
                    command, CancellationToken.None, TestTimeoutSeconds).ConfigureAwait(false);
                string tail = output.Length <= TailChars ? output : output.Substring(output.Length - TailChars);
                return new BuildVerification { Command = command, ExitCode = exitCode, OutputTail = tail };
            }
            catch (Exception ex)
            {
                return new BuildVerification
                {
                    Command = command,
                    ExitCode = -1,
                    OutputTail = $"test verification crashed: {ex.Message}",
                };
            }
        }

        /// <summary>
        /// Persists a finished job's outcome next to its transcript
        /// ({TranscriptDir}\{id}.result.json) so devmind_task_result can serve REAL
        /// results — answer, actions, verification — after a server restart, not just
        /// a transcript tail. Best effort; ids are unique across restarts (see
        /// NextJobId), so a sidecar is never ambiguous.
        /// </summary>
        private static void WriteResultSidecar(AgentJob job)
        {
            try
            {
                Directory.CreateDirectory(TranscriptDir);
                var r = job.Result;
                string json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    job_id = job.Id,
                    state = job.IsIncomplete ? "stopped_incomplete" : job.State.ToString().ToLowerInvariant(),
                    incomplete_reasons = job.IncompleteReasons(),
                    answer = r?.Answer ?? "",
                    actions = (r?.Actions ?? Array.Empty<HostAction>())
                        .Select(a => new { kind = a.Kind, detail = a.Detail, success = a.Success }),
                    iterations = r?.Iterations ?? 0,
                    elapsed_seconds = r?.ElapsedSeconds ?? 0,
                    hit_depth_cap = r?.HitDepthCap ?? false,
                    error = job.Error,
                    transcript_path = r?.TranscriptPath,
                    parent_job_id = job.ParentJobId,
                    working_dir = job.WorkingDirectory,
                    ended_at_utc = (job.EndedAtUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm:ss"),
                    build_verification = job.Build == null ? null : new
                    {
                        command = job.Build.Command,
                        succeeded = job.Build.Succeeded,
                        exit_code = job.Build.ExitCode,
                        output_tail = job.Build.OutputTail,
                    },
                    test_verification = job.Tests == null ? null : new
                    {
                        command = job.Tests.Command,
                        succeeded = job.Tests.Succeeded,
                        exit_code = job.Tests.ExitCode,
                        output_tail = job.Tests.OutputTail,
                    },
                });
                File.WriteAllText(Path.Combine(TranscriptDir, $"{job.Id}.result.json"), json);
            }
            catch { /* sidecar is best-effort — never fail the job over it */ }
        }

        /// <summary>Runs the working directory's resolved build command with a
        /// build-sized timeout. Never throws — a verification failure is data.</summary>
        private static async Task<BuildVerification?> VerifyBuildAsync(AgentJob job)
        {
            const int BuildTimeoutSeconds = 600;
            const int TailChars = 2_000;

            string command;
            try
            {
                command = BuildCommandResolver.Resolve(job.WorkingDirectory, _ => { });
            }
            catch
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(command))
                return null;

            try
            {
                var runner = new ShellRunner(job.WorkingDirectory);
                var (output, exitCode) = await runner.ExecuteAsync(
                    command, CancellationToken.None, BuildTimeoutSeconds).ConfigureAwait(false);
                string tail = output.Length <= TailChars ? output : output.Substring(output.Length - TailChars);
                return new BuildVerification { Command = command, ExitCode = exitCode, OutputTail = tail };
            }
            catch (Exception ex)
            {
                return new BuildVerification
                {
                    Command = command,
                    ExitCode = -1,
                    OutputTail = $"build verification crashed: {ex.Message}",
                };
            }
        }

        public void Dispose()
        {
            _queue.Writer.TryComplete();
            _shutdownCts.Cancel();
            lock (_lock)
            {
                foreach (var job in _jobs.Values)
                {
                    job.Cts.Cancel();
                    try { job.Session?.Dispose(); } catch { }
                    job.Session = null;
                }
            }
            try { _workerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            ClearActiveMarker(); // graceful shutdown — don't leave a stale busy signal
            _shutdownCts.Dispose();
        }
    }
}
