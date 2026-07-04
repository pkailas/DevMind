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
            "~60) — hitting the cap is cheap to recover: devmind_task_continue resumes the " +
            "conversation where it stopped.")]
        public async Task<string> TaskStart(
            [Description("The task brief: goal, relevant files, constraints, and how to verify success.")] string prompt,
            [Description("Absolute path of the directory the agent operates in (its sandbox).")] string working_dir,
            [Description("Max agentic iterations before the agent must stop (default 25).")] int? max_depth = null,
            [Description("Wall-clock kill timeout in minutes (default 30).")] int? timeout_minutes = null,
            [Description("Allow the agent to run git commit (default false — the caller owns version control).")] bool? allow_commit = null,
            [Description("After the agent finishes, the job runner builds the working_dir itself and attaches build_verification to the result (default true).")] bool? verify_build = null,
            [Description("Enable model reasoning (think blocks) for this task (default false). Leave off for briefed mechanical tasks — thinking runs UNBOUNDED on the local server and can add minutes per iteration. Turn on only for genuinely hard design/debugging tasks. Continuations inherit this setting.")] bool? think = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return Err("prompt is required.");
            if (string.IsNullOrWhiteSpace(working_dir) || !Path.IsPathRooted(working_dir))
                return Err("working_dir must be an absolute path.");
            if (!Directory.Exists(working_dir))
                return Err($"working_dir does not exist: {working_dir}");

            // Fail fast when the model server is down — better than a queued job that
            // dies minutes later.
            string? health = await ProbeModelServerAsync(cancellationToken).ConfigureAwait(false);
            if (health != null)
                return Err(health);

            var job = _jobs.Start(
                prompt, working_dir,
                maxDepth: Math.Clamp(max_depth ?? 25, 1, 100),
                timeoutMinutes: Math.Clamp(timeout_minutes ?? 30, 1, 240),
                allowCommit: allow_commit ?? false,
                verifyBuild: verify_build ?? true,
                think: think ?? false);

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
            [Description("Max agentic iterations for this continuation (default 25).")] int? max_depth = null,
            [Description("Wall-clock kill timeout in minutes (default 30).")] int? timeout_minutes = null,
            [Description("Run build verification after this turn (default true).")] bool? verify_build = null,
            CancellationToken cancellationToken = default)
        {
            string? health = await ProbeModelServerAsync(cancellationToken).ConfigureAwait(false);
            if (health != null)
                return Err(health);

            var job = _jobs.Continue(
                job_id,
                string.IsNullOrWhiteSpace(prompt) ? "Continue the task from where you left off." : prompt,
                maxDepth: Math.Clamp(max_depth ?? 25, 1, 100),
                timeoutMinutes: Math.Clamp(timeout_minutes ?? 30, 1, 240),
                verifyBuild: verify_build ?? true,
                out string error);

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
            "Check a delegated DevMind task: state (queued/running/done/failed/cancelled), elapsed " +
            "time, queue position, and the live tail of the agent's transcript.")]
        public Task<string> TaskStatus(
            [Description("The job_id returned by devmind_task_start.")] string job_id,
            CancellationToken cancellationToken = default)
        {
            var job = _jobs.Get(job_id);
            if (job == null)
                return Task.FromResult(Err($"Unknown job_id: {job_id} (results are retained for the server's lifetime, max 20 jobs)."));

            double? elapsed = job.StartedAtUtc.HasValue
                ? Math.Round(((job.EndedAtUtc ?? DateTime.UtcNow) - job.StartedAtUtc.Value).TotalSeconds, 0)
                : null;

            return Task.FromResult(JsonSerializer.Serialize(new
            {
                job_id = job.Id,
                state = job.State.ToString().ToLowerInvariant(),
                queue_position = job.State == AgentJobState.Queued ? _jobs.QueuePosition(job) : (int?)null,
                elapsed_seconds = elapsed,
                working_dir = job.WorkingDirectory,
                error = job.Error,
                transcript_tail = job.GetTail(),
            }, JsonOpts));
        }

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
                state = j.State.ToString().ToLowerInvariant(),
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
                    ? "No jobs in this server process (it may have restarted — job state is in-memory). " +
                      "Recent transcript files below survive restarts; read one with read_file, or pass its " +
                      "job id to devmind_task_result for the disk-fallback summary."
                    : "Job ids reset when the server restarts; transcript files below are the durable record.",
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
                state = job.State.ToString().ToLowerInvariant(),
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
                    output_tail = job.Build.OutputTail,
                },
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

        // ── Helpers ───────────────────────────────────────────────────────────────

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

        private static string Snippet(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
        }

        /// <summary>
        /// devmind_task_result fallback when the job is not in memory (server restarted
        /// or evicted past the retention cap): serve the newest on-disk transcript for
        /// that job id. Job ids RESET per server process, so multiple runs' files can
        /// match — all candidates are listed and the newest one's tail is returned.
        /// </summary>
        private static string TranscriptFallback(string jobId)
        {
            const int TailChars = 4_000;
            FileInfo[] matches;
            try
            {
                matches = new DirectoryInfo(AgentJobManager.TranscriptDir)
                    .GetFiles($"{jobId}-*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToArray();
            }
            catch { matches = Array.Empty<FileInfo>(); }

            if (matches.Length == 0)
                return Err($"Unknown job_id: {jobId} — not in this server process and no transcript " +
                           "found on disk. Use devmind_task_list to see what exists.");

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
                state = "unknown (not in this server process — recovered from disk transcript)",
                note = "Job ids reset when the server restarts; this is the NEWEST transcript matching " +
                       "the id. The tail below usually ends with the agent's final summary. " +
                       "The conversation is NOT continuable — start a fresh task with a continuation brief.",
                transcript_path = matches[0].FullName,
                transcript_written_utc = matches[0].LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                transcript_tail = tail,
                other_matching_transcripts = matches.Skip(1).Select(f => f.FullName),
            }, JsonOpts);
        }
    }
}
