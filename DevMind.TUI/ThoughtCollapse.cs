// File: ThoughtCollapse.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The one-line stand-in for a response's hidden reasoning.
//
// With thinking display off the transcript said nothing at all while the model reasoned —
// a gap of many seconds with no explanation, which reads as a hang. A single dim line says
// what happened and how long it took, and /expand still has the text.
//
// ThinkFilter is asked for the reasoning even when it will not be shown; this accumulates
// it per response and formats the summary. Kept separate from the token path so the format
// and the accounting are testable without a running UI.

using System;
using System.Globalization;
using System.Text;

namespace DevMind
{
    /// <summary>
    /// Accumulates one response's reasoning text and renders the collapsed summary line.
    /// Not thread-safe: the SSE token callback is the only writer.
    /// </summary>
    public sealed class ThoughtCollapse
    {
        // ThinkFilter tags the first reasoning chunk of a block when asked to surface it.
        // The tag is display furniture for the inline path; it is not part of the thought,
        // so it is stripped before the text is counted or parked.
        private const string Tag = "[THINKING] ";

        private readonly StringBuilder _text = new StringBuilder();
        private readonly bool _showInline;

        /// <summary>
        /// Create the collapser for one response. With <paramref name="showThinking"/> true
        /// the reasoning is displayed inline as it always was and nothing is collapsed — the
        /// summary line would then be a stand-in for text that is right there above it.
        /// </summary>
        public ThoughtCollapse(bool showThinking) => _showInline = showThinking;

        /// <summary>True when reasoning is displayed inline rather than collapsed.</summary>
        public bool ShowsInline => _showInline;

        /// <summary>Reasoning text has been collapsed for this response.</summary>
        public bool HasThought => _text.Length > 0;

        /// <summary>The accumulated reasoning, tag stripped.</summary>
        public string Text => _text.ToString();

        /// <summary>
        /// Route one chunk of reasoning. Returns the text to append inline, or null when the
        /// chunk was collapsed instead. One decision, made here rather than at the call site,
        /// so "collapsed a thought that was already on screen" is a test failure.
        /// </summary>
        public string Route(string chunk)
        {
            if (_showInline) return chunk;
            Append(chunk);
            return null;
        }

        /// <summary>Add one chunk of reasoning as it streams.</summary>
        public void Append(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;

            if (_text.Length == 0 && chunk.StartsWith(Tag, StringComparison.Ordinal))
                chunk = chunk.Substring(Tag.Length);

            _text.Append(chunk);
        }

        /// <summary>Drop what has been accumulated — call between responses.</summary>
        public void Reset() => _text.Clear();

        /// <summary>
        /// The live line, rewritten in place while reasoning streams:
        /// <c>∵ Thinking… 11s</c>. Whole seconds — a tenths digit on a line that redraws
        /// once a second is motion without information.
        /// </summary>
        public static string Live(TimeSpan elapsed)
            => string.Format(CultureInfo.InvariantCulture, "∵ Thinking… {0}s", Seconds(elapsed));

        /// <summary>
        /// The dim line that replaces it when the reasoning is done:
        /// <c>∴ Thought for 5s (~1,240 tokens) — ctrl+o or /expand to show</c>.
        /// <para>
        /// The token count is an estimate (chars/4) and says so with the tilde. The real
        /// count is only known per response from the server's usage, which lumps reasoning
        /// and visible output together — so a precise-looking number here would be a
        /// precise-looking lie.
        /// </para>
        /// </summary>
        /// <returns>The summary line, or null when there is nothing collapsed to stand in for.</returns>
        public string Summarize(TimeSpan elapsed)
            => HasThought ? Summary(elapsed, _text.Length) : null;

        /// <summary>The summary line for a given elapsed time and character count.</summary>
        public static string Summary(TimeSpan elapsed, int charCount)
        {
            int tokens = charCount / 4;
            return string.Format(
                CultureInfo.InvariantCulture,
                "∴ Thought for {0}s (~{1:N0} tokens) — ctrl+o or /expand to show",
                Seconds(elapsed), tokens);
        }

        // Floored at 1: a thought that happened took at least a moment, and "0s" reads as
        // "did not happen".
        private static int Seconds(TimeSpan elapsed)
            => Math.Max(1, (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero));
    }
}
