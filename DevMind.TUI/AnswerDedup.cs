// File: AnswerDedup.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The one decision that stops the final answer showing twice.
//
// In the terminal iteration of a turn the model streams its report as PROSE and then calls
// task_done(summary) with essentially the same report. The transcript drew both — the streamed
// ◆ block and the executor's copy of the summary — so the user read the same answer back to
// back. "Pretty much always", because that is how the prompt tells the model to end a turn.
//
// The fix is a display choice, made here and nowhere else: when the terminal iteration already
// streamed a real prose answer, the task_done summary is redundant and is replaced by one dim
// line, the summary parked for /expand. ask_caller's questions are NEVER collapsed — a person
// is waiting to answer them, and folding them would hide the run from the user.
//
// Kept pure so the rule is testable without a running UI: the only inputs are how much prose
// the user SAW in the terminal iteration, the summary text, and which kind of answer this is.

using System;

namespace DevMind
{
    /// <summary>
    /// Decides whether a model-authored answer is redundant with the prose already on screen.
    /// Pure and stateless — the host carries the per-iteration prose count in; this only judges it.
    /// </summary>
    public static class AnswerDedup
    {
        /// <summary>
        /// The fewest non-whitespace characters of visible prose in the TERMINAL iteration that
        /// counts as "the model already said the answer". Below this the prose was a fragment
        /// ("Let me read the file…") and the summary still carries the real answer, so it is
        /// drawn in full.
        /// </para>
        /// </summary>
        public const int MinProseChars = 40;

        /// <summary>
        /// Collapse the answer into one dim line (parking its text for /expand)?
        /// <para>
        /// True only for a <c>task_done</c> summary that follows at least
        /// <see cref="MinProseChars"/> of visible prose in the SAME iteration. An
        /// <c>ask_caller</c> question is never collapsed, an empty summary is never collapsed
        /// (there is nothing to repeat), and prose from an earlier iteration does not count —
        /// the caller passes the terminal iteration's count, not a running total.
        /// </para>
        /// </summary>
        /// <param name="terminalIterationProse">
        /// Non-whitespace characters of visible prose the user saw in the terminal iteration
        /// (thinking and tool-call text excluded — what the transcript actually drew as prose).
        /// </param>
        /// <param name="summary">The task_done summary / ask_caller questions text.</param>
        /// <param name="kind">Which kind of answer this is.</param>
        public static bool ShouldCollapse(int terminalIterationProse, string summary, AnswerKind kind)
        {
            // Questions are for a person to answer — collapsing them hides a run that is
            // blocked, which is the worst moment for the transcript to go quiet.
            if (kind != AnswerKind.TaskDone) return false;

            // Nothing to repeat.
            if (string.IsNullOrWhiteSpace(summary)) return false;

            return terminalIterationProse >= MinProseChars;
        }

        /// <summary>
        /// Count the non-whitespace characters of <paramref name="text"/> — the measure the
        /// collapse rule compares against <see cref="MinProseChars"/>.
        /// </summary>
        public static int CountVisibleProse(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int count = 0;
            for (int i = 0; i < text.Length; i++)
                if (!char.IsWhiteSpace(text[i])) count++;
            return count;
        }
    }
}
