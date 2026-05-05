// File: LoopState.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

namespace DevMind
{
    /// <summary>
    /// Aggregates the five mutable loop-control fields that thread through
    /// the agentic pipeline in SendToLlm(). Owned by DevMindToolWindowControl;
    /// shared with partials via the single <c>_loopState</c> instance.
    /// </summary>
    public sealed class LoopState
    {
        public int    AgenticDepth             { get; set; }
        public bool   ShellLoopPending         { get; set; }
        public bool   PromptedForTaskDone      { get; set; }
        public string ConsecutiveErrorToolName { get; set; }
        public int    ConsecutiveErrorCount    { get; set; }

        /// <summary>
        /// Resets all fields to initial values for a new user-initiated turn.
        /// Does NOT clear _taskReadFiles — the caller handles that separately.
        /// </summary>
        public void ResetForUserTurn()
        {
            AgenticDepth             = 0;
            ShellLoopPending         = false;
            PromptedForTaskDone      = false;
            ConsecutiveErrorToolName = null;
            ConsecutiveErrorCount    = 0;
        }
    }
}
