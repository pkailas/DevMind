// File: AnswerCapture.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The answer the operator saw, kept until history has it.
//
// A turn is saved to history after each response streams and before the tools in it run.
// That order is right for prose — the text is complete the moment the stream ends — and
// wrong for the answer itself, which the model now puts where the prompt tells it to: the
// task_done summary, or ask_caller's questions. Those are rendered by the executor, after
// the save, on the iteration that ends the turn, and the loop returns straight after. So
// every assistant row of a tool-driven turn was empty, the pairing that builds a resume
// dropped the exchange for having no answer, and "resume" reopened a session that had
// forgotten what it last said.
//
// This holds what AppendAnswer drew, so the terminal step can write it as one more
// assistant row. The pairing joins a segment's assistant rows, so the row lands where it
// belongs: at the end of the turn it answers.

using System.Text;

namespace DevMind
{
    /// <summary>Accumulates rendered answers until they are taken. Thread-safe.</summary>
    internal sealed class AnswerCapture
    {
        private readonly StringBuilder _text = new StringBuilder();
        private readonly object _lock = new object();

        /// <summary>Record an answer as it is drawn.</summary>
        public void Append(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            lock (_lock)
            {
                if (_text.Length > 0) _text.Append("\n\n");
                _text.Append(text.TrimEnd('\r', '\n'));
            }
        }

        /// <summary>Everything recorded since the last take, or null when nothing was; clears.</summary>
        public string Take()
        {
            lock (_lock)
            {
                if (_text.Length == 0) return null;
                string result = _text.ToString();
                _text.Clear();
                return result;
            }
        }

        /// <summary>Forget anything held — the start of a turn.</summary>
        public void Clear()
        {
            lock (_lock) _text.Clear();
        }
    }
}
