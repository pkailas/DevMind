// File: SteerFold.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The per-iteration drain, minus the parts that need a running UI.
//
// This exists because of where the drain lives: inside RunTurnAsync, a method that takes an
// IApplication, a TuiAgenticHost and a live LoopState, and which no test can call without
// standing up Terminal.Gui. Left inline, the one piece of steering unique to the TUI — the
// decision made at each iteration boundary — would have been the only piece with no test
// behind it, verifiable only by running the app and typing.
//
// So the decision moved here and RunTurnAsync keeps only what is genuinely UI: appending the
// returned line to the transcript in the returned colour.
//
// The decision itself is not reimplemented. Steer.IsLastIteration and Steer.Apply are the
// same functions the headless drain calls, already pinned by SteerDecisionTests; what this
// adds is the wiring — that the TUI passes ITS depth values to that predicate, in the right
// order, and reports each disposition. Getting the arguments the wrong way round is exactly
// the kind of mistake a pure function cannot protect you from.

namespace DevMind
{
    /// <summary>The outcome of folding a pending steer into the prompt for one iteration.</summary>
    public readonly struct SteerFoldResult
    {
        /// <summary>The prompt to send — folded, or unchanged when the steer was rejected.</summary>
        public string Prompt { get; }

        /// <summary>The transcript line describing what happened. Never empty.</summary>
        public string TranscriptLine { get; }

        /// <summary>Colour for that line: rejection reads as a warning, a fold as success.</summary>
        public OutputColor Color { get; }

        /// <summary>What was decided, for callers that need more than the line.</summary>
        public SteerDisposition Disposition { get; }

        public SteerFoldResult(string prompt, string line, OutputColor color, SteerDisposition disposition)
        {
            Prompt = prompt;
            TranscriptLine = line;
            Color = color;
            Disposition = disposition;
        }
    }

    /// <summary>
    /// Folds a drained steer into the prompt about to be sent, and says what to show for it.
    /// Pure: no Terminal.Gui, no mailbox, no state.
    /// </summary>
    public static class SteerFold
    {
        /// <summary>
        /// Applies a pending steer at an iteration boundary.
        /// </summary>
        /// <param name="currentPrompt">The prompt this iteration would otherwise send.</param>
        /// <param name="message">The steer text, as the user typed it.</param>
        /// <param name="mode">Suggestion or override.</param>
        /// <param name="maxDepth">The session's agentic depth cap (options.AgenticLoopMaxDepth).</param>
        /// <param name="agenticDepth">The loop's current depth (state.AgenticDepth).</param>
        public static SteerFoldResult Fold(
            string currentPrompt, string message, SteerMode mode, int maxDepth, int agenticDepth)
        {
            bool lastIteration = Steer.IsLastIteration(maxDepth, agenticDepth);
            (string prompt, SteerDisposition disposition) =
                Steer.Apply(currentPrompt, message, mode, lastIteration);

            string modeTag = mode == SteerMode.Override ? "override" : "suggest";

            // Wording follows the headless drain's, so a transcript from the TUI and one from
            // a delegated job describe the same event the same way.
            string line = disposition == SteerDisposition.Rejected
                ? $"[STEER] {modeTag} REJECTED — last iteration, no iterations left to change course. {message}\n"
                : $"[STEER] {modeTag} folded into the prompt at this iteration boundary. {message}\n";

            OutputColor color = disposition == SteerDisposition.Rejected
                ? OutputColor.Warning
                : OutputColor.Success;

            return new SteerFoldResult(prompt, line, color, disposition);
        }
    }
}
