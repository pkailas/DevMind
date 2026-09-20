// File: Steer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The DECISION logic for steering a RUNNING headless job (devmind_task_steer).
// A steer is a message the caller injects into an in-flight turn; the loop folds
// it into the prompt at the next iteration boundary. Kept as pure, stateless
// functions so the last-iteration predicate and the fold/reject decision are
// directly testable without a live model. HeadlessSession owns the mailbox, the
// LoopState, and the host; Steer owns only the decision of what to do with a
// pending steer at a given point in the loop.

namespace DevMind
{
    /// <summary>
    /// How the caller wants a steer treated. Explicit — never parsed from the message
    /// text — and it GENERATES the framing the model sees. An explicit flag cannot
    /// misfire on wording the way a keyword scan can.
    /// </summary>
    public enum SteerMode
    {
        /// <summary>Default: an addition that folds into the current approach without interrupting it.</summary>
        Suggest,
        /// <summary>A stop-and-change-course instruction that supersedes the current direction.</summary>
        Override,
    }

    /// <summary>
    /// What happened to a steer — the audit disposition recorded in the action journal,
    /// so a result that changed because of an injection at iteration N is explainable
    /// from the journal alone.
    /// </summary>
    public enum SteerDisposition
    {
        /// <summary>Folded into the prompt at the next iteration boundary. Journal kind "steer".</summary>
        Consumed,
        /// <summary>Refused at the drain (an override with no iterations left to act on). Journal kind "steer_rejected".</summary>
        Rejected,
        /// <summary>Accepted but not folded — the turn ended first, or a newer steer superseded it. Journal kind "steer_unconsumed".</summary>
        Unconsumed,
    }

    /// <summary>
    /// Pure decision logic for the steer drain. HeadlessSession calls these at the top
    /// of each iteration (before the LLM request) and once at turn end; the tests call
    /// them directly to pin the last-iteration predicate and the fold/reject behaviour.
    /// </summary>
    public static class Steer
    {
        /// <summary>
        /// Whether the iteration about to start is the LAST one before the depth cap.
        /// At the top of iteration k the driver has already incremented <c>AgenticDepth</c>
        /// to k-1 (it increments at the END of each re-triggering iteration, LoopDriver.cs),
        /// and both the finish-up reserve and the depth cap fire when that count reaches
        /// <c>maxDepth</c>. So the last iteration is the one entering with
        /// <c>agenticDepth &gt;= maxDepth</c>. <c>maxDepth &lt;= 0</c> means uncapped —
        /// never the last iteration.
        /// </summary>
        public static bool IsLastIteration(int maxDepth, int agenticDepth)
            => maxDepth > 0 && agenticDepth >= maxDepth;

        /// <summary>
        /// Frames a steer as a block to append to the prompt. It is deliberately a
        /// bracketed, caller-attributed instruction so it reads as a human steer and is
        /// visibly distinct from a harness re-trigger (the loop's own synthetic prompts —
        /// Continue, the thrash directive, the finish-up reserve — are plain prose with
        /// no such marker).
        /// </summary>
        public static string Frame(SteerMode mode, string message)
            => mode == SteerMode.Override
                ? "[CALLER STEER — override] The caller is redirecting you. Stop your current approach and change course:\n"
                    + message + "\nThis supersedes your current direction — follow it."
                : "[CALLER STEER — suggestion] While you continue your current approach, the caller adds:\n"
                    + message + "\nFold this in as you go; it is not a reason to abandon your current line of work.";

        /// <summary>
        /// Applies a pending steer to the prompt that is about to be sent. Returns the
        /// (possibly) updated prompt and the disposition to record. An override on the
        /// last iteration is refused (no iterations left to act on it) and leaves the
        /// prompt unchanged; a suggestion on the last iteration is folded normally, as is
        /// everything off the last iteration.
        /// </summary>
        public static (string prompt, SteerDisposition disposition) Apply(
            string currentPrompt, string message, SteerMode mode, bool isLastIteration)
        {
            if (isLastIteration && mode == SteerMode.Override)
                return (currentPrompt, SteerDisposition.Rejected);

            string framed = Frame(mode, message);
            string folded = string.IsNullOrEmpty(currentPrompt) ? framed : currentPrompt + "\n\n" + framed;
            return (folded, SteerDisposition.Consumed);
        }
    }

    /// <summary>
    /// The outcome of handing a steer to a session (devmind_task_steer). A steer is
    /// accepted only while a turn is actually running on that session; otherwise it is
    /// REFUSED rather than silently accepted into a mailbox nothing will drain. The
    /// refused case closes the enqueue-after-turn-end window: between the job's State
    /// check and the enqueue, the turn can end — and a steer enqueued then would
    /// otherwise be stranded (silently lost, or drained into a later turn on a
    /// continuation and attributed to a job nobody steered).
    /// </summary>
    public sealed class SteerEnqueueResult
    {
        /// <summary>
        /// False when the session had no turn in progress and the steer was refused —
        /// nothing was queued and nothing was recorded. True means a live turn will drain it.
        /// </summary>
        public bool Accepted { get; init; }

        /// <summary>True when this steer replaced an earlier, still-un-consumed steer.</summary>
        public bool Superseded { get; init; }

        /// <summary>
        /// The mode of the steer this one replaced, when <see cref="Superseded"/> — so a
        /// downgrade onto a pending override is visible to the caller ("replaced a pending
        /// override" ≠ "replaced a pending steer"). Null when nothing was superseded.
        /// </summary>
        public SteerMode? SupersededMode { get; init; }
    }
}
