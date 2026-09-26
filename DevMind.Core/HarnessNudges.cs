// File: HarnessNudges.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Two harness nudges for the headless loop (watchlist H-25 / H-26). Pure and stateful per
// session: HeadlessSession feeds each iteration's evidence in at the iteration boundary and
// appends whatever comes back to the next prompt. Nothing here blocks or stops a job.
//
// H-26 — optional-work spend guard. job-1698 spent ~30 iterations making InternalsVisibleTo
// expose private Designer fields for a test the brief marked optional, and needed an override
// steer. The brief's optional sentences are recorded; when the agent's last N iterations ALL
// reference one of them (prose or tool-call arguments mention its keywords), the agent is told
// to drop it. Once per item.
//
// H-25 — repeated compile error. job-1699 twice blamed "a recurring quirk" of the net48
// compile context for its own CS1061 (it had typed the variable as Control, not
// ContainerControl). When the same error code + member name shows up in tool output on a third
// iteration, the agent is told to re-read the declaration instead. Once per error key.
//
// Keyword heuristic (new — the existing thrash guard matches normalized failure SIGNATURES,
// not keywords): an optional sentence's keywords are its words of 4+ characters that are not
// stopwords and do NOT occur (up to a plural "s") in the brief's other sentences, so words shared with the
// required work ("test", the project name) never count as evidence of the optional item.
// An iteration references the item when it mentions at least min(2, keyword count) of them.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>A sentence of the brief marked optional, and the keywords that identify it.</summary>
    public sealed class OptionalBriefItem
    {
        public string Sentence { get; }
        public IReadOnlyList<string> Keywords { get; }

        public OptionalBriefItem(string sentence, IReadOnlyList<string> keywords)
        {
            Sentence = sentence;
            Keywords = keywords;
        }

        /// <summary>True when <paramref name="text"/> mentions at least min(2, keyword count)
        /// distinct keywords (whole word, case-insensitive).</summary>
        public bool IsReferencedBy(string text)
        {
            if (string.IsNullOrEmpty(text) || Keywords.Count == 0) return false;
            int needed = Math.Min(2, Keywords.Count);
            int hits = 0;
            foreach (string k in Keywords)
            {
                if (Regex.IsMatch(text, @"(?<![A-Za-z0-9_])" + Regex.Escape(k) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase)
                    && ++hits >= needed)
                    return true;
            }
            return false;
        }
    }

    /// <summary>What <see cref="HarnessNudges.ObserveIteration"/> is fed from one loop iteration,
    /// and how a fired nudge is framed into the next prompt.</summary>
    public static class HarnessNudgeEvidence
    {
        /// <summary>The agent's prose plus every tool-call argument value — what the agent is
        /// working ON this iteration. Tool output is excluded: a file listing or a read can
        /// mention the optional item's words without the agent pursuing it.</summary>
        public static string AgentText(string assistantResponse, IEnumerable<ToolCallResult> toolCalls)
        {
            var parts = new List<string> { assistantResponse ?? "" };
            if (toolCalls != null)
                foreach (var tc in toolCalls)
                    if (tc?.Arguments != null)
                        parts.AddRange(tc.Arguments.Values.Where(v => v != null));
            return string.Join("\n", parts);
        }

        /// <summary>Shell / build / test output and tool errors of the iteration.</summary>
        public static string ToolOutput(ExecutionResult result)
        {
            if (result == null) return "";
            var parts = new List<string> { result.ShellOutput ?? "" };
            if (result.Errors != null) parts.AddRange(result.Errors.Where(e => e != null));
            return string.Join("\n", parts);
        }

        /// <summary>Appends a nudge as its own delimited block — the harness's voice, visibly
        /// distinct from a caller steer ([CALLER STEER …]) and from the plain re-trigger.</summary>
        public static string Fold(string prompt, string nudge)
        {
            string framed = "[HARNESS GUARD] " + nudge;
            return string.IsNullOrEmpty(prompt) ? framed : prompt + "\n\n" + framed;
        }
    }

    public sealed class HarnessNudges
    {
        /// <summary>Consecutive iterations spent on an optional item before the nudge.</summary>
        public const int OptionalWindow = 8;

        /// <summary>Iterations whose tool output carries the same compile error before the nudge.</summary>
        public const int CompileErrorRepeats = 3;

        public const string OptionalWorkMessage =
            "This item was marked optional in the brief. Drop it and continue with the required work.";

        public const string RepeatedCompileErrorMessage =
            "The same compile error has repeated three times — stop and re-read the declaration you are " +
            "calling; do not attribute it to the toolchain.";

        // "optional" as a word ("Optional:", "(optional)"), not "optionally"; "not optional" and
        // "non-optional" are required work, so they are excluded.
        private static readonly Regex OptionalMarker = new Regex(
            @"(?<!\bnot\s)(?<!\bnon-)\boptional\b|\bif quick\b|\bnice[\s-]to[\s-]have\b|\bskip this if\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Word = new Regex(@"[A-Za-z_][A-Za-z0-9_.]*[A-Za-z0-9_]", RegexOptions.Compiled);

        // A compiler error line: "error CS1061: 'Control' does not contain a definition for 'AutoScaleMode' ...".
        private static readonly Regex CompileError = new Regex(
            @"\berror\s+(CS\d{4})\s*:([^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> Stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "about", "after", "also", "been", "before", "both", "but", "can", "could", "does", "done", "each",
            "else", "from", "have", "here", "into", "just", "make", "more", "most", "must", "need", "nice",
            "only", "onto", "optional", "optionally", "other", "over", "quick", "quickly", "same", "should",
            "skip", "some", "such", "than", "that", "their", "them", "then", "there", "these", "they", "this",
            "those", "time", "under", "using", "very", "want", "were", "what", "when", "where", "which",
            "while", "will", "with", "without", "would", "your", "item", "step", "task", "work", "please",
        };

        private readonly List<OptionalBriefItem> _items = new List<OptionalBriefItem>();
        private readonly Dictionary<OptionalBriefItem, Queue<bool>> _windows = new Dictionary<OptionalBriefItem, Queue<bool>>();
        private readonly HashSet<string> _seenSentences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<OptionalBriefItem> _optionalFired = new HashSet<OptionalBriefItem>();
        private readonly Dictionary<string, int> _errorCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _errorFired = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyList<OptionalBriefItem> OptionalItems => _items;

        /// <summary>
        /// Finds the sentences of <paramref name="brief"/> that carry an optional marker
        /// ("optional", "if quick", "nice to have", "skip this if"), with their keywords.
        /// </summary>
        public static List<OptionalBriefItem> FindOptionalItems(string brief)
        {
            var result = new List<OptionalBriefItem>();
            if (string.IsNullOrWhiteSpace(brief)) return result;

            List<string> sentences = SplitSentences(brief);
            var optional = sentences.Where(s => OptionalMarker.IsMatch(s)).ToList();
            if (optional.Count == 0) return result;

            // Compared by a crude stem so "test" in the optional sentence matches "tests" in a
            // required step.
            var requiredStems = new HashSet<string>(
                sentences.Except(optional).SelectMany(Tokens).Select(Stem), StringComparer.OrdinalIgnoreCase);

            foreach (string sentence in optional)
            {
                var candidates = Tokens(sentence).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var distinctive = candidates.Where(t => !requiredStems.Contains(Stem(t))).ToList();
                result.Add(new OptionalBriefItem(sentence, distinctive.Count > 0 ? distinctive : candidates));
            }
            return result;
        }

        /// <summary>Registers the optional items of a turn's prompt (the brief, or a
        /// continuation). An item already registered keeps its window and fired state.</summary>
        public void AddBrief(string brief)
        {
            foreach (var item in FindOptionalItems(brief))
            {
                if (!_seenSentences.Add(item.Sentence)) continue;
                _items.Add(item);
                _windows[item] = new Queue<bool>();
            }
        }

        /// <summary>Compile-error counts are per turn (one job run); optional items persist.</summary>
        public void ResetCompileErrors() => _errorCounts.Clear();

        /// <summary>
        /// Records one iteration: <paramref name="agentText"/> is the agent's prose plus its
        /// tool-call arguments, <paramref name="toolOutput"/> the shell/build/test output and
        /// errors. Returns the nudges to append to the next prompt (usually none).
        /// </summary>
        public List<string> ObserveIteration(string agentText, string toolOutput)
        {
            var nudges = new List<string>();

            foreach (var item in _items)
            {
                var window = _windows[item];
                window.Enqueue(item.IsReferencedBy(agentText));
                while (window.Count > OptionalWindow) window.Dequeue();
                if (window.Count == OptionalWindow && window.All(b => b) && _optionalFired.Add(item))
                    nudges.Add(OptionalWorkMessage + "\nOptional item: \"" + item.Sentence + "\"");
            }

            // One count per iteration per error key: a single dotnet build prints each error
            // twice (inline and in the summary), and multi-target builds once per framework.
            foreach (string key in CompileErrorKeys(toolOutput))
            {
                _errorCounts.TryGetValue(key, out int n);
                _errorCounts[key] = ++n;
                if (n >= CompileErrorRepeats && _errorFired.Add(key))
                    nudges.Add(RepeatedCompileErrorMessage + "\nError: " + key);
            }

            return nudges;
        }

        /// <summary>Distinct "CS1061 'member'" keys in <paramref name="toolOutput"/>. The member
        /// is the name after "definition for" when present (CS1061/CS0117), otherwise the first
        /// quoted name in the message.</summary>
        public static IEnumerable<string> CompileErrorKeys(string toolOutput)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(toolOutput)) return keys;
            foreach (Match m in CompileError.Matches(toolOutput))
            {
                string code = m.Groups[1].Value.ToUpperInvariant();
                string message = m.Groups[2].Value;
                var member = Regex.Match(message, @"definition for '([^']+)'");
                if (!member.Success) member = Regex.Match(message, @"'([^']+)'");
                keys.Add(member.Success ? $"{code} '{member.Groups[1].Value}'" : code);
            }
            return keys;
        }

        private static List<string> SplitSentences(string text)
        {
            // Lines first (bullets, numbered steps), then sentence ends within a line.
            var sentences = new List<string>();
            foreach (string line in text.Split('\n'))
                foreach (string s in Regex.Split(line, @"(?<=[.!?])\s+"))
                {
                    string t = s.Trim().TrimStart('-', '*', '+', ' ');
                    if (t.Length > 0) sentences.Add(t);
                }
            return sentences;
        }

        private static string Stem(string word)
        {
            string w = word.ToLowerInvariant();
            if (w.Length > 5 && w.EndsWith("es", StringComparison.Ordinal)) return w.Substring(0, w.Length - 2);
            if (w.Length > 4 && w.EndsWith("s", StringComparison.Ordinal) && !w.EndsWith("ss", StringComparison.Ordinal))
                return w.Substring(0, w.Length - 1);
            return w;
        }

        private static IEnumerable<string> Tokens(string sentence)
        {
            foreach (Match m in Word.Matches(sentence))
            {
                string t = m.Value.Trim('.');
                if (t.Length >= 4 && !Stopwords.Contains(t)) yield return t;
            }
        }
    }
}
