// File: DepthAutoExtender.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Raises a headless job's depth cap in place when it reaches the cap still converging.
//
// The decision is made by LoopDriver at the boundary that would otherwise send the
// finish-up directive (the request that starts iteration N/N): extending there means the
// model is never told to stop, and the conversation carries on with no context reset —
// which a devmind_task_continue cannot promise. Headless only: the TUI never sets
// LoopDriver.DepthExtender, so its behaviour is unchanged.
//
// Extend iff ALL hold:
//   a. auto-extension is armed and extensions granted < max_extensions
//   b. a failure was resolved in the window, OR the latest build/test is green and at
//      least one mutation landed in the window
//   c. at least MinMutations mutations in the window
//   d. no thrash state: RepeatedFailureCount < ThrashRepeatLimit and no research directive
//      issued for the current signature
//   e. context usage below the context-guard limit, when one is configured and known
// Extension size is the turn's ORIGINAL cap / 2, at least MinExtension. The devmind_task
// 200 clamp applies to the requested max_depth only, so extensions may go past it.

using System;
using System.Collections.Generic;

namespace DevMind
{
    /// <summary>One granted extension, as reported in depth_extensions.</summary>
    public sealed class DepthExtension
    {
        /// <summary>The iteration count at which the cap was reached.</summary>
        public int AtDepth { get; init; }

        /// <summary>The cap after the extension.</summary>
        public int NewCap { get; init; }

        /// <summary>The convergence evidence that justified it.</summary>
        public string SignalSummary { get; init; } = "";
    }

    /// <summary>The outcome of one decision at the cap.</summary>
    public readonly struct DepthExtensionDecision
    {
        public readonly bool Extended;
        public readonly int NewCap;
        /// <summary>The evidence summary (extended) or the failed condition (declined).</summary>
        public readonly string Detail;

        private DepthExtensionDecision(bool extended, int newCap, string detail)
        {
            Extended = extended;
            NewCap = newCap;
            Detail = detail ?? string.Empty;
        }

        public static DepthExtensionDecision Extend(int newCap, string summary) => new DepthExtensionDecision(true, newCap, summary);
        public static DepthExtensionDecision Decline(string reason) => new DepthExtensionDecision(false, 0, reason);
    }

    /// <summary>
    /// The per-turn extension policy for a headless job. The decision runs on the loop's
    /// worker thread; <see cref="Extensions"/> and <see cref="Count"/> may be read from
    /// another thread (devmind_task_status), so the list is guarded.
    /// </summary>
    public sealed class DepthAutoExtender
    {
        /// <summary>Smallest extension granted, whatever the original cap.</summary>
        public const int MinExtension = 10;

        /// <summary>Condition c: mutations the window must hold.</summary>
        public const int MinMutations = 3;

        /// <summary>Condition d: the same failure this many times in a row is thrashing
        /// (LoopDriver's research-nudge threshold).</summary>
        public const int ThrashRepeatLimit = 3;

        private readonly int _originalMaxDepth;
        private readonly int _maxExtensions;
        private readonly Action<int> _applyCap;
        private readonly List<DepthExtension> _extensions = new List<DepthExtension>();
        private readonly object _lock = new object();
        private string _disarmedReason;

        /// <param name="originalMaxDepth">The turn's cap before any extension.</param>
        /// <param name="maxExtensions">Most extensions granted this turn.</param>
        /// <param name="applyCap">Raises the cap — HeadlessSession.SetMaxDepth, the same path a
        /// continuation's max_depth takes.</param>
        public DepthAutoExtender(int originalMaxDepth, int maxExtensions, Action<int> applyCap,
            ConvergenceTracker tracker = null)
        {
            _originalMaxDepth = originalMaxDepth;
            _maxExtensions = Math.Max(0, maxExtensions);
            _applyCap = applyCap ?? throw new ArgumentNullException(nameof(applyCap));
            Tracker = tracker ?? new ConvergenceTracker();
        }

        /// <summary>The progress evidence, fed by LoopDriver once per tool-call iteration.</summary>
        public ConvergenceTracker Tracker { get; }

        /// <summary>Most extensions this turn may grant.</summary>
        public int MaxExtensions => _maxExtensions;

        /// <summary>Iterations each extension adds: the original cap / 2, at least <see cref="MinExtension"/>.</summary>
        public int ExtensionSize => Math.Max(MinExtension, _originalMaxDepth / 2);

        /// <summary>Extensions granted so far this turn.</summary>
        public int Count { get { lock (_lock) return _extensions.Count; } }

        /// <summary>A snapshot of the extensions granted so far.</summary>
        public IReadOnlyList<DepthExtension> Extensions { get { lock (_lock) return _extensions.ToArray(); } }

        /// <summary>Non-null once <see cref="Disarm"/> has been called.</summary>
        public string DisarmedReason { get { lock (_lock) return _disarmedReason; } }

        /// <summary>
        /// Stops any further extension this turn — a caller steer that overrides the job wins
        /// over the harness's own judgement that the job is converging. Idempotent; the first
        /// reason is kept.
        /// </summary>
        public void Disarm(string reason)
        {
            lock (_lock) _disarmedReason ??= string.IsNullOrWhiteSpace(reason) ? "disarmed" : reason;
        }

        /// <summary>
        /// Decide at the cap. On extension the cap is raised through the apply callback and the
        /// extension is recorded; on a decline nothing changes.
        /// </summary>
        /// <param name="atDepth">The iteration count that reached the cap.</param>
        /// <param name="currentCap">The cap in force now.</param>
        /// <param name="state">The loop's thrash-guard state.</param>
        /// <param name="contextUsedPercent">Context usage, or null when unknown.</param>
        /// <param name="contextLimitPercent">The context-guard limit; 0 = none configured.</param>
        public DepthExtensionDecision Decide(int atDepth, int currentCap, LoopState state,
            int? contextUsedPercent, int contextLimitPercent)
        {
            string declined = Check(state, contextUsedPercent, contextLimitPercent);
            if (declined != null) return DepthExtensionDecision.Decline(declined);

            int newCap = currentCap + ExtensionSize;
            string summary = Tracker.Summary();
            lock (_lock)
                _extensions.Add(new DepthExtension { AtDepth = atDepth, NewCap = newCap, SignalSummary = summary });
            _applyCap(newCap);
            return DepthExtensionDecision.Extend(newCap, summary);
        }

        // The first condition that fails, as a reason a transcript reader can act on; null
        // when every condition holds.
        private string Check(LoopState state, int? contextUsedPercent, int contextLimitPercent)
        {
            string disarmed = DisarmedReason;
            if (disarmed != null) return disarmed;                                              // a
            int used = Count;
            if (used >= _maxExtensions) return $"extensions used up ({used}/{_maxExtensions})"; // a

            int window = Tracker.Window;
            int mutations = Tracker.MutationsInWindow;
            bool resolved = Tracker.ResolutionsInWindow >= 1;
            bool greenAfterWork = Tracker.LatestBuildOrTestGreen == true && mutations >= 1;
            if (!resolved && !greenAfterWork)                                                   // b
                return $"no failure resolved and no green build/test after a change in the last {window} iterations";

            if (mutations < MinMutations)                                                       // c
                return $"only {mutations} mutation(s) in the last {window} iterations (need {MinMutations})";

            if (state != null && (state.RepeatedFailureCount >= ThrashRepeatLimit || state.ResearchNudgeIssued)) // d
                return state.ResearchNudgeIssued
                    ? $"thrashing (same failure {state.RepeatedFailureCount}x, research directive already issued)"
                    : $"thrashing (same failure {state.RepeatedFailureCount}x)";

            if (contextLimitPercent > 0 && contextUsedPercent.HasValue && contextUsedPercent.Value >= contextLimitPercent) // e
                return $"context at {contextUsedPercent.Value}% (limit {contextLimitPercent}%)";

            return null;
        }
    }
}
