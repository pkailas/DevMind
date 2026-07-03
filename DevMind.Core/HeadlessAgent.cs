// File: HeadlessAgent.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// One-shot headless agentic runner — the engine behind DevMind.McpServer's
// devmind_task_* tools (Claude Code delegating whole coding tasks to the local
// model). Wires LlmClient + BufferedAgenticHost (auto-approving, no console) +
// LoopDriver, seeds a single prompt, iterates the tool loop to completion or the
// depth cap, and returns a structured result: the final answer, the host's action
// journal (audit trail), and a transcript. Mirrors DevMind.Cli's RunTurnAsync with
// every console interaction removed. NOTHING here may write to Console — in the MCP
// process stdout is the JSON-RPC wire.

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

    /// <summary>Outcome of one headless agentic task.</summary>
    public sealed class HeadlessAgentResult
    {
        /// <summary>The model's final visible message (think tokens filtered).</summary>
        public string Answer { get; set; } = "";
        /// <summary>Audit trail of every mutating host action.</summary>
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

    /// <summary>One-shot headless agentic loop over the local model.</summary>
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
            "summary of what you changed and why.\n";

        internal const string NoCommitRule =
            "Do NOT run git commit, git push, or any other git command that rewrites history\n" +
            "or publishes changes — the delegating agent handles version control.\n";

        /// <summary>
        /// Runs one agentic task to completion. Never throws for task-level failures —
        /// errors are reported in the result. <paramref name="progress"/> receives every
        /// transcript chunk as it happens (model text and tool activity), for live tails.
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
            var result = new HeadlessAgentResult();
            var transcript = new StringBuilder();
            var transcriptLock = new object();
            var sw = Stopwatch.StartNew();

            void Emit(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                lock (transcriptLock) transcript.Append(text);
                try { progress?.Invoke(text); } catch { /* progress is best-effort */ }
            }

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var llmClient = new LlmClient(options);
            llmClient.Configure(endpointUrl, apiKey);

            var host = new BufferedAgenticHost(workingDirectory,
                cancelTurn: () => runCts.Cancel(),
                outputSink: (text, _) => Emit(text))
            {
                // Local models sometimes hallucinate absolute paths (/home/user/…);
                // headless writes are hard-confined to the working directory.
                RestrictWritesToWorkingDirectory = true,
            };
            var callbacks = new HeadlessLoopCallbacks(llmClient);
            var state = new LoopState();
            var driver = new LoopDriver(llmClient, host, callbacks, options, state);

            string resolvedBuildCommand = !string.IsNullOrWhiteSpace(buildCommand)
                ? buildCommand
                : BuildCommandResolver.Resolve(workingDirectory, _ => { });

            string BuildSystemPrompt()
            {
                string llmDirective = LoopHelpers.BuildToolUsePrompt(resolvedBuildCommand, projectNamespace: null);
                string combined = $"{options.SystemPrompt}\n\n{llmDirective}";

                string context = LoadAgentsContext(workingDirectory);
                if (!string.IsNullOrEmpty(context))
                    combined += $"\n\n--- Project Context (AGENTS.md) ---\n{context}\n---";

                try
                {
                    string memoryIndex = new MemoryManager(workingDirectory).LoadIndex();
                    if (!string.IsNullOrWhiteSpace(memoryIndex))
                        combined += $"\n\n--- Session Memory (MEMORY.md) ---\n{memoryIndex}\n---";
                }
                catch { /* memory is best-effort */ }

                if (!string.IsNullOrEmpty(host.TaskScratchpad))
                    combined += $"\n\n--- CURRENT SCRATCHPAD ---\n{host.TaskScratchpad}\n---";

                combined += HeadlessAddendum;
                if (!allowCommit)
                    combined += NoCommitRule;
                return combined;
            }

            state.ResetForUserTurn();
            host.ResetTaskContext();

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

                    llmClient.IncrementTurn();
                    host.CancellationToken = runCts.Token;

                    if (!firstIteration) thinkFilter.Reset();
                    firstIteration = false;
                    result.Iterations++;

                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var responseBuffer = new StringBuilder();

                    // LlmClient swallows OperationCanceledException without calling
                    // onComplete or onError — without this registration the await below
                    // would hang forever after cancellation.
                    using var cancelReg = runCts.Token.Register(() => tcs.TrySetCanceled(runCts.Token));

                    await llmClient.SendMessageAsync(
                        currentPrompt,
                        onToken: token =>
                        {
                            string visible = thinkFilter.Process(token, showThinking: false, out _);
                            if (string.IsNullOrEmpty(visible)) return;
                            responseBuffer.Append(visible);
                            Emit(visible);
                        },
                        onComplete: () => tcs.TrySetResult(true),
                        onError: ex => tcs.TrySetException(ex),
                        deferCompression: state.ShellLoopPending,
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
                        Emit($"\n[HEADLESS ERROR] {ex.Message}\n");
                        break;
                    }

                    lastResponse = responseBuffer.ToString();

                    LoopIterationResult iter;
                    try
                    {
                        iter = await driver.ProcessIterationAsync(currentPrompt, lastResponse,
                            resolvedBuildCommand, runCts.Token).ConfigureAwait(false);
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
                    currentPrompt = iter.NextContextualMessage ?? callbacks.GetInputText();
                    callbacks.SetInputText(string.Empty);
                }
            }
            finally
            {
                sw.Stop();
            }

            result.Answer = lastResponse.Trim();
            result.Actions = host.GetActions();
            result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
            result.HitDepthCap = options.AgenticLoopMaxDepth > 0
                && result.Iterations >= options.AgenticLoopMaxDepth;

            if (!string.IsNullOrEmpty(transcriptPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath));
                    lock (transcriptLock)
                        File.WriteAllText(transcriptPath, transcript.ToString());
                    result.TranscriptPath = transcriptPath;
                }
                catch { /* transcript is best-effort — never fail the task over it */ }
            }

            return result;
        }

        /// <summary>AGENTS.md discovery: working directory first, then git root.</summary>
        private static string LoadAgentsContext(string workingDir)
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
}
