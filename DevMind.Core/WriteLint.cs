// File: WriteLint.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Harness-side nudges for mistakes agents keep making, delivered at the moment they happen.
//
// Two layers, both table-driven so a new rule is one row, not a new code path:
//
//   WriteLint        — runs after a successful create_file / append_file / patch_file and
//                      appends a "[LINT] ..." line to that tool's result. Looks ONLY at the
//                      lines the write changed, and never blocks the write.
//   BuildErrorHints  — runs over build/test output the agent is about to read and appends a
//                      "[HINT] ..." line when an error code plus its source line matches a
//                      known trap. Once per rule per build.
//
// The first rule of each is the xUnit "message argument" trap (H-19): xUnit v2 and v3 have
// no Assert.Equal(expected, actual, "message") overload. The string binds to a comparer or
// other overload and the build fails with CS1503 / CS1929 — jobs 1660, 1668 and 1683 each
// lost iterations to it, some of them to a misdiagnosed "stale build".
//
// Both layers prefer a false negative to a false positive: a warning that fires on correct
// code teaches the agent to ignore warnings, which costs more than the one it missed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>One lint finding: where, and the text of the call that tripped it.</summary>
    public readonly struct LintHit
    {
        /// <summary>1-based line in the written file.</summary>
        public readonly int Line;

        /// <summary>A short rendering of what was found, for the message.</summary>
        public readonly string Snippet;

        public LintHit(int line, string snippet)
        {
            Line = line;
            Snippet = snippet ?? string.Empty;
        }
    }

    /// <summary>A write-time lint rule. Pure: file path + content + changed lines in, hits out.</summary>
    public sealed class LintRule
    {
        public string Id { get; init; }

        /// <summary>Which files the rule applies to: (path, full content after the write).</summary>
        public Func<string, string, bool> FileFilter { get; init; }

        /// <summary>Finds hits whose call STARTS on one of the changed lines (1-based).</summary>
        public Func<string, ISet<int>, IReadOnlyList<LintHit>> Detector { get; init; }

        /// <summary>The single line appended to the tool result: (first hit, all hits).</summary>
        public Func<LintHit, IReadOnlyList<LintHit>, string> Message { get; init; }
    }

    /// <summary>Write-time lint over the lines a file-writing tool just changed.</summary>
    public static class WriteLint
    {
        /// <summary>The rule table. Add a row to add a rule.</summary>
        public static readonly IReadOnlyList<LintRule> Rules = new[]
        {
            new LintRule
            {
                Id = "xunit-message-argument",
                FileFilter = XunitMessageArgument.IsTestFile,
                Detector = XunitMessageArgument.Find,
                Message = XunitMessageArgument.Describe,
            },
        };

        /// <summary>
        /// Lint a file after a write. <paramref name="changedLines"/> are 1-based line numbers in
        /// <paramref name="content"/>; null means every line changed (a new file). Returns one
        /// "[LINT] ..." line per rule that fired, or an empty list. Never throws.
        /// </summary>
        public static IReadOnlyList<string> Check(string path, string content, ISet<int> changedLines)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(content)) return lines;

            changedLines ??= AllLines(content);
            if (changedLines.Count == 0) return lines;

            foreach (LintRule rule in Rules)
            {
                try
                {
                    if (!rule.FileFilter(path, content)) continue;
                    IReadOnlyList<LintHit> hits = rule.Detector(content, changedLines);
                    if (hits.Count > 0) lines.Add(rule.Message(hits[0], hits));
                }
                catch
                {
                    // A lint rule is advice. It must never fail the write it is commenting on.
                }
            }
            return lines;
        }

        /// <summary>
        /// The 1-based lines of <paramref name="after"/> that are not in <paramref name="before"/>.
        /// A multiset comparison, not a real diff: a line that merely moved is not reported, which
        /// errs toward linting less. Null or empty <paramref name="before"/> means every line.
        /// </summary>
        public static ISet<int> ChangedLines(string before, string after)
        {
            if (string.IsNullOrEmpty(before)) return AllLines(after ?? string.Empty);

            var pool = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string l in SplitLines(before))
            {
                string k = l.TrimEnd();
                pool[k] = pool.TryGetValue(k, out int n) ? n + 1 : 1;
            }

            var changed = new HashSet<int>();
            string[] afterLines = SplitLines(after ?? string.Empty);
            for (int i = 0; i < afterLines.Length; i++)
            {
                string k = afterLines[i].TrimEnd();
                if (pool.TryGetValue(k, out int n) && n > 0) pool[k] = n - 1;
                else changed.Add(i + 1);
            }
            return changed;
        }

        /// <summary>Lines <paramref name="first"/>..(first + count - 1).</summary>
        public static ISet<int> LineRange(int first, int count)
            => new HashSet<int>(Enumerable.Range(Math.Max(1, first), Math.Max(0, count)));

        internal static string[] SplitLines(string text)
            => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private static ISet<int> AllLines(string content) => LineRange(1, SplitLines(content).Length);
    }

    /// <summary>
    /// xUnit asserts given a trailing message string. xUnit has no message overload on these —
    /// only Assert.True / Assert.False take one — so the literal binds elsewhere and the build
    /// fails with CS1503 / CS1929.
    /// </summary>
    public static class XunitMessageArgument
    {
        // Assert method -> the argument count from which a trailing string literal can only be
        // a message. Two-value asserts have no overload whose third parameter is a string
        // (the third is a comparer, a StringComparison, a precision or a named bool); the
        // one-value asserts have none whose second is.
        private static readonly Dictionary<string, int> MessageArity = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Equal"] = 3, ["NotEqual"] = 3, ["Same"] = 3, ["NotSame"] = 3,
            ["Contains"] = 3, ["DoesNotContain"] = 3, ["StartsWith"] = 3, ["EndsWith"] = 3,
            ["Matches"] = 3, ["DoesNotMatch"] = 3,
            ["Empty"] = 2, ["NotEmpty"] = 2,
        };

        // "Assert.Name(" or "Assert.Name<T>(" — not "MyAssert.", but "Xunit.Assert." is fine.
        private static readonly Regex Call = new Regex(
            @"(?<![\w])Assert\.(?<name>Equal|NotEqual|Same|NotSame|Contains|DoesNotContain|StartsWith|EndsWith|Matches|DoesNotMatch|Empty|NotEmpty)\s*(?:<[^()]*?>)?\s*\(",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex TestFileName = new Regex(@"Test[^\\/]*\.cs$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex UsingXunit = new Regex(@"^\s*using\s+Xunit\s*;",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        // Longest call scanned for its argument list; past this it is not a call we understand.
        private const int MaxCallLength = 4000;

        /// <summary>A C# file named *Test*.cs, or one that says `using Xunit;`.</summary>
        public static bool IsTestFile(string path, string content)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return false;
            return TestFileName.IsMatch(Path.GetFileName(path))
                || (content != null && UsingXunit.IsMatch(content));
        }

        /// <summary>Calls that start on a changed line and end in a message-shaped string literal.</summary>
        public static IReadOnlyList<LintHit> Find(string content, ISet<int> changedLines)
        {
            var hits = new List<LintHit>();
            if (string.IsNullOrEmpty(content)) return hits;

            int[] lineStarts = LineStarts(content);
            bool[] notCode = null;

            foreach (Match m in Call.Matches(content))
            {
                int line = LineOf(lineStarts, m.Index);
                if (!changedLines.Contains(line)) continue;

                // "Assert.Equal(1, x, \"msg\")" inside a string or a comment is text, not a call —
                // a test of this very lint is full of them.
                notCode ??= StringsAndComments(content);
                if (notCode[m.Index]) continue;

                List<string> args = SplitArguments(content, m.Index + m.Length);
                if (args == null) continue;

                string name = m.Groups["name"].Value;
                if (args.Count < MessageArity[name]) continue;

                string last = args[args.Count - 1];
                if (!IsSingleStringLiteral(last)) continue;

                hits.Add(new LintHit(line, $"Assert.{name}(..., {Shorten(last)})"));
            }
            return hits;
        }

        /// <summary>The one line the tool result carries.</summary>
        public static string Describe(LintHit first, IReadOnlyList<LintHit> all)
        {
            var sb = new StringBuilder();
            sb.Append("[LINT] xUnit: ").Append(first.Snippet).Append(" at line ").Append(first.Line);
            if (all.Count > 1)
                sb.Append(" (also line").Append(all.Count > 2 ? "s " : " ")
                  .Append(string.Join(", ", all.Skip(1).Select(h => h.Line))).Append(')');
            sb.Append(" - xUnit has no message overload; use Assert.True(<condition>, \"message\") or put the note in a comment.");
            return sb.ToString();
        }

        // ── Scanning ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The top-level arguments of a call whose "(" ends just before <paramref name="start"/>,
        /// trimmed. Null when the call is not closed, too long, or uses syntax this scanner does
        /// not model (raw string literals) — a call we cannot read is a call we do not flag.
        /// </summary>
        internal static List<string> SplitArguments(string s, int start)
        {
            var args = new List<string>();
            int depth = 0;
            int argStart = start;
            int limit = Math.Min(s.Length, start + MaxCallLength);

            for (int i = start; i < limit; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\'' || ((c == '@' || c == '$') && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '@' || s[i + 1] == '$')))
                {
                    int end = SkipLiteral(s, i);
                    if (end < 0) return null;
                    i = end;
                    continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    int nl = s.IndexOf('\n', i);
                    if (nl < 0) return null;
                    i = nl;
                    continue;
                }
                if (c == '(' || c == '[' || c == '{') { depth++; continue; }
                if (c == ')' || c == ']' || c == '}')
                {
                    if (depth == 0)
                    {
                        if (c != ')') return null;
                        string tail = s.Substring(argStart, i - argStart).Trim();
                        if (tail.Length > 0 || args.Count > 0) args.Add(tail);
                        return args;
                    }
                    depth--;
                    continue;
                }
                if (c == ',' && depth == 0)
                {
                    args.Add(s.Substring(argStart, i - argStart).Trim());
                    argStart = i + 1;
                }
            }
            return null;
        }

        /// <summary>
        /// Index of the closing quote of the literal starting at <paramref name="i"/> ("...",
        /// @"...", $"...", $@"...", @$"...", '...'), or -1 for anything not modelled.
        /// </summary>
        private static int SkipLiteral(string s, int i)
        {
            bool verbatim = false, interpolated = false;
            while (i < s.Length && (s[i] == '@' || s[i] == '$'))
            {
                if (s[i] == '@') verbatim = true; else interpolated = true;
                i++;
            }
            if (i >= s.Length) return -1;

            char quote = s[i];
            if (quote == '\'')
            {
                for (int j = i + 1; j < s.Length && s[j] != '\n'; j++)
                {
                    if (s[j] == '\\') { j++; continue; }
                    if (s[j] == '\'') return j;
                }
                return -1;
            }
            if (quote != '"') return -1;
            if (i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"') return -1; // raw string literal

            int holes = 0;
            for (int j = i + 1; j < s.Length; j++)
            {
                char c = s[j];
                if (!verbatim && c == '\n') return -1;
                if (interpolated)
                {
                    if (c == '{')
                    {
                        if (j + 1 < s.Length && s[j + 1] == '{' && holes == 0) { j++; continue; }
                        holes++;
                        continue;
                    }
                    if (c == '}' && holes > 0) { holes--; continue; }
                    if (holes > 0)
                    {
                        // A string inside a hole ends the model; a nested literal is rare
                        // enough in an assert message to decline.
                        if (c == '"') return -1;
                        continue;
                    }
                }
                if (!verbatim && c == '\\') { j++; continue; }
                if (c == '"')
                {
                    if (verbatim && j + 1 < s.Length && s[j + 1] == '"') { j++; continue; }
                    return j;
                }
            }
            return -1;
        }

        /// <summary>
        /// Marks every character inside a comment or a string/char literal. Anything the lexer
        /// cannot model (an unterminated literal, a string nested in an interpolation hole) marks
        /// the rest of its line, so an unreadable region is never linted.
        /// </summary>
        internal static bool[] StringsAndComments(string s)
        {
            var mask = new bool[s.Length + 1];
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                int end = -1;
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    end = s.IndexOf('\n', i);
                    if (end < 0) end = s.Length - 1;
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? s.Length - 1 : end + 1;
                }
                else if (c == '"' || c == '\'' || ((c == '@' || c == '$') && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '@' || s[i + 1] == '$')))
                {
                    end = SkipRawLiteral(s, i);
                    if (end < 0) end = SkipLiteral(s, i);
                    if (end < 0)
                    {
                        end = s.IndexOf('\n', i);
                        if (end < 0) end = s.Length - 1;
                    }
                }

                if (end < 0) { i++; continue; }
                for (int k = i; k <= end && k < s.Length; k++) mask[k] = true;
                i = end + 1;
            }
            return mask;
        }

        /// <summary>Closing index of a raw string literal ("""...""", $"""...""", $$"""..."""), or -1.</summary>
        private static int SkipRawLiteral(string s, int i)
        {
            while (i < s.Length && s[i] == '$') i++;
            int q = 0;
            while (i + q < s.Length && s[i + q] == '"') q++;
            if (q < 3) return -1;
            int close = s.IndexOf(new string('"', q), i + q, StringComparison.Ordinal);
            return close < 0 ? s.Length - 1 : close + q - 1;
        }

        /// <summary>True when the whole argument is exactly one string literal (not a named argument or an expression).</summary>
        internal static bool IsSingleStringLiteral(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return false;
            int k = 0;
            while (k < arg.Length && (arg[k] == '@' || arg[k] == '$')) k++;
            if (k >= arg.Length || arg[k] != '"') return false;
            return SkipLiteral(arg, 0) == arg.Length - 1;
        }

        private static string Shorten(string literal)
            => literal.Length <= 40 ? literal : literal.Substring(0, 36) + "...\"";

        private static int[] LineStarts(string s)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\n') starts.Add(i + 1);
                else if (s[i] == '\r' && (i + 1 >= s.Length || s[i + 1] != '\n')) starts.Add(i + 1);
            }
            return starts.ToArray();
        }

        private static int LineOf(int[] starts, int index)
        {
            int k = Array.BinarySearch(starts, index);
            return (k >= 0 ? k : ~k - 1) + 1;
        }
    }

    /// <summary>A build-error hint rule: which codes, and what the reported source line must look like.</summary>
    public sealed class BuildErrorHint
    {
        public string Id { get; init; }

        /// <summary>Compiler error codes this hint is about, e.g. "CS1503".</summary>
        public IReadOnlyCollection<string> Codes { get; init; }

        /// <summary>Tested against the source line the error points at. Null source line never matches.</summary>
        public Func<string, bool> LinePredicate { get; init; }

        /// <summary>The line appended to the build output, once per build.</summary>
        public string Message { get; init; }
    }

    /// <summary>Hints appended to build/test output the agent reads.</summary>
    public static class BuildErrorHints
    {
        /// <summary>The rule table. Add a row to add a hint.</summary>
        public static readonly IReadOnlyList<BuildErrorHint> Rules = new[]
        {
            new BuildErrorHint
            {
                Id = "xunit-message-argument",
                Codes = new[] { "CS1503", "CS1929" },
                LinePredicate = line => line.IndexOf("Assert.", StringComparison.Ordinal) >= 0,
                Message = "[HINT] CS1503/CS1929 on an Assert.* call usually means an xUnit message argument - xUnit has no " +
                          "message overload; use Assert.True(cond, \"message\").",
            },
        };

        // MSBuild's canonical error format: "path(line,col): error CODE: text", optionally with an
        // end position "(line,col,endLine,endCol)" and any prefix (project number, indent).
        private static readonly Regex ErrorLine = new Regex(
            @"(?<file>[^\s(][^(\r\n]*?\.\w+)\((?<line>\d+),\d+(?:,\d+,\d+)?\)\s*:\s*error\s+(?<code>[A-Z]+\d+)\s*:",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Distinct errors inspected per build; a thousand-error build does not need a thousand reads.
        private const int MaxErrorsInspected = 50;

        /// <summary>
        /// Returns <paramref name="output"/> with one "[HINT] ..." line appended per rule that
        /// matched, or unchanged. Relative paths resolve against <paramref name="workingDirectory"/>.
        /// Never throws.
        /// </summary>
        public static string Annotate(string output, string workingDirectory)
        {
            if (string.IsNullOrEmpty(output) || output.IndexOf(": error ", StringComparison.Ordinal) < 0) return output;

            try
            {
                var fired = new List<BuildErrorHint>();
                var fileCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Match m in ErrorLine.Matches(output))
                {
                    if (fired.Count == Rules.Count) break;

                    string file = m.Groups["file"].Value.Trim();
                    string code = m.Groups["code"].Value;
                    string key = file + "|" + m.Groups["line"].Value + "|" + code;
                    if (!seen.Add(key)) continue;
                    if (seen.Count > MaxErrorsInspected) break;

                    foreach (BuildErrorHint rule in Rules)
                    {
                        if (fired.Contains(rule) || !rule.Codes.Contains(code, StringComparer.Ordinal)) continue;
                        string source = SourceLine(file, int.Parse(m.Groups["line"].Value), workingDirectory, fileCache);
                        if (source != null && rule.LinePredicate(source)) fired.Add(rule);
                    }
                }

                if (fired.Count == 0) return output;
                var sb = new StringBuilder(output);
                if (!output.EndsWith("\n", StringComparison.Ordinal)) sb.Append('\n');
                foreach (BuildErrorHint rule in fired) sb.Append(rule.Message).Append('\n');
                return sb.ToString();
            }
            catch
            {
                return output;
            }
        }

        private static string SourceLine(string file, int line, string workingDirectory, Dictionary<string, string[]> cache)
        {
            try
            {
                string path = Path.IsPathRooted(file) || string.IsNullOrEmpty(workingDirectory)
                    ? file
                    : Path.Combine(workingDirectory, file);
                if (!cache.TryGetValue(path, out string[] lines))
                {
                    lines = File.Exists(path) ? WriteLint.SplitLines(File.ReadAllText(path)) : null;
                    cache[path] = lines;
                }
                return lines != null && line >= 1 && line <= lines.Length ? lines[line - 1] : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
