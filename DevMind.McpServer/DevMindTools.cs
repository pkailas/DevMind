// File: DevMindTools.cs  v4.0
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
//   Every tool method wraps its body in _svc.WithGateAsync(...). The session-level
//   SemaphoreSlim(1,1) in McpServices ensures strictly sequential tool execution,
//   preventing file-handle conflicts and dictionary races when a bursty client
//   (test harness, Claude Desktop batch) sends multiple requests simultaneously.
//
// Phase status:
//   Phase A (complete): list_memory_topics.
//   Phase B (complete): read_file, list_files, grep_file, find_in_files,
//                       diff_file (upgraded in Phase C), recall_memory.
//   Phase C (complete): patch_file, create_file, append_file, delete_file,
//                       rename_file, save_memory; diff_file real unified diffs.
//   Phase C fix (this): all 16 tools serialized via _svc.WithGateAsync.
//   Phase D: run_shell, run_build, run_tests (shell / streaming tools).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DevMind;
using DevMind.McpServer;
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
        return await _svc.WithGateAsync(async () =>
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
        return await _svc.WithGateAsync(async () =>
        {
            try
            {
                if (filename.StartsWith("git ", StringComparison.OrdinalIgnoreCase))
                    return await ReadGitAsync(filename, start_line ?? 0, cancellationToken);

                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("read_file", filename);

                string fileNameOnly = Path.GetFileName(fullPath) ?? Path.GetFileName(filename) ?? filename;
                bool forceFullRead  = force_full == true;

                // ── Line-range path ──────────────────────────────────────────
                if (start_line.HasValue && start_line.Value > 0)
                {
                    EnsureCached(fileNameOnly, fullPath);

                    int totalLines   = _svc.FileCache.GetLineCount(fileNameOnly);
                    int rawStart     = start_line.Value;
                    int rawEnd       = end_line.HasValue ? end_line.Value : totalLines;
                    if (rawStart > rawEnd) { int t = rawStart; rawStart = rawEnd; rawEnd = t; }
                    int clampedStart = Math.Max(1, rawStart);
                    int clampedEnd   = Math.Min(totalLines, rawEnd);

                    string rangeContent = _svc.FileCache.GetLineRange(fileNameOnly, clampedStart, clampedEnd);
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
                _svc.FileCache.Store(fileNameOnly, content);
                int lineCount    = content.Split('\n').Length;
                bool alreadyRead = _svc.FilesRead.Contains(fileNameOnly);
                _svc.FilesRead.Add(fileNameOnly);

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
        return await _svc.WithGateAsync(async () =>
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
        return await _svc.WithGateAsync(async () =>
        {
            const int MaxMatches = 50;
            try
            {
                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("grep_file", filename);

                string fileNameOnly = Path.GetFileName(fullPath) ?? Path.GetFileName(filename) ?? filename;
                EnsureCached(fileNameOnly, fullPath);

                int totalLines = _svc.FileCache.GetLineCount(fileNameOnly);
                int scanStart  = start_line.HasValue ? Math.Max(1, start_line.Value) : 1;
                int scanEnd    = end_line.HasValue   ? Math.Min(totalLines, end_line.Value) : totalLines;

                var matches = new List<(int lineNum, string lineText)>();
                for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
                {
                    string lineContent = _svc.FileCache.GetLineRange(fileNameOnly, lineNum, lineNum);
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
        return await _svc.WithGateAsync(async () =>
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

                    if (!_svc.FileCache.Contains(fileNameOnly))
                    {
                        string diskContent;
                        try { diskContent = File.ReadAllText(filePath); }
                        catch { continue; }
                        _svc.FileCache.Store(fileNameOnly, diskContent);
                    }

                    int totalLines = _svc.FileCache.GetLineCount(fileNameOnly);
                    int scanStart  = start_line.HasValue ? Math.Max(1, start_line.Value) : 1;
                    int scanEnd    = end_line.HasValue   ? Math.Min(totalLines, end_line.Value) : totalLines;

                    for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
                    {
                        string lineContent = _svc.FileCache.GetLineRange(fileNameOnly, lineNum, lineNum);
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
        return await _svc.WithGateAsync(async () =>
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
        return await _svc.WithGateAsync(async () =>
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
        return await _svc.WithGateAsync(async () =>
        {
            try
            {
                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("patch_file", filename);

                string fileNameOnly = Path.GetFileName(fullPath) ?? filename;

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
                _svc.FileCache.Store(fileNameOnly, applyResult.UpdatedContent);
                _svc.FilesRead.Add(fileNameOnly);

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
        return await _svc.WithGateAsync(async () =>
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

                File.WriteAllText(fullPath, content, System.Text.Encoding.UTF8);

                int lineCount       = content.Split('\n').Length;
                string fileNameOnly = Path.GetFileName(fullPath) ?? filename;
                _svc.FileCache.Store(fileNameOnly, content);
                _svc.FilesRead.Add(fileNameOnly);

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
        return await _svc.WithGateAsync(async () =>
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

                // Ensure a newline separator between existing content and appended content.
                if (existed)
                {
                    string existing  = File.ReadAllText(fullPath);
                    string separator = existing.Length > 0 && !existing.EndsWith("\n") ? "\n" : "";
                    File.WriteAllText(fullPath, existing + separator + content, System.Text.Encoding.UTF8);
                }
                else
                {
                    File.WriteAllText(fullPath, content, System.Text.Encoding.UTF8);
                }

                string fileNameOnly = Path.GetFileName(fullPath) ?? filename;
                _svc.FileCache.Invalidate(fileNameOnly);

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
        return await _svc.WithGateAsync(async () =>
        {
            try
            {
                string? fullPath = ResolveFilePath(filename);
                if (fullPath == null || !File.Exists(fullPath))
                    return BuildFileNotFoundMessage("delete_file", filename);

                // Snapshot before deleting so diff_file can show the removal.
                _svc.TrySnapshot(fullPath);

                File.Delete(fullPath);

                string fileNameOnly = Path.GetFileName(fullPath) ?? filename;
                _svc.FileCache.Invalidate(fileNameOnly);

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
        return await _svc.WithGateAsync(async () =>
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
                string oldFileNameOnly = Path.GetFileName(oldPath) ?? old_filename;
                _svc.FileCache.Invalidate(oldFileNameOnly);

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
        return await _svc.WithGateAsync(async () =>
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

    // ── Phase D stubs: shell / streaming tools ───────────────────────────────

    [McpServerTool(Name = "run_shell")]
    [Description(
        "Execute a shell command and return its output. Commands run via PowerShell with a " +
        "120-second timeout. Use this for git commands and operations no other tool covers. " +
        "Do not use run_shell to list or search files — use list_files or find_in_files instead.")]
    public async Task<string> RunShell(
        [Description("The shell command to execute.")] string command,
        CancellationToken cancellationToken = default)
    {
        return await _svc.WithGateAsync(
            () => Task.FromException<string>(new NotImplementedException("run_shell is implemented in Phase D.")),
            cancellationToken);
    }

    [McpServerTool(Name = "run_build")]
    [Description(
        "Run the project build command. Call this after ANY code change (patch_file or create_file). " +
        "The build command is auto-detected from the working directory " +
        "(VSIX projects use MSBuild; other projects use dotnet build). No parameters needed.")]
    public async Task<string> RunBuild(CancellationToken cancellationToken = default)
    {
        return await _svc.WithGateAsync(
            () => Task.FromException<string>(new NotImplementedException("run_build is implemented in Phase D.")),
            cancellationToken);
    }

    [McpServerTool(Name = "run_tests")]
    [Description(
        "Run dotnet test and return structured pass/fail results. " +
        "Only failed tests show details. Use run_tests after making changes to verify correctness.")]
    public async Task<string> RunTests(
        [Description("Project file name (e.g., 'MyProject.csproj'). Omit to run all tests.")] string? project = null,
        [Description("Test filter expression (e.g., 'FullyQualifiedName~SomeTest').")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        return await _svc.WithGateAsync(
            () => Task.FromException<string>(new NotImplementedException("run_tests is implemented in Phase D.")),
            cancellationToken);
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
        if (_svc.FileCache.Contains(fileNameOnly)) return;
        try
        {
            string content = File.ReadAllText(fullPath);
            _svc.FileCache.Store(fileNameOnly, content);
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
}
