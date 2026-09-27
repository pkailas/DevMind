// File: ResumeReplay.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Putting the resumed conversation back on the screen.
//
// --resume restored the conversation into the MODEL's history and told the operator so in
// one line. The pane stayed empty. That is a session where the agent remembers everything
// and the person remembers whatever they did before they quit — and the only way to find out
// what the model now knows was to ask it.
//
// So the pairs get drawn as well as prepended. What matters is that those are the same
// pairs: a replay assembled from a second read of the store, or filtered a second time,
// could differ from what the model was given and would be a transcript that lies about the
// context behind it. Plan takes the arrays that went to PrependMessages, and the call site
// hands it those exact arrays.
//
// The drawing is Terminal.Gui and cannot be tested. The decision — what is drawn, in what
// order, and that the rule closes it — is this.

using System;
using System.Collections.Generic;

namespace DevMind
{
    /// <summary>What one step of a replay draws.</summary>
    internal enum ReplayKind
    {
        /// <summary>A question, echoed the way the input loop echoes a live one.</summary>
        User,
        /// <summary>An answer, drawn through the answer path so markdown and tables apply.</summary>
        Answer,
        /// <summary>
        /// A task_done summary that follows a prose assistant row — drawn as one dim line,
        /// not in full, so the replay shows the same thing the live session did. The summary
        /// text is parked on the step for /expand.
        /// </summary>
        AnswerCollapsed,
        /// <summary>The boundary between what was restored and what happens next.</summary>
        Rule,
    }

    /// <summary>One drawing instruction.</summary>
    internal readonly struct ReplayStep
    {
        public readonly ReplayKind Kind;
        public readonly string Text;

        public ReplayStep(ReplayKind kind, string text)
        {
            Kind = kind;
            Text = text ?? string.Empty;
        }
    }

    /// <summary>Turns restored message pairs into a drawing plan. Pure.</summary>
    internal static class ResumeReplay
    {
        /// <summary>
        /// The line between the restored conversation and the live one.
        /// <para>
        /// It earns its place by answering the question the replay itself creates: everything
        /// above this looks exactly like a live session, because it is drawn by the same code,
        /// so without a boundary the operator cannot tell what they are about to add to from
        /// what they are looking back at.
        /// </para>
        /// </summary>
        public const string RuleText = "── resumed above · new turns below ──";

        /// <summary>ASCII, for a terminal whose font has no box drawing (see brief 12/14).</summary>
        public const string AsciiRuleText = "-- resumed above * new turns below --";

        /// <summary>
        /// The plan for a restored conversation, in order, closed by the rule.
        /// </summary>
        /// <param name="roles">Alternating "user"/"assistant", as <c>SessionResume.PairMessages</c> built them.</param>
        /// <param name="contents">The matching texts. Same length as <paramref name="roles"/>.</param>
        /// <returns>
        /// An empty plan when there is nothing restored — including a ragged pair of arrays,
        /// which would mean something upstream is broken and is not a thing to half-draw.
        /// </returns>
        public static IReadOnlyList<ReplayStep> Plan(string[] roles, string[] contents)
        {
            if (roles == null || contents == null) return Array.Empty<ReplayStep>();
            if (roles.Length == 0 || roles.Length != contents.Length) return Array.Empty<ReplayStep>();

            var plan = new List<ReplayStep>(roles.Length + 1);

            for (int i = 0; i < roles.Length; i++)
            {
                bool isUser = string.Equals(roles[i], "user", StringComparison.Ordinal);

                if (isUser)
                {
                    plan.Add(new ReplayStep(ReplayKind.User, contents[i]));
                    continue;
                }

                // Two consecutive assistant rows: the first is the terminal iteration's prose
                // (saved as the turn's assistant message), the second is the task_done summary
                // (saved by SaveDrawnAnswerAsync). In the live session the second was collapsed
                // when the prose was real — the replay must show the same thing. The prose
                // count is the first row's visible-prose characters, the same measure the live
                // path uses.
                bool precededByAssistant = i > 0 &&
                    !string.Equals(roles[i - 1], "user", StringComparison.Ordinal);

                if (precededByAssistant &&
                    AnswerDedup.ShouldCollapse(
                        AnswerDedup.CountVisibleProse(contents[i - 1]),
                        contents[i],
                        AnswerKind.TaskDone))
                {
                    plan.Add(new ReplayStep(ReplayKind.AnswerCollapsed, contents[i]));
                }
                else
                {
                    plan.Add(new ReplayStep(ReplayKind.Answer, contents[i]));
                }
            }

            plan.Add(new ReplayStep(ReplayKind.Rule, RuleText));
            return plan;
        }
    }
}
