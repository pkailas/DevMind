// File: DevMindTools.cs  v5.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Diagnostic policy: never write to Console.Out / Console.WriteLine in this file.
// All diagnostics go to Console.Error (stderr). stdout is reserved exclusively for
// the MCP stdio transport's JSON-RPC framing and must not be touched by application code.
//
// Stderr diagnostic pattern for Core callbacks:
//   When a Core utility accepts Action<string, OutputColor> for diagnostics, pass:
//     (text, _) => Console.Error.Write(text)
//   This redirects all Core diagnostic output to stderr.
//
// Tool serialization:
//   Every tool method enqueues its body via _svc.EnqueueAsync(...). The Channel-based
//   single consumer in McpServices ensures strictly sequential FIFO tool execution,
//   preventing file-handle conflicts and dictionary races when a bursty client
//   (test harness, Claude Desktop batch) sends multiple requests simultaneously.
//
// Phase status:
//   Phase A (complete): list_memory_topics.
//   Phase B (complete): read_file, list_files, grep_file, find_in_files,
//                       diff_file (upgraded in Phase C), recall_memory.
//   Phase C (complete): patch_file, create_file, append_file, delete_file,
//                       rename_file, save_memory; diff_file real unified diffs.
//   Phase C fix v2: Channel<DispatchItem> FIFO dispatch via _svc.EnqueueAsync.
//   Phase D (complete): run_shell, run_build, run_tests with streaming progress.
//   Phase D (web): web_search, web_fetch.
//   Phase D (clip/open/http): clip_read, clip_write, open_file, http_request.
//   Phase E (complete): LSP tools — get_diagnostics, go_to_definition, find_references, hover.
//   Phase F (complete): git_commit.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DevMind;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Renci.SshNet;
using DevMind.McpServer;
using ModelContextProtocol;
using ModelContextProtocol.Server;

[McpServerToolType]
internal sealed class DevMindTools
{
    private readonly McpServices _svc;

    public DevMindTools(McpServices svc) => _svc = svc;

    // ── Phase A ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_memory_topics")]
    [Description(
        "List all available memory topics with their descriptions from the memory index. " +
        "Use this to see what knowledge has been saved in previous sessions before " +
        "recalling a specific topic with recall_memory.")]
    public async Task<string> ListMemoryTopics(CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            string index = _svc.Memory.LoadIndex();
            if (!string.IsNullOrWhiteSpace(index))
                return index;

            var topics = _svc.Memory.ListTopics();
            return topics.Count == 0
                ? "No memory topics found. Use save_memory to create one."
                : string.Join("\n", topics.Select(t => $"- [{t}]"));
        }, cancellationToken);
    }

    // ── Phase B: read-only tools ─────────────────────────────────────────────

    [McpServerTool(Name = "read_file")]
    [Description(
        "Read a file from the project. Files under 100 lines return full content; " +
        "larger files return an outline (class/method/property declarations with line numbers). " +
        "Use start_line/end_line for targeted reads after reviewing the outline. " +
        "Set force_full to true only when you need the entire file regardless of size. " +
        "For git operations set filename to 'git log' or 'git diff [args]'.")]
    public async Task<string> ReadFile(
        [Description("Absolute file path, or 'git log'/'git diff [args]' for git operations.")] string filename,
        [Description("1-based start line for a targeted range read.")] int? start_line = null,
        [Description("1-based end line for a targeted range read.")] int? end_line = null,
        [Description("When true, bypasses the outline threshold and returns full file content.")] bool? force_full = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                if (filename.StartsWith("git ", StringComparison.OrdinalIgnoreCase))
                    return await ReadGitAsync(filename, start_line ?? 0, cancellationToken);

                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("read_file", filename);

                string fileNameOnly = Path.GetFileName(fullPath) ?? Path.GetFileName(filename) ?? filename;
                string cacheKey     = Path.GetFullPath(fullPath);
                bool forceFullRead  = force_full == true;

                // ── Line-range path ──────────────────────────────────────────
                if (start_line.HasValue && start_line.Value > 0)
                {
                    EnsureCached(fileNameOnly, fullPath);

                    int totalLines   = _svc.FileCache.GetLineCount(cacheKey);
                    int rawStart     = start_line.Value;
                    int rawEnd       = end_line.HasValue ? end_line.Value : totalLines;
                    if (rawStart > rawEnd) { int t = rawStart; rawStart = rawEnd; rawEnd = t; }
                    int clampedStart = Math.Max(1, rawStart);
                    int clampedEnd   = Math.Min(totalLines, rawEnd);

                    string rangeContent = _svc.FileCache.GetLineRange(cacheKey, clampedStart, clampedEnd);
                    if (rangeContent == null)
                        return $"[read_file] Range {rawStart}-{rawEnd} out of bounds for {fileNameOnly} ({totalLines} lines)";

                    var rawLines = rangeContent.Split('\n');
                    var numbered = new StringBuilder();
                    for (int i = 0; i < rawLines.Length; i++)
                        numbered.AppendLine($"{clampedStart + i}: {rawLines[i].TrimEnd('\r')}");

                    bool clamped = clampedEnd < rawEnd;
                    return ContextEngine.RenderReadRangeBlock(
                        fileNameOnly, clampedStart, clampedEnd, totalLines, numbered.ToString(), clamped);
                }

                // ── Full / outline path ──────────────────────────────────────
                string content   = File.ReadAllText(fullPath);
                _svc.FileCache.Store(cacheKey, content);
                int lineCount    = content.Split('\n').Length;
                bool alreadyRead = _svc.FilesRead.Contains(cacheKey);
                _svc.FilesRead.Add(cacheKey);

                return ContextEngine.RenderReadBlock(
                    fileNameOnly, content, lineCount, forceFullRead, alreadyRead, out _);
            }
            catch (Exception ex)
            {
                return $"[read_file error] {filename}: {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "list_files")]
    [Description(
        "List files matching a glob pattern under the project root. Returns absolute paths, " +
        "alphabetically sorted, capped at 200 results. Skips build artifacts (bin, obj) and " +
        "version control metadata (.vs, .git, node_modules, packages) automatically.")]
    public async Task<string> ListFiles(
        [Description("Glob pattern to match files (e.g., '*.cs', 'Services/*.cs'). Required.")] string glob,
        [Description("If true (default), searches all subdirectories.")] bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            const int Cap = 200;
            try
            {
                string searchDir = _svc.WorkingDirectory;

                string normalizedGlob = (glob ?? "").Replace('\\', '/');
                string filePattern    = normalizedGlob;
                string effectiveRoot  = searchDir;
                int lastSlash         = normalizedGlob.LastIndexOf('/');
                if (lastSlash >= 0)
                {
                    string dirPart   = normalizedGlob.Substring(0, lastSlash);
                    filePattern      = normalizedGlob.Substring(lastSlash + 1);
                    string candidate = Path.Combine(searchDir, dirPart.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(candidate)) effectiveRoot = candidate;
                }

                if (string.IsNullOrWhiteSpace(filePattern))
                    return "[ERROR: glob pattern is empty]";

                IEnumerable<string> matches = recursive
                    ? ContextEngine.SafeEnumerateFilesGlob(effectiveRoot, filePattern)
                        .Where(f => !ContextEngine.IsNoisePath(f))
                    : Directory.EnumerateFiles(effectiveRoot, filePattern, SearchOption.TopDirectoryOnly)
                        .Where(f => !ContextEngine.IsNoisePath(f));

                var sorted = matches
                    .Select(Path.GetFullPath)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (sorted.Count == 0)
                    return "[no matches]";

                var sb    = new StringBuilder();
                int shown = Math.Min(sorted.Count, Cap);
                for (int i = 0; i < shown; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sb.AppendLine(sorted[i]);
                }
                if (sorted.Count > Cap)
                    sb.AppendLine($"[truncated — {sorted.Count - Cap} more matches]");

                return sb.ToString().TrimEnd();
            }
            catch (OperationCanceledException)
            {
                return "[list_files: cancelled]";
            }
            catch (Exception ex)
            {
                return $"[list_files error] {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "grep_file")]
    [Description(
        "Search a single file for lines matching a pattern (case-insensitive substring match). " +
        "Returns matching lines with 1-based line numbers, capped at 50 matches. " +
        "Use grep_file to locate code, then read_file with a targeted range, then patch_file.")]
    public async Task<string> GrepFile(
        [Description("Search pattern (case-insensitive substring match, not regex).")] string pattern,
        [Description("Absolute file path.")] string filename,
        [Description("1-based start line to restrict the search window.")] int? start_line = null,
        [Description("1-based end line to restrict the search window.")] int? end_line = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            const int MaxMatches = 50;
            try
            {
                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("grep_file", filename);

                string fileNameOnly = Path.GetFileName(fullPath) ?? Path.GetFileName(filename) ?? filename;
                string cacheKey     = Path.GetFullPath(fullPath);
                EnsureCached(fileNameOnly, fullPath);

                int totalLines = _svc.FileCache.GetLineCount(cacheKey);
                int scanStart  = start_line.HasValue ? Math.Max(1, start_line.Value) : 1;
                int scanEnd    = end_line.HasValue   ? Math.Min(totalLines, end_line.Value) : totalLines;

                var matches = new List<(int lineNum, string lineText)>();
                for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
                {
                    string lineContent = _svc.FileCache.GetLineRange(cacheKey, lineNum, lineNum);
                    if (lineContent == null) continue;
                    if (lineContent.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        matches.Add((lineNum, lineContent));
                }

                if (matches.Count == 0)
                    return $"grep_file: no matches for \"{pattern}\" in {filename}";

                int totalMatches = matches.Count;
                bool truncated   = totalMatches > MaxMatches;
                if (truncated) matches = matches.GetRange(0, MaxMatches);

                int numWidth = matches[matches.Count - 1].lineNum.ToString().Length;
                string header = truncated
                    ? $"grep_file results for \"{pattern}\" in {filename} ({MaxMatches} of {totalMatches} matches — narrow your pattern or use a line range):"
                    : $"grep_file results for \"{pattern}\" in {filename} ({totalMatches} match{(totalMatches == 1 ? "" : "es")}):";

                var sb = new StringBuilder();
                sb.AppendLine(header);
                foreach (var (lineNum, lineText) in matches)
                    sb.AppendLine($"  {lineNum.ToString().PadLeft(numWidth)}: {lineText.TrimEnd()}");

                return sb.ToString().TrimEnd('\r', '\n');
            }
            catch (Exception ex)
            {
                return $"[grep_file error] {filename}: {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "find_in_files")]
    [Description(
        "Search across multiple files by glob pattern for lines matching a text pattern. " +
        "Returns filename:line:content for each hit, capped at 100 results. " +
        "Use find_in_files when you need to know where something is used across the project. " +
        "Use grep_file when you already know which file to search.")]
    public async Task<string> FindInFiles(
        [Description("Search pattern (case-insensitive substring match).")] string pattern,
        [Description("Glob pattern to match files (e.g., '*.cs', 'Services/*.cs').")] string glob,
        [Description("1-based start line to restrict the search window within each file.")] int? start_line = null,
        [Description("1-based end line to restrict the search window within each file.")] int? end_line = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            const int MaxMatches = 100;
            try
            {
                string searchDir      = _svc.WorkingDirectory;
                string normalizedGlob = glob.Replace('\\', '/');
                string filePattern    = normalizedGlob;
                string effectiveRoot  = searchDir;
                int lastSlash         = normalizedGlob.LastIndexOf('/');
                if (lastSlash >= 0)
                {
                    string dirPart   = normalizedGlob.Substring(0, lastSlash);
                    filePattern      = normalizedGlob.Substring(lastSlash + 1);
                    string candidate = Path.Combine(searchDir, dirPart.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(candidate)) effectiveRoot = candidate;
                }

                IEnumerable<string> files;
                try
                {
                    files = ContextEngine.SafeEnumerateFilesGlob(effectiveRoot, filePattern)
                        .Where(f => !ContextEngine.IsNoisePath(f))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    return $"find_in_files: error enumerating files for {glob} — {ex.Message}";
                }

                var allMatches = new List<(string fileLabel, int lineNum, string lineText)>();
                bool hitCap    = false;

                foreach (string filePath in files)
                {
                    if (hitCap) break;
                    string fileNameOnly = Path.GetFileName(filePath);
                    string cacheKey     = Path.GetFullPath(filePath);

                    if (!_svc.FileCache.Contains(cacheKey))
                    {
                        string diskContent;
                        try { diskContent = File.ReadAllText(filePath); }
                        catch { continue; }
                        _svc.FileCache.Store(cacheKey, diskContent);
                    }

                    int totalLines = _svc.FileCache.GetLineCount(cacheKey);
                    int scanStart  = start_line.HasValue ? Math.Max(1, start_line.Value) : 1;
                    int scanEnd    = end_line.HasValue   ? Math.Min(totalLines, end_line.Value) : totalLines;

                    for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
                    {
                        string lineContent = _svc.FileCache.GetLineRange(cacheKey, lineNum, lineNum);
                        if (lineContent == null) continue;
                        if (lineContent.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            allMatches.Add((fileNameOnly, lineNum, lineContent));
                            if (allMatches.Count >= MaxMatches) { hitCap = true; break; }
                        }
                    }
                }

                if (allMatches.Count == 0)
                    return $"find_in_files: no matches for \"{pattern}\" in {glob}";

                int shownCount    = allMatches.Count;
                string findHeader = hitCap
                    ? $"find_in_files results for \"{pattern}\" in {glob} ({MaxMatches}+ matches — narrow your pattern or add a line range):"
                    : $"find_in_files results for \"{pattern}\" in {glob} ({shownCount} match{(shownCount == 1 ? "" : "es")}):";

                var sb = new StringBuilder();
                sb.AppendLine(findHeader);
                foreach (var (fileLabel, lineNum, lineText) in allMatches)
                    sb.AppendLine($"  {fileLabel}:{lineNum}: {lineText.TrimEnd()}");

                return sb.ToString().TrimEnd('\r', '\n');
            }
            catch (Exception ex)
            {
                return $"[find_in_files error] {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "diff_file")]
    [Description(
        "Show all changes made to a file during this session as a unified-style diff. " +
        "Use diff_file after multiple patches to verify cumulative changes before completing a task. " +
        "Information-gathering only — does not modify files.")]
    public async Task<string> DiffFile(
        [Description("Absolute file path.")] string filename,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                // Resolve path; handle deleted files (they won't resolve via normal lookup).
                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null)
                {
                    fullPath = Path.IsPathRooted(filename)
                        ? filename
                        : Path.Combine(_svc.WorkingDirectory, filename);
                }

                if (!_svc.TryGetSnapshot(fullPath, out string snapshot))
                    return $"diff_file: no session changes tracked for {Path.GetFileName(filename)} — " +
                           "diff_file reflects changes since first read_file or patch_file call this session.";

                // Current content: empty string if the file was deleted.
                string current = File.Exists(fullPath)
                    ? File.ReadAllText(fullPath)
                    : string.Empty;

                string normOld = snapshot.Replace("\r\n", "\n").Replace("\r", "\n");
                string normNew = current.Replace("\r\n", "\n").Replace("\r", "\n");

                if (string.Equals(normOld, normNew, StringComparison.Ordinal))
                    return $"diff_file: no changes detected in {Path.GetFileName(filename)}.";

                string[] oldLines = normOld.Split('\n');
                string[] newLines = normNew.Split('\n');
                return DiffHelper.GenerateUnifiedDiff(Path.GetFileName(filename), oldLines, newLines);
            }
            catch (Exception ex)
            {
                return $"[diff_file error] {filename}: {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "recall_memory")]
    [Description(
        "Recall previously saved knowledge about a topic. Returns the content of a memory " +
        "topic file. Call list_memory_topics first if you are not sure what topics are available.")]
    public async Task<string> RecallMemory(
        [Description("The topic slug to recall (e.g., 'auth-system', 'build-quirks').")] string topic,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            string content = _svc.Memory.LoadTopic(topic);
            if (content == null)
            {
                var available = _svc.Memory.ListTopics();
                if (available.Count == 0)
                    return $"recall_memory: topic \"{topic}\" not found — no memory topics exist yet.";
                string list = string.Join(", ", available.Select(t => $"[{t}]"));
                return $"recall_memory: topic \"{topic}\" not found. Available topics: {list}";
            }
           return content;
        }, cancellationToken);
    }

    [McpServerTool(Name = "search_memory")]
    [Description(
        "Search across all saved memory topics for a keyword or phrase. " +
        "Returns matching topic names and the lines that matched, with context. " +
        "Use when you know what you're looking for but not which topic it's in.")]
    public async Task<string> SearchMemory(
        [Description("Search pattern (case-insensitive substring match).")] string pattern,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                var topics = _svc.Memory.ListTopics();
                if (topics.Count == 0)
                    return "search_memory: no memory topics found.";

                var results = new List<(string topic, int lineNum, string lineText)>();

                foreach (var topic in topics)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string content = _svc.Memory.LoadTopic(topic);
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    var lines = content.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                            results.Add((topic, i + 1, lines[i].TrimEnd()));
                    }
                }

                if (results.Count == 0)
                    return $"search_memory: no matches for \"{pattern}\" across {topics.Count} topic(s).";

                var sb = new StringBuilder();
                sb.AppendLine($"search_memory results for \"{pattern}\" ({results.Count} match(es) across {results.Select(r => r.topic).Distinct().Count()} topic(s)):");

                foreach (var group in results.GroupBy(r => r.topic))
                {
                    sb.AppendLine($"\n[{group.Key}]");
                    foreach (var (_, lineNum, lineText) in group)
                        sb.AppendLine($"  {lineNum}: {lineText}");
                }

                return sb.ToString().TrimEnd();
            }
            catch (OperationCanceledException)
            {
                return "[search_memory] Cancelled.";
            }
            catch (Exception ex)
            {
                return $"[search_memory error] {ex.Message}";
            }
       }, cancellationToken);
    }

    [McpServerTool(Name = "query_db")]
    [Description(
        "Execute a read-only SQL query against a named database connection. " +
        "Connections are defined in DEVMIND_DB_CONNECTIONS environment variable. " +
        "Only SELECT statements and read-only stored proc calls are permitted. " +
        "Supports SQL Server, PostgreSQL, and SQLite — auto-detected from connection string. " +
        "Returns results as a formatted table, capped at 100 rows.")]
    public async Task<string> QueryDb(
        [Description("Connection alias from DEVMIND_DB_CONNECTIONS.")] string connection,
        [Description("SQL query to execute (SELECT only).")] string query,
        [Description("Query timeout in seconds (default 30).")] int? timeout_seconds = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            const int MaxRows = 100;
            try
            {
                // Resolve connection string.
                if (!_svc.DbConnections.TryGetValue(connection, out string? connStr) || string.IsNullOrWhiteSpace(connStr))
                    return $"[query_db] Unknown connection \"{connection}\". Define it in DEVMIND_DB_CONNECTIONS.";

                // Safety check — read-only only.
                string trimmed = query.TrimStart().ToUpperInvariant();
                string[] blocked = { "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "TRUNCATE", "CREATE", "EXEC ", "EXECUTE " };
                foreach (var keyword in blocked)
                {
                    if (trimmed.StartsWith(keyword, StringComparison.Ordinal))
                        return $"[query_db] Blocked: only SELECT queries are permitted. Got: {keyword}";
                }

                int timeoutSecs = timeout_seconds ?? 30;

                // Auto-detect provider from connection string.
                DbConnection dbConn;
                string connUpper = connStr.ToUpperInvariant();
                if (connStr.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
                    connUpper.Contains("HOST="))
                {
                    dbConn = new NpgsqlConnection(connStr);
                }
                // SQLite only when the connection string names a .db/.sqlite FILE and is not
                // a SQL Server string (whose host can contain ".db", e.g. prod.db.corp.local).
                // Explicit parens fix a precedence bug: `Contains(".DB") || X && Y` bound as
                // `Contains(".DB") || (X && Y)`, misrouting any ".db"-containing SQL Server host.
                else if ((connUpper.Contains(".DB") || connUpper.Contains(".SQLITE"))
                         && !(connUpper.Contains("SERVER=")
                              || connUpper.Contains("INITIAL CATALOG=")
                              || connUpper.Contains("INTEGRATED SECURITY=")
                              || connUpper.Contains("TRUSTED_CONNECTION=")
                              || connUpper.Contains("TRUSTSERVERCERTIFICATE=")))
                {
                    dbConn = new SqliteConnection(connStr);
                }
                else
                {
                    dbConn = new SqlConnection(connStr);
                }

                await using (dbConn)
                {
                    await dbConn.OpenAsync(cancellationToken);

                    using var cmd = dbConn.CreateCommand();
                    cmd.CommandText    = query;
                    cmd.CommandTimeout = timeoutSecs;

                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

                    // Build column headers.
                    var columns = new List<string>();
                    for (int i = 0; i < reader.FieldCount; i++)
                        columns.Add(reader.GetName(i));

                    var rows = new List<string[]>();
                    int rowCount = 0;
                    bool capped  = false;

                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (rowCount >= MaxRows) { capped = true; break; }
                        var row = new string[reader.FieldCount];
                        for (int i = 0; i < reader.FieldCount; i++)
                            row[i] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "";
                        rows.Add(row);
                        rowCount++;
                    }

                    if (rows.Count == 0)
                        return $"[query_db] Query returned no rows.\nQuery: {query}";

                    // Calculate column widths.
                    var widths = columns.Select((c, i) =>
                        Math.Max(c.Length, rows.Max(r => r[i].Length))).ToArray();

                    // Render table.
                    var sb = new StringBuilder();
                    sb.AppendLine($"[query_db] {connection} → {rows.Count} row(s){(capped ? $" (capped at {MaxRows})" : "")}");
                    sb.AppendLine();

                    // Header.
                    sb.AppendLine("| " + string.Join(" | ", columns.Select((c, i) => c.PadRight(widths[i]))) + " |");
                    sb.AppendLine("|-" + string.Join("-|-", widths.Select(w => new string('-', w))) + "-|");

                    // Rows.
                    foreach (var row in rows)
                        sb.AppendLine("| " + string.Join(" | ", row.Select((v, i) => v.PadRight(widths[i]))) + " |");

                    if (capped)
                        sb.AppendLine($"\n[truncated — showing first {MaxRows} rows]");

                    return sb.ToString().TrimEnd();
                }
            }
            catch (OperationCanceledException)
            {
                return "[query_db] Cancelled.";
            }
            catch (Exception ex)
            {
                return $"[query_db error] {ex.Message}";
            }
        }, cancellationToken);
    }

    // ── Phase C: mutation tools ──────────────────────────────────────────────

    [McpServerTool(Name = "patch_file")]
    [Description(
        "Edit an existing file by replacing exact text. The find text must be copied " +
        "verbatim from read_file output — never reconstructed from memory. " +
        "Whitespace-normalized matching is applied (CRLF and indentation differences ignored). " +
        "Always read_file first if you have not seen the file.")]
    public async Task<string> PatchFile(
        [Description("Absolute file path.")] string filename,
        [Description("Exact text to find in the file (verbatim from read_file output).")] string find,
        [Description("Replacement text.")] string replace,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("patch_file", filename);

                string fileNameOnly = Path.GetFileName(fullPath) ?? filename;
                string cacheKey     = Path.GetFullPath(fullPath);

                // Capture pre-patch baseline for diff_file.
                _svc.TrySnapshot(fullPath);

                // Read file content preserving encoding (matches what PatchEngine writes back).
                var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(fullPath);

                // Build PATCH block. fromToolCall=true: PatchEngine skips fence stripping.
                string patchInput = $"PATCH {fileNameOnly}\nFIND:\n{find}\nREPLACE:\n{replace}";

                // Capture reporter output so errors return to the MCP client AND go to stderr.
                var errorLog = new StringBuilder();
                Action<string, OutputColor> reporter = (text, color) =>
                {
                    Console.Error.Write(text);
                    if (color == OutputColor.Error || color == OutputColor.Warning)
                        errorLog.Append(text);
                };

                var resolved = PatchEngine.ResolvePatch(
                    patchInput, fullPath, fileNameOnly, content, encoding,
                    fromToolCall: true, reporter: reporter);

                if (resolved == null)
                {
                    string detail = errorLog.Length > 0
                        ? errorLog.ToString().Trim()
                        : "find text not found or is ambiguous — read_file first and copy the text verbatim";
                    return $"patch_file: failed — {detail}";
                }

                string backupDir = Path.Combine(Path.GetTempPath(), "DevMind", "McpServer");
                var applyResult  = PatchEngine.ApplyPatch(resolved, backupDir);
                if (!applyResult.Success)
                    return $"patch_file: write failed — {applyResult.Error}";

                // Update cache with post-patch content so subsequent read_file is consistent.
                _svc.FileCache.Store(cacheKey, applyResult.UpdatedContent);
                _svc.FilesRead.Add(cacheKey);

                string badge = resolved.Confidence == PatchConfidence.Fuzzy
                    ? " (fuzzy match — verify with diff_file)"
                    : "";
                return $"patch_file: applied to {fullPath}{badge}";
            }
            catch (Exception ex)
            {
                return $"[patch_file error] {filename}: {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "create_file")]
    [Description(
        "Create a new file with the given content. Use for brand-new files only — " +
        "use patch_file to edit existing files. Do not wrap content in code fences.")]
    public async Task<string> CreateFile(
        [Description("Absolute file path.")] string filename,
        [Description("The complete content for the new file.")] string content,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                string fullPath = Path.IsPathRooted(filename)
                    ? filename
                    : Path.Combine(_svc.WorkingDirectory, filename);

                if (File.Exists(fullPath))
                    return $"create_file: file already exists — {fullPath}. Use patch_file to edit it.";

               string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Script files (.cmd/.bat/.sh/.ps1) always get UTF-8 without BOM to prevent
                // garbled output in shells that don't expect a BOM preamble.
                var fileEncoding = IsScriptFileExtension(fullPath)
                    ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    : System.Text.Encoding.UTF8;
                File.WriteAllText(fullPath, content, fileEncoding);

                int lineCount       = content.Split('\n').Length;
                string cacheKey     = Path.GetFullPath(fullPath);
                _svc.FileCache.Store(cacheKey, content);
                _svc.FilesRead.Add(cacheKey);

                return $"create_file: created {fullPath} ({lineCount} lines)";
            }
            catch (Exception ex)
            {
                return $"[create_file error] {filename}: {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "append_file")]
    [Description(
        "Append content to the end of an existing file. If the file does not exist, it will be created.")]
    public async Task<string> AppendFile(
        [Description("Absolute file path.")] string filename,
        [Description("Content to append to the end of the file.")] string content,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                string fullPath = Path.IsPathRooted(filename)
                    ? filename
                    : Path.Combine(_svc.WorkingDirectory, filename);

                // Snapshot the original content before first mutation (for diff_file).
                if (File.Exists(fullPath))
                    _svc.TrySnapshot(fullPath);

                bool existed = File.Exists(fullPath);

                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

               // Script files (.cmd/.bat/.sh/.ps1) always get UTF-8 without BOM.
                var appendEncoding = IsScriptFileExtension(fullPath)
                    ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    : System.Text.Encoding.UTF8;

                // Ensure a newline separator between existing content and appended content.
                if (existed)
                {
                    string existing  = File.ReadAllText(fullPath);
                    string separator = existing.Length > 0 && !existing.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "";
                    File.WriteAllText(fullPath, existing + separator + content, appendEncoding);
                }
                else
                {
                    File.WriteAllText(fullPath, content, appendEncoding);
                }

                string cacheKey = Path.GetFullPath(fullPath);
                _svc.FileCache.Invalidate(cacheKey);

                return existed
                    ? $"append_file: appended to {fullPath}"
                    : $"append_file: created {fullPath}";
            }
            catch (Exception ex)
            {
                return $"[append_file error] {filename}: {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "delete_file")]
    [Description(
        "Delete a file from disk. Use only when explicitly asked to remove a file.")]
    public async Task<string> DeleteFile(
        [Description("Absolute file path.")] string filename,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("delete_file", filename);

                // Snapshot before deleting so diff_file can show the removal.
                _svc.TrySnapshot(fullPath);

                File.Delete(fullPath);

                string cacheKey = Path.GetFullPath(fullPath);
                _svc.FileCache.Invalidate(cacheKey);

                return $"delete_file: deleted {fullPath}";
            }
            catch (Exception ex)
            {
                return $"[delete_file error] {filename}: {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "rename_file")]
    [Description(
        "Rename or move a file on disk. Does not update references in other files — " +
        "use find_in_files + patch_file to update imports after renaming.")]
    public async Task<string> RenameFile(
        [Description("Current absolute file path.")] string old_filename,
        [Description("New absolute file path.")] string new_filename,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                string? oldPath = ResolveFilePath(old_filename);
                if (oldPath == null || !File.Exists(oldPath))
                    return BuildFileNotFoundMessage("rename_file", old_filename);

                // Resolve new path: absolute → use as-is; relative → WorkingDirectory.
                string newPath = Path.IsPathRooted(new_filename)
                    ? new_filename
                    : Path.Combine(_svc.WorkingDirectory, new_filename);

                if (File.Exists(newPath))
                    return $"rename_file: destination already exists — {newPath}. Delete it first or choose a different name.";

                // Snapshot the old file before the rename so diff history is preserved.
                _svc.TrySnapshot(oldPath);

                string? newDir = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(newDir) && !Directory.Exists(newDir))
                    Directory.CreateDirectory(newDir);

                File.Move(oldPath, newPath);

                // Move snapshot from old path to new path.
                _svc.MoveSnapshot(oldPath, newPath);

                // Invalidate old cache entry; new entry will be populated on next read.
                string cacheKey = Path.GetFullPath(oldPath);
                _svc.FileCache.Invalidate(cacheKey);

                return $"rename_file: {oldPath} → {newPath}";
            }
            catch (Exception ex)
            {
                return $"[rename_file error] {old_filename}: {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "save_memory")]
    [Description(
        "Save project knowledge that should persist across sessions. Topics are overwritten " +
        "if they already exist — include all relevant content, not just additions.")]
    public async Task<string> SaveMemory(
        [Description("Slug for the topic (e.g., 'auth-system', 'build-quirks').")] string topic,
        [Description("The knowledge to save — conventions, insights, patterns.")] string content,
        [Description("Short one-line description for the memory index.")] string? description = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                _svc.Memory.SaveTopic(topic, content, description);
                string sanitized = MemoryManager.SanitizeSlug(topic);
                string desc      = string.IsNullOrEmpty(description) ? sanitized : description;
                return $"save_memory: saved topic [{sanitized}] — {desc}";
            }
            catch (Exception ex)
            {
                return $"[save_memory error] {topic}: {ex.Message}";
            }
        }, cancellationToken);
    }

    // ── Phase D: shell / streaming tools ────────────────────────────────────

    [McpServerTool(Name = "run_shell")]
    [Description(
        "Execute a shell command and return its output. Commands run via PowerShell with a " +
        "120-second timeout. Use this for git commands and operations no other tool covers. " +
        "Do not use run_shell to list or search files — use list_files or find_in_files instead. " +
        "Progress notifications stream output lines to the client during long-running commands.")]
    public async Task<string> RunShell(
        [Description("The shell command to execute.")] string command,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                IProgress<ShellOutputLine>? bridgedProgress = progress != null
                    ? new Progress<ShellOutputLine>(line =>
                        progress.Report(new ProgressNotificationValue { Progress = 0, Message = line.Line }))
                    : null;

                var (output, exitCode) = await _svc.Shell.ExecuteAsync(
                    command, cancellationToken, onLine: bridgedProgress);

                return CapShellOutput(output, exitCode);
            }
            catch (Exception ex)
            {
                return $"[run_shell error] {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "run_build")]
    [Description(
        "Run the project build command. Call this after ANY code change (patch_file or create_file). " +
        "The build command is auto-detected from the working directory: VSIX projects (detected via " +
        ".vsixmanifest) use MSBuild; other projects use dotnet build against the first .sln/.slnx found. " +
        "Progress notifications stream build output to the client. No parameters needed.")]
    public async Task<string> RunBuild(
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                string buildCommand = DetectBuildCommand();

                IProgress<ShellOutputLine>? bridgedProgress = progress != null
                    ? new Progress<ShellOutputLine>(line =>
                        progress.Report(new ProgressNotificationValue { Progress = 0, Message = line.Line }))
                    : null;

                var (output, exitCode) = await _svc.Shell.ExecuteAsync(
                    buildCommand, cancellationToken, onLine: bridgedProgress);

                return CapShellOutput(output, exitCode);
            }
            catch (Exception ex)
            {
                return $"[run_build error] {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "run_tests")]
    [Description(
        "Run dotnet test and return test output. Use run_tests after making changes to verify " +
        "correctness. Omit project to run all tests in the working directory. " +
        "Progress notifications stream test output to the client during the run.")]
    public async Task<string> RunTests(
        [Description("Project file name (e.g., 'MyProject.csproj'). Omit to run all tests.")] string? project = null,
        [Description("Test filter expression (e.g., 'FullyQualifiedName~SomeTest').")] string? filter = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                string testCommand = BuildTestCommand(project, filter);

                IProgress<ShellOutputLine>? bridgedProgress = progress != null
                    ? new Progress<ShellOutputLine>(line =>
                        progress.Report(new ProgressNotificationValue { Progress = 0, Message = line.Line }))
                    : null;

                var (output, exitCode) = await _svc.Shell.ExecuteAsync(
                    testCommand, cancellationToken, onLine: bridgedProgress);

                return CapShellOutput(output, exitCode);
            }
            catch (Exception ex)
            {
               return $"[run_tests error] {ex.Message}";
            }
        }, cancellationToken);
    }

    // ── Phase D: web_search, web_fetch ──────────────────────────────────────

    [McpServerTool(Name = "web_search")]
    [Description(
        "Search the web via the local SearXNG instance. Returns a ranked list of results " +
        "with title, URL, and snippet. Use for looking up documentation, APIs, error messages, " +
        "or any information that requires current web content.")]
    public async Task<string> WebSearch(
        [Description("Search query string.")] string query,
        [Description("Maximum number of results to return (default 10, max 20).")] int? max_results = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                int limit = Math.Min(max_results ?? 10, 20);
                string searxngUrl = Environment.GetEnvironmentVariable("DEVMIND_SEARCH_URL")
                    ?? "http://vard-nas:8180";
                string url = $"{searxngUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json&language=en&safesearch=0";

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.Add("User-Agent", "DevMind/1.0");
                var response = await http.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var results = doc.RootElement.GetProperty("results");

                var sb = new StringBuilder();
                sb.AppendLine($"web_search results for \"{query}\":");
                int count = 0;
                foreach (var result in results.EnumerateArray())
                {
                    if (count >= limit) break;
                    string title   = result.TryGetProperty("title",   out var t) ? t.GetString() ?? "" : "";
                    string resUrl  = result.TryGetProperty("url",     out var u) ? u.GetString() ?? "" : "";
                    string snippet = result.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    sb.AppendLine($"\n[{count + 1}] {title}");
                    sb.AppendLine($"    URL: {resUrl}");
                    if (!string.IsNullOrWhiteSpace(snippet))
                        sb.AppendLine($"    {snippet.Trim()}");
                    count++;
                }
                if (count == 0)
                    return $"web_search: no results for \"{query}\"";

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"[web_search error] {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "web_fetch")]
    [Description(
        "Fetch a URL and return its content as clean text. HTML is stripped to readable text. " +
        "Use for reading documentation pages, GitHub files, API references, or vendor support articles. " +
        "Auth-required pages will return an error or redirect — add credentials to the fetcher config if needed.")]
    public async Task<string> WebFetch(
        [Description("URL to fetch.")] string url,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                string fetcherUrl = Environment.GetEnvironmentVariable("DEVMIND_FETCH_URL")
                    ?? "http://vard-nas:8181";
                string endpoint = $"{fetcherUrl.TrimEnd('/')}/fetch";

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
                var payload = new StringContent(
                    $"{{\"url\":\"{url}\"}}",
                    Encoding.UTF8,
                    "application/json");

                var response = await http.PostAsync(endpoint, payload, cancellationToken);
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                string content = doc.RootElement.GetProperty("content").GetString() ?? "";

                if (string.IsNullOrWhiteSpace(content))
                    return $"[web_fetch] No content extracted from {url}";

                // Cap at 8000 chars to avoid flooding context.
                const int Cap = 8000;
                bool capped = content.Length > Cap;
                string output = capped ? content.Substring(0, Cap) : content;
                return capped
                    ? $"{output}\n\n[web_fetch: content truncated at {Cap} chars]"
                    : output;
            }
            catch (Exception ex)
            {
                return $"[web_fetch error] {ex.Message}";
            }
       }, cancellationToken);
    }

    // ── Phase D: ssh_exec ────────────────────────────────────────────────────

    [McpServerTool(Name = "ssh_exec")]
    [Description(
        "Execute a command on a remote host via SSH. Hosts can be defined by alias in the " +
        "DEVMIND_SSH_HOSTS environment variable, or specified directly as user@host. " +
        "Returns stdout and stderr combined, capped at 4000 lines. " +
        "Uses key-based authentication only — no passwords.")]
    public async Task<string> SshExec(
        [Description("Host alias from DEVMIND_SSH_HOSTS config, or user@host directly.")] string host,
        [Description("Command to execute on the remote host.")] string command,
        [Description("Timeout in seconds (default 60).")] int? timeout_seconds = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                int timeoutSecs = timeout_seconds ?? 60;

                // Resolve host config — alias lookup first, then parse user@host.
                string remoteHost, remoteUser, keyPath;
                if (_svc.SshHosts.TryGetValue(host, out var cfg))
                {
                    remoteHost = cfg.Host;
                    remoteUser = cfg.User;
                    keyPath    = cfg.Key;
                }
                else if (host.Contains('@'))
                {
                    var parts  = host.Split('@', 2);
                    remoteUser = parts[0];
                    remoteHost = parts[1];
                    keyPath    = "~/.ssh/id_rsa";
                }
                else
                {
                    return $"[ssh_exec] Unknown host alias \"{host}\". " +
                           "Define it in DEVMIND_SSH_HOSTS or use user@host format.";
                }

                // Expand ~ in key path.
                string expandedKey = keyPath.StartsWith("~", StringComparison.Ordinal)
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        keyPath.Substring(1).TrimStart('/', '\\'))
                    : keyPath;

                if (!File.Exists(expandedKey))
                    return $"[ssh_exec] SSH key not found: {expandedKey}";

                var keyFile        = new PrivateKeyFile(expandedKey);
                var authMethod     = new PrivateKeyAuthenticationMethod(remoteUser, keyFile);
                var connectionInfo = new ConnectionInfo(remoteHost, remoteUser, authMethod);
                connectionInfo.Timeout = TimeSpan.FromSeconds(timeoutSecs);

               using var client = new SshClient(connectionInfo);
                await Task.Run(() => client.Connect(), cancellationToken);

                using var cmd = client.CreateCommand(command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSecs);

                var result = await Task.Run(() =>
                {
                    string stdout = cmd.Execute();
                    return (stdout, cmd.Error, cmd.ExitStatus);
                }, cancellationToken);

                client.Disconnect();

                string combined = result.stdout;
                if (!string.IsNullOrEmpty(result.Error))
                    combined += (combined.Length > 0 ? "\n" : "") + result.Error;

               // Cap output at 4000 lines / 50 KB.
                const int MaxLines = 4_000;
                const int MaxBytes = 50_000;
                string prefix = $"[ssh_exec {remoteUser}@{remoteHost}] $ {command}\n";

                if (string.IsNullOrWhiteSpace(combined))
                    return $"[ssh_exec] Command completed with exit code {result.ExitStatus} (no output)";

                string[] lines = combined.Split('\n');
                bool linesCapped = lines.Length > MaxLines;
                string working = linesCapped
                    ? string.Join("\n", lines, 0, MaxLines)
                    : combined;

                byte[] bytes = Encoding.UTF8.GetBytes(working);
                bool bytesCapped = bytes.Length > MaxBytes;
                if (bytesCapped)
                {
                    int cutAt = MaxBytes;
                    while (cutAt > 0 && bytes[cutAt] != (byte)'\n') cutAt--;
                    working = Encoding.UTF8.GetString(bytes, 0, cutAt > 0 ? cutAt : MaxBytes);
                }

                var sb = new StringBuilder(prefix);
                sb.Append(working);
                if (linesCapped)
                    sb.Append($"\n[output truncated — {lines.Length} lines total, showing first {MaxLines}]");
                else if (bytesCapped)
                    sb.Append($"\n[output truncated — {bytes.Length} bytes total, showing first {MaxBytes} bytes]");

                return sb.ToString();
            }
            catch (OperationCanceledException)
            {
                return "[ssh_exec] Cancelled.";
            }
            catch (Exception ex)
            {
                return $"[ssh_exec error] {ex.Message}";
            }
        }, cancellationToken);
   }

    // ── Phase F: Git operations ──

   [McpServerTool(Name = "git_commit")]
    [Description(
        "Stage files and create a git commit in the working directory. " +
        "Stages all changes by default, or specific files if provided. " +
        "Pass \"auto\" as the message to generate one from the staged diff. " +
        "Optionally pushes to a named remote after committing. " +
        "Uses conventional commit format: type(scope): description.")]
    public async Task<string> GitCommit(
        [Description("Commit message. Pass \"auto\" to generate from staged diff.")] string message,
        [Description("Files to stage. If omitted, stages all changes (-A).")] string[]? files = null,
        [Description("Remote name to push to after committing (e.g. \"origin\", \"nas\"). Omit to skip push.")] string? remote = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                // Find git root and set shell working directory.
                string gitRoot = ContextEngine.FindGitRoot(_svc.WorkingDirectory);
                if (gitRoot == null)
                    return "[git_commit] Not a git repository.";

                string savedDir = _svc.Shell.WorkingDirectory;
                _svc.Shell.ChangeDirectory(gitRoot);

                try
                {
                    // Stage files.
                    string stageArgs = (files != null && files.Length > 0)
                        ? string.Join(" ", files.Select(f => $"\"{f}\""))
                        : "-A";
                    var (stageOutput, stageExitCode) = await _svc.Shell.ExecuteAsync($"git add {stageArgs}", cancellationToken);
                    if (stageExitCode != 0)
                        return $"[git_commit] git add failed:\n{stageOutput}";

                    // Check what's staged.
                    var (statusOutput, statusExitCode) = await _svc.Shell.ExecuteAsync("git status --short", cancellationToken);
                    string status = statusOutput?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(status))
                        return "[git_commit] Nothing to commit — working tree clean.";

                    // Count staged vs unstaged.
                    var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var staged   = lines.Where(l => l.Length >= 2 && l[0] != ' ' && l[0] != '?').ToList();
                    var unstaged = lines.Where(l => l.Length >= 2 && (l[0] == ' ' || l[0] == '?')).ToList();

                    if (staged.Count == 0)
                        return $"[git_commit] Nothing staged to commit.\nUnstaged changes:\n{string.Join("\n", unstaged)}";

                    // Auto-generate message if requested.
                    string commitMessage = message;
                    if (message.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        var changedFiles = staged.Select(l => l.Substring(2).Trim()).ToList();
                        string scope = changedFiles.Count == 1
                            ? Path.GetFileNameWithoutExtension(changedFiles[0])
                            : "multiple";
                        commitMessage = $"chore({scope}): update {string.Join(", ", changedFiles.Take(3))}{(changedFiles.Count > 3 ? $" and {changedFiles.Count - 3} more" : "")}";
                    }

                    // Commit.
                    string safeMessage = commitMessage.Replace("\"", "\\\"");
                    var (commitOutput, commitExitCode) = await _svc.Shell.ExecuteAsync($"git commit -m \"{safeMessage}\"", cancellationToken);
                    if (commitExitCode != 0)
                        return $"[git_commit] git commit failed:\n{commitOutput}";

                    var sb = new StringBuilder();
                    sb.AppendLine($"[git_commit] Committed {staged.Count} file(s): {commitMessage}");
                    sb.AppendLine(commitOutput?.Trim());

                    if (unstaged.Count > 0)
                        sb.AppendLine($"\nNote: {unstaged.Count} unstaged change(s) not included:\n{string.Join("\n", unstaged.Take(5))}{(unstaged.Count > 5 ? $"\n...and {unstaged.Count - 5} more" : "")}");

                    // Optional push.
                    if (!string.IsNullOrWhiteSpace(remote))
                    {
                        sb.AppendLine($"\nPushing to {remote}...");
                        var (pushOutput, pushExitCode) = await _svc.Shell.ExecuteAsync($"git push {remote}", cancellationToken);
                        sb.AppendLine(pushExitCode == 0
                            ? $"Pushed to {remote} successfully."
                            : $"Push failed:\n{pushOutput}");
                    }

                    return sb.ToString().TrimEnd();
                }
                finally
                {
                    _svc.Shell.ChangeDirectory(savedDir);
                }
            }
            catch (Exception ex)
            {
                return $"[git_commit error] {ex.Message}";
            }
       }, cancellationToken);
    }

    // ── Phase D: clip_read, clip_write, open_file, http_request ─────────────

    [McpServerTool(Name = "clip_read")]
    [Description(
        "Read the current contents of the Windows clipboard. " +
        "Useful for grabbing URLs, error messages, or code snippets the user has copied.")]
    public async Task<string> ClipRead(
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
               var (output, _) = await _svc.Shell.ExecuteAsync(
                    "powershell -NoProfile -Command \"Get-Clipboard\"",
                    cancellationToken);
                string text = output?.Trim() ?? "";
                return string.IsNullOrEmpty(text)
                    ? "[clip_read] Clipboard is empty."
                    : text;
            }
            catch (Exception ex)
            {
                return $"[clip_read error] {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "clip_write")]
    [Description(
        "Write text to the Windows clipboard. " +
        "Useful for placing generated code, URLs, or other output where the user can paste it.")]
    public async Task<string> ClipWrite(
        [Description("Text to write to the clipboard.")] string text,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
               string escaped = text.Replace("\"", "`\"").Replace("'", "''");
                var (writeOutput, exitCode) = await _svc.Shell.ExecuteAsync(
                    $"powershell -NoProfile -Command \"Set-Clipboard -Value '{escaped}'\"",
                    cancellationToken);
                return exitCode == 0
                    ? $"[clip_write] Copied {text.Length} characters to clipboard."
                    : $"[clip_write] Failed: {writeOutput}";
            }
            catch (Exception ex)
            {
                return $"[clip_write error] {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "open_file")]
    [Description(
        "Open a file or URL in its default application (VS Code for code files, browser for URLs, etc). " +
        "Useful for opening files for manual review after editing, or launching a browser to a local endpoint.")]
    public async Task<string> OpenFile(
        [Description("Absolute file path or URL to open.")] string path,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
               string target = path.StartsWith("http://", StringComparison.Ordinal) || path.StartsWith("https://", StringComparison.Ordinal)
                    ? path
                    : ResolveFilePath(path) ?? path;
                var (openOutput, openExitCode) = await _svc.Shell.ExecuteAsync(
                    $"powershell -NoProfile -Command \"Start-Process '{target}'\"",
                    cancellationToken);
                return openExitCode == 0
                    ? $"[open_file] Opened: {target}"
                    : $"[open_file] Failed: {openOutput}";
            }
            catch (Exception ex)
            {
                return $"[open_file error] {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "http_request")]
    [Description(
        "Make an HTTP request to a local or remote endpoint. " +
        "Endpoints can be defined by alias in DEVMIND_HTTP_ENDPOINTS, or use a full URL. " +
        "Useful for testing APIs, querying local services (Qwen, Parsely, TTA), or hitting the fetcher/SearXNG directly.")]
    public async Task<string> HttpRequest(
        [Description("Endpoint alias from DEVMIND_HTTP_ENDPOINTS, or full URL.")] string endpoint,
        [Description("Path to append to the base URL (e.g. \"/v1/models\"). Ignored if endpoint is a full URL.")] string? path = null,
        [Description("HTTP method: GET, POST, PUT, DELETE (default GET).")] string? method = null,
        [Description("Request body as a JSON string (for POST/PUT).")] string? body = null,
        [Description("Timeout in seconds (default 30).")] int? timeout_seconds = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                string httpMethod = (method ?? "GET").ToUpperInvariant();
                int timeoutSecs   = timeout_seconds ?? 30;

                // Resolve endpoint alias or use as full URL.
                string baseUrl;
                if (_svc.HttpEndpoints.TryGetValue(endpoint, out var alias))
                    baseUrl = alias;
                else if (endpoint.StartsWith("http://", StringComparison.Ordinal) || endpoint.StartsWith("https://", StringComparison.Ordinal))
                    baseUrl = endpoint;
                else
                    return $"[http_request] Unknown endpoint alias \"{endpoint}\". " +
                           "Define it in DEVMIND_HTTP_ENDPOINTS or use a full URL.";

                string url = string.IsNullOrWhiteSpace(path)
                    ? baseUrl.TrimEnd('/')
                    : $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSecs) };
                HttpResponseMessage response;

                if (httpMethod == "GET" || httpMethod == "DELETE")
                {
                    var request = new HttpRequestMessage(
                        httpMethod == "GET" ? HttpMethod.Get : HttpMethod.Delete, url);
                    response = await http.SendAsync(request, cancellationToken);
                }
                else
                {
                    var content = new StringContent(
                        body ?? "{}", Encoding.UTF8, "application/json");
                    response = httpMethod == "POST"
                        ? await http.PostAsync(url, content, cancellationToken)
                        : await http.PutAsync(url, content, cancellationToken);
                }

                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                // Pretty-print JSON if possible.
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    responseBody = JsonSerializer.Serialize(doc.RootElement,
                        new JsonSerializerOptions { WriteIndented = true });
                }
                catch { }

                // Cap output.
                const int Cap = 4000;
                bool capped = responseBody.Length > Cap;
                string output = capped ? responseBody.Substring(0, Cap) : responseBody;

                return $"[http_request] {httpMethod} {url} → {(int)response.StatusCode} {response.StatusCode}\n\n{output}" +
                       (capped ? $"\n\n[truncated at {Cap} chars]" : "");
            }
            catch (Exception ex)
            {
                return $"[http_request error] {ex.Message}";
            }
        }, cancellationToken);
    }

    // ── Phase E: LSP tools (C# via csharp-ls, TS/JS via typescript-language-server) ──

    [McpServerTool(Name = "get_diagnostics")]
    [Description(
        "Return errors and warnings for a source file without a full build. " +
        "Uses the language server for the file type (.cs → csharp-ls, .ts/.tsx/.js/.jsx → typescript-language-server). " +
        "Prefer this over run_build when checking a single file.")]
    public async Task<string> GetDiagnostics(
        [Description("Absolute path to a .cs, .ts, .tsx, .js, or .jsx file.")] string filename,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            if (_svc.Lsp == null)
                return "[get_diagnostics] LSP is disabled (set DEVMIND_LSP_ENABLED=true to enable).";

            try
            {
                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("get_diagnostics", filename);

                return await _svc.Lsp.GetDiagnosticsAsync(fullPath, cancellationToken);
            }
            catch (Exception ex)
            {
                return $"[get_diagnostics error] {filename}: {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "go_to_definition")]
    [Description(
        "Navigate to the definition of the symbol at the given position. " +
        "Returns file:line:column for each definition location. Line and character are 1-based.")]
    public async Task<string> GoToDefinition(
        [Description("Absolute path to a supported source file (.cs, .ts, .tsx, .js, .jsx).")] string filename,
        [Description("1-based line number.")] int line,
        [Description("1-based character (column) position.")] int character,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            if (_svc.Lsp == null)
                return "[go_to_definition] LSP is disabled (set DEVMIND_LSP_ENABLED=true to enable).";

            try
            {
                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("go_to_definition", filename);

                return await _svc.Lsp.GoToDefinitionAsync(fullPath, line, character, cancellationToken);
            }
            catch (Exception ex)
            {
                return $"[go_to_definition error] {filename}: {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "find_references")]
    [Description(
        "Find all references to the symbol at the given position. " +
        "Semantic search — prefer over find_in_files when locating usages of a type or member. " +
        "Line and character are 1-based.")]
    public async Task<string> FindReferences(
        [Description("Absolute path to a supported source file (.cs, .ts, .tsx, .js, .jsx).")] string filename,
        [Description("1-based line number.")] int line,
        [Description("1-based character (column) position.")] int character,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            if (_svc.Lsp == null)
                return "[find_references] LSP is disabled (set DEVMIND_LSP_ENABLED=true to enable).";

            try
            {
                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("find_references", filename);

                return await _svc.Lsp.FindReferencesAsync(fullPath, line, character, cancellationToken);
            }
            catch (Exception ex)
            {
                return $"[find_references error] {filename}: {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "hover")]
    [Description(
        "Return type signature and documentation for the symbol at the given position. " +
        "Line and character are 1-based.")]
    public async Task<string> Hover(
        [Description("Absolute path to a supported source file (.cs, .ts, .tsx, .js, .jsx).")] string filename,
        [Description("1-based line number.")] int line,
        [Description("1-based character (column) position.")] int character,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            if (_svc.Lsp == null)
                return "[hover] LSP is disabled (set DEVMIND_LSP_ENABLED=true to enable).";

            try
            {
                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("hover", filename);

                return await _svc.Lsp.HoverAsync(fullPath, line, character, cancellationToken);
            }
            catch (Exception ex)
            {
                return $"[hover error] {filename}: {ex.Message}";
            }
        }, cancellationToken);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a filename to a full absolute path.
    /// Priority: (1) already absolute and exists; (2) hint-relative to WorkingDirectory;
    /// (3) basename recursive search under WorkingDirectory (noise paths excluded).
    /// Returns null if the file cannot be located.
    /// </summary>
    private string? ResolveFilePath(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;

        // Absolute path that exists — use directly.
        if (Path.IsPathRooted(filename) && File.Exists(filename))
            return filename;

        string wd = _svc.WorkingDirectory;

        // Hint-relative: combine with WorkingDirectory.
        string byHint = Path.Combine(wd, filename.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(byHint)) return byHint;

        // Basename-only fallback: recursive search.
        string fileNameOnly = Path.GetFileName(filename.Replace('\\', '/'));
        if (string.IsNullOrEmpty(fileNameOnly)) return null;

        try
        {
            string[] found = Directory.GetFiles(wd, fileNameOnly, SearchOption.AllDirectories);
            string[] clean = found.Where(f => !ContextEngine.IsNoisePath(f)).ToArray();
            if (clean.Length == 1) return clean[0];
            if (clean.Length > 1)
            {
                // Prefer the path whose suffix most closely matches the hint.
                string normalized = filename.Replace('\\', '/');
                return clean.FirstOrDefault(f =>
                    f.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
                    ?? clean[0];
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Loads a file into FileCache if it is not already present.
    /// </summary>
    private void EnsureCached(string fileNameOnly, string fullPath)
    {
        // Key the cache by the full normalized path, not the basename, so same-named
        // files in different directories don't collide. fileNameOnly is retained in the
        // signature for call-site compatibility (display) but is not used as the key.
        string cacheKey = Path.GetFullPath(fullPath);
        if (_svc.FileCache.Contains(cacheKey)) return;
        try
        {
            string content = File.ReadAllText(fullPath);
            _svc.FileCache.Store(cacheKey, content);
        }
        catch { }
    }

    /// <summary>
    /// Handles git log / git diff variants of read_file by delegating to ShellRunner.
    /// Called from within ReadFile which already holds the gate — no re-entry needed.
    /// </summary>
    private async Task<string> ReadGitAsync(
        string filename, int startLine, CancellationToken cancellationToken)
    {
        string gitRoot = ContextEngine.FindGitRoot(_svc.WorkingDirectory);
        if (gitRoot == null)
            return "[read_file] git: not a git repository";

        string command, header;

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
                count = 10;
                if (!string.IsNullOrEmpty(countPart)) int.TryParse(countPart, out count);
            }
            count   = Math.Max(1, Math.Min(count, 50));
            command = $"git log --oneline --no-decorate -{count}";
            header  = $"[read_file] git log (last {count} commits)";
        }
        else if (filename.StartsWith("git diff", StringComparison.OrdinalIgnoreCase))
        {
            string diffArgs = filename.Substring("git diff".Length).Trim();
            command = string.IsNullOrEmpty(diffArgs) ? "git diff" : $"git diff {diffArgs}";
            header  = string.IsNullOrEmpty(diffArgs)
                ? "[read_file] git diff (working changes)"
                : $"[read_file] git diff {diffArgs}";
        }
        else
        {
            return $"[read_file] Unrecognized git command: {filename}";
        }

        // Temporarily change ShellRunner working directory to the git root.
        string savedDir = _svc.Shell.WorkingDirectory;
        _svc.Shell.ChangeDirectory(gitRoot);
        string output;
        int exitCode;
        try
        {
            (output, exitCode) = await _svc.Shell.ExecuteAsync(command, cancellationToken);
        }
        finally
        {
            _svc.Shell.ChangeDirectory(savedDir);
        }

        if (exitCode != 0)
            return $"{header}\n(error — exit code {exitCode})\n{output}";

        const int MaxDiffLines = 500;
        string[] outputLines = output.Split('\n');
        string truncatedOutput = outputLines.Length > MaxDiffLines
            ? string.Join("\n", outputLines.Take(MaxDiffLines))
              + $"\n[... {outputLines.Length - MaxDiffLines} lines omitted — use read_file with 'git diff <filename>' to narrow scope]"
            : output;

        if (string.IsNullOrWhiteSpace(truncatedOutput)) truncatedOutput = "(no output)";

        return $"{header}\n```\n{truncatedOutput}\n```\n\n";
    }

    /// <summary>
    /// Returns a descriptive "file not found" message, listing available .cs files in
    /// WorkingDirectory so the caller can identify the correct filename.
    /// </summary>
    private string BuildFileNotFoundMessage(string tool, string filename)
    {
        const int MaxFiles = 50;
        string searchDir = _svc.WorkingDirectory;

        List<string>? csFiles = null;
        try
        {
            csFiles = Directory.GetFiles(searchDir, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetFileName(f) ?? f)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { }

        if (csFiles == null || csFiles.Count == 0)
            return $"{tool}: file not found — {filename}";

        var sb = new StringBuilder();
        sb.AppendLine($"{tool}: file not found — {filename}");
        sb.AppendLine("C# files in project root:");
        int shown = Math.Min(csFiles.Count, MaxFiles);
        for (int i = 0; i < shown; i++) sb.AppendLine($"  {csFiles[i]}");
        if (csFiles.Count > MaxFiles) sb.AppendLine($"  ... and {csFiles.Count - MaxFiles} more");
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Truncates shell output to at most 1000 lines or 50 KB (whichever is hit first),
    /// and prepends the exit code. Truncation is performed at a newline boundary to
    /// avoid splitting UTF-8 sequences.
    /// </summary>
    private static string CapShellOutput(string output, int exitCode)
    {
        const int MaxLines = 1_000;
        const int MaxBytes = 50_000;

        string prefix = $"[exit code: {exitCode}]\n";

        if (string.IsNullOrEmpty(output))
            return prefix + "(no output)";

        // ── Line cap ─────────────────────────────────────────────────────────
        string[] lines     = output.Split('\n');
        int      totalLines = lines.Length;
        bool     linesCapped = totalLines > MaxLines;
        string   working     = linesCapped
            ? string.Join("\n", lines, 0, MaxLines)
            : output;

        // ── Byte cap (truncate at last newline before MaxBytes) ───────────────
        byte[] bytes      = Encoding.UTF8.GetBytes(working);
        int    totalBytes = bytes.Length;
        bool   bytesCapped = totalBytes > MaxBytes;
        if (bytesCapped)
        {
            // Find the last newline before the byte limit so we don't split a line.
            int cutAt = MaxBytes;
            while (cutAt > 0 && bytes[cutAt] != (byte)'\n') cutAt--;
            working = Encoding.UTF8.GetString(bytes, 0, cutAt > 0 ? cutAt : MaxBytes);
        }

        var sb = new StringBuilder(prefix);
        sb.Append(working);

        if (linesCapped)
            sb.Append($"\n[output truncated — {totalLines} lines total, showing first {MaxLines}]");
        else if (bytesCapped)
            sb.Append($"\n[output truncated — {totalBytes} bytes total, showing first {MaxBytes} bytes]");

        return sb.ToString();
    }

    /// <summary>
    /// Auto-detects the correct build command for the current working directory.
    /// VSIX projects (detected via *.vsixmanifest) use MSBuild; others use dotnet build.
    /// </summary>
    private string DetectBuildCommand()
    {
        string wd = _svc.WorkingDirectory;

        // Detect VSIX: search for *.vsixmanifest under WorkingDirectory.
        string? vsixManifest = null;
        try
        {
            var candidates = Directory.GetFiles(wd, "*.vsixmanifest", SearchOption.AllDirectories)
                .Where(f => !ContextEngine.IsNoisePath(f))
                .ToArray();
            if (candidates.Length > 0) vsixManifest = candidates[0];
        }
        catch { }

        if (vsixManifest != null)
        {
            // VSIX project — use MSBuild.
            // Find the first .sln/.slnx in WorkingDirectory or one level up.
            string? solution = FindSolutionFile(wd);
            string  target   = solution ?? wd;
            string  msbuild  = LoopHelpers.FindMSBuildPath();
            string  invoke   = msbuild.Contains(" ") ? $"& \"{msbuild}\"" : msbuild;

            // If MSBuild was only found as the bare "msbuild" fallback and this is a non-VS
            // environment (no VSINSTALLDIR, no vswhere hit), fall back to dotnet build with
            // an explanatory note rather than a silent failure.
            if (msbuild == "msbuild" &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VSINSTALLDIR")))
            {
                Console.Error.WriteLine(
                    "[McpServer] Warning: VSIX project detected but MSBuild not found via " +
                    "VSINSTALLDIR or vswhere. Falling back to dotnet build (may fail for VSIX).");
                return solution != null
                    ? $"dotnet build \"{solution}\" /p:DeployExtension=false"
                    : $"dotnet build \"{wd}\" /p:DeployExtension=false";
            }

            return solution != null
                ? $"{invoke} \"{solution}\" /p:DeployExtension=false /verbosity:minimal"
                : $"{invoke} \"{wd}\" /p:DeployExtension=false /verbosity:minimal";
        }
        else
        {
            // Detect Node/TypeScript: search for package.json.
            string packageJsonPath = Path.Combine(wd, "package.json");
            if (File.Exists(packageJsonPath))
            {
                bool isBun = File.Exists(Path.Combine(wd, "bun.lockb")) || 
                             File.Exists(Path.Combine(wd, "bunfig.toml"));
                
                if (!isBun)
                {
                    try
                    {
                        string content = File.ReadAllText(packageJsonPath);
                        if (content.Contains("\"packageManager\": \"bun@"))
                        {
                            isBun = true;
                        }
                    }
                    catch { }
                }
                
                return isBun ? "bun run build" : "npm run build";
            }

            // .NET project — use dotnet build.
            string? solution = FindSolutionFile(wd);
            if (solution != null)
            {
                return $"dotnet build \"{solution}\"";
            }

            throw new Exception("Could not auto-detect a build system. No .vsixmanifest, package.json, or .sln/.slnx file found.");
        }
    }

    /// <summary>
    /// Searches <paramref name="dir"/> for a *.sln or *.slnx file, then one level up.
    /// Returns the first match, or null if none found.
    /// </summary>
    private static string? FindSolutionFile(string dir)
    {
        foreach (string pattern in new[] { "*.slnx", "*.sln" })
        {
            try
            {
                var hits = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
                if (hits.Length > 0) return hits[0];
            }
            catch { }
        }
        // One level up.
        string? parent = Path.GetDirectoryName(dir);
        if (parent != null && parent != dir)
        {
            foreach (string pattern in new[] { "*.slnx", "*.sln" })
            {
                try
                {
                    var hits = Directory.GetFiles(parent, pattern, SearchOption.TopDirectoryOnly);
                    if (hits.Length > 0) return hits[0];
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>
    /// Builds the dotnet test command string from optional project and filter arguments.
    /// </summary>
    private string BuildTestCommand(string? project, string? filter)
    {
        string wd = _svc.WorkingDirectory;
        var sb = new StringBuilder("dotnet test --no-build --verbosity normal");

        if (!string.IsNullOrWhiteSpace(project))
        {
            // Resolve project: absolute path → use as-is; bare name → search under WorkingDirectory.
            string projectPath;
            if (Path.IsPathRooted(project))
            {
                projectPath = project;
            }
            else
            {
                // Try hint-relative first.
                string candidate = Path.Combine(wd, project);
                if (File.Exists(candidate))
                {
                    projectPath = candidate;
                }
                else
                {
                    // Basename search.
                    string nameOnly = Path.GetFileName(project);
                    var found = Directory.GetFiles(wd, nameOnly, SearchOption.AllDirectories)
                        .Where(f => !ContextEngine.IsNoisePath(f))
                        .ToArray();
                    projectPath = found.Length > 0 ? found[0] : candidate;
                }
            }
            sb.Append($" \"{projectPath}\"");
        }

        if (!string.IsNullOrWhiteSpace(filter))
            sb.Append($" --filter \"{filter}\"");

       return sb.ToString();
    }

    /// <summary>
    /// Returns true for script file extensions where a UTF-8 BOM causes problems
    /// (cmd.exe, PowerShell, bash interpret the BOM bytes as part of the shebang/command).
    /// </summary>
    private static bool IsScriptFileExtension(string path)
    {
        string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
       return ext == ".cmd" || ext == ".bat" || ext == ".sh" || ext == ".ps1";
    }
}
