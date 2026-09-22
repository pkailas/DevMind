// File: ExpandBuffer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// What /expand hands back.
//
// The transcript now hides two things it used to show in full: the middle of a long tool
// output, and the model's reasoning when thinking display is off. Hiding is only tolerable
// if the hidden text is one command away, so it is parked here.
//
// Parked, not re-rendered in place. The output pane is append-only — every write goes
// through the coalesced span queue and lands at the document end — so "expand" means
// "append the full text now", not "rewrite the line up there". Only the LAST of each kind
// is kept: the question /expand answers is "what did that just hide", and keeping a history
// would make the command ambiguous and the memory unbounded.

using System;
using System.Collections.Generic;

namespace DevMind
{
    /// <summary>The outcome of an <c>/expand</c> request.</summary>
    public readonly struct ExpandResult
    {
        /// <summary>Lines to append to the transcript, in order. Empty when nothing matched.</summary>
        public readonly IReadOnlyList<TranscriptLine> Lines;

        /// <summary>The message for the command result — what was expanded, or why nothing was.</summary>
        public readonly string Message;

        /// <summary>True when the request could not be served.</summary>
        public readonly bool IsError;

        public ExpandResult(IReadOnlyList<TranscriptLine> lines, string message, bool isError)
        {
            Lines    = lines;
            Message  = message;
            IsError  = isError;
        }
    }

    /// <summary>
    /// Holds the most recent hidden thought and the most recent hidden tool output, and
    /// resolves an <c>/expand</c> argument to one of them. UI-free, so the command's
    /// behaviour is testable without Terminal.Gui.
    /// </summary>
    public sealed class ExpandBuffer
    {
        private static readonly TranscriptLine[] None = Array.Empty<TranscriptLine>();

        private readonly object _gate = new object();

        private string _thought;
        private IReadOnlyList<TranscriptLine> _output = None;

        // Which kind was parked last, so a bare /expand can mean "the thing that just
        // happened" rather than picking a fixed winner.
        private int _seq;
        private int _thoughtSeq;
        private int _outputSeq;

        /// <summary>Park the reasoning text of the response that just finished.</summary>
        public void ParkThought(string text)
        {
            lock (_gate)
            {
                _thought    = string.IsNullOrEmpty(text) ? null : text;
                _thoughtSeq = _thought == null ? 0 : ++_seq;
            }
        }

        /// <summary>Park the lines a capped tool output withheld, in order.</summary>
        public void ParkOutput(IReadOnlyList<TranscriptLine> lines)
        {
            lock (_gate)
            {
                // Copied, not aliased: the writer's list is its own working state.
                _output    = lines != null && lines.Count > 0 ? new List<TranscriptLine>(lines) : None;
                _outputSeq = _output.Count == 0 ? 0 : ++_seq;
            }
        }

        /// <summary>Forget everything parked — a new session starts with nothing to expand.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _thought    = null;
                _output     = None;
                _thoughtSeq = 0;
                _outputSeq  = 0;
            }
        }

        /// <summary>
        /// Resolve an <c>/expand</c> argument: "thought", "output", or empty for whichever
        /// was parked most recently. The parked text is NOT consumed — expanding twice is
        /// harmless, and losing it to a mistyped argument would not be.
        /// </summary>
        public ExpandResult Resolve(string argument)
        {
            string arg = (argument ?? string.Empty).Trim();

            lock (_gate)
            {
                if (arg.Length == 0)
                {
                    if (_thoughtSeq == 0 && _outputSeq == 0)
                        return new ExpandResult(None, "Nothing to expand — no hidden thought or tool output yet.", false);
                    return _thoughtSeq > _outputSeq ? Thought() : Output();
                }

                if (arg.Equals("thought", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("thinking", StringComparison.OrdinalIgnoreCase))
                    return Thought();

                if (arg.Equals("output", StringComparison.OrdinalIgnoreCase))
                    return Output();

                return new ExpandResult(None, "Usage: /expand [thought|output]", true);
            }
        }

        private ExpandResult Thought()
        {
            if (_thought == null)
                return new ExpandResult(None, "No hidden thought — the last response did not reason, or thinking display is on.", false);

            return new ExpandResult(
                new[] { new TranscriptLine(_thought, OutputColor.Thinking) },
                "Expanded the last hidden thought.",
                false);
        }

        private ExpandResult Output()
        {
            if (_output.Count == 0)
                return new ExpandResult(None, "No hidden output — the last tool call fit inside the line cap.", false);

            return new ExpandResult(_output, $"Expanded {_output.Count:N0} hidden output line(s).", false);
        }
    }
}
