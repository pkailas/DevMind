// File: SyntheticPrompts.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Canonical literals for auto-injected prompts used by the agentic loop.
// Shared between LoopDriver (injection sites) and SlashCommand (resume filtering).

namespace DevMind
{
    /// <summary>
    /// Auto-injected prompt texts produced by the agentic loop. Used both at
    /// injection time (LoopDriver) and at resume-filter time (SlashCommand).
    /// </summary>
    public static class SyntheticPrompts
    {
        /// <summary>Neutral continuation prompt fed back when the loop re-triggers after tool execution.</summary>
        public const string Continue = "Continue with the task.";

        /// <summary>
        /// One-shot re-prompt after a prose-finish (the model ended in prose without a
        /// tool call — the exact point where it chooses "stop" vs "stop with questions").
        /// It must present BOTH terminal tools, not only task_done: telling the model
        /// "call task_done now" pushes a blocked run to bury its questions in the task_done
        /// summary (state "done") instead of pausing for the caller (state "needs_input").
        /// </summary>
        public const string ProseFinish =
            "You ended in prose without calling a terminal tool. Make that decision now, as a " +
            "single tool call (do not answer in prose): " +
            "If the task is genuinely complete, call task_done with your answer in the summary " +
            "parameter. " +
            "If you are blocked on something only the caller can decide or supply — an open " +
            "question, a consequential choice, or a conflicting requirement you have not been " +
            "able to resolve — call ask_caller with 1-3 specific numbered questions and what you " +
            "already tried. Never bury those questions in a task_done summary: the caller only " +
            "sees a needs_input state when you call ask_caller.";

        /// <summary>Retried prompt when the model described an action but made no tool call.</summary>
        public const string NarrationRetry =
            "You described an action but did not call any tool. " +
            "Call the appropriate tool now to perform the action you described.";

        /// <summary>All known synthetic prompt texts.</summary>
        public static readonly string[] All = { Continue, ProseFinish, NarrationRetry };
    }
}
