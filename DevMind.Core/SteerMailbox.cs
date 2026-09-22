// File: SteerMailbox.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The MAILBOX half of steering: one pending message, last-write-wins, handed between a
// thread that enqueues and a thread that drains. The DECISION half — what to do with a
// pending steer at a given point in the loop — is Steer (pure, stateless).
//
// Extracted from HeadlessAgent so the TUI can steer a running turn with the same machinery
// rather than a second, subtly different one. Two implementations of "is a turn running"
// is exactly how the enqueue-after-turn-end window gets reopened in the copy that nobody
// wrote the tests for.
//
// The locking discipline is the load-bearing part, and it is not incidental:
//
//   * BeginTurn and TakeAndEndTurn each do their read, their clear and their flag change
//     in ONE lock acquisition. Splitting a take from its flag change reopens the gap the
//     flag exists to close — an Enqueue landing between them sets a pending steer that
//     nothing will ever drain.
//   * Enqueue refuses when no turn is open rather than accepting into a mailbox nothing
//     reads. "Accepted" means a live turn will drain it; there is no maybe.
//   * A steer displaced by a newer one is RETURNED to the caller, never dropped inside
//     this class. The caller owns how it is reported (a journal entry in headless, a
//     transcript line in the TUI), but it must be told, because a caller's message
//     disappearing silently is the failure mode all of this exists to prevent.
//
// This class deliberately does no reporting of its own: it has no host, no transcript and
// no opinion about either. Everything it takes away from a caller, it hands back.

namespace DevMind
{
    /// <summary>
    /// A steer waiting to be folded into a running turn: the caller's text and how they
    /// want it treated.
    /// </summary>
    public sealed class SteerMessage
    {
        /// <summary>The caller's message, as typed. Never parsed for intent.</summary>
        public required string Message { get; init; }

        /// <summary>Suggestion (fold in and keep going) or override (change course).</summary>
        public SteerMode Mode { get; init; }
    }

    /// <summary>
    /// Single-slot, last-write-wins mailbox for steering an in-flight turn. Enqueued from
    /// one thread (an MCP request, or the TUI's UI thread) and drained from another (the
    /// turn's worker), so every operation takes the lock.
    /// </summary>
    public sealed class SteerMailbox
    {
        private readonly object _lock = new object();
        private SteerMessage _pending;

        // True for the duration of a turn (set by BeginTurn, cleared atomically with the
        // final drain by TakeAndEndTurn). Enqueue refuses when false — this is what closes
        // the enqueue-after-turn-end window: there is no state where a turn is over but a
        // steer would still be accepted into a mailbox nothing will ever drain.
        private bool _turnInProgress;

        /// <summary>
        /// Whether a turn is currently open to steering. A UI asks this to decide whether
        /// typed input is a steer or a new prompt. Advisory only — the turn can end between
        /// the check and the <see cref="Enqueue"/>, which is exactly why Enqueue re-checks
        /// under the lock and can still refuse.
        /// </summary>
        public bool IsTurnOpen { get { lock (_lock) return _turnInProgress; } }

        /// <summary>
        /// Opens the steer window for a turn. Setting the flag and clearing any stale
        /// pending steer is ONE lock acquisition. A pending steer here is an invariant
        /// violation — the previous turn's atomic close must have taken it — so it is
        /// returned for the caller to report as unconsumed (self-healing, honest) rather
        /// than silently dropped.
        /// </summary>
        /// <returns>A stale steer left by a previous turn, or null.</returns>
        public SteerMessage BeginTurn()
        {
            lock (_lock)
            {
                SteerMessage stale = _pending;
                _pending = null;
                _turnInProgress = true;
                return stale;
            }
        }

        /// <summary>
        /// Queues a steer for the running turn, replacing any un-consumed one.
        /// </summary>
        /// <param name="superseded">
        /// The steer this one displaced, or null. Returned rather than discarded: the
        /// caller must report it, because the whole point of the mailbox is that a
        /// caller's message is never lost without a trace.
        /// </param>
        /// <returns>
        /// Accepted=false when no turn is open — nothing was queued and nothing displaced.
        /// </returns>
        public SteerEnqueueResult Enqueue(string message, SteerMode mode, out SteerMessage superseded)
        {
            lock (_lock)
            {
                superseded = null;

                // The fix for the enqueue-after-turn-end race. A steer enqueued once the
                // turn is over would otherwise sit in _pending, un-recorded and un-drained
                // — silently lost, or drained into the NEXT turn and attributed to a turn
                // nobody steered. There is no "maybe": accepted means a live turn will
                // drain it.
                if (!_turnInProgress)
                    return new SteerEnqueueResult { Accepted = false };

                superseded = _pending;
                SteerMode? supersededMode = superseded != null ? (SteerMode?)superseded.Mode : null;

                _pending = new SteerMessage { Message = message, Mode = mode };
                return new SteerEnqueueResult
                {
                    Accepted = true,
                    Superseded = superseded != null,
                    SupersededMode = supersededMode,
                };
            }
        }

        /// <summary>
        /// Atomic get-and-clear for the per-iteration drain. The turn stays open, so the
        /// flag is untouched. Returns null when nothing is pending.
        /// </summary>
        public SteerMessage Take()
        {
            lock (_lock)
            {
                SteerMessage s = _pending;
                _pending = null;
                return s;
            }
        }

        /// <summary>
        /// Closes the steer window at turn end: takes the final pending steer AND clears
        /// the in-progress flag in ONE lock acquisition.
        /// <para>
        /// A steer enqueued before this point (flag still true) is captured here and should
        /// be reported as unconsumed; one enqueued after (flag already false) is refused by
        /// <see cref="Enqueue"/>. Splitting the take and the clear into two acquisitions
        /// would reopen the exact gap this closes: an Enqueue landing between them would
        /// set a pending steer that nothing drains.
        /// </para>
        /// <para>
        /// Call it from a finally, so the flag clears on every exit path. Leaving it set on
        /// an error path keeps Enqueue accepting into a mailbox nothing will read.
        /// </para>
        /// </summary>
        public SteerMessage TakeAndEndTurn()
        {
            lock (_lock)
            {
                SteerMessage s = _pending;
                _pending = null;
                _turnInProgress = false;
                return s;
            }
        }
    }
}
