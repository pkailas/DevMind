// File: PatchCardDecision.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Whether the patch card asks.
//
// Manual mode's promise is that every mutation stops and waits. The executor honours that
// for patches by forcing the diff card rather than by asking a yes/no question — the card
// IS the question — and the TUI's card did not ask. It painted the diff, approved the patch
// and printed "Auto-approved", so the one mutation kind with a preview was the one kind
// Manual mode did not gate.
//
// The card itself is a modal dialog on a Terminal.Gui run loop, which no test can stand up.
// The decision is not: it is "does this mode ask", and it lives here so that question has an
// answer a test can read. What is left at the call site is painting and prompting.

using System;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// The patch card's approval decision, free of any UI.
    /// </summary>
    public static class PatchCardDecision
    {
        /// <summary>
        /// Approve one patch shown on the card. In <see cref="ApprovalMode.Manual"/> the
        /// answer is whatever <paramref name="ask"/> returns; in <see cref="ApprovalMode.Auto"/>
        /// it is yes and <paramref name="ask"/> is never invoked.
        /// <para>
        /// <paramref name="confidence"/> is taken and deliberately not consulted. The card is
        /// reached for two different reasons — a fuzzy match in Auto, every patch in Manual —
        /// and the point of this function is that the answer depends on the MODE, not on why
        /// the card came up. A future "always confirm fuzzy" would change that here, in the
        /// one place, rather than at a call site that also owns a dialog.
        /// </para>
        /// </summary>
        /// <param name="ask">
        /// Poses the question. Asynchronous because the real one marshals to the UI thread and
        /// runs a modal; a synchronous overload for tests alone would be a second copy of this
        /// decision, and two copies of a decision is how the card and the mode disagreed in the
        /// first place. Tests pass <c>() =&gt; Task.FromResult(answer)</c>.
        /// </param>
        public static Task<bool> ApproveAsync(ApprovalMode mode, PatchConfidence confidence,
                                              Func<Task<bool>> ask)
        {
            if (mode != ApprovalMode.Manual)
                return Task.FromResult(true);

            if (ask == null) throw new ArgumentNullException(nameof(ask));
            return ask();
        }

        /// <summary>
        /// The card's question. Names the file and the match quality, because a fuzzy match is
        /// the case where reading the diff before answering actually matters.
        /// </summary>
        public static string Question(string fileName, PatchConfidence confidence)
        {
            string badge = confidence == PatchConfidence.Fuzzy ? "Fuzzy ⚠" : "Exact ✓";
            return $"Apply patch to {fileName}? ({badge})";
        }

        /// <summary>The transcript record of a refusal. The model is told separately, by the
        /// executor's SKIPPED tool result; this is the line the operator reads afterwards.</summary>
        public static string DeclinedLine(string fileName)
            => $"[PATCH] Declined by user: {fileName}\n";
    }
}
