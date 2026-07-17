// File: DevMindTools.cs  v5.3
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
using System.Diagnostics;
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

/// <summary>One find/replace edit in a batched <c>patch_file</c> call.</summary>
public sealed class PatchEdit
{
    [Description("Exact text to find in the file (verbatim from read_file output).")]
    public string Find { get; set; } = "";

    [Description("Replacement text.")]
    public string Replace { get; set; } = "";
}

[McpServerToolType]
internal sealed class DevMindTools
{
    private readonly McpServices _svc;

    // Single shared HttpClient for the whole process — creating one per request leaks sockets
    // (connections linger in TIME_WAIT and can exhaust ephemeral ports under load). Its Timeout is
    // left infinite because it is process-wide; per-call deadlines are applied via a linked
    // CancellationTokenSource at each call site instead.
    private static readonly HttpClient _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

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
        [Description("Absolute directory to list under. Defaults to the server's working directory — pass this explicitly when unsure (GUI-launched servers have no meaningful default).")] string? root = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            const int Cap = 200;
            try
            {
                string searchDir = _svc.WorkingDirectory;
                if (!string.IsNullOrWhiteSpace(root))
                {
                    if (!Path.IsPathRooted(root))
                        return $"list_files: root must be an absolute path — got '{root}'.";
                    if (!Directory.Exists(root))
                        return $"list_files: root does not exist — {root}";
                    searchDir = Path.GetFullPath(root);
                }

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
        "Search a single file for lines matching a pattern (case-insensitive substring; " +
        "'|' separates OR alternatives; no other regex syntax). " +
        "Returns matching lines with 1-based line numbers, capped at 50 matches. " +
        "Use grep_file to locate code, then read_file with a targeted range, then patch_file.")]
    public async Task<string> GrepFile(
        [Description("Search text: case-insensitive substring; '|' separates OR alternatives; no other regex.")] string pattern,
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

                var matcher = SearchPattern.BuildMatcher(pattern);
                var matches = new List<(int lineNum, string lineText)>();
                for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
                {
                    string lineContent = _svc.FileCache.GetLineRange(cacheKey, lineNum, lineNum);
                    if (lineContent == null) continue;
                    if (matcher(lineContent))
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
        "Use grep_file when you already know which file to search. " +
        "Pattern is a case-insensitive SUBSTRING with one extension: '|' separates OR " +
        "alternatives (e.g. 'PdfGenerated|Exported' matches lines containing either). " +
        "Other regex syntax is NOT supported. When the server was launched without " +
        "--dir (e.g. by a GUI client like Claude Desktop), you MUST pass root, or the " +
        "search runs against the wrong directory and returns false 'no matches'.")]
    public async Task<string> FindInFiles(
        [Description("Search text: case-insensitive substring; '|' separates OR alternatives; no other regex.")] string pattern,
        [Description("Glob pattern to match files (e.g., '*.cs', 'Services/*.cs').")] string glob,
        [Description("Absolute directory to search under. Defaults to the server's working directory — pass this explicitly when unsure (GUI-launched servers have no meaningful default).")] string? root = null,
        [Description("1-based start line to restrict the search window within each file.")] int? start_line = null,
        [Description("1-based end line to restrict the search window within each file.")] int? end_line = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            const int MaxMatches = 100;
            try
            {
                string searchDir = _svc.WorkingDirectory;
                if (!string.IsNullOrWhiteSpace(root))
                {
                    if (!Path.IsPathRooted(root))
                        return $"find_in_files: root must be an absolute path — got '{root}'.";
                    if (!Directory.Exists(root))
                        return $"find_in_files: root does not exist — {root}";
                    searchDir = Path.GetFullPath(root);
                }
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

                var findMatcher = SearchPattern.BuildMatcher(pattern);
                var allMatches = new List<(string fileLabel, int lineNum, string lineText)>();
                bool hitCap    = false;

                foreach (string filePath in files)
                {
                    if (hitCap) break;
                    string fileNameOnly = Path.GetFileName(filePath);
                    string cacheKey     = Path.GetFullPath(filePath);

                    _svc.FileCache.InvalidateIfStale(cacheKey, cacheKey); // out-of-band writes
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
                        if (findMatcher(lineContent))
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

                var memoryMatcher = SearchPattern.BuildMatcher(pattern);
                var results = new List<(string topic, int lineNum, string lineText)>();

                foreach (var topic in topics)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string content = _svc.Memory.LoadTopic(topic);
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    var lines = content.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (memoryMatcher(lines[i]))
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

    // ── Document library (RAG) ──────────────────────────────────────────────
    // Config comes from the global devmind.json (libraryConnectionString /
    // libraryEmbeddingEndpoint) — same source as the TUI's /library command.

    [McpServerTool(Name = "library_add")]
    [Description(
        "Ingest a document into the RAG library so query_library (and this server's library_query) can retrieve it. " +
        "Accepts .md/.markdown/.txt/.docx (fast: chunked at paragraph boundaries, embedded verbatim — needs only the " +
        "embedding server) and .pdf (SLOW: vision notes per page range via the chat model — can take minutes; " +
        "needs chat AND embedding servers). Re-ingesting the same or a changed file replaces its prior chunks.")]
    public async Task<string> LibraryAdd(
        [Description("Path to the document (.pdf/.md/.markdown/.txt/.docx). Relative paths resolve against the working directory.")] string path,
        [Description("PDF only: pages per vision chunk (default 5). Ignored for text documents.")] int pages_per_chunk = 5,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                var config = TuiConfig.Load();
                if (string.IsNullOrWhiteSpace(config.LibraryConnectionString))
                    return "library_add: the library is not configured — set libraryConnectionString " +
                           "(SQL Server 2025+) in devmind.json.";

                string fullPath = Path.IsPathRooted(path)
                    ? path
                    : Path.GetFullPath(Path.Combine(_svc.WorkingDirectory, path));
                bool isPdf = fullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                if (!File.Exists(fullPath))
                    return $"library_add: file not found — {fullPath}";
                if (!isPdf && !TextDocumentReader.IsTextDocument(fullPath))
                    return $"library_add: unsupported type — {Path.GetExtension(fullPath)}. " +
                           "Supported: .pdf, .md, .markdown, .txt, .docx.";

                // PDFs need a chat model for the vision pass; text docs never touch it.
                string? endpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT")?.Trim();
                if (string.IsNullOrEmpty(endpoint)) endpoint = "http://127.0.0.1:8080/v1";
                string apiKey = Environment.GetEnvironmentVariable("DEVMIND_API_KEY") ?? "";

                var ingest = await DocumentLibrarian.IngestAsync(
                    new HeadlessOptions(), endpoint, apiKey,
                    config.LibraryEmbeddingEndpoint, config.LibraryConnectionString,
                    fullPath, pages_per_chunk > 0 ? pages_per_chunk : 5,
                    line => Console.Error.Write(line),
                    cancellationToken);

                string extent = isPdf
                    ? $"{ingest.Chunks} chunk(s), {ingest.Pages} page(s)"
                    : $"{ingest.Chunks} section(s)";
                return $"library_add: {Path.GetFileName(fullPath)} ingested and searchable ({extent}). " +
                       "Verify with library_list or library_query.";
            }
            catch (OperationCanceledException)
            {
                return "[library_add] Cancelled.";
            }
            catch (Exception ex)
            {
                return $"[library_add error] {ex.Message} " +
                       "(is the embedding server running? PDFs also need the chat server)";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "library_list")]
    [Description(
        "List all documents in the RAG library with id, name, extent, chunk count, and ingest time. " +
        "Use to verify what reference material is available before relying on library queries.")]
    public async Task<string> LibraryList(CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                var config = TuiConfig.Load();
                if (string.IsNullOrWhiteSpace(config.LibraryConnectionString))
                    return "library_list: the library is not configured — set libraryConnectionString " +
                           "(SQL Server 2025+) in devmind.json.";

                var store = new LibraryStore(config.LibraryConnectionString);
                await store.EnsureSchemaAsync(cancellationToken);
                var docs = await store.ListDocumentsAsync(cancellationToken);
                if (docs.Count == 0)
                    return "library_list: the library is empty. Ingest with library_add.";

                var sb = new StringBuilder($"Library documents ({docs.Count}):\n");
                foreach (var d in docs)
                    sb.AppendLine($"  [{d.Id}] {d.Name} — {d.Pages} page(s), {d.ChunkCount} chunk(s), ingested {d.IngestedAtUtc:yyyy-MM-dd HH:mm}Z");
                return sb.ToString().TrimEnd();
            }
            catch (OperationCanceledException)
            {
                return "[library_list] Cancelled.";
            }
            catch (Exception ex)
            {
                return $"[library_list error] {ex.Message}";
            }
        }, cancellationToken);
    }

    [McpServerTool(Name = "library_query")]
    [Description(
        "Semantic (RAG) search over the ingested document library. Returns provenance-labelled excerpts " +
        "(document name + page/section range) ranked by relevance. Same retrieval the task agent's " +
        "query_library tool uses — handy for verifying ingested content actually answers a question.")]
    public async Task<string> LibraryQuery(
        [Description("Natural-language question to retrieve reference excerpts for.")] string question,
        [Description("Number of excerpts to retrieve (default 6).")] int top_k = 6,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            var config = TuiConfig.Load();
            return await DocumentLibrarian.QueryAsTextAsync(
                config.LibraryEmbeddingEndpoint, config.LibraryConnectionString,
                question, top_k, cancellationToken);
        }, cancellationToken);
    }

    [McpServerTool(Name = "query_db")]
    [Description(
        "Execute a SQL query against a named database connection. " +
        "Connections are defined in DEVMIND_DB_CONNECTIONS environment variable. " +
        "Read-only by default (SELECT only, enforced by an always-rolled-back transaction). " +
        "Single-statement DML writes (INSERT/UPDATE/DELETE, single statement, no DDL) are " +
        "permitted ONLY when allow_write=true AND the server has DEVMIND_DB_ALLOW_WRITE=1 — " +
        "writes run with ADO.NET session defaults (QUOTED_IDENTIFIER ON, unlike raw sqlcmd) " +
        "and are committed; the response reports rows affected. " +
        "Supports SQL Server, PostgreSQL, and SQLite — auto-detected from connection string. " +
        "Returns results as a formatted table, capped at 100 rows.")]
    public async Task<string> QueryDb(
        [Description("Connection alias from DEVMIND_DB_CONNECTIONS.")] string connection,
        [Description("SQL to execute (single statement).")] string query,
        [Description("Query timeout in seconds (default 30).")] int? timeout_seconds = null,
        [Description("Permit a single-statement DML write (requires DEVMIND_DB_ALLOW_WRITE=1 on the server; default false).")] bool? allow_write = null,
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

                bool writeRequested = allow_write == true;
                bool writeEnabled = Environment.GetEnvironmentVariable("DEVMIND_DB_ALLOW_WRITE")?.Trim() == "1";
                if (writeRequested && !writeEnabled)
                    return "[query_db] Blocked: allow_write requires DEVMIND_DB_ALLOW_WRITE=1 in the server's environment.";

                string trimmed = query.TrimStart().ToUpperInvariant();

                // DDL and EXEC are never permitted, write mode or not.
                string[] alwaysBlocked = { "DROP", "ALTER", "TRUNCATE", "CREATE", "EXEC ", "EXECUTE ", "MERGE", "GRANT", "REVOKE" };
                foreach (var keyword in alwaysBlocked)
                {
                    if (trimmed.StartsWith(keyword, StringComparison.Ordinal))
                        return $"[query_db] Blocked: {keyword.TrimEnd()} is not permitted (write mode allows single-statement INSERT/UPDATE/DELETE only).";
                }

                string[] dml = { "INSERT", "UPDATE", "DELETE" };
                bool isDml = dml.Any(k => trimmed.StartsWith(k, StringComparison.Ordinal));
                if (isDml && !writeRequested)
                    return "[query_db] Blocked: only SELECT queries are permitted without allow_write=true.";

                // Reject stacked statements: a ';' outside a single-quoted string literal can
                // smuggle a COMMIT that escapes the rollback enforcement below. Scan for ';'
                // outside single-quoted spans (treating '' as an escaped quote). Not a SQL
                // parser — just a quote-aware scan so a ';' inside a string value is ignored.
                bool inStringLiteral = false;
                for (int i = 0; i < query.Length; i++)
                {
                    char ch = query[i];
                    if (ch == '\'')
                    {
                        // Doubled '' inside a string is an escaped quote — stay in the string.
                        if (inStringLiteral && i + 1 < query.Length && query[i + 1] == '\'') { i++; continue; }
                        inStringLiteral = !inStringLiteral;
                    }
                    else if (ch == ';' && !inStringLiteral)
                    {
                        return "[query_db error] Stacked statements are not permitted.";
                    }
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

                    // ── REAL read-only enforcement ──────────────────────────────────────
                    // The keyword blocklist above is only a first-pass fast-fail. The actual
                    // guarantee that query_db cannot modify the database is this: run the query
                    // inside a transaction that is ALWAYS rolled back and NEVER committed, so any
                    // DML/DDL smuggled past the blocklist is undone. SQLite does not accept
                    // ReadCommitted, so it uses its default isolation; the rollback enforces
                    // read-only regardless of isolation level.
                    DbTransaction tx = dbConn is SqliteConnection
                        ? dbConn.BeginTransaction()
                        : dbConn.BeginTransaction(IsolationLevel.ReadCommitted);

                    // ── Gated write path ────────────────────────────────────────────
                    // Single-statement DML, double-gated (allow_write param AND
                    // DEVMIND_DB_ALLOW_WRITE=1). Runs with ADO.NET session defaults —
                    // QUOTED_IDENTIFIER ON — which is exactly the sqlcmd trap this
                    // path replaces. Committed explicitly; everything else below
                    // stays on the always-rollback read-only guarantee.
                    if (isDml && writeRequested)
                    {
                        try
                        {
                            using var writeCmd = dbConn.CreateCommand();
                            writeCmd.Transaction    = tx;
                            writeCmd.CommandText    = query;
                            writeCmd.CommandTimeout = timeoutSecs;
                            int affected = await writeCmd.ExecuteNonQueryAsync(cancellationToken);
                            tx.Commit();
                            return $"[query_db] {connection} → write committed, {affected} row(s) affected.";
                        }
                        catch (Exception writeEx)
                        {
                            try { tx.Rollback(); } catch { }
                            return $"[query_db error] write failed (rolled back): {writeEx.Message}";
                        }
                        finally
                        {
                            tx.Dispose();
                        }
                    }

                    try
                    {
                        using var cmd = dbConn.CreateCommand();
                        cmd.Transaction    = tx;
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
                    finally
                    {
                        // Never commit — roll back so the query cannot persist any change.
                        // THIS is the read-only guarantee, not the keyword blocklist above.
                        try { tx.Rollback(); } catch { /* connection may already be faulted/closing */ }
                        tx.Dispose();
                    }
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
        "Edit an existing file by replacing exact text. Provide EITHER a single find/replace, OR an " +
        "'edits' array to apply several replacements in ONE atomic patch — strongly preferred for " +
        "multi-site edits (one call instead of many, applied all-or-nothing). Find text must be copied " +
        "verbatim from read_file output, never reconstructed from memory. Whitespace-normalized matching " +
        "is applied (CRLF and indentation differences ignored). Always read_file first if you have not " +
        "seen the file.")]
    public async Task<string> PatchFile(
        [Description("Absolute file path.")] string filename,
        [Description("Exact text to find (verbatim from read_file output). Omit when using 'edits'.")] string? find = null,
        [Description("Replacement text. Omit when using 'edits'.")] string? replace = null,
        [Description("Batch of {find, replace} edits applied to this file in one atomic patch (all succeed or none). When provided, top-level find/replace are ignored.")] PatchEdit[]? edits = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                // Normalize inputs into a list of find/replace pairs.
                var pairs = new List<(string find, string replace)>();
                if (edits is { Length: > 0 })
                {
                    foreach (var e in edits)
                    {
                        if (e == null || string.IsNullOrEmpty(e.Find))
                            return "patch_file: failed — each entry in 'edits' must have non-empty 'find' text.";
                        pairs.Add((e.Find, e.Replace ?? ""));
                    }
                }
                else if (find != null)
                {
                    pairs.Add((find, replace ?? ""));
                }
                else
                {
                    return "patch_file: failed — provide either 'find'/'replace' or a non-empty 'edits' array.";
                }

                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("patch_file", filename);
                fullPath = PathContainmentCheck(fullPath);

                string fileNameOnly = Path.GetFileName(fullPath) ?? filename;
                string cacheKey     = Path.GetFullPath(fullPath);

                // Capture pre-patch baseline for diff_file.
                _svc.TrySnapshot(fullPath);

                // Read file content preserving encoding (matches what PatchEngine writes back).
                var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(fullPath);

                // Capture reporter output so errors return to the MCP client AND go to stderr.
                var errorLog = new StringBuilder();
                Action<string, OutputColor> reporter = (text, color) =>
                {
                    Console.Error.Write(text);
                    if (color == OutputColor.Error || color == OutputColor.Warning)
                        errorLog.Append(text);
                };

                // Structured pairs go STRAIGHT to the engine (atomic: any unresolved
                // find fails the whole patch, no partial writes). Never serialize them
                // into the text PATCH format for re-parsing: its directive markers can
                // collide with literal file content and silently truncate an edit.
                var resolved = PatchEngine.ResolvePairs(
                    pairs, fullPath, fileNameOnly, content, encoding, reporter);

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
                string countNote = pairs.Count > 1 ? $" ({pairs.Count} edits)" : "";
                return $"patch_file: applied to {fullPath}{countNote}{badge}";
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
        "use patch_file to edit existing files, or write_file to overwrite. " +
        "Do not wrap content in code fences.")]
    public Task<string> CreateFile(
        [Description("Absolute file path (must be inside the working directory).")] string filename,
        [Description("The complete content for the new file.")] string content,
        CancellationToken cancellationToken = default)
        => WriteFileCore("create_file", filename, content, overwrite: false, cancellationToken);

    [McpServerTool(Name = "write_file")]
    [Description(
        "Write a file with the given content, creating it or fully OVERWRITING it if it exists. " +
        "Content is written to disk verbatim — this is the reliable way to produce multi-line " +
        "files (no shell quoting or newline hazards; never route file content through run_shell). " +
        "Prefer patch_file for small edits to existing files. Do not wrap content in code fences.")]
    public Task<string> WriteFile(
        [Description("Absolute file path (must be inside the working directory).")] string filename,
        [Description("The complete content to write.")] string content,
        CancellationToken cancellationToken = default)
        => WriteFileCore("write_file", filename, content, overwrite: true, cancellationToken);

    /// <summary>
    /// Shared body for create_file / write_file. EVERYTHING is inside the outer
    /// try/catch — an exception must never escape to the MCP SDK, whose generic
    /// "An error occurred invoking '<tool>'" hides the actual reason from the caller
    /// (field complaint: create_file failed opaquely while the internal agent's path
    /// worked; a path-containment throw or a null argument must surface as text).
    /// </summary>
    private async Task<string> WriteFileCore(
        string toolName, string filename, string content, bool overwrite, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filename))
                return $"[{toolName} error] filename is required.";
            if (content is null)
                return $"[{toolName} error] content is required (send an empty string for an empty file).";

            return await _svc.EnqueueAsync(async () =>
            {
                try
                {
                    string fullPath = PathContainmentCheck(Path.IsPathRooted(filename)
                        ? filename
                        : Path.Combine(_svc.WorkingDirectory, filename));

                    bool existed = File.Exists(fullPath);
                    if (existed && !overwrite)
                        return $"create_file: file already exists — {fullPath}. Use patch_file to edit it or write_file to overwrite.";

                    // Snapshot the original content before first mutation (for diff_file).
                    if (existed)
                        _svc.TrySnapshot(fullPath);

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

                    return existed
                        ? $"{toolName}: overwrote {fullPath} ({lineCount} lines)"
                        : $"{toolName}: created {fullPath} ({lineCount} lines)";
                }
                catch (Exception ex)
                {
                    return $"[{toolName} error] {filename}: {ex.Message}";
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return $"[{toolName}] Cancelled.";
        }
        catch (Exception ex)
        {
            // Last-resort surface: whatever went wrong OUTSIDE the queued work
            // (dispatch, argument handling) is reported as text, never rethrown.
            return $"[{toolName} error] {filename}: {ex.GetType().Name}: {ex.Message}";
        }
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
                string fullPath = PathContainmentCheck(Path.IsPathRooted(filename)
                    ? filename
                    : Path.Combine(_svc.WorkingDirectory, filename));

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
                fullPath = PathContainmentCheck(fullPath);

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
                oldPath = PathContainmentCheck(oldPath);

                // Resolve new path: absolute → use as-is; relative → WorkingDirectory.
                string newPath = PathContainmentCheck(Path.IsPathRooted(new_filename)
                    ? new_filename
                    : Path.Combine(_svc.WorkingDirectory, new_filename));

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
        "Execute a shell command and return its output. Commands run via PowerShell " +
        "(multi-line commands and here-strings are passed to PowerShell verbatim). Default " +
        "timeout 120s — override with timeout_seconds. For anything expected to run longer than " +
        "~45s (installs, deploys, long test runs), pass background=true: the call returns a " +
        "shell_job_id immediately and the command runs detached — poll shell_job_status. This " +
        "avoids the MCP client-timeout trap where the call dies but the command keeps running " +
        "and blocks every subsequent tool call. Do not use run_shell to list or search files — " +
        "use list_files or find_in_files instead; do not route file content through the shell — " +
        "use write_file.")]
    public async Task<string> RunShell(
        [Description("The shell command to execute. Newlines are preserved.")] string command,
        [Description("Timeout in seconds (default 120, max 3600). Ignored when background=true (background default 1800).")] int? timeout_seconds = null,
        [Description("Run detached and return a shell_job_id to poll with shell_job_status (default false).")] bool? background = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (background == true)
            return StartBackgroundShell(command, timeout_seconds);

        int? timeout = timeout_seconds is > 0 ? Math.Min(timeout_seconds.Value, 3600) : null;
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                IProgress<ShellOutputLine>? bridgedProgress = progress != null
                    ? new Progress<ShellOutputLine>(line =>
                        progress.Report(new ProgressNotificationValue { Progress = 0, Message = line.Line }))
                    : null;

                var (output, exitCode) = await _svc.Shell.ExecuteAsync(
                    command, cancellationToken, timeoutSeconds: timeout, onLine: bridgedProgress);

                return CapShellOutput(output, exitCode);
            }
            catch (Exception ex)
            {
                return $"[run_shell error] {ex.Message}";
            }
        }, cancellationToken);
    }

    // ── Background shell jobs ────────────────────────────────────────────────
    // A long command must never ride the synchronous tool call: MCP clients time out
    // (observed ~60s) WITHOUT sending a cancel, the shell keeps running server-side,
    // and — worse — it used to keep the FIFO dispatcher busy, wedging every later
    // tool call behind it. Background jobs run OFF the dispatcher on their own task.

    private sealed class ShellJob
    {
        public required string Id { get; init; }
        public required string Command { get; init; }
        public required Task<(string Output, int ExitCode)> Run { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public readonly DateTime StartedUtc = DateTime.UtcNow;
        public readonly StringBuilder Tail = new StringBuilder();
        public readonly object TailLock = new object();

        public string GetTail(int maxChars)
        {
            lock (TailLock)
            {
                string s = Tail.ToString();
                return s.Length <= maxChars ? s : s.Substring(s.Length - maxChars);
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ShellJob> _shellJobs = new();
    private static int _nextShellJobId;

    private string StartBackgroundShell(string command, int? timeoutSeconds)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command))
                return "[run_shell error] command is required.";

            int timeout = timeoutSeconds is > 0 ? Math.Min(timeoutSeconds.Value, 7200) : 1800;
            var cts = new CancellationTokenSource();
            // Own runner so a background job can't fight the shared runner's cd state.
            var runner = new ShellRunner(_svc.Shell.WorkingDirectory);
            string id = $"sh-{Interlocked.Increment(ref _nextShellJobId)}";

            ShellJob? job = null;
            var onLine = new Progress<ShellOutputLine>(line =>
            {
                var j = job;
                if (j == null) return;
                lock (j.TailLock)
                {
                    j.Tail.AppendLine(line.Line);
                    if (j.Tail.Length > 64_000) j.Tail.Remove(0, j.Tail.Length - 32_000);
                }
            });

            var run = Task.Run(() => runner.ExecuteAsync(command, cts.Token, timeoutSeconds: timeout, onLine: onLine));
            job = new ShellJob { Id = id, Command = command, Run = run, Cts = cts };
            _shellJobs[id] = job;

            // Bounded retention: drop the oldest FINISHED jobs past 20.
            if (_shellJobs.Count > 20)
            {
                foreach (var old in _shellJobs.Values
                    .Where(j => j.Run.IsCompleted)
                    .OrderBy(j => j.StartedUtc)
                    .Take(_shellJobs.Count - 20))
                {
                    _shellJobs.TryRemove(old.Id, out _);
                }
            }

            return JsonSerializer.Serialize(new
            {
                shell_job_id = id,
                state = "running",
                timeout_seconds = timeout,
                hint = "Poll shell_job_status with this id. The command runs detached — later tool calls are not blocked.",
            });
        }
        catch (Exception ex)
        {
            return $"[run_shell error] {ex.Message}";
        }
    }

    [McpServerTool(Name = "shell_job_status")]
    [Description(
        "Check a background shell job started with run_shell background=true: state, elapsed time, " +
        "live output tail while running, and full (capped) output + exit code once finished. " +
        "Pass cancel=true to kill the job's process tree.")]
    public async Task<string> ShellJobStatus(
        [Description("The shell_job_id returned by run_shell.")] string shell_job_id,
        [Description("Kill the job's process tree (default false).")] bool? cancel = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_shellJobs.TryGetValue(shell_job_id, out var job))
                return $"[shell_job_status error] Unknown shell_job_id: {shell_job_id} (jobs are retained in-process, max 20).";

            if (cancel == true && !job.Run.IsCompleted)
                job.Cts.Cancel();

            double elapsed = Math.Round((DateTime.UtcNow - job.StartedUtc).TotalSeconds, 0);

            if (!job.Run.IsCompleted)
            {
                return JsonSerializer.Serialize(new
                {
                    shell_job_id,
                    state = cancel == true ? "cancelling" : "running",
                    elapsed_seconds = elapsed,
                    output_tail = job.GetTail(4_000),
                });
            }

            var (output, exitCode) = await job.Run.ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                shell_job_id,
                state = "done",
                elapsed_seconds = elapsed,
                exit_code = exitCode,
                output = output.Length <= 24_000 ? output : output.Substring(output.Length - 24_000),
            });
        }
        catch (Exception ex)
        {
            return $"[shell_job_status error] {ex.Message}";
        }
    }

    [McpServerTool(Name = "elevated_shell")]
    [Description(
        "Run a PowerShell command ELEVATED (Administrator) via a UAC prompt on the interactive " +
        "desktop — for service restarts, installs, and deploys that plain run_shell cannot do. " +
        "Opt-in: refused unless the DEVMIND_ALLOW_ELEVATION environment variable is set to 1. " +
        "The command runs from a script file with all output captured to a transcript and " +
        "returned; the user must approve the UAC prompt within the timeout.")]
    public async Task<string> ElevatedShell(
        [Description("The PowerShell command/script to run elevated. Newlines are preserved.")] string command,
        [Description("Seconds to wait for UAC approval + completion (default 300, max 1800).")] int? timeout_seconds = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("DEVMIND_ALLOW_ELEVATION")?.Trim() != "1")
                return "[elevated_shell] Refused: elevation is not enabled for this server. " +
                       "Set DEVMIND_ALLOW_ELEVATION=1 in the MCP server's environment to opt in.";
            if (string.IsNullOrWhiteSpace(command))
                return "[elevated_shell error] command is required.";

            int timeout = Math.Min(Math.Max(timeout_seconds ?? 300, 10), 1800);

            // Script + output files in %TEMP%\devmind — never in the working tree.
            string dir = Path.Combine(Path.GetTempPath(), "devmind", "elevated");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string scriptPath = Path.Combine(dir, $"elev-{stamp}.ps1");
            string outPath = Path.Combine(dir, $"elev-{stamp}.out.txt");

            // The wrapper script self-captures ALL streams and its exit code — the
            // elevated process cannot share our redirected pipes across the integrity
            // boundary, so a transcript file is the only reliable output channel.
            string wrapper =
                "$ErrorActionPreference = 'Continue'\n" +
                "& {\n" + command + "\n} *>&1 | Out-File -FilePath '" + outPath.Replace("'", "''") + "' -Encoding utf8\n" +
                "exit $LASTEXITCODE\n";
            File.WriteAllText(scriptPath, wrapper, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,   // required for the UAC verb
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _svc.WorkingDirectory,
            };

            Process proc;
            try
            {
                proc = Process.Start(psi)!;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "[elevated_shell] UAC prompt was declined (or elevation is blocked by policy).";
            }

            using (proc)
            {
                var exited = await Task.Run(() => proc.WaitForExit(timeout * 1000), cancellationToken)
                    .ConfigureAwait(false);
                if (!exited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return $"[elevated_shell] Timed out after {timeout}s (output so far, if any, is at {outPath}).";
                }

                string output = "";
                try { if (File.Exists(outPath)) output = File.ReadAllText(outPath); } catch { }
                int exitCode = 0;
                try { exitCode = proc.ExitCode; } catch { }

                string capped = output.Length <= 24_000 ? output : output.Substring(output.Length - 24_000);
                return $"[elevated_shell] exit code {exitCode}\n{(string.IsNullOrWhiteSpace(capped) ? "(no output)" : capped.TrimEnd())}";
            }
        }
        catch (OperationCanceledException)
        {
            return "[elevated_shell] Cancelled.";
        }
        catch (Exception ex)
        {
            return $"[elevated_shell error] {ex.Message}";
        }
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
                var testArgs = BuildTestArgs(project, filter);

                IProgress<ShellOutputLine>? bridgedProgress = progress != null
                    ? new Progress<ShellOutputLine>(line =>
                        progress.Report(new ProgressNotificationValue { Progress = 0, Message = line.Line }))
                    : null;

                var (output, exitCode) = await _svc.Shell.ExecuteArgvAsync(
                    "dotnet", testArgs, cancellationToken, onLine: bridgedProgress);

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
        // Shared implementation in DevMind.Core — same code path as the TUI/CLI hosts.
        return await _svc.EnqueueAsync(
            async () => await WebTools.WebSearchAsync(query, max_results, cancellationToken),
            cancellationToken);
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
        // Shared implementation in DevMind.Core — same code path as the TUI/CLI hosts.
        return await _svc.EnqueueAsync(
            async () => await WebTools.WebFetchAsync(url, cancellationToken),
            cancellationToken);
    }

    // ── Phase D: ssh_exec ────────────────────────────────────────────────────

    [McpServerTool(Name = "ssh_exec")]
    [Description(
        "Execute a command on a remote host via SSH. Hosts must be defined by alias in the " +
        "DEVMIND_SSH_HOSTS environment variable, each with a pinned host-key fingerprint. " +
        "Arbitrary user@host targets are not permitted. " +
        "Returns stdout and stderr combined, capped at 4000 lines. " +
        "Uses key-based authentication only — no passwords.")]
    public async Task<string> SshExec(
        [Description("Host alias defined in DEVMIND_SSH_HOSTS config.")] string host,
        [Description("Command to execute on the remote host.")] string command,
        [Description("Timeout in seconds (default 60).")] int? timeout_seconds = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            try
            {
                int timeoutSecs = timeout_seconds ?? 60;

                // Resolve host config — configured aliases only. Arbitrary user@host is
                // rejected: every SSH target must be pre-declared in DEVMIND_SSH_HOSTS so
                // its host-key fingerprint can be pinned (see HostKeyReceived below).
                if (!_svc.SshHosts.TryGetValue(host, out var cfg))
                {
                    return $"[ssh_exec] Unknown host alias \"{host}\". " +
                           "Define it in DEVMIND_SSH_HOSTS (with a \"fingerprint\"). " +
                           "Arbitrary user@host targets are not permitted.";
                }

                string remoteHost = cfg.Host;
                string remoteUser = cfg.User;
                string keyPath    = cfg.Key;

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

                // Host-key verification (MITM protection). SSH.NET trusts ANY key unless a
                // HostKeyReceived handler sets CanTrust = false. Pin against the configured
                // fingerprint; if none is configured, reject and surface the observed
                // fingerprint so it can be verified out-of-band and added to config.
                string? hostKeyError = null;
                client.HostKeyReceived += (sender, e) =>
                {
                    // FingerPrintSHA256: base64, no padding, no "SHA256:" prefix.
                    string observed = e.FingerPrintSHA256;
                    if (string.IsNullOrEmpty(cfg.Fingerprint))
                    {
                        e.CanTrust = false;
                        hostKeyError =
                            $"[ssh_exec] No fingerprint configured for alias \"{host}\". Connection refused. " +
                            $"Verify this host key out-of-band, then add " +
                            $"\"fingerprint\": \"SHA256:{observed}\" to the \"{host}\" entry in DEVMIND_SSH_HOSTS. " +
                            $"Observed key: SHA256:{observed}";
                    }
                    else if (!string.Equals(cfg.Fingerprint, observed, StringComparison.Ordinal))
                    {
                        e.CanTrust = false;
                        hostKeyError =
                            $"[ssh_exec] Host key verification FAILED for alias \"{host}\" — possible MITM. " +
                            $"Expected SHA256:{cfg.Fingerprint} but server presented SHA256:{observed}. Connection refused.";
                    }
                    else
                    {
                        e.CanTrust = true;
                    }
                };

                try
                {
                    await Task.Run(() => client.Connect(), cancellationToken);
                }
                catch (Exception) when (hostKeyError != null)
                {
                    return hostKeyError;
                }

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
                    // Stage files. Each file is its own argv element — no shell quoting.
                    var addArgs = new List<string> { "add" };
                    if (files != null && files.Length > 0)
                        addArgs.AddRange(files);
                    else
                        addArgs.Add("-A");
                    var (stageOutput, stageExitCode) = await _svc.Shell.ExecuteArgvAsync("git", addArgs, cancellationToken);
                    if (stageExitCode != 0)
                        return $"[git_commit] git add failed:\n{stageOutput}";

                    // Check what's staged.
                    var (statusOutput, statusExitCode) = await _svc.Shell.ExecuteArgvAsync("git", new[] { "status", "--short" }, cancellationToken);
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

                    // Commit. Message is one argv element — no escaping, no shell parse.
                    var (commitOutput, commitExitCode) = await _svc.Shell.ExecuteArgvAsync("git", new[] { "commit", "-m", commitMessage }, cancellationToken);
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
                        var pushArgs = new List<string> { "push" };
                        pushArgs.AddRange(TokenizeArgs(remote));
                        var (pushOutput, pushExitCode) = await _svc.Shell.ExecuteArgvAsync("git", pushArgs, cancellationToken);
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
               var (output, _) = await _svc.Shell.ExecuteArgvAsync(
                    "powershell.exe",
                    new[] { "-NoProfile", "-NonInteractive", "-Command", "Get-Clipboard" },
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
               // The text travels via an environment variable, never the command text, so it
                // is bound at runtime as a literal string and can never be parsed as PowerShell.
                var clipEnv = new Dictionary<string, string> { ["DEVMIND_CLIP_VALUE"] = text ?? "" };
                var (writeOutput, exitCode) = await _svc.Shell.ExecuteArgvAsync(
                    "powershell.exe",
                    new[] { "-NoProfile", "-NonInteractive", "-Command", "Set-Clipboard -Value $env:DEVMIND_CLIP_VALUE" },
                    cancellationToken,
                    extraEnv: clipEnv);
                return exitCode == 0
                    ? $"[clip_write] Copied {text?.Length ?? 0} characters to clipboard."
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
                // Launch via the OS shell-execute handler directly (the Start-Process equivalent).
                // target is a single ProcessStartInfo.FileName — never parsed as a command, so no
                // quoting/injection surface. UseShellExecute=true picks the default app for the type.
                await Task.Run(
                    () => Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }),
                    cancellationToken);
                return $"[open_file] Opened: {target}";
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

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSecs));

                HttpResponseMessage response;
                if (httpMethod == "GET" || httpMethod == "DELETE")
                {
                    using var request = new HttpRequestMessage(
                        httpMethod == "GET" ? HttpMethod.Get : HttpMethod.Delete, url);
                    response = await _http.SendAsync(request, timeoutCts.Token);
                }
                else
                {
                    using var content = new StringContent(
                        body ?? "{}", Encoding.UTF8, "application/json");
                    response = httpMethod == "POST"
                        ? await _http.PostAsync(url, content, timeoutCts.Token)
                        : await _http.PutAsync(url, content, timeoutCts.Token);
                }

                using (response)
                {
                    string responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);

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
        "Uses the language server for the file type (.cs → Roslyn, .ts/.tsx/.js/.jsx → typescript-language-server). " +
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

    [McpServerTool(Name = "find_symbol")]
    [Description(
        "Find a type or member across the whole solution by name (semantic, solution-wide). " +
        "Use this to answer \"where is X defined?\" when you do NOT already have a file+position — " +
        "prefer it over find_in_files text search for locating a type or member. " +
        "Returns kind, name, file:line:col, and containing type, capped at 50.")]
    public async Task<string> FindSymbol(
        [Description("Symbol name or substring to search for (e.g. \"LanguageServerHost\", \"GetDiagnostics\").")] string query,
        [Description("Max results (default 50, capped at 100).")] int? max_results = null,
        [Description("Language to search: \"csharp\" (default) or \"typescript\".")] string language = "csharp",
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            if (_svc.Lsp == null)
                return "[find_symbol] LSP is disabled (set DEVMIND_LSP_ENABLED=true to enable).";
            if (string.IsNullOrWhiteSpace(query))
                return "[find_symbol] Provide a non-empty symbol name to search for.";

            try
            {
                int cap = Math.Min(max_results ?? 50, 100);
                return await _svc.Lsp.FindSymbolAsync(query, cap, language ?? "csharp", cancellationToken);
            }
            catch (Exception ex)
            {
                return $"[find_symbol error] {ex.Message}";
            }
        }, cancellationToken);
    }

    // ── Image input ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "attach_image")]
    [Description(
        "Validate an image file for multimodal LLM input and stage a copy in the session " +
        "screenshot folder. Returns the resolved path and metadata (mime type, size) — NOT " +
        "the image bytes: tool results are text, so inlining base64 here cannot reach a " +
        "vision encoder and only floods context. To actually send the image to the model, " +
        "the DevMind user runs /image <path> in the TUI, which attaches it to their next " +
        "message as true multimodal content.")]
    public async Task<string> AttachImage(
        [Description("Path to the image file (PNG, JPG, JPEG, GIF, WEBP, BMP).")] string filePath,
        CancellationToken cancellationToken = default)
    {
        return await _svc.EnqueueAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return "[attach_image] Provide a non-empty file path.";

            // Resolve path: absolute or relative to working directory
            string fullPath = filePath;
            if (!Path.IsPathRooted(filePath))
                fullPath = Path.Combine(_svc.WorkingDirectory, filePath);

            if (!File.Exists(fullPath))
                return $"[attach_image] File not found: {fullPath}";

            // Validate extension
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            string? mimeType = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => null
            };
            if (mimeType == null)
                return $"[attach_image] Unsupported image format '{ext}'. Supported: PNG, JPG, JPEG, GIF, WEBP, BMP.";

            byte[] fileBytes;
            try
            {
                fileBytes = File.ReadAllBytes(fullPath);
            }
            catch (Exception ex)
            {
                return $"[attach_image] Failed to read file: {ex.Message}";
            }

            // Store copy in session screenshot folder
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "dm_screenshots",
                SessionId.Get());
            string? savedPath = Path.Combine(baseDir, Path.GetFileName(fullPath));
            try
            {
                Directory.CreateDirectory(baseDir);
                File.WriteAllBytes(savedPath, fileBytes);
            }
            catch
            {
                // Non-fatal — the original path is still valid for /image
                savedPath = null;
            }

            // Deliberately NO data_uri / base64 in the result. A tool result is plain text
            // fed back into the conversation: the vision encoder never sees it, the LLM
            // can't do anything useful with it, and a real image's base64 (~1.3 MB for a
            // 1 MB PNG) would blow past AddToolResultMessage's 50K-char truncation anyway.
            // The image reaches the model via the TUI /image command (multimodal ContentParts).
            string escapedFullPath = fullPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"file\": \"{escapedFullPath}\",");
            sb.AppendLine($"  \"mime_type\": \"{mimeType}\",");
            sb.AppendLine($"  \"size_bytes\": {fileBytes.Length},");
            if (savedPath != null)
            {
                string escapedSaved = savedPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
                sb.AppendLine($"  \"saved_to\": \"{escapedSaved}\",");
            }
            sb.AppendLine($"  \"how_to_send\": \"Ask the user to run: /image {escapedFullPath} — then type their question; the image attaches to that message.\"");
            sb.Append("}");
            return sb.ToString();
        }, cancellationToken);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a filename to a full absolute path.
    /// Priority: (1) already absolute and exists; (2) hint-relative to WorkingDirectory;
    /// (3) recursive basename search under WorkingDirectory (noise paths excluded), using any
    ///     directory portion of the hint to disambiguate same-named files. Returns null rather
    ///     than guessing an arbitrary same-named file when the hint does not uniquely identify one.
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

        // Directory-aware fallback: recursive search by basename, then use any directory
        // information in the hint to disambiguate. We never return an arbitrary same-named
        // file — a silently wrong file is worse than "not found" (the caller can recover via
        // run_shell). Normalize: forward slashes, strip leading "./", strip leading "/".
        string normalizedHint = filename.Replace('\\', '/');
        while (normalizedHint.StartsWith("./", StringComparison.Ordinal))
            normalizedHint = normalizedHint.Substring(2);
        normalizedHint = normalizedHint.TrimStart('/');

        string fileNameOnly = Path.GetFileName(normalizedHint);
        if (string.IsNullOrEmpty(fileNameOnly)) return null;
        bool hintHasDirectory = normalizedHint.Contains('/');

        try
        {
            string[] found = Directory.GetFiles(wd, fileNameOnly, SearchOption.AllDirectories);
            string[] clean = found.Where(f => !ContextEngine.IsNoisePath(f)).ToArray();
            if (clean.Length == 0) return null;

            if (hintHasDirectory)
            {
                // Match candidates whose path ENDS WITH the hint's directory+name suffix.
                // Plain (substring) EndsWith so a partial/abbreviated directory in the hint
                // still matches — e.g. "TestHarness/Program.cs" matches a real directory named
                // ".../VLink.PSCPConnector.TestHarness/Program.cs".
                string[] suffixMatches = clean
                    .Where(f => f.Replace('\\', '/')
                        .EndsWith(normalizedHint, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                // Exactly one → use it. Zero (directory portion matched nothing) or more than
                // one (still ambiguous) → not found; never guess an arbitrary same-named file.
                return suffixMatches.Length == 1 ? suffixMatches[0] : null;
            }

            // Bare basename with no directory to disambiguate: resolve only a unique match.
            return clean.Length == 1 ? clean[0] : null;
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Ensures a resolved path stays within the session WorkingDirectory before any
    /// file-mutating operation. Normalizes traversals (..\) and absolute paths via
    /// Path.GetFullPath, then verifies the result is under WorkingDirectory; throws
    /// InvalidOperationException if it escapes. Returns the normalized full path.
    /// Applied only to write/delete/rename tools — read-only tools stay unrestricted.
    /// </summary>
    private string PathContainmentCheck(string resolvedFullPath)
    {
        string fullPath = Path.GetFullPath(resolvedFullPath);
        string root     = Path.GetFullPath(_svc.WorkingDirectory);

        // Append a trailing separator so "C:\WorkDir" does not match "C:\WorkDir2".
        if (root.Length > 0
            && root[root.Length - 1] != Path.DirectorySeparatorChar
            && root[root.Length - 1] != Path.AltDirectorySeparatorChar)
        {
            root += Path.DirectorySeparatorChar;
        }

        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path containment violation: '{fullPath}' is outside the working directory " +
                $"'{root}'. File create/modify/delete/rename is restricted to the working directory.");
        }

        return fullPath;
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
        // Out-of-band writes (task agents in this same process, git, the user's IDE)
        // must never be masked by session-cached content — a stale entry evicts here
        // and re-reads below. Field evidence: read_file/grep_file served pre-task
        // content after a delegated job rewrote the files.
        _svc.FileCache.InvalidateIfStale(cacheKey, cacheKey);
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

        List<string> gitArgs; string header;

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
            gitArgs = new List<string> { "log", "--oneline", "--no-decorate", $"-{count}" };
            header  = $"[read_file] git log (last {count} commits)";
        }
        else if (filename.StartsWith("git diff", StringComparison.OrdinalIgnoreCase))
        {
            string diffArgs = filename.Substring("git diff".Length).Trim();
            gitArgs = new List<string> { "diff" };
            gitArgs.AddRange(TokenizeArgs(diffArgs));
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
            (output, exitCode) = await _svc.Shell.ExecuteArgvAsync("git", gitArgs, cancellationToken);
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
    /// Shared implementation in DevMind.Core — same code path as the TUI/CLI hosts.
    /// </summary>
    private string DetectBuildCommand()
    {
        string? command = BuildCommandResolver.Resolve(
            _svc.WorkingDirectory,
            warn => Console.Error.WriteLine($"[McpServer] Warning: {warn}"));

        if (command == null)
            throw new Exception(
                "Could not auto-detect a build system. No .vsixmanifest, package.json, " +
                ".sln/.slnx, or .csproj file found.");

        return command;
    }

    /// <summary>
    /// Builds the dotnet test command string from optional project and filter arguments.
    /// </summary>
    private List<string> BuildTestArgs(string? project, string? filter)
    {
        string wd = _svc.WorkingDirectory;
        var args = new List<string> { "test", "--no-build", "--verbosity", "normal" };

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
            args.Add(projectPath);
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.Add("--filter");
            args.Add(filter);
        }

       return args;
    }

    /// <summary>
    /// Splits a free-form argument string into argv tokens the way a shell would: whitespace
    /// separates tokens, but text inside matching single/double quotes stays one token (quotes
    /// stripped). Used to turn a model-supplied "git diff ..." argument tail or a "push" remote
    /// into argv elements for ExecuteArgvAsync — shell metacharacters become literal arguments,
    /// not executable syntax. Returns an empty list for null/whitespace input.
    ///
    /// LOSSY BY DESIGN — this is a minimal splitter for git diff/log args and remote names,
    /// NOT a general-purpose shell-word parser. Known, accepted limitations:
    ///   • Quote characters are removed, so an argument that must literally contain a quote
    ///     cannot be represented (no escape syntax — '\' is not special).
    ///   • An unterminated quote does not error: the remainder is swallowed into a single
    ///     token (e.g. input <c>"HEAD~5</c> yields the one token <c>HEAD~5</c>).
    ///   • No support for adjacent quoted/unquoted concatenation beyond what the char loop
    ///     happens to produce.
    /// These are safe trade-offs: every token is passed verbatim as one argv element, so the
    /// worst case for a malformed input is git/dotnet rejecting an odd pathspec — never code
    /// execution. Do not reuse this as a shell parser where fidelity matters.
    /// </summary>
    private static List<string> TokenizeArgs(string input)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return tokens;

        var  sb     = new StringBuilder();
        char quote  = '\0';
        bool inTok  = false;

        foreach (char c in input)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else            sb.Append(c);
            }
            else if (c == '"' || c == '\'')
            {
                quote = c;
                inTok = true;
            }
            else if (c == ' ' || c == '\t')
            {
                if (inTok) { tokens.Add(sb.ToString()); sb.Clear(); inTok = false; }
            }
            else
            {
                sb.Append(c);
                inTok = true;
            }
        }

        if (inTok) tokens.Add(sb.ToString());
        return tokens;
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
