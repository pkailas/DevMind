// File: FileReadTools.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The ONE implementation of the read-side file tools — read_file (with its 'git log' /
// 'git diff' branches), grep_file, find_in_files, list_files and diff_file. They used to
// exist three times (TuiAgenticHost, BufferedAgenticHost, McpServer DevMindTools) and had
// drifted; each of those is now a thin adapter over this class.
//
// What still differs between surfaces is explicit and small (FileReadPolicy):
//   * vocabulary — the agent surface names its tools READ/GREP/FIND/DIFF in messages and
//     headers, the MCP surface read_file/grep_file/find_in_files/diff_file/list_files;
//   * root — only the MCP tools take an explicit root (a parameter, not policy).
// Host-side bookkeeping (write-guard sets, stale-read nudges, the TUI's syntax-highlighted
// listing and painted diff, the transcript itself) stays in the hosts: every result carries
// the transcript line an agent host prints, and the facts a host needs (resolved path,
// outline or not, the normalized diff sides).
//
// Matching is SearchPattern.BuildMatcher (substring, '|' = OR) — unchanged.
// Synchronous bodies (the async-over-sync of the host interface is kept by the adapters);
// only the git branch is async, as it always was.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// The intentional, surface-specific differences of the read-side tools — nothing else.
    /// </summary>
    public sealed class FileReadPolicy
    {
        /// <summary>Agent tool surface (TUI, CLI, headless jobs): READ / GREP / FIND / DIFF.</summary>
        public static readonly FileReadPolicy Agent = new FileReadPolicy("READ", "GREP", "FIND", "DIFF", "LIST");

        /// <summary>MCP server surface: the tool names the external driver called.</summary>
        public static readonly FileReadPolicy Mcp = new FileReadPolicy("read_file", "grep_file", "find_in_files", "diff_file", "list_files");

        public string ReadLabel { get; }
        public string GrepLabel { get; }
        public string FindLabel { get; }
        public string DiffLabel { get; }
        public string ListLabel { get; }

        private FileReadPolicy(string read, string grep, string find, string diff, string list)
        {
            ReadLabel = read; GrepLabel = grep; FindLabel = find; DiffLabel = diff; ListLabel = list;
        }
    }

    /// <summary>Outcome of one read-side tool call.</summary>
    public sealed class FileReadResult
    {
        /// <summary>What the tool returns to its caller (model or MCP driver).</summary>
        public string Text { get; init; }

        /// <summary>The file the call resolved to (read/grep/diff), or null.</summary>
        public string ResolvedPath { get; init; }

        /// <summary>True when a file was actually read (read_file full or range).</summary>
        public bool FileLoaded { get; init; }

        /// <summary>read_file: the outline was returned instead of the content.</summary>
        public bool WasOutline { get; init; }

        /// <summary>read_file: the lines that were returned (range or full, not outline) and
        /// their count — the TUI lists them syntax-highlighted.</summary>
        public string ListingContent { get; init; }
        public int ListingLineCount { get; init; }

        /// <summary>grep_file / find_in_files: number of matching lines found (before the cap).</summary>
        public int MatchCount { get; init; }

        /// <summary>diff_file: both sides, CRLF-normalized, when a diff was produced.</summary>
        public string DiffOld { get; init; }
        public string DiffNew { get; init; }

        /// <summary>The transcript line an agent host prints (null = none).</summary>
        public string LogLine { get; init; }
        public OutputColor LogColor { get; init; } = OutputColor.Dim;
    }

    public sealed class FileReadTools
    {
        public const int GrepMaxMatches = 50;
        public const int FindMaxMatches = 100;
        public const int ListCap = 200;
        public const int GitLogDefault = 10;
        public const int GitLogMax = 50;
        public const int GitMaxOutputLines = 500;

        private readonly FileReadPolicy _policy;
        private readonly string _workingDirectory;
        private readonly FileContentCache _cache;
        private readonly ISet<string> _filesRead;
        private readonly FileSnapshotStore _snapshots;
        private readonly ShellRunner _shell;

        /// <param name="policy">Surface vocabulary (<see cref="FileReadPolicy.Agent"/> / <see cref="FileReadPolicy.Mcp"/>).</param>
        /// <param name="workingDirectory">Resolution root and default search root.</param>
        /// <param name="cache">The surface's session file cache (keyed by full path).</param>
        /// <param name="filesRead">Full paths read in full this session — a re-read returns the outline.</param>
        /// <param name="snapshots">Session diff baselines; read_file captures one.</param>
        /// <param name="shell">Runs the git branch of read_file (may be null when unused).</param>
        public FileReadTools(FileReadPolicy policy, string workingDirectory, FileContentCache cache,
            ISet<string> filesRead, FileSnapshotStore snapshots, ShellRunner shell)
        {
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _workingDirectory = workingDirectory;
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _filesRead = filesRead ?? throw new ArgumentNullException(nameof(filesRead));
            _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
            _shell = shell;
        }

        // ── Shared helpers ───────────────────────────────────────────────────

        /// <summary>Path.GetFileName that never throws (a hallucinated "C:\" or bad chars).</summary>
        public static string SafeGetFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path ?? "";
            try { return Path.GetFileName(path.Replace('\\', '/')); }
            catch { return path; }
        }

        /// <summary>Canonical cache/read-set key: the FULL path, never the bare name (same-named
        /// files must not poison each other — the job-8 postmortem).</summary>
        public static string FileKey(string fullPath)
        {
            try { return Path.GetFullPath(fullPath); }
            catch { return fullPath; }
        }

        /// <summary>The one text reader for every read-side tool (BOM-aware, UTF-8 default).</summary>
        public static string ReadText(string fullPath) => PatchEngine.ReadFilePreservingEncoding(fullPath).content;

        public FileResolution Resolve(string filename)
        {
            string fileNameOnly = SafeGetFileName(filename);
            return FilePathResolver.Resolve(fileNameOnly, (filename ?? "").Replace('\\', '/'), _workingDirectory);
        }

        /// <summary>Loads a file into the cache unless a fresh entry is there. Throws on read errors.</summary>
        private string EnsureCached(string fullPath)
        {
            string key = FileKey(fullPath);
            _cache.InvalidateIfStale(key, fullPath); // out-of-band writes
            if (!_cache.Contains(key))
                _cache.Store(key, ReadText(fullPath));
            return key;
        }

        /// <summary>Splits "dir/sub/*.cs" into an existing root + file pattern (a missing
        /// directory part falls back to the search root with the file pattern alone).</summary>
        private static (string root, string pattern) SplitGlob(string searchDir, string glob)
        {
            string normalized = (glob ?? "").Replace('\\', '/');
            int lastSlash = normalized.LastIndexOf('/');
            if (lastSlash < 0) return (searchDir, normalized);
            string dirPart = normalized.Substring(0, lastSlash);
            string pattern = normalized.Substring(lastSlash + 1);
            string candidate = Path.Combine(searchDir, dirPart.Replace('/', Path.DirectorySeparatorChar));
            return (Directory.Exists(candidate) ? candidate : searchDir, pattern);
        }

        /// <summary>
        /// H-60: a glob whose DIRECTORY part has a wildcard ("**/X/*.cs", "a/**/X/*.cs",
        /// "src/*/Tests/*.cs"). <see cref="SplitGlob"/> treats the directory part as a literal
        /// path; "**/ConfigPages" never exists, so it fell back to the search root and listed
        /// every "*.cs" in the tree (job-2112: 200+ results for a pattern with 24 matches).
        /// Returns null for any other glob — those keep the <see cref="SplitGlob"/> behaviour.
        /// Otherwise: the root is the literal directory prefix before the first wildcard
        /// segment (no fallback — a missing prefix matches nothing), the enumeration is
        /// recursive with the last segment as the file pattern, and <c>Matches</c> filters
        /// each file's path relative to that root: "**" is zero or more directories, "*" and
        /// "?" stay within one segment, case-insensitive.
        /// </summary>
        internal static (string root, string filePattern, Regex matches)? WildcardDirGlob(string searchDir, string glob)
        {
            string normalized = (glob ?? "").Replace('\\', '/').Trim();
            while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized.Substring(2);
            string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) return null;
            int firstWild = Array.FindIndex(segments, s => s.IndexOfAny(new[] { '*', '?' }) >= 0);
            if (firstWild < 0 || firstWild == segments.Length - 1) return null; // wildcard only in the file name

            // The literal prefix as written (keeps a leading "/" or "C:/"), then resolved against
            // the search root — Path.Combine returns an absolute prefix unchanged.
            string root = searchDir;
            if (firstWild > 0)
            {
                string prefix = normalized.Substring(0, normalized.IndexOf(segments[firstWild], StringComparison.Ordinal)).TrimEnd('/');
                if (prefix.EndsWith(":", StringComparison.Ordinal)) prefix += "/";
                root = Path.Combine(searchDir, prefix.Replace('/', Path.DirectorySeparatorChar));
            }

            var rx = new StringBuilder("^");
            for (int i = firstWild; i < segments.Length; i++)
            {
                bool last = i == segments.Length - 1;
                string seg = segments[i];
                if (seg == "**")
                {
                    rx.Append(last ? ".*" : "(?:[^/]+/)*");
                    continue;
                }
                foreach (char c in seg)
                    rx.Append(c switch { '*' => "[^/]*", '?' => "[^/]", _ => Regex.Escape(c.ToString()) });
                if (!last) rx.Append('/');
            }
            rx.Append('$');

            string filePattern = segments[^1] == "**" ? "*" : segments[^1];
            return (root, filePattern, new Regex(rx.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        /// <summary>Files under <paramref name="root"/> whose root-relative path matches the
        /// <see cref="WildcardDirGlob"/> filter. Noise directories are pruned by the walk.</summary>
        private static IEnumerable<string> EnumerateWildcardDirGlob((string root, string filePattern, Regex matches) g)
        {
            if (!Directory.Exists(g.root)) return Enumerable.Empty<string>();
            string rootFull = Path.GetFullPath(g.root);
            return ContextEngine.SafeEnumerateFilesGlob(rootFull, g.filePattern)
                .Where(f => g.matches.IsMatch(Path.GetRelativePath(rootFull, f).Replace('\\', '/')));
        }

        /// <summary>The search root: an explicit absolute <paramref name="root"/> (MCP) or the
        /// working directory. Returns an error text instead when the root is unusable.</summary>
        private string ResolveSearchRoot(string root, string label, out string error)
        {
            error = null;
            if (!string.IsNullOrWhiteSpace(root))
            {
                if (!Path.IsPathRooted(root))
                {
                    error = $"{label}: root must be an absolute path — got '{root}'.";
                    return null;
                }
                if (!Directory.Exists(root))
                {
                    error = $"{label}: root does not exist — {root}";
                    return null;
                }
                return Path.GetFullPath(root);
            }
            if (string.IsNullOrEmpty(_workingDirectory))
            {
                error = "[ERROR: working directory not set]";
                return null;
            }
            return _workingDirectory;
        }

        // ── read_file ────────────────────────────────────────────────────────

        /// <summary>
        /// read_file for a path. <paramref name="startLine"/> &gt; 0 reads a line range (to
        /// <paramref name="endLine"/>, or end of file when null); otherwise the full file, or
        /// its outline from <see cref="ContextEngine.ReadOutlineThresholdLines"/> lines or on a
        /// re-read, unless <paramref name="forceFull"/>. Captures the diff baseline. Read errors
        /// throw — each surface's dispatcher reports them in its own envelope.
        /// </summary>
        public FileReadResult Read(string filename, int? startLine, int? endLine, bool forceFull)
        {
            FileResolution res = Resolve(filename);
            string fullPath = res.Path;
            if (fullPath == null || !File.Exists(fullPath))
            {
                return new FileReadResult
                {
                    Text = FilePathResolver.BuildFileNotFoundMessage(_policy.ReadLabel, filename, res),
                    LogLine = $"[READ] File not found: {filename}",
                    LogColor = OutputColor.Warning,
                };
            }

            // The header names the file actually read (its on-disk name), not the request's spelling.
            string name = SafeGetFileName(fullPath);
            _snapshots.Capture(FileKey(fullPath));

            if (startLine.HasValue && startLine.Value > 0)
            {
                string key = EnsureCached(fullPath);
                int totalLines = _cache.GetLineCount(key);
                int rangeStart = startLine.Value;
                int rangeEnd = endLine.HasValue && endLine.Value > 0 ? endLine.Value : totalLines;
                if (rangeStart > rangeEnd) { int t = rangeStart; rangeStart = rangeEnd; rangeEnd = t; }
                int clampedStart = Math.Max(1, rangeStart);
                int clampedEnd = Math.Min(rangeEnd, totalLines);

                string rangeContent = _cache.GetLineRange(key, clampedStart, clampedEnd);
                if (rangeContent == null)
                {
                    string oob = $"[{_policy.ReadLabel}] Range {rangeStart}-{rangeEnd} out of bounds for {name} ({totalLines} lines)";
                    return new FileReadResult
                    {
                        Text = oob,
                        ResolvedPath = fullPath,
                        FileLoaded = true, // it was read (into the cache), just not this range
                        LogLine = $"[READ] Range {rangeStart}-{rangeEnd} out of bounds for {name} ({totalLines} lines)",
                        LogColor = OutputColor.Error,
                    };
                }

                var rawLines = rangeContent.Split('\n');
                var numbered = new StringBuilder();
                for (int i = 0; i < rawLines.Length; i++)
                    numbered.AppendLine($"{clampedStart + i}: {rawLines[i].TrimEnd('\r')}");

                bool clamped = clampedEnd < rangeEnd;
                int count = clampedEnd - clampedStart + 1;
                return new FileReadResult
                {
                    Text = ContextEngine.RenderReadRangeBlock(name, clampedStart, clampedEnd, totalLines, numbered.ToString(), clamped),
                    ResolvedPath = fullPath,
                    FileLoaded = true,
                    ListingContent = rangeContent,
                    ListingLineCount = count,
                    LogLine = $"[READ] {name}:{clampedStart}-{clampedEnd} ({count} lines){(clamped ? " [clamped]" : "")}",
                    LogColor = OutputColor.Success,
                };
            }

            // Full / outline: always from disk (a full read is the model refreshing its view).
            string content = ReadText(fullPath);
            string fileKey = FileKey(fullPath);
            _cache.Store(fileKey, content);
            int lineCount = content.Split('\n').Length;
            bool alreadyRead = _filesRead.Contains(fileKey);
            _filesRead.Add(fileKey);

            string rendered = ContextEngine.RenderReadBlock(name, content, lineCount, forceFull, alreadyRead, out bool wasOutline);
            return new FileReadResult
            {
                Text = rendered,
                ResolvedPath = fullPath,
                FileLoaded = true,
                WasOutline = wasOutline,
                ListingContent = wasOutline ? null : content,
                ListingLineCount = wasOutline ? 0 : lineCount,
                LogLine = wasOutline
                    ? $"[READ] {fullPath} ({lineCount} lines — outline{(alreadyRead ? ", re-read" : "")})"
                    : $"[READ] Loaded {fullPath} ({lineCount} lines)",
                LogColor = OutputColor.Success,
            };
        }

        /// <summary>True when read_file's filename selects the git branch ("git log", "git diff …").</summary>
        public static bool IsGitRequest(string filename) =>
            filename != null && filename.StartsWith("git ", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// read_file's git branch: 'git log [N]' (count from <paramref name="startLine"/> or the
        /// filename, default 10, max 50) or 'git diff [args]', run at the git root as an argv
        /// process (no shell — the args are never interpreted), output capped at 500 lines.
        /// </summary>
        public async Task<FileReadResult> ReadGitAsync(string filename, int startLine, CancellationToken ct)
        {
            string label = $"[{_policy.ReadLabel}]";
            string gitRoot = ContextEngine.FindGitRoot(_workingDirectory);
            if (gitRoot == null)
            {
                return new FileReadResult
                {
                    Text = $"{label} git: not a git repository\n",
                    LogLine = "[READ] git: not a git repository",
                    LogColor = OutputColor.Error,
                };
            }

            List<string> gitArgs;
            string header;
            if (filename.StartsWith("git log", StringComparison.OrdinalIgnoreCase))
            {
                int count;
                if (startLine > 0)
                {
                    count = startLine;
                }
                else
                {
                    string countPart = filename.Substring("git log".Length).Trim();
                    count = GitLogDefault;
                    if (!string.IsNullOrEmpty(countPart)) int.TryParse(countPart, out count);
                }
                count = Math.Max(1, Math.Min(count, GitLogMax));
                gitArgs = new List<string> { "log", "--oneline", "--no-decorate", $"-{count}" };
                header = $"{label} git log (last {count} commits)";
            }
            else if (filename.StartsWith("git diff", StringComparison.OrdinalIgnoreCase))
            {
                string diffArgs = filename.Substring("git diff".Length).Trim();
                gitArgs = new List<string> { "diff" };
                gitArgs.AddRange(ArgTokenizer.Tokenize(diffArgs));
                header = string.IsNullOrEmpty(diffArgs)
                    ? $"{label} git diff (working changes)"
                    : $"{label} git diff {diffArgs}";
            }
            else
            {
                string msg = $"{label} Unrecognized git command: {filename}";
                return new FileReadResult { Text = msg + "\n", LogLine = msg, LogColor = OutputColor.Error };
            }

            if (_shell == null) throw new InvalidOperationException("read_file git branch needs a ShellRunner");
            string savedDir = _shell.WorkingDirectory;
            _shell.ChangeDirectory(gitRoot);
            string output;
            int exitCode;
            try
            {
                (output, exitCode) = await _shell.ExecuteArgvAsync("git", gitArgs, ct).ConfigureAwait(false);
            }
            finally
            {
                _shell.ChangeDirectory(savedDir);
            }

            if (exitCode != 0)
            {
                string err = $"{header}\n(error — exit code {exitCode})\n{output}\n";
                return new FileReadResult { Text = err, LogLine = err.TrimEnd('\n'), LogColor = OutputColor.Error };
            }

            string[] outputLines = output.Split('\n');
            string shown = outputLines.Length > GitMaxOutputLines
                ? string.Join("\n", outputLines.Take(GitMaxOutputLines))
                  + $"\n[... {outputLines.Length - GitMaxOutputLines} lines omitted — use read_file with 'git diff <filename>' to narrow scope]"
                : output;
            if (string.IsNullOrWhiteSpace(shown)) shown = "(no output)";

            return new FileReadResult
            {
                Text = $"{header}\n```\n{shown}\n```\n\n",
                LogLine = header,
                LogColor = OutputColor.Success,
            };
        }

        // ── grep_file ────────────────────────────────────────────────────────

        /// <summary>grep_file: matching lines of one file (substring, '|' = OR), capped at 50.</summary>
        public FileReadResult Grep(string pattern, string filename, int? startLine, int? endLine)
        {
            string label = _policy.GrepLabel;
            FileResolution res = Resolve(filename);
            string fullPath = res.Path;
            if (fullPath == null || !File.Exists(fullPath))
                return new FileReadResult { Text = FilePathResolver.BuildFileNotFoundMessage(label, filename, res) };

            string key;
            try { key = EnsureCached(fullPath); }
            catch (Exception ex)
            {
                return new FileReadResult { Text = $"{label}: error reading {filename} — {ex.Message}", ResolvedPath = fullPath };
            }

            int totalLines = _cache.GetLineCount(key);
            int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
            int scanEnd = endLine.HasValue ? Math.Min(totalLines, endLine.Value) : totalLines;

            // Parameters + effective window in the transcript line — a bare "0 matches"
            // made the job-8 false negative undiagnosable from logs.
            string scope = $"[lines {scanStart}-{scanEnd} of {totalLines}" +
                $"{(startLine.HasValue || endLine.HasValue ? $", requested start_line={(startLine?.ToString() ?? "-")} end_line={(endLine?.ToString() ?? "-")}" : "")}]";

            var matcher = SearchPattern.BuildMatcher(pattern);
            var matches = new List<(int lineNum, string lineText)>();
            for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
            {
                string line = _cache.GetLineRange(key, lineNum, lineNum);
                if (line != null && matcher(line)) matches.Add((lineNum, line));
            }

            if (matches.Count == 0)
            {
                return new FileReadResult
                {
                    Text = SearchPattern.DescribeSearchMiss(pattern, $"{filename} {scope}", -1,
                        $"{label}: no matches for \"{pattern}\" in {filename}"),
                    ResolvedPath = fullPath,
                    LogLine = $"[GREP] no matches for \"{pattern}\" in {filename} {scope}",
                };
            }

            int total = matches.Count;
            bool truncated = total > GrepMaxMatches;
            if (truncated) matches = matches.GetRange(0, GrepMaxMatches);
            int numWidth = matches[matches.Count - 1].lineNum.ToString().Length;

            var sb = new StringBuilder();
            sb.AppendLine(truncated
                ? $"{label} results for \"{pattern}\" in {filename} ({GrepMaxMatches} of {total} matches — narrow your pattern or use a line range):"
                : $"{label} results for \"{pattern}\" in {filename} ({total} match{(total == 1 ? "" : "es")}):");
            foreach (var (lineNum, lineText) in matches)
                sb.AppendLine($"  {lineNum.ToString().PadLeft(numWidth)}: {lineText.TrimEnd()}");

            return new FileReadResult
            {
                Text = sb.ToString().TrimEnd('\r', '\n'),
                ResolvedPath = fullPath,
                MatchCount = total,
                LogLine = $"[GREP] {total} match{(total == 1 ? "" : "es")} for \"{pattern}\" in {filename} {scope}",
                LogColor = OutputColor.Success,
            };
        }

        // ── find_in_files ────────────────────────────────────────────────────

        /// <summary>
        /// find_in_files: matching lines across the files a glob selects under the working
        /// directory (or an explicit absolute <paramref name="root"/>), capped at 100. Skips
        /// noise directories, cloud placeholders, binaries and oversized files; an unreadable
        /// file is skipped.
        /// </summary>
        public FileReadResult Find(string pattern, string glob, string root, int? startLine, int? endLine)
        {
            string label = _policy.FindLabel;
            string searchDir = ResolveSearchRoot(root, label, out string rootError);
            if (rootError != null) return new FileReadResult { Text = rootError };

            var (effectiveRoot, filePattern) = SplitGlob(searchDir, glob);
            var wildcardDirs = WildcardDirGlob(searchDir, glob);

            List<string> files;
            try
            {
                files = (wildcardDirs is { } g
                        ? EnumerateWildcardDirGlob(g)
                        : ContextEngine.SafeEnumerateFilesGlob(effectiveRoot, filePattern))
                    .Where(f => !ContextEngine.IsNoisePath(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                return new FileReadResult { Text = $"{label}: error enumerating files for {glob} — {ex.Message}" };
            }

            var matcher = SearchPattern.BuildMatcher(pattern);
            var all = new List<(string file, int lineNum, string lineText)>();
            bool hitCap = false;

            foreach (string filePath in files)
            {
                if (hitCap) break;
                string key = FileKey(filePath);
                _cache.InvalidateIfStale(key, filePath); // out-of-band writes
                if (!_cache.Contains(key))
                {
                    // Never open cloud/OneDrive placeholders (would download), binaries, or
                    // oversized files for a text search — metadata checks only.
                    if (ContextEngine.ShouldSkipForContentSearch(filePath)) continue;
                    string text;
                    try { text = ReadText(filePath); }
                    catch { continue; }
                    _cache.Store(key, text);
                }

                int totalLines = _cache.GetLineCount(key);
                int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
                int scanEnd = endLine.HasValue ? Math.Min(totalLines, endLine.Value) : totalLines;
                string fileLabel = SafeGetFileName(filePath);

                for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
                {
                    string line = _cache.GetLineRange(key, lineNum, lineNum);
                    if (line == null || !matcher(line)) continue;
                    all.Add((fileLabel, lineNum, line));
                    if (all.Count >= FindMaxMatches) { hitCap = true; break; }
                }
            }

            if (all.Count == 0)
            {
                return new FileReadResult
                {
                    Text = SearchPattern.DescribeSearchMiss(pattern, glob, files.Count,
                        $"{label}: no matches for \"{pattern}\" in {glob}"),
                    LogLine = $"[FIND] no matches for \"{pattern}\" in {glob}",
                };
            }

            int shown = all.Count;
            var sb = new StringBuilder();
            sb.AppendLine(hitCap
                ? $"{label} results for \"{pattern}\" in {glob} ({FindMaxMatches}+ matches — narrow your pattern or add a line range):"
                : $"{label} results for \"{pattern}\" in {glob} ({shown} match{(shown == 1 ? "" : "es")}):");
            foreach (var (file, lineNum, lineText) in all)
                sb.AppendLine($"  {file}:{lineNum}: {lineText.TrimEnd()}");

            return new FileReadResult
            {
                Text = sb.ToString().TrimEnd('\r', '\n'),
                MatchCount = shown,
                LogLine = $"[FIND] {(hitCap ? FindMaxMatches + "+" : shown.ToString())} match{(shown == 1 ? "" : "es")} for \"{pattern}\" in {glob}",
                LogColor = OutputColor.Success,
            };
        }

        // ── list_files ───────────────────────────────────────────────────────

        /// <summary>
        /// list_files: absolute paths matching a glob under the working directory (or an
        /// explicit absolute <paramref name="root"/>), noise directories pruned, sorted, capped
        /// at 200. Enumeration errors are reported, never thrown; cancellation throws.
        /// </summary>
        public FileReadResult List(string glob, bool recursive, string root, CancellationToken ct)
        {
            string searchDir = ResolveSearchRoot(root, _policy.ListLabel, out string rootError);
            if (rootError != null) return new FileReadResult { Text = rootError };

            var (effectiveRoot, filePattern) = SplitGlob(searchDir, glob);
            if (string.IsNullOrWhiteSpace(filePattern))
                return new FileReadResult { Text = "[ERROR: glob pattern is empty]" };
            // A wildcard in the directory part spells out its own depth, so `recursive` does not apply.
            var wildcardDirs = WildcardDirGlob(searchDir, glob);

            List<string> sorted;
            try
            {
                IEnumerable<string> matches = wildcardDirs is { } g
                    ? EnumerateWildcardDirGlob(g)
                    : recursive
                        ? ContextEngine.SafeEnumerateFilesGlob(effectiveRoot, filePattern)
                        : Directory.EnumerateFiles(effectiveRoot, filePattern, SearchOption.TopDirectoryOnly);
                // Materialized INSIDE the try: enumeration is lazy, and its errors used to
                // escape the agent hosts' try as an unhandled tool exception.
                sorted = matches
                    .Where(f => !ContextEngine.IsNoisePath(f))
                    .Select(Path.GetFullPath)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new FileReadResult { Text = $"[ERROR: {ex.Message}]" };
            }

            if (sorted.Count == 0)
                return new FileReadResult { Text = "[no matches]" };

            var sb = new StringBuilder();
            int shown = Math.Min(sorted.Count, ListCap);
            for (int i = 0; i < shown; i++)
            {
                ct.ThrowIfCancellationRequested();
                sb.AppendLine(sorted[i]);
            }
            if (sorted.Count > ListCap)
                sb.AppendLine($"[truncated — {sorted.Count - ListCap} more matches]");

            return new FileReadResult
            {
                Text = sb.ToString().TrimEnd(),
                MatchCount = sorted.Count,
                LogLine = $"[LIST] {shown} file{(shown == 1 ? "" : "s")} matching \"{glob}\"",
            };
        }

        // ── diff_file ────────────────────────────────────────────────────────

        /// <summary>
        /// diff_file: the file now vs its session baseline (captured on first read/patch),
        /// CRLF-normalized, as a unified diff. A deleted file diffs against empty.
        /// </summary>
        public FileReadResult Diff(string filename)
        {
            string label = _policy.DiffLabel;
            FileResolution res = Resolve(filename);
            // A deleted file does not resolve — fall back to the path as given.
            string fullPath = res.Path ?? (string.IsNullOrEmpty(_workingDirectory)
                ? filename
                : Path.Combine(_workingDirectory, filename ?? ""));
            string key = FileKey(fullPath);

            if (!_snapshots.TryGet(key, out string original))
            {
                if (_snapshots.WasEvicted(key))
                {
                    return new FileReadResult
                    {
                        Text = $"{label}: No diff available — the session baseline for {filename} was evicted " +
                               $"(only the {_snapshots.Capacity} most recently used files keep one).",
                        ResolvedPath = res.Path,
                        LogLine = $"[DIFF] {filename}: baseline evicted",
                    };
                }
                return new FileReadResult
                {
                    Text = $"{label}: No changes — {filename} has not been modified this session.",
                    ResolvedPath = res.Path,
                    LogLine = $"[DIFF] {filename}: not modified this session",
                };
            }

            string current;
            try { current = File.Exists(fullPath) ? ReadText(fullPath) : string.Empty; }
            catch (Exception ex)
            {
                return new FileReadResult { Text = $"{label}: error reading {filename} — {ex.Message}", ResolvedPath = res.Path };
            }

            string normOld = original.Replace("\r\n", "\n").Replace("\r", "\n");
            string normNew = current.Replace("\r\n", "\n").Replace("\r", "\n");
            if (string.Equals(normOld, normNew, StringComparison.Ordinal))
            {
                return new FileReadResult
                {
                    Text = $"{label}: No changes detected in {filename}.",
                    ResolvedPath = res.Path,
                    LogLine = $"[DIFF] {filename}: no changes",
                };
            }

            string[] oldLines = normOld.Split('\n');
            string[] newLines = normNew.Split('\n');
            return new FileReadResult
            {
                Text = DiffHelper.GenerateUnifiedDiff(filename, oldLines, newLines),
                ResolvedPath = res.Path,
                DiffOld = normOld,
                DiffNew = normNew,
                LogLine = $"[DIFF] {filename}: changes shown ({oldLines.Length} → {newLines.Length} lines)",
            };
        }
    }
}
