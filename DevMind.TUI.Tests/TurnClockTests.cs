// File: TurnClockTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Pins the turn-clock advance policy (TurnClock) that DevMind.TUI/Program.cs applies at the
// user-turn boundary. The rule was lost twice — 64f3a5c dropped the !shellLoopPending guard
// when the TUI was written, and ba4cdbe copied the per-iteration increment into headless — so
// the decision lives in a pure state machine and is driven here through the scenario that
// actually bites: a user send, an ask_caller question, the answer (a new agentic cycle in the
// SAME session, which must NOT advance a second time), and the next fresh send.
//
// Division of labour with the source-derived guard (DevMind.Core.Tests
// AgenticLoopTurnClockParityTests): THIS file proves the DECISION — one advance per fresh
// send, none for a resume. The guard proves the PLACEMENT — the increment sits at the turn
// boundary, not inside the agentic loop, so a multi-iteration cycle advances exactly once.
// Together they close the gap from either direction.

using DevMind;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class TurnClockTests
    {
        // A single fresh send advances the clock exactly once.
        [Fact]
        public void FreshSend_AdvancesOnce()
        {
            var clock = new TurnClock();

            Assert.True(clock.BeginTurn());      // the user typed a message
            clock.EndTurn(needsInput: false);    // …ended with task_done

            Assert.Equal(1, clock.Advances);
        }

        // THE load-bearing case. A send that ends in an ask_caller question, then the answer
        // (a new agentic cycle in the same session), then the next fresh send. The answer
        // resumes the SAME turn — it must NOT advance a second time. So the whole exchange is
        // TWO advances (send 1 + next send), not three.
        [Fact]
        public void AskCallerAnswer_DoesNotReAdvance_ThenNextSendDoes()
        {
            var clock = new TurnClock();

            Assert.True(clock.BeginTurn());      // send 1 → advance
            clock.EndTurn(needsInput: true);     // …the model called ask_caller

            Assert.False(clock.BeginTurn());     // the ANSWER → resume, do NOT advance
            clock.EndTurn(needsInput: false);    // …answered; task complete

            Assert.True(clock.BeginTurn());      // next fresh send → advance
            clock.EndTurn(needsInput: false);

            Assert.Equal(2, clock.Advances);     // send 1 + next send; the answer did not count
        }

        // Two consecutive fresh sends each advance — the clock is not latched: a
        // needs_input=false turn clears the resume flag so the following send counts.
        [Fact]
        public void ConsecutiveFreshSends_EachAdvance()
        {
            var clock = new TurnClock();

            Assert.True(clock.BeginTurn());
            clock.EndTurn(needsInput: false);
            Assert.True(clock.BeginTurn());
            clock.EndTurn(needsInput: false);

            Assert.Equal(2, clock.Advances);
        }

        // A resume that itself ends in another question: the answer to the FIRST question does
        // not advance, but the model asks AGAIN, so the SECOND answer also does not advance,
        // and only a genuinely fresh send after does. Three cycles, one advance.
        [Fact]
        public void ChainedQuestions_OnlyTheFreshSendAdvances()
        {
            var clock = new TurnClock();

            Assert.True(clock.BeginTurn());      // fresh send → advance
            clock.EndTurn(needsInput: true);     // question 1
            Assert.False(clock.BeginTurn());     // answer 1 → no advance
            clock.EndTurn(needsInput: true);     // question 2
            Assert.False(clock.BeginTurn());     // answer 2 → no advance
            clock.EndTurn(needsInput: false);    // done
            Assert.True(clock.BeginTurn());      // next fresh send → advance
            clock.EndTurn(needsInput: false);

            Assert.Equal(2, clock.Advances);     // two fresh sends; both answers held
        }
    }
}
