// File: BuildLockDiagnostics.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-68: a build that fails (or warns) only because files are locked is the environment, not the code.
//
// job-2152's code was clean — the agent proved 0/0 with an alternate OutputPath — but the harness's
// verification rebuild hit file locks held by the user's debug session (devenv, the app itself) and
// the job ended stopped_incomplete with "51 warnings" blamed on the agent. MSB3026 is MSBuild's
// copy-retry WARNING; MSB3027 / MSB3021 are the copy errors after the retries; MSB3061 is a delete
// that could not happen. The real lines (MSBuild 17 / SDK 10.0.302):
//
//   ...targets(5096,5): warning MSB3026: Could not copy "<src>" to "<dst>". Beginning retry 10 in 1000ms.
//     The process cannot access the file '<dst>' because it is being used by another process.
//     The file is locked by: "devenv.exe (116488), VLink.PDFSanitizerConfig.exe (83868)" [<proj>]
//   ...targets(5096,5): error MSB3027: Could not copy "<src>" to "<dst>". Exceeded retry count of 10.
//     Failed. The file is locked by: "devenv.exe (116488), VLink.PDFSanitizerConfig.exe (83868)" [<proj>]
//   ...targets(5096,5): error MSB3021: Unable to copy file "<src>" to "<dst>". The process cannot access
//     the file '<dst>' because it is being used by another process. [<proj>]
//   ...targets(5953,5): warning MSB3061: Unable to delete file "<path>". Access to the path '<path>' is
//     denied. The file is locked by: "VLink.PDFSanitizerConfig.exe (83868)" [<proj>]
//
// (each one line in the build output; shown wrapped here). Output that went through a console
// can arrive wrapped mid-diagnostic, so a diagnostic is read up to its closing " [<proj>]". MSBuild
// prints every diagnostic twice — inline, then again in the summary after "Build FAILED." /
// "Build succeeded." — so only the inline ones are counted; with no summary marker, identical lines
// are counted once.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>What a build's output says about file locks. Pure; never throws.</summary>
    public sealed class BuildLockReport
    {
        /// <summary>Lock diagnostics emitted as warnings (MSB3026, and any lock code emitted as a warning).</summary>
        public int LockWarningCount { get; init; }

        /// <summary>Lock diagnostics emitted as errors.</summary>
        public int LockErrorCount { get; init; }

        /// <summary>Errors that are NOT lock errors (compiler errors, other MSBuild errors).</summary>
        public int OtherErrorCount { get; init; }

        /// <summary>The locking processes, "name (pid)", distinct, in first-seen order.</summary>
        public IReadOnlyList<string> LockedBy { get; init; } = Array.Empty<string>();

        /// <summary>A few distinct lock diagnostic lines (project suffix removed, trimmed for quoting).</summary>
        public IReadOnlyList<string> LockLines { get; init; } = Array.Empty<string>();

        /// <summary>A few distinct non-lock error lines, same shape.</summary>
        public IReadOnlyList<string> OtherErrorLines { get; init; } = Array.Empty<string>();

        public static readonly BuildLockReport None = new BuildLockReport();
    }

    public static class BuildLockDiagnostics
    {
        /// <summary>MSBuild's file-lock diagnostic codes.</summary>
        public static readonly IReadOnlyCollection<string> LockCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MSB3021",   // unable to copy
            "MSB3026",   // could not copy, retrying (warning)
            "MSB3027",   // could not copy, exceeded retry count
            "MSB3061",   // unable to delete
        };

        private const int MaxQuoted = 5;
        private const int MaxQuotedLength = 240;
        private const int MaxContinuationLines = 6;

        // "...: warning MSB3026: ..." / "...: error CS0103: ..."
        private static readonly Regex Diagnostic = new Regex(
            @":\s+(warning|error)\s+([A-Za-z]+\d+)\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SummaryMarker = new Regex(
            @"^\s*Build (?:succeeded|FAILED)\.\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex LockedBy = new Regex(
            @"The file is locked by:\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ProjectSuffix = new Regex(@"\s+\[[^\[\]]+\]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>Whether a diagnostic line is a lock diagnostic.</summary>
        public static bool IsLockLine(string line)
        {
            Match m = Diagnostic.Match(line ?? "");
            return m.Success && LockCodes.Contains(m.Groups[2].Value);
        }

        /// <summary>Reads the FULL build output for lock diagnostics.</summary>
        public static BuildLockReport Analyze(string output)
        {
            if (string.IsNullOrEmpty(output)) return BuildLockReport.None;
            try
            {
                List<string> lines = LogicalLines(output);

                // Count the inline diagnostics only: everything after the last summary marker repeats them.
                int summaryAt = lines.FindLastIndex(l => SummaryMarker.IsMatch(l));
                IEnumerable<string> counted = summaryAt >= 0
                    ? lines.Take(summaryAt)
                    : lines.Distinct(StringComparer.Ordinal);

                int lockWarnings = 0, lockErrors = 0, otherErrors = 0;
                foreach (string line in counted)
                {
                    Match m = Diagnostic.Match(line);
                    if (!m.Success) continue;
                    bool isLock = LockCodes.Contains(m.Groups[2].Value);
                    bool isError = m.Groups[1].Value.Equals("error", StringComparison.OrdinalIgnoreCase);
                    if (isLock && isError) lockErrors++;
                    else if (isLock) lockWarnings++;
                    else if (isError) otherErrors++;
                }

                var lockedBy = new List<string>();
                var lockLines = new List<string>();
                var otherErrorLines = new List<string>();
                foreach (string line in lines)
                {
                    Match m = Diagnostic.Match(line);
                    if (!m.Success) continue;
                    bool isLock = LockCodes.Contains(m.Groups[2].Value);
                    if (isLock)
                    {
                        foreach (Match who in LockedBy.Matches(line))
                            foreach (string entry in who.Groups[1].Value.Split(','))
                            {
                                string name = Spaces.Replace(entry, " ").Trim();
                                if (name.Length > 0 && !lockedBy.Contains(name, StringComparer.OrdinalIgnoreCase))
                                    lockedBy.Add(name);
                            }
                        AddQuoted(lockLines, line);
                    }
                    else if (m.Groups[1].Value.Equals("error", StringComparison.OrdinalIgnoreCase))
                    {
                        AddQuoted(otherErrorLines, line);
                    }
                }

                return new BuildLockReport
                {
                    LockWarningCount = lockWarnings,
                    LockErrorCount = lockErrors,
                    OtherErrorCount = otherErrors,
                    LockedBy = lockedBy,
                    LockLines = lockLines,
                    OtherErrorLines = otherErrorLines,
                };
            }
            catch
            {
                return BuildLockReport.None;
            }
        }

        private static void AddQuoted(List<string> into, string line)
        {
            if (into.Count >= MaxQuoted) return;
            string quoted = ProjectSuffix.Replace(line.Trim(), "");
            if (quoted.Length > MaxQuotedLength) quoted = quoted.Substring(0, MaxQuotedLength - 1) + "…";
            if (!into.Contains(quoted, StringComparer.Ordinal)) into.Add(quoted);
        }

        // Lines, with a diagnostic that was wrapped across several physical lines joined back up to its
        // closing " [<project>]".
        private static List<string> LogicalLines(string output)
        {
            string[] raw = output.Replace("\r\n", "\n").Split('\n');
            var lines = new List<string>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                string line = raw[i];
                if (Diagnostic.IsMatch(line) && !line.TrimEnd().EndsWith("]", StringComparison.Ordinal))
                {
                    int k = 0;
                    while (k < MaxContinuationLines && i + 1 < raw.Length
                           && !Diagnostic.IsMatch(raw[i + 1]) && !SummaryMarker.IsMatch(raw[i + 1])
                           && raw[i + 1].Trim().Length > 0)
                    {
                        line = line.TrimEnd() + " " + raw[++i].Trim();
                        k++;
                        if (line.EndsWith("]", StringComparison.Ordinal)) break;
                    }
                }
                lines.Add(line);
            }
            return lines;
        }
    }
}
