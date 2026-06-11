// File: DevMindToolWindowControl.AgenticHost.cs  v7.11
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Xml.Linq;

namespace DevMind
{
    /// <summary>
    /// Implements <see cref="IAgenticHost"/> on <see cref="DevMindToolWindowControl"/>,
    /// delegating each method to the existing private partial-class methods.
    /// This partial class is the seam between the pure agentic pipeline and VS/UI side effects.
    /// </summary>
    public partial class DevMindToolWindowControl : UserControl, IAgenticHost
    {
        // ── Memory Manager ───────────────────────────────────────────────────────

        private MemoryManager _memoryManager;

        /// <summary>
        /// Initializes the MemoryManager for the current working directory.
        /// Called when the solution directory is available.
        /// </summary>
        private void EnsureMemoryManager()
        {
            if (_memoryManager == null && !string.IsNullOrEmpty(_shellRunner.WorkingDirectory))
                _memoryManager = new MemoryManager(_shellRunner.WorkingDirectory);
        }

        // ── File snapshot tracking (for DIFF directive) ───────────────────────────

        // Stores original file content keyed by full path, captured before first patch/read.
        // Never updated after initial capture — always holds pre-conversation state.
        private readonly Dictionary<string, string> _fileSnapshots =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stores the current disk content of the file as the original snapshot, if not
        /// already captured. Call before any mutation (patch) or on first read.
        /// </summary>
        internal void CaptureFileSnapshot(string fullPath)
        {
            if (_fileSnapshots.ContainsKey(fullPath)) return;
            try { _fileSnapshots[fullPath] = File.ReadAllText(fullPath); }
            catch { }
        }

        /// <summary>
        /// Clears all captured file snapshots. Called when the conversation is reset.
        /// </summary>
        internal void ClearFileSnapshots() => _fileSnapshots.Clear();

        // ── Unrelated-file write guard ────────────────────────────────────────────

        /// <summary>
        /// Returns true if the model is allowed to write to the given file without user
        /// confirmation — i.e., the file was read during the current task, or no files
        /// have been read yet (new task / direct invocation — be permissive).
        /// </summary>
        private bool IsFileKnownToTask(string fileNameOnly)
        {
            return _taskReadFiles.Contains(fileNameOnly) || _taskReadFiles.Count == 0;
        }

        /// <summary>
        /// Shows a Yes/No dialog asking the user to approve writing to a file that the model
        /// has not read during the current task. Returns true if the user approves.
        /// </summary>
        private async Task<bool> ConfirmUnreadFileWriteAsync(string fileNameOnly)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            AppendOutput(
                $"[WRITE GUARD] \"{fileNameOnly}\" was not read during this task — asking for approval.\n",
                OutputColor.Warning);
            var answer = System.Windows.MessageBox.Show(
                $"DevMind wants to write to \"{fileNameOnly}\", but that file was not read during this task " +
                $"and was not mentioned in your request.\n\nAllow this write?",
                "DevMind — Unread File Write",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            return answer == System.Windows.MessageBoxResult.Yes;
        }

        // ── IAgenticHost.RunShellAsync ────────────────────────────────────────────

        async Task<(int exitCode, string output)> IAgenticHost.RunShellAsync(string command)
        {
            AppendOutput($"[SHELL] > {command}\n", OutputColor.Dim);
            // Progress<T> captures the current SynchronizationContext (UI thread) so each
            // Report() call is marshalled back to the UI thread — safe to call AppendOutput directly.
            var progress = new Progress<ShellOutputLine>(o =>
                AppendOutput(o.Line + "\n", o.IsError ? OutputColor.Error : OutputColor.Normal));
            var (output, exitCode) = await _shellRunner.ExecuteAsync(
                command, _cts?.Token ?? CancellationToken.None, onLine: progress);
            _lastShellExitCode = exitCode;
            _lastShellCommand  = command;
            return (exitCode, output);
        }

        // ── IAgenticHost.SaveFileAsync ────────────────────────────────────────────

        async Task<string> IAgenticHost.SaveFileAsync(string fileName, string content, bool fromToolCall)
        {
            // Unrelated-file write guard: confirm before creating/overwriting an unread file.
            string saveFileOnly;
            try { saveFileOnly = Path.GetFileName(fileName.Replace('\\', '/')); }
            catch { saveFileOnly = fileName; }

            if (!IsFileKnownToTask(saveFileOnly))
            {
                bool approved = await ConfirmUnreadFileWriteAsync(saveFileOnly);
                if (!approved)
                {
                    AppendOutput($"[WRITE GUARD] File write to \"{saveFileOnly}\" blocked by user.\n", OutputColor.Dim);
                    return null;
                }
                _taskReadFiles.Add(saveFileOnly);
            }

            // In tool_use mode, content comes from structured JSON — backticks are
            // legitimate content, not markdown formatting. Skip fence stripping.
            string fileContent = fromToolCall ? content : PatchEngine.StripOuterCodeFence(content);
            await SaveGeneratedFileAsync(fileName, fileContent);
            // Approximate the resolved path for agentic context / diff view purposes.
            try
            {
                if (Path.IsPathRooted(fileName))
                    return fileName;
                return Path.Combine(_shellRunner.WorkingDirectory, fileName);
            }
            catch
            {
                return fileName;
            }
        }

        // ── IAgenticHost.AppendFileAsync ──────────────────────────────────────────

        async Task<string> IAgenticHost.AppendFileAsync(string fileName, string content)
        {
            // Unrelated-file write guard: confirm before appending to an unread file.
            string appendFileOnly;
            try { appendFileOnly = Path.GetFileName(fileName.Replace('\\', '/')); }
            catch { appendFileOnly = fileName; }

            if (!IsFileKnownToTask(appendFileOnly))
            {
                bool approved = await ConfirmUnreadFileWriteAsync(appendFileOnly);
                if (!approved)
                {
                    AppendOutput($"[WRITE GUARD] File append to \"{appendFileOnly}\" blocked by user.\n", OutputColor.Dim);
                    return null;
                }
                _taskReadFiles.Add(appendFileOnly);
            }

            try
            {
                string resolvedPath = await FindFileInSolutionAsync(appendFileOnly, fileName.Replace('\\', '/'))
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileName);

                if (File.Exists(resolvedPath))
                {
                    // Append with a newline separator to avoid content merging
                    string existing = File.ReadAllText(resolvedPath);
                    string separator = existing.Length > 0 && !existing.EndsWith("\n") ? "\n" : "";
                    File.WriteAllText(resolvedPath, existing + separator + content);
                    AppendOutput($"[APPEND] Appended to {appendFileOnly}\n", OutputColor.Success);
                }
                else
                {
                    // File does not exist — create it with the content
                    string dir = Path.GetDirectoryName(resolvedPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(resolvedPath, content);
                    AppendOutput($"[APPEND] Created {appendFileOnly}\n", OutputColor.Success);
                }

                // Invalidate cache so subsequent READs see the updated content
                try { _llmClient.FileCache.Invalidate(appendFileOnly); } catch { }

                // Track completed files for brainwash context
                _llmClient.TrackCompletedFiles(content);

                return resolvedPath;
            }
            catch (Exception ex)
            {
                AppendOutput($"[APPEND ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return null;
            }
        }

        // ── IAgenticHost.LoadFileContentAsync ────────────────────────────────────

        async Task<string> IAgenticHost.LoadFileContentAsync(
            string fileName, int rangeStart, int rangeEnd, bool forceFullRead)
        {
            if (fileName.StartsWith("git ", StringComparison.OrdinalIgnoreCase))
                return await LoadGitContentForToolUseAsync(fileName, rangeStart);

            return await LoadFileContentForToolUseAsync(fileName, rangeStart, rangeEnd, forceFullRead);
        }

        /// <summary>
        /// ToolUse-mode read: renders the [READ:filename] block (full, outline, or range)
        /// and returns it as a string for delivery via the tool result message.
        /// Updates _filesReadThisSession, _taskReadFiles, the file cache, and DIFF snapshot.
        /// </summary>
        private async Task<string> LoadFileContentForToolUseAsync(
            string fileName, int rangeStart, int rangeEnd, bool forceFullRead)
        {
            try
            {
                // ── NearlineCache hit — cached content is already wrapped in the [READ:…] block format
                if (rangeStart <= 0 && !forceFullRead && _llmClient?.NearlineCache != null)
                {
                    string cacheKey = $"read:{fileName}";
                    string cached = _llmClient.NearlineCache.Retrieve(cacheKey);
                    if (cached != null)
                    {
                        AppendOutput($"[CACHE HIT] {fileName}\n", OutputColor.Dim);
                        return cached;
                    }
                }

                // ── Resolve file path
                string normalizedHint = fileName.Replace('\\', '/');
                string fileNameOnly;
                try { fileNameOnly = Path.GetFileName(normalizedHint); }
                catch { fileNameOnly = fileName; }

                string fullPath = await FindFileInSolutionAsync(fileNameOnly, normalizedHint)
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileName);

                if (!File.Exists(fullPath))
                {
                    AppendOutput($"[READ] File not found: {fileName}\n", OutputColor.Warning);
                    string notFoundMsg = await BuildFileNotFoundMessageAsync("READ", fileName);
                    return notFoundMsg;
                }

                CaptureFileSnapshot(fullPath);

                // ── Range-read path
                if (rangeStart > 0)
                {
                    // Ensure the file is in the cache for line-range access
                    if (!_llmClient.FileCache.Contains(fileNameOnly))
                    {
                        var (diskContent, _) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                        _llmClient.FileCache.Store(fileNameOnly, diskContent);
                    }

                    _taskReadFiles.Add(fileNameOnly);
                    int totalLines = _llmClient.FileCache.GetLineCount(fileNameOnly);

                    // Swap inverted range silently
                    if (rangeStart > rangeEnd)
                    {
                        int tmp = rangeStart; rangeStart = rangeEnd; rangeEnd = tmp;
                    }
                    int clampedEnd   = Math.Min(rangeEnd,   totalLines);
                    int clampedStart = Math.Max(1, rangeStart);

                    string rangeContent = _llmClient.FileCache.GetLineRange(fileNameOnly, clampedStart, clampedEnd);
                    if (rangeContent == null)
                    {
                        AppendOutput($"[READ] Range {rangeStart}-{rangeEnd} out of bounds for {fileNameOnly} ({totalLines} lines)\n", OutputColor.Error);
                        return $"[READ] Range {rangeStart}-{rangeEnd} out of bounds for {fileNameOnly} ({totalLines} lines)";
                    }

                    var rawLines = rangeContent.Split('\n');
                    var numbered = new System.Text.StringBuilder();
                    for (int i = 0; i < rawLines.Length; i++)
                        numbered.AppendLine($"{clampedStart + i}: {rawLines[i].TrimEnd('\r')}");

                    bool clamped = clampedEnd < rangeEnd;
                    string rangeBlock = ContextEngine.RenderReadRangeBlock(fileNameOnly, clampedStart, clampedEnd, totalLines, numbered.ToString(), clamped);

                    AppendOutput($"[READ] {fileNameOnly}:{clampedStart}-{clampedEnd} ({clampedEnd - clampedStart + 1} lines){(clamped ? " [clamped]" : "")}\n", OutputColor.Success);
                    return rangeBlock;
                }

                // ── Full / outline path
                var (content, _) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                _llmClient.FileCache.Store(fileNameOnly, content);
                _taskReadFiles.Add(fileNameOnly);
                int lineCount = content.Split('\n').Length;

                bool alreadyRead = _llmClient.MarkFileRead(fileNameOnly);

                string rendered = ContextEngine.RenderReadBlock(fileNameOnly, content, lineCount, forceFullRead, alreadyRead, out bool wasOutline);

                if (wasOutline)
                    AppendOutput($"[READ] {fullPath} ({lineCount} lines — outline{(alreadyRead ? ", re-read" : "")})\n", OutputColor.Success);
                else
                    AppendOutput($"[READ] Loaded {fullPath} ({lineCount} lines)\n", OutputColor.Success);

                return rendered;
            }
            catch (Exception ex)
            {
                AppendOutput($"[READ ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return $"[ERROR reading {fileName}: {ex.Message}]";
            }
        }

        /// <summary>
        /// ToolUse-mode git read: executes "git log" or "git diff" in the discovered git root,
        /// renders output identically to ApplyReadGitCommandAsync's format, and returns the
        /// rendered string for delivery via the tool result message.
        ///
        /// Honors rangeStart as the count for git log (default 10, max 50, clamped to 1-50).
        /// rangeStart is ignored for git diff.
        /// </summary>
        private async Task<string> LoadGitContentForToolUseAsync(string fileName, int rangeStart)
        {
            string gitRoot = await FindGitRootAsync();
            if (gitRoot == null)
            {
                AppendOutput("[READ] git: not a git repository\n", OutputColor.Error);
                return "[READ] git: not a git repository\n";
            }

            string command;
            string header;

            if (fileName.StartsWith("git log", StringComparison.OrdinalIgnoreCase))
            {
                int count;
                if (rangeStart > 0)
                {
                    count = rangeStart;
                }
                else
                {
                    string countPart = fileName.Substring("git log".Length).Trim();
                    count = 10;
                    if (!string.IsNullOrEmpty(countPart))
                        int.TryParse(countPart, out count);
                }
                count = Math.Max(1, Math.Min(count, 50));
                command = $"git log --oneline --no-decorate -{count}";
                header = $"[READ] git log (last {count} commits)";
            }
            else if (fileName.StartsWith("git diff", StringComparison.OrdinalIgnoreCase))
            {
                string diffArgs = fileName.Substring("git diff".Length).Trim();
                if (string.IsNullOrEmpty(diffArgs))
                {
                    command = "git diff";
                    header = "[READ] git diff (working changes)";
                }
                else if (diffArgs.Equals("--staged", StringComparison.OrdinalIgnoreCase) ||
                         diffArgs.Equals("--cached", StringComparison.OrdinalIgnoreCase))
                {
                    command = $"git diff {diffArgs}";
                    header = "[READ] git diff --staged";
                }
                else
                {
                    command = $"git diff {diffArgs}";
                    header = $"[READ] git diff {diffArgs}";
                }
            }
            else
            {
                string errMsg = $"[READ] Unrecognized git command: {fileName}";
                AppendOutput(errMsg + "\n", OutputColor.Error);
                return errMsg + "\n";
            }

            string savedDir = _shellRunner.WorkingDirectory;
            _shellRunner.ChangeDirectory(gitRoot);
            string output;
            int exitCode;
            try
            {
                (output, exitCode) = await _shellRunner.ExecuteAsync(command, _cts?.Token ?? CancellationToken.None);
            }
            finally
            {
                _shellRunner.ChangeDirectory(savedDir);
            }

            if (exitCode != 0)
            {
                string errMsg = $"{header}\n(error — exit code {exitCode})\n{output}\n";
                AppendOutput(errMsg, OutputColor.Error);
                return errMsg;
            }

            const int MaxDiffLines = 500;
            var outputLines = output.Split('\n');
            string truncatedOutput;
            if (outputLines.Length > MaxDiffLines)
            {
                int omitted = outputLines.Length - MaxDiffLines;
                truncatedOutput = string.Join("\n", outputLines.Take(MaxDiffLines))
                    + $"\n[... {omitted} lines omitted — use READ git diff <filename> for specific files]";
            }
            else
            {
                truncatedOutput = output;
            }

            if (string.IsNullOrWhiteSpace(truncatedOutput))
                truncatedOutput = "(no output)";

            AppendOutput($"{header}\n", OutputColor.Success);
            return $"{header}\n```\n{truncatedOutput}\n```\n\n";
        }

        // ── IAgenticHost.AppendOutput ─────────────────────────────────────────────

        void IAgenticHost.AppendOutput(string text, OutputColor color)
            => AppendOutput(text, color);

        // ── IAgenticHost.UpdateScratchpad ─────────────────────────────────────────

       void IAgenticHost.UpdateScratchpad(string content)
        {
            string trimLog = _llmClient.UpdateScratchpad(content);
            if (trimLog != null)
                AppendOutput(trimLog, OutputColor.Dim);
            AppendOutput("[SCRATCHPAD] Updated\n", OutputColor.Dim);
        }

        string IAgenticHost.TaskScratchpad => _llmClient.TaskScratchpad;

        // ── IAgenticHost.GetWorkingDirectory ──────────────────────────────────────

        string IAgenticHost.GetWorkingDirectory() => _shellRunner.WorkingDirectory;

        // ── IAgenticHost.GrepFileAsync ────────────────────────────────────────────

        async Task<string> IAgenticHost.GrepFileAsync(string pattern, string filename, int? startLine, int? endLine)
        {
            const int MaxMatches = 50;

            // Resolve the file
            string fileNameOnly;
            try { fileNameOnly = Path.GetFileName(filename.Replace('\\', '/')); }
            catch { fileNameOnly = filename; }

            string resolvedPath = await FindFileInSolutionAsync(fileNameOnly, filename.Replace('\\', '/'))
                ?? Path.Combine(_shellRunner.WorkingDirectory, filename);

            if (!File.Exists(resolvedPath))
                return await BuildFileNotFoundMessageAsync("GREP", filename);

            // Populate cache if needed
            if (!_llmClient.FileCache.Contains(fileNameOnly))
            {
                string diskContent;
                try { diskContent = File.ReadAllText(resolvedPath); }
                catch (Exception ex) { return $"GREP: error reading {filename} — {ex.Message}"; }
                _llmClient.FileCache.Store(fileNameOnly, diskContent);
            }

            // Get lines from cache
            int totalFileLines = _llmClient.FileCache.GetLineCount(fileNameOnly);
            int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
            int scanEnd   = endLine.HasValue   ? Math.Min(totalFileLines, endLine.Value) : totalFileLines;

            // Collect matches
            var matches = new System.Collections.Generic.List<(int lineNum, string lineText)>();
            for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
            {
                string lineContent = _llmClient.FileCache.GetLineRange(fileNameOnly, lineNum, lineNum);
                if (lineContent == null) continue;
                if (lineContent.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    matches.Add((lineNum, lineContent));
            }

            if (matches.Count == 0)
            {
                string noMatch = $"GREP: no matches for \"{pattern}\" in {filename}";
                AppendOutput($"[GREP] no matches for \"{pattern}\" in {filename}\n", OutputColor.Dim);
                return noMatch;
            }

            int totalMatches = matches.Count;
            bool truncated = totalMatches > MaxMatches;
            if (truncated)
                matches = matches.GetRange(0, MaxMatches);

            // Right-align line numbers to the width of the largest line number shown
            int maxLineNum = matches[matches.Count - 1].lineNum;
            int numWidth = maxLineNum.ToString().Length;

            string header = truncated
                ? $"GREP results for \"{pattern}\" in {filename} ({MaxMatches} of {totalMatches} matches — narrow your pattern or use a line range):"
                : $"GREP results for \"{pattern}\" in {filename} ({totalMatches} match{(totalMatches == 1 ? "" : "es")}):";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(header);
            foreach (var (lineNum, lineText) in matches)
                sb.AppendLine($"  {lineNum.ToString().PadLeft(numWidth)}: {lineText.TrimEnd()}");

            string result = sb.ToString().TrimEnd('\r', '\n');

            _taskReadFiles.Add(fileNameOnly);
            AppendOutput($"[GREP] {totalMatches} match{(totalMatches == 1 ? "" : "es")} for \"{pattern}\" in {filename}\n", OutputColor.Success);

            return result;
        }

        // ── IAgenticHost.FindInFilesAsync ─────────────────────────────────────────

        async Task<string> IAgenticHost.FindInFilesAsync(string pattern, string globPattern, int? startLine, int? endLine)
        {
            const int MaxMatches = 100;

            // Determine search root (project directory preferred, fallback to working dir)
            string searchDir = _shellRunner.WorkingDirectory;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                var project = dte?.ActiveDocument?.ProjectItem?.ContainingProject;
                if (project != null)
                {
                    string projFile = project.FullName;
                    if (!string.IsNullOrEmpty(projFile))
                        searchDir = Path.GetDirectoryName(projFile);
                }
            }
            catch { }

            // Split glob into directory prefix and file pattern
            // e.g. "Services/*.cs" → dir="Services", filePattern="*.cs"
            string normalizedGlob = globPattern.Replace('\\', '/');
            string filePattern = normalizedGlob;
            string effectiveRoot = searchDir;
            int lastSlash = normalizedGlob.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                string dirPart = normalizedGlob.Substring(0, lastSlash);
                filePattern    = normalizedGlob.Substring(lastSlash + 1);
                string candidate = Path.Combine(searchDir, dirPart.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(candidate))
                    effectiveRoot = candidate;
            }

            // Enumerate matching files (sorted for deterministic output)
            IEnumerable<string> files;
            try
            {
                files = ContextEngine.SafeEnumerateFilesGlob(effectiveRoot, filePattern)
                    .Where(f => !ContextEngine.IsNoisePath(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                return $"FIND: error enumerating files for {globPattern} — {ex.Message}";
            }

            var allMatches = new List<(string fileLabel, int lineNum, string lineText)>();
            bool hitCap = false;

            foreach (string filePath in files)
            {
                if (hitCap) break;

                string fileNameOnly;
                try { fileNameOnly = Path.GetFileName(filePath); }
                catch { fileNameOnly = filePath; }

                // Populate cache if needed
                if (!_llmClient.FileCache.Contains(fileNameOnly))
                {
                    string diskContent;
                    try { diskContent = File.ReadAllText(filePath); }
                    catch { continue; }
                    _llmClient.FileCache.Store(fileNameOnly, diskContent);
                }

                int totalFileLines = _llmClient.FileCache.GetLineCount(fileNameOnly);
                int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
                int scanEnd   = endLine.HasValue   ? Math.Min(totalFileLines, endLine.Value) : totalFileLines;

                for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
                {
                    string lineContent = _llmClient.FileCache.GetLineRange(fileNameOnly, lineNum, lineNum);
                    if (lineContent == null) continue;
                    if (lineContent.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        allMatches.Add((fileNameOnly, lineNum, lineContent));
                        if (allMatches.Count >= MaxMatches)
                        {
                            hitCap = true;
                            break;
                        }
                    }
                }
            }

            if (allMatches.Count == 0)
            {
                string noMatch = $"FIND: no matches for \"{pattern}\" in {globPattern}";
                AppendOutput($"[FIND] no matches for \"{pattern}\" in {globPattern}\n", OutputColor.Dim);
                return noMatch;
            }

            int shownCount = allMatches.Count;
            string header = hitCap
                ? $"FIND results for \"{pattern}\" in {globPattern} ({MaxMatches}+ matches — narrow your pattern or add a line range):"
                : $"FIND results for \"{pattern}\" in {globPattern} ({shownCount} match{(shownCount == 1 ? "" : "es")}):";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(header);
            foreach (var (fileLabel, lineNum, lineText) in allMatches)
                sb.AppendLine($"  {fileLabel}:{lineNum}: {lineText.TrimEnd()}");

            string result = sb.ToString().TrimEnd('\r', '\n');

            AppendOutput($"[FIND] {(hitCap ? MaxMatches + "+" : shownCount.ToString())} match{(shownCount == 1 ? "" : "es")} for \"{pattern}\" in {globPattern}\n", OutputColor.Success);

            return result;
        }

        // ── IAgenticHost.ListFilesAsync ───────────────────────────────────────────

        async Task<string> IAgenticHost.ListFilesAsync(string glob, bool recursive, CancellationToken cancellationToken)
        {
            const int Cap = 200;

            // Resolve search root (project directory preferred, fallback to working dir)
            string searchDir = _shellRunner.WorkingDirectory;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                var project = dte?.ActiveDocument?.ProjectItem?.ContainingProject;
                if (project != null)
                {
                    string projFile = project.FullName;
                    if (!string.IsNullOrEmpty(projFile))
                        searchDir = Path.GetDirectoryName(projFile);
                }
            }
            catch { }

            if (string.IsNullOrEmpty(searchDir))
                return "[ERROR: project root unresolved]";

            // Split glob into directory prefix and file pattern (e.g. "Services/*.cs" → dir="Services", pattern="*.cs")
            string normalizedGlob = (glob ?? "").Replace('\\', '/');
            string filePattern = normalizedGlob;
            string effectiveRoot = searchDir;
            int lastSlash = normalizedGlob.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                string dirPart = normalizedGlob.Substring(0, lastSlash);
                filePattern = normalizedGlob.Substring(lastSlash + 1);
                string candidate = Path.Combine(searchDir, dirPart.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(candidate))
                    effectiveRoot = candidate;
            }

            if (string.IsNullOrWhiteSpace(filePattern))
                return "[ERROR: glob pattern is empty]";

            IEnumerable<string> matches;
            try
            {
                if (recursive)
                    matches = ContextEngine.SafeEnumerateFilesGlob(effectiveRoot, filePattern).Where(f => !ContextEngine.IsNoisePath(f));
                else
                    matches = Directory.EnumerateFiles(effectiveRoot, filePattern, SearchOption.TopDirectoryOnly)
                        .Where(f => !ContextEngine.IsNoisePath(f));
            }
            catch (Exception ex)
            {
                return $"[ERROR: {ex.Message}]";
            }

            var sorted = matches
                .Select(Path.GetFullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sorted.Count == 0)
                return "[no matches]";

            var sb = new System.Text.StringBuilder();
            int shown = Math.Min(sorted.Count, Cap);
            for (int i = 0; i < shown; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sb.AppendLine(sorted[i]);
            }
            if (sorted.Count > Cap)
                sb.AppendLine($"[truncated — {sorted.Count - Cap} more matches]");

            AppendOutput($"[LIST] {shown} file{(shown == 1 ? "" : "s")} matching \"{glob}\"\n", OutputColor.Dim);
            return sb.ToString().TrimEnd();
        }

        // ── IAgenticHost.DeleteFileAsync ──────────────────────────────────────────

        async Task<string> IAgenticHost.DeleteFileAsync(string filename)
        {
            string fileNameOnly;
            try { fileNameOnly = Path.GetFileName(filename.Replace('\\', '/')); }
            catch { fileNameOnly = filename; }

            string resolvedPath = await FindFileInSolutionAsync(fileNameOnly, filename.Replace('\\', '/'))
                ?? Path.Combine(_shellRunner.WorkingDirectory, filename);

            if (!File.Exists(resolvedPath))
                return await BuildFileNotFoundMessageAsync("DELETE", filename);

            // Close the file in the VS editor if it is open
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                if (dte?.Documents != null)
                {
                    foreach (EnvDTE.Document doc in dte.Documents)
                    {
                        if (string.Equals(doc.FullName, resolvedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            doc.Close(EnvDTE.vsSaveChanges.vsSaveChangesNo);
                            break;
                        }
                    }
                }
            }
            catch { }

            try
            {
                File.Delete(resolvedPath);
                return $"Deleted: {resolvedPath}";
            }
            catch (Exception ex)
            {
                return $"DELETE: failed to delete {resolvedPath} — {ex.Message}";
            }
        }

        // ── IAgenticHost.RenameFileAsync ──────────────────────────────────────────

        async Task<string> IAgenticHost.RenameFileAsync(string oldFilename, string newFilename)
        {
            string oldNameOnly;
            try { oldNameOnly = Path.GetFileName(oldFilename.Replace('\\', '/')); }
            catch { oldNameOnly = oldFilename; }

            string oldPath = await FindFileInSolutionAsync(oldNameOnly, oldFilename.Replace('\\', '/'))
                ?? Path.Combine(_shellRunner.WorkingDirectory, oldFilename);

            if (!File.Exists(oldPath))
                return await BuildFileNotFoundMessageAsync("RENAME", oldFilename);

            // Build destination path
            string newPath;
            bool newHasDir = newFilename.Contains('/') || newFilename.Contains('\\');
            if (newHasDir)
            {
                // Treat as relative to project/working directory
                string projectDir = Path.GetDirectoryName(oldPath) ?? _shellRunner.WorkingDirectory;
                newPath = Path.Combine(projectDir, newFilename.Replace('/', Path.DirectorySeparatorChar));
            }
            else
            {
                // Same directory as the old file, just different name
                newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? _shellRunner.WorkingDirectory, newFilename);
            }

            if (File.Exists(newPath))
                return $"RENAME: destination already exists — {newPath}";

            // Close old file in the VS editor if open
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                if (dte?.Documents != null)
                {
                    foreach (EnvDTE.Document doc in dte.Documents)
                    {
                        if (string.Equals(doc.FullName, oldPath, StringComparison.OrdinalIgnoreCase))
                        {
                            doc.Close(EnvDTE.vsSaveChanges.vsSaveChangesNo);
                            break;
                        }
                    }
                }
            }
            catch { }

            try
            {
                File.Move(oldPath, newPath);
            }
            catch (Exception ex)
            {
                return $"RENAME: failed to rename {oldPath} → {newPath} — {ex.Message}";
            }

            // Invalidate FileContentCache for the old filename
            try { _llmClient.FileCache.Invalidate(oldNameOnly); } catch { }

            // Open new file in VS editor
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await VS.Documents.OpenAsync(newPath);
            }
            catch { }

            return $"Renamed: {oldPath} → {newPath}";
        }

        // ── IAgenticHost.GetFileDiffAsync ─────────────────────────────────────────

        async Task<string> IAgenticHost.GetFileDiffAsync(string filename)
        {
            string fileNameOnly;
            try { fileNameOnly = Path.GetFileName(filename.Replace('\\', '/')); }
            catch { fileNameOnly = filename; }

            string resolvedPath = await FindFileInSolutionAsync(fileNameOnly, filename.Replace('\\', '/'))
                ?? Path.Combine(_shellRunner.WorkingDirectory, filename);

            if (!_fileSnapshots.ContainsKey(resolvedPath))
            {
                string noSnap = $"DIFF: No changes — {filename} has not been modified this session.";
                AppendOutput($"[DIFF] {filename}: not modified this session\n", OutputColor.Dim);
                return noSnap;
            }

            string original = _fileSnapshots[resolvedPath];

            string current;
            try { current = File.ReadAllText(resolvedPath); }
            catch (Exception ex) { return $"DIFF: error reading {filename} — {ex.Message}"; }

            // Normalize line endings before comparison
            string normOld = original.Replace("\r\n", "\n").Replace("\r", "\n");
            string normNew = current.Replace("\r\n", "\n").Replace("\r", "\n");

            if (string.Equals(normOld, normNew, StringComparison.Ordinal))
            {
                string noChange = $"DIFF: No changes detected in {filename}.";
                AppendOutput($"[DIFF] {filename}: no changes\n", OutputColor.Dim);
                return noChange;
            }

            string[] oldLines = normOld.Split('\n');
            string[] newLines = normNew.Split('\n');

            string diffResult = DiffHelper.GenerateUnifiedDiff(filename, oldLines, newLines);

            AppendOutput($"[DIFF] {filename}: changes shown ({oldLines.Length} → {newLines.Length} lines)\n", OutputColor.Dim);

            return diffResult;
        }

        // ── IAgenticHost.RunTestsAsync ────────────────────────────────────────────

        async Task<string> IAgenticHost.RunTestsAsync(string project, string filter)
        {
            const int MaxFailedTests = 10;

            // Auto-detect project when none specified: look for *.csproj in working directory.
            if (string.IsNullOrWhiteSpace(project))
            {
                try
                {
                    string[] csprojFiles = Directory.GetFiles(_shellRunner.WorkingDirectory, "*.csproj", SearchOption.TopDirectoryOnly);
                    if (csprojFiles.Length == 1)
                    {
                        project = csprojFiles[0];
                        AppendOutput($"[TEST] Auto-detected project: {Path.GetFileName(project)}\n", OutputColor.Dim);
                    }
                    else if (csprojFiles.Length > 1)
                    {
                        project = csprojFiles[0];
                        AppendOutput($"[TEST] Multiple .csproj files found — using {Path.GetFileName(project)}\n", OutputColor.Dim);
                    }
                    else
                    {
                        return "[TEST] No project specified and no .csproj found in working directory.";
                    }
                }
                catch
                {
                    return "[TEST] No project specified.";
                }
            }

            // Resolve project path — if it looks like a bare name (no path separators, no .csproj ext),
            // search for a matching .csproj in the solution/working directory.
            string resolvedProject = project;
            bool looksLikeBare = !project.Contains('/') && !project.Contains('\\');
            string projectDir = null;

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                var activeProject = dte?.ActiveDocument?.ProjectItem?.ContainingProject;
                if (activeProject != null)
                {
                    string projFile = activeProject.FullName;
                    if (!string.IsNullOrEmpty(projFile))
                        projectDir = Path.GetDirectoryName(projFile);
                }
            }
            catch { }

            string searchDir = projectDir ?? _shellRunner.WorkingDirectory;

            if (looksLikeBare && !string.IsNullOrEmpty(searchDir))
            {
                // Search for <project>.csproj or just project if it already ends with .csproj
                string searchName = project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    ? project : project + ".csproj";
                try
                {
                    string[] found = Directory.GetFiles(searchDir, searchName, SearchOption.AllDirectories);
                    if (found.Length > 0)
                        resolvedProject = found[0];
                }
                catch { }
            }
            else if (!looksLikeBare && !Path.IsPathRooted(resolvedProject) && !string.IsNullOrEmpty(searchDir))
            {
                resolvedProject = Path.Combine(searchDir, resolvedProject.Replace('/', Path.DirectorySeparatorChar));
            }

            // Determine a unique TRX output path so we can parse it after the run
            string trxDir  = Path.Combine(Path.GetTempPath(), "DevMind", "TestResults");
            string trxFile = Path.Combine(trxDir, "devmind_test.trx");
            try { Directory.CreateDirectory(trxDir); } catch { }
            // Remove stale TRX from a previous run
            try { if (File.Exists(trxFile)) File.Delete(trxFile); } catch { }

            // Build command — quote project path if it contains spaces
            string quotedProject = (resolvedProject ?? project).Contains(' ')
                ? $"\"{resolvedProject ?? project}\"" : (resolvedProject ?? project);
            string filterArg = !string.IsNullOrWhiteSpace(filter)
                ? $" --filter \"{filter.Trim('\"')}\"" : "";

            // Phase 1: try with TRX logger for structured output
            string cmd = $"dotnet test {quotedProject} --no-build --verbosity quiet" +
                         $" --logger \"trx;LogFileName={trxFile}\"{filterArg}";

            AppendOutput($"[TEST] > {cmd}\n", OutputColor.Dim);

            string rawOutput = null;
            int exitCode = -1;

            try
            {
                var result = await _shellRunner.ExecuteAsync(cmd, _cts?.Token ?? CancellationToken.None);
                rawOutput = result.output;
                exitCode  = result.exitCode;
            }
            catch (Exception ex)
            {
                AppendOutput($"[TEST] Shell execution failed: {ex.Message}\n", OutputColor.Dim);
            }

            // Try TRX parsing first
            try
            {
                if (File.Exists(trxFile))
                {
                    string trxContent = File.ReadAllText(trxFile);
                    if (!string.IsNullOrWhiteSpace(trxContent))
                    {
                        string summary = ParseTrxSummary(trxContent, MaxFailedTests, resolvedProject ?? project, filter);
                        try { File.Delete(trxFile); } catch { }
                        if (!string.IsNullOrWhiteSpace(summary))
                            return summary;
                    }
                }
            }
            catch (Exception trxEx)
            {
                AppendOutput($"[TEST] TRX parse failed: {trxEx.Message}\n", OutputColor.Dim);
            }

            // If we got usable raw output from the TRX run, return it
            if (!string.IsNullOrWhiteSpace(rawOutput) && rawOutput != "(no output)")
                return rawOutput;

            // Phase 2: fallback — re-run without --no-build and without TRX logger
            // so we get plain console output even for projects that don't produce TRX
            string fallbackCmd = $"dotnet test {quotedProject} --verbosity normal{filterArg}";
            AppendOutput($"[TEST] TRX unavailable, falling back to console output.\n", OutputColor.Dim);
            AppendOutput($"[TEST] > {fallbackCmd}\n", OutputColor.Dim);

            try
            {
                var fallback = await _shellRunner.ExecuteAsync(fallbackCmd, _cts?.Token ?? CancellationToken.None);
                rawOutput = fallback.output;
                exitCode  = fallback.exitCode;
            }
            catch (Exception ex)
            {
                return $"[TEST] Failed to run tests: {ex.Message}";
            }

            return string.IsNullOrWhiteSpace(rawOutput)
                ? $"TEST: no output (exit code {exitCode})"
                : rawOutput;
        }

        /// <summary>
        /// Parses a TRX XML file and returns a compact test results summary.
        /// Only failed tests show details; passed/skipped get summary counts only.
        /// </summary>
        private static string ParseTrxSummary(string trxXml, int maxFailedTests, string project, string filter)
        {
            if (string.IsNullOrWhiteSpace(trxXml))
                return null;

            // TRX files use the VS test results namespace
            XDocument doc = XDocument.Parse(trxXml);
            if (doc?.Root == null)
                return null;

            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

            // Counters element: total, passed, failed, etc.
            var counters = doc.Descendants(ns + "Counters")?.FirstOrDefault();
            int total   = counters != null ? (int?)counters.Attribute("total")   ?? 0 : 0;
            int passed  = counters != null ? (int?)counters.Attribute("passed")  ?? 0 : 0;
            int failed  = counters != null ? (int?)counters.Attribute("failed")  ?? 0 : 0;
            int skipped = total - passed - failed;
            if (skipped < 0) skipped = 0;

            // Handle zero tests found
            if (total == 0)
            {
                string projectShort = Path.GetFileName(project ?? "unknown");
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    return $"[TEST] No tests found matching filter \"{filter.Trim('\"')}\" in {projectShort}";
                }
                return $"[TEST] No tests found in {projectShort} — verify the project references MSTest.TestFramework or another test framework";
            }

            // Duration from TestRun summary
            double totalSecs = 0;
            var runInfos = doc.Descendants(ns + "Times")?.FirstOrDefault();
            if (runInfos != null)
            {
                string start  = (string)runInfos.Attribute("start");
                string finish = (string)runInfos.Attribute("finish");
                if (DateTime.TryParse(start, out DateTime s) && DateTime.TryParse(finish, out DateTime f))
                    totalSecs = (f - s).TotalSeconds;
            }

            string durationStr = totalSecs > 0 ? $"{totalSecs:0.#}s" : "";

            var sb = new System.Text.StringBuilder();
            sb.Append($"TEST RESULTS: {passed} passed, {failed} failed, {skipped} skipped ({total} total");
            if (!string.IsNullOrEmpty(durationStr)) sb.Append($", {durationStr}");
            sb.AppendLine(")");

            if (failed > 0)
            {
                sb.AppendLine();
                sb.AppendLine("FAILED:");

                var failedResults = doc.Descendants(ns + "UnitTestResult");
                var unitTestResults = failedResults != null
                    ? failedResults
                        .Where(r => string.Equals((string)r.Attribute("outcome"), "Failed", StringComparison.OrdinalIgnoreCase))
                        .Take(maxFailedTests)
                        .ToList()
                    : new List<XElement>();

                foreach (var tr in unitTestResults)
                {
                    string name = (string)tr.Attribute("testName") ?? "Unknown";
                    string dur  = (string)tr.Attribute("duration") ?? "";
                    // duration is like "00:00:00.0500000" — convert to seconds
                    string durStr = "";
                    if (TimeSpan.TryParse(dur, out TimeSpan durTs))
                        durStr = $" ({durTs.TotalSeconds:0.##}s)";

                    sb.AppendLine($"  {name}{durStr}");

                    // Error message from Output/ErrorInfo/Message
                    var messages = tr.Descendants(ns + "Message");
                    var errorMsg = messages?.FirstOrDefault()?.Value;
                    if (!string.IsNullOrWhiteSpace(errorMsg))
                    {
                        // Trim to first 3 lines to keep output compact
                        string[] errorLines = errorMsg.Trim().Split('\n');
                        int showLines = Math.Min(errorLines.Length, 3);
                        for (int i = 0; i < showLines; i++)
                            sb.AppendLine($"    {errorLines[i].TrimEnd()}");
                    }
                }

                if (failed > maxFailedTests)
                    sb.AppendLine($"  ... and {failed - maxFailedTests} more failed test(s) (truncated)");
            }

            if (passed > 0)
            {
                sb.AppendLine();
                string passedDur = totalSecs > 0 && failed == 0 ? $" ({totalSecs:0.#}s)" : "";
                sb.AppendLine($"PASSED: {passed} test{(passed == 1 ? "" : "s")}{passedDur}");
            }

            if (skipped > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"SKIPPED: {skipped} test{(skipped == 1 ? "" : "s")}");
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        // ── IAgenticHost.ResolvePatchAsync ───────────────────────────────────────

        async Task<PatchResolveResult> IAgenticHost.ResolvePatchAsync(string patchContent, bool fromToolCall)
        {
            // Extract filename for auto-READ and write guard
            string firstLine = (patchContent ?? string.Empty).Split('\n')[0];
            string blockFileName = firstLine.Length > 5 ? firstLine.Substring(5).Trim() : string.Empty;

            // Unrelated-file write guard
            if (!string.IsNullOrEmpty(blockFileName))
            {
                string guardFileOnly;
                try { guardFileOnly = Path.GetFileName(blockFileName.Replace('\\', '/')); }
                catch { guardFileOnly = blockFileName; }

                if (!IsFileKnownToTask(guardFileOnly))
                {
                    bool approved = await ConfirmUnreadFileWriteAsync(guardFileOnly);
                    if (!approved)
                    {
                        AppendOutput($"[WRITE GUARD] Patch to \"{guardFileOnly}\" blocked by user.\n", OutputColor.Dim);
                        return null;
                    }
                    _taskReadFiles.Add(guardFileOnly);
                }
            }

            // Auto-READ target file
            if (!string.IsNullOrEmpty(blockFileName))
            {
                string patchFileOnly;
                try { patchFileOnly = Path.GetFileName(blockFileName.Replace('\\', '/')); }
                catch { patchFileOnly = blockFileName; }

                if (!string.IsNullOrEmpty(patchFileOnly))
                {
                    string resolvedPath =
                        await FindFileInSolutionAsync(patchFileOnly, blockFileName.Replace('\\', '/'))
                        ?? Path.Combine(_shellRunner.WorkingDirectory, patchFileOnly);

                    if (!_llmClient.FileCache.Contains(patchFileOnly))
                    {
                        AppendOutput($"[AUTO-READ] Loading {patchFileOnly} before patch...\n", OutputColor.Dim);
                        await ApplyReadCommandAsync($"READ {blockFileName}");
                    }

                    if (File.Exists(resolvedPath))
                        CaptureFileSnapshot(resolvedPath);
                }
            }

            return await ResolvePatchAsync(patchContent, fromToolCall);
        }

        // ── IAgenticHost.ApplyResolvedPatchAsync ────────────────────────────────

        async Task<string> IAgenticHost.ApplyResolvedPatchAsync(PatchResolveResult resolved)
        {
            return await ApplyResolvedPatchAsync(resolved);
        }

        // ── IAgenticHost.ShowDiffPreviewAsync ───────────────────────────────────

        async Task<List<int>> IAgenticHost.ShowDiffPreviewAsync(
            List<PatchResolveResult> resolvedPatches,
            CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var cards = new List<DiffPreviewCard>();
            var approvedIndices = new List<int>();

            // Create all diff preview cards
            for (int i = 0; i < resolvedPatches.Count; i++)
            {
                var resolved = resolvedPatches[i];
                var card = new DiffPreviewCard();
                card.ResolveResult = resolved;
                card.ConfigureMultiBlock(
                    resolved.FileName,
                    resolved.Confidence,
                    resolved.ParsedPairs,
                    resolved.OriginalContent);

                cards.Add(card);

                // Add card inline into the output panel
                var container = new BlockUIContainer(card) { Margin = new Thickness(0) };
                OutputBox.Document.Blocks.Add(container);
            }

            // Add batch bar if more than one card
            DiffBatchBar batchBar = null;
            if (cards.Count > 1)
            {
                batchBar = new DiffBatchBar(cards);
                var batchContainer = new BlockUIContainer(batchBar) { Margin = new Thickness(0) };
                OutputBox.Document.Blocks.Add(batchContainer);
            }

            OutputBox.ScrollToEnd();

            // Register cancellation to cancel all pending cards
            using (cancellationToken.Register(() =>
            {
                // Fire-and-forget dispatch to UI thread is intentional — cancellation callback
                // runs on a thread-pool thread and must not block.
#pragma warning disable VSTHRD001, VSTHRD110
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var card in cards)
                        card.Cancel();
                }));
#pragma warning restore VSTHRD001, VSTHRD110
            }))
            {
                // Await all card decisions — keep Stop button enabled while waiting
                _diffPreviewPending = true;
                try
                {
                    var tasks = cards.Select(c => c.UserDecision).ToArray();
                    await Task.WhenAll(tasks);
                }
                finally
                {
                    _diffPreviewPending = false;
                }
            }

            // Collect approved indices — all tasks are completed at this point,
            // so .Result is non-blocking.
            for (int i = 0; i < cards.Count; i++)
            {
#pragma warning disable VSTHRD103
                if (cards[i].UserDecision.Result)
                    approvedIndices.Add(i);
#pragma warning restore VSTHRD103
            }

            return approvedIndices;
        }

        // ── Shared helper: file-not-found message with project file listing ──────

        /// <summary>
        /// Returns a file-not-found message for the given directive, augmented with
        /// a sorted list of *.cs files in the active project directory so the model
        /// can self-correct the filename in one turn.
        /// </summary>
        internal async Task<string> BuildFileNotFoundMessageAsync(string directive, string filename)
        {
            const int MaxFiles = 50;

            string projectDir = null;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                var project = dte?.ActiveDocument?.ProjectItem?.ContainingProject;
                if (project != null)
                {
                    string projFile = project.FullName;
                    if (!string.IsNullOrEmpty(projFile))
                        projectDir = Path.GetDirectoryName(projFile);
                }
            }
            catch { }

            string searchDir = projectDir ?? _shellRunner.WorkingDirectory;

            List<string> csFiles = null;
            try
            {
                csFiles = Directory.GetFiles(searchDir, "*.cs", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { }

            if (csFiles == null || csFiles.Count == 0)
                return $"{directive}: file not found — {filename}";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{directive}: file not found — {filename}");
            sb.AppendLine("Project files:");

            int shown = Math.Min(csFiles.Count, MaxFiles);
            for (int i = 0; i < shown; i++)
                sb.AppendLine($"  {csFiles[i]}");

            if (csFiles.Count > MaxFiles)
                sb.AppendLine($"  ... and {csFiles.Count - MaxFiles} more");

            return sb.ToString().TrimEnd('\r', '\n');
        }

        // ── IAgenticHost.RecallMemoryAsync ──────────────────────────────────────

        async Task<string> IAgenticHost.RecallMemoryAsync(string topic)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureMemoryManager();
            if (_memoryManager == null)
                return "Memory not available: no solution open";

            string content = _memoryManager.LoadTopic(topic);
            if (content == null)
            {
                AppendOutput($"[MEMORY] Topic not found: {topic}\n", OutputColor.Dim);
                return $"Topic not found: {topic}";
            }

            AppendOutput($"[MEMORY] Recalled: {topic}\n", OutputColor.Dim);
            return content;
        }

        // ── IAgenticHost.SaveMemoryAsync ────────────────────────────────────────

        async Task<string> IAgenticHost.SaveMemoryAsync(string topic, string content, string description)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureMemoryManager();
            if (_memoryManager == null)
                return "Memory not available: no solution open";

            _memoryManager.SaveTopic(topic, content, description);
            string desc = string.IsNullOrEmpty(description) ? topic : description;
            AppendOutput($"[MEMORY] Saved: [{topic}] {desc}\n", OutputColor.Success);
            return $"Memory saved: [{topic}] {desc}";
        }

        // ── IAgenticHost.ListMemoryTopicsAsync ──────────────────────────────────

        async Task<string> IAgenticHost.ListMemoryTopicsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureMemoryManager();
            if (_memoryManager == null)
                return "Memory not available: no solution open";

            string index = _memoryManager.LoadIndex();
            if (string.IsNullOrWhiteSpace(index))
            {
                var topics = _memoryManager.ListTopics();
                if (topics.Count == 0)
                {
                    AppendOutput("[MEMORY] No memory topics found.\n", OutputColor.Dim);
                    return "No memory topics found. Use save_memory to create one.";
                }
                // Fallback: list topic slugs without descriptions
                string list = string.Join("\n", topics.Select(t => $"- [{t}]"));
                AppendOutput($"[MEMORY] {topics.Count} topic(s) available.\n", OutputColor.Dim);
                return list;
            }

            AppendOutput("[MEMORY] Topics listed.\n", OutputColor.Dim);
            return index;
        }

        int IAgenticHost.GetPatchBackupCount() => _patchBackupStack.Count;
    }

}
