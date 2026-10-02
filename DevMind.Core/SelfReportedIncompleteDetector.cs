// File: SelfReportedIncompleteDetector.cs  v1.5
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// When the agent's own final answer says the work is not finished.
//
// Five jobs in two days ended state=done with incomplete_reasons=null while their final
// message said, in as many words, that the job was not done: "The core defect is NOT fixed
// ... the caller must not report LT-04 as fixed", "no test run was performed ... red run NOT
// demonstrated", "NOT DONE / caller must finish". The harness had every signal it needed and
// was not reading the one place the agent had put it.
//
// The existing incompleteness signals are all things the HARNESS measured — the iteration
// cap, a failed build, a failed test run. This is the one the AGENT reported, and it is the
// only one that catches a job whose build was skipped and whose tests were never run, which
// is exactly the shape the five jobs had.
//
// The phrase list is deliberately short and deliberately blunt. A false positive costs a
// caller one extra look at an answer they were going to read anyway; a false negative is
// what produced the five jobs. Fenced code is skipped because a phrase in a diff or a log
// excerpt is quoting something, not reporting on the work.
//
// v1.1: the explicit marker is matched after markdown list/emphasis decoration. job-1674 ended
// `done` with five lines reading "- INCOMPLETE: ..." under a "**INCOMPLETE:**" header — the
// classifier was reading the right text (the answer devmind_task_result returns), but v1.0
// only matched a line that opened with the bare word, and agents write their lists in
// markdown.
//
// v1.2: a marker that declares nothing is not a declaration. job-1679 finished its work and
// wrote "INCOMPLETE: none." — the brief had asked for unfinished items on INCOMPLETE: lines —
// and ended stopped_incomplete. A marker whose text is only none / nothing / n/a / na / - / —
// (or nothing at all, with no list under it) now reads as the all-clear it is.
//
// v1.3: a markdown header is a section title, not a statement about the work. job-1686 finished
// cleanly (tests green, build clean) and ended stopped_incomplete because its notes section was
// titled "## Caller must know". Header lines (# to ######) are no longer read by the phrase
// list — the INCOMPLETE: marker still counts in a header — and "caller must" is dropped from
// the list: agents now use the INCOMPLETE: convention consistently, and "caller must" was only
// ever the weakest of the backups ("the caller must not report X as fixed" is still caught by
// "must not report" and "not fixed").

//
// v1.4 (H-31): the result says HOW it knows. An INCOMPLETE: marker is the agent's explicit
// declaration (Strong); a phrase-list hit is a guess from wording (Weak). job-1714 ("The full
// solution suite was not run by me — the harness verifies it.") and job-1715 ("Did not run
// the TUI; did not commit." — both things the brief forbade) ended stopped_incomplete on a
// phrase hit while the harness's own build and tests were green. The detector does not decide
// what a weak hit means; AgentJob does, by weighing it against harness verification.
//
// v1.5 (H-43): "INCOMPLETE: none." followed by an explanation is still the all-clear. job-2104
// ("INCOMPLETE: none. Only the TotalAgility page changed; ...") and job-1850/1861 ended
// stopped_incomplete because v1.2 accepted the none-word only as the WHOLE marker text.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>How a final answer said the work is unfinished.</summary>
    public enum SelfReportStrength
    {
        /// <summary>It did not.</summary>
        None,
        /// <summary>A phrase-list hit only ("not fixed", "did not run") — wording, not a declaration.</summary>
        Weak,
        /// <summary>An explicit INCOMPLETE: marker line.</summary>
        Strong,
    }

    /// <summary>What a final answer admitted, if anything.</summary>
    public readonly struct SelfReportedIncomplete
    {
        /// <summary>How the answer said it — an explicit marker or a phrase hit.</summary>
        public readonly SelfReportStrength Strength;

        /// <summary>The first line that said so, trimmed for a reason field.</summary>
        public readonly string Line;

        /// <summary>True when the answer says the work is unfinished, by either route.</summary>
        public bool Detected => Strength != SelfReportStrength.None;

        public SelfReportedIncomplete(SelfReportStrength strength, string line)
        {
            Strength = strength;
            Line = line ?? string.Empty;
        }

        public static readonly SelfReportedIncomplete None = new SelfReportedIncomplete(SelfReportStrength.None, string.Empty);
    }

    /// <summary>
    /// Reads a final answer for the agent's own statement that it did not finish. Pure.
    /// </summary>
    public static class SelfReportedIncompleteDetector
    {
        /// <summary>The reason code added to a job's incomplete_reasons.</summary>
        public const string Reason = "self_reported_incomplete";

        /// <summary>Longest quoted line kept in a reason field.</summary>
        public const int MaxLineLength = 200;

        /// <summary>The explicit convention: a line that opens with this is a declaration.</summary>
        public const string ExplicitMarker = "INCOMPLETE:";

        /// <summary>
        /// Phrases that mean the work is unfinished, whatever sentence they sit in.
        /// <para>
        /// Kept blunt on purpose. Every one of these is taken from a final answer that ended
        /// as `done`, and the list is not trying to parse English — a sentence like "nothing
        /// was not done" would be missed, and that is an acceptable trade for a rule anyone
        /// can read and predict.
        /// </para>
        /// </summary>
        public static readonly IReadOnlyList<string> Phrases = new[]
        {
            "not done",
            "not fixed",
            "not verified",
            "not demonstrated",
            "did not run",
            "was not run",
            "were not run",
            "never executed",
            "must not report",
            "remaining work",
            "still failing",
            "could not finish",
            "hit the iteration cap",
        };

        // ``` or ~~~ opening or closing a fenced block, with optional leading whitespace.
        private static readonly Regex Fence = new Regex(@"^\s*(```|~~~)", RegexOptions.Compiled);

        // The explicit marker, allowing the markdown an agent actually wraps it in: indent,
        // blockquote ">", a heading "#", a list bullet ("-", "*", "+", "1.", "1)") and emphasis
        // ("**", "__", "*", "_") around the word — "- INCOMPLETE:", "1. **INCOMPLETE:** x",
        // "**INCOMPLETE**: x". The marker must still START the line's content: "is incomplete:"
        // mid-sentence is not a declaration.
        private static readonly Regex Marker = new Regex(
            @"^\s*(?:>\s*)*(?:#{1,6}\s+)?(?:(?:[-*+]|\d+[.)])\s+)?(?:\*\*|__|\*|_)?INCOMPLETE(?:\*\*|__|\*|_)?:(?:\*\*|__|\*|_)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // A markdown ATX header: "#" to "######" then whitespace or end of line, after optional
        // indent/">". "#hashtag" and "#123" are not headers.
        private static readonly Regex Header = new Regex(
            @"^\s*(?:>\s*)*#{1,6}(?:\s|$)", RegexOptions.Compiled);

        // A list item: "-", "*", "+", "1.", "1)" followed by whitespace, after optional indent/">".
        private static readonly Regex ListItem = new Regex(
            @"^\s*(?:>\s*)*(?:[-*+]|\d+[.)])\s+", RegexOptions.Compiled);

        // The text after a marker that says there is nothing unfinished: none, nothing, n/a, na,
        // "-" or a dash, optionally emphasised, optionally "outstanding"/"remaining"/"left"/
        // "pending", then either the end of the line (after optional punctuation) or an
        // explanation: a sentence after . ! ; ("none. Only the X page changed"), or a
        // parenthesis or dash ("none (full suite delegated)", "none — X"). A word straight after
        // it ("none of the tests ran", "n/a for build, but ...") or a comma ("none, but ...") is
        // a statement about unfinished work and does not match. Empty is handled by the caller,
        // because an empty marker may be a header over a list.
        private static readonly Regex NothingUnfinished = new Regex(
            @"^(?:\*\*|__|\*|_)?(?:none|nothing|n/a|na|-|—|–)(?:\*\*|__|\*|_)?(?:\s+(?:outstanding|remaining|left|pending))?(?:\*\*|__|\*|_)?" +
            @"(?:[.!;,]*(?:\*\*|__|\*|_)?$|[.!;]+(?:\*\*|__|\*|_)?\s+\S.*$|\s+[(\[—–].*$|\s+-\s+.*$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Examine a final answer.
        /// </summary>
        /// <returns>
        /// The first INCOMPLETE: declaration (<see cref="SelfReportStrength.Strong"/>) anywhere in
        /// the answer; failing that, the first phrase-list line (<see cref="SelfReportStrength.Weak"/>);
        /// otherwise <see cref="SelfReportedIncomplete.None"/>. Lines inside a fenced code block
        /// are skipped: a phrase in a diff or a pasted log is being quoted, not claimed.
        /// </returns>
        public static SelfReportedIncomplete Detect(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)) return SelfReportedIncomplete.None;

            bool inFence = false;
            string bareMarker = null;     // a marker line with nothing after it, e.g. "**INCOMPLETE:**"
            string firstWeak = null;      // the first phrase-list hit — kept while a marker may still follow

            foreach (string raw in answer.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (Fence.IsMatch(raw))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence) continue;

                string line = raw.Trim();
                if (line.Length == 0) continue;

                // A bare marker is a heading over the list that says what is unfinished; the
                // first item under it is the one worth quoting in the reason field — unless that
                // item itself says "none". A bare marker with no list under it declares nothing,
                // and the line that follows is read on its own merits.
                if (bareMarker != null)
                {
                    bareMarker = null;
                    Match item = ListItem.Match(line);
                    if (item.Success)
                    {
                        string itemText = line.Substring(item.Length).Trim();
                        Match inner = Marker.Match(itemText);
                        if (inner.Success) itemText = itemText.Substring(inner.Length).Trim();
                        if (itemText.Length == 0 || NothingUnfinished.IsMatch(itemText)) continue;
                        return new SelfReportedIncomplete(SelfReportStrength.Strong, Trim(line));
                    }
                }

                // The explicit convention first: it is a declaration, not a guess, so it is
                // checked before the phrase list and never subject to it — including when it
                // declares that nothing is unfinished ("INCOMPLETE: none.").
                Match marker = Marker.Match(line);
                if (marker.Success)
                {
                    string rest = line.Substring(marker.Length).Trim();
                    if (rest.Length == 0) bareMarker = line;
                    else if (!NothingUnfinished.IsMatch(rest)) return new SelfReportedIncomplete(SelfReportStrength.Strong, Trim(line));
                    continue;
                }

                // A header titles a section; it does not report on the work ("## Caller must know").
                if (Header.IsMatch(line)) continue;

                // A phrase hit is weak evidence, and a later INCOMPLETE: line outranks it: keep
                // reading, or an answer with "Did not run the TUI." above an "INCOMPLETE: X" line
                // would report only the weak line, and a green harness build would excuse a
                // declared gap.
                if (firstWeak != null) continue;
                foreach (string phrase in Phrases)
                {
                    if (line.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        firstWeak = Trim(line);
                        break;
                    }
                }
            }

            return firstWeak != null
                ? new SelfReportedIncomplete(SelfReportStrength.Weak, firstWeak)
                : SelfReportedIncomplete.None;
        }

        private static string Trim(string line)
            => line.Length <= MaxLineLength ? line : line.Substring(0, MaxLineLength - 1) + "…";
    }
}
