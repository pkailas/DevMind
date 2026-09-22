// File: CappedOutputWriter.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Head/tail cap for tool output echoed into the transcript.
//
// A single `git commit` with a real commit message, or a build, pushes dozens of lines into
// the scrollback and shoves the model's own reasoning off the top of the screen. The output
// still matters — it goes to the model and to history untouched — but the transcript is a
// place to glance at, and the interesting parts of a long output are its beginning and its
// end.
//
// So the first `head` lines stream through live (a long-running command still shows it is
// doing something), the last `tail` lines are held back and released at the end, and what
// fell between them is counted in one dim marker and parked for /expand.
//
// The writer is deliberately UI-free: it takes a sink delegate. The host passes one that
// appends to the Editor; a test passes one that appends to a list.

using System;
using System.Collections.Generic;

namespace DevMind
{
    /// <summary>One line destined for the transcript, with the colour it should carry.</summary>
    public readonly struct TranscriptLine
    {
        /// <summary>The line's text, without a trailing newline.</summary>
        public readonly string Text;

        /// <summary>The colour to render it in.</summary>
        public readonly OutputColor Color;

        public TranscriptLine(string text, OutputColor color)
        {
            Text  = text;
            Color = color;
        }
    }

    /// <summary>
    /// Caps line-by-line tool output at N transcript lines (head + tail), emitting one marker
    /// for the remainder and retaining it for <c>/expand</c>.
    /// </summary>
    public sealed class CappedOutputWriter
    {
        /// <summary>Transcript lines per tool call when nothing else is configured.</summary>
        public const int DefaultCap = 12;

        private readonly int _head;
        private readonly int _tail;
        private readonly bool _uncapped;
        private readonly Action<TranscriptLine> _sink;

        // Shell output arrives on the process's stdout/stderr reader threads, so the head
        // counter, the tail queue and the hidden list are all touched from more than one
        // thread. The lock also makes Flush atomic against a late-arriving line.
        private readonly object _gate = new object();
        private readonly Queue<TranscriptLine> _tailBuffer = new Queue<TranscriptLine>();
        private readonly List<TranscriptLine> _hidden = new List<TranscriptLine>();
        private int _shown;

        /// <summary>
        /// Create a writer that emits at most <paramref name="cap"/> lines to
        /// <paramref name="sink"/>. A cap of 0 (or less) is uncapped — every line goes through
        /// and nothing is ever hidden.
        /// </summary>
        public CappedOutputWriter(int cap, Action<TranscriptLine> sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _uncapped = cap <= 0;
            (_head, _tail) = Split(cap);
        }

        /// <summary>
        /// How a cap of N divides into head and tail lines: a third at the head (at least one),
        /// the rest at the tail — the default 12 becomes 4 + 8.
        /// <para>
        /// Tail-heavy because that is where the answer is. A build, a test run or any command
        /// whose point is "did it work" puts its verdict on the last lines; the head only has
        /// to show enough to recognise what is running. The case that argues the other way is
        /// git, which prints its summary in the MIDDLE — between the file list and the
        /// create-mode lines — and no head/tail ratio rescues that one, so it does not get a
        /// vote. <c>/expand</c> is the answer there.
        /// </para>
        /// </summary>
        public static (int head, int tail) Split(int cap)
        {
            if (cap <= 0) return (0, 0);
            int head = Math.Max(1, cap / 3);
            return (head, cap - head);
        }

        /// <summary>Lines withheld from the transcript, in order. Empty until <see cref="Flush"/>.</summary>
        public IReadOnlyList<TranscriptLine> Hidden => _hidden;

        /// <summary>Feed one output line (no trailing newline).</summary>
        public void Write(string text, OutputColor color)
        {
            var line = new TranscriptLine(text ?? string.Empty, color);

            lock (_gate)
            {
                if (_uncapped || _shown < _head)
                {
                    _shown++;
                    _sink(line);
                    return;
                }

                // Past the head: hold the most recent `tail` lines, and retire anything they
                // push out into the hidden remainder. Nothing is emitted until Flush, because
                // until the stream ends we do not know which lines are the last ones.
                _tailBuffer.Enqueue(line);
                while (_tailBuffer.Count > _tail)
                    _hidden.Add(_tailBuffer.Dequeue());
            }
        }

        /// <summary>
        /// The output has ended: emit the marker (only if anything was actually hidden) and
        /// release the held tail lines.
        /// </summary>
        public void Flush()
        {
            lock (_gate)
            {
                if (_hidden.Count > 0)
                    _sink(new TranscriptLine(Marker(_hidden.Count), OutputColor.Dim));

                while (_tailBuffer.Count > 0)
                    _sink(_tailBuffer.Dequeue());
            }
        }

        /// <summary>The dim one-line stand-in for the hidden remainder.</summary>
        public static string Marker(int hiddenCount)
            => $"… {hiddenCount:N0} lines hidden — /expand to show …";
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that runs its handler on the reporting thread.
    /// <para>
    /// <see cref="Progress{T}"/> posts to the captured synchronization context, and an agentic
    /// turn runs on a thread-pool thread with none — so every report would be queued to the
    /// pool, free to run concurrently with the others and to land AFTER the command finished.
    /// A capped writer cannot survive that: its flush would emit the tail before the last lines
    /// arrived. Reporting inline keeps the lines in the order the process produced them.
    /// </para>
    /// </summary>
    public sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
            => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        public void Report(T value) => _handler(value);
    }
}
