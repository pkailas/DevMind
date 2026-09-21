// File: TestVerification.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Structural test counts for devmind_task_* test_verification. The whole point:
// the harness derives the total test count from the `dotnet test` output IT
// itself captured, so a number in the result can no longer be just what the
// agent claimed to have seen. Standing rule 4 ("a baseline test count is not a
// pass") is enforced here structurally: when the baseline suite is run by the
// harness before the agent starts, the result carries a delta the agent never
// reported.
//
// Honesty invariant (non-negotiable): when the harness cannot stand behind a
// number — no summary line, ambiguous output, internally inconsistent counts,
// failed baseline run — the field is null and the reason is stated. There is
// no fallback that guesses.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DevMind.McpServer
{
    /// <summary>
    /// One `dotnet test` run plus the harness-derived total test count parsed
    /// from that run's own output. Wraps the shared <see cref="BuildVerification"/>
    /// (whose shape stays byte-identical in build_verification and in the
    /// output_tail here) rather than altering it. <see cref="Total"/> is null
    /// ONLY when the parse cannot stand behind a number; <see cref="ParseFailure"/>
    /// states exactly why in that case.
    /// </summary>
    internal sealed class TestVerification
    {
        public required BuildVerification Raw { get; init; }

        /// <summary>Total tests in this run (summed across test projects when the
        /// run covers several). Null when unparseable — see <see cref="ParseFailure"/>.</summary>
        public int? Total { get; init; }

        /// <summary>Why <see cref="Total"/> is null (null when Total is known).</summary>
        public string? ParseFailure { get; init; }

        public bool Succeeded => Raw.Succeeded;
        public string Command => Raw.Command;
        public int ExitCode => Raw.ExitCode;
        public string OutputTail => Raw.OutputTail;

        /// <summary>Builds the verification from a finished run: keeps the last
        /// ~2 KB as the tail (unchanged from the old behavior) and parses the
        /// total from the FULL output (a project's summary line can fall outside
        /// the tail on a multi-project run).</summary>
        public static TestVerification FromRun(string command, int exitCode, string output)
        {
            const int TailChars = 2_000;
            string tail = output.Length <= TailChars ? output : output.Substring(output.Length - TailChars);
            var (total, why) = TestSummaryParse.Parse(output);
            return new TestVerification
            {
                Raw = new BuildVerification { Command = command, ExitCode = exitCode, OutputTail = tail },
                Total = total,
                ParseFailure = total == null ? why : null,
            };
        }
    }

    /// <summary>
    /// Parses test-run summaries out of `dotnet test` output. Never throws.
    ///
    /// The real .NET 10 per-project summary line on this machine (captured from
    /// an actual run, not guessed):
    ///   Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 4 s - DevMind.McpServer.Tests.dll (net10.0)
    /// Column widths are not a contract, so the regex is whitespace-tolerant;
    /// what is a contract is the field names and their order (Failed, Passed,
    /// Skipped, Total), which VSTest has used for years.
    /// </summary>
    internal static class TestSummaryParse
    {
        private static readonly Regex SummaryLinePattern = new(
            @"Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+).*?-\s*([\w.\-]+\.dll)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Returns (total, null) on a confident parse, or (null, reason) when
        /// the harness must not state a number. Multi-project runs sum the
        /// per-project totals; a repeated summary line for one project is
        /// ambiguous and yields null, not a guess.
        /// </summary>
        public static (int? total, string? why) Parse(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return (null, "no test summary line found in output");

            var perProject = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawLine in output.Split('\n'))
            {
                var m = SummaryLinePattern.Match(rawLine);
                if (!m.Success) continue;

                int failed = int.Parse(m.Groups[1].Value);
                int passed = int.Parse(m.Groups[2].Value);
                int skipped = int.Parse(m.Groups[3].Value);
                int total = int.Parse(m.Groups[4].Value);
                string project = m.Groups[5].Value;

                // Internal consistency: the line must account for every test it
                // counts. A mismatch means this is not the line we think it is.
                if (failed + passed + skipped != total)
                    return (null, $"internally inconsistent summary line for {project} " +
                                  $"(Failed+Passed+Skipped {failed}+{passed}+{skipped} != Total {total})");

                if (perProject.ContainsKey(project))
                    return (null, $"ambiguous output: a summary line for {project} appears more than once — cannot tell which run is authoritative");

                perProject[project] = total;
            }

            if (perProject.Count == 0)
                return (null, "no test summary line found in output");

            int sum = 0;
            foreach (int t in perProject.Values) sum += t;
            return (sum, null);
        }
    }

    /// <summary>
    /// The test_verification payload for devmind_task_result and the persisted
    /// result sidecar — one source for both. The raw run fields (command,
    /// succeeded, exit_code, output_tail) keep today's exact names; the count
    /// fields are new. <see cref="AgentJob.Tests"/> being null (verify_tests
    /// off, no file changes, or the build verification failed) still yields
    /// null test_verification — exactly today's shape for callers who pass
    /// neither flag.
    /// </summary>
    internal static class TestVerificationPayload
    {
        public static object? Create(AgentJob job)
        {
            var after = job.Tests;
            if (after == null) return null;

            int? baseline = null;
            string? baselineWhy;
            var baseRun = job.BaselineTests;
            if (baseRun == null)
            {
                // Reachable only when the before-run was skipped (test_baseline off)
                // or the agent run never started it (job cancelled while queued).
                baselineWhy = "baseline run skipped (test_baseline off or job did not reach the before-run)";
            }
            else if (baseRun.Total == null)
            {
                // A parseable total is the criterion for a usable baseline — a
                // red run that still produced its summary line did measure the
                // suite. Only when no total is present is there no number to
                // stand behind, and the reason must say whether the suite never
                // ran to a summary at all (build failure, empty output) or ran
                // and the summary did not parse (ambiguous or inconsistent line).
                baselineWhy = baseRun.Succeeded
                    ? $"baseline test run had no parseable total ({baseRun.ParseFailure ?? "unknown parse failure"})"
                    : $"baseline test run failed (exit code {baseRun.ExitCode}) and produced no parseable total ({baseRun.ParseFailure ?? "no test summary line found in output"})";
            }
            else
            {
                // The count is structural (tests added or removed), so it holds
                // even when the baseline run exited non-zero. A red baseline is
                // disclosed in baseline_unavailable_reason and in the note, never
                // silently presented as a clean measurement. Report, do not
                // block: a red baseline must not become an incomplete reason.
                baseline = baseRun.Total;
                baselineWhy = baseRun.Succeeded
                    ? null
                    : $"baseline test run had failing tests (exit code {baseRun.ExitCode}) — structural before-count, suite already red before this task";
            }

            int? delta = after.Total.HasValue && baseline.HasValue
                ? after.Total.Value - baseline.Value
                : null;

            // Emit the field, do not make it an incomplete reason: deleting a
            // test can be legitimate. Report, do not block.
            int? testsRemoved = delta.HasValue ? Math.Max(0, -delta.Value) : null;

            string note;
            if (delta.HasValue)
            {
                note = $"harness-measured test counts: {baseline} before this task, {after.Total} after (delta {FormatSigned(delta.Value)})";
                if (baselineWhy != null)
                    note += $"; baseline was a failing run — delta is structural, suite was already red before this task";
            }
            else if (after.Total.HasValue)
            {
                note = $"harness-measured test counts: {after.Total} after; baseline unavailable ({baselineWhy}) — total reported, no delta claimed";
            }
            else
            {
                note = $"no harness-measured test total ({ReasonForMissingTotal(after)})";
            }

            return new
            {
                command = after.Command,
                succeeded = after.Succeeded,
                exit_code = after.ExitCode,
                output_tail = after.OutputTail,
                total = after.Total,
                total_unavailable_reason = after.Total.HasValue ? null : ReasonForMissingTotal(after),
                baseline_total = baseline,
                baseline_unavailable_reason = baselineWhy,
                delta,
                tests_removed = testsRemoved,
                note,
            };
        }

        private static string ReasonForMissingTotal(TestVerification run)
            => run.ParseFailure ?? "no test summary line found in output";

        private static string FormatSigned(int d)
            => d > 0 ? $"+{d}" : d.ToString();
    }
}
