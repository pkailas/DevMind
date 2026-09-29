// File: McpJournal.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The action-journal entry for one external MCP tool call. The journal's entries are
// { kind, detail, success } (HostAction). An "mcp" entry keeps that shape and packs its
// fields into the detail in one fixed order, so a reviewer reads it like a shell entry:
//
//   comfy.run_workflow {"workflow":"a.json"} → ok, 1,234 chars, 2.3 s
//   comfy.run_workflow {"workflow":"a.json"} → timeout, 187 chars, 900.0 s
//
// The arguments are compact JSON, cut at MaxArgsChars with an ellipsis.

using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevMind
{
    public enum McpCallOutcome { Ok, Error, Timeout }

    public static class McpJournal
    {
        /// <summary>Longest argument text kept in a journal entry.</summary>
        public const int MaxArgsChars = 500;

        /// <summary>
        /// The phrase McpClientManager's per-call timeout message contains ("… timed out after
        /// 120s …"). Shared so the classification cannot drift from the message.
        /// </summary>
        public const string TimeoutPhrase = "timed out after";

        /// <summary>ok, unless the text is an MCP error; a manager timeout is its own outcome.</summary>
        public static McpCallOutcome Classify(string text)
        {
            text ??= "";
            bool error = text.StartsWith("[MCP ERROR]", StringComparison.Ordinal)
                      || text.StartsWith("[MCP TOOL ERROR]", StringComparison.Ordinal);
            if (!error) return McpCallOutcome.Ok;
            return text.StartsWith("[MCP ERROR]", StringComparison.Ordinal)
                   && FirstLine(text).Contains(TimeoutPhrase, StringComparison.Ordinal)
                ? McpCallOutcome.Timeout
                : McpCallOutcome.Error;
        }

        /// <summary>"server.tool {args} → outcome, N chars, S.s s".</summary>
        public static string Detail(string server, string tool, JObject args, McpCallOutcome outcome,
            int resultChars, TimeSpan elapsed)
        {
            string json = args == null ? "{}" : args.ToString(Formatting.None);
            if (json.Length > MaxArgsChars)
                json = json.Substring(0, MaxArgsChars) + "…";
            string word = outcome switch
            {
                McpCallOutcome.Timeout => "timeout",
                McpCallOutcome.Error   => "error",
                _                      => "ok",
            };
            return string.Create(CultureInfo.InvariantCulture,
                $"{server}.{tool} {json} → {word}, {resultChars:N0} chars, {elapsed.TotalSeconds:0.0} s");
        }

        private static string FirstLine(string text)
        {
            int nl = text.IndexOf('\n');
            return nl < 0 ? text : text.Substring(0, nl);
        }
    }
}
