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
        /// <summary>STALL window in minutes (H-37; the name predates it): the job is cancelled
        /// only when the agent turn has made no progress for this long. There is no wall-clock
        /// cap — a progressing job is bounded by <see cref="MaxDepth"/>.</summary>
        public int TimeoutMinutes { get; init; }
        public bool AllowCommit { get; init; }
        public bool VerifyBuild { get; init; }
        /// <summary>Run `dotnet test` after a successful build verification and attach
        /// test_verification to the result (default false — tests can be slow).</summary>
        public bool VerifyTests { get; init; }
        /// <summary>Enable model reasoning (think blocks) for this task. Default false:
        /// briefed mechanical tasks iterate faster without spending the server's reasoning budget each iteration.</summary>
        public bool Think { get; init; }
        /// Stream the model's think blocks into the job's transcript (DISPLAY only —
        /// generation is <see cref="Think"/> / HeadlessOptions.ShowLlmThinking).
        /// Tri-state: null = no explicit per-job setting — the DEVMIND_TASK_SHOW_THINKING
        /// environment variable applies (legacy behaviour, preserved as a fallback). true
        /// = stream thinking into the transcript (wins over the env var); false = filtered,
        /// heartbeat lines mark visible silence (also wins over the env var). A continuation
        /// inherits the parent's setting, including an inherited null (same rule as Think).
        public bool? ShowThinking { get; init; }
        /// <summary>Effective chat_template_kwargs.reasoning_effort (low|medium|high|xhigh,
        /// normalized) when <see cref="Think"/> is on; null when thinking is off (the template
        /// never reads it then). Resolved at start: per-job value, else devmind.json
        /// "reasoningEffort", else "medium". A continuation inherits the parent's value.</summary>
        public string? ReasoningEffort { get; init; }
        /// <summary>H-58: let the harness turn thinking on when the same tests keep failing
        /// (default true). Only matters when <see cref="Think"/> is off. A continuation inherits
        /// the parent's value.</summary>
        public bool AutoThink { get; init; } = true;
        /// <summary>Depth-cap auto-extension: raise the cap in place when the job reaches it
        /// still converging (default true). A continuation inherits the parent's value.</summary>
        public bool AutoExtend { get; init; } = true;
        /// <summary>Most auto-extensions per turn (default 2). Inherited like <see cref="AutoExtend"/>.</summary>
        public int MaxExtensions { get; init; } = 2;
        /// <summary>Caller-imposed no-execution restriction for this task (default false):
        /// the agent may build but not run executables, tests, or a debugger. Continuations
        /// inherit this from their parent — a constraint set on turn 1 must not evaporate
        /// on turn 2 (the failure mode this exists to prevent).</summary>
        public bool NoExecute { get; init; }

        /// <summary>External MCP servers this job starts before its first LLM request
        /// (devmind_task_start mcp_servers, resolved against devmind.json at start time).
        /// Empty = no manager, no MCP tools. Continuations inherit the list, like NoExecute.</summary>
        public IReadOnlyList<McpServerConfig> McpServers { get; init; } = Array.Empty<McpServerConfig>();

        /// <summary>What happened to <see cref="McpServers"/> (null when none were requested).</summary>
        public McpJobReport? Mcp;

        public AgentJobState State;
        public HeadlessAgentResult? Result;
        public string? Error;

        /// <summary>Set by the stall watchdog when it cancelled the job:
        /// "stalled: no progress for N min (last progress: &lt;what&gt; at &lt;time&gt;)".
        /// Becomes <see cref="Error"/> so the result says why, not just "cancelled".</summary>
        public volatile string? StallReason;

        /// <summary>Post-run build verification outcome (null when skipped: no file
        /// changes, no resolvable build command, or verify_build false).</summary>
        public BuildVerification? Build;

        /// <summary>Post-run test verification outcome (null when skipped: verify_tests
        /// false, no file changes, or the build verification failed). Carries the
        /// harness-measured total test count parsed from this run's own output.</summary>
        public TestVerification? Tests;

        /// <summary>Before-agent baseline test run (null when test_baseline was "off"
        /// or the run was never performed). Harness-measured, like Tests.</summary>
        public TestVerification? BaselineTests;

        /// <summary>Whether the harness runs the test suite ONCE before the agent starts
        /// (so the result can report a real "before -> after" delta). Default true —
        /// "before-run"; callers with a slow suite opt out via "off". Only meaningful
        /// when VerifyTests is on.</summary>
        public bool RunTestBaseline { get; init; }

        /// <summary>
        /// True when the job technically finished but its work is NOT trustworthy as-is:
        /// it hit the iteration cap mid-task, or the post-run build/test verification
        /// failed. Surfaced as state "stopped_incomplete" — field lesson: "done" read as
        /// done, and broken-build results were built upon.
        /// </summary>
        public bool IsIncomplete =>
            State == AgentJobState.Done
            && ((Result?.HitDepthCap ?? false)
                || (Result?.NeedsInput ?? false)
                || (Result?.ThrashStopped ?? false)
                || Build is { Succeeded: false }
                || Tests is { Succeeded: false }
                || HasVerifiedBuildWarnings
                || NoFinalAnswer
                || (Result?.EndedWithoutTaskDone ?? false)
                || SelfReportMakesIncomplete);

        /// <summary>
        /// H-32 follow-up: the standard is 0 errors AND 0 warnings, so a harness FULL rebuild that
        /// reported warnings makes the job stopped_incomplete. Only a verified count counts: an
        /// incremental build (custom DEVMIND_BUILD_COMMAND, DEVMIND_VERIFY_REBUILD=0, another
        /// build system) does not re-emit warnings for up-to-date projects, so its number is
        /// not evidence either way.
        /// </summary>
        public bool HasVerifiedBuildWarnings =>
            Build is { WarningCountVerified: true, WarningCount: > 0 };

        /// <summary>
        /// H-20: the run ended but left no answer — nothing but harness status lines
        /// ("[CONTEXT] …", "[TOOL_USE] …"), or nothing at all. A finished task always says
        /// what it did (task_done carries a summary), so an empty answer means the loop
        /// stopped for some other reason. job-1693 ended `done` this way after one research
        /// shell command, and nothing downstream could tell. False when there is no Result
        /// to judge.
        /// </summary>
        public bool NoFinalAnswer =>
            Result != null && HeadlessAgent.StripStatusLines(Result.Answer).Trim().Length == 0;

        /// <summary>
        /// What the agent's own final answer said about whether it finished.
        /// <para>
        /// Every other signal above is one the HARNESS measured. This is the one the AGENT
        /// reported, and it is the only one that catches a job whose build was skipped and
        /// whose tests were never run — which is exactly the shape of the five jobs that
        /// ended `done` while their final message said "the caller must not report this as
        /// fixed". Computed once: the answer does not change after the run ends.
        /// </para>
        /// </summary>
        public SelfReportedIncomplete SelfReportedIncomplete =>
            Result == null
                ? SelfReportedIncomplete.None   // not cached: there is no answer to judge yet
                : _selfReported ??= SelfReportedIncompleteDetector.Detect(Result.Answer);

        private SelfReportedIncomplete? _selfReported;

        /// <summary>
        /// H-31: the harness itself ran the test suite green — tests were requested
        /// (verify_tests), the harness build succeeded, and the harness test run succeeded. A
        /// green build alone does not count: compiling proves nothing was fixed. A requested
        /// test run that did not happen (null) does not count either.
        /// </summary>
        public bool HarnessTestVerified =>
            Build is { Succeeded: true } && VerifyTests && Tests is { Succeeded: true };

        /// <summary>
        /// Whether the INCOMPLETE: declaration makes the job incomplete: it does unless the harness
        /// TEST run was green. job-2118 ("INCOMPLETE: full solution test suite ... not run by me —
        /// harness verifies it", 1319 green) ended stopped_incomplete on a gap the harness had
        /// already closed (H-43). A green build alone does not count: a declared gap still
        /// compiles. With verify_tests off or failed — the H-01 jobs ran with verify_build off —
        /// the declaration stops the job.
        /// Read only once State is Done, which the worker publishes after both verifications.
        /// </summary>
        public bool SelfReportMakesIncomplete =>
            SelfReportedIncomplete.Detected && !HarnessTestVerified;

        /// <summary>
        /// The INCOMPLETE: line that did NOT make the job incomplete because
        /// the harness test run was green — surfaced as self_report_note in devmind_task_status /
        /// devmind_task_result so a driver still reads the line. Null otherwise (including when
        /// the line did count: it is then in incomplete_reasons).
        /// </summary>
        public string? SelfReportNote =>
            State == AgentJobState.Done
            && SelfReportedIncomplete.Detected
            && !SelfReportMakesIncomplete
                ? SelfReportedIncomplete.Line
                : null;

        /// <summary>The cap in force: the finished turn's (requested cap plus extensions), the
        /// live session's while it runs, otherwise the requested cap.</summary>
        public int EffectiveMaxDepth =>
            Result != null ? Result.EffectiveMaxDepth
            : State == AgentJobState.Running && Session != null ? Session.EffectiveMaxDepth
            : MaxDepth;

        /// <summary>Depth-cap auto-extensions granted so far (live while running).</summary>
        public int DepthExtensionsUsed =>
            Result != null ? Result.DepthExtensions.Count
            : State == AgentJobState.Running && Session != null ? Session.DepthExtensionsUsed
            : 0;

        /// <summary>depth_extensions for the result payload and sidecar.</summary>
        public object[] DepthExtensionsPayload() =>
            (Result?.DepthExtensions ?? Array.Empty<DepthExtension>())
                .Select(e => (object)new { at_depth = e.AtDepth, new_cap = e.NewCap, signal_summary = e.SignalSummary })
                .ToArray();

        /// <summary>Why the job is incomplete (empty when it isn't).</summary>
        public string[] IncompleteReasons()
        {
            if (State != AgentJobState.Done) return Array.Empty<string>();
            var reasons = new List<string>(3);
            if (Result?.NeedsInput ?? false) reasons.Add("needs_input");
            if (Result?.ThrashStopped ?? false) reasons.Add("thrashing");
            if (Result?.HitDepthCap ?? false) reasons.Add("hit_depth_cap");
            if (Build is { Succeeded: false }) reasons.Add("build_verification_failed");
            if (Tests is { Succeeded: false }) reasons.Add("test_verification_failed");
            if (HasVerifiedBuildWarnings)
            {
                // The count, then the first few warnings themselves — enough for a driver to
                // continue the job with "fix these" without re-running the build.
                reasons.Add("build_warnings");
                reasons.Add($"harness rebuild reported {Build!.WarningCount} warning(s)");
                reasons.AddRange(Build.WarningLines);
            }
            if (NoFinalAnswer) reasons.Add("no_final_answer");
            // H-52: the loop ended on a narration-only reply, not task_done / ask_caller.
            if (Result?.EndedWithoutTaskDone ?? false) reasons.Add("ended_without_task_done");

            // The agent's own words, and the line it said them in — a caller reading
            // incomplete_reasons should not have to go back to the answer to find out which
            // sentence tripped it.
            SelfReportedIncomplete self = SelfReportedIncomplete;
            if (SelfReportMakesIncomplete)
            {
                reasons.Add(SelfReportedIncompleteDetector.Reason);
                if (self.Line.Length > 0) reasons.Add(self.Line);
            }

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

    /// <summary>The "mcp" section of devmind_task_result: which servers the job asked for, which
    /// started, which did not and why, and how many MCP tool calls the agent made.</summary>
    internal sealed class McpJobReport
    {
        public IReadOnlyList<string> Requested { get; init; } = Array.Empty<string>();
        public List<string> Started { get; } = new List<string>();
        /// <summary>Server name → reason it did not start. The job ran on without it.</summary>
        public Dictionary<string, string> Failed { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public int Calls { get; set; }

        /// <summary>The JSON shape shared by devmind_task_result and the result sidecar; null when no servers were requested.</summary>
        public static object? Payload(McpJobReport? report) => report == null ? null : new
        {
            requested = report.Requested,
            started = report.Started,
            failed = report.Failed.Select(kv => new { server = kv.Key, reason = kv.Value }).ToArray(),
            calls = report.Calls,
        };
    }

    /// <summary>Outcome of the job runner's own post-agent build check.</summary>
    internal sealed class BuildVerification
    {
        public required string Command { get; init; }
        public int ExitCode { get; init; }
        /// <summary>Last ~2 KB of build output — enough for the error summary — followed by a
        /// [verification] line saying whether the warning count in it is verified.</summary>
        public required string OutputTail { get; init; }
        public bool Succeeded => ExitCode == 0;

        /// <summary>H-32: true when the harness ran a FULL rebuild (VerificationBuild) and read a
        /// warning count from it — only then is <see cref="WarningCount"/> a real figure. False
        /// for an incremental build, which does not re-emit warnings for up-to-date projects.</summary>
        public bool WarningCountVerified { get; init; }

        /// <summary>The warning count parsed from the FULL output (null when unparseable or
        /// the build was not a verified rebuild).</summary>
        public int? WarningCount { get; init; }

        /// <summary>The first few distinct warning lines from the FULL output of a verified
        /// rebuild (empty otherwise) — quoted in incomplete_reasons under build_warnings.</summary>
        public IReadOnlyList<string> WarningLines { get; init; } = Array.Empty<string>();
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

        /// <summary>
        /// Test seam: when set, test-suite runs (baseline and after) go through this
        /// instead of a real `dotnet test`. Lets tests COUNT the invocations ("off
        /// skips the before-run" must mean no second call, not merely that it was fast)
        /// and shape the output hermetically. Null = production path. Per-instance, so
        /// test classes cannot interfere with each other or with a live server.
        /// </summary>
        internal Func<string /*workingDirectory*/, CancellationToken, Task<TestVerification>>? TestRunnerOverride { get; set; }

        /// <summary>
        /// Test seam: when set, the post-agent build verification goes through this
        /// instead of a real build on the working directory. Lets tests drive a
        /// verify_build job hermetically — a temp dir with no .slnx/.csproj makes the
        /// real resolver return null so job.Build would never populate (the exact
        /// shape the verification-race tests need to observe and assert on).
        /// Null = production path. Mirrors TestRunnerOverride.
        /// </summary>
        internal Func<string /*workingDirectory*/, CancellationToken, Task<BuildVerification?>>? BuildRunnerOverride { get; set; }

        /// <summary>
        /// Test seam, narrower than <see cref="BuildRunnerOverride"/>: only the process run is
        /// replaced. Resolution, the H-32 rebuild rewrite and the warning-count parse all run
        /// for real, and the seam receives the exact command the harness would execute.
        /// Returns (output, exitCode). Null = production path.
        /// </summary>
        internal Func<string /*command*/, string /*workingDirectory*/, Task<(string output, int exitCode)>>? BuildExecOverride { get; set; }

        /// <summary>Clock for the stall watchdog's liveness record (test seam; default system).</summary>
        internal TimeProvider Clock { get; set; } = TimeProvider.System;

        /// <summary>Test seam: replaces the job's timeout_minutes stall window (e.g. seconds).</summary>
        internal TimeSpan? StallWindowOverride { get; set; }

        /// <summary>Test seam: how often the watchdog checks (real time). Null = window / 20,
        /// clamped to 50 ms..15 s.</summary>
        internal TimeSpan? WatchdogPollOverride { get; set; }

        /// <summary>Test seam: builds a job's MCP manager from its server list. Null = a real
        /// <see cref="McpClientManager"/>. Lets tests count disposals and script starts.</summary>
        internal Func<IReadOnlyList<McpServerConfig>, IMcpClientManager>? McpManagerFactory { get; set; }

        /// <summary>How long one requested MCP server may take to start before the job gives up
        /// on it and runs without it. Settable for tests.</summary>
        internal TimeSpan McpStartTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Resolves devmind_task_start's mcp_servers against the configured servers. Null or
        /// empty → an empty list (no manager). An unknown name → null, with an error that
        /// names every configured server, so the caller can fix the call without opening
        /// devmind.json. Duplicates collapse. Pure, for the tests.
        /// </summary>
        internal static IReadOnlyList<McpServerConfig>? ResolveMcpServers(
            IReadOnlyList<string>? requested, IReadOnlyList<McpServerConfig> configured, out string? error)
        {
            error = null;
            if (requested == null || requested.Count == 0)
                return Array.Empty<McpServerConfig>();

            var byName = configured.ToDictionary(c => c.Name, StringComparer.Ordinal);
            var result = new List<McpServerConfig>();
            var unknown = new List<string>();
            foreach (string name in requested.Distinct(StringComparer.Ordinal))
            {
                if (byName.TryGetValue(name ?? "", out var cfg)) result.Add(cfg);
                else unknown.Add($"'{name}'");
            }
            if (unknown.Count == 0)
                return result;

            string known = configured.Count == 0
                ? "none are configured (add an \"mcpServers\" block to %APPDATA%\\devmind\\devmind.json)"
                : "configured: " + string.Join(", ", configured.Select(c => c.Name));
            error = $"mcp_servers: unknown server {string.Join(", ", unknown)} — {known}.";
            return null;
        }

        /// <summary>
        /// Starts every requested server in parallel and waits for all of them, each bounded
        /// by <paramref name="perServer"/>. A server that fails or times out is recorded in the
        /// report and skipped; it never fails the job. Job cancellation ends the wait (the
        /// turn that follows then reports Cancelled). Never throws.
        /// </summary>
        internal static async Task<McpJobReport> StartJobMcpServersAsync(
            IMcpClientManager clients, IReadOnlyList<string> names, TimeSpan perServer,
            Action<string> tail, CancellationToken ct)
        {
            var report = new McpJobReport { Requested = names.ToArray() };
            var outcomes = await Task.WhenAll(names.Select(async name =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(perServer);
                try
                {
                    // WaitAsync bounds even a start that ignores its token.
                    string? err = await clients.StartAsync(name, timeout.Token)
                        .WaitAsync(perServer, ct).ConfigureAwait(false);
                    return (name, err: err == null ? null : ShortReason(err));
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    return (name, err: (string?)"job cancelled while the server was starting");
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    return (name, err: (string?)$"did not start within {perServer.TotalSeconds:0} s");
                }
                catch (Exception ex)
                {
                    return (name, err: (string?)ex.Message);
                }
            })).ConfigureAwait(false);

            var statuses = clients.GetStatuses().ToDictionary(s => s.Name, StringComparer.Ordinal);
            foreach (var (name, err) in outcomes)
            {
                if (err == null)
                {
                    report.Started.Add(name);
                    int tools = statuses.TryGetValue(name, out var st) ? st.ToolCount : 0;
                    tail($"\n[job] mcp: {name} started ({tools} tools)\n");
                }
                else
                {
                    report.Failed[name] = err;
                    tail($"\n[job] mcp: {name} failed to start: {err} — continuing without it\n");
                }
            }
            return report;
        }

        private static string ShortReason(string error) => McpClientManager.ShortReason(error);

        public AgentJobManager(Action? onJobFinished = null)
        {
            _onJobFinished = onJobFinished ?? (() => { });
            // DEVMIND_ENDPOINT / DEVMIND_API_KEY is the established env convention
            // (see LlmClient); default is the local llama-server.
            string? endpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT")?.Trim();
            EndpointUrl = string.IsNullOrEmpty(endpoint) ? "http://127.0.0.1:8080/v1" : endpoint;
            ApiKey = Environment.GetEnvironmentVariable("DEVMIND_API_KEY") ?? "";

            _nextId = LoadPersistedIdCounter();
            // Best-effort: make sure the transcript location exists before the worker
            // thread starts writing. Production default is always creatable; a
            // test-set DEVMIND_TASKS_DIR may not be, and the individual writers
            // (WriteActiveMarker, WriteResultSidecar) are themselves best-effort, so a
            // failure here must never take the manager down with it.
            try { Directory.CreateDirectory(TranscriptDir); } catch { /* best-effort */ }
            // H-03: a job whose server died under it left its marker behind. Record it as
            // stopped_incomplete / server_restart and remove the marker. Markers of jobs
            // another live process is running are never touched.
            try { ActiveJobMarkers.SweepDeadMarkers(); } catch { /* best-effort */ }
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

        // Allocation is a read-increment-write under an EXCLUSIVE file lock, not a
        // startup snapshot: multiple MCP server processes run concurrently (one per
        // client session — three were live on BEAST at once), and a per-process
        // in-memory counter seeded at startup would mint colliding ids across them.
        private static readonly object _idAllocLock = new object();

        private string NextJobId()
        {
            lock (_idAllocLock) // intra-process; the FileShare.None handle is the inter-process lock
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        Directory.CreateDirectory(TranscriptDir);
                        using var fs = new FileStream(
                            IdCounterPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        using var reader = new StreamReader(fs, Encoding.UTF8,
                            detectEncodingFromByteOrderMarks: true, 128, leaveOpen: true);
                        string text = reader.ReadToEnd();
                        int persisted = int.TryParse(text.Trim(), out int v) && v > 0 ? v : 0;
                        int next = Math.Max(persisted, Volatile.Read(ref _nextId)) + 1;

                        fs.SetLength(0);
                        fs.Position = 0;
                        using (var writer = new StreamWriter(fs, Encoding.UTF8, 128, leaveOpen: true))
                        {
                            writer.Write(next);
                            writer.Flush();
                        }

                        Volatile.Write(ref _nextId, next);
                        return $"job-{next}";
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(25); // another server process holds the counter — brief retry
                    }
                    catch
                    {
                        break; // unexpected (permissions, disk) — fall through to in-memory
                    }
                }

                // Degraded fallback: in-memory increment from the startup seed (the
                // pre-lock behavior). Only here can ids collide with another process.
                return $"job-{Interlocked.Increment(ref _nextId)}";
            }
        }

        public AgentJob Start(string prompt, string workingDirectory, int maxDepth, int timeoutMinutes,
            bool allowCommit, bool verifyBuild, bool think = false, bool verifyTests = false,
            bool noExecute = false, bool runTestBaseline = true, bool? showThinking = null,
            IReadOnlyList<McpServerConfig>? mcpServers = null, string? reasoningEffort = null,
            bool autoThink = true, bool autoExtend = true, int maxExtensions = 2)
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
                ShowThinking = showThinking,
                // Null when thinking is off; otherwise the given value, normalized, or "medium"
                // (never absent — the Qwen3.8 templates treat absent as xhigh).
                ReasoningEffort = think ? DevMind.ReasoningEffort.NormalizeOrDefault(reasoningEffort) : null,
                AutoThink = autoThink,
                AutoExtend = autoExtend,
                MaxExtensions = maxExtensions,
                VerifyTests = verifyTests,
                NoExecute = noExecute,
                RunTestBaseline = runTestBaseline,
                McpServers = mcpServers ?? Array.Empty<McpServerConfig>(),
                State = AgentJobState.Queued,
            };

            Register(job);
            _queue.Writer.TryWrite(job);
            return job;
        }

        /// <summary>
        /// The no-execution restriction is inherited from the parent by default: a
        /// constraint the caller set on turn 1 must survive to turn N (the failure mode
        /// that motivated no_execute). An explicit noExecute: true can turn it ON for a
        /// continuation that didn't have it; it CANNOT be relaxed by a continuation —
        /// passing false is treated the same as omitting it, and the restriction, once
        /// set, can only be lifted by starting a fresh task. Extracted as a pure static
        /// so the inheritance decision is unit-testable without a live job queue.
        /// </summary>
        internal static bool ResolveContinuationNoExecute(bool parentNoExecute, bool? requestedNoExecute)
        {
            return parentNoExecute || requestedNoExecute == true;
        }

        /// <summary>
        /// Resumes a finished job's conversation as a new job: the session (LlmClient
        /// history, caches, scratchpad) transfers to the continuation, so a bare
        /// "continue" picks up exactly where the parent stopped. Returns null with a
        /// user-presentable error when the parent cannot be continued.
        /// </summary>
        public AgentJob? Continue(string parentJobId, string prompt, int maxDepth, int timeoutMinutes,
            bool verifyBuild, out string error, bool verifyTests = false, bool? noExecute = null,
            bool runTestBaseline = true, bool? showThinking = null)
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
                // Inherited by default (same rule as Think — including an inherited
                // null, i.e. "env fallback"); an explicit value overrides it for this
                // continuation (a display preference flips either way, unlike
                // noExecute's ratchet).
                ShowThinking = showThinking ?? parent.ShowThinking,
                // Inherited with Think (no per-continuation override: the session's options
                // keep the parent's value, which is what the request actually sends).
                ReasoningEffort = parent.ReasoningEffort,
                AutoThink = parent.AutoThink, // inherited with Think (H-58)
                AutoExtend = parent.AutoExtend,
                MaxExtensions = parent.MaxExtensions,
                NoExecute = ResolveContinuationNoExecute(parent.NoExecute, noExecute),
                // Inherited like NoExecute. The parent's manager was disposed when the parent
                // ended, so the worker starts these servers again for the continued turn.
                McpServers = parent.McpServers,
                RunTestBaseline = runTestBaseline,
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
        /// process — the disk-fallback in devmind_task_result depends on this path.
        ///
        /// Overridable via DEVMIND_TASKS_DIR (2026-08-18): the test suite spins real
        /// agent jobs against stub LLM servers, and when they wrote their transcripts,
        /// _active.json, _jobcounter.txt and .result.json sidecars into this GLOBAL
        /// folder they yanked dm-watch off the live job's transcript (it follows the
        /// newest job-*.log), poisoned its BUSY header with test job ids, and burned
        /// the global job-id counter (~340 ids/hour, mostly tests). Tests set the var
        /// to a private per-run dir in a ModuleInitializer; production (unset) keeps
        /// the default below, so nothing outside tests changes behaviour. Re-evaluated
        /// per access so an override set at process start is always picked up.</summary>
        public static string TranscriptDir =>
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEVMIND_TASKS_DIR"))
                ? Path.Combine(Path.GetTempPath(), "devmind", "tasks")
                : Environment.GetEnvironmentVariable("DEVMIND_TASKS_DIR")!;

        /// <summary>Cap for devmind_task_status wait_seconds — beyond this the
        /// caller is polling faster than a task ever finishes anyway, and a stuck
        /// MCP tool call is worse than a short wait. Values above the cap are
        /// CLAMPED, not rejected: a caller asking for 300s gets 55s, not an error.
        /// 55, not 60: over Claude's remote-devices bridge the transport's own call
        /// timeout is 60s, so a full 60s wait always lost the race ("Device did not
        /// respond within 60s") while the job kept running (watchlist H-23).</summary>
        public const int MaxWaitSeconds = 55;

        /// <summary>Clamps a requested wait_seconds to [0, MaxWaitSeconds] (0 =
        /// return immediately, the long-standing default). Public static so the
        /// tests pin the clamp without spinning up a job queue.</summary>
        public static int ClampWaitSeconds(int requested)
            => requested <= 0 ? 0 : Math.Min(requested, MaxWaitSeconds);

        /// <summary>
        /// The devmind_task_status wait: with waitSeconds 0 (or negative — the
        /// omit/zero default) returns immediately, observing the state once. With
        /// waitSeconds > 0, polls roughly every second (never busy-waiting, always
        /// honoring cancellation) until the observed state STRING differs from the
        /// first observation or the clamped budget elapses. Returns true when the
        /// state changed within the budget. The string probe (not the raw enum) is
        /// what callers pass so queued->running and done->needs_input both count
        /// as changes. Pure on the probe — no job access — so tests drive it with
        /// a fake sequence of states.
        /// </summary>
        public static async Task<bool> WaitForStateChangeAsync(
            Func<string> observeState, int waitSeconds, CancellationToken cancellationToken)
        {
            if (ClampWaitSeconds(waitSeconds) <= 0) return false;

            string initial = observeState();
            var deadline = DateTime.UtcNow.AddSeconds(ClampWaitSeconds(waitSeconds));
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return observeState() != initial;
                // Race the 1s poll against the remaining budget so the wait ends
                // exactly at the deadline (not at deadline + one more poll).
                await Task.WhenAny(
                    Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken),
                    Task.Delay(remaining, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (observeState() != initial) return true;
            }
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
                // H-37: no wall-clock kill. The stall watchdog below runs for the agent turn
                // only; the harness phases (test baseline, build/test verification) are not
                // the agent's time and carry their own timeouts.

                string transcriptPath = Path.Combine(
                    TranscriptDir,
                    $"{job.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

                ActiveJobMarkers.Write(job, transcriptPath);

                // Per JOB, not per session: disposed in the finally below whatever the terminal
                // state, while the session it was attached to lives on for a continuation.
                IMcpClientManager? jobMcp = null;

                try
                {
                    // Test baseline (tier 1, opt-in via verify_tests): the harness runs
                    // the suite ONCE before the agent starts so the result can report a
                    // real "before -> after" delta the agent never claimed. Gated on
                    // verify_tests ALONE — HasFileChanges is not known until afterwards,
                    // and a baseline on a no-change job is just wasted time, never a wrong
                    // number. "off" (RunTestBaseline false) skips the run entirely.
                    if (job.VerifyTests && job.RunTestBaseline)
                    {
                        job.BaselineTests = await RunTestSuiteAsync(job).ConfigureAwait(false);
                        var b = job.BaselineTests;
                        job.AppendTail($"\n[job] test baseline: exit {b.ExitCode}, " +
                            (b.Total.HasValue ? $"total {b.Total} (harness-measured)" : $"no parseable total ({b.ParseFailure})") + "\n");
                    }

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
                            // chat_template_kwargs.reasoning_effort, sent only while thinking
                            // is on (null when off -> the option default, never sent).
                            ReasoningEffort = job.ReasoningEffort ?? DevMind.ReasoningEffort.Default,
                            // H-58: the harness may turn thinking on for repeated test failures.
                            AutoThink = job.AutoThink,
                            // Depth-cap auto-extension for a job still converging at the cap.
                            AutoExtendDepth = job.AutoExtend,
                            MaxDepthExtensions = job.MaxExtensions,
                            // DISPLAY switch (per-job, from show_thinking): streams the
                            // generated think blocks into the transcript. Tri-state — a
                            // non-null value wins over the DEVMIND_TASK_SHOW_THINKING env
                            // var; null keeps the legacy env-var fallback.
                            StreamThinkingToTranscript = job.ShowThinking,
                            // Decides which test-verification regime the headless addendum
                            // writes: verify_tests on -> the agent must NOT re-run the full
                            // suite (this runner does it right after, with no file changes in
                            // between — measured at 23% of one short job); off
                            // -> no harness safety net, the agent runs the suite itself.
                            HarnessVerifiesTests = job.VerifyTests,
                        };
                        session = new HeadlessSession(options, EndpointUrl, ApiKey,
                            job.WorkingDirectory, buildCommand: null, allowCommit: job.AllowCommit,
                            noExecute: job.NoExecute, sessionId: job.Id);
                        job.Session = session;
                    }
                    else
                    {
                        session.SetMaxDepth(job.MaxDepth);
                        session.SetAutoExtendDepth(job.AutoExtend, job.MaxExtensions);
                        // A continuation's job may differ from the session it reuses
                        // (e.g. noExecute newly opted in), so re-sync the host guard.
                        session.SetNoExecute(job.NoExecute);
                        // Same rule for the display switch: an explicit show_thinking on
                        // the continuation can differ from what the parent's session was
                        // built with (e.g. parent omitted it -> env fallback; now explicit).
                        session.SetStreamThinking(job.ShowThinking);
                        // Same rule for the test-verification regime: a continuation's
                        // verify_tests can differ from the parent's (e.g. parent ran with
                        // verification off, the continue job opts in), and BuildSystemPrompt
                        // rebuilds the addendum from _options every turn — a stale regime
                        // would misinstruct the agent on turn N+1.
                        session.SetHarnessVerifiesTests(job.VerifyTests);
                    }

                    // External MCP servers (mcp_servers): started and awaited BEFORE the first
                    // LLM request, so the tools are in that request. A server that fails is
                    // recorded and skipped — the job runs on without it.
                    if (job.McpServers.Count > 0)
                    {
                        jobMcp = McpManagerFactory?.Invoke(job.McpServers) ?? new McpClientManager(job.McpServers);
                        job.AppendTail($"\n[job] mcp: starting {string.Join(", ", job.McpServers.Select(s => s.Name))}...\n");
                        job.Mcp = await StartJobMcpServersAsync(jobMcp, job.McpServers.Select(s => s.Name).ToList(),
                            McpStartTimeout, job.AppendTail, job.Cts.Token).ConfigureAwait(false);
                        session.SetMcpClients(jobMcp);
                    }

                    var liveness = new JobLiveness(Clock,
                        job.ParentJobId == null ? "job started" : "continuation started");
                    HeadlessAgentResult result;
                    using (var watchdogStop = new CancellationTokenSource())
                    {
                        Task watchdog = RunStallWatchdogAsync(job, liveness, watchdogStop.Token);
                        try
                        {
                            result = await session.RunTurnAsync(
                                job.Prompt,
                                transcriptPath: transcriptPath,
                                progress: job.AppendTail,
                                ct: job.Cts.Token,
                                liveness: liveness).ConfigureAwait(false);
                        }
                        finally
                        {
                            watchdogStop.Cancel();
                            await watchdog.ConfigureAwait(false);
                        }
                    }

                    // The RESULT is set as soon as the turn ends — devmind_task_result
                    // reads it once the job is Done, and it's needed to compute the
                    // terminal state below. But the STATE is deliberately NOT published
                    // yet when verification is requested: a caller that sees
                    // State == Done treats the job as final and serializes job.Build /
                    // job.Tests (devmind_task_result) with no synchronization against
                    // this worker. Publishing Done before the verification fields
                    // settled let a concurrent reader observe the in-memory result
                    // mid-verification (build populated, test_verification null). We
                    // hold the job in Running through verification and publish the
                    // terminal state only once both have settled (end of the try block).
                    job.Result = result;
                    job.Error = result.Error;
                    // Before the terminal state is published (see below): a reader that sees Done
                    // must see the final call count.
                    if (job.Mcp != null && jobMcp != null) job.Mcp.Calls = jobMcp.CallCount;
                    if (result.Cancelled && job.StallReason is { } stall)
                        job.Error = stall; // not a generic cancel: say it stalled, and on what

                    // The state the job will report once nothing is left to verify.
                    var terminalState = result.Cancelled ? AgentJobState.Cancelled
                        : result.Error != null ? AgentJobState.Failed
                        : AgentJobState.Done;

                    // Post-agent build verification: the job runner checks the build so
                    // agents don't burn iterations fighting shell timeouts/PATH to do it
                    // themselves (two live delegations lost most of their depth to this).
                    // Only when the agent actually changed files, and never on cancel.
                    if (job.VerifyBuild && terminalState == AgentJobState.Done && HasFileChanges(result))
                    {
                        // Keep a poller informed the job is still working (a real build
                        // can take seconds): the transcript tail is the live signal, not
                        // a new state value. Held in Running until the result is final.
                        job.AppendTail("\n[job] build verification: running...\n");
                        job.Build = await VerifyBuildAsync(job).ConfigureAwait(false);
                        // H-43: close the "running..." line, or the tail's last word on a
                        // finished job reads as if verification were still going.
                        job.AppendTail(job.Build == null
                            ? "[job] build verification: skipped\n"
                            : $"[job] build verification: {(job.Build.Succeeded ? "passed" : "failed")}\n");
                    }

                    // Test verification (opt-in): only when the build verification did
                    // not already fail — red tests on a broken build are noise.
                    if (job.VerifyTests && terminalState == AgentJobState.Done && HasFileChanges(result)
                        && job.Build is not { Succeeded: false })
                    {
                        // Same as build: a real `dotnet test` can take a long time, so
                        // surface it in the tail while the job stays Running.
                        job.AppendTail("\n[job] test verification: running...\n");
                        job.Tests = await RunTestSuiteAsync(job).ConfigureAwait(false);
                        job.AppendTail($"[job] test verification: {(job.Tests.Succeeded ? "passed" : "failed")}" +
                            $"{(job.Tests.Total is int total ? $" ({total} tests)" : "")}\n");
                    }

                    // Publish the terminal state. Invariant this line guards: a job
                    // observed as Done never carries a verification field that is still
                    // going to change. If verify_build / verify_tests was requested, the
                    // corresponding field was populated (or explicitly null with a stated
                    // reason) on the lines above, BEFORE this point — so devmind_task_result
                    // and the persisted sidecar can never serve a Done job with a
                    // half-set verification. This also closes the pre-existing
                    // build_verification window: before this fix a job published Done
                    // right after the turn, so a concurrent reader could observe
                    // build_verification still null while the build ran. For a job with
                    // neither verify flag, terminalState is published exactly as before
                    // (no verification ran, no tail lines) — byte-for-byte unchanged.
                    job.State = terminalState;
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
                    // The job's MCP servers end with the job — done, failed, cancelled and
                    // stopped_incomplete all pass through here. The session is detached first
                    // so the retained conversation never points at a disposed manager; a
                    // continuation attaches a fresh one.
                    if (jobMcp != null)
                    {
                        if (job.Mcp != null) job.Mcp.Calls = jobMcp.CallCount;
                        try { job.Session?.SetMcpClients(null!); } catch { /* never kill the worker */ }
                        try { await jobMcp.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception ex) { Console.Error.WriteLine($"[AgentJobManager] {job.Id} MCP dispose failed: {ex.Message}"); }
                    }

                    job.EndedAtUtc = DateTime.UtcNow;
                    ActiveJobMarkers.Clear(job.Id);   // this job's marker only (H-66)
                    WriteResultSidecar(job);

                    // Drop the turn's PATCH backups now the job is over, WITHOUT ending
                    // the session — a finished job keeps its conversation so it can be
                    // continued, and disposal is what used to be relied on here. It runs
                    // only on session expiry, eviction past the retention cap, or a clean
                    // shutdown, so backups from a job nobody follows up on sat in
                    // %TEMP%\DevMind indefinitely, and a force-killed server orphaned them
                    // permanently. The backups play no part in a continuation: the stack is
                    // write-only, with no path that restores a file from one.
                    try { job.Session?.DrainPatchBackups(); } catch { /* never kill the worker */ }

                    try { _onJobFinished(); } catch { /* never kill the worker */ }
                }
            }
        }

        /// <summary>The job's stall window: timeout_minutes (or the test override).</summary>
        private TimeSpan StallWindow(AgentJob job) =>
            StallWindowOverride ?? TimeSpan.FromMinutes(job.TimeoutMinutes);

        /// <summary>
        /// H-37 stall watchdog for one agent turn: polls the liveness record and cancels the
        /// job once no progress event has happened for the whole window (an in-flight shell or
        /// test command holds the clock for its own timeout — see JobLiveness). Runs only while
        /// the turn runs, so the harness phases are never counted or killed by it.
        /// </summary>
        private async Task RunStallWatchdogAsync(AgentJob job, JobLiveness liveness, CancellationToken stop)
        {
            TimeSpan window = StallWindow(job);
            TimeSpan poll = WatchdogPollOverride ?? TimeSpan.FromMilliseconds(
                Math.Clamp(window.TotalMilliseconds / 20, 50, 15_000));
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(poll, stop).ConfigureAwait(false);
                    if (!liveness.IsStalled(window, out string reason)) continue;

                    job.StallReason = reason;
                    job.AppendTail($"\n[job] {reason} - cancelling\n");
                    Console.Error.WriteLine($"[AgentJobManager] {job.Id} {reason}");
                    job.Cts.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException) { /* turn ended first */ }
        }

        // ── Active-job markers ───────────────────────────────────────────────
        // One marker per running job, {TranscriptDir}\_active_<job_id>.json — see
        // ActiveJobMarkers. Several servers share TranscriptDir, so each writes and
        // clears only its own jobs' markers (H-66).

        /// <summary>
        /// True when any active-job marker names a LIVE process other than this one — a
        /// second server, or a job still running under a previous one. Callers that clean
        /// up shared temp state (patch backups, transcripts) use this to stay off files
        /// another agent may still own. A marker whose owner is dead is a leftover from a
        /// crash and does not count, which is the whole reason the pid is written.
        /// </summary>
        public static bool IsJobActiveElsewhere() => ActiveJobMarkers.IsJobActiveElsewhere();

        private static bool HasFileChanges(HeadlessAgentResult result)
            => result.Actions.Any(a =>
                a.Kind is "save" or "append" or "patch" or "delete" or "rename");

        /// <summary>Runs `dotnet test` in the working directory (opt-in via verify_tests),
        /// and derives the total test count from the output it captured itself. Never
        /// throws — a verification failure is data. Used for BOTH the before-agent
        /// baseline (tier 1) and the after-run verification, so both counts come from
        /// the same instrument.</summary>
        private async Task<TestVerification> RunTestSuiteAsync(AgentJob job)
        {
            const int TestTimeoutSeconds = 900;
            const string command = "dotnet test";

            // Test seam: when set, suite runs go through this instead of a real
            // `dotnet test` — lets tests count and shape the invocations hermetically
            // (no dotnet process, no user-level paths resolved). Null = production path.
            if (TestRunnerOverride is { } override_)
                return await override_(job.WorkingDirectory, job.Cts.Token).ConfigureAwait(false);

            try
            {
                var runner = new ShellRunner(job.WorkingDirectory);
                var (output, exitCode) = await runner.ExecuteAsync(
                    command, job.Cts.Token, TestTimeoutSeconds).ConfigureAwait(false);
                return TestVerification.FromRun(command, exitCode, output);
            }
            catch (OperationCanceledException)
            {
                return TestVerification.FromRun(command, -1, "test run cancelled");
            }
            catch (Exception ex)
            {
                return TestVerification.FromRun(command, -1, $"test verification crashed: {ex.Message}");
            }
        }

        /// <summary>
        /// Persists a finished job's outcome next to its transcript
        /// ({TranscriptDir}\{id}.result.json) so devmind_task_result can serve REAL
        /// results — answer, actions, verification — after a server restart, not just
        /// a transcript tail. Best effort; ids are unique across restarts (see
        /// NextJobId), so a sidecar is never ambiguous.
        /// </summary>
        internal static void WriteResultSidecar(AgentJob job)
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
                    self_report_note = job.SelfReportNote,
                    answer = r?.Answer ?? "",
                    actions = (r?.Actions ?? Array.Empty<HostAction>())
                        .Select(a => new { kind = a.Kind, detail = a.Detail, success = a.Success }),
                    iterations = r?.Iterations ?? 0,
                    elapsed_seconds = r?.ElapsedSeconds ?? 0,
                    // Per-job token usage (this job's model requests only) — persisted so
                    // devmind_task_result serves it after a server restart.
                    tokens_in_total = r?.TokensInTotal,
                    tokens_in_new = r?.TokensInNew,
                    tokens_in_new_partial = (bool?)(r?.TokensInNewPartial ?? false),
                    tokens_out = r?.TokensOut,
                    hit_depth_cap = r?.HitDepthCap ?? false,
                    max_depth = job.MaxDepth,
                    effective_max_depth = job.EffectiveMaxDepth,
                    depth_extensions = job.DepthExtensionsPayload(),
                    auto_think_escalations = r?.AutoThinkEscalations ?? 0,
                    auto_think_iterations = r?.AutoThinkIterations ?? 0,
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
                        // H-32: true only when the harness ran a full rebuild; an incremental
                        // build does not re-emit warnings for up-to-date projects.
                        warning_count_verified = job.Build.WarningCountVerified,
                        warning_count = job.Build.WarningCount,
                        output_tail = job.Build.OutputTail,
                    },
                    test_verification = TestVerificationPayload.Create(job),
                    mcp = McpJobReport.Payload(job.Mcp),
                });
                File.WriteAllText(Path.Combine(TranscriptDir, $"{job.Id}.result.json"), json);
            }
            catch { /* sidecar is best-effort — never fail the job over it */ }
        }

        /// <summary>Warning lines quoted under build_warnings in incomplete_reasons.</summary>
        internal const int MaxQuotedWarningLines = 5;

        /// <summary>Runs the working directory's resolved build command with a
        /// build-sized timeout. Never throws — a verification failure is data.</summary>
        private async Task<BuildVerification?> VerifyBuildAsync(AgentJob job)
        {
            // Explicit, not ShellRunner's default: a full rebuild is slower than the long budget.
            const int BuildTimeoutSeconds = 600;
            const int TailChars = 2_000;
            // Appended to every INCREMENTAL verification tail. The flag alone is not enough:
            // the misleading "N Warning(s)" is in the tail text, which is what a model reads
            // as prose, so the disclaimer has to sit with it rather than beside it.
            const string BuildWarningCountDisclaimer =
                "\n[verification] Warning count above is NOT verified - this build is incremental\n" +
                "and does not re-emit warnings for projects that were already up to date.\n" +
                "Only the error result and exit code are reliable.\n";

            // Test seam: when set, build verification goes through this instead of a
            // real build (hermetic — a temp dir resolves to no build command). Null =
            // production path. Mirrors the TestRunnerOverride pattern below.
            if (BuildRunnerOverride is { } buildOverride)
                return await buildOverride(job.WorkingDirectory, job.Cts.Token).ConfigureAwait(false);

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

            // H-32: a plain `dotnet build` is verified as a full rebuild (-t:Rebuild), so its
            // warning count is real. Anything else runs as resolved (VerificationBuild).
            VerificationBuild.Plan plan = VerificationBuild.For(command);
            command = plan.Command;

            try
            {
                var (output, exitCode) = BuildExecOverride is { } exec
                    ? await exec(command, job.WorkingDirectory).ConfigureAwait(false)
                    : await new ShellRunner(job.WorkingDirectory).ExecuteAsync(
                        command, CancellationToken.None, BuildTimeoutSeconds).ConfigureAwait(false);
                string tail = output.Length <= TailChars ? output : output.Substring(output.Length - TailChars);

                // Parsed from the FULL output: the summary line can fall outside the tail.
                int? warnings = plan.FullRebuild ? VerificationBuild.ParseWarningCount(output) : null;
                bool verified = warnings != null;

                // The warning_count_verified flag is a sibling field; the "N Warning(s)" lives
                // here in the tail, which is what a model actually reads as prose. Keep the
                // verdict WITH the text it qualifies, or a reader takes the count at face value
                // and never looks at the flag.
                tail += verified
                    ? $"\n[verification] Full rebuild: warning count is verified - {warnings} warning(s).\n"
                    : BuildWarningCountDisclaimer;
                return new BuildVerification
                {
                    Command = command,
                    ExitCode = exitCode,
                    OutputTail = tail,
                    WarningCountVerified = verified,
                    WarningCount = warnings,
                    WarningLines = verified && warnings > 0
                        ? VerificationBuild.ExtractWarningLines(output, MaxQuotedWarningLines)
                        : Array.Empty<string>(),
                };
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
            // Graceful shutdown — don't leave a stale busy signal. Only this manager's jobs:
            // another process's marker is its own business (H-66).
            List<string> ownIds;
            lock (_lock) ownIds = _jobs.Keys.ToList();
            foreach (string id in ownIds) ActiveJobMarkers.Clear(id);
            _shutdownCts.Dispose();
        }
    }
}
