// File: ArgTokenizer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;
using System.Text;

namespace DevMind
{
    /// <summary>
    /// Splits a user-supplied argument string into argv tokens for
    /// <see cref="ShellRunner.ExecuteArgvAsync"/> — whitespace separates, single or double
    /// quotes group (quotes are removed). Nothing is interpreted by a shell, so text such as
    /// "; rm -rf" stays a literal argument. Moved from DevMindTools so the agent's
    /// read_file git branch and the MCP tools share one tokenizer.
    /// </summary>
    public static class ArgTokenizer
    {
        public static List<string> Tokenize(string input)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(input)) return tokens;

            var sb = new StringBuilder();
            char quote = '\0';
            bool inTok = false;

            foreach (char c in input)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else sb.Append(c);
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                    inTok = true;
                }
                else if (c == ' ' || c == '\t')
                {
                    if (inTok) { tokens.Add(sb.ToString()); sb.Clear(); inTok = false; }
                }
                else
                {
                    sb.Append(c);
                    inTok = true;
                }
            }

            if (inTok) tokens.Add(sb.ToString());
            return tokens;
        }
    }
}
