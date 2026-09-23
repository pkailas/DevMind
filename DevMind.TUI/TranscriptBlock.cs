// File: TranscriptBlock.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Where the indentation comes from.
//
// A tool call and the lines it produced are one thing, and a transcript that prints them at
// the same margin makes the reader work out which is which from the text. Four spaces says
// it for free: a call line starts at the left, everything it produced hangs under it, and
// the next call line brings the eye back. That is the third of the three devices Qwen Code
// and Claude Code spend, and the only one that needs any state at all.
//
// The state is one field — whether a call is open — and it lives here rather than in the
// host because the host is a Terminal.Gui view and nothing in it can be tested. What this
// does is take the chunks the engine writes, in the order it writes them, and return the
// lines to draw. That is a question with an answer, so it has tests.

using System.Collections.Generic;

namespace DevMind
{
    /// <summary>
    /// Assembles engine output into indented blocks. Stateful across calls, in arrival order;
    /// the caller feeds it every chunk that reaches the transcript's tool-line door.
    /// </summary>
    public sealed class TranscriptBlock
    {
        /// <summary>Columns of indent for a line nested under the call that produced it.</summary>
        public const int OutputIndent = 4;

        private static readonly string Indent = new string(' ', OutputIndent);

        private readonly bool _verbose;
        private bool _callOpen;

        /// <param name="verbose">
        /// <c>DEVMIND_TUI_VERBOSE</c>: the raw firehose. It has meant "show me exactly what
        /// the engine emitted" since the quiet filter was introduced, and translating lines
        /// under it would make it a differently-decorated view instead of the escape hatch it
        /// is — the thing you turn on precisely because you do not trust the presentation.
        /// </param>
        public TranscriptBlock(bool verbose) => _verbose = verbose;

        /// <summary>True while output is nesting under a call line.</summary>
        public bool IsCallOpen => _callOpen;

        /// <summary>
        /// Something that is not engine output was written — model prose, a code block, a
        /// painted diff. It ends the block, so the next tool output does not hang under a
        /// call the reader has lost sight of.
        /// </summary>
        public void CloseBlock() => _callOpen = false;

        /// <summary>
        /// Translate and lay out one chunk. A chunk may hold any number of lines, or part of
        /// one; each complete line is translated, and the trailing fragment is emitted as it
        /// stands so a caller that streams mid-line is never held up.
        /// </summary>
        public IReadOnlyList<TranscriptLine> Accept(string text, OutputColor color)
        {
            var result = new List<TranscriptLine>();
            if (string.IsNullOrEmpty(text)) return result;

            if (_verbose)
            {
                result.Add(new TranscriptLine(text, color));
                return result;
            }

            // Split keeping the newlines: "a\nb" is two lines, "a\n" is one line and an empty
            // remainder, and the difference decides whether the last piece is a whole line.
            int start = 0;
            while (start <= text.Length)
            {
                int nl = text.IndexOf('\n', start);
                bool complete = nl >= 0;
                string line = complete ? text.Substring(start, nl - start) : text.Substring(start);

                if (!complete && line.Length == 0) break;   // nothing after the final newline

                Emit(result, line, color, complete);

                if (!complete) break;
                start = nl + 1;
            }

            return result;
        }

        private void Emit(List<TranscriptLine> result, string rawLine, OutputColor color, bool complete)
        {
            string line = rawLine.TrimEnd('\r');
            string newline = complete ? "\n" : string.Empty;

            // A blank line is blank. It does NOT close the block: shell and build output are
            // full of blank lines, and de-indenting the rest of a command's output at its
            // first one would break exactly the case the indent was added for.
            if (line.Trim().Length == 0)
            {
                result.Add(new TranscriptLine(line + newline, color));
                return;
            }

            TranscriptEvent ev = TranscriptVocabulary.Translate(line, color);

            switch (ev.Kind)
            {
                case TranscriptKind.Call:
                    _callOpen = true;
                    result.Add(new TranscriptLine(ev.Render() + newline, ev.Color));
                    break;

                case TranscriptKind.State:
                case TranscriptKind.User:
                    _callOpen = false;
                    result.Add(new TranscriptLine(ev.Render() + newline, ev.Color));
                    break;

                default:
                    result.Add(new TranscriptLine(
                        (_callOpen ? Indent : string.Empty) + ev.Render() + newline, ev.Color));
                    break;
            }
        }
    }
}
