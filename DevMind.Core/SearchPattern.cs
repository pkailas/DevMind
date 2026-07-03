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
using System.Linq;

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
    }
}
