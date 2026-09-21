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
        /// <summary>
        /// DISPLAY switch: whether the model's think blocks stream into the transcript.
        /// This does NOT control generation — that is <see cref="ShowLlmThinking"/>
        /// (the enable_thinking template switch). null = no explicit per-session setting:
        /// fall back to the DEVMIND_TASK_SHOW_THINKING environment variable. A non-null
        /// value wins over the environment variable.
        /// </summary>
        public bool?  StreamThinkingToTranscript { get; set; }
        /// <summary>
        /// Whether the JOB HARNESS runs the test suite over the solution after the agent
        /// stops (MCP verify_tests). Decides which test-verification regime is written
        /// into the headless addendum: harness-on tells the agent NOT to run the full
        /// suite itself (the harness numbers are the ones reported); harness-off tells it
        /// to run the full suite and report what it actually observed. Prompt-only — no
        /// host guard to mirror. Default false: every non-harness path (TUI/CLI/one-shot
        /// RunAsync) has no harness, and a forgotten flag fails loud (one wasted suite
        /// run) rather than silent (an unverified change shipped as "done").
        /// </summary>
        public bool   HarnessVerifiesTests { get; set; } = false;
        public ContextEvictionMode ContextEviction { get; set; } = ContextEvictionMode.Balanced;
        public int    ManualContextSize        { get; set; } = 0;
        public LlmServerType ServerType        { get; set; } = LlmServerType.LlamaServer;
        public string CustomContextEndpoint    { get; set; } = "";
        public int    MicroCompactThreshold    { get; set; } = 85;
        public int    NearlineIngestThresholdChars { get; set; } = 8_000;
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
        /// <summary>True when the agent called ask_caller: the task is paused on
        /// questions only the caller can answer. Answer contains the questions and
        /// what was already tried; resume via the conversation with the answers.</summary>
        public bool NeedsInput { get; set; }
        /// <summary>True when the loop's thrash guard stopped the turn: the same
        /// failure kept recurring even after an injected research directive.</summary>
        public bool ThrashStopped { get; set; }
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
        // Optional override for the global system-prompt file path. Null = use the
        // production default (%APPDATA%\devmind\system-prompt.md via SystemPromptFile.Path).
        private readonly string _promptFilePath;
        // Mutable: re-synced by SetNoExecute on a reused (continuation) session.
        private bool _noExecute;

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
            bool allowCommit = false,
            bool noExecute = false,
            string sessionId = null,
            string promptFilePath = null)
        {
            _options = options;
            _workingDirectory = workingDirectory;
            _allowCommit = allowCommit;
            _noExecute = noExecute;
            _promptFilePath = promptFilePath;

            _llmClient = new LlmClient(options);
            _llmClient.Configure(endpointUrl, apiKey);

            _host = new BufferedAgenticHost(workingDirectory,
                cancelTurn: () => _currentTurnCts?.Cancel(),
                outputSink: (text, _) => EmitToTurn(text))
            {
                // Local models sometimes hallucinate absolute paths (/home/user/…);
                // headless writes are hard-confined to the working directory.
                RestrictWritesToWorkingDirectory = true,
                // Caller-imposed no-execution restriction (default false — interactive
                // and ordinary jobs are untouched). Enforced at the host's three spawn
                // surfaces; the system prompt below steers the model away first.
                NoExecute = noExecute,
                NearlineCache = _llmClient.NearlineCache, // for the recall_cache tool
            };
            _callbacks = new HeadlessLoopCallbacks(_llmClient);
            _state = new LoopState();

            // Training capture for delegated (headless) runs. Previously only the TUI
            // passed a logger, so every devmind_task_start job — the long unsupervised
            // runs where the local model's failure modes actually show up — was invisible
            // to the corpus. Keyed on the job id so each task lands in its own file with
            // its own terminal state, which makes per-task analysis trivial.
            //
            // Divergence from the TUI on purpose: the TUI fails loud on a bad training
            // config because a human is right there to fix it. Killing a background job
            // over logging is disproportionate, so this degrades to a warning on stderr
            // and runs unlogged — visible, not silent.
            ITrainingLogger trainingLogger = null;
            // TrainingCapture.Disabled is the test-run opt-out: test assemblies set it once
            // via [ModuleInitializer], and no test can then write into the operator's real
            // configured corpus folder by picking up the ambient devmind.json here.
            if (!TrainingCapture.Disabled && !string.IsNullOrWhiteSpace(sessionId))
            {
                try
                {
                    var cfg = TuiConfig.Load();
                    if (cfg.TrainingLogEnabled && !string.IsNullOrWhiteSpace(cfg.TrainingLogFolder))
                        trainingLogger = new JsonlTrainingLogger(
                            () => sessionId, true, cfg.TrainingLogFolder);
                    else if (cfg.TrainingLogEnabled)
                        Console.Error.WriteLine(
                            "[HeadlessSession] trainingLogEnabled is true but trainingLogFolder " +
                            "is blank in devmind.json — this job will not be captured.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[HeadlessSession] training capture disabled for this job: {ex.Message}");
                }
            }

            _driver = new LoopDriver(_llmClient, _host, _callbacks, options, _state, trainingLogger);

            _resolvedBuildCommand = !string.IsNullOrWhiteSpace(buildCommand)
                ? buildCommand
                : BuildCommandResolver.Resolve(workingDirectory, _ => { });
        }

        // Per-turn transcript sink (reset each turn so each job's transcript file
        // holds exactly that turn's activity). The writer streams to the transcript
        // file LIVE (FileShare.Read) so an operator can tail the file while the job
        // runs — before this, the file appeared only after the turn finished, giving
        // zero mid-run visibility. The StringBuilder stays as the fallback source
        // when the writer could not be opened.
        private StringBuilder _turnTranscript;
        private StreamWriter _turnTranscriptWriter;
        private readonly object _transcriptLock = new object();
        private Action<string> _turnProgress;

        // Steer mailbox (devmind_task_steer): a single pending steer, last-write-wins.
        // Enqueued on an MCP request thread, drained on the worker thread at the top of
        // each iteration — guarded by one lock, matching the AgentJob._tail/_tailLock pattern.
        private readonly object _steerLock = new object();
        private SteerMessage _pendingSteer;
        // True for the duration of a turn (set at turn start, cleared atomically with the
        // final drain at turn end). EnqueueSteer refuses when false — this is what closes
        // the enqueue-after-turn-end window: there is no state where a turn is over but a
        // steer would still be accepted into a mailbox nothing will ever drain.
        private bool _turnInProgress;

        private sealed class SteerMessage
        {
            public required string Message { get; init; }
            public SteerMode Mode { get; init; }
        }

        private void EmitToTurn(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_transcriptLock)
            {
                _turnTranscript?.Append(text);
                try { _turnTranscriptWriter?.Write(text); } catch { /* live tail is best-effort */ }
            }
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

            lock (_transcriptLock)
            {
                _turnTranscript = new StringBuilder();
                _turnTranscriptWriter = null;
                if (!string.IsNullOrEmpty(transcriptPath))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath));
                        var stream = new FileStream(transcriptPath,
                            FileMode.Create, FileAccess.Write, FileShare.Read);
                        _turnTranscriptWriter = new StreamWriter(stream) { AutoFlush = true };
                    }
                    catch { _turnTranscriptWriter = null; } // fall back to end-of-turn write
                }
            }
            _turnProgress = progress;

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentTurnCts = runCts;

            // Per-turn resets: write guard set, iteration depth. Conversation history,
            // file caches, and the scratchpad deliberately persist across turns.
            _state.ResetForUserTurn();
            _host.ResetTaskContext();
            _host.ClearActions(); // result.Actions is THIS turn's journal

            // Turn clock: ONE increment per user turn, at the turn boundary — NOT inside the
            // agentic loop below. The loop re-triggers many iterations for a single turn and
            // they SHARE this turn (the contract at LlmClient.IncrementTurn: "once per
            // user-initiated send, not per agentic resubmit"). A per-iteration increment ran
            // the context-aging clock ~an order of magnitude faster than dropAge was tuned
            // for, so a long job age-evicted its own middle while the window was nearly empty.
            // Deliberately here with the resets, OUTSIDE the try below (BeginSteerTurn is the
            // first statement INSIDE it) — a once-per-call advance, not a per-iteration one.
            _llmClient.IncrementTurn();

            var thinkFilter = new ThinkFilter();
            string currentPrompt = prompt;
            bool firstIteration = true;
            bool forceToolChoiceRequired = false; // Layer 2 narration-retry flag (mirrors TUI Program.cs)
            string lastResponse = "";
            string lastTerminalReason = null;
            // Last response that contained real prose (status lines like [CONTEXT]/[TOOL_USE]
            // stripped). A tool-call-only final turn leaves lastResponse as pure status noise,
            // which is what a thrash-stopped job used to return as its "answer" (job-1384).
            string lastProse = "";
            string lastRepeatedFailure = null;


            // DEVMIND_TASK_SHOW_THINKING=1 streams the model's think tokens into the
            // transcript (dm-watch shows it reasoning live). Off by default: think
            // blocks are filtered, so instead a heartbeat line lands every
            // HeartbeatSeconds of visible silence — long unbounded reasoning otherwise
            // looks identical to a wedged request from the outside.
            //
            // The per-session StreamThinkingToTranscript (set by the job runner from
            // the job's show_thinking parameter) travels with the request and wins
            // when supplied — it replaces the invisible process-environment chain.
            // null (no explicit setting) keeps the legacy behaviour: the env var is
            // read here per turn, exactly as before, so env-based setups keep working.
            bool showThinking = _options.StreamThinkingToTranscript
                ?? Environment.GetEnvironmentVariable("DEVMIND_TASK_SHOW_THINKING") is string showThinkingRaw
                    && (showThinkingRaw == "1"
                        || string.Equals(showThinkingRaw, "true", StringComparison.OrdinalIgnoreCase));
            const int HeartbeatSeconds = 30;

            try
            {
                // Open the steer window (devmind_task_steer), first thing in the try so the
                // finally's atomic close always balances it on every exit path. If a steer is
                // somehow already pending, the previous turn's close should have taken it —
                // record it as unconsumed (self-healing, honest: never silently drop a
                // caller's message) and start this turn clean.
                var staleSteer = BeginSteerTurn();
                if (staleSteer != null)
                {
                    _host.RecordSteer(staleSteer.Message, staleSteer.Mode, SteerDisposition.Unconsumed,
                        "stale pending steer at turn start — carried over from a prior turn");
                    EmitToTurn($"[STEER] unconsumed — stale steer at turn start. {staleSteer.Message}\n");
                }

                while (true)
                {
                    if (runCts.Token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }

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

                    long thinkChars = 0;
                    DateTime generationStartUtc = DateTime.UtcNow;
                    DateTime lastTranscriptEmitUtc = DateTime.UtcNow;

                    // Fold a pending steer (devmind_task_steer) into this iteration's prompt,
                    // before it is sent — its own delimited block, never glued into the driver's
                    // synthetic re-trigger. An override on the last iteration is refused (Steer).
                    // No-op when no steer is pending.
                    DrainSteerIntoPrompt(ref currentPrompt);

                    await _llmClient.SendMessageAsync(
                        currentPrompt,
                        forceToolChoiceRequired: forceToolChoiceRequired,
                        onToken: token =>
                        {
                            string visible = thinkFilter.Process(token, showThinking, out string thinkText);
                            if (showThinking && !string.IsNullOrEmpty(thinkText))
                            {
                                EmitToTurn(thinkText);
                                lastTranscriptEmitUtc = DateTime.UtcNow;
                            }
                            if (!string.IsNullOrEmpty(visible))
                            {
                                responseBuffer.Append(visible);
                                EmitToTurn(visible);
                                lastTranscriptEmitUtc = DateTime.UtcNow;
                                return;
                            }

                            // Nothing visible this chunk — the model is inside a think
                            // block. Heartbeat so an operator tailing the transcript can
                            // tell "reasoning" from "wedged" (transcript-only breadcrumb;
                            // never part of the response).
                            thinkChars += token.Length;
                            DateTime now = DateTime.UtcNow;
                            if (!showThinking && (now - lastTranscriptEmitUtc).TotalSeconds >= HeartbeatSeconds)
                            {
                                lastTranscriptEmitUtc = now;
                                EmitToTurn(
                                    $"[LLM] reasoning… ~{thinkChars / 4:N0} think tokens, " +
                                    $"{(int)(now - generationStartUtc).TotalSeconds}s into this response\n");
                            }
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
                    {
                        string prose = HeadlessAgent.StripStatusLines(HeadlessAgent.SanitizeAnswer(lastResponse));
                        if (!string.IsNullOrWhiteSpace(prose)) lastProse = prose;
                    }

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
                        // empty (tool-call-only response). ask_caller likewise carries
                        // the questions in its block content.
                        string needsInputContent = iter.Outcome?.Blocks?
                            .FirstOrDefault(b => b.Type == BlockType.NeedsInput)?.Content;
                        string doneSummary = iter.Outcome?.Blocks?
                            .FirstOrDefault(b => b.Type == BlockType.Done)?.Content;
                        if (!string.IsNullOrWhiteSpace(needsInputContent))
                        {
                            result.NeedsInput = true;
                            lastResponse = needsInputContent;
                        }
                        else if (!string.IsNullOrWhiteSpace(doneSummary))
                        {
                            lastResponse = doneSummary;
                        }
                        lastTerminalReason = iter.TerminalReason;
                        if (iter.TerminalReason == "needs_input")
                            result.NeedsInput = true;
                        else if (iter.TerminalReason is "thrashing" or "consecutive_errors")
                        {
                            result.ThrashStopped = true;
                            lastRepeatedFailure = iter.Result?.Errors?.LastOrDefault();
                        }
                        break;
                    }

                    // ShouldReTrigger — feed the next contextual message (tool results) back in.
                    if (runCts.Token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }
                    currentPrompt = iter.NextContextualMessage ?? _callbacks.GetInputText();
                    // Assign (not OR) so a later non-forcing re-trigger clears the flag —
                    // it must not latch across iterations.
                    forceToolChoiceRequired = iter.ForceToolChoiceRequired;
                    _callbacks.SetInputText(string.Empty);
                }
            }
            finally
            {
                sw.Stop();
                _currentTurnCts = null;
                _turnProgress = null;
                LastActivityUtc = DateTime.UtcNow;

                // Close the steer window: atomically take the final pending steer AND clear
                // the in-progress flag (one lock acquisition — see TakePendingSteerAndEndTurn).
                // Done in the FINALLY so the flag is cleared on EVERY exit path — a clean
                // break, a cancellation, or an exception escaping the loop (ProcessIteration
                // only catches OperationCanceledException). Leaving the flag set on an error
                // path would keep EnqueueSteer accepting into a mailbox nothing will drain —
                // the exact bug this closes. A steer still pending here was accepted but never
                // folded (the turn ended first) — record it as unconsumed. A steer enqueued
                // AFTER the flag clears is refused by EnqueueSteer, so no message is silently
                // lost and none leaks into a later turn (a continuation).
                var unconsumed = TakePendingSteerAndEndTurn();
                if (unconsumed != null)
                {
                    _host.RecordSteer(unconsumed.Message, unconsumed.Mode, SteerDisposition.Unconsumed,
                        "turn ended before the next iteration boundary");
                    EmitToTurn($"[STEER] unconsumed — the turn ended before the next iteration boundary. {unconsumed.Message}\n");
                }
            }

            result.Answer = HeadlessAgent.SanitizeAnswer(lastResponse);
            result.Actions = _host.GetActions();
            result.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
            // HitDepthCap means the loop stopped BECAUSE of the cap (LoopDriver's
            // depth-cap terminal), not that the counter happened to reach it. Field
            // lesson: a run that finished with a clean task_done ON the cap boundary
            // was mislabeled stopped_incomplete by the old iterations>=max check.
            result.HitDepthCap = lastTerminalReason == "depth_cap";

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
            else if (result.ThrashStopped)
            {
                // The final turn of a thrash-stopped run is almost always a tool-call-only
                // retry, so lastResponse is status noise. Return the last real prose plus the
                // failure that kept repeating, and mark it so the caller doesn't read it as a
                // completion summary.
                string reason = lastTerminalReason == "consecutive_errors" ? "consecutive tool errors" : "the same failure repeated";
                var sb = new StringBuilder();
                sb.Append($"[INCOMPLETE — stopped by the harness: {reason}. The work is NOT trustworthy as-is. ")
                  .Append("Below is the agent's last real message and the failure it kept hitting — not a completion summary. ")
                  .Append("Re-brief around that failure (or devmind_task_continue with specific guidance).]");
                if (!string.IsNullOrWhiteSpace(lastRepeatedFailure))
                    sb.Append("\n\nRepeating failure:\n").Append(lastRepeatedFailure.Trim());
                if (!string.IsNullOrWhiteSpace(lastProse))
                    sb.Append("\n\nAgent's last message:\n").Append(lastProse);
                result.Answer = sb.ToString();
            }

            if (!string.IsNullOrEmpty(transcriptPath))
            {
                lock (_transcriptLock)
                {
                    if (_turnTranscriptWriter != null)
                    {
                        // Streamed live throughout the turn — just close it out.
                        try { _turnTranscriptWriter.Dispose(); result.TranscriptPath = transcriptPath; }
                        catch { }
                        _turnTranscriptWriter = null;
                    }
                    else
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath));
                            File.WriteAllText(transcriptPath, _turnTranscript.ToString());
                            result.TranscriptPath = transcriptPath;
                        }
                        catch { /* transcript is best-effort — never fail the task over it */ }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Queues a steer into this session's running turn — single slot, last-write-wins
        /// (the devmind_task_steer mailbox). The loop folds it into the prompt at the next
        /// iteration boundary, before the next LLM request. A new steer supersedes any
        /// un-consumed one; the superseded steer is recorded in the journal (never silently
        /// dropped) and its mode is returned so a downgrade onto a pending override is
        /// visible to the caller. REFUSED when no turn is in progress (the enqueue-after-
        /// turn-end window): accepted means a live turn will drain it. Safe to call from
        /// the MCP request thread while the turn runs on the worker thread.
        /// </summary>
        public SteerEnqueueResult EnqueueSteer(string message, SteerMode mode)
        {
            lock (_steerLock)
            {
                // The fix for the enqueue-after-turn-end race. A steer enqueued once the
                // turn is over would otherwise sit in _pendingSteer, un-recorded and
                // un-drained — silently lost (no continuation) or drained into the NEXT
                // turn (a continuation reuses this session), attributed to a job nobody
                // steered. There is no "maybe": accepted means a live turn will drain it.
                if (!_turnInProgress)
                    return new SteerEnqueueResult { Accepted = false };

                bool superseded = _pendingSteer != null;
                SteerMode? supersededMode = superseded ? (SteerMode?)_pendingSteer!.Mode : null;
                if (superseded)
                {
                    _host.RecordSteer(_pendingSteer!.Message, _pendingSteer.Mode, SteerDisposition.Unconsumed,
                        "superseded by a newer steer before it was consumed");
                    EmitToTurn($"[STEER] superseded (not consumed): {_pendingSteer.Message}\n");
                }
                _pendingSteer = new SteerMessage { Message = message, Mode = mode };
                return new SteerEnqueueResult { Accepted = true, Superseded = superseded, SupersededMode = supersededMode };
            }
        }

        // Atomic get-and-clear of the pending steer. Returns null when none is pending.
        // Used by the per-iteration DRAIN (the turn stays in progress, so the flag is untouched).
        private SteerMessage TakePendingSteer()
        {
            lock (_steerLock)
            {
                SteerMessage s = _pendingSteer;
                _pendingSteer = null;
                return s;
            }
        }

        // Turn end: atomically close the steer window — take the final pending steer AND
        // clear the in-progress flag in ONE lock acquisition. A steer enqueued before this
        // point (flag still true) is captured here and recorded as unconsumed; one enqueued
        // after (flag already false) is refused by EnqueueSteer. Splitting the take and the
        // clear into two lock acquisitions would reopen the exact gap this closes: an
        // EnqueueSteer landing between them would set _pendingSteer that nothing drains.
        // The caller records the taken steer (RecordSteer/EmitToTurn) AFTER the lock.
        private SteerMessage TakePendingSteerAndEndTurn()
        {
            lock (_steerLock)
            {
                SteerMessage s = _pendingSteer;
                _pendingSteer = null;
                _turnInProgress = false;
                return s;
            }
        }

        // Turn start: open the steer window. Setting the flag and clearing any stale pending
        // steer is ONE lock acquisition. A pending steer here is an invariant violation — the
        // previous turn's atomic close must have taken it — so it is returned for the caller
        // to record as unconsumed (self-healing, honest) rather than silently dropped.
        private SteerMessage BeginSteerTurn()
        {
            lock (_steerLock)
            {
                SteerMessage stale = _pendingSteer;
                _pendingSteer = null;
                _turnInProgress = true;
                return stale;
            }
        }

        // Fold the pending steer (if any) into the prompt about to be sent this iteration.
        // The decision lives in Steer (pure/testable); this method only wires it to the
        // state, the audit journal, and the transcript. An override on the last iteration
        // is refused (no iterations left to act on it); everything else is folded.
        private void DrainSteerIntoPrompt(ref string currentPrompt)
        {
            var pending = TakePendingSteer();
            if (pending == null) return;

            bool last = Steer.IsLastIteration(_options.AgenticLoopMaxDepth, _state.AgenticDepth);
            (currentPrompt, SteerDisposition disp) = Steer.Apply(currentPrompt, pending.Message, pending.Mode, last);

            _host.RecordSteer(pending.Message, pending.Mode, disp,
                disp == SteerDisposition.Rejected ? "last_iteration" : null);

            string modeTag = pending.Mode == SteerMode.Override ? "override" : "suggest";
            EmitToTurn(disp == SteerDisposition.Rejected
                ? $"[STEER] {modeTag} REJECTED — last iteration, no iterations left to change course. {pending.Message}\n"
                : $"[STEER] {modeTag} folded into the prompt at this iteration boundary. {pending.Message}\n");
        }

        /// <summary>
        /// Test seam (visible to DevMind.Core.Tests via InternalsVisibleTo) — exposes the
        /// action journal so tests can assert what the loop/steer recorded without driving
        /// a live model. Same source as result.Actions (GetActions at turn end).
        /// </summary>
        internal IReadOnlyList<HostAction> JournalForTest => _host.GetActions();

        /// <summary>
        /// Test seam (visible to DevMind.Core.Tests via InternalsVisibleTo) — the session's
        /// turn-clock value, so a test can pin the increment to once-per-user-turn rather than
        /// once-per-agentic-iteration.
        /// </summary>
        internal int CurrentTurnForTest => _llmClient.CurrentTurn;

        /// <summary>
        /// Test seam (visible to DevMind.Core.Tests via InternalsVisibleTo) — the cumulative
        /// age-eviction drop count, so a test can assert a continuation did not age-evict a
        /// prior job's still-recent messages (the 37-dropped-at-3%-context regression).
        /// </summary>
        internal int EvictedMessageCountForTest => _llmClient.EvictedMessageCountForTest;

        /// <summary>Adjusts the per-turn iteration cap for a continuation.</summary>
        public void SetMaxDepth(int maxDepth) => _options.AgenticLoopMaxDepth = maxDepth;

        /// <summary>Re-syncs the no-execution guard on a REUSED (continuation) session. The
        /// job's flag wins: a continuation that opted in must block, and an inherited one
        /// must keep blocking. Idempotent — calling it with the constructor's value is a no-op.</summary>
        public void SetNoExecute(bool noExecute)
        {
            _noExecute = noExecute;
            _host.NoExecute = noExecute;
        }

        /// <summary>Re-syncs the thinking-to-transcript DISPLAY switch on a REUSED
        /// (continuation) session — a continuation's explicit show_thinking can differ from
        /// what the parent's session was built with. null reverts to the
        /// DEVMIND_TASK_SHOW_THINKING environment-variable fallback. Display only — never
        /// touches generation (that is the ShowLlmThinking option, read per request by the
        /// LlmClient). Idempotent — re-syncing with the constructor's value is a no-op.</summary>
        public void SetStreamThinking(bool? streamThinkingToTranscript)
        {
            _options.StreamThinkingToTranscript = streamThinkingToTranscript;
        }

        /// <summary>Re-syncs the test-verification REGIME on a REUSED (continuation)
        /// session — a continuation's verify_tests can differ from what the parent's
        /// session was built with, and the addendum is rebuilt on every turn, so a stale
        /// regime would misinstruct the agent on turn N+1. Prompt-only — no host guard.
        /// Idempotent — re-syncing with the current value is a no-op.</summary>
        public void SetHarnessVerifiesTests(bool harnessVerifiesTests)
        {
            _options.HarnessVerifiesTests = harnessVerifiesTests;
        }

        private string BuildSystemPrompt()
        {
            string llmDirective = LoopHelpers.BuildToolUsePrompt(_resolvedBuildCommand, projectNamespace: null);
            // The global system-prompt file (%APPDATA%\devmind\system-prompt.md) replaces
            // the hardcoded options.SystemPrompt when present. Absence is normal —
            // fall back to options.SystemPrompt unchanged. An explicit --system-prompt
            // CLI arg still wins (it sets _options.SystemPrompt before this method runs).
            // _promptFilePath is an optional override (tests inject a temp path);
            // null means use the production default. Read on EVERY rebuild (hot-reload).
            string filePrompt = _promptFilePath != null
                ? SystemPromptFile.LoadFrom(_promptFilePath)
                : SystemPromptFile.Load();
            string basePrompt = filePrompt ?? _options.SystemPrompt;
            string combined = $"{basePrompt}\n\n{llmDirective}";

            string context = HeadlessAgent.LoadAgentsContext(_workingDirectory);
            if (!string.IsNullOrEmpty(context))
                combined += $"\n\n--- Project Context (AGENTS.md) ---\n{context}\n---";

            try
            {
                var memory = new MemoryManager(_workingDirectory);
                string memoryIndex = memory.LoadIndex();
                if (!string.IsNullOrWhiteSpace(memoryIndex))
                    combined += $"\n\n--- Session Memory (MEMORY.md) ---\n{memoryIndex}\n---";

                // Standing convention topics are injected IN FULL, not just indexed:
                // delegated agents repeatedly tripped repo rules (warnings-as-errors,
                // LoggerMessage pattern, test naming) that were sitting un-recalled in
                // .devmind/memory — the index alone doesn't put them in frame.
                string standing = memory.LoadStandingContext();
                if (!string.IsNullOrWhiteSpace(standing))
                    combined += $"\n\n--- STANDING REPO CONVENTIONS (follow these in every change) ---\n{standing}\n---";
            }
            catch { /* memory is best-effort */ }

            if (!string.IsNullOrEmpty(_host.TaskScratchpad))
                combined += $"\n\n--- CURRENT SCRATCHPAD ---\n{_host.TaskScratchpad}\n---";

            combined += HeadlessAgent.BuildHeadlessAddendum(_options.HarnessVerifiesTests);
            if (!_allowCommit)
                combined += HeadlessAgent.NoCommitRule;
            if (_noExecute)
                combined += HeadlessAgent.NoExecuteRule;
            return combined;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _currentTurnCts?.Cancel(); } catch { }
            lock (_transcriptLock)
            {
                try { _turnTranscriptWriter?.Dispose(); } catch { }
                _turnTranscriptWriter = null;
            }
            try { _host.DrainPatchBackups(); } catch { /* the drain never throws; belt and suspenders on a disposal path */ }
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
        /// Shared behavioral rails appended to every headless system prompt, regardless
        /// of test-verification regime. The commit rule is conditional (see
        /// <c>allowCommit</c>); this body plus the regime picked by
        /// <see cref="BuildHeadlessAddendum"/> keeps a delegated agent inside its sandbox
        /// and prevents it stalling on questions nobody will answer.
        /// </summary>
        internal const string HeadlessAddendum =
            "\n\n--- HEADLESS DELEGATION RULES ---\n" +
            "You are running unattended on behalf of another agent (the caller). For minor\n" +
            "ambiguities make the most reasonable choice and note the assumption in your final\n" +
            "answer. But when you are blocked on a consequential decision (data semantics,\n" +
            "destructive changes, conflicting requirements), or the same failure keeps\n" +
            "recurring after real research, call ask_caller with 1-3 specific questions and\n" +
            "what you already tried — the caller answers and resumes this conversation with\n" +
            "full context. NEVER guess at facts you could not verify; a good question is\n" +
            "cheap, a confident wrong answer is expensive. Write operations (create, patch, delete, rename, append) are confined\n" +
            "to the working directory. Read operations are not — use an absolute path to reach a file outside it if\n" +
            "a relative lookup misses. Do NOT spend iterations verifying the full build\n" +
            "or wrestling shell timeouts to do so — the job runner builds the project itself\n" +
            "after you finish and reports the result to the caller; use the run_build tool\n" +
            "only for a quick compile check when you genuinely need one mid-task. Do not\n" +
            "write scratch/output files into the repository — describe results in your\n" +
            "answer instead. When the task is complete, call task_done with a concise\n" +
            "summary of what you changed and why.\n" +
            "\n" +
            "C# discipline: NEVER guess an API shape. Before calling, overriding, or mocking\n" +
            "any method or type you have not read in this session, use hover or go_to_definition\n" +
            "on it — signatures, return types (Task vs Task<T>), and overloads must come from\n" +
            "the source, not from memory. After EVERY C# edit, call get_diagnostics on the\n" +
            "file and fix reported errors before moving on.\n" +
            "\n" +
            "When a build/test/shell error REPEATS: stop patching. Re-read the error\n" +
            "literally, then research in this order: (1) LSP — hover / go_to_definition /\n" +
            "find_references on the symbols named in the error; (2) query_library and\n" +
            "recall_memory / search_memory for repo conventions; (3) web_search the exact\n" +
            "error message. State your new hypothesis and its evidence before the next\n" +
            "patch. If research produces no new hypothesis, call ask_caller instead of\n" +
            "trying again.\n" +
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

        /// <summary>Regime: the harness runs the full suite over the solution after the
        /// agent stops and those numbers are the ones reported. Measured before this
        /// existed: an agent re-ran the full suite as its final act and the harness ran
        /// it again moments later with no file changes in between — 23% of a 256-second
        /// job spent proving the same thing twice. Targeted/filtered runs during the work stay
        /// encouraged: the waste is only the final full-suite sweep.</summary>
        internal const string HarnessVerifiesTestsRule =
            "\n" +
            "--- TEST VERIFICATION: HARNESS REGIME (the job runner runs the suite for you) ---\n" +
            "This job has harness test verification ENABLED. The harness runs `dotnet test`\n" +
            "over the solution immediately after you stop, and its numbers are the ones\n" +
            "reported.\n" +
            "- While working, use targeted/filtered test runs (run_tests with a filter, or\n" +
            "  `dotnet test --filter`) to check your own edits as you go — that is how you\n" +
            "  verify incrementally, and it is cheap; keep doing it.\n" +
            "- Do NOT run the full test suite as a final step. It will be run for you\n" +
            "  moments later with no file changes in between — the extra minutes are pure\n" +
            "  waste.\n" +
            "- In your final report, say the full suite was not run and that the harness\n" +
            "  verifies it. Do NOT claim a suite result you did not observe. If the harness\n" +
            "  run fails, the job comes back stopped_incomplete and is continued — that is\n" +
            "  the intended path and it is cheap.\n";

        /// <summary>Regime: no harness will run the suite — the agent is the only
        /// verifier. TUI/CLI jobs and MCP jobs with verify_tests off all land here,
        /// which is also the default when the flag is forgotten: failing loud (one
        /// wasted suite run) is cheap; failing silent (an unverified change shipped as
        /// \"done\") is not.</summary>
        internal const string NoHarnessSafetyNetRule =
            "\n" +
            "--- TEST VERIFICATION: NO HARNESS SAFETY NET (you are the only verifier) ---\n" +
            "This job has NO harness test verification. There is no harness safety net —\n" +
            "nobody runs the suite for you after you stop. Run the FULL test suite yourself\n" +
            "before finishing, and report the per-assembly counts you actually observed.\n";

        /// <summary>Assembles the headless addendum: the shared body plus the
        /// test-verification regime matching this job. The two regimes must read as
        /// genuinely different instructions — a single hedged paragraph that works for
        /// both is the failure mode: the agent needs to know which world it is in.</summary>
        internal static string BuildHeadlessAddendum(bool harnessVerifiesTests)
        {
            return HeadlessAddendum +
                   (harnessVerifiesTests ? HarnessVerifiesTestsRule : NoHarnessSafetyNetRule);
        }

        internal const string NoCommitRule =
            "Do NOT run git commit, git push, or any other git command that rewrites history\n" +
            "or publishes changes — the delegating agent handles version control.\n";

        /// <summary>Appended every iteration (BuildSystemPrompt runs per turn, and the session
        /// is retained across continuations, so the rule cannot be forgotten on turn N+1).
        /// Mirrors the harness-level block: the model is steered away before it even tries,
        /// and the host guard is the backstop if it does.</summary>
        internal const string NoExecuteRule =
            "\n" +
            "--- NO-EXECUTION RESTRICTION (set by the delegating caller for THIS task) ---\n" +
            "The caller restricted this task to no-execution: attempting to run a built\n" +
            "executable or `dotnet run` / `dotnet exec` (run_shell), run the test suite\n" +
            "(run_tests / `dotnet test`), or launch/attach a debugger (debug) will be BLOCKED\n" +
            "by the harness — do not try it and do not try workarounds that start a process.\n" +
            "This is a restriction on THIS task, not a DevMind limitation. Build commands\n" +
            "(dotnet build / run_build) remain allowed for compile verification — verify\n" +
            "your work by building, and note in your final summary that execution was\n" +
            "blocked by the caller.\n";

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
            CancellationToken ct = default,
            string promptFilePath = null)
        {
            using var session = new HeadlessSession(options, endpointUrl, apiKey,
                workingDirectory, buildCommand, allowCommit, promptFilePath: promptFilePath);
            return await session.RunTurnAsync(prompt, transcriptPath, progress, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Strips control tokens the local model occasionally leaks into visible text
        /// (raw &lt;/think&gt; tags and &lt;tool_call&gt;/&lt;function=...&gt; syntax that
        /// escaped the server-side parser) so they never reach the returned answer.
        /// </summary>
        /// <summary>
        /// Drops harness status lines that LlmClient streams through the token callback
        /// ("[CONTEXT] …", "[TOOL_USE] …", "[LLM] …", "[AGENTIC] …") so only the model's
        /// own prose remains. Returns "" when nothing else was there.
        /// </summary>
        internal static string StripStatusLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var keep = new List<string>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                string t = line.TrimStart();
                if (t.StartsWith("[CONTEXT]", StringComparison.Ordinal)
                    || t.StartsWith("[TOOL_USE]", StringComparison.Ordinal)
                    || t.StartsWith("[LLM]", StringComparison.Ordinal)
                    || t.StartsWith("[AGENTIC]", StringComparison.Ordinal))
                    continue;
                keep.Add(line);
            }
            return string.Join("\n", keep).Trim();
        }

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
