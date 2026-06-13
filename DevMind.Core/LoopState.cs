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
        /// True when a narration-retry has already been consumed for the current
        /// user turn. Prevents infinite retry loops — only one forced retry per turn.
        /// </summary>
        public bool   NarrationRetryUsed       { get; set; }

        /// <summary>Cumulative model-generated tokens across all rounds of the current turn
        /// (sum of the server's predicted_n). Drives the per-turn token-budget guard.</summary>
        public int    CumulativeGeneratedTokens { get; set; }

        /// <summary>The token count at which the budget guard next pauses. Starts at the
        /// configured budget and is raised by another budget each time the user chooses to
        /// continue, so the guard re-arms instead of firing every round.</summary>
        public int    TokenBudgetCeiling        { get; set; }

        /// <summary>
        /// Resets all fields to initial values for a new user-initiated turn.
        /// Does NOT clear _taskReadFiles — the caller handles that separately.
        /// </summary>
       public void ResetForUserTurn()
        {
            AgenticDepth              = 0;
            ShellLoopPending          = false;
            PromptedForTaskDone       = false;
            ConsecutiveErrorToolName  = null;
            ConsecutiveErrorCount     = 0;
            NarrationRetryUsed        = false;
            CumulativeGeneratedTokens = 0;
            TokenBudgetCeiling        = 0; // lazily set to the configured budget on first use
        }
    }
}
