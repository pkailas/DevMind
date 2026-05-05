// File: DevMindTools.cs  v2.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Diagnostic policy: never write to Console.Out / Console.WriteLine in this file.
// All diagnostics go to Console.Error (stderr). stdout is reserved exclusively for
// the MCP stdio transport's JSON-RPC framing and must not be touched by application code.
//
// Stderr diagnostic pattern for Core callbacks:
//   When a Core utility accepts Action<string, OutputColor> for diagnostics, pass:
//     (text, _) => Console.Error.Write(text)
//   This redirects all Core diagnostic output to stderr. Phase C reviewers should
//   follow this convention for PatchEngine.ResolvePatch and PatchEngine.ApplyPatch.
//
// Phase status:
//   Phase A (complete): list_memory_topics implemented end-to-end.
//   Phase B (this file): read_file, list_files, grep_file, find_in_files,
//                        diff_file (session-baseline placeholder), recall_memory.
//   Phase C: patch_file, create_file, append_file, delete_file, rename_file,
//            save_memory (mutation tools).
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

    // ── Phase A: implemented ─────────────────────────────────────────────────

    [McpServerTool(Name = "list_memory_topics")]
    [Description(
        "List all available memory topics with their descriptions from the memory index. " +
        "Use this to see what knowledge has been saved in previous sessions before " +
        "recalling a specific topic with recall_memory.")]
    public string ListMemoryTopics()
    {
        string index = _svc.Memory.LoadIndex();
        if (!string.IsNullOrWhiteSpace(index))
            return index;

        var topics = _svc.Memory.ListTopics();
        return topics.Count == 0
            ? "No memory topics found. Use save_memory to create one."
            : string.Join("\n", topics.Select(t => $"- [{t}]"));
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
        try
        {
            if (filename.StartsWith("git ", StringComparison.OrdinalIgnoreCase))
                return await ReadGitAsync(filename, start_line ?? 0, cancellationToken);

            string? fullPath = ResolveFilePath(filename);
            if (fullPath == null || !File.Exists(fullPath))
                return BuildFileNotFoundMessage("read_file", filename);

            string fileNameOnly = Path.GetFileName(fullPath) ?? Path.GetFileName(filename) ?? filename;
            bool forceFullRead  = force_full == true;

            // ── Line-range path ──────────────────────────────────────────────
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

            // ── Full / outline path ──────────────────────────────────────────
            string content    = File.ReadAllText(fullPath);
            _svc.FileCache.Store(fileNameOnly, content);
            int lineCount     = content.Split('\n').Length;
            bool alreadyRead  = _svc.FilesRead.Contains(fileNameOnly);
            _svc.FilesRead.Add(fileNameOnly);

            return ContextEngine.RenderReadBlock(
                fileNameOnly, content, lineCount, forceFullRead, alreadyRead, out _);
        }
        catch (Exception ex)
        {
            return $"[read_file error] {filename}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_files")]
    [Description(
        "List files matching a glob pattern under the project root. Returns absolute paths, " +
        "alphabetically sorted, capped at 200 results. Skips build artifacts (bin, obj) and " +
        "version control metadata (.vs, .git, node_modules, packages) automatically.")]
    public Task<string> ListFiles(
        [Description("Glob pattern to match files (e.g., '*.cs', 'Services/*.cs'). Required.")] string glob,
        [Description("If true (default), searches all subdirectories.")] bool recursive = true,
        CancellationToken cancellationToken = default)
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
                return Task.FromResult("[ERROR: glob pattern is empty]");

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
                return Task.FromResult("[no matches]");

            var sb    = new StringBuilder();
            int shown = Math.Min(sorted.Count, Cap);
            for (int i = 0; i < shown; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sb.AppendLine(sorted[i]);
            }
            if (sorted.Count > Cap)
                sb.AppendLine($"[truncated — {sorted.Count - Cap} more matches]");

            return Task.FromResult(sb.ToString().TrimEnd());
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult("[list_files: cancelled]");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"[list_files error] {ex.Message}");
        }
    }

    [McpServerTool(Name = "grep_file")]
    [Description(
        "Search a single file for lines matching a pattern (case-insensitive substring match). " +
        "Returns matching lines with 1-based line numbers, capped at 50 matches. " +
        "Use grep_file to locate code, then read_file with a targeted range, then patch_file.")]
    public Task<string> GrepFile(
        [Description("Search pattern (case-insensitive substring match, not regex).")] string pattern,
        [Description("Absolute file path.")] string filename,
        [Description("1-based start line to restrict the search window.")] int? start_line = null,
        [Description("1-based end line to restrict the search window.")] int? end_line = null)
    {
        const int MaxMatches = 50;

        try
        {
            string? fullPath = ResolveFilePath(filename);
            if (fullPath == null || !File.Exists(fullPath))
                return Task.FromResult(BuildFileNotFoundMessage("grep_file", filename));

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
                return Task.FromResult($"grep_file: no matches for \"{pattern}\" in {filename}");

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

            return Task.FromResult(sb.ToString().TrimEnd('\r', '\n'));
        }
        catch (Exception ex)
        {
            return Task.FromResult($"[grep_file error] {filename}: {ex.Message}");
        }
    }

    [McpServerTool(Name = "find_in_files")]
    [Description(
        "Search across multiple files by glob pattern for lines matching a text pattern. " +
        "Returns filename:line:content for each hit, capped at 100 results. " +
        "Use find_in_files when you need to know where something is used across the project. " +
        "Use grep_file when you already know which file to search.")]
    public Task<string> FindInFiles(
        [Description("Search pattern (case-insensitive substring match).")] string pattern,
        [Description("Glob pattern to match files (e.g., '*.cs', 'Services/*.cs').")] string glob,
        [Description("1-based start line to restrict the search window within each file.")] int? start_line = null,
        [Description("1-based end line to restrict the search window within each file.")] int? end_line = null)
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
                return Task.FromResult($"find_in_files: error enumerating files for {glob} — {ex.Message}");
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
                return Task.FromResult($"find_in_files: no matches for \"{pattern}\" in {glob}");

            int shownCount  = allMatches.Count;
            string findHeader = hitCap
                ? $"find_in_files results for \"{pattern}\" in {glob} ({MaxMatches}+ matches — narrow your pattern or add a line range):"
                : $"find_in_files results for \"{pattern}\" in {glob} ({shownCount} match{(shownCount == 1 ? "" : "es")}):";

            var sb = new StringBuilder();
            sb.AppendLine(findHeader);
            foreach (var (fileLabel, lineNum, lineText) in allMatches)
                sb.AppendLine($"  {fileLabel}:{lineNum}: {lineText.TrimEnd()}");

            return Task.FromResult(sb.ToString().TrimEnd('\r', '\n'));
        }
        catch (Exception ex)
        {
            return Task.FromResult($"[find_in_files error] {ex.Message}");
        }
    }

    [McpServerTool(Name = "diff_file")]
    [Description(
        "Show all changes made to a file during this session as a unified-style diff. " +
        "Use diff_file after multiple patches to verify cumulative changes before completing a task. " +
        "Information-gathering only — does not modify files.")]
    public string DiffFile(
        [Description("Absolute file path.")] string filename)
    {
        // Session-baseline snapshot tracking is established in Phase C when patch_file
        // captures pre-patch content. Until then, no snapshots exist.
        return $"diff_file: No session changes recorded for {Path.GetFileName(filename)} — " +
               "patch tracking begins when patch_file is first used (Phase C).";
    }

    [McpServerTool(Name = "recall_memory")]
    [Description(
        "Recall previously saved knowledge about a topic. Returns the content of a memory " +
        "topic file. Call list_memory_topics first if you are not sure what topics are available.")]
    public string RecallMemory(
        [Description("The topic slug to recall (e.g., 'auth-system', 'build-quirks').")] string topic)
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
    }

    // ── Phase C stubs: mutation tools ────────────────────────────────────────

    [McpServerTool(Name = "patch_file")]
    [Description(
        "Edit an existing file by replacing exact text. The find text must be copied " +
        "verbatim from read_file output — never reconstructed from memory. " +
        "Whitespace-normalized matching is applied (CRLF and indentation differences ignored). " +
        "Always read_file first if you have not seen the file.")]
    public string PatchFile(
        [Description("Absolute file path.")] string filename,
        [Description("Exact text to find in the file (verbatim from read_file output).")] string find,
        [Description("Replacement text.")] string replace)
        => throw new NotImplementedException("patch_file is implemented in Phase C.");

    [McpServerTool(Name = "create_file")]
    [Description(
        "Create a new file with the given content. Use for brand-new files only — " +
        "use patch_file to edit existing files. Do not wrap content in code fences.")]
    public string CreateFile(
        [Description("Absolute file path.")] string filename,
        [Description("The complete content for the new file.")] string content)
        => throw new NotImplementedException("create_file is implemented in Phase C.");

    [McpServerTool(Name = "append_file")]
    [Description(
        "Append content to the end of an existing file. If the file does not exist, it will be created.")]
    public string AppendFile(
        [Description("Absolute file path.")] string filename,
        [Description("Content to append to the end of the file.")] string content)
        => throw new NotImplementedException("append_file is implemented in Phase C.");

    [McpServerTool(Name = "delete_file")]
    [Description(
        "Delete a file from disk. Use only when explicitly asked to remove a file.")]
    public string DeleteFile(
        [Description("Absolute file path.")] string filename)
        => throw new NotImplementedException("delete_file is implemented in Phase C.");

    [McpServerTool(Name = "rename_file")]
    [Description(
        "Rename or move a file on disk. Does not update references in other files — " +
        "use find_in_files + patch_file to update imports after renaming.")]
    public string RenameFile(
        [Description("Current absolute file path.")] string old_filename,
        [Description("New absolute file path.")] string new_filename)
        => throw new NotImplementedException("rename_file is implemented in Phase C.");

    [McpServerTool(Name = "save_memory")]
    [Description(
        "Save project knowledge that should persist across sessions. Topics are overwritten " +
        "if they already exist — include all relevant content, not just additions.")]
    public string SaveMemory(
        [Description("Slug for the topic (e.g., 'auth-system', 'build-quirks').")] string topic,
        [Description("The knowledge to save — conventions, insights, patterns.")] string content,
        [Description("Short one-line description for the memory index.")] string? description = null)
        => throw new NotImplementedException("save_memory is implemented in Phase C.");

    // ── Phase D stubs: shell / streaming tools ───────────────────────────────

    [McpServerTool(Name = "run_shell")]
    [Description(
        "Execute a shell command and return its output. Commands run via PowerShell with a " +
        "120-second timeout. Use this for git commands and operations no other tool covers. " +
        "Do not use run_shell to list or search files — use list_files or find_in_files instead.")]
    public string RunShell(
        [Description("The shell command to execute.")] string command,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("run_shell is implemented in Phase D.");

    [McpServerTool(Name = "run_build")]
    [Description(
        "Run the project build command. Call this after ANY code change (patch_file or create_file). " +
        "The build command is auto-detected from the working directory " +
        "(VSIX projects use MSBuild; other projects use dotnet build). No parameters needed.")]
    public string RunBuild(CancellationToken cancellationToken = default)
        => throw new NotImplementedException("run_build is implemented in Phase D.");

    [McpServerTool(Name = "run_tests")]
    [Description(
        "Run dotnet test and return structured pass/fail results. " +
        "Only failed tests show details. Use run_tests after making changes to verify correctness.")]
    public string RunTests(
        [Description("Project file name (e.g., 'MyProject.csproj'). Omit to run all tests.")] string? project = null,
        [Description("Test filter expression (e.g., 'FullyQualifiedName~SomeTest').")] string? filter = null,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("run_tests is implemented in Phase D.");

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
    /// Mirrors ConsoleAgenticHost.LoadGitContentAsync.
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
