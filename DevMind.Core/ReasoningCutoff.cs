// File: ReasoningCutoff.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-71 — carry a cut-off thought into the next iteration. Reasoning is kept out of the
// conversation history on purpose (LlmClient streams reasoning_content to onToken and
// LastReasoning, never into the stored assistant message), so whatever the model worked out
// while thinking is gone by the next iteration. When the backend's thinking budget cuts the
// reasoning off mid-debate, the model goes straight to a tool call having recorded nothing,
// and the next iteration re-opens the same question: job-2171 re-debated one test-host
// question across many iterations until a caller steer broke the loop (19 of its 72
// iterations were cut off; job-2165 22/94, job-2170 25/99, job-2173 14/47).
//
// Strata appends its cut-off sentence to the reasoning stream itself — a captured stream
// (2026-10-05) showed "I have thought about this long enough; time to give my answer." as the
// last reasoning_content deltas, followed by ordinary content — so detection reads the
// captured reasoning, and the marker never reaches history.
//
//   ReasoningCutoff       pure helpers: marker detection, tail extraction, inline <think> text.
//   ReasoningCutoffCarry  per-turn state: counts cut-offs and builds the one-shot harness note
//                         (escalated on the third consecutive cut-off) for the next request.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>Detection and tail extraction for reasoning cut off by the thinking budget.</summary>
    public static class ReasoningCutoff
    {
        /// <summary>The cut-off sentence's stable prefix as Strata emits it.</summary>
        public static readonly IReadOnlyList<string> DefaultMarkers =
            new[] { "I have thought about this long enough" };

        /// <summary>Most characters of reasoning quoted back in the note.</summary>
        public const int TailChars = 600;

        /// <summary>
        /// The markers in force: <paramref name="configured"/> (devmind.json
        /// "reasoningCutoffMarkers") with blanks dropped, or <see cref="DefaultMarkers"/> when it
        /// is absent. An explicit empty array means no markers, i.e. detection off.
        /// </summary>
        public static IReadOnlyList<string> ResolveMarkers(IReadOnlyList<string> configured)
        {
            if (configured == null) return DefaultMarkers;
            return configured.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToArray();
        }

        /// <summary>
        /// True when <paramref name="reasoning"/> contains a marker (case-insensitive);
        /// <paramref name="markerIndex"/> is the start of the LAST occurrence, since the budget's
        /// sentence is appended at the end and the tail is what precedes it.
        /// </summary>
        public static bool TryFindMarker(string reasoning, IReadOnlyList<string> markers, out int markerIndex)
        {
            markerIndex = -1;
            if (string.IsNullOrEmpty(reasoning) || markers == null) return false;
            foreach (string marker in markers)
            {
                if (string.IsNullOrEmpty(marker)) continue;
                int i = reasoning.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i > markerIndex) markerIndex = i;
            }
            return markerIndex >= 0;
        }

        /// <summary>
        /// The reasoning before <paramref name="markerIndex"/>: at most <paramref name="maxChars"/>
        /// characters, starting at a line or sentence start when it had to be cut, whitespace
        /// collapsed. Never includes the marker. "" when nothing but whitespace preceded it.
        /// </summary>
        public static string ExtractTail(string reasoning, int markerIndex, int maxChars = TailChars)
        {
            if (string.IsNullOrEmpty(reasoning) || markerIndex <= 0) return "";
            string before = reasoning.Substring(0, Math.Min(markerIndex, reasoning.Length));

            string window = before;
            if (before.Length > maxChars)
            {
                window = before.Substring(before.Length - maxChars);
                int start = BoundaryStart(window);
                if (start > 0 && !string.IsNullOrWhiteSpace(window.Substring(start)))
                    window = window.Substring(start);
            }

            return Regex.Replace(window, @"\s+", " ").Trim();
        }

        // The first line start or sentence start in the window, else the first word start, else 0.
        private static int BoundaryStart(string window)
        {
            for (int i = 0; i < window.Length - 1; i++)
            {
                char c = window[i];
                if (c == '\n') return i + 1;
                if ((c == '.' || c == '!' || c == '?') && char.IsWhiteSpace(window[i + 1])) return i + 2;
            }
            int space = window.IndexOf(' ');
            return space >= 0 ? space + 1 : 0;
        }

        private static readonly Regex InlineThink = new Regex(
            @"<think>(.*?)(?:</think>|$)", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// The text of inline &lt;think&gt; blocks in an assistant message, for backends that put
        /// reasoning in the content stream instead of reasoning_content. "" when there are none.
        /// </summary>
        public static string InlineThinkText(string assistantText)
        {
            if (string.IsNullOrEmpty(assistantText) || assistantText.IndexOf("<think>", StringComparison.Ordinal) < 0)
                return "";
            var sb = new StringBuilder();
            foreach (Match m in InlineThink.Matches(assistantText))
                sb.Append(m.Groups[1].Value);
            return sb.ToString();
        }
    }

    /// <summary>A harness note carrying a cut-off thought into the next request.</summary>
    public sealed class ReasoningCarryNote
    {
        /// <summary>The message inserted before the last message of the next request.</summary>
        public string Text { get; }
        /// <summary>Short journal detail.</summary>
        public string Detail { get; }
        public bool Escalated { get; }

        public ReasoningCarryNote(string text, string detail, bool escalated)
        {
            Text = text;
            Detail = detail;
            Escalated = escalated;
        }
    }

    /// <summary>
    /// Per-turn state for H-71. HeadlessSession calls <see cref="Observe"/> once per model
    /// response and <see cref="RecordInjected"/> when the note it returned is actually sent
    /// (a note produced on the turn's last iteration never is).
    /// </summary>
    public sealed class ReasoningCutoffCarry
    {
        /// <summary>The consecutive cut-off at which the note adds the escalation.</summary>
        public const int EscalateAt = 3;

        public const string EscalationText =
            "Stop deliberating and run an experiment now.";

        private readonly IReadOnlyList<string> _markers;
        private readonly bool _carry;
        private int _consecutive;

        /// <summary>Responses this turn whose reasoning was cut off by the thinking budget.</summary>
        public int Cutoffs { get; private set; }
        /// <summary>Notes injected into a request this turn.</summary>
        public int NotesInjected { get; private set; }
        /// <summary>Injected notes that carried the escalation.</summary>
        public int Escalations { get; private set; }

        /// <param name="markers">Resolved markers (<see cref="ReasoningCutoff.ResolveMarkers"/>).</param>
        /// <param name="carry">False (carry_cutoff_reasoning=false): cut-offs are counted, no note is built.</param>
        public ReasoningCutoffCarry(IReadOnlyList<string> markers, bool carry)
        {
            _markers = markers ?? ReasoningCutoff.DefaultMarkers;
            _carry = carry;
        }

        /// <summary>
        /// Observes one response's reasoning. <paramref name="cutOff"/> is true when thinking
        /// was on for that request and a marker matched. Returns the note for the next request,
        /// or null: not cut off, carry off, or nothing but the marker in the reasoning.
        /// </summary>
        public ReasoningCarryNote Observe(string reasoning, bool thinkingOn, out bool cutOff)
        {
            cutOff = false;
            int markerIndex = -1;
            if (thinkingOn && ReasoningCutoff.TryFindMarker(reasoning, _markers, out markerIndex))
                cutOff = true;

            if (!cutOff)
            {
                _consecutive = 0;
                return null;
            }

            Cutoffs++;
            _consecutive++;
            if (!_carry) return null;

            string tail = ReasoningCutoff.ExtractTail(reasoning, markerIndex);
            if (tail.Length == 0) return null;

            bool escalated = _consecutive >= EscalateAt;
            return new ReasoningCarryNote(Note(tail, escalated ? _consecutive : 0), Detail(tail, escalated, _consecutive), escalated);
        }

        /// <summary>Counts a note that was sent.</summary>
        public void RecordInjected(ReasoningCarryNote note)
        {
            if (note == null) return;
            NotesInjected++;
            if (note.Escalated) Escalations++;
        }

        /// <summary>The note's text. <paramref name="consecutive"/> &gt;= 3 adds the escalation.</summary>
        public static string Note(string tail, int consecutive = 0)
        {
            var sb = new StringBuilder();
            sb.Append("[harness] Your previous reasoning was cut off by the thinking budget while working on: \"")
              .Append(tail)
              .Append("\". Do not re-open this. Either decide it now or settle it with a quick test or experiment. ")
              .Append("First write a one-line 'Decision: …' (or 'Testing: …') to your SCRATCHPAD.");
            if (consecutive >= EscalateAt)
            {
                sb.Append(consecutive == EscalateAt
                    ? " This is the third cut-off in a row. "
                    : $" This is cut-off {consecutive} in a row. ");
                sb.Append(EscalationText);
            }
            return sb.ToString();
        }

        private static string Detail(string tail, bool escalated, int consecutive)
        {
            const int Quote = 100;
            string quoted = tail.Length <= Quote ? tail : tail.Substring(0, Quote) + "…";
            string head = escalated
                ? $"reasoning cut off by the thinking budget ({consecutive} in a row, escalated) — carried into the next request"
                : "reasoning cut off by the thinking budget — carried into the next request";
            return $"{head}: \"{quoted}\"";
        }
    }
}
