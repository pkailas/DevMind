// File: LoopDriver.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
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
        private readonly LlmClient _llmClient;
        private readonly IAgenticHost _agenticHost;
        private readonly ILoopCallbacks _callbacks;
        private readonly ILlmOptions _options;
        private readonly LoopState _state;

        // Same heuristic as the extension constant — gives slack for legitimate
        // progressive debugging cycles without masking genuine stuck loops.
        private const int ConsecutiveErrorAbortThreshold = 5;

        public LoopDriver(
            LlmClient llmClient,
            IAgenticHost agenticHost,
            ILoopCallbacks callbacks,
            ILlmOptions options,
            LoopState state)
        {
            _llmClient   = llmClient;
            _agenticHost = agenticHost;
            _callbacks   = callbacks;
            _options     = options;
            _state       = state;
        }

        /// <summary>
        /// Processes one iteration of the agentic pipeline for the given LLM response.
        /// Must be called on the main thread (VS Dispatcher / JoinableTaskFactory).
        /// </summary>
        /// <param name="assistantResponse">Full LLM response (thinking-stripped, from responseBuffer).</param>
        /// <param name="buildCommand">Build command for this project, passed to ToolCallMapper.</param>
        /// <param name="ct">Token from the extension's CancellationTokenSource.</param>
        public async Task<LoopIterationResult> ProcessIterationAsync(
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

                // Explicit DONE signal
                if (outcome.IsDone)
                {
                    _agenticHost.AppendOutput("[AGENTIC] Task complete.\n", OutputColor.Success);
                    _state.AgenticDepth = 0;
                    return MakeTerminal(assistantResponse, outcome, result, lastToolCalls);
                }

                // Run/exec command succeeded — treat as task complete
                if (result.ShellExitCode.HasValue && result.ShellExitCode == 0
                    && !string.IsNullOrEmpty(result.LastShellCommand)
                    && LoopHelpers.IsRunOrExecCommand(result.LastShellCommand))
                {
                    _agenticHost.AppendOutput("[AGENTIC] Run/exec command succeeded — treating as task complete.\n", OutputColor.Success);
                    _state.AgenticDepth = 0;
                    return MakeTerminal(assistantResponse, outcome, result, lastToolCalls);
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
                    return MakeTerminal(assistantResponse, outcome, result, lastToolCalls);
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
                    return MakeTerminal(assistantResponse, outcome, result, lastToolCalls);
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

                _state.ShellLoopPending = true;
                return LoopIterationResult.MakeShouldReTrigger(
                    assistantResponse, outcome, result, lastToolCalls,
                    "Continue with the task.", shouldLog: true);
            }
            else
            {
                // No-tool-calls path: model answered in prose or called task_done inline.
                if (outcome.IsDone)
                    _agenticHost.AppendOutput("Task complete.\n", OutputColor.Success);

                string trimmedResponse = assistantResponse?.Trim() ?? "";
                bool insideAgenticCycle = _state.AgenticDepth > 0 || _state.ShellLoopPending;
                bool prosePresent       = trimmedResponse.Length > 40 && !outcome.HasAnyDirective && !outcome.IsDone;

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
                    else
                        _agenticHost.AppendOutput("[DIAG] Tool use loop: no tool calls — stopping.\n", OutputColor.Dim);
                }
                return MakeTerminal(assistantResponse, outcome, null, null);
            }
        }

        private LoopIterationResult MakeTerminal(
            string assistantResponse, ResponseOutcome outcome, ExecutionResult result, List<ToolCallResult> toolCalls)
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
            return LoopIterationResult.MakeTerminal(assistantResponse, outcome, result, toolCalls);
        }
    }
}
