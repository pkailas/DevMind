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
                // Anything that is not a user row is drawn as an answer. The pairing only ever
                // produces the two, and a third would be new information the transcript should
                // show rather than drop.
                ReplayKind kind = string.Equals(roles[i], "user", StringComparison.Ordinal)
                    ? ReplayKind.User
                    : ReplayKind.Answer;

                plan.Add(new ReplayStep(kind, contents[i]));
            }

            plan.Add(new ReplayStep(ReplayKind.Rule, RuleText));
            return plan;
        }
    }
}
