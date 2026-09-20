// File: AgentTaskTools.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The devmind_task_* MCP tools: delegate whole coding tasks to DevMind's headless
// agent on the local model (see DevMind_HeadlessAgent_MCP_DesignSpec.md). Job
// pattern (start/status/result/cancel) because an agentic turn runs minutes —
// far past MCP tool-call timeouts. These tools deliberately BYPASS McpServices'
// FIFO dispatcher: they only touch the AgentJobManager (own locking) and must
// stay responsive while a task runs.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace DevMind.McpServer
{
    [McpServerToolType]
    internal sealed class AgentTaskTools
    {
        private readonly AgentJobManager _jobs;

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            // JavaScriptEncoder.Default escapes the HTML-sensitive set (+ > < ' ` &) as \uXXXX.
            // These payloads carry unified diffs, code and shell output to an MCP client, never
            // HTML, and escaped '+' markers make diffs in transcript_tail unreadable. The relaxed
            // encoder still escapes everything JSON requires (quotes, backslash, control chars).
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public AgentTaskTools(AgentJobManager jobs) => _jobs = jobs;

        [McpServerTool(Name = "devmind_task_start")]
        [Description(
            "Delegate a whole coding task to DevMind's local agent (runs on a local GPU model at zero " +
            "API cost). The agent works autonomously inside working_dir: it reads files, edits, runs " +
            "shell/build/test commands, and iterates until done. Returns a job_id immediately — poll " +
            "devmind_task_status, then fetch devmind_task_result. Jobs run one at a time. Write the " +
            "prompt like a task brief for a junior developer: include the goal, relevant file paths, " +
            "and how to verify success. Do not edit files under working_dir while the job runs. " +
            "Estimate max_depth to the task size (verify ~25, single-file feature ~40, cross-cutting " +
            "~60; default 40) — hitting the cap is cheap to recover: devmind_task_continue resumes " +
            "the conversation where it stopped, and the result reports state stopped_incomplete " +
            "when the cap or a failed build verification means the work is not trustworthy as-is.")]
        public async Task<string> TaskStart(
            [Description("The task brief: goal, relevant files, constraints, and how to verify success.")] string prompt,
            [Description("Absolute path of the directory the agent operates in (its sandbox).")] string working_dir,
            [Description("Max agentic iterations before the agent must stop (default 40).")] int? max_depth = null,
            [Description("Wall-clock kill timeout in minutes (default 30).")] int? timeout_minutes = null,
            [Description("Allow the agent to run git commit (default false — the caller owns version control).")] bool? allow_commit = null,
            [Description("After the agent finishes, the job runner builds the working_dir itself and attaches build_verification to the result (default true).")] bool? verify_build = null,
            [Description("After a successful build verification, also run `dotnet test` in working_dir and attach test_verification (default false — tests can be slow). test_verification carries harness-measured test counts (baseline_total, total, delta) parsed from the runs the harness itself captured.")] bool? verify_tests = null,
            [Description("How the harness obtains the baseline test count for the structural delta (default \"before-run\": it runs the suite ONCE before the agent starts, so the result reports a real before->after delta the agent cannot misreport. \"off\": skip the before-run — for slow suites — and report only the after total. Only takes effect when verify_tests is on.")] string? test_baseline = null,
            [Description("Enable model reasoning (think blocks) for this task (default false). Leave off for briefed mechanical tasks — thinking runs UNBOUNDED on the local server and can add minutes per iteration. Turn on only for genuinely hard design/debugging tasks. Continuations inherit this setting.")] bool? think = null,
            [Description("Restrict the agent to no execution (default false): it may still build (dotnet build / run_build) for compile verification, but running executables, `dotnet run`/`dotnet exec`, the test suite (run_tests / dotnet test), and debug launch/attach are blocked at the harness. This is NOT a sandbox — it blocks a named set of execution invocations, not every conceivable way to start a process; use it to stop an agent from launching (or re-launching) something that hangs or spawns runaway children, not as a security boundary. Continuations inherit this setting.")] bool? no_execute = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return Err("prompt is required.");
            if (string.IsNullOrWhiteSpace(working_dir) || !Path.IsPathRooted(working_dir))
                return Err("working_dir must be an absolute path.");
            if (!Directory.Exists(working_dir))
                return Err($"working_dir does not exist: {working_dir}");
            string baseline = string.IsNullOrWhiteSpace(test_baseline) ? "before-run" : test_baseline;
            if (baseline != "before-run" && baseline != "off")
                return Err($"test_baseline must be \"before-run\" or \"off\" (got \"{test_baseline}\").");

            // Fail fast when the model server is down — better than a queued job that
            // dies minutes later.
            string? health = await ProbeModelServerAsync(cancellationToken).ConfigureAwait(false);
            if (health != null)
                return Err(health);

            var job = _jobs.Start(
                prompt, working_dir,
                maxDepth: Math.Clamp(max_depth ?? 40, 1, 100),
                timeoutMinutes: Math.Clamp(timeout_minutes ?? 30, 1, 240),
                allowCommit: allow_commit ?? false,
                verifyBuild: verify_build ?? true,
                think: think ?? false,
                verifyTests: verify_tests ?? false,
                noExecute: no_execute ?? false,
                runTestBaseline: baseline != "off");

            return JsonSerializer.Serialize(new
            {
                job_id = job.Id,
                state = "queued",
                queue_position = _jobs.QueuePosition(job),
                endpoint = _jobs.EndpointUrl,
                hint = "Poll devmind_task_status with this job_id; fetch devmind_task_result when done.",
            }, JsonOpts);
        }

        [McpServerTool(Name = "devmind_task_continue")]
        [Description(
            "Resume a FINISHED DevMind task's conversation — the agent keeps its full context " +
            "(everything it read, did, and concluded), so a bare 'continue' picks up exactly " +
            "where it left off. Use this when a task hit its iteration cap, or to send follow-up " +
            "instructions ('now also fix the failing test'). Returns a NEW job_id to poll. " +
            "A conversation is a chain: always continue its NEWEST job_id. Conversations expire " +
            "after ~60 minutes idle — after that, start a fresh task with a continuation brief.")]
        public async Task<string> TaskContinue(
            [Description("The job_id of the finished task to resume (the newest in its chain).")] string job_id,
            [Description("Instruction for the resumed agent. Default: 'Continue the task from where you left off.'")] string? prompt = null,
            [Description("Max agentic iterations for this continuation (default 40).")] int? max_depth = null,
            [Description("Wall-clock kill timeout in minutes (default 30).")] int? timeout_minutes = null,
            [Description("Run build verification after this turn (default true). Note: post-turn build verification by the job runner is independent of no_execute and unaffected by it.")] bool? verify_build = null,
            [Description("After a successful build verification, also run `dotnet test` and attach test_verification (default false). test_verification carries harness-measured test counts (baseline_total, total, delta).")]
            bool? verify_tests = null,
            [Description("Baseline mode for the structural test delta (default \"before-run\": the harness runs the suite ONCE before this continuation's agent starts. \"off\": skip the before-run and report only the after total. Only takes effect when verify_tests is on.")] string? test_baseline = null,
            [Description("Restrict this continuation to no execution (default: inherit the parent task's setting). When inherited or set, running executables, the test suite, and debug launch/attach are blocked at the harness; builds stay allowed. Cannot be used to relax a parent's restriction — start a fresh task for that.")] bool? no_execute = null,
            CancellationToken cancellationToken = default)
        {
            string baseline = string.IsNullOrWhiteSpace(test_baseline) ? "before-run" : test_baseline;
            if (baseline != "before-run" && baseline != "off")
                return Err($"test_baseline must be \"before-run\" or \"off\" (got \"{test_baseline}\").");

            string? health = await ProbeModelServerAsync(cancellationToken).ConfigureAwait(false);
            if (health != null)
                return Err(health);

            var job = _jobs.Continue(
                job_id,
                string.IsNullOrWhiteSpace(prompt) ? "Continue the task from where you left off." : prompt,
                maxDepth: Math.Clamp(max_depth ?? 40, 1, 100),
                timeoutMinutes: Math.Clamp(timeout_minutes ?? 30, 1, 240),
                verifyBuild: verify_build ?? true,
                out string error,
                verifyTests: verify_tests ?? false,
                noExecute: no_execute,
                runTestBaseline: baseline != "off");

            if (job == null)
                return Err(error);

            return JsonSerializer.Serialize(new
            {
                job_id = job.Id,
                parent_job_id = job_id,
                state = "queued",
                queue_position = _jobs.QueuePosition(job),
                hint = "Poll devmind_task_status with the NEW job_id; the conversation context carried over.",
            }, JsonOpts);
        }

        [McpServerTool(Name = "devmind_task_status")]
        [Description(
            "Check a delegated DevMind task: state (queued/running/done/needs_input/stopped_incomplete/" +
            "failed/cancelled), elapsed time, queue position, and the live tail of the agent's transcript. " +
            "stopped_incomplete means the job finished but its work is NOT trustworthy as-is (hit " +
            "the iteration cap mid-task, thrashed on a repeating failure, or build/test verification " +
            "failed) — check incomplete_reasons and usually devmind_task_continue it. needs_input means " +
            "the agent paused with specific questions (in the result answer) — answer them via " +
            "devmind_task_continue. Optional wait_seconds: when > 0, blocks until the job's state " +
            "CHANGES (e.g. running -> done/needs_input/failed) or that many seconds elapse, then " +
            "returns the same payload — a waiter gets the fresh state in one call instead of " +
            "re-polling. Omitted or 0 returns immediately (the default, unchanged); values above " +
            "60 are clamped to 60, not rejected. Use it in place of your own sleep-and-retry loop.")]
        public async Task<string> TaskStatus(
            [Description("The job_id returned by devmind_task_start.")] string job_id,
            [Description("Optional: seconds to wait for the job's state to change before returning " +
                        "(returns the moment it does; clamped to 60). Omitted or 0 = return " +
                        "immediately, as before.")] int? wait_seconds = null,
            CancellationToken cancellationToken = default)
        {
            var job = _jobs.Get(job_id);
            if (job != null)
            {
                // Optional blocking wait: poll ~1s (never busy-wait) and return the
                // moment the DISPLAY state changes — queued -> running is worth
                // waking on, as is done-with-questions -> needs_input. The payload
                // below recomputes DisplayState(job), so it reflects the fresh state.
                _ = await AgentJobManager.WaitForStateChangeAsync(
                    () => DisplayState(job), wait_seconds ?? 0, cancellationToken).ConfigureAwait(false);
            }
            if (job == null)
            {
                var (diedMidRun, startedAt) = CheckStaleActiveMarker(job_id);
                if (diedMidRun)
                    return Err(
                        $"Job {job_id} was RUNNING when its server process died or was killed" +
                        $"{(startedAt != null ? $" (started {startedAt} UTC)" : "")}. Its conversation and " +
                        "result are gone; at most a partial transcript survives — devmind_task_result " +
                        "will serve it. Re-delegate with a fresh brief.");
                return Err(
                    $"Unknown job_id: {job_id} — not in this server process. Finished jobs persist a " +
                    "result sidecar + transcript on disk: try devmind_task_result, or devmind_task_list.");
            }

            double? elapsed = job.StartedAtUtc.HasValue
                ? Math.Round(((job.EndedAtUtc ?? DateTime.UtcNow) - job.StartedAtUtc.Value).TotalSeconds, 0)
                : null;

            return JsonSerializer.Serialize(new
            {
                job_id = job.Id,
                state = DisplayState(job),
                incomplete_reasons = job.IsIncomplete ? job.IncompleteReasons() : null,
                queue_position = job.State == AgentJobState.Queued ? _jobs.QueuePosition(job) : (int?)null,
                elapsed_seconds = elapsed,
                working_dir = job.WorkingDirectory,
                error = job.Error,
                transcript_tail = job.GetTail(),
            }, JsonOpts);
        }

        /// <summary>"done" only when the work is actually trustworthy — a depth-capped or
        /// verification-failed job reports stopped_incomplete instead (field lesson: "done"
        /// with a broken build was built upon). A job paused on ask_caller reports
        /// needs_input: the agent has specific questions — answer them via
        /// devmind_task_continue (the conversation keeps full context).</summary>
        private static string DisplayState(AgentJob job)
            => (job.Result?.NeedsInput ?? false) && job.State == AgentJobState.Done
                ? "needs_input"
                : job.IsIncomplete ? "stopped_incomplete" : job.State.ToString().ToLowerInvariant();

        [McpServerTool(Name = "devmind_task_list")]
        [Description(
            "List DevMind delegated tasks: every job known to the running server (id, state, prompt " +
            "snippet, timings, whether its conversation can still be continued) plus recent transcript " +
            "files on disk, which survive server restarts. Call this FIRST in a new conversation to " +
            "rediscover job ids from earlier work.")]
        public Task<string> TaskList(CancellationToken cancellationToken = default)
        {
            var jobs = _jobs.List().Select(j => new
            {
                job_id = j.Id,
                state = DisplayState(j),
                prompt_snippet = Snippet(j.Prompt, 120),
                working_dir = j.WorkingDirectory,
                queued_at_utc = j.QueuedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                ended_at_utc = j.EndedAtUtc?.ToString("yyyy-MM-dd HH:mm:ss"),
                parent_job_id = j.ParentJobId,
                continued_by = j.ContinuedByJobId,
                can_continue = j.Session != null && j.ContinuedByJobId == null
                    && j.State is AgentJobState.Done or AgentJobState.Failed or AgentJobState.Cancelled,
            }).ToList();

            object[] transcripts;
            try
            {
                transcripts = new DirectoryInfo(AgentJobManager.TranscriptDir)
                    .GetFiles("job-*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(15)
                    .Select(f => (object)new
                    {
                        file = f.FullName,
                        written_utc = f.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                        size_bytes = f.Length,
                    })
                    .ToArray();
            }
            catch { transcripts = Array.Empty<object>(); }

            return Task.FromResult(JsonSerializer.Serialize(new
            {
                jobs,
                note = jobs.Count == 0
                    ? "No jobs in this server process (it may have restarted — live job state is in-memory). " +
                      "Job ids are unique across restarts; pass an old id to devmind_task_result to recover " +
                      "its persisted result sidecar, or read a transcript file below with read_file."
                    : "Job ids are unique across restarts. Finished jobs persist a result sidecar + transcript " +
                      "on disk; live conversations still die with the process.",
                recent_transcripts = transcripts,
            }, JsonOpts));
        }

        [McpServerTool(Name = "devmind_task_result")]
        [Description(
            "Fetch the result of a finished DevMind task: the agent's final answer, the action " +
            "journal (every file changed, shell command run, build/test outcome — review it like a " +
            "junior developer's PR), and the transcript file path.")]
        public Task<string> TaskResult(
            [Description("The job_id returned by devmind_task_start.")] string job_id,
            CancellationToken cancellationToken = default)
        {
            var job = _jobs.Get(job_id);
            if (job == null)
                return Task.FromResult(TranscriptFallback(job_id));

            if (job.State is AgentJobState.Queued or AgentJobState.Running)
                return Task.FromResult(Err($"Job {job_id} is still {job.State.ToString().ToLowerInvariant()} — poll devmind_task_status."));

            var r = job.Result;
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                job_id = job.Id,
                state = DisplayState(job),
                incomplete_reasons = job.IsIncomplete ? job.IncompleteReasons() : null,
                answer = r?.Answer ?? "",
                actions = (r?.Actions ?? Array.Empty<HostAction>())
                    .Select(a => new { kind = a.Kind, detail = a.Detail, success = a.Success }),
                iterations = r?.Iterations ?? 0,
                elapsed_seconds = r?.ElapsedSeconds ?? 0,
                hit_depth_cap = r?.HitDepthCap ?? false,
                error = job.Error,
                transcript_path = r?.TranscriptPath,
                parent_job_id = job.ParentJobId,
                continued_by = job.ContinuedByJobId,
                can_continue = job.Session != null && job.ContinuedByJobId == null,
                build_verification = job.Build == null ? null : new
                {
                    command = job.Build.Command,
                    succeeded = job.Build.Succeeded,
                    exit_code = job.Build.ExitCode,
                    // The post-run build is incremental: up-to-date projects are not recompiled and
                    // therefore do not re-emit their warnings, so any "N Warning(s)" in output_tail
                    // is NOT a verified count. Only the error/exit-code result (succeeded) is
                    // reliable. The field is present and false so the tail cannot read as a warning
                    // check that actually happened.
                    warning_count_verified = false,
                    output_tail = job.Build.OutputTail,
                },
                test_verification = TestVerificationPayload.Create(job),
            }, JsonOpts));
        }

        [McpServerTool(Name = "devmind_task_cancel")]
        [Description("Cancel a queued or running DevMind task.")]
        public Task<string> TaskCancel(
            [Description("The job_id returned by devmind_task_start.")] string job_id,
            CancellationToken cancellationToken = default)
        {
            var job = _jobs.Get(job_id);
            if (job == null)
                return Task.FromResult(Err($"Unknown job_id: {job_id}."));

            bool cancelled = _jobs.Cancel(job_id);
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                job_id,
                cancelled,
                state = _jobs.Get(job_id)?.State.ToString().ToLowerInvariant(),
                note = cancelled ? "Cancellation requested." : "Job already finished.",
            }, JsonOpts));
        }

        [McpServerTool(Name = "devmind_task_steer")]
        [Description(
            "Send a message into a DevMind task that is CURRENTLY RUNNING. The agent folds it into " +
            "its prompt at the next iteration boundary and acts on it without waiting for the job to " +
            "finish — use it to redirect an in-flight task. mode: \"suggest\" (default) reads as an " +
            "addition that does not interrupt the current approach; \"override\" reads as a " +
            "stop-and-change-course instruction. An override is refused on the task's last iteration " +
            "(no iterations left to act on it); the refusal is recorded in the action journal. For a " +
            "FINISHED or needs_input job use devmind_task_continue (it resumes the conversation); " +
            "steer only reaches a job that is still running. Returns whether this steer superseded an " +
            "earlier, still-un-consumed steer and that steer's mode.")]
        public Task<string> TaskSteer(
            [Description("The job_id of the RUNNING task to steer.")] string job_id,
            [Description("The instruction to fold into the running task at its next iteration boundary.")] string message,
            [Description("How to treat it: \"suggest\" (default — an addition that does not interrupt the current approach) or \"override\" (stop and change course). Not inferred from the message text.")] string? mode = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(message))
                return Task.FromResult(Err("message is required."));

            SteerMode? steerMode = ParseSteerMode(mode);
            if (steerMode is null)
                return Task.FromResult(Err($"mode must be \"suggest\" or \"override\" (got \"{mode ?? ""}\")."));

            var job = _jobs.Get(job_id);
            if (job == null)
                return Task.FromResult(Err(
                    $"Unknown job_id: {job_id} — not in this server process. For a finished job use " +
                    "devmind_task_result, or resume it with devmind_task_continue."));

            if (job.State != AgentJobState.Running)
            {
                string state = job.State.ToString().ToLowerInvariant();
                string rightTool = job.State == AgentJobState.Queued
                    ? "devmind_task_status (it has not started yet — steering works once it is running)"
                    : "devmind_task_continue (it is finished/failed/cancelled — resume it; it keeps full context)";
                return Task.FromResult(Err(
                    $"Job {job_id} is {state}, not running — devmind_task_steer only reaches a running job. Use {rightTool}."));
            }

            // Running, but the session is created by the worker just before the turn starts — a
            // Running job with no session yet is in the startup window, not yet steerable.
            if (job.Session is null)
                return Task.FromResult(Err(
                    $"Job {job_id} is starting up and not yet ready to accept a steer. Poll " +
                    "devmind_task_status and retry once it is fully running."));

            var (superseded, supersededMode) = job.Session.EnqueueSteer(message, steerMode.Value);

            return Task.FromResult(JsonSerializer.Serialize(new
            {
                job_id,
                accepted = true,
                mode = steerMode.Value.ToString().ToLowerInvariant(),
                superseded,
                superseded_mode = supersededMode?.ToString().ToLowerInvariant(),
                note = superseded
                    ? "Replaced an earlier, still-un-consumed steer (the earlier one was not yet folded in; its disposition is recorded in the action journal)."
                    : "Queued; it will be folded into the task at the next iteration boundary. Its disposition (steer / steer_rejected / steer_unconsumed) is recorded in the action journal — check devmind_task_result if the job ends before the next boundary.",
            }, JsonOpts));
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>Parses the explicit steer mode; null when the value is not suggest/override.
        /// Blank/omitted defaults to suggest.</summary>
        private static SteerMode? ParseSteerMode(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
                return SteerMode.Suggest;
            return mode.Trim().ToLowerInvariant() switch
            {
                "suggest"  => SteerMode.Suggest,
                "override" => SteerMode.Override,
                _ => null,
            };
        }

        /// <summary>Null when the model server answers; otherwise a user-presentable error.</summary>
        private async Task<string?> ProbeModelServerAsync(CancellationToken ct)
        {
            string root = _jobs.EndpointUrl.TrimEnd('/');
            string url = root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? root + "/models"
                : root + "/v1/models";
            try
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return null;
                return $"Model server at {_jobs.EndpointUrl} answered {(int)response.StatusCode} — is the right model loaded?";
            }
            catch (Exception ex)
            {
                return $"Model server unreachable at {_jobs.EndpointUrl} ({ex.Message}). " +
                       "Start it (e.g. llm-launchers\\start-qwen36-mtp-fast.bat) or set DEVMIND_ENDPOINT.";
            }
        }

        private static string Err(string message)
            => JsonSerializer.Serialize(new { error = message }, JsonOpts);

        /// <summary>
        /// Detects a job that died mid-run: the _active.json marker is written while a
        /// job executes and cleared on every graceful finish — so a marker naming this
        /// job whose pid is no longer alive means the server was killed or crashed
        /// WHILE the job was running. Turns "unknown job_id" into an honest answer.
        /// </summary>
        private static (bool DiedMidRun, string? StartedAtUtc) CheckStaleActiveMarker(string jobId)
        {
            try
            {
                string markerPath = Path.Combine(AgentJobManager.TranscriptDir, "_active.json");
                if (!File.Exists(markerPath))
                    return (false, null);

                using var doc = JsonDocument.Parse(File.ReadAllText(markerPath));
                var root = doc.RootElement;
                if (!root.TryGetProperty("job_id", out var idEl)
                    || !string.Equals(idEl.GetString(), jobId, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, null);
                }

                int pid = root.TryGetProperty("pid", out var pidEl) ? pidEl.GetInt32() : 0;
                bool alive = false;
                try
                {
                    alive = pid > 0 && !System.Diagnostics.Process.GetProcessById(pid).HasExited;
                }
                catch { /* GetProcessById throws when the pid is gone — that IS the signal */ }
                if (alive)
                    return (false, null); // another live server process is running it right now

                string? started = root.TryGetProperty("started_at_utc", out var s) ? s.GetString() : null;
                return (true, started);
            }
            catch
            {
                return (false, null);
            }
        }

        private static string Snippet(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
        }

        /// <summary>
        /// devmind_task_result fallback when the job is not in memory (server restarted
        /// or evicted past the retention cap). Preference order:
        /// 1. The {id}.result.json sidecar — the REAL persisted result (answer, actions,
        ///    verifications), written on every job finish; ids are unique across restarts.
        /// 2. The newest on-disk transcript's tail (legacy jobs that predate sidecars).
        /// </summary>
        private static string TranscriptFallback(string jobId)
        {
            const int TailChars = 4_000;

            // 1) Result sidecar — authoritative.
            try
            {
                string sidecarPath = Path.Combine(AgentJobManager.TranscriptDir, $"{jobId}.result.json");
                if (File.Exists(sidecarPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
                    return JsonSerializer.Serialize(new
                    {
                        recovered_from = "result sidecar (server restarted or job evicted — this is the persisted final result)",
                        can_continue = false,
                        note = "The conversation is NOT continuable across restarts — start a fresh task with a continuation brief.",
                        result = doc.RootElement.Clone(),
                    }, JsonOpts);
                }
            }
            catch { /* fall through to transcript tail */ }
            FileInfo[] matches;
            try
            {
                matches = new DirectoryInfo(AgentJobManager.TranscriptDir)
                    .GetFiles($"{jobId}-*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToArray();
            }
            catch { matches = Array.Empty<FileInfo>(); }

            var (diedMidRun, startedAtUtc) = CheckStaleActiveMarker(jobId);

            if (matches.Length == 0)
            {
                if (diedMidRun)
                    return Err($"Job {jobId} was RUNNING when its server process died or was killed" +
                               $"{(startedAtUtc != null ? $" (started {startedAtUtc} UTC)" : "")} — " +
                               "nothing was persisted (the result sidecar only writes on completion, and " +
                               "no transcript reached disk). Re-delegate with a fresh brief.");
                return Err($"Unknown job_id: {jobId} — not in this server process and no transcript " +
                           "found on disk. Use devmind_task_list to see what exists.");
            }

            string tail;
            try
            {
                string content = File.ReadAllText(matches[0].FullName);
                tail = content.Length <= TailChars ? content : content.Substring(content.Length - TailChars);
            }
            catch (Exception ex)
            {
                return Err($"Job {jobId} is not in this server process and its transcript could not be read: {ex.Message}");
            }

            return JsonSerializer.Serialize(new
            {
                job_id = jobId,
                state = diedMidRun
                    ? "died_mid_run (server process died or was killed while this job was running)"
                    : "unknown (not in this server process — recovered from disk transcript)",
                note = diedMidRun
                    ? "The transcript below is PARTIAL — no final answer, actions journal, or " +
                      "verification was persisted (the result sidecar only writes on completion). " +
                      "Re-delegate with a fresh brief."
                    : "No result sidecar exists for this id (job predates sidecar support); this is the " +
                      "NEWEST transcript matching the id. The tail below usually ends with the agent's " +
                      "final summary. The conversation is NOT continuable — start a fresh task with a " +
                      "continuation brief.",
                transcript_path = matches[0].FullName,
                transcript_written_utc = matches[0].LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                transcript_tail = tail,
                other_matching_transcripts = matches.Skip(1).Select(f => f.FullName),
            }, JsonOpts);
        }
    }
}
