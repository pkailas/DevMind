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

        /// <summary>One-shot re-prompt asking the model to call task_done after a prose-finish.</summary>
        public const string ProseFinish =
            "You produced a prose answer but did not call task_done. " +
            "Call task_done now with your answer in the summary parameter. " +
            "Do not repeat the answer in prose \u2014 only the tool call.";

        /// <summary>Retried prompt when the model described an action but made no tool call.</summary>
        public const string NarrationRetry =
            "You described an action but did not call any tool. " +
            "Call the appropriate tool now to perform the action you described.";

        /// <summary>All known synthetic prompt texts.</summary>
        public static readonly string[] All = { Continue, ProseFinish, NarrationRetry };
    }
}
