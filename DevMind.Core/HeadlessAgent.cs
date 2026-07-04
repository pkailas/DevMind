// File: HeadlessAgent.cs  v2.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Headless agentic runner — the engine behind DevMind.McpServer's devmind_task_*
// tools (Claude Code / Claude Desktop delegating whole coding tasks to the local
// model). v2 splits the former one-shot RunAsync into a resumable HeadlessSession:
// the conversation (LlmClient history, host caches, scratchpad) survives between
// turns, so devmind_task_continue can reopen a finished job with full context —
// a bare "continue" picks up exactly where a depth-capped run stopped, no
// re-briefing needed. HeadlessAgent.RunAsync remains as the one-shot wrapper
// (create session → one turn → dispose). NOTHING here may write to Console — in
// the MCP process stdout is the JSON-RPC wire.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>Settable <see cref="ILlmOptions"/> for headless runs. Defaults mirror
    /// the CLI's, except AgenticLoopMaxDepth (25 — a delegated task gets room to work).</summary>
    public sealed class HeadlessOptions : ILlmOptions
    {
        public string SystemPrompt             { get; set; } = "You are a helpful coding assistant. Be concise and precise.";
        public string ModelName                { get; set; } = "";
        public int    RequestTimeoutMinutes    { get; set; } = 10;
        public int    FirstTokenTimeoutMinutes { get; set; } = 5;
        public bool   ShowDebugOutput          { get; set; } = false;
        public bool   ShowContextBudget        { get; set; } = false;
        public bool   ShowLlmThinking          { get; set; } = false;
        public ContextEvictionMode ContextEviction { get; set; } = ContextEvictionMode.Balanced;
        public int    ManualContextSize        { get; set; } = 0;
        public LlmServerType ServerType        { get; set; } = LlmServerType.LlamaServer;
        public string CustomContextEndpoint    { get; set; } = "";
        public int    MicroCompactThreshold    { get; set; } = 85;
        public bool   MicroCompactSummarize    { get; set; } = true;
        public bool   MicroCompactBrainwash    { get; set; } = false;
        public bool   AlwaysConfirmPatch       { get; set; } = false;
        public int    AgenticLoopMaxDepth      { get; set; } = 25;
        public int    AgenticContextLimitPercent { get; set; } = 78;
    }

    /// <summary>Outcome of one headless agentic turn (task or continuation).</summary>
    public sealed class HeadlessAgentResult
    {
        /// <summary>The model's final visible message (think tokens filtered).</summary>
        public string Answer { get; set; } = "";
        /// <summary>Audit trail of every mutating host action THIS turn.</summary>
        public IReadOnlyList<HostAction> Actions { get; set; } = Array.Empty<HostAction>();
        public int Iterations { get; set; }
        public double ElapsedSeconds { get; set; }
        public bool HitDepthCap { get; set; }
        public bool Cancelled { get; set; }
        /// <summary>Non-null when the run failed with an error (endpoint down, etc.).</summary>
        public string Error { get; set; }
        /// <summary>Full transcript file (model output + tool activity), when requested.</summary>
        public string TranscriptPath { get; set; }
    }

    /// <summary>
    /// A resumable headless agent conversation. Construct once per delegated task;
    /// call <see cref="RunTurnAsync"/> for the initial prompt and again for each
    /// continuation — LlmClient history, file caches, and the scratchpad persist
    /// across turns (LlmClient's compaction machinery manages the window). Dispose
    /// when the conversation is retired.
    /// </summary>
    public sealed class HeadlessSession : IDisposable
    {
        private readonly HeadlessOptions _options;
        private readonly LlmClient _llmClient;
        private readonly BufferedAgenticHost _host;
        private readonly HeadlessLoopCallbacks _callbacks;
        private readonly LoopState _state;
        private readonly LoopDriver _driver;
        private readonly string _workingDirectory;
        private readonly string _resolvedBuildCommand;
        private readonly bool _allowCommit;

        // The CTS of the turn currently executing — the host's cancel-turn callback
        // (patch preview 'q', etc.) must always target the ACTIVE turn.
        private CancellationTokenSource _currentTurnCts;
        private bool _disposed;

        /// <summary>UTC time the last turn finished — idle-expiry input for session owners.</summary>
        public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

        public HeadlessSession(
            HeadlessOptions options,
            string endpointUrl,
            string apiKey,
            string workingDirectory,
            string buildCommand = null,
            bool allowCommit = false)
        {
            _options = options;
            _workingDirectory = workingDirectory;
            _allowCommit = allowCommit;

            _llmClient = new LlmClient(options);
            _llmClient.Configure(endpointUrl, apiKey);

            _host = new BufferedAgenticHost(workingDirectory,
                cancelTurn: () => _currentTurnCts?.Cancel(),
                outputSink: (text, _) => EmitToTurn(text))
            {
                // Local models sometimes hallucinate absolute paths (/home/user/…);
                // headless writes are hard-confined to the working directory.
                RestrictWritesToWorkingDirectory = true,
            };
            _callbacks = new HeadlessLoopCallbacks(_llmClient);
            _state = new LoopState();
            _driver = new LoopDriver(_llmClient, _host, _callbacks, options, _state);

            _resolvedBuildCommand = !string.IsNullOrWhiteSpace(buildCommand)
                ? buildCommand
                : BuildCommandResolver.Resolve(workingDirectory, _ => { });
        }

        // Per-turn transcript sink (reset each turn so each job's transcript file
        // holds exactly that turn's activity).
        private StringBuilder _turnTranscript;
        private readonly object _transcriptLock = new object();
        private Action<string> _turnProgress;

        private void EmitToTurn(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_transcriptLock) _turnTranscript?.Append(text);
            try { _turnProgress?.Invoke(text); } catch { /* progress is best-effort */ }
        }

        /// <summary>
        /// Runs one agentic turn: the initial task prompt, or a continuation
        /// ("continue", a refinement, a follow-up). Never throws for task-level
        /// failures — errors are reported in the result. Turns must not overlap.
        /// </summary>
        public async Task<HeadlessAgentResult> RunTurnAsync(
            string prompt,
            string transcriptPath = null,
            Action<string> progress = null,
            CancellationToken ct = default)
        {
            var result = new HeadlessAgentResult();
            var sw = Stopwatch.StartNew();

            lock (_transcriptLock) _turnTranscript = new StringBuilder();
            _turnProgress = progress;

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentTurnCts = runCts;

            // Per-turn resets: write guard set, iteration depth. Conversation history,
            // file caches, and the scratchpad deliberately persist across turns.
            _state.ResetForUserTurn();
            _host.ResetTaskContext();
            _host.ClearActions(); // result.Actions is THIS turn's journal

            var thinkFilter = new ThinkFilter();
            string currentPrompt = prompt;
            bool firstIteration = true;
            string lastResponse = "";

            try
            {
                while (true)
                {
                    if (runCts.Token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }

                    _llmClient.IncrementTurn();
                    _host.CancellationToken = runCts.Token;

                    if (!firstIteration) thinkFilter.Reset();
                    firstIteration = false;
                    result.Iterations++;

                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var responseBuffer = new StringBuilder();

                    // LlmClient swallows OperationCanceledException without calling
                    // onComplete or onError — without this registration the await below
                    // would hang forever after cancellation.
                    using var cancelReg = runCts.Token.Register(() => tcs.TrySetCanceled(runCts.Token));

                    await _llmClient.SendMessageAsync(
                        currentPrompt,
                        onToken: token =>
                        {
                            string visible = thinkFilter.Process(token, showThinking: false, out _);
                            if (string.IsNullOrEmpty(visible)) return;
                            responseBuffer.Append(visible);
                            EmitToTurn(visible);
                        },
                        onComplete: () => tcs.TrySetResult(true),
                        onError: ex => tcs.TrySetException(ex),
                        deferCompression: _state.ShellLoopPending,
                        combinedSystemPrompt: BuildSystemPrompt(),
                        cancellationToken: runCts.Token).ConfigureAwait(false);

                    try
                    {
                        await tcs.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        result.Cancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        result.Error = ex.Message;
                        EmitToTurn($"\n[HEADLESS ERROR] {ex.Message}\n");
                        break;
                    }

                    lastResponse = responseBuffer.ToString();

                    LoopIterationResult iter;
                    try
                    {
                        iter = await _driver.ProcessIterationAsync(currentPrompt, lastResponse,
                            _resolvedBuildCommand, runCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        result.Cancelled = true;
                        break;
                    }

                    if (iter.Kind == LoopIterationKind.Terminal || iter.Kind == LoopIterationKind.Cancelled)
                    {
                        result.Cancelled |= iter.Kind == LoopIterationKind.Cancelled;

                        // A run finished via task_done carries the real answer in the
                        // tool call's summary — the visible prose of that turn is often
                        // empty (tool-call-only response).
                        string doneSummary = iter.Outcome?.Blocks?
                            .FirstOrDefault(b => b.Type == BlockType.Done)?.Content;
                        if (!string.IsNullOrWhiteSpace(doneSummary))
                            lastResponse = doneSummary;
                        break;
                    }

                    // ShouldReTrigger — feed the next contextual message (tool results) back in.
                    if (runCts.Token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }
                    currentPrompt = iter.NextContextualMessage ?? _callbacks.GetInputText();
                    _callbacks.SetInputText(string.Empty);
                }
            }
            finally
            {
                sw.Stop();
                _currentTurnCts = null;
                _turnProgress = null;
                LastActivityUtc = DateTime.UtcNow;
            }

            result.Answer = HeadlessAgent.SanitizeAnswer(lastResponse);
            result.Actions = _host.GetActions();
            result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
            result.HitDepthCap = _options.AgenticLoopMaxDepth > 0
                && result.Iterations >= _options.AgenticLoopMaxDepth;

            // A depth-cap exit truncates the model mid-thought; its last message can
            // claim failure that already resolved (or success that didn't). Field
            // evidence: "build still failing" at the cap of a job whose tree built
            // clean. Mark the answer itself — hit_depth_cap alone was overlooked.
            if (result.HitDepthCap)
            {
                result.Answer =
                    $"[INCOMPLETE — iteration cap ({_options.AgenticLoopMaxDepth}) reached; the text below is the " +
                    "agent's LAST message, not a completion summary. It may be stale or cut off. Judge the actual " +
                    "state from the action journal and build_verification, or send devmind_task_continue to resume " +
                    "this conversation where it left off.]\n\n"
                    + result.Answer;
            }

            if (!string.IsNullOrEmpty(transcriptPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath));
                    lock (_transcriptLock)
                        File.WriteAllText(transcriptPath, _turnTranscript.ToString());
                    result.TranscriptPath = transcriptPath;
                }
                catch { /* transcript is best-effort — never fail the task over it */ }
            }

            return result;
        }

        /// <summary>Adjusts the per-turn iteration cap for a continuation.</summary>
        public void SetMaxDepth(int maxDepth) => _options.AgenticLoopMaxDepth = maxDepth;

        private string BuildSystemPrompt()
        {
            string llmDirective = LoopHelpers.BuildToolUsePrompt(_resolvedBuildCommand, projectNamespace: null);
            string combined = $"{_options.SystemPrompt}\n\n{llmDirective}";

            string context = HeadlessAgent.LoadAgentsContext(_workingDirectory);
            if (!string.IsNullOrEmpty(context))
                combined += $"\n\n--- Project Context (AGENTS.md) ---\n{context}\n---";

            try
            {
                string memoryIndex = new MemoryManager(_workingDirectory).LoadIndex();
                if (!string.IsNullOrWhiteSpace(memoryIndex))
                    combined += $"\n\n--- Session Memory (MEMORY.md) ---\n{memoryIndex}\n---";
            }
            catch { /* memory is best-effort */ }

            if (!string.IsNullOrEmpty(_host.TaskScratchpad))
                combined += $"\n\n--- CURRENT SCRATCHPAD ---\n{_host.TaskScratchpad}\n---";

            combined += HeadlessAgent.HeadlessAddendum;
            if (!_allowCommit)
                combined += HeadlessAgent.NoCommitRule;
            return combined;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _currentTurnCts?.Cancel(); } catch { }
            _llmClient.Dispose();
        }

        /// <summary>No-op ILoopCallbacks for headless runs: no status surface, no input
        /// box; only the re-trigger input hand-off and context metrics are real.</summary>
        private sealed class HeadlessLoopCallbacks : ILoopCallbacks
        {
            private readonly ILlmClient _llmClient;
            private string _pendingInput = string.Empty;

            public HeadlessLoopCallbacks(ILlmClient llmClient) => _llmClient = llmClient;

            public void AppendNewLine() { }
            public void SetStatus(string text) { }
            public void SetContextIndicator(string text) { }
            public void SetInputText(string text) => _pendingInput = text ?? string.Empty;

            public string GetInputText()
            {
                string value = _pendingInput;
                _pendingInput = string.Empty;
                return value;
            }

            public void FocusInput() { }
            public void SetInputEnabled(bool enabled) { }
            public void StartThinkingTimer(int depth, int maxDepth) { }
            public void StopThinkingTimer() { }

            public (int used, int total) GetContextMetrics()
            {
                int used = _llmClient.LastContextUsed > 0
                    ? _llmClient.LastContextUsed
                    : _llmClient.EstimateHistoryTokens();
                return (used, _llmClient.ServerContextSize);
            }
        }
    }

    /// <summary>One-shot convenience wrapper over <see cref="HeadlessSession"/>.</summary>
    public static class HeadlessAgent
    {
        /// <summary>
        /// Behavioral rails appended to every headless system prompt. The commit rule is
        /// conditional (see <c>allowCommit</c>); the rest keeps a delegated agent inside
        /// its sandbox and prevents it stalling on questions nobody will answer.
        /// </summary>
        internal const string HeadlessAddendum =
            "\n\n--- HEADLESS DELEGATION RULES ---\n" +
            "You are running unattended on behalf of another agent. There is no human to ask:\n" +
            "never ask clarifying questions — make the most reasonable choice and note the\n" +
            "assumption in your final answer. Operate ONLY within the working directory, and\n" +
            "always refer to files by paths RELATIVE to it — absolute paths outside the\n" +
            "working directory are blocked. Do NOT spend iterations verifying the full build\n" +
            "or wrestling shell timeouts to do so — the job runner builds the project itself\n" +
            "after you finish and reports the result to the caller; use the run_build tool\n" +
            "only for a quick compile check when you genuinely need one mid-task. Do not\n" +
            "write scratch/output files into the repository — describe results in your\n" +
            "answer instead. When the task is complete, call task_done with a concise\n" +
            "summary of what you changed and why.\n" +
            "\n" +
            "TypeScript discipline: after EVERY write to a .ts or .tsx file (create_file,\n" +
            "patch_file, or append_file), immediately call get_diagnostics on that file and\n" +
            "fix all reported errors before doing anything else. A type error caught at\n" +
            "write time costs one iteration; the same error discovered at build time costs\n" +
            "many (wrong import module for a type, implicit-any callbacks, and rules-of-hooks\n" +
            "violations are all visible to get_diagnostics the moment the file is written).\n" +
            "\n" +
            "Repo knowledge first: the Session Memory index above lists saved topics. Before\n" +
            "editing code — especially frontend code — recall_memory any topic matching this\n" +
            "repo or its frameworks (conventions, patterns, gotchas), or search_memory when\n" +
            "the right topic isn't obvious. For framework reference questions (React hooks\n" +
            "rules, TypeScript patterns), use query_library before guessing from memory.\n";

        internal const string NoCommitRule =
            "Do NOT run git commit, git push, or any other git command that rewrites history\n" +
            "or publishes changes — the delegating agent handles version control.\n";

        /// <summary>
        /// Runs one agentic task to completion in a throwaway session. Never throws for
        /// task-level failures — errors are reported in the result.
        /// </summary>
        public static async Task<HeadlessAgentResult> RunAsync(
            string prompt,
            HeadlessOptions options,
            string endpointUrl,
            string apiKey,
            string workingDirectory,
            string buildCommand = null,
            bool allowCommit = false,
            string transcriptPath = null,
            Action<string> progress = null,
            CancellationToken ct = default)
        {
            using var session = new HeadlessSession(options, endpointUrl, apiKey,
                workingDirectory, buildCommand, allowCommit);
            return await session.RunTurnAsync(prompt, transcriptPath, progress, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Strips control tokens the local model occasionally leaks into visible text
        /// (raw &lt;/think&gt; tags and &lt;tool_call&gt;/&lt;function=...&gt; syntax that
        /// escaped the server-side parser) so they never reach the returned answer.
        /// </summary>
        internal static string SanitizeAnswer(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string cleaned = System.Text.RegularExpressions.Regex.Replace(
                text, @"<tool_call>.*?(</tool_call>|$)", "",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned, @"</?think>|<function=[^>]*>|</function>", "");
            return cleaned.Trim();
        }

        /// <summary>AGENTS.md discovery: working directory first, then git root.</summary>
        internal static string LoadAgentsContext(string workingDir)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;
            var searchDirs = new List<string> { workingDir };
            string gitRoot = ContextEngine.FindGitRoot(workingDir);
            if (!string.IsNullOrEmpty(gitRoot) &&
                !string.Equals(gitRoot, workingDir, StringComparison.OrdinalIgnoreCase))
                searchDirs.Add(gitRoot);

            foreach (string dir in searchDirs)
            {
                string path = Path.Combine(dir, "AGENTS.md");
                if (!File.Exists(path)) continue;
                try { return File.ReadAllText(path); }
                catch { }
            }
            return null;
        }
    }
}
