// File: SteerMailboxTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The mailbox is the half of steering that is about timing rather than meaning: a caller's
// message arrives on one thread while a turn runs on another, and every way it can be lost
// is a race. It was extracted from HeadlessAgent so the TUI could steer with the same
// machinery, which makes these the tests that protect BOTH skins from the same bugs.
//
// What each guards, in the order the bugs actually happened:
//   * accepting into a mailbox nothing will drain (enqueue after the turn ended);
//   * losing a displaced steer silently instead of handing it back to be reported;
//   * splitting the final take from the flag clear, which reopens the first bug;
//   * carrying a stale steer from a previous turn into a new one.
//
// Steer.Apply — what a drained steer DOES to the prompt — is SteerDecisionTests' job and is
// deliberately not duplicated here.

using Xunit;

namespace DevMind.Core.Tests
{
    public class SteerMailboxTests
    {
        [Fact]
        public void EnqueueBeforeTheTurnOpens_IsRefused_AndQueuesNothing()
        {
            var box = new SteerMailbox();

            var result = box.Enqueue("too early", SteerMode.Suggest, out var superseded);

            Assert.False(result.Accepted);
            Assert.Null(superseded);
            Assert.False(box.IsTurnOpen);

            // Nothing queued: opening a turn now must find the mailbox clean.
            Assert.Null(box.BeginTurn());
            Assert.Null(box.Take());
        }

        [Fact]
        public void ASecondSteer_SupersedesTheFirst_AndHandsItBack()
        {
            var box = new SteerMailbox();
            box.BeginTurn();

            var first = box.Enqueue("first", SteerMode.Override, out var nothingYet);
            Assert.True(first.Accepted);
            Assert.False(first.Superseded);
            Assert.Null(nothingYet);

            var second = box.Enqueue("second", SteerMode.Suggest, out var displaced);

            Assert.True(second.Accepted);
            Assert.True(second.Superseded);

            // The displaced steer's MODE travels with the result, so a caller can say
            // "replaced a pending override" rather than the weaker "replaced a steer".
            Assert.Equal(SteerMode.Override, second.SupersededMode);

            // And the message itself comes back, so it can be reported rather than vanish.
            Assert.NotNull(displaced);
            Assert.Equal("first", displaced!.Message);
            Assert.Equal(SteerMode.Override, displaced.Mode);

            // Last write wins: only the second is pending, and only once.
            var taken = box.Take();
            Assert.NotNull(taken);
            Assert.Equal("second", taken!.Message);
            Assert.Equal(SteerMode.Suggest, taken.Mode);
            Assert.Null(box.Take());
        }

        // One test, because the atomicity of the pair is the whole point: taking the final
        // steer and closing the window are the same act. If they were separable, a steer
        // landing between them would be accepted into a mailbox nothing will ever read.
        [Fact]
        public void TakeAndEndTurn_ReturnsThePendingSteer_AndClosesTheWindow()
        {
            var box = new SteerMailbox();
            box.BeginTurn();
            box.Enqueue("never folded", SteerMode.Suggest, out _);

            var unconsumed = box.TakeAndEndTurn();

            Assert.NotNull(unconsumed);
            Assert.Equal("never folded", unconsumed!.Message);

            Assert.False(box.IsTurnOpen);

            var afterClose = box.Enqueue("too late", SteerMode.Override, out var displaced);
            Assert.False(afterClose.Accepted);
            Assert.Null(displaced);
            Assert.Null(box.Take());
        }

        [Fact]
        public void TakeAndEndTurn_WithNothingPending_StillClosesTheWindow()
        {
            var box = new SteerMailbox();
            box.BeginTurn();

            Assert.Null(box.TakeAndEndTurn());
            Assert.False(box.IsTurnOpen);
            Assert.False(box.Enqueue("too late", SteerMode.Suggest, out _).Accepted);
        }

        // A pending steer at turn start is an invariant violation — the previous turn's
        // close must have taken it. It is returned rather than dropped so the caller can
        // report it, and the new turn starts clean either way. Self-healing, and honest
        // about having healed.
        [Fact]
        public void BeginTurn_ReturnsAStaleSteer_AndStartsClean()
        {
            var box = new SteerMailbox();

            // Manufacture the violation: open, queue, and re-open without closing.
            box.BeginTurn();
            box.Enqueue("left over", SteerMode.Override, out _);

            var stale = box.BeginTurn();

            Assert.NotNull(stale);
            Assert.Equal("left over", stale!.Message);
            Assert.Equal(SteerMode.Override, stale.Mode);

            Assert.True(box.IsTurnOpen);
            Assert.Null(box.Take());          // the new turn is not carrying it
        }

        [Fact]
        public void BeginTurn_OnACleanMailbox_ReturnsNull_AndOpensTheWindow()
        {
            var box = new SteerMailbox();

            Assert.Null(box.BeginTurn());
            Assert.True(box.IsTurnOpen);
            Assert.True(box.Enqueue("now accepted", SteerMode.Suggest, out _).Accepted);
        }

        // A turn can be reopened after a clean close — the TUI runs many turns through one
        // mailbox, so the window has to be genuinely reusable rather than one-shot.
        [Fact]
        public void TheWindowReopens_ForTheNextTurn()
        {
            var box = new SteerMailbox();

            box.BeginTurn();
            box.TakeAndEndTurn();
            Assert.False(box.Enqueue("between turns", SteerMode.Suggest, out _).Accepted);

            Assert.Null(box.BeginTurn());
            Assert.True(box.Enqueue("next turn", SteerMode.Suggest, out _).Accepted);
            Assert.Equal("next turn", box.Take()!.Message);
        }

        // Enqueued from one thread while the turn drains on another is the real usage, so
        // the mailbox must not lose or duplicate under contention. Exactly one of the
        // racing enqueues survives as pending, and every displaced one is handed back —
        // accepted-but-untraceable is the outcome this class exists to make impossible.
        [Fact]
        public async Task ConcurrentEnqueues_LoseNothingWithoutHandingItBack()
        {
            var box = new SteerMailbox();
            box.BeginTurn();

            const int writers = 8;
            const int each = 50;

            var handedBack = new System.Collections.Concurrent.ConcurrentBag<string>();
            int accepted = 0;

            await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                for (int i = 0; i < each; i++)
                {
                    var r = box.Enqueue($"w{w}-{i}", SteerMode.Suggest, out var displaced);
                    if (r.Accepted) Interlocked.Increment(ref accepted);
                    if (displaced != null) handedBack.Add(displaced.Message);
                }
            })));

            Assert.Equal(writers * each, accepted);          // the window stayed open throughout

            var remaining = box.TakeAndEndTurn();
            Assert.NotNull(remaining);

            // Every accepted steer either is the survivor or was handed back exactly once.
            Assert.Equal(accepted - 1, handedBack.Count);
            Assert.DoesNotContain(remaining!.Message, handedBack);
        }
    }
}
