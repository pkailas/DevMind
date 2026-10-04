// File: ToolCallNudges.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Four more harness nudges, read from each tool call of an iteration rather than from the
// iteration's flattened text (watchlist H-46, H-47, H-50, H-51b). HarnessNudges owns one
// instance and runs it from ObserveIteration; the framing, journal entry and transcript line
// are the same as for the H-25 / H-26 nudges. Each fires once per key per job turn.
//
//   H-46  build output as evidence: while the latest build/test run is failing, the agent
//         reads bin\ / obj\ or a .deps.json / .dll / .pdb / .runtimeconfig.json (job-1859
//         grepped deps.json and listed bin\ while the cause was a one-line product bug).
//   H-47  fetching framework source: 2 failed web_fetch / learn_fetch in a row (job-1858
//         chased ServerAddressesFeature through repeated 404s and never wrote its tests).
//   H-50  paging shell output: 3 shell calls in a row that differ only in a -Skip/-First/
//         head/tail offset (job-1954 read a file 3 lines at a time through Select-Object -Skip).
//   H-51b same run, same result: 3 runs in a row of the same tests (project + --filter) with
//         the same outcome, or of the same other shell command with the same output. The key
//         is semantic: job-1866 ran its test 11 times before an override steer and 10 after,
//         and no two commands were byte-identical (redirect targets isoA.txt ... isoK.txt). Skipped for a run whose
//         failing tests are named — that is H-58's note (RepeatedTestFailureGuard), and one
//         nudge per loop is enough; likewise a repeated compile error is H-25's. A file
//         change starts every streak over: re-running after an edit is the normal loop.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>One tool call of an iteration, as the nudges see it.</summary>
    public sealed class ToolCallEvidence
    {
        /// <summary>The tool name ("read_file", "run_shell", ...).</summary>
        public string Name { get; init; } = "";

        /// <summary>The call's arguments (string values, as the mapper sees them).</summary>
        public IReadOnlyDictionary<string, string> Arguments { get; init; } = new Dictionary<string, string>();

        /// <summary>The result text the model was handed for this call.</summary>
        public string Result { get; init; } = "";

        /// <summary>Exit code of a run_shell / run_build / run_tests call; null otherwise.</summary>
        public int? ExitCode { get; init; }

        public string Arg(string name)
            => Arguments != null && Arguments.TryGetValue(name, out string v) ? v ?? "" : "";
    }

    /// <summary>The H-46 / H-47 / H-50 / H-51b guards. Not thread-safe: worker thread only.</summary>
    public sealed class ToolCallNudges
    {
        public const int FailedFetchRepeats = 2;
        public const int PagingRepeats = 3;
        public const int SameResultRepeats = 3;

        public const string BuildOutputMessage =
            "Build output is never the evidence. Print the failing assertion and the actual value, and fix what that shows.";

        public const string StopFetchingMessage =
            "Stop fetching. Write the code (or your own class implementing the interface) and build — the compiler is the reference.";

        public const string PagingMessage =
            "Use read_file with start_line/end_line, or write the output to a file once and read that.";

        public const string SameResultMessage =
            "Same tests, same result, three times. State in one sentence what question you are still answering " +
            "and the one different command that would answer it — or move on.";

        private static readonly HashSet<string> ReadTools = new HashSet<string>(StringComparer.Ordinal)
            { "read_file", "grep_file", "find_in_files", "list_files" };

        private static readonly HashSet<string> WriteTools = new HashSet<string>(StringComparer.Ordinal)
            { "create_file", "append_file", "patch_file", "delete_file", "rename_file" };

        // A bin\ or obj\ path segment, or a build artefact by extension.
        private static readonly Regex BuildOutputPath = new Regex(
            @"(?:^|[\\/""'\s*])(?:bin|obj)[\\/]|\.(?:deps\.json|runtimeconfig\.json|dll|pdb)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Shell verbs that read a file or list a folder. Not preceded by "-" ("-Type" is a parameter).
        private static readonly Regex ShellReadVerb = new Regex(
            @"(?<![-\w])(?:Get-Content|gc|cat|type|more|Select-String|sls|findstr|grep|Get-ChildItem|gci|ls|dir|Get-Item|Format-Hex)(?![-\w])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Paging arguments, whose numbers are what changes from one call to the next.
        private static readonly Regex PsPaging = new Regex(
            @"(-(?:Skip|First|Last|Head|Tail|Index|TotalCount)\s*[:\s]\s*)\d+(?:\s*(?:,|\.\.)\s*\d+)*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex UnixPagingN = new Regex(
            @"\b(head|tail)(\s+-n)\s*\+?\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex UnixPagingDash = new Regex(
            @"\b(head|tail)\s+-\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Redirections and capture-to-file pipes, with their targets.
        private static readonly Regex Redirect = new Regex(
            @"(?:\d|\*)?>>?\s*(?:&\d|""[^""]*""|'[^']*'|[^\s;|]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CapturePipe = new Regex(
            @"\|\s*(?:Out-File|Tee-Object|Set-Content|Add-Content)\b[^;|]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex TempPath = new Regex(
            @"""[^""]*(?:\\Temp\\|\$env:TEMP|%TEMP%|/tmp/)[^""]*""|'[^']*(?:\\Temp\\|\$env:TEMP|%TEMP%|/tmp/)[^']*'|[^\s;|""']*(?:\\Temp\\|\$env:TEMP|%TEMP%|/tmp/)[^\s;|""']*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Digits = new Regex(@"\d+", RegexOptions.Compiled);
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        // One redirect token of a tokenized command; group 1 is a target attached to it.
        private static readonly Regex RedirectToken = new Regex(
            @"^(?:\d|\*)?>>?(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex DotnetTest = new Regex(
            @"\bdotnet(?:\.exe)?\s+test\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // A VSTest per-project summary anywhere in a line — the model often prints its own
        // excerpt of a redirected run ("L57: Passed!  - Failed: 0, Passed: 3, ...").
        private static readonly Regex SummaryCounts = new Regex(
            @"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // `dotnet test` options that take a value (so the value is not read as the target).
        private static readonly HashSet<string> ValueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-v", "--verbosity", "-l", "--logger", "-c", "--configuration", "-f", "--framework", "-s", "--settings",
            "-e", "--environment", "-r", "--results-directory", "-a", "--test-adapter-path", "--collect", "-o",
            "--output", "--runtime", "--arch", "--os", "-d", "--diag", "--blame-hang-timeout", "--blame-hang-dump-type",
            "--blame-crash-dump-type", "--property", "-p",
        };

        private bool? _buildOrTestFailing;
        private int _failedFetches;
        private string _pagingKey;
        private int _pagingCount;
        private readonly HashSet<string> _pagingCommands = new HashSet<string>(StringComparer.Ordinal);
        private string _repeatKey;
        private string _repeatOutcome;
        private int _repeatCount;
        private readonly HashSet<string> _fired = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Forget everything — a new job turn.</summary>
        public void Reset()
        {
            _buildOrTestFailing = null;
            _failedFetches = 0;
            ResetPaging();
            ResetRepeat();
            _fired.Clear();
        }

        /// <summary>Records the iteration's tool calls in order; returns the nudges that fired.</summary>
        public List<string> Observe(IReadOnlyList<ToolCallEvidence> calls)
        {
            var nudges = new List<string>();
            if (calls == null) return nudges;
            foreach (ToolCallEvidence call in calls)
                if (call != null) ObserveCall(call, nudges);
            return nudges;
        }

        private void ObserveCall(ToolCallEvidence call, List<string> nudges)
        {
            string name = call.Name ?? "";
            bool isShell = name == "run_shell";
            string command = isShell ? call.Arg("command") : "";

            // The latest build/test verdict (H-46's gate).
            bool isTestRun = name == "run_tests" || (isShell && ConvergenceTracker.IsTestCommand(command));
            bool isBuildRun = name == "run_build" || (isShell && !isTestRun && ConvergenceTracker.IsBuildCommand(command));
            if (isTestRun || isBuildRun)
            {
                bool? failing = isTestRun ? TestRunFailing(call) : call.ExitCode.HasValue ? call.ExitCode != 0 : (bool?)null;
                if (failing.HasValue) _buildOrTestFailing = failing;
            }

            // H-46
            if (_buildOrTestFailing == true && ReadsBuildOutput(call, isShell && !isTestRun && !isBuildRun, command))
                Fire("H-46", BuildOutputMessage, nudges);

            // H-47
            if (name is "web_fetch" or "learn_fetch")
            {
                if (IsFailedFetch(call.Result))
                {
                    if (++_failedFetches >= FailedFetchRepeats) Fire("H-47", StopFetchingMessage, nudges);
                }
                else _failedFetches = 0;
            }

            // H-50: consecutive shell calls only — any other call breaks the run.
            if (isShell) ObservePaging(command, nudges);
            else ResetPaging();

            // H-51b: a file change means re-running is the normal loop again.
            if (WriteTools.Contains(name)) ResetRepeat();
            else if (isTestRun) ObserveRepeat("test:" + TestKey(call, command), TestOutcome(call.Result, out bool namedFailure), namedFailure, nudges);
            else if (isShell)
                // A repeated compile error is H-25's nudge (RepeatedCompileErrorMessage), and a
                // paging command is H-50's.
                ObserveRepeat("sh:" + NormalizeCommand(command), "out:" + NormalizeOutput(call.Result),
                    HarnessNudges.CompileErrorKeys(call.Result).Any() || PagingKey(command) != null, nudges);
        }

        private void Fire(string key, string message, List<string> nudges)
        {
            if (_fired.Add(key)) nudges.Add(message);
        }

        // ── H-46 ─────────────────────────────────────────────────────────────────

        private static bool ReadsBuildOutput(ToolCallEvidence call, bool plainShell, string command)
        {
            switch (call.Name)
            {
                case "read_file":
                case "grep_file":
                    return BuildOutputPath.IsMatch(call.Arg("filename"));
                case "find_in_files":
                case "list_files":
                    return BuildOutputPath.IsMatch(call.Arg("glob"));
                default:
                    return plainShell && ShellReadVerb.IsMatch(command) && BuildOutputPath.IsMatch(command);
            }
        }

        private static bool? TestRunFailing(ToolCallEvidence call)
        {
            TestRunOutcome run = TestRunOutcome.Parse(call.Result);
            if (run != null) return !run.Passed;
            var counts = SumCounts(call.Result);
            if (counts != null) return counts.Value.failed > 0;
            return call.ExitCode.HasValue ? call.ExitCode != 0 : (bool?)null;
        }

        // ── H-47 ─────────────────────────────────────────────────────────────────

        /// <summary>A fetch that brought back nothing usable: an error or empty-content marker
        /// from the fetch tool, nothing at all, or a short page that is a 404 / not-found.</summary>
        public static bool IsFailedFetch(string result)
        {
            if (string.IsNullOrWhiteSpace(result)) return true;
            string r = result.TrimStart();
            if (r.StartsWith("[web_fetch error]", StringComparison.OrdinalIgnoreCase)
                || r.StartsWith("[learn_fetch error]", StringComparison.OrdinalIgnoreCase)
                || r.StartsWith("[web_fetch] No content", StringComparison.OrdinalIgnoreCase)
                || r.StartsWith("[learn_fetch] No content", StringComparison.OrdinalIgnoreCase)
                || r.StartsWith("[Fetched content not available]", StringComparison.Ordinal))
                return true;
            // A real page can mention "not found"; a 404 page is short.
            return r.Length < 300
                && (r.Contains("404") || r.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ── H-50 ─────────────────────────────────────────────────────────────────

        /// <summary>The command with its paging numbers replaced by "#", or null when it pages nothing.</summary>
        public static string PagingKey(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string s = PsPaging.Replace(command, "$1#");
            s = UnixPagingN.Replace(s, "$1$2 #");
            s = UnixPagingDash.Replace(s, "$1 -#");
            return s == command ? null : Spaces.Replace(s.Trim(), " ");
        }

        private void ObservePaging(string command, List<string> nudges)
        {
            string key = PagingKey(command);
            if (key == null) { ResetPaging(); return; }
            if (key != _pagingKey)
            {
                ResetPaging();
                _pagingKey = key;
            }
            _pagingCount++;
            _pagingCommands.Add(command.Trim());
            // Paging means the offsets moved; the same command three times is H-51b's case.
            if (_pagingCount >= PagingRepeats && _pagingCommands.Count >= 2)
                Fire("H-50:" + key, PagingMessage, nudges);
        }

        private void ResetPaging()
        {
            _pagingKey = null;
            _pagingCount = 0;
            _pagingCommands.Clear();
        }

        // ── H-51b ────────────────────────────────────────────────────────────────

        // coveredElsewhere: the repeated result is another guard's — named failing tests (H-58's
        // note) or a compile error (H-25's nudge). One nudge per loop.
        private void ObserveRepeat(string key, string outcome, bool coveredElsewhere, List<string> nudges)
        {
            if (key == _repeatKey && outcome == _repeatOutcome) _repeatCount++;
            else
            {
                _repeatKey = key;
                _repeatOutcome = outcome;
                _repeatCount = 1;
            }
            if (_repeatCount >= SameResultRepeats && !coveredElsewhere)
                Fire("H-51b:" + key, SameResultMessage, nudges);
        }

        private void ResetRepeat()
        {
            _repeatKey = null;
            _repeatOutcome = null;
            _repeatCount = 0;
        }

        /// <summary>
        /// What a test run ran: the project/solution and the --filter value, normalized. Redirects,
        /// loggers, verbosity, environment settings and everything after the `dotnet test`
        /// statement are not part of it. For run_tests, its project and filter arguments.
        /// </summary>
        public static string TestKey(ToolCallEvidence call, string command)
        {
            if (call.Name == "run_tests")
                return Key(call.Arg("project"), call.Arg("filter"));

            Match m = DotnetTest.Match(command ?? "");
            if (!m.Success) return Key("", "");   // a test runner other than dotnet test
            List<string> tokens = Tokenize(command.Substring(m.Index + m.Length));

            string target = "", filter = "";
            for (int i = 0; i < tokens.Count; i++)
            {
                string t = tokens[i];
                Match redirect = RedirectToken.Match(t);
                if (redirect.Success)
                {
                    if (redirect.Groups[1].Length == 0) i++;   // "> file": the target is the next token
                    continue;                                  // "2>&1", ">file": self-contained
                }
                if (t.StartsWith("--filter", StringComparison.OrdinalIgnoreCase))
                {
                    int sep = t.IndexOfAny(new[] { ':', '=' });
                    filter = sep > 0 ? t.Substring(sep + 1) : (i + 1 < tokens.Count ? tokens[++i] : "");
                    continue;
                }
                if (t.StartsWith("-", StringComparison.Ordinal))
                {
                    if (ValueOptions.Contains(t)) i++;
                    continue;
                }
                if (target.Length == 0) target = t;
            }
            return Key(target, filter);
        }

        private static string Key(string target, string filter)
            => Unquote(target).ToLowerInvariant() + " | " + Unquote(filter).Trim().ToLowerInvariant();

        private static string Unquote(string s)
        {
            s = (s ?? "").Trim();
            return s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0] ? s.Substring(1, s.Length - 2) : s;
        }

        // Arguments of one statement: quoted strings stay whole; an unquoted ; | & or newline ends
        // the statement (a "2>&1" is kept as one redirect token).
        private static List<string> Tokenize(string args)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            char quote = '\0';
            for (int i = 0; i < args.Length; i++)
            {
                char c = args[i];
                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; sb.Append(c); continue; }
                if (c == '&' && sb.Length > 0 && sb[sb.Length - 1] == '>') { sb.Append(c); continue; }   // 2>&1
                if (c == ';' || c == '|' || c == '&' || c == '\n' || c == '\r') break;
                if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        /// <summary>
        /// A test run's outcome: the summed passed/failed/skipped/total counts and the failing test
        /// names; failing that, the normalized output. <paramref name="namedFailure"/> is true when
        /// the run failed and names its failing tests — H-58's territory.
        /// </summary>
        public static string TestOutcome(string output, out bool namedFailure)
        {
            TestRunOutcome run = TestRunOutcome.Parse(output);
            namedFailure = run != null && !run.Passed && run.FailedTests.Count > 0;
            var counts = SumCounts(output);
            string names = run != null ? string.Join(",", run.FailedTests) : "";
            if (counts != null)
                return $"counts {counts.Value.failed}/{counts.Value.passed}/{counts.Value.skipped}/{counts.Value.total} {names}";
            if (run != null) return (run.Passed ? "passed " : "failed ") + names;
            return "out:" + NormalizeOutput(output);
        }

        private static (int failed, int passed, int skipped, int total)? SumCounts(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            MatchCollection ms = SummaryCounts.Matches(output);
            if (ms.Count == 0) return null;
            int f = 0, p = 0, s = 0, t = 0;
            foreach (Match m in ms)
            {
                f += int.Parse(m.Groups[1].Value);
                p += int.Parse(m.Groups[2].Value);
                s += int.Parse(m.Groups[3].Value);
                t += int.Parse(m.Groups[4].Value);
            }
            return (f, p, s, t);
        }

        /// <summary>A shell command with its redirect targets, capture pipes, temp-file paths and
        /// digits taken out — two runs that differ only in where they wrote the output compare equal.</summary>
        public static string NormalizeCommand(string command)
        {
            string s = command ?? "";
            s = CapturePipe.Replace(s, "| >");
            s = Redirect.Replace(s, ">");
            s = TempPath.Replace(s, "<tmp>");
            s = Digits.Replace(s, "#");
            return Spaces.Replace(s.Trim(), " ").ToLowerInvariant();
        }

        /// <summary>
        /// Output with what changes between two identical runs taken out — durations, clock times,
        /// dates, GUIDs, temp paths — and spacing normalized. Other numbers stay: a count that
        /// changed is a different result.
        /// </summary>
        public static string NormalizeOutput(string output)
        {
            string s = output ?? "";
            s = VolatileNumber.Replace(s, "#");
            s = TempPath.Replace(s, "<tmp>");
            return Spaces.Replace(s.Trim(), " ");
        }

        // Durations ("4 s", "312 ms", "00:00:02.13"), clock times, ISO dates and GUIDs.
        private static readonly Regex VolatileNumber = new Regex(
            @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b" +
            @"|\b\d{4}-\d{2}-\d{2}(?:[T ]\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?)?" +
            @"|\b\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?\b" +
            @"|\b\d+(?:\.\d+)?\s*(?:ms|s|sec|secs|seconds|m|min|minutes)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
