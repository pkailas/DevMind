// File: SearchPattern.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shared line-match semantics for the grep/find tools (agent hosts + MCP server).
// Patterns are case-insensitive SUBSTRINGS with ONE regex-ism supported: '|' acts
// as OR between alternatives. Field evidence: agents (and frontier callers) keep
// writing "PdfGenerated|Exported" from grep muscle-memory; as a literal substring
// that matches nothing, and the silent "no matches" led to false doesn't-exist
// conclusions. Supporting alternation converts the habit into correct behavior.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevMind
{
    /// <summary>Line matcher for the search tools: substring OR-alternation.</summary>
    public static class SearchPattern
    {
        /// <summary>
        /// Builds a line predicate for <paramref name="pattern"/>: the line matches when
        /// it contains ANY '|'-separated alternative (case-insensitive, ordinal). Empty
        /// alternatives are dropped; a pattern of only '|' characters matches nothing.
        /// </summary>
        public static Func<string, bool> BuildMatcher(string pattern)
        {
            string[] alternatives = (pattern ?? "")
                .Split('|')
                .Where(s => s.Length > 0)
                .ToArray();

            if (alternatives.Length == 0)
                return _ => false;

            if (alternatives.Length == 1)
            {
                string single = alternatives[0];
                return line => line != null
                    && line.IndexOf(single, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return line =>
            {
                if (line == null) return false;
                foreach (string alt in alternatives)
                {
                    if (line.IndexOf(alt, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                return false;
            };
        }

        /// <summary>
        /// Regex metacharacters that are NOT supported by the substring matcher.
        /// </summary>
        private static readonly HashSet<char> s_regexMetaChars = new HashSet<char>
        {
            '\\', '^', '$', '*', '+', '?', '(', ')', '[', ']', '{', '}', '.'
        };

        /// <summary>
        /// Returns the existing "no matches" text augmented with diagnostics that help
        /// an LLM agent self-correct.
        /// </summary>
        /// <param name="pattern">The search pattern that produced zero hits.</param>
        /// <param name="scope">Resolved scope string (file path, glob, etc.).</param>
        /// <param name="filesScanned">Number of files actually scanned (for find_in_files); -1 for grep_file.</param>
        /// <param name="baseMessage">The original no-match message.</param>
        public static string DescribeSearchMiss(string pattern, string scope, int filesScanned, string baseMessage)
        {
            var sb = new StringBuilder();
            sb.Append(baseMessage);

            var diagnostics = new List<string>();

            /* (a) Regex-syntax detection */
            string[] alternatives = (pattern ?? "").Split('|');
            var offendingChars = new HashSet<char>();
            foreach (string alt in alternatives)
            {
                foreach (char c in alt)
                {
                    if (s_regexMetaChars.Contains(c))
                        offendingChars.Add(c);
                }
            }

            if (offendingChars.Count > 0)
            {
                string charsList = string.Join(", ", offendingChars.Order().Select(c => EscapeForDisplay(c)));
                string stripped = StripRegexMetacharacters(pattern);
                diagnostics.Add($"Pattern contains regex metacharacters ({charsList}) but only literal substring matching is supported (| for OR). Try: \"{stripped}\"");
            }

            /* (b) Whitespace/case note */
            if (pattern != null && (pattern.Contains("  ") || pattern.Contains("\t")))
            {
                string fragment = System.Text.RegularExpressions.Regex.Replace(pattern.Trim(), @"\s+", " ").Trim();
                if (fragment.Length > 30)
                    fragment = fragment.Substring(0, 30).TrimEnd();
                diagnostics.Add($"Pattern has multi-space/tab runs — whitespace must match literally. Try shorter fragment: \"{fragment}\"");
            }

            /* (c) Over-long pattern */
            if (pattern != null && pattern.Length > 40)
            {
                string[] words = pattern.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string suggestion;
                if (words.Length >= 3)
                {
                    int start = Math.Max(0, words.Length / 2 - 1);
                    int count = Math.Min(3, words.Length - start);
                    suggestion = string.Join(" ", words.Skip(start).Take(count));
                }
                else
                {
                    suggestion = pattern.Substring(0, Math.Min(30, pattern.Length)).TrimEnd();
                }
                diagnostics.Add($"Pattern is {pattern.Length} chars — try a distinctive 2-4 word fragment: \"{suggestion}\"");
            }

            /* (d) Scope echo / file count */
            if (filesScanned >= 0)
            {
                if (filesScanned == 0)
                    diagnostics.Add($"0 files matched the glob \"{scope}\" — check the glob pattern or search directory.");
                else
                    sb.Append($" ({filesScanned} file{(filesScanned == 1 ? "" : "s")} scanned)");
            }

            if (diagnostics.Count > 0)
            {
                sb.Append("\n");
                sb.Append(string.Join("\n", diagnostics));
            }

            return sb.ToString();
        }

        private static string EscapeForDisplay(char c)
        {
            return c switch
            {
                '\\' => "\\",
                _ => c.ToString()
            };
        }

        private static string StripRegexMetacharacters(string pattern)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '|')
                {
                    sb.Append(c);
                    continue;
                }
                if (!s_regexMetaChars.Contains(c))
                {
                    sb.Append(c);
                }
                else if (c == '\\' && i + 1 < pattern.Length && s_regexMetaChars.Contains(pattern[i + 1]))
                {
                    // "\." -> ".", skip backslash, next char handled in next iteration
                    i++;
                    continue;
                }
            }
            string result = sb.ToString();
            return result.Trim('.');
        }
    }
}
