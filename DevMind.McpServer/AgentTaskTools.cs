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
            "and how to verify success. Do not edit files under working_dir while the job runs.")]
        public async Task<string> TaskStart(
            [Description("The task brief: goal, relevant files, constraints, and how to verify success.")] string prompt,
            [Description("Absolute path of the directory the agent operates in (its sandbox).")] string working_dir,
            [Description("Max agentic iterations before the agent must stop (default 25).")] int? max_depth = null,
            [Description("Wall-clock kill timeout in minutes (default 30).")] int? timeout_minutes = null,
            [Description("Allow the agent to run git commit (default false — the caller owns version control).")] bool? allow_commit = null,
            [Description("After the agent finishes, the job runner builds the working_dir itself and attaches build_verification to the result (default true).")] bool? verify_build = null,
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
                verifyBuild: verify_build ?? true);

            return JsonSerializer.Serialize(new
            {
                job_id = job.Id,
                state = "queued",
                queue_position = _jobs.QueuePosition(job),
                endpoint = _jobs.EndpointUrl,
                hint = "Poll devmind_task_status with this job_id; fetch devmind_task_result when done.",
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
                return Task.FromResult(Err($"Unknown job_id: {job_id}."));

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
    }
}
