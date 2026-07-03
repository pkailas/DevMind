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

        public AgentJobState State;
        public HeadlessAgentResult? Result;
        public string? Error;

        /// <summary>Post-run build verification outcome (null when skipped: no file
        /// changes, no resolvable build command, or verify_build false).</summary>
        public BuildVerification? Build;
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

        public AgentJobManager()
        {
            // DEVMIND_ENDPOINT / DEVMIND_API_KEY is the established env convention
            // (see LlmClient); default is the local llama-server.
            string? endpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT")?.Trim();
            EndpointUrl = string.IsNullOrEmpty(endpoint) ? "http://127.0.0.1:8080/v1" : endpoint;
            ApiKey = Environment.GetEnvironmentVariable("DEVMIND_API_KEY") ?? "";

            _workerTask = Task.Run(WorkerLoopAsync);
        }

        public AgentJob Start(string prompt, string workingDirectory, int maxDepth, int timeoutMinutes,
            bool allowCommit, bool verifyBuild)
        {
            var job = new AgentJob
            {
                Id = $"job-{Interlocked.Increment(ref _nextId)}",
                Prompt = prompt,
                WorkingDirectory = workingDirectory,
                MaxDepth = maxDepth,
                TimeoutMinutes = timeoutMinutes,
                AllowCommit = allowCommit,
                VerifyBuild = verifyBuild,
                State = AgentJobState.Queued,
            };

            lock (_lock)
            {
                _jobs[job.Id] = job;
                _jobOrder.Add(job.Id);

                // Evict oldest FINISHED jobs past the retention cap (never evict live ones).
                while (_jobOrder.Count > RetainedJobs)
                {
                    string? evictId = _jobOrder.FirstOrDefault(id =>
                        _jobs[id].State is AgentJobState.Done or AgentJobState.Failed or AgentJobState.Cancelled);
                    if (evictId == null) break;
                    _jobOrder.Remove(evictId);
                    _jobs.Remove(evictId);
                }
            }

            _queue.Writer.TryWrite(job);
            return job;
        }

        public AgentJob? Get(string jobId)
        {
            lock (_lock) return _jobs.TryGetValue(jobId, out var job) ? job : null;
        }

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
                    Path.GetTempPath(), "devmind", "tasks",
                    $"{job.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

                try
                {
                    var options = new HeadlessOptions { AgenticLoopMaxDepth = job.MaxDepth };
                    var result = await HeadlessAgent.RunAsync(
                        job.Prompt, options, EndpointUrl, ApiKey,
                        job.WorkingDirectory,
                        buildCommand: null,
                        allowCommit: job.AllowCommit,
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
                }
            }
        }

        private static bool HasFileChanges(HeadlessAgentResult result)
            => result.Actions.Any(a =>
                a.Kind is "save" or "append" or "patch" or "delete" or "rename");

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
                    job.Cts.Cancel();
            }
            try { _workerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _shutdownCts.Dispose();
        }
    }
}
