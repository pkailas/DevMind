// File: TurnClock.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The advance policy for the context-aging turn clock (LlmClient.IncrementTurn).
//
// A user turn advances the clock ONCE, at the turn boundary — the point where a
// freshly-typed message starts an agentic cycle. It must NOT advance per agentic-loop
// iteration (the loop re-triggers many iterations for a single turn, and they SHARE the
// turn), and it must NOT advance a second time when an answer to a pending ask_caller
// question starts a new cycle within the same session — that answer is the SAME turn,
// not a new one.
//
// This rule is the one that was lost in the ports: the TUI (64f3a5c) and headless
// (ba4cdbe) each called IncrementTurn() at the top of their agentic loop (per iteration),
// and the CLI (DevMind.Cli) mirrors the same per-iteration call. Keeping the policy here
// in Core — shared by every front-end — means it is testable without a live agentic loop or
// a UI, and every port advances the clock the same way (see DevMind.TUI.Tests/TurnClockTests
// and the source-derived guard in DevMind.Core.Tests/AgenticLoopTurnClockParityTests).

namespace DevMind
{
    /// <summary>
    /// Pure state machine deciding, at each turn boundary, whether the context-aging turn
    /// clock advances. Call <see cref="BeginTurn"/> once per user turn (the turn boundary,
    /// OUTSIDE the agentic loop) and <see cref="EndTurn(bool)"/> when the turn stops, passing
    /// whether it stopped needing input (the model called ask_caller).
    /// </summary>
    public sealed class TurnClock
    {
        private bool _resumePending;

        /// <summary>How many user turns have advanced the clock this session.</summary>
        public int Advances { get; private set; }

        /// <summary>
        /// Turn boundary. Advances the clock and returns <c>true</c>, except when the previous
        /// turn ended needing input — an answer to that pending question is the SAME turn, so
        /// the clock holds and <c>false</c> is returned.
        /// </summary>
        public bool BeginTurn()
        {
            if (_resumePending)
            {
                _resumePending = false;
                return false;
            }
            Advances++;
            return true;
        }

        /// <summary>
        /// Turn end. Records whether the turn stopped needing input (ask_caller), so the next
        /// <see cref="BeginTurn"/> knows an incoming send is a resume, not a fresh turn.
        /// </summary>
        public void EndTurn(bool needsInput) => _resumePending = needsInput;
    }
}
