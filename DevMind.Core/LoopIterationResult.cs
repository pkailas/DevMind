// File: LoopIterationResult.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;

namespace DevMind
{
    public enum LoopIterationKind
    {
        /// <summary>Loop reached a legitimate stopping point. Extension restores Ready UI state.</summary>
        Terminal,
        /// <summary>Driver wants the extension to assign NextContextualMessage and call SendToLlm again.</summary>
        ShouldReTrigger,
        /// <summary>CancellationToken was already set before re-trigger; extension shows Stopped.</summary>
        Cancelled,
    }

    public sealed class LoopIterationResult
    {
        public LoopIterationKind Kind { get; }
        /// <summary>Full LLM response (thinking-stripped). For LogTrainingTurn.</summary>
        public string AssistantResponse { get; }
        /// <summary>Classified outcome. For LogTrainingTurn.</summary>
        public ResponseOutcome Outcome { get; }
        /// <summary>Execution result; null when no tool calls were made.</summary>
        public ExecutionResult Result { get; }
        /// <summary>Tool calls from the LLM; null when none were made.</summary>
        public List<ToolCallResult> ToolCalls { get; }
        /// <summary>Non-null for ShouldReTrigger — extension assigns this to InputTextBox.Text before re-triggering.</summary>
        public string NextContextualMessage { get; }
        /// <summary>True when the extension should call LogTrainingTurn after processing Kind.</summary>
        public bool ShouldLogTurn { get; }

        private LoopIterationResult(
            LoopIterationKind kind,
            string assistantResponse,
            ResponseOutcome outcome,
            ExecutionResult result,
            List<ToolCallResult> toolCalls,
            string nextContextualMessage,
            bool shouldLogTurn)
        {
            Kind = kind;
            AssistantResponse = assistantResponse;
            Outcome = outcome;
            Result = result;
            ToolCalls = toolCalls;
            NextContextualMessage = nextContextualMessage;
            ShouldLogTurn = shouldLogTurn;
        }

        internal static LoopIterationResult MakeTerminal(
            string assistantResponse, ResponseOutcome outcome, ExecutionResult result, List<ToolCallResult> toolCalls)
            => new LoopIterationResult(LoopIterationKind.Terminal, assistantResponse, outcome, result, toolCalls, null, true);

        internal static LoopIterationResult MakeShouldReTrigger(
            string assistantResponse, ResponseOutcome outcome, ExecutionResult result, List<ToolCallResult> toolCalls,
            string nextMessage, bool shouldLog)
            => new LoopIterationResult(LoopIterationKind.ShouldReTrigger, assistantResponse, outcome, result, toolCalls, nextMessage, shouldLog);

        internal static LoopIterationResult MakeCancelled(
            string assistantResponse, ResponseOutcome outcome, ExecutionResult result, List<ToolCallResult> toolCalls)
            => new LoopIterationResult(LoopIterationKind.Cancelled, assistantResponse, outcome, result, toolCalls, null, false);
    }
}
