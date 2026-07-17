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

        /// <summary>Whether the context-window guard may fire. Set false after the user
        /// continues past the limit (so it doesn't nag every round) and re-armed once usage
        /// drops back below the limit — e.g. after a compaction.</summary>
        public bool   ContextGuardArmed         { get; set; }

        // ── Thrash guard ─────────────────────────────────────────────────────
        // Tracks the SAME normalized failure signature recurring across iterations
        // (edit→build→same error cycles). Distinct from ConsecutiveError*: there the
        // same TOOL fails repeatedly; here the tools alternate (patch succeeds, build
        // fails with an unchanged error) so the tool counter never trips.

        /// <summary>Normalized signature of the most recent failure (null when the last
        /// consequential action succeeded).</summary>
        public string LastFailureSignature      { get; set; }

        /// <summary>How many times LastFailureSignature has occurred in a row.</summary>
        public int    RepeatedFailureCount      { get; set; }

        /// <summary>True once the research-directive nudge for the current signature
        /// has been injected — one nudge per signature, then the hard stop.</summary>
        public bool   ResearchNudgeIssued       { get; set; }

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
            ContextGuardArmed         = true; // armed at the start of every turn
            LastFailureSignature      = null;
            RepeatedFailureCount      = 0;
            ResearchNudgeIssued       = false;
        }
    }
}
