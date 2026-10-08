// File: StreamPhase.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// When the status bar leaves "Thinking…" for "Generating…".
//
// The decision used to sit inline in RunTurnAsync's onToken lambda, unreachable without
// Terminal.Gui — and it was wrong there: the first reasoning token flipped the phase, so a
// model streaming delta.reasoning_content showed "Generating…" for its whole think. Thinking
// only ever rendered during prompt processing. Like SteerFold, the decision moved here so a
// test can pin it; the lambda keeps only the call to StopThinkingTimer.

namespace DevMind
{
    /// <summary>Decides, per streamed chunk, whether the Thinking phase is over.</summary>
    public static class StreamPhase
    {
        /// <summary>
        /// True when this chunk ends the Thinking phase: real visible model content. Reasoning
        /// (<paramref name="thinkText"/>) is still thinking, and DevMind's own status lines
        /// ([CONTEXT], [LLM], …) flow through the same callback but are not the model talking.
        /// </summary>
        /// <param name="isStatus">The chunk is a DevMind status line, not a model token.</param>
        /// <param name="thinkText">The reasoning part of the chunk, as ThinkFilter split it.</param>
        /// <param name="visible">The visible part of the chunk, as ThinkFilter split it.</param>
        public static bool EndsThinking(bool isStatus, string thinkText, string visible)
        {
            _ = thinkText; // reasoning never ends the phase — named so that is a visible choice
            return !isStatus && !string.IsNullOrEmpty(visible);
        }
    }
}
