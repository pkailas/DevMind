// File: ConvergenceTracker.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Is a headless job still converging? The evidence the depth-cap auto-extension reads.
//
// A delegated job that is mid-build-and-fix and still making progress used to stop at
// max_depth with "depth_cap", and the continuation that resumed it usually started with a
// context eviction and spent its budget re-reading what the first job already knew. Fed
// once per tool-call iteration by LoopDriver, this keeps a sliding window of what the job
// actually did: mutations that landed, and failures it moved past. DepthAutoExtender turns
// that into the extend / don't-extend decision.
//
// What counts:
//   * a mutation is a successful write the executor itself counted in ExecutionResult —
//     patches applied, files created, appended, deleted, renamed. The action journal was the
//     alternative; it also carries shell commands, steers and nudges, and is per turn rather
//     than per iteration, so ExecutionResult is the cleaner per-iteration source.
//   * a failure is RESOLVED when the consequential action that produced it later runs clean
//     (exit 0, no error), or when a DIFFERENT failure replaces it after an intervening
//     mutation — the job changed something and the old error went away. A build failure
//     followed by a test failure counts even without a mutation in between: the build now
//     passes, so the job moved forward. The same signature again is never a resolution.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>
    /// Sliding-window record of a headless job's progress. Not thread-safe: written and read
    /// on the loop's worker thread only.
    /// </summary>
    public sealed class ConvergenceTracker
    {
        /// <summary>How many recent iterations the window covers.</summary>
        public const int DefaultWindow = 30;

        private enum Stage { None, Build, Test }

        // A run_tests call (AgenticExecutor writes "TEST <project>") or a shell command that
        // runs a test runner. Checked before Build: "dotnet test" also builds.
        private static readonly Regex TestCommand = new Regex(
            @"^TEST\b|\b(?:dotnet\s+test|vstest|pytest|jest|vitest|(?:npm|yarn|pnpm)\s+(?:run\s+)?test|cargo\s+test|go\s+test|mvn\s+test|gradle\w*\s+test)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex BuildCommand = new Regex(
            @"\b(?:dotnet\s+build|msbuild|tsc|(?:npm|yarn|pnpm)\s+(?:run\s+)?build|cargo\s+build|go\s+build|mvn\s+(?:compile|package)|gradle\w*\s+build|make)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly int _window;
        private readonly Queue<(int mutations, int resolutions)> _recent = new Queue<(int, int)>();

        private string _currentFailure;
        private Stage _currentFailureStage;
        private int _mutationsSinceFailure;

        public ConvergenceTracker(int window = DefaultWindow)
        {
            if (window < 1) throw new ArgumentOutOfRangeException(nameof(window));
            _window = window;
        }

        /// <summary>Iterations the window covers.</summary>
        public int Window => _window;

        /// <summary>Successful mutations in the window.</summary>
        public int MutationsInWindow { get; private set; }

        /// <summary>Failure signatures resolved in the window.</summary>
        public int ResolutionsInWindow { get; private set; }

        /// <summary>The outcome of the most recent build or test run: true green, false red,
        /// null when none has run yet. Not windowed — "most recent" is the point.</summary>
        public bool? LatestBuildOrTestGreen { get; private set; }

        /// <summary>The failure signature currently outstanding, or null.</summary>
        public string CurrentFailure => _currentFailure;

        /// <summary>
        /// Record one tool-call iteration.
        /// </summary>
        /// <param name="mutations">Successful writes this iteration.</param>
        /// <param name="failureSignature">Normalized signature of this iteration's failure, or
        /// null when it had none (LoopDriver's thrash-guard signature).</param>
        /// <param name="shellExitCode">Exit code of the iteration's shell/build/test run, or
        /// null when none ran.</param>
        /// <param name="shellCommand">The command that produced <paramref name="shellExitCode"/>.</param>
        public void Observe(int mutations, string failureSignature, int? shellExitCode, string shellCommand)
        {
            if (mutations < 0) mutations = 0;
            Stage stage = Classify(shellCommand);
            if (shellExitCode.HasValue && stage != Stage.None)
                LatestBuildOrTestGreen = shellExitCode.Value == 0 && failureSignature == null;

            int resolved = 0;
            _mutationsSinceFailure += mutations;

            if (_currentFailure != null)
            {
                if (failureSignature == null)
                {
                    // Mirrors LoopDriver: only a clean shell run clears the failure; a read-only
                    // iteration between failures leaves it outstanding.
                    if (shellExitCode == 0)
                    {
                        resolved = 1;
                        _currentFailure = null;
                    }
                }
                else if (!string.Equals(failureSignature, _currentFailure, StringComparison.Ordinal))
                {
                    bool movedForward = _mutationsSinceFailure > 0
                        || (_currentFailureStage == Stage.Build && stage == Stage.Test);
                    if (movedForward) resolved = 1;
                    SetFailure(failureSignature, stage);
                }
            }
            else if (failureSignature != null)
            {
                SetFailure(failureSignature, stage);
            }

            _recent.Enqueue((mutations, resolved));
            MutationsInWindow += mutations;
            ResolutionsInWindow += resolved;
            while (_recent.Count > _window)
            {
                var (m, r) = _recent.Dequeue();
                MutationsInWindow -= m;
                ResolutionsInWindow -= r;
            }
        }

        /// <summary>
        /// Forget everything observed so far: the window, the outstanding failure and the latest
        /// build/test outcome. Called when a caller override steer redirects the job — progress
        /// toward the old direction is no evidence that the new one is converging.
        /// </summary>
        public void Reset()
        {
            _recent.Clear();
            MutationsInWindow = 0;
            ResolutionsInWindow = 0;
            LatestBuildOrTestGreen = null;
            _currentFailure = null;
            _currentFailureStage = Stage.None;
            _mutationsSinceFailure = 0;
        }

        /// <summary>One-line evidence summary for the transcript and the result.</summary>
        public string Summary()
        {
            string latest = LatestBuildOrTestGreen switch
            {
                true => "green",
                false => "failing",
                null => "not run",
            };
            return $"{ResolutionsInWindow} failure(s) resolved, {MutationsInWindow} mutation(s) in the last " +
                   $"{Math.Min(_recent.Count, _window)} iteration(s), latest build/test {latest}";
        }

        private void SetFailure(string signature, Stage stage)
        {
            _currentFailure = signature;
            _currentFailureStage = stage;
            _mutationsSinceFailure = 0;
        }

        private static Stage Classify(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return Stage.None;
            if (TestCommand.IsMatch(command)) return Stage.Test;
            if (BuildCommand.IsMatch(command)) return Stage.Build;
            return Stage.None;
        }
    }
}
