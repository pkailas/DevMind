// File: RepeatedTestFailures.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-58 — repeated identical test failures. job-2108 ran the same failing layout tests about
// ten times while changing its theory each time, and only dumped the real HTML late; once it
// looked, the fix was quick. The H-52 stall guards do not fire because the agent is editing,
// i.e. "progressing". Pure and stateful per turn: HeadlessSession feeds each iteration's tool
// output in at the iteration boundary.
//
//   RepeatedTestFailureGuard  the same failing test name(s) in 3 consecutive test runs ->
//                             one harness note (once per distinct set, once per streak).
//   AutoThinkEscalation       when that note fires on a think=false job, thinking is turned on
//                             at effort "medium" until the tests pass or 15 requests have run
//                             with it, whichever comes first.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>One <c>dotnet test</c> run read from tool output.</summary>
    public sealed class TestRunOutcome
    {
        /// <summary>True when the run reported no failures.</summary>
        public bool Passed { get; }

        /// <summary>The failing test names, sorted ordinal. Empty for a passing run, and also
        /// for a failing run whose output names no tests (quiet verbosity).</summary>
        public IReadOnlyList<string> FailedTests { get; }

        public TestRunOutcome(bool passed, IReadOnlyList<string> failedTests)
        {
            Passed = passed;
            FailedTests = failedTests;
        }

        // VSTest console logger: "  Failed Ns.Class.Method [70 ms]". The trailing duration is
        // what tells it apart from "Failed to load prune package data ..." restore noise.
        private static readonly Regex VsTestFailed = new Regex(
            @"^\s*Failed\s+(\S+)\s+\[[^\]\r\n]*\]\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

        // Microsoft.Testing.Platform (xUnit v3 / MSTest runner): "failed Ns.Class.Method (12ms)".
        private static readonly Regex MtpFailed = new Regex(
            @"^\s*failed\s+(\S+)\s+\([^)\r\n]*\)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

        // A test run happened: normal-verbosity footer, minimal-verbosity summary, or MTP summary.
        private static readonly Regex RunSummary = new Regex(
            @"Test Run (?:Failed|Successful)\.|^\s*(?:Failed|Passed)!\s+-|Test run summary:\s*(?:Failed|Passed)!",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex FailedSummary = new Regex(
            @"Test Run Failed\.|^\s*Failed!\s+-|Test run summary:\s*Failed!",
            RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>
        /// The test run in <paramref name="toolOutput"/>, or null when it holds none (no
        /// summary line: a build that failed before any test ran, or no test command at all).
        /// Several projects' summaries in one output are one run: their failures are merged.
        /// </summary>
        public static TestRunOutcome Parse(string toolOutput)
        {
            if (string.IsNullOrEmpty(toolOutput) || !RunSummary.IsMatch(toolOutput)) return null;

            var failed = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in VsTestFailed.Matches(toolOutput)) failed.Add(m.Groups[1].Value);
            foreach (Match m in MtpFailed.Matches(toolOutput)) failed.Add(m.Groups[1].Value);

            bool passed = failed.Count == 0 && !FailedSummary.IsMatch(toolOutput);
            return new TestRunOutcome(passed, failed.ToList());
        }
    }

    /// <summary>
    /// Counts consecutive test runs that share failing test names. The tracked set is the
    /// names that failed in EVERY run of the current streak, so a full run then a filtered
    /// re-run of one of its failures still counts as the same failure. A passing run, or a
    /// failing run with no name in common with the streak, starts over.
    /// </summary>
    public sealed class RepeatedTestFailureGuard
    {
        /// <summary>Consecutive runs with the same failing test(s) before the note.</summary>
        public const int Repeats = 3;

        private SortedSet<string> _tracked = new SortedSet<string>(StringComparer.Ordinal);
        private int _streak;
        private bool _firedThisStreak;
        private readonly HashSet<string> _firedSets = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The current streak length (runs).</summary>
        public int Streak => _streak;

        /// <summary>
        /// Records one test run (null = this iteration ran none, and changes nothing). Returns
        /// the failing names when this run completes a streak of <see cref="Repeats"/> for a set
        /// that has not had its note yet; otherwise null.
        /// </summary>
        public IReadOnlyList<string> Observe(TestRunOutcome run)
        {
            if (run == null) return null;
            if (run.Passed)
            {
                Reset();
                return null;
            }
            // A failing run that names no test cannot be compared: neither counted nor reset.
            if (run.FailedTests.Count == 0) return null;

            var common = new SortedSet<string>(_tracked, StringComparer.Ordinal);
            common.IntersectWith(run.FailedTests);
            if (_streak == 0 || common.Count == 0)
            {
                _tracked = new SortedSet<string>(run.FailedTests, StringComparer.Ordinal);
                _streak = 1;
                _firedThisStreak = false;
            }
            else
            {
                _tracked = common;
                _streak++;
            }

            if (_streak < Repeats || _firedThisStreak) return null;
            _firedThisStreak = true;
            return _firedSets.Add(string.Join("\n", _tracked)) ? _tracked.ToList() : null;
        }

        private void Reset()
        {
            _tracked = new SortedSet<string>(StringComparer.Ordinal);
            _streak = 0;
            _firedThisStreak = false;
        }

        /// <summary>The note injected into the next prompt (H-58).</summary>
        public static string Note(IReadOnlyList<string> failedTests)
            => $"The same test(s) have failed {Repeats} runs in a row: {string.Join(", ", failedTests)}. " +
               "Before changing code again, print the actual value being asserted (rendered output, response " +
               "body or variable) and compare it with the expectation. Do not change the test's expectation to " +
               "match without saying why.";
    }

    /// <summary>
    /// Thinking turned on by the harness for a think=false job whose tests keep failing the same
    /// way (H-58 addendum). Holds only the state and the decisions; HeadlessSession applies them
    /// to the request options and the journal.
    /// </summary>
    public sealed class AutoThinkEscalation
    {
        /// <summary>Requests sent with auto-enabled thinking before it is turned off again.</summary>
        public const int MaxIterations = 15;

        /// <summary>The reasoning effort auto-enabled thinking runs at.</summary>
        public const string Effort = "medium";

        /// <summary>False when the job already thinks, or was started with auto_think=false.</summary>
        public bool Allowed { get; }
        public bool Active { get; private set; }

        /// <summary>Times thinking was auto-enabled this turn.</summary>
        public int Escalations { get; private set; }

        /// <summary>Requests sent with auto-enabled thinking this turn, over all escalations.</summary>
        public int Iterations { get; private set; }

        private int _activeIterations;

        public AutoThinkEscalation(bool jobThinks, bool autoThink) => Allowed = !jobThinks && autoThink;

        /// <summary>True when thinking should be turned on now.</summary>
        public bool TryEscalate()
        {
            if (!Allowed || Active) return false;
            Active = true;
            Escalations++;
            _activeIterations = 0;
            return true;
        }

        /// <summary>Called once per model request.</summary>
        public void OnRequest()
        {
            if (!Active) return;
            Iterations++;
            _activeIterations++;
        }

        /// <summary>
        /// Why thinking should be turned off at this boundary, or null to leave it as it is:
        /// the tests passed (<paramref name="testsPassed"/>), or <see cref="MaxIterations"/>
        /// requests have run with it.
        /// </summary>
        public string TryDeEscalate(bool testsPassed)
        {
            if (!Active) return null;
            string reason = testsPassed ? "tests passed"
                : _activeIterations >= MaxIterations ? $"{MaxIterations}-iteration cap"
                : null;
            if (reason != null) Active = false;
            return reason;
        }
    }
}
