// File: ResumeHintTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// When the exit hint is worth printing, and when it is a trap.
//
// The hint's whole value is that it appears without being asked for, which is also why the
// conditions matter more than the wording: a line offered unprompted is believed. Printing
// "dm --resume <id>" after a session whose history was disabled, or one in which nothing was
// ever saved, hands someone a command that fails — and the failure reads as a bug in resume
// rather than as an answer to a question they never asked.
//
// The two guards are therefore the test, and they fail in opposite directions. Dropping the
// turns check produces a hint for an empty session; dropping the history check produces one
// for a session that was never written anywhere.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class ResumeHintTests
    {
        private const string Id = "2026-09-23T141205Z-pid18432";

        [Fact]
        public void WithHistoryOffThereIsNothingToResume()
        {
            Assert.Null(ResumeHint.Build(historyEnabled: false, turnsSaved: 4, sessionId: Id));
        }

        [Fact]
        public void WithNoTurnsSavedThereIsNothingWorthResuming()
        {
            // Launch, look at something, quit. The id exists; the session does not.
            Assert.Null(ResumeHint.Build(historyEnabled: true, turnsSaved: 0, sessionId: Id));
        }

        [Fact]
        public void WithHistoryOnAndAtLeastOneTurn_TheCommandIsSpelledOut()
        {
            string hint = ResumeHint.Build(historyEnabled: true, turnsSaved: 1, sessionId: Id);

            Assert.Equal(
                "Resume this session with:" + Environment.NewLine +
                "  dm --resume " + Id,
                hint);
        }

        [Fact]
        public void TheCommandSitsOnItsOwnLine_SoItCanBePastedWhole()
        {
            // Selecting a line should give a runnable command, not prose with a command in it.
            string hint = ResumeHint.Build(historyEnabled: true, turnsSaved: 2, sessionId: Id);
            string[] lines = hint.Split(Environment.NewLine);

            Assert.Equal(2, lines.Length);
            Assert.Equal("  dm --resume " + Id, lines[1]);
            Assert.Equal("dm --resume " + Id, lines[1].Trim());
        }

        [Fact]
        public void AResumedSessionNamesTheSameId()
        {
            // --resume adopts the id it was given (brief 07), so the hint on the way out of a
            // resumed session is the line that got you in. That identity is the feature: the
            // id survives however many times you come back to it.
            string first  = ResumeHint.Build(historyEnabled: true, turnsSaved: 2, sessionId: Id);
            string second = ResumeHint.Build(historyEnabled: true, turnsSaved: 9, sessionId: Id);

            Assert.Equal(first, second);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void WithoutAnIdThereIsNothingToPrint(string? sessionId)
        {
            // Defensive: an id this blank means something upstream failed, and a hint reading
            // "dm --resume" with nothing after it is worse than silence.
            Assert.Null(ResumeHint.Build(historyEnabled: true, turnsSaved: 3, sessionId));
        }

        [Fact]
        public void ANegativeCountIsTreatedAsNone()
        {
            Assert.Null(ResumeHint.Build(historyEnabled: true, turnsSaved: -1, sessionId: Id));
        }
    }
}
