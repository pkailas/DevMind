// File: SessionResume.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Turning stored history rows back into a conversation.
//
// This is the valuable half of /resume and it used to live inside a TUI command handler,
// reachable only by typing /history and then /resume into a running terminal. Nothing about
// it is a UI concern: it takes rows and returns pairs. Moving it here is what lets the
// launch flag and the slash command run the same code instead of two implementations that
// would drift the first time someone fixed one of them.
//
// What it has to get right is subtle and invisible from the outside. The agentic loop writes
// synthetic user turns — continuation prompts, task_done nags — between a real question and
// the prose that eventually answers it. Pairing rows naively attaches an answer to whichever
// user row came last, which is usually a machine-generated "continue". So the unit is a
// SEGMENT: a real question, plus every assistant row up to the next real question.

using System;
using System.Collections.Generic;

namespace DevMind
{
    /// <summary>
    /// Rebuilds a conversation from stored history rows.
    /// </summary>
    public static class SessionResume
    {
        /// <summary>
        /// Pair stored rows into alternating user/assistant messages suitable for
        /// <c>PrependMessages</c>.
        /// </summary>
        /// <returns>
        /// The roles and contents (equal length, alternating user/assistant), and the number
        /// of rows dropped — reported to the user, because "12 messages loaded" without it
        /// hides that a session came back smaller than it went in.
        /// </returns>
        public static (string[] Roles, string[] Contents, int Skipped) PairMessages(
            IReadOnlyList<HistoryMessage> messages)
        {
            var paired = new List<(string Role, string Content)>();
            int skipped = 0;

            if (messages == null || messages.Count == 0)
                return (Array.Empty<string>(), Array.Empty<string>(), 0);

            string segmentQuestion = null;    // null = no open segment
            var segmentAnswers = new List<string>();

            foreach (var msg in messages)
            {
                if (msg == null) continue;

                // --- User rows ---
                if (msg.Role == "user")
                {
                    bool isSynthetic = msg.IsSynthetic || IsKnownSyntheticPrompt(msg.Content);

                    if (isSynthetic)
                    {
                        skipped++;
                        // Do not open or close a segment; this turn belongs to the
                        // current open segment (if any) as scaffolding.
                        continue;
                    }

                    // Non-synthetic user row: close the previous segment (if any) and
                    // open a new one.
                    if (segmentQuestion != null)
                    {
                        // Flush previous segment.
                        if (segmentAnswers.Count > 0)
                        {
                            paired.Add(("user", segmentQuestion));
                            paired.Add(("assistant", string.Join("\n\n", segmentAnswers)));
                        }
                        else
                        {
                            // Segment had no surviving assistant content — drop entirely.
                            skipped++; // count the user row
                        }
                        segmentAnswers.Clear();
                    }

                    segmentQuestion = msg.Content;
                }
                // --- Assistant rows ---
                else if (msg.Role == "assistant")
                {
                    if (segmentQuestion == null)
                    {
                        // Before the first real user row — discard.
                        skipped++;
                        continue;
                    }

                    string cleaned = StripTuiDecorations(msg.Content);
                    if (cleaned != null)
                    {
                        segmentAnswers.Add(cleaned);
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }

            // Flush the final segment.
            if (segmentQuestion != null)
            {
                if (segmentAnswers.Count > 0)
                {
                    paired.Add(("user", segmentQuestion));
                    paired.Add(("assistant", string.Join("\n\n", segmentAnswers)));
                }
                else
                {
                    skipped++; // count the user row
                }
            }

            var roles = new string[paired.Count];
            var contents = new string[paired.Count];
            for (int i = 0; i < paired.Count; i++)
            {
                roles[i] = paired[i].Role;
                contents[i] = paired[i].Content;
            }

            return (roles, contents, skipped);
        }

        /// <summary>
        /// Pick the session a launch flag names: the most recent one for <c>--continue</c>, or
        /// the one whose id matches for <c>--resume</c>. Null when there is nothing to open.
        /// </summary>
        /// <remarks>
        /// <para>
        /// "Most recent" is the first row, because both providers order by LastActiveAt
        /// descending. That is a contract between this and the store, and the one place an
        /// off-by-one would be silent: picking the oldest session still opens A session, still
        /// prints a confident startup line, and only looks wrong once the operator reads an
        /// answer about work they did last week.
        /// </para>
        /// <para>
        /// The id match ignores case because an id is copied out of a listing, never typed
        /// from memory, and a case-mangled paste is a worse failure than a lenient compare.
        /// </para>
        /// </remarks>
        public static SessionSummary SelectSession(IReadOnlyList<SessionSummary> sessions,
                                                   string sessionId, bool continueLatest)
        {
            if (sessions == null || sessions.Count == 0) return null;

            if (continueLatest) return sessions[0];

            string wanted = sessionId?.Trim();
            if (string.IsNullOrEmpty(wanted)) return null;

            foreach (var s in sessions)
            {
                if (s != null && string.Equals(s.SessionId, wanted, StringComparison.OrdinalIgnoreCase))
                    return s;
            }

            return null;
        }

        /// <summary>
        /// Whether a user row matches a known synthetic prompt. Rows written before the
        /// IsSynthetic column existed carry no flag, so the text is checked as well —
        /// otherwise every legacy session resumes with the loop's own scaffolding in it.
        /// </summary>
        private static bool IsKnownSyntheticPrompt(string content)
        {
            string trimmed = content?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return false;
            foreach (var prompt in SyntheticPrompts.All)
            {
                if (trimmed == prompt) return true;
            }
            return false;
        }

        /// <summary>
        /// Strip TUI decoration lines (<c>[CONTEXT]</c>, <c>[TOOL_USE]</c>, <c>[LLM]</c>,
        /// <c>[DIAG]</c>, <c>[FLUSH]</c>, <c>[DROPPED]</c>) from assistant content wherever
        /// they occur. Returns null if stripping leaves the content empty.
        /// <para>
        /// These are rows written before the transcript stopped carrying engine status lines.
        /// Feeding them back to the model as its own past words teaches it that reciting
        /// context meters is part of answering.
        /// </para>
        /// </summary>
        private static string StripTuiDecorations(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;

            var lines = content.Split('\n');
            var cleaned = new List<string>();
            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("[CONTEXT]", StringComparison.Ordinal)
                    || trimmed.StartsWith("[TOOL_USE]", StringComparison.Ordinal)
                    || trimmed.StartsWith("[LLM]", StringComparison.Ordinal)
                    || trimmed.StartsWith("[DIAG]", StringComparison.Ordinal)
                    || trimmed.StartsWith("[FLUSH]", StringComparison.Ordinal)
                    || trimmed.StartsWith("[DROPPED]", StringComparison.Ordinal))
                {
                    continue; // skip decoration line anywhere in the message
                }
                cleaned.Add(line);
            }

            string result = string.Join("\n", cleaned).Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }

        /// <summary>
        /// The one-line caveat printed when a session is resumed. It is said once, at startup,
        /// because it is the only place anyone learns it: tool calls and tool results are not
        /// persisted, so a resumed session is the conversation, not the working state. The
        /// model remembers what it SAID about a file, not what the file contained.
        /// </summary>
        public const string ToolStateCaveat =
            "Tool results are not persisted — the model sees the conversation, not the files it read.";
    }
}
