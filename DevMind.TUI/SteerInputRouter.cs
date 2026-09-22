// File: SteerInputRouter.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// What Enter means depends on whether a turn is running, and that decision is the whole of
// the steering feature that is worth testing on its own. Kept as a pure static so it can be
// exercised without Terminal.Gui: the input handler in Program.cs then has nothing left to
// get wrong except calling this and acting on the answer.
//
// The rule, in one line: while a turn is steerable, typing is a steer; otherwise it is a
// prompt.
//
// Plain text during a turn is a SUGGESTION, not a queued next-turn prompt. Someone typing
// while they watch the model work is nudging the work in front of them — that is what the
// feature is for. Silently deferring it to a turn that has not started yet would be a
// different feature wearing the same gesture, and the user would not find out which one
// they got until the turn ended.
//
// /override and /steer are explicit because Steer.Frame generates different framing from
// the mode, and an explicit flag cannot misfire on wording the way a keyword scan can. The
// same reasoning already governs devmind_task_steer's mode parameter.

using System;

namespace DevMind
{
    /// <summary>What the TUI should do with a line the user just entered.</summary>
    public enum SteerInputAction
    {
        /// <summary>Nothing — empty input, or a steer command with no turn to steer.</summary>
        Ignore,

        /// <summary>Start a new turn with this text, the pre-existing behaviour.</summary>
        Send,

        /// <summary>Queue this text as a steer on the running turn.</summary>
        Steer,

        /// <summary>A steer command was used with no turn running; say so and do nothing.</summary>
        RefuseNoTurn,

        /// <summary>
        /// Some other slash command was typed while a turn is running. Refused, not folded
        /// in as a steer: the box used to be disabled during a turn, and commands like
        /// /compact or /new mutate the very history the turn is streaming into. Quietly
        /// turning one into a steer message would also be wrong in a way the user could
        /// not see — they typed a command and would get a nudge.
        /// </summary>
        RefuseCommandDuringTurn,
    }

    /// <summary>The routed decision: what to do, and with what text.</summary>
    public readonly struct SteerInputRoute
    {
        public SteerInputAction Action { get; }

        /// <summary>The text to send or steer with, trimmed. Empty for Ignore/RefuseNoTurn.</summary>
        public string Message { get; }

        /// <summary>Meaningful only when <see cref="Action"/> is Steer.</summary>
        public SteerMode Mode { get; }

        public SteerInputRoute(SteerInputAction action, string message, SteerMode mode)
        {
            Action = action;
            Message = message;
            Mode = mode;
        }
    }

    /// <summary>
    /// Decides whether a line of input starts a turn, steers the running one, or does
    /// nothing. Pure: no Terminal.Gui, no mailbox, no state.
    /// </summary>
    public static class SteerInputRouter
    {
        /// <summary>Explicit synonym for a suggestion, for a user who wants to be sure.</summary>
        public const string SteerCommand = "/steer";

        /// <summary>Queues an override: stop the current approach and change course.</summary>
        public const string OverrideCommand = "/override";

        /// <summary>
        /// Routes one entered line.
        /// </summary>
        /// <param name="input">Raw text from the input box.</param>
        /// <param name="turnSteerable">
        /// Whether a turn is open to steering — SteerMailbox.IsTurnOpen, NOT merely "some
        /// long operation is running". A /digest or a library ingest is busy but has no
        /// iteration boundaries to fold a steer into, so it is not steerable and typing
        /// during one is not a steer.
        /// </param>
        public static SteerInputRoute Route(string input, bool turnSteerable)
        {
            string text = (input ?? string.Empty).Trim();
            if (text.Length == 0)
                return new SteerInputRoute(SteerInputAction.Ignore, string.Empty, SteerMode.Suggest);

            if (TryCommand(text, OverrideCommand, out string overrideBody))
                return RouteCommand(overrideBody, SteerMode.Override, turnSteerable);

            if (TryCommand(text, SteerCommand, out string steerBody))
                return RouteCommand(steerBody, SteerMode.Suggest, turnSteerable);

            if (!turnSteerable)
                return new SteerInputRoute(SteerInputAction.Send, text, SteerMode.Suggest);

            // A turn is running. Any OTHER command is refused rather than steered with:
            // the command table is not this router's to interpret, and the commands that
            // matter mid-turn are the ones that would rewrite the running turn's history.
            if (text[0] == '/')
                return new SteerInputRoute(SteerInputAction.RefuseCommandDuringTurn, text, SteerMode.Suggest);

            return new SteerInputRoute(SteerInputAction.Steer, text, SteerMode.Suggest);
        }

        /// <summary>
        /// A steer command with no turn is refused rather than silently dropped, matching
        /// what devmind_task_steer tells a caller who steers a job that is not running. A
        /// steer command with no text is nothing at all, running or not.
        /// </summary>
        private static SteerInputRoute RouteCommand(string body, SteerMode mode, bool turnSteerable)
        {
            if (body.Length == 0)
                return new SteerInputRoute(SteerInputAction.Ignore, string.Empty, mode);

            return turnSteerable
                ? new SteerInputRoute(SteerInputAction.Steer, body, mode)
                : new SteerInputRoute(SteerInputAction.RefuseNoTurn, body, mode);
        }

        /// <summary>
        /// Matches "/cmd rest" or a bare "/cmd", case-insensitively. Requires whitespace
        /// after the command so "/overridefoo" is not read as "/override foo".
        /// </summary>
        private static bool TryCommand(string text, string command, out string body)
        {
            body = string.Empty;

            if (!text.StartsWith(command, StringComparison.OrdinalIgnoreCase))
                return false;

            if (text.Length == command.Length)
                return true;                                  // bare command, empty body

            if (!char.IsWhiteSpace(text[command.Length]))
                return false;                                 // a longer word that merely starts the same

            body = text.Substring(command.Length).Trim();
            return true;
        }
    }
}
