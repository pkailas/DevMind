// File: SteerInputRouterTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Enter means two different things depending on whether a turn is running, and getting that
// wrong is the worst failure this feature can have: a brief the user meant to steer with
// becomes a new turn, or a new prompt is swallowed as a nudge into a turn that is ending.
//
// The routing is a pure static precisely so it can be pinned here without Terminal.Gui —
// the input handler then has nothing left to get wrong except calling it.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class SteerInputRouterTests
    {
        // ── While a turn is steerable ──────────────────────────────────────────

        [Fact]
        public void PlainText_DuringATurn_IsASuggestion()
        {
            var route = SteerInputRouter.Route("also check the CLI", turnSteerable: true);

            Assert.Equal(SteerInputAction.Steer, route.Action);
            Assert.Equal(SteerMode.Suggest, route.Mode);
            Assert.Equal("also check the CLI", route.Message);
        }

        [Fact]
        public void OverrideCommand_DuringATurn_IsAnOverride()
        {
            var route = SteerInputRouter.Route("/override stop and fix the build first", turnSteerable: true);

            Assert.Equal(SteerInputAction.Steer, route.Action);
            Assert.Equal(SteerMode.Override, route.Mode);
            Assert.Equal("stop and fix the build first", route.Message);
        }

        [Fact]
        public void SteerCommand_DuringATurn_IsAnExplicitSuggestion()
        {
            var route = SteerInputRouter.Route("/steer also update the docs", turnSteerable: true);

            Assert.Equal(SteerInputAction.Steer, route.Action);
            Assert.Equal(SteerMode.Suggest, route.Mode);
            Assert.Equal("also update the docs", route.Message);
        }

        // ── While no turn is running ───────────────────────────────────────────

        [Fact]
        public void PlainText_WithNoTurn_StartsOne()
        {
            var route = SteerInputRouter.Route("read the brief and carry it out", turnSteerable: false);

            Assert.Equal(SteerInputAction.Send, route.Action);
            Assert.Equal("read the brief and carry it out", route.Message);
        }

        // Refused rather than silently dropped, and refused rather than sent as a prompt:
        // "/steer x" with nothing running is a mistake worth telling the user about, the
        // same way devmind_task_steer refuses a steer for a job that is not running.
        [Theory]
        [InlineData("/steer do the thing")]
        [InlineData("/override do the other thing")]
        public void ASteerCommand_WithNoTurn_IsRefused(string input)
        {
            var route = SteerInputRouter.Route(input, turnSteerable: false);

            Assert.Equal(SteerInputAction.RefuseNoTurn, route.Action);
        }

        // ── Nothing to do ──────────────────────────────────────────────────────

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t  \n ")]
        public void EmptyInput_DoesNothing_EitherWay(string input)
        {
            Assert.Equal(SteerInputAction.Ignore, SteerInputRouter.Route(input, turnSteerable: true).Action);
            Assert.Equal(SteerInputAction.Ignore, SteerInputRouter.Route(input, turnSteerable: false).Action);
        }

        [Fact]
        public void NullInput_DoesNothing()
        {
            Assert.Equal(SteerInputAction.Ignore, SteerInputRouter.Route(null!, turnSteerable: true).Action);
        }

        // A bare command carries no message, so there is nothing to queue. Ignored rather
        // than refused: the user has not asked for anything yet.
        [Theory]
        [InlineData("/steer")]
        [InlineData("/override")]
        [InlineData("/steer    ")]
        public void ABareSteerCommand_DoesNothing(string input)
        {
            Assert.Equal(SteerInputAction.Ignore, SteerInputRouter.Route(input, turnSteerable: true).Action);
        }

        // ── Parsing edges ──────────────────────────────────────────────────────

        // A word that merely starts with the command is not the command. Without the
        // whitespace requirement "/overridden the tests" would queue an override of
        // "n the tests", which is worse than not matching at all.
        [Theory]
        [InlineData("/overridden the earlier decision")]
        [InlineData("/steering committee notes")]
        public void ALongerWordStartingWithACommand_IsNotThatCommand(string input)
        {
            // Not an override and not a suggestion: it is some other slash command as far as
            // this router can tell, which during a turn is refused rather than guessed at.
            var route = SteerInputRouter.Route(input, turnSteerable: true);
            Assert.Equal(SteerInputAction.RefuseCommandDuringTurn, route.Action);

            // With no turn running it is simply sent, exactly as it was before steering existed.
            Assert.Equal(SteerInputAction.Send, SteerInputRouter.Route(input, turnSteerable: false).Action);
        }

        [Theory]
        [InlineData("/OVERRIDE change course")]
        [InlineData("/Override change course")]
        public void CommandsAreCaseInsensitive(string input)
        {
            var route = SteerInputRouter.Route(input, turnSteerable: true);

            Assert.Equal(SteerMode.Override, route.Mode);
            Assert.Equal("change course", route.Message);
        }

        [Fact]
        public void SurroundingWhitespaceIsTrimmed()
        {
            var route = SteerInputRouter.Route("   /override   change course   ", turnSteerable: true);

            Assert.Equal(SteerMode.Override, route.Mode);
            Assert.Equal("change course", route.Message);
        }

        // Refused, not folded in as a steer. The box used to be disabled during a turn,
        // and these are exactly the commands that rewrite the history the turn is streaming
        // into. Turning one into a steer message would also be invisible to the user: they
        // typed a command and would silently get a nudge.
        [Theory]
        [InlineData("/compact")]
        [InlineData("/new")]
        [InlineData("/dir C:\\somewhere")]
        public void AnUnrelatedSlashCommand_DuringATurn_IsRefused(string input)
        {
            var route = SteerInputRouter.Route(input, turnSteerable: true);

            Assert.Equal(SteerInputAction.RefuseCommandDuringTurn, route.Action);
            Assert.Equal(input, route.Message);
        }

        // ...but the same command with no turn running is an ordinary command again.
        [Fact]
        public void AnUnrelatedSlashCommand_WithNoTurn_IsSentAsBefore()
        {
            var route = SteerInputRouter.Route("/compact", turnSteerable: false);

            Assert.Equal(SteerInputAction.Send, route.Action);
            Assert.Equal("/compact", route.Message);
        }
    }
}
