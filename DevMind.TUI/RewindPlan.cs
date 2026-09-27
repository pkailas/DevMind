// File: RewindPlan.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The decision half of /rewind.
//
// Rewind is a FORK, not a delete: /rewind N copies everything BEFORE user turn N into a new
// session and leaves the original untouched. The one place an off-by-one would be silent is
// the cut: a row too far in and the fork carries the turn it was supposed to undo, a row too
// far short and it loses the last real exchange. So the cut is a pure function here — rows
// in, rows to copy — testable without a store, a terminal, or a running turn.
//
// What it has to get right:
//
//  * The NUMBERING. A turn is a real user question. The agentic loop writes synthetic user
//    rows (continuation prompts, task_done nags) with the IsSynthetic flag — those are not
//    turns and are not numbered. The listing and the cut must agree on the same numbering,
//    so both come from ListTurns.
//
//  * The CUT. It is not arithmetic on TurnIndex. TurnIndex is the context-aging clock
//    (LlmClient.CurrentTurn): one value per user turn, shared by every iteration of it —
//    which means a single turn can hold several user rows (an ask_caller answer re-enters
//    the loop under the SAME turn number) and the rows of one turn are ordered between
//    themselves by CreatedAt, which the store cannot resolve below the millisecond. The
//    cut therefore stands on the POSITION of turn N's user row in the store's own
//    chronological order (TurnIndex ASC, CreatedAt ASC) — the same order a /resume of this
//    session reads, so the fork can never disagree with what a resume would draw.

using System;
using System.Collections.Generic;

namespace DevMind
{
    /// <summary>One numbered turn in a session's listing: a real user question.</summary>
    internal readonly struct RewindTurn
    {
        /// <summary>1-based number, as shown by the listing and taken by /rewind N.</summary>
        public readonly int Number;

        /// <summary>When the user asked.</summary>
        public readonly DateTime CreatedAt;

        /// <summary>The first line of the prompt, truncated for display.</summary>
        public readonly string FirstLine;

        /// <summary>The full original prompt text — what /rewind N restores to the input box.</summary>
        public readonly string Prompt;

        public RewindTurn(int number, DateTime createdAt, string firstLine, string prompt)
        {
            Number = number;
            CreatedAt = createdAt;
            FirstLine = firstLine ?? string.Empty;
            Prompt = prompt ?? string.Empty;
        }
    }

    /// <summary>The outcome of a /rewind N plan: what the fork copies and what comes back.</summary>
    internal sealed class RewindPlan
    {
        /// <summary>The turn number the plan was built for.</summary>
        public int TurnNumber { get; }

        /// <summary>
        /// The messages the new session gets: every row that precedes turn N's user row in
        /// chronological order — all roles, synthetic rows included, order and TurnIndex
        /// preserved. Empty when N is 1.
        /// </summary>
        public HistoryMessage[] MessagesToCopy { get; }

        /// <summary>Turn N's original prompt, restored to the input box (not sent).</summary>
        public string RestoredPrompt { get; }

        public RewindPlan(int turnNumber, HistoryMessage[] messagesToCopy, string restoredPrompt)
        {
            TurnNumber = turnNumber;
            MessagesToCopy = messagesToCopy ?? Array.Empty<HistoryMessage>();
            RestoredPrompt = restoredPrompt ?? string.Empty;
        }
    }

    /// <summary>
    /// The pure decisions behind /rewind: which rows exist as turns, where the cut falls, and
    /// what the fork is titled. The handler does the I/O; this makes the cut impossible to
    /// get wrong by a row.
    /// </summary>
    internal static class RewindPlanner
    {
        /// <summary>
        /// The user turns of a session in chronological order, 1..N. Synthetic user rows
        /// (the flag, or a known synthetic prompt text on legacy rows) are not turns and are
        /// not numbered — the same rule <c>SessionResume.PairMessages</c> applies when it
        /// rebuilds a session, so the number shown and the cut taken cannot drift.
        /// </summary>
        public static IReadOnlyList<RewindTurn> ListTurns(IReadOnlyList<HistoryMessage> messages)
        {
            var turns = new List<RewindTurn>();
            if (messages == null) return turns;

            int number = 0;
            foreach (var msg in messages)
            {
                if (msg == null || msg.Role != "user") continue;
                if (IsSyntheticUserRow(msg)) continue;
                number++;
                turns.Add(new RewindTurn(number, msg.CreatedAt, FirstLineOf(msg.Content), msg.Content));
            }
            return turns;
        }

        /// <summary>
        /// The /rewind N plan over a session's rows, in the store's chronological order
        /// (the order <c>LoadSessionMessagesAsync</c> returns). The cut is the position of
        /// turn N's user row: everything before it is copied, everything from it on — that
        /// prompt included — is left to the original session.
        /// </summary>
        /// <param name="messages">The session's rows, chronological.</param>
        /// <param name="turn">The 1-based turn number to rewind to (BEFORE that turn).</param>
        /// <param name="forkSessionId">The new session's id, stamped onto every copied row.</param>
        /// <param name="copiedAt">When the copy was written — the rows' CreatedAt is refreshed
        /// to this, because the fork's ordering must stand on TurnIndex alone: the original's
        /// row order within a turn can be millisecond ties the store cannot re-resolve, and
        /// keeping old stamps would date the fork's history before its own session record.</param>
        /// <param name="error">Why nothing happened, when the plan cannot be made.</param>
        public static RewindPlan Build(IReadOnlyList<HistoryMessage> messages, int turn,
                                       string forkSessionId, DateTime copiedAt, out string error)
        {
            var turns = ListTurns(messages);

            if (turns.Count == 0)
            {
                error = "This session has no user turns to rewind.";
                return null;
            }
            if (turn < 1 || turn > turns.Count)
            {
                error = $"Turn #{turn} is out of range — this session has {turns.Count} user " +
                        $"turn(s) (1 = the first).";
                return null;
            }

            // Walk in chronological order and cut at the row that OPENS turn N — the Nth
            // real user row. Position, not TurnIndex arithmetic: see the file header.
            var copy = new List<HistoryMessage>();
            int seen = 0;
            foreach (var msg in messages)
            {
                if (msg == null) continue;
                if (msg.Role == "user" && !IsSyntheticUserRow(msg))
                {
                    seen++;
                    if (seen == turn) break;
                }
                copy.Add(new HistoryMessage
                {
                    SessionId = forkSessionId,
                    MachineName = msg.MachineName,
                    TurnIndex = msg.TurnIndex,     // preserved — the fork's order is the original's
                    Role = msg.Role,
                    Content = msg.Content,
                    CreatedAt = copiedAt,
                    IsSynthetic = msg.IsSynthetic,
                });
            }

            error = null;
            return new RewindPlan(turn, copy.ToArray(), turns[turn - 1].Prompt);
        }

        /// <summary>
        /// The /rewind listing: the session's user turns, numbered, with time and the first
        /// line of each prompt — the shape /history uses for its sessions.
        /// </summary>
        public static string ListTurnsText(IReadOnlyList<HistoryMessage> messages)
        {
            var turns = ListTurns(messages);
            if (turns.Count == 0)
                return "This session has no user turns to rewind.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("User turns in this session (oldest first):");
            foreach (var t in turns)
                sb.AppendLine($"  [{t.Number}] {t.CreatedAt:HH:mm}  {t.FirstLine}");
            sb.Append($"  /rewind <n> forks the session to BEFORE turn n; " +
                      $"the original stays (recover with /history + /resume).");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The fork's title: the old title, or the first prompt's first line when the
        /// session had none, plus where it came from.
        /// </summary>
        /// <param name="oldTitle">The current session's title ("" when untitled).</param>
        /// <param name="messages">The session's rows, chronological — for the first prompt.</param>
        /// <param name="turn">The turn number the fork was taken from.</param>
        public static string BuildTitle(string oldTitle, IReadOnlyList<HistoryMessage> messages, int turn)
        {
            string baseTitle = (oldTitle ?? string.Empty).Trim();
            if (baseTitle.Length == 0)
            {
                var turns = ListTurns(messages);
                baseTitle = turns.Count > 0 ? FirstLineOf(turns[0].Prompt) : string.Empty;
            }
            if (baseTitle.Length == 0)
                baseTitle = "(untitled)";
            return $"{baseTitle} (rewound from #{turn})";
        }

        /// <summary>
        /// The display line of a prompt: its first line, trailing whitespace trimmed, capped
        /// at 70 characters with an ellipsis that stays inside the cap.
        /// </summary>
        internal static string FirstLineOf(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return string.Empty;
            int nl = content.IndexOf('\n');
            string line = nl < 0 ? content : content.Substring(0, nl);
            line = line.Trim();
            if (line.Length <= 70) return line;
            return line.Substring(0, 69) + "…";
        }

        /// <summary>
        /// A user row the loop wrote, not the user typed: the IsSynthetic flag, or a known
        /// synthetic prompt on a legacy row written before the column existed — the same
        /// test <c>SessionResume.PairMessages</c> applies.
        /// </summary>
        static bool IsSyntheticUserRow(HistoryMessage msg)
        {
            if (msg.IsSynthetic) return true;
            string trimmed = (msg.Content ?? string.Empty).Trim();
            if (trimmed.Length == 0) return true;
            foreach (var prompt in SyntheticPrompts.All)
                if (trimmed == prompt) return true;
            return false;
        }
    }
}
