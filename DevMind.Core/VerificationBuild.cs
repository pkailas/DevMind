// File: VerificationBuild.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The command the HARNESS runs to verify a delegated job's build (H-32 / H-13), and the warning
// count read from its output.
//
// An incremental `dotnet build` does not re-emit warnings for projects that are already up to
// date, so its "N Warning(s)" line under-reports — a scratch .slnx with one CS0168 printed
// "1 Warning(s)" on its first build and "0 Warning(s)" on the next. The standard is 0 errors AND
// 0 warnings, so the driver re-ran -t:Rebuild by hand after every job, and agents skip -t:Rebuild
// even when the brief asks for it (job-1712, job-1714). The harness now runs the rebuild itself.
//
// The rewrite, for a resolved build command:
//   - a single `dotnet build ...` statement with no target of its own → append -t:Rebuild
//     (the same command the driver's done-check runs; --no-incremental works on .slnx too);
//   - already -t:Rebuild (in any switch spelling, alone or in a target list) or
//     --no-incremental → run unchanged, and it IS a full rebuild;
//   - any other -t: target, a composite command (; && | newline), a non-dotnet command, a
//     DEVMIND_BUILD_COMMAND override, or DEVMIND_VERIFY_REBUILD=0 → run unchanged, and the
//     warning count is not verified.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DevMind
{
    public static class VerificationBuild
    {
        /// <summary>Set to 0 / false / off to verify with the plain (incremental) build command.</summary>
        public const string RebuildEnvVar = "DEVMIND_VERIFY_REBUILD";

        /// <summary>What the harness runs, and whether it is a full rebuild (so its warning count is real).</summary>
        public readonly struct Plan
        {
            public string Command { get; }
            public bool FullRebuild { get; }
            public Plan(string command, bool fullRebuild) { Command = command; FullRebuild = fullRebuild; }
        }

        /// <summary>
        /// The verification plan for <paramref name="resolvedCommand"/>, reading
        /// DEVMIND_BUILD_COMMAND (an override is run unchanged) and DEVMIND_VERIFY_REBUILD.
        /// </summary>
        public static Plan For(string resolvedCommand)
            => For(resolvedCommand,
                   fromOverride: !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEVMIND_BUILD_COMMAND")),
                   rebuildEnabled: RebuildEnabled());

        /// <summary>Pure form of <see cref="For(string)"/>.</summary>
        public static Plan For(string resolvedCommand, bool fromOverride, bool rebuildEnabled)
        {
            string cmd = resolvedCommand?.Trim() ?? "";
            if (!rebuildEnabled || fromOverride || !IsSingleDotnetBuild(cmd))
                return new Plan(cmd, false);

            string[] words = cmd.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Contains("--", StringComparer.Ordinal))
                return new Plan(cmd, false);                  // pass-through args: leave the line alone
            if (words.Any(w => w.Equals("--no-incremental", StringComparison.OrdinalIgnoreCase)))
                return new Plan(cmd, true);

            string[] targets = words.Select(w => TargetSwitch.Match(w))
                                    .Where(m => m.Success)
                                    .SelectMany(m => m.Groups[1].Value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                                    .ToArray();
            if (targets.Length > 0)
                return new Plan(cmd, targets.Any(t => t.Trim().Equals("Rebuild", StringComparison.OrdinalIgnoreCase)));

            return new Plan(cmd + " -t:Rebuild", true);
        }

        /// <summary>DEVMIND_VERIFY_REBUILD: on unless set to 0 / false / off / no.</summary>
        public static bool RebuildEnabled()
        {
            string v = Environment.GetEnvironmentVariable(RebuildEnvVar)?.Trim();
            return !(v == "0"
                || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "no", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The build's warning count: the last "N Warning(s)" summary line (classic console
        /// logger — what a redirected build prints), else the terminal logger's "... with N
        /// warning(s)". Null when the output has neither.
        /// </summary>
        public static int? ParseWarningCount(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            MatchCollection summary = SummaryWarnings.Matches(output);
            if (summary.Count > 0) return int.Parse(summary[summary.Count - 1].Groups[1].Value);
            MatchCollection tl = TerminalLoggerWarnings.Matches(output);
            if (tl.Count > 0) return int.Parse(tl[tl.Count - 1].Groups[1].Value);
            return null;
        }

        /// <summary>
        /// Up to <paramref name="max"/> distinct compiler/MSBuild warning lines
        /// ("file(line,col): warning CS0168: ..."), in first-seen order. MSBuild prints each warning
        /// twice (inline, then in the summary), so duplicates are dropped; the trailing
        /// " [project]" suffix is removed and a long line is cut to <paramref name="maxLineLength"/>.
        /// </summary>
        public static IReadOnlyList<string> ExtractWarningLines(string output, int max = 5, int maxLineLength = 240)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(output) || max <= 0) return lines;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (!WarningLine.IsMatch(line)) continue;
                line = ProjectSuffix.Replace(line, "");
                if (line.Length > maxLineLength) line = line.Substring(0, maxLineLength - 1) + "…";
                if (!seen.Add(line)) continue;
                lines.Add(line);
                if (lines.Count >= max) break;
            }
            return lines;
        }

        // "...: warning CS0168: ..." / "...: warning MSB3277: ..." / "...: warning NU1903: ..."
        private static readonly Regex WarningLine = new Regex(
            @":\s+warning\s+[A-Za-z]+\d+\s*:", RegexOptions.CultureInvariant);

        // The " [C:\path\Proj.csproj]" MSBuild appends to every diagnostic line.
        private static readonly Regex ProjectSuffix = new Regex(@"\s+\[[^\[\]]+\]$", RegexOptions.CultureInvariant);

        // -t:X, /t:X, -target:X, /target:X, --target:X (X may be a ;-list).
        private static readonly Regex TargetSwitch = new Regex(
            @"^(?:--?|/)(?:t|target):(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex SummaryWarnings = new Regex(
            @"^\s*(\d+) Warning\(s\)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TerminalLoggerWarnings = new Regex(
            @"\bwith (?:\d+ error\(s\) and )?(\d+) warning\(s\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // "dotnet build ..." (dotnet / dotnet.exe, optionally quoted or path-qualified) as the
        // only statement.
        private static bool IsSingleDotnetBuild(string cmd)
        {
            if (cmd.Length == 0 || cmd.IndexOfAny(new[] { '|', '\n', '\r' }) >= 0 || cmd.Contains("&&")) return false;
            string[] words = cmd.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            // ';' separates statements, except inside a switch value: -t:Clean;Rebuild, -p:X=A;B.
            if (words.Any(w => w.Contains(';') && !(w.StartsWith("-", StringComparison.Ordinal) || w.StartsWith("/", StringComparison.Ordinal))))
                return false;
            int i = words[0] == "&" ? 1 : 0;
            if (words.Length < i + 2) return false;
            string exe = Path.GetFileName(words[i].TrimStart('&').Trim('"', '\''));
            if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe.Substring(0, exe.Length - 4);
            return exe.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                && words[i + 1].Equals("build", StringComparison.OrdinalIgnoreCase);
        }
    }
}
