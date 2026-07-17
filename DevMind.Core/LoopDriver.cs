// File: LoopDriver.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// Drives one post-stream-complete iteration of the agentic pipeline:
    /// classify → execute → decide → signal.
    /// Owns no WPF state; signals the extension via <see cref="LoopIterationResult.Kind"/>
    /// for terminal UI cleanup and re-trigger orchestration.
    /// </summary>
    public sealed class LoopDriver
    {
        private readonly ILlmClient _llmClient;
        private readonly IAgenticHost _agenticHost;
        private readonly ILoopCallbacks _callbacks;
        private readonly ILlmOptions _options;
        private readonly LoopState _state;
        private readonly ITrainingLogger _trainingLogger;

       // Same heuristic as the extension constant — gives slack for legitimate
        // progressive debugging cycles without masking genuine stuck loops.
        private const int ConsecutiveErrorAbortThreshold = 5;

        // ── Thrash guard thresholds ─────────────────────────────────────
        // Detects edit→build→SAME-error cycles that the consecutive-error counter
        // misses (the tools alternate: patch_file succeeds, run_build fails, so the
        // per-tool counter keeps resetting while the underlying error never changes).
        // At the nudge threshold the re-trigger prompt is replaced with a research
        // directive (LSP → library/memory → web); at the abort threshold the loop
        // stops with TerminalReason "thrashing".
        private const int ThrashNudgeThreshold = 3;
        private const int ThrashAbortThreshold = 5;

        // ── Layer 2: Narration-stall retry guard ──────────────────────────────
        //
        // When the model returns prose with no tool_calls but the content matches
        // a "claim signal" (language that asserts a tool-worthy action), re-issue
        // the same turn once with tool_choice forced to "required". This catches
        // mid-session stalls that Layer 1 (cold-start) cannot reach.
        //
        // Patterns ported from tools/narration_scan.py (validated against 68-stall scan).
        // Cap: ONE forced retry per user turn. If it still returns no tool_call, stop.

        private const bool NARRATION_RETRY_ENABLED = true;

       private static readonly Regex[] _narrationClaimPatterns =
        {
            // announced_action: "let me read/check/look/inspect/verify/find/search/
            //   grep/fix/update/edit/modify/change/add/create/remove/delete/rename/
            //   run/build/test"
            new Regex(
                @"\b(?:now\s+)?let me\s+" +
                @"(?:read|check|look|inspect|verify|find|search|grep|fix|update|" +
                @"edit|modify|change|add|create|remove|delete|rename|run|build|test)" +
                @"\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // build_test_claim: "build: N errors", "build succeeded/failed"
            new Regex(@"\bbuild:\s*\d+\s*(?:errors?|warnings?)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // build_test_claim: "0 errors", "0 warnings"
            new Regex(@"\b0\s+(?:errors?|warnings?)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // build_test_claim: "build succeeded/failed"
            new Regex(@"\bbuild\s+(?:succeeded|failed)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // build_test_claim: "tests passed", "test passed"
            new Regex(@"\btests?\s+pass(?:ed)?\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // build_test_claim: "compiles cleanly"
            new Regex(@"\bcompiles?\s+cleanly\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // report_header: "## Fix", "## Root Cause", "## Changes", "## Summary"
            new Regex(
                @"^\s*#{1,6}\s+(?:fix|root cause|changes|summary)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline),
        };

       public LoopDriver(
            ILlmClient llmClient,
            IAgenticHost agenticHost,
            ILoopCallbacks callbacks,
            ILlmOptions options,
            LoopState state,
            ITrainingLogger trainingLogger = null)
        {
            _llmClient      = llmClient;
            _agenticHost    = agenticHost;
            _callbacks      = callbacks;
            _options        = options;
            _state          = state;
            _trainingLogger = trainingLogger;
        }

        /// <summary>
        /// Returns the first matching claim-signal category from the narration patterns,
        /// or null if no pattern matched. Used to gate the Layer 2 retry.
        /// </summary>
        private static string MatchNarrationClaim(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            for (int i = 0; i < _narrationClaimPatterns.Length; i++)
            {
                if (_narrationClaimPatterns[i].IsMatch(text))
                    return _narrationClaimPatterns[i].ToString(); // pattern as identifier
            }
            return null;
        }

        /// <summary>
        /// Processes one iteration of the agentic pipeline for the given LLM response.
        /// Must be called on the main thread (VS Dispatcher / JoinableTaskFactory).
        /// </summary>
        /// <param name="userMessage">The user/contextual message that was sent for this iteration
        /// (the original input on iteration 1, the contextual re-trigger prompt thereafter).
        /// Recorded as <c>user_message</c> in the training log.</param>
        /// <param name="assistantResponse">Full LLM response (thinking-stripped, from responseBuffer).</param>
        /// <param name="buildCommand">Build command for this project, passed to ToolCallMapper.</param>
        /// <param name="ct">Token from the extension's CancellationTokenSource.</param>
        public async Task<LoopIterationResult> ProcessIterationAsync(
            string userMessage,
            string assistantResponse,
            string buildCommand,
            CancellationToken ct)
        {
            _callbacks.AppendNewLine();

            // ── Classify ──────────────────────────────────────────────────────────────

            ResponseOutcome outcome = ResponseOutcome.Empty();
            var lastToolCalls = _llmClient.LastToolCalls;

            if (_options.ShowDebugOutput)
                _agenticHost.AppendOutput($"[DIAG] LastToolCalls: {lastToolCalls?.Count ?? 0}\n", OutputColor.Dim);

            if (lastToolCalls != null && lastToolCalls.Count > 0)
            {
                var toolBlocks = ToolCallMapper.Map(lastToolCalls, buildCommand);

                if (_options.ShowDebugOutput)
                {
                    foreach (var tb in toolBlocks)
                        _agenticHost.AppendOutput(
                            $"[DIAG] Block: Type={tb.Type}, FileName={tb.FileName}, Command={tb.Command}, MemoryTopic={tb.MemoryTopic}\n",
                            OutputColor.Dim);
                }

                string prose = assistantResponse.TrimEnd('\r', '\n');
                if (!string.IsNullOrEmpty(prose))
                    toolBlocks.Insert(0, new ResponseBlock { Type = BlockType.Text, Content = prose });

                outcome = new ResponseOutcome(toolBlocks);

                if (_options.ShowDebugOutput)
                    _agenticHost.AppendOutput(
                        $"[DIAG] Tool use path: {lastToolCalls.Count} call(s) mapped to {toolBlocks.Count} block(s)\n",
                        OutputColor.Dim);
            }

            var executor = new AgenticExecutor(_agenticHost, _options);
            executor.SetCancellationToken(ct);
            int maxDepth = _options.AgenticLoopMaxDepth;

            if (_options.ShowDebugOutput)
                _agenticHost.AppendOutput(
                    $"[DIAG] Outcome: HasPatches={outcome.HasPatches}, HasShell={outcome.HasShellCommands}, " +
                    $"HasRead={outcome.HasReadRequests}, IsDone={outcome.IsDone}, IsReadOnly={outcome.IsReadOnly}\n",
                    OutputColor.Dim);

            // ── Execute & Decide ──────────────────────────────────────────────────────

            if (lastToolCalls != null && lastToolCalls.Count > 0)
            {
                // Tool use path: model called tools — execute them, then decide next step.
                var action = new AgenticAction { Type = ActionType.ApplyAndBuild };
                ExecutionResult result = await executor.ExecuteAsync(action, outcome);

                LoopHelpers.InjectToolResultMessages(_llmClient, lastToolCalls, result, outcome.Blocks);

                // Update consecutive-error counter for stuck-loop detection.
                string primaryTool = lastToolCalls.Count > 0 ? lastToolCalls[0].Name : null;
                bool turnHadError  = result.Errors.Count > 0
                                     || (result.ShellExitCode.HasValue && result.ShellExitCode.Value != 0);
                if (primaryTool != null && turnHadError)
                {
                    if (primaryTool == _state.ConsecutiveErrorToolName)
                        _state.ConsecutiveErrorCount++;
                    else
                    {
                        _state.ConsecutiveErrorToolName = primaryTool;
                        _state.ConsecutiveErrorCount    = 1;
                    }
                }
                else
                {
                    _state.ConsecutiveErrorToolName = null;
                    _state.ConsecutiveErrorCount    = 0;
                }

                // Update thrash-guard signature: does THIS failure look like the LAST one?
                if (turnHadError)
                {
                    string sig = NormalizeFailureSignature(
                        result.Errors.Count > 0 ? result.Errors[0] : result.ShellOutput);
                    if (sig != null)
                    {
                        if (sig == _state.LastFailureSignature)
                        {
                            _state.RepeatedFailureCount++;
                        }
                        else
                        {
                            _state.LastFailureSignature = sig;
                            _state.RepeatedFailureCount = 1;
                            _state.ResearchNudgeIssued  = false;
                        }
                    }
                }
                else if (result.ShellExitCode.HasValue && result.ShellExitCode.Value == 0)
                {
                    // A clean shell/build/test run resolves the failure. Read-only turns
                    // (research between failures) deliberately leave the counters alone.
                    _state.LastFailureSignature = null;
                    _state.RepeatedFailureCount = 0;
                    _state.ResearchNudgeIssued  = false;
                }

                // ask_caller — the model is blocked and needs the caller's answer.
                if (outcome.IsNeedsInput)
                {
                    _agenticHost.AppendOutput("[AGENTIC] Task paused — the agent needs input from the caller (ask_caller).\n", OutputColor.Normal);
                    _state.AgenticDepth = 0;
                    return MakeTerminal(userMessage, assistantResponse, outcome, result, lastToolCalls, "needs_input");
                }

                // Explicit DONE signal
                if (outcome.IsDone)
                {
                    _agenticHost.AppendOutput("[AGENTIC] Task complete.\n", OutputColor.Success);
                    _state.AgenticDepth = 0;
                    return MakeTerminal(userMessage, assistantResponse, outcome, result, lastToolCalls);
                }

                // Run/exec command succeeded — treat as task complete
                if (result.ShellExitCode.HasValue && result.ShellExitCode == 0
                    && !string.IsNullOrEmpty(result.LastShellCommand)
                    && LoopHelpers.IsRunOrExecCommand(result.LastShellCommand))
                {
                    _agenticHost.AppendOutput("[AGENTIC] Run/exec command succeeded — treating as task complete.\n", OutputColor.Success);
                    _state.AgenticDepth = 0;
                    return MakeTerminal(userMessage, assistantResponse, outcome, result, lastToolCalls);
                }

                // Consecutive-error abort — same tool failing without resolution
                if (_state.ConsecutiveErrorCount >= ConsecutiveErrorAbortThreshold)
                {
                    const int SnippetLen = 250;
                    string firstError  = result.Errors.Count > 0 ? result.Errors[0] : "(no error detail)";
                    string cmdLine     = result.LastShellCommand.Length > 0
                                         ? $"\nLast command: {result.LastShellCommand}" : "";
                    string outputSnip  = result.ShellOutput.Length > 0
                                         ? $"\nOutput:\n{result.ShellOutput.Substring(0, Math.Min(result.ShellOutput.Length, SnippetLen))}" : "";
                    _agenticHost.AppendOutput(
                        $"[AGENTIC] Aborted: '{_state.ConsecutiveErrorToolName}' failed {_state.ConsecutiveErrorCount} " +
                        $"consecutive times with no resolution.\n" +
                        $"Last error: {firstError}{cmdLine}{outputSnip}\n" +
                        "Review the failure above and re-send with a corrected approach.\n",
                        OutputColor.Error);
                    _state.ConsecutiveErrorToolName = null;
                    _state.ConsecutiveErrorCount    = 0;
                    _state.AgenticDepth = 0;
                    return MakeTerminal(userMessage, assistantResponse, outcome, result, lastToolCalls);
                }

                // Thrash abort — same failure signature past the cap, research nudge spent.
                if (_state.RepeatedFailureCount >= ThrashAbortThreshold && _state.ResearchNudgeIssued)
                {
                    _agenticHost.AppendOutput(
                        $"[AGENTIC] Stopped: the same failure recurred {_state.RepeatedFailureCount} times, " +
                        "including after a research directive.\n" +
                        $"Failure: {_state.LastFailureSignature}\n" +
                        "The current approach is wrong — re-brief with more context or answer the agent's open questions.\n",
                        OutputColor.Error);
                    _state.AgenticDepth = 0;
                    return MakeTerminal(userMessage, assistantResponse, outcome, result, lastToolCalls, "thrashing");
                }

                // Depth cap
                if (maxDepth > 0 && _state.AgenticDepth >= maxDepth)
                {
                    if (result.ShellExitCode.HasValue && result.ShellExitCode != 0)
                    {
                        int undoDepth = _agenticHost.GetPatchBackupCount();
                        _agenticHost.AppendOutput($"[AGENTIC] Depth cap reached ({_state.AgenticDepth}) — build still failing.\n", OutputColor.Error);
                        _agenticHost.AppendOutput("Type UNDO to revert all changes, or continue editing manually.\n", OutputColor.Dim);
                        _agenticHost.AppendOutput($"({undoDepth} change(s) can be undone)\n", OutputColor.Dim);
                    }
                    else
                    {
                        _agenticHost.AppendOutput($"[AGENTIC] Depth cap reached ({_state.AgenticDepth}). Stopping.\n", OutputColor.Dim);
                    }
                    _state.AgenticDepth = 0;
                    return MakeTerminal(userMessage, assistantResponse, outcome, result, lastToolCalls);
                }

                // ── Context-window guard ───────────────────────────────────────────────
                // Pause and ask before another round once the conversation fills the context
                // window past the limit (n_past / n_ctx ≥ limit%). Fires once per upward
                // crossing; re-arms after usage drops back below the limit (e.g. a compaction).
                int ctxLimitPct = _options.AgenticContextLimitPercent;
                if (ctxLimitPct > 0 && _llmClient.ServerContextSize > 0 && _llmClient.LastContextUsed > 0)
                {
                    int usedPct = (int)(_llmClient.LastContextUsed * 100L / _llmClient.ServerContextSize);
                    if (usedPct >= ctxLimitPct && _state.ContextGuardArmed)
                    {
                        bool cont = await _agenticHost.ConfirmContinueAsync(
                            $"Context window at {usedPct}% " +
                            $"({_llmClient.LastContextUsed:N0} / {_llmClient.ServerContextSize:N0} tokens), " +
                            $"past the {ctxLimitPct}% limit. Continue?");
                        if (!cont)
                        {
                            _agenticHost.AppendOutput(
                                $"[AGENTIC] Stopped at {usedPct}% context " +
                                $"({_llmClient.LastContextUsed:N0} / {_llmClient.ServerContextSize:N0}).\n",
                                OutputColor.Dim);
                            _state.AgenticDepth = 0;
                            return MakeTerminal(userMessage, assistantResponse, outcome, result, lastToolCalls);
                        }
                        _state.ContextGuardArmed = false; // don't nag until usage drops and climbs again
                    }
                    else if (usedPct < ctxLimitPct - 5)
                    {
                        _state.ContextGuardArmed = true; // headroom restored — re-arm
                    }
                }

                // Re-trigger: feed results back, let model decide next step
                _state.AgenticDepth++;
                {
                    int agCtx  = _llmClient.ServerContextSize > 0 ? _llmClient.ServerContextSize : _llmClient.MaxPromptTokens;
                    int agUsed = _llmClient.LastContextUsed > 0 ? _llmClient.LastContextUsed : _llmClient.EstimateHistoryTokens();
                    int agPct  = agCtx > 0 ? (int)(agUsed * 100.0 / agCtx) : 0;
                    string iterLabel = maxDepth > 0
                        ? $"Iteration {_state.AgenticDepth}/{maxDepth}"
                        : $"Iteration {_state.AgenticDepth}";
                    _agenticHost.AppendOutput($"[AGENTIC] {iterLabel} — {agUsed:N0} / {agCtx:N0} ({agPct}%)\n", OutputColor.Dim);
                }

                if (ct.IsCancellationRequested)
                {
                    _state.ShellLoopPending = false;
                    _state.AgenticDepth     = 0;
                    _agenticHost.AppendOutput("[AGENTIC] Cancelled.\n", OutputColor.Dim);
                    return LoopIterationResult.MakeCancelled(assistantResponse, outcome, result, lastToolCalls);
                }

                // Thrash nudge — replace the neutral re-trigger with a research directive
                // the first time a failure signature repeats to the nudge threshold.
                string nextMessage = "Continue with the task.";
                if (_state.RepeatedFailureCount >= ThrashNudgeThreshold && !_state.ResearchNudgeIssued)
                {
                    _state.ResearchNudgeIssued = true;
                    _agenticHost.AppendOutput(
                        $"[AGENTIC] Thrash guard: same failure {_state.RepeatedFailureCount}x — injecting research directive.\n",
                        OutputColor.Dim);
                    nextMessage =
                        $"STOP. The SAME failure has now occurred {_state.RepeatedFailureCount} times in a row:\n" +
                        $"{_state.LastFailureSignature}\n" +
                        "Your current hypothesis is wrong — do not patch again yet. Research first, in this order, " +
                        "citing the evidence you gather: " +
                        "(1) LSP: hover / go_to_definition / find_references on the exact symbols named in the error — " +
                        "verify real signatures, return types, and overloads instead of guessing; " +
                        "(2) query_library and recall_memory / search_memory for repo conventions related to this error; " +
                        "(3) web_search the exact error text. " +
                        "Then state your NEW hypothesis and the evidence for it before your next patch. " +
                        "If research does not produce a new hypothesis, call ask_caller with specific questions " +
                        "instead of patching again.";
                }

                _state.ShellLoopPending = true;
                MaybeLogTurn(userMessage, assistantResponse, outcome, result, lastToolCalls);
                return LoopIterationResult.MakeShouldReTrigger(
                    assistantResponse, outcome, result, lastToolCalls,
                    nextMessage, shouldLog: true);
            }
           else
            {
                // No-tool-calls path: model answered in prose or called task_done inline.
                if (outcome.IsDone)
                    _agenticHost.AppendOutput("Task complete.\n", OutputColor.Success);

                string trimmedResponse = assistantResponse?.Trim() ?? "";
                bool insideAgenticCycle = _state.AgenticDepth > 0 || _state.ShellLoopPending;
                bool prosePresent       = trimmedResponse.Length > 40 && !outcome.HasAnyDirective && !outcome.IsDone;

                // ── Layer 2: Narration-stall retry guard ─────────────────────────
                // When inside an agentic cycle, no tool calls were made, but the prose
                // matches a claim signal (e.g. "let me check the build" or "build: 0 errors"),
                // re-issue the turn once with tool_choice forced to "required".
                // This catches mid-session stalls that Layer 1 (cold-start) cannot reach.
                //
                // Gate: ONE retry per user turn. If the retry also returns no tool_call,
                // fall through to the normal prose-finish / terminal path.
                if (NARRATION_RETRY_ENABLED && insideAgenticCycle && !_state.NarrationRetryUsed && !outcome.IsDone)
                {
                    string claimSignal = MatchNarrationClaim(trimmedResponse);
                    if (claimSignal != null)
                    {
                        _state.NarrationRetryUsed = true;
                        _state.ShellLoopPending   = true;

                        if (_options.ShowDebugOutput)
                            _agenticHost.AppendOutput(
                                $"[DIAG] Narration stall detected (claim: {claimSignal}) — " +
                                $"retrying with tool_choice=required.\n", OutputColor.Dim);

                        const string NarrationRetryPrompt =
                            "You described an action but did not call any tool. " +
                            "Call the appropriate tool now to perform the action you described.";
                        return LoopIterationResult.MakeShouldReTrigger(
                            assistantResponse, outcome, null, null,
                            NarrationRetryPrompt, shouldLog: false,
                            forceToolChoiceRequired: true);
                    }
                }

                if (insideAgenticCycle && prosePresent && !_state.PromptedForTaskDone)
                {
                    // One-shot re-prompt asking for task_done.
                    _state.PromptedForTaskDone = true;
                    _state.ShellLoopPending    = true;

                    if (_options.ShowDebugOutput)
                        _agenticHost.AppendOutput("[DIAG] Prose-finish detected — re-prompting for task_done.\n", OutputColor.Dim);

                    const string ProsePrompt =
                        "You produced a prose answer but did not call task_done. " +
                        "Call task_done now with your answer in the summary parameter. " +
                        "Do not repeat the answer in prose — only the tool call.";
                    return LoopIterationResult.MakeShouldReTrigger(
                        assistantResponse, outcome, null, null,
                        ProsePrompt, shouldLog: false);
                }

                // Stop — question answered, already re-prompted once, or empty response
                _state.AgenticDepth = 0;
                if (_options.ShowDebugOutput)
                {
                    if (_state.PromptedForTaskDone)
                        _agenticHost.AppendOutput(
                            "[DIAG] Tool use loop: re-prompt also produced no tool calls — accepting prose-finish.\n",
                            OutputColor.Dim);
                    else if (_state.NarrationRetryUsed)
                        _agenticHost.AppendOutput(
                            "[DIAG] Narration retry also produced no tool calls — accepting prose-finish.\n",
                            OutputColor.Dim);
                    else
                        _agenticHost.AppendOutput("[DIAG] Tool use loop: no tool calls — stopping.\n", OutputColor.Dim);
                }
                return MakeTerminal(userMessage, assistantResponse, outcome, null, null);
            }
        }

        private LoopIterationResult MakeTerminal(
            string userMessage, string assistantResponse, ResponseOutcome outcome, ExecutionResult result, List<ToolCallResult> toolCalls,
            string terminalReason = null)
        {
            if (_options.ShowContextBudget)
            {
                int cbCtx  = _llmClient.ServerContextSize > 0 ? _llmClient.ServerContextSize : _llmClient.MaxPromptTokens;
                int cbUsed = _llmClient.LastContextUsed > 0 ? _llmClient.LastContextUsed : _llmClient.EstimateHistoryTokens();
                int cbPct  = cbCtx > 0 ? (int)(cbUsed * 100.0 / cbCtx) : 0;
                OutputColor cbColor = cbPct < 60 ? OutputColor.Dim
                                    : cbPct < 80 ? OutputColor.Normal
                                    : OutputColor.Error;
                _agenticHost.AppendOutput($"[CONTEXT] {cbUsed:N0} / {cbCtx:N0} ({cbPct}%)\n", cbColor);
            }
            _state.AgenticDepth = 0;
            // Terminal results always carry ShouldLogTurn == true (see LoopIterationResult.MakeTerminal).
            MaybeLogTurn(userMessage, assistantResponse, outcome, result, toolCalls);
            return LoopIterationResult.MakeTerminal(assistantResponse, outcome, result, toolCalls, terminalReason);
        }

        /// <summary>
        /// Reduces raw failure output to a comparable one-line signature: the first
        /// line mentioning "error" (else the first non-empty line), whitespace-collapsed
        /// and capped. Two iterations with the same signature are fighting the same
        /// failure, whatever tools they used to get there.
        /// </summary>
        private static string NormalizeFailureSignature(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string[] lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string line = null;
            foreach (string l in lines)
            {
                if (l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    line = l;
                    break;
                }
            }
            line = line ?? lines[0];
            line = Regex.Replace(line.Trim(), @"\s+", " ");
            return line.Length <= 240 ? line : line.Substring(0, 240);
        }

        // ── Training-turn capture ─────────────────────────────────────────────────
        // Core owns both the WHEN (ShouldLogTurn — every terminal result and the
        // tool-use re-trigger) and the call. The host supplies the logger (config +
        // enablement) through the constructor; a null or disabled logger is a no-op.

        private void MaybeLogTurn(
            string userMessage, string assistantResponse, ResponseOutcome outcome,
            ExecutionResult result, List<ToolCallResult> toolCalls)
        {
            if (_trainingLogger == null || !_trainingLogger.Enabled)
                return;

            try
            {
                _trainingLogger.LogTurn(
                    BuildTurnData(userMessage, assistantResponse, outcome, result, toolCalls));
            }
            catch
            {
                // Logging must never break the agentic loop.
            }
        }

        private TrainingTurnData BuildTurnData(
            string userMessage, string assistantResponse, ResponseOutcome outcome,
            ExecutionResult result, List<ToolCallResult> toolCalls)
        {
            bool hasErrors = result?.Errors?.Count > 0
                || (result?.ShellExitCode.HasValue == true && result.ShellExitCode != 0);
            bool hasToolCalls = toolCalls?.Count > 0
                || (outcome?.Blocks?.Any(b => b.Type != BlockType.Text && b.Type != BlockType.Scratchpad) == true);

            int nCtx = _llmClient.ServerContextSize > 0 ? _llmClient.ServerContextSize : _llmClient.MaxPromptTokens;
            int nPast = _llmClient.LastContextUsed;
            int pct = nCtx > 0 ? (int)(nPast * 100.0 / nCtx) : 0;
            double tokPerSec = _llmClient.LastGeneratedMs > 0
                ? _llmClient.LastGeneratedTokens * 1000.0 / _llmClient.LastGeneratedMs
                : 0;

            return new TrainingTurnData
            {
                TurnNumber = _llmClient.CurrentTurn,
                SystemPrompt = _llmClient.SystemPromptContent,
                UserMessage = userMessage,
                AssistantResponse = assistantResponse,
                ToolCalls = JsonlTrainingLogger.ExtractToolCalls(outcome?.Blocks),
                ToolResults = JsonlTrainingLogger.ExtractToolResults(result),
                SummaryContext = _llmClient.LastCompactionSummary,
                Metrics = new MetricsEntry
                {
                    NPast = nPast,
                    NCtx = nCtx,
                    PredictedTokens = _llmClient.LastGeneratedTokens,
                    PromptTokens = _llmClient.LastPromptTokens,
                    TokPerSec = Math.Round(tokPerSec, 1),
                    Iteration = _state.AgenticDepth,
                    ContextPercent = pct
                },
                Outcome = JsonlTrainingLogger.ClassifyOutcome(
                    outcome?.IsDone == true,
                    hasErrors,
                    outcome?.HasReadRequests == true,
                    outcome?.HasPatches == true,
                    outcome?.HasFileCreation == true,
                    outcome?.HasShellCommands == true,
                    hasToolCalls)
            };
        }
    }
}
