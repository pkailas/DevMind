// File: ToolArgumentRepair.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Repair ladder for a tool call's function.arguments payload.
//
// A local model's tool call arrives as a JSON string it generated token by token, and it
// gets that string wrong in a small number of recognisable ways. Collapsing all of them to
// "{}" runs the tool with no arguments, which fails for a reason unrelated to what actually
// went wrong — the model then sees "filename is required" when the real event was a stream
// truncated at the token limit.
//
// The ladder is cheapest-first; each rung is attempted only after the one above it fails:
//
//   0. Parse as-is.                      → Clean
//   1. Close an unclosed string/brace.   → ClosedTruncation   (the dominant failure)
//   2. Rewrite: fences, duplicated /     → Rewritten
//      trailing fragments, Python
//      literals.
//   3. Give up.                          → Failed ("{}")
//
// What is NOT here, deliberately: trailing commas, unquoted keys, single-quoted strings,
// duplicate keys and comments. Newtonsoft accepts all of those at rung 0, so a repair pass
// for them would be code that can never run. Measured against Newtonsoft.Json 13.0.4:
//
//   {a:1}            accepted     {"a":1,}          accepted     {"a":1,"a":2}   accepted
//   {'a':'b'}        accepted     {"a":[1,2,]}      accepted     {"a":1 /*x*/}   accepted
//   {"a":"trunc      REJECTED     {"a":1}{"b":2}    REJECTED     {"a":True}      REJECTED
//   {"a":1           REJECTED     {"a":1} trailing  REJECTED     ```json...```   REJECTED
//
// Every rung is pure and total: it returns a result or falls through, and never throws.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevMind
{
    /// <summary>Which rung of <see cref="ToolArgumentRepair"/> produced the arguments.</summary>
    internal enum ToolArgumentRung
    {
        /// <summary>There were no arguments to parse (null/blank) — the empty object is correct, not a repair.</summary>
        Absent,
        /// <summary>Parsed as sent. No repair happened.</summary>
        Clean,
        /// <summary>Rung 1: an unterminated string and/or unclosed containers were closed.</summary>
        ClosedTruncation,
        /// <summary>Rung 2: the payload was rewritten (fence stripped, duplicated tail dropped, literals normalised).</summary>
        Rewritten,
        /// <summary>Rung 3: nothing worked. The arguments are the empty object.</summary>
        Failed,
    }

    /// <summary>Outcome of one repair attempt. <see cref="Json"/> is always valid JSON text.</summary>
    internal readonly struct ToolArgumentRepairResult
    {
        public ToolArgumentRepairResult(string json, ToolArgumentRung rung, int rawLength, string rawHead)
        {
            Json = json;
            Rung = rung;
            RawLength = rawLength;
            RawHead = rawHead;
        }

        /// <summary>Valid, compact JSON. <c>"{}"</c> when <see cref="Rung"/> is Absent or Failed.</summary>
        public string Json { get; }

        public ToolArgumentRung Rung { get; }

        /// <summary>Length of the payload as the model sent it — the useful number when a stream truncates.</summary>
        public int RawLength { get; }

        /// <summary>Leading characters of the raw payload, newlines flattened, for the failure line.</summary>
        public string RawHead { get; }

        /// <summary>True when a rung above the fallback had to act. Worth telling the operator about.</summary>
        public bool Repaired => Rung == ToolArgumentRung.ClosedTruncation || Rung == ToolArgumentRung.Rewritten;

        public bool Failed => Rung == ToolArgumentRung.Failed;

        /// <summary>
        /// One line for the transcript and the diagnostic log, or null when nothing happened
        /// worth reporting. A repaired call must not be indistinguishable from a clean one:
        /// repeated repairs are evidence about the model (a token limit set too low, a
        /// template emitting fences) and that evidence is only useful if it is visible.
        /// </summary>
        public string Describe(string toolName)
        {
            string tool = string.IsNullOrEmpty(toolName) ? "(unnamed tool)" : toolName;
            switch (Rung)
            {
                case ToolArgumentRung.ClosedTruncation:
                    return string.Format(CultureInfo.InvariantCulture,
                        "[TOOL_ARGS] {0}: arguments were truncated at {1} chars — closed and recovered.",
                        tool, RawLength);
                case ToolArgumentRung.Rewritten:
                    return string.Format(CultureInfo.InvariantCulture,
                        "[TOOL_ARGS] {0}: arguments were malformed ({1} chars) — repaired.",
                        tool, RawLength);
                case ToolArgumentRung.Failed:
                    return string.Format(CultureInfo.InvariantCulture,
                        "[TOOL_ARGS] {0}: arguments could not be parsed or repaired ({1} chars) — " +
                        "running with empty arguments. Raw head: {2}",
                        tool, RawLength, RawHead);
                default:
                    return null;
            }
        }
    }

    internal static class ToolArgumentRepair
    {
        /// <summary>Characters of the raw payload quoted back on a total failure.</summary>
        private const int RawHeadChars = 120;

        internal const string EmptyObject = "{}";

        /// <summary>
        /// Runs the ladder over one <c>function.arguments</c> payload. Never throws; the worst
        /// outcome is <see cref="ToolArgumentRung.Failed"/> with the empty object.
        /// </summary>
        public static ToolArgumentRepairResult Repair(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new ToolArgumentRepairResult(EmptyObject, ToolArgumentRung.Absent, raw?.Length ?? 0, "");

            int rawLength = raw.Length;
            string head = Head(raw);

            // ── Rung 0: as sent ────────────────────────────────────────────────
            // Kept byte-identical in behaviour to the original parse so a well-formed
            // call is never touched by anything below.
            try
            {
                string asSent = JToken.Parse(raw).ToString(Formatting.None);
                return new ToolArgumentRepairResult(asSent, ToolArgumentRung.Clean, rawLength, head);
            }
            catch (JsonException)
            {
                // fall through
            }

            // ── Rung 1: truncation ─────────────────────────────────────────────
            if (TryCloseTruncation(raw, out string closed))
                return new ToolArgumentRepairResult(closed, ToolArgumentRung.ClosedTruncation, rawLength, head);

            // ── Rung 2: rewrite ────────────────────────────────────────────────
            if (TryRewrite(raw, out string rewritten))
                return new ToolArgumentRepairResult(rewritten, ToolArgumentRung.Rewritten, rawLength, head);

            // ── Rung 3: the empty object ───────────────────────────────────────
            return new ToolArgumentRepairResult(EmptyObject, ToolArgumentRung.Failed, rawLength, head);
        }

        // ── Rung 1 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Closes a payload the model stopped emitting mid-way: an unterminated string, then
        /// whatever objects and arrays are still open. This is the dominant local-model
        /// failure — generation hits the token limit in the middle of a long argument.
        /// <para>
        /// Two candidates, in order. (A) close the open string in place, which KEEPS the
        /// partial value: <c>{"command":"dotnet bui</c> becomes <c>{"command":"dotnet bui"}</c>.
        /// That is deliberately preferred over dropping the key — a tool that fails on a
        /// visibly half-written argument tells the model what was lost, where a missing
        /// argument does not. (B) when the cut landed somewhere a quote cannot rescue — after
        /// a key's colon, say — fall back to the last completed pair.
        /// </para>
        /// <para>
        /// A candidate that collapses to the empty object is rejected: that is the fallback's
        /// answer, and claiming it as a repair would misreport what happened.
        /// </para>
        /// </summary>
        internal static bool TryCloseTruncation(string raw, out string json)
        {
            json = null;

            JsonScan scan = JsonScan.Of(raw);
            if (!scan.InString && scan.OpenContainers.Count == 0)
                return false;   // nothing is open — this is not a truncation

            // (A) close the string, then the containers, outermost last.
            string candidate = raw + (scan.InString ? "\"" : "") + Closers(scan.OpenContainers);
            if (TryParseContainer(candidate, out json))
                return true;

            // (B) cut back to the last completed member and close that.
            if (scan.LastStructuralComma > 0)
            {
                string prefix = raw.Substring(0, scan.LastStructuralComma);
                JsonScan prefixScan = JsonScan.Of(prefix);
                if (!prefixScan.InString)
                {
                    string trimmed = prefix + Closers(prefixScan.OpenContainers);
                    if (TryParseContainer(trimmed, out json))
                        return true;
                }
            }

            json = null;
            return false;
        }

        // ── Rung 2 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The rest of the recognisable damage, applied cumulatively so a payload carrying
        /// more than one problem still lands: a markdown fence around the JSON, a duplicated
        /// or trailing fragment after the real object, and Python's <c>True</c>/<c>False</c>/
        /// <c>None</c> in place of JSON literals.
        /// </summary>
        internal static bool TryRewrite(string raw, out string json)
        {
            string s = StripCodeFence(raw);
            if (!ReferenceEquals(s, raw) && TryParseContainer(s, out json))
                return true;

            // A duplicated stream emits the object twice ({"a":1}{"a":1}); a chatty one adds
            // a sentence after it. Either way the FIRST complete value is the call.
            JsonScan scan = JsonScan.Of(s);
            if (scan.FirstValueEnd > 0 && scan.FirstValueEnd < s.Length)
            {
                s = s.Substring(0, scan.FirstValueEnd);
                if (TryParseContainer(s, out json))
                    return true;
            }

            string literals = NormalizePythonLiterals(s);
            if (!ReferenceEquals(literals, s) && TryParseContainer(literals, out json))
                return true;

            json = null;
            return false;
        }

        /// <summary>Content of the first fenced block, or the input unchanged when there is no fence.</summary>
        internal static string StripCodeFence(string s)
        {
            int open = s.IndexOf("```", StringComparison.Ordinal);
            if (open < 0) return s;

            int afterInfo = s.IndexOf('\n', open);
            if (afterInfo < 0) return s;

            int close = s.IndexOf("```", afterInfo, StringComparison.Ordinal);
            return close < 0
                ? s.Substring(afterInfo + 1)
                : s.Substring(afterInfo + 1, close - afterInfo - 1);
        }

        /// <summary>
        /// Rewrites Python's literals to JSON's, outside string content only — a payload
        /// whose VALUE is the word "None" must keep it. Returns the input unchanged (by
        /// reference) when there was nothing to rewrite, so the caller can tell.
        /// </summary>
        internal static string NormalizePythonLiterals(string s)
        {
            StringBuilder sb = null;
            bool inString = false, escaped = false;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    sb?.Append(c);
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    sb?.Append(c);
                    continue;
                }

                string replacement = MatchPythonLiteral(s, i);
                if (replacement != null)
                {
                    sb ??= new StringBuilder(s.Length).Append(s, 0, i);
                    sb.Append(replacement);
                    i += PythonLiteralLength(replacement) - 1;
                    continue;
                }

                sb?.Append(c);
            }

            return sb?.ToString() ?? s;
        }

        private static string MatchPythonLiteral(string s, int i)
        {
            if (StartsWithWord(s, i, "True")) return "true";
            if (StartsWithWord(s, i, "False")) return "false";
            if (StartsWithWord(s, i, "None")) return "null";
            return null;
        }

        private static int PythonLiteralLength(string jsonLiteral)
            => jsonLiteral == "true" ? 4 : jsonLiteral == "false" ? 5 : 4;   // True / False / None

        private static bool StartsWithWord(string s, int i, string word)
        {
            if (i + word.Length > s.Length) return false;
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;

            int after = i + word.Length;
            if (after < s.Length && (char.IsLetterOrDigit(s[after]) || s[after] == '_')) return false;
            if (i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_')) return false;
            return true;
        }

        // ── Shared ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses and requires an object or array. A repaired scalar is meaningless as a tool
        /// argument list, and accepting one would turn a broken payload into a confident wrong
        /// answer. The empty object is rejected too — see <see cref="TryCloseTruncation"/>.
        /// </summary>
        private static bool TryParseContainer(string candidate, out string json)
        {
            json = null;
            if (string.IsNullOrWhiteSpace(candidate)) return false;

            try
            {
                JToken token = JToken.Parse(candidate);
                if (token.Type != JTokenType.Object && token.Type != JTokenType.Array)
                    return false;
                if (token is JObject obj && obj.Count == 0)
                    return false;

                json = token.ToString(Formatting.None);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string Closers(IReadOnlyList<char> openContainers)
        {
            if (openContainers.Count == 0) return "";
            var sb = new StringBuilder(openContainers.Count);
            for (int i = openContainers.Count - 1; i >= 0; i--)
                sb.Append(openContainers[i] == '[' ? ']' : '}');
            return sb.ToString();
        }

        private static string Head(string raw)
        {
            string flat = raw.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
            return flat.Length <= RawHeadChars ? flat : flat.Substring(0, RawHeadChars) + "…";
        }

        /// <summary>
        /// One structural pass over a (possibly broken) JSON document: what is still open at
        /// the end, where the last member separator was, and where the first complete
        /// top-level container ended. String and escape aware, so none of the three is
        /// confused by punctuation inside a value.
        /// </summary>
        private readonly struct JsonScan
        {
            private JsonScan(bool inString, List<char> open, int lastComma, int firstValueEnd)
            {
                InString = inString;
                OpenContainers = open;
                LastStructuralComma = lastComma;
                FirstValueEnd = firstValueEnd;
            }

            /// <summary>The document ended inside a string literal.</summary>
            public bool InString { get; }

            /// <summary>Containers still open at the end, outermost first.</summary>
            public List<char> OpenContainers { get; }

            /// <summary>Index of the last ',' that separates members of a container, or -1.</summary>
            public int LastStructuralComma { get; }

            /// <summary>Index just past the first complete top-level object/array, or -1.</summary>
            public int FirstValueEnd { get; }

            public static JsonScan Of(string s)
            {
                bool inString = false, escaped = false;
                var open = new List<char>();
                int lastComma = -1, firstValueEnd = -1;

                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];

                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (c == '\\') escaped = true;
                        else if (c == '"') inString = false;
                        continue;
                    }

                    switch (c)
                    {
                        case '"':
                            inString = true;
                            break;
                        case '{':
                        case '[':
                            open.Add(c);
                            break;
                        case '}':
                        case ']':
                            if (open.Count > 0) open.RemoveAt(open.Count - 1);
                            if (open.Count == 0 && firstValueEnd < 0) firstValueEnd = i + 1;
                            break;
                        case ',':
                            if (open.Count > 0) lastComma = i;
                            break;
                    }
                }

                return new JsonScan(inString, open, lastComma, firstValueEnd);
            }
        }
    }
}
