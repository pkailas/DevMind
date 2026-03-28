// File: DevMindToolWindowControl.AgenticHost.cs  v1.7.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

        // ── IAgenticHost.ApplyPatchAsync ──────────────────────────────────────────

        async Task<string> IAgenticHost.ApplyPatchAsync(string patchContent)
        {
            // Extract the filename from the first line ("PATCH filename") for auto-READ.
            string firstLine = (patchContent ?? string.Empty).Split('\n')[0];
            string blockFileName = firstLine.Length > 5 ? firstLine.Substring(5).Trim() : string.Empty;

            // Auto-READ the target file into context if it is not already present.
            if (!string.IsNullOrEmpty(blockFileName))
            {
                string patchFileOnly;
                try { patchFileOnly = Path.GetFileName(blockFileName.Replace('\\', '/')); }
                catch { patchFileOnly = blockFileName; }

                if (!string.IsNullOrEmpty(patchFileOnly))
                {
                    string resolvedPath =
                        await FindFileInSolutionAsync(patchFileOnly, blockFileName.Replace('\\', '/'))
                        ?? Path.Combine(_terminalWorkingDir, patchFileOnly);

                    bool alreadyLoaded = _readContext != null && _readContext.Contains(resolvedPath);
                    if (!alreadyLoaded)
                    {
                        AppendOutput($"[AUTO-READ] Loading {patchFileOnly} before patch...\n", OutputColor.Dim);
                        await ApplyReadCommandAsync($"READ {blockFileName}");
                    }

                    // Capture pre-patch snapshot for DIFF support
                    if (File.Exists(resolvedPath))
                        CaptureFileSnapshot(resolvedPath);
                }
            }

            AppendOutput($"[AUTO-PATCH] Executing PATCH {blockFileName}...\n", OutputColor.Dim);
            // Returns full path on success, null on failure.
            return await ApplyPatchAsync(patchContent, clearInput: false);
        }

        // ── IAgenticHost.RunShellAsync ────────────────────────────────────────────

        async Task<(int exitCode, string output)> IAgenticHost.RunShellAsync(string command)
        {
            AppendOutput($"[SHELL] > {command}\n", OutputColor.Dim);
            var (output, exitCode) = await RunShellCommandCaptureAsync(command);
            _lastShellExitCode = exitCode;
            _lastShellCommand  = command;
            AppendOutput(output + "\n", OutputColor.Normal);
            return (exitCode, output);
        }

        // ── IAgenticHost.SaveFileAsync ────────────────────────────────────────────

        async Task<string> IAgenticHost.SaveFileAsync(string fileName, string content)
        {
            await SaveGeneratedFileAsync(fileName, StripOuterCodeFence(content));
            // Approximate the resolved path for agentic context / diff view purposes.
            try
            {
                if (Path.IsPathRooted(fileName))
                    return fileName;
                return Path.Combine(_terminalWorkingDir, fileName);
            }
            catch
            {
                return fileName;
            }
        }

        // ── IAgenticHost.LoadFileContentAsync ────────────────────────────────────

        async Task<string> IAgenticHost.LoadFileContentAsync(
            string fileName, int rangeStart, int rangeEnd, bool forceFullRead)
        {
            // Resolve file path and capture original snapshot (for DIFF support) before reading
            try
            {
                string fileNameOnly;
                try { fileNameOnly = Path.GetFileName(fileName.Replace('\\', '/')); }
                catch { fileNameOnly = fileName; }
                string resolvedPath = await FindFileInSolutionAsync(fileNameOnly, fileName.Replace('\\', '/'))
                    ?? Path.Combine(_terminalWorkingDir, fileNameOnly);
                if (File.Exists(resolvedPath))
                    CaptureFileSnapshot(resolvedPath);
            }
            catch { }

            if (rangeStart > 0)
                await ApplyReadRangeAsync(fileName, rangeStart, rangeEnd);
            else if (forceFullRead)
                await ApplyReadCommandAsync("READ! " + fileName, showOutline: true);
            else
                await ApplyReadCommandAsync("READ " + fileName, showOutline: true);

            // Content is injected into LLM context by the above calls; not returned directly.
            return string.Empty;
        }

        // ── IAgenticHost.AppendOutput ─────────────────────────────────────────────

        void IAgenticHost.AppendOutput(string text, OutputColor color)
            => AppendOutput(text, color);

        // ── IAgenticHost.ResubmitPromptAsync ─────────────────────────────────────

        // The main loop handles resubmission by setting InputTextBox.Text and calling
        // SendToLlm(). The executor never needs the raw response string directly.
        Task<string> IAgenticHost.ResubmitPromptAsync(string prompt)
            => Task.FromResult(string.Empty);

        // ── IAgenticHost.ShowConfirmationAsync ───────────────────────────────────

        async Task<bool> IAgenticHost.ShowConfirmationAsync(string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var result = System.Windows.MessageBox.Show(
                message, "DevMind",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            return result == System.Windows.MessageBoxResult.Yes;
        }

        // ── IAgenticHost.UpdateScratchpad ─────────────────────────────────────────

        void IAgenticHost.UpdateScratchpad(string content)
        {
            string trimLog = _llmClient.UpdateScratchpad(content);
            if (trimLog != null)
                AppendOutput(trimLog, OutputColor.Dim);
            AppendOutput("[SCRATCHPAD] Updated\n", OutputColor.Dim);
        }

        // ── IAgenticHost.GetWorkingDirectory ──────────────────────────────────────

        string IAgenticHost.GetWorkingDirectory() => _terminalWorkingDir;

        // ── IAgenticHost.GrepFileAsync ────────────────────────────────────────────

        async Task<string> IAgenticHost.GrepFileAsync(string pattern, string filename, int? startLine, int? endLine)
        {
            const int MaxMatches = 50;

            // Resolve the file
            string fileNameOnly;
            try { fileNameOnly = Path.GetFileName(filename.Replace('\\', '/')); }
            catch { fileNameOnly = filename; }

            string resolvedPath = await FindFileInSolutionAsync(fileNameOnly, filename.Replace('\\', '/'))
                ?? Path.Combine(_terminalWorkingDir, fileNameOnly);

            if (!File.Exists(resolvedPath))
                return await BuildFileNotFoundMessageAsync("GREP", filename);

            // Populate cache if needed
            if (!_llmClient._fileCache.Contains(fileNameOnly))
            {
                string diskContent;
                try { diskContent = File.ReadAllText(resolvedPath); }
                catch (Exception ex) { return $"GREP: error reading {filename} — {ex.Message}"; }
                _llmClient._fileCache.Store(fileNameOnly, diskContent);
            }

            // Get lines from cache
            int totalFileLines = _llmClient._fileCache.GetLineCount(fileNameOnly);
            int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
            int scanEnd   = endLine.HasValue   ? Math.Min(totalFileLines, endLine.Value) : totalFileLines;

            // Collect matches
            var matches = new System.Collections.Generic.List<(int lineNum, string lineText)>();
            for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
            {
                string lineContent = _llmClient._fileCache.GetLineRange(fileNameOnly, lineNum, lineNum);
                if (lineContent == null) continue;
                if (lineContent.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    matches.Add((lineNum, lineContent));
            }

            if (matches.Count == 0)
            {
                string noMatch = $"GREP: no matches for \"{pattern}\" in {filename}";
                _readContext = (_readContext ?? "") + noMatch + "\n\n";
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

            // Inject into read context so the LLM sees the results on resubmit (same pattern as ApplyReadCommandAsync)
            _readContext = (_readContext ?? "") + result + "\n\n";
            AppendOutput($"[GREP] {totalMatches} match{(totalMatches == 1 ? "" : "es")} for \"{pattern}\" in {filename}\n", OutputColor.Success);

            return result;
        }

        // ── IAgenticHost.FindInFilesAsync ─────────────────────────────────────────

        async Task<string> IAgenticHost.FindInFilesAsync(string pattern, string globPattern, int? startLine, int? endLine)
        {
            const int MaxMatches = 100;

            // Determine search root (project directory preferred, fallback to working dir)
            string searchDir = _terminalWorkingDir;
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
                files = SafeEnumerateFilesGlob(effectiveRoot, filePattern)
                    .Where(f => !IsNoisePath(f))
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
                if (!_llmClient._fileCache.Contains(fileNameOnly))
                {
                    string diskContent;
                    try { diskContent = File.ReadAllText(filePath); }
                    catch { continue; }
                    _llmClient._fileCache.Store(fileNameOnly, diskContent);
                }

                int totalFileLines = _llmClient._fileCache.GetLineCount(fileNameOnly);
                int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
                int scanEnd   = endLine.HasValue   ? Math.Min(totalFileLines, endLine.Value) : totalFileLines;

                for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
                {
                    string lineContent = _llmClient._fileCache.GetLineRange(fileNameOnly, lineNum, lineNum);
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
                _readContext = (_readContext ?? "") + noMatch + "\n\n";
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

            _readContext = (_readContext ?? "") + result + "\n\n";
            AppendOutput($"[FIND] {(hitCap ? MaxMatches + "+" : shownCount.ToString())} match{(shownCount == 1 ? "" : "es")} for \"{pattern}\" in {globPattern}\n", OutputColor.Success);

            return result;
        }

        // ── IAgenticHost.DeleteFileAsync ──────────────────────────────────────────

        async Task<string> IAgenticHost.DeleteFileAsync(string filename)
        {
            string fileNameOnly;
            try { fileNameOnly = Path.GetFileName(filename.Replace('\\', '/')); }
            catch { fileNameOnly = filename; }

            string resolvedPath = await FindFileInSolutionAsync(fileNameOnly, filename.Replace('\\', '/'))
                ?? Path.Combine(_terminalWorkingDir, fileNameOnly);

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
                ?? Path.Combine(_terminalWorkingDir, oldNameOnly);

            if (!File.Exists(oldPath))
                return await BuildFileNotFoundMessageAsync("RENAME", oldFilename);

            // Build destination path
            string newPath;
            bool newHasDir = newFilename.Contains('/') || newFilename.Contains('\\');
            if (newHasDir)
            {
                // Treat as relative to project/working directory
                string projectDir = Path.GetDirectoryName(oldPath) ?? _terminalWorkingDir;
                newPath = Path.Combine(projectDir, newFilename.Replace('/', Path.DirectorySeparatorChar));
            }
            else
            {
                // Same directory as the old file, just different name
                newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? _terminalWorkingDir, newFilename);
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
            try { _llmClient._fileCache.Invalidate(oldNameOnly); } catch { }

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
                ?? Path.Combine(_terminalWorkingDir, fileNameOnly);

            if (!_fileSnapshots.ContainsKey(resolvedPath))
            {
                string noSnap = $"DIFF: No changes — {filename} has not been modified this session.";
                _readContext = (_readContext ?? "") + noSnap + "\n\n";
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
                _readContext = (_readContext ?? "") + noChange + "\n\n";
                AppendOutput($"[DIFF] {filename}: no changes\n", OutputColor.Dim);
                return noChange;
            }

            string[] oldLines = normOld.Split('\n');
            string[] newLines = normNew.Split('\n');

            string diffResult = DiffHelper.GenerateUnifiedDiff(filename, oldLines, newLines);
            _readContext = (_readContext ?? "") + diffResult + "\n\n";

            AppendOutput($"[DIFF] {filename}: changes shown ({oldLines.Length} → {newLines.Length} lines)\n", OutputColor.Dim);

            return diffResult;
        }

        // ── IAgenticHost.RunTestsAsync ────────────────────────────────────────────

        async Task<string> IAgenticHost.RunTestsAsync(string project, string filter)
        {
            const int MaxFailedTests = 10;

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

            string searchDir = projectDir ?? _terminalWorkingDir;

            if (looksLikeBare)
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
            else if (!Path.IsPathRooted(resolvedProject))
            {
                resolvedProject = Path.Combine(searchDir, resolvedProject.Replace('/', Path.DirectorySeparatorChar));
            }

            // Determine a unique TRX output path so we can parse it after the run
            string trxDir  = Path.Combine(Path.GetTempPath(), "DevMind", "TestResults");
            string trxFile = Path.Combine(trxDir, "devmind_test.trx");
            try { Directory.CreateDirectory(trxDir); } catch { }
            // Remove stale TRX from a previous run
            try { if (File.Exists(trxFile)) File.Delete(trxFile); } catch { }

            // Build command
            string quotedProject = resolvedProject.Contains(' ')
                ? $"\"{resolvedProject}\"" : resolvedProject;
            string cmd = $"dotnet test {quotedProject} --no-build --verbosity quiet" +
                         $" --logger \"trx;LogFileName={trxFile}\"";
            if (!string.IsNullOrWhiteSpace(filter))
                cmd += $" --filter \"{filter.Trim('\"')}\"";

            AppendOutput($"[TEST] > {cmd}\n", OutputColor.Dim);
            var (rawOutput, exitCode) = await RunShellCommandCaptureAsync(cmd);

            // Try TRX parsing first
            try
            {
                if (File.Exists(trxFile))
                {
                    string trxContent = File.ReadAllText(trxFile);
                    string summary = ParseTrxSummary(trxContent, MaxFailedTests);
                    try { File.Delete(trxFile); } catch { }
                    return summary;
                }
            }
            catch { }

            // Fallback: return raw console output
            return string.IsNullOrWhiteSpace(rawOutput)
                ? $"TEST: no output (exit code {exitCode})"
                : rawOutput;
        }

        /// <summary>
        /// Parses a TRX XML file and returns a compact test results summary.
        /// Only failed tests show details; passed/skipped get summary counts only.
        /// </summary>
        private static string ParseTrxSummary(string trxXml, int maxFailedTests)
        {
            // TRX files use the VS test results namespace
            XDocument doc = XDocument.Parse(trxXml);
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

            // Counters element: total, passed, failed, etc.
            var counters = doc.Descendants(ns + "Counters").FirstOrDefault();
            int total   = counters != null ? (int?)counters.Attribute("total")   ?? 0 : 0;
            int passed  = counters != null ? (int?)counters.Attribute("passed")  ?? 0 : 0;
            int failed  = counters != null ? (int?)counters.Attribute("failed")  ?? 0 : 0;
            int skipped = total - passed - failed;
            if (skipped < 0) skipped = 0;

            // Duration from TestRun summary
            double totalSecs = 0;
            var runInfos = doc.Descendants(ns + "Times").FirstOrDefault();
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

                var unitTestResults = doc.Descendants(ns + "UnitTestResult")
                    .Where(r => string.Equals((string)r.Attribute("outcome"), "Failed", StringComparison.OrdinalIgnoreCase))
                    .Take(maxFailedTests)
                    .ToList();

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
                    var errorMsg = tr.Descendants(ns + "Message").FirstOrDefault()?.Value;
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

            string searchDir = projectDir ?? _terminalWorkingDir;

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

    }

    // ── Diff algorithm helper ─────────────────────────────────────────────────────

    /// <summary>
    /// Produces a simple unified-style diff between two sets of lines.
    /// Used by the DIFF directive to show per-conversation file changes.
    /// </summary>
    internal static class DiffHelper
    {
        private const int Context    = 3;
        private const int MaxOutput  = 200;
        private const long LcsSizeLimit = 2_000_000L; // m*n threshold for LCS vs. positional diff

        public static string GenerateUnifiedDiff(string filename, string[] oldLines, string[] newLines)
        {
            var edits = ComputeEditScript(oldLines, newLines);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"--- {filename} (original)");
            sb.AppendLine($"+++ {filename} (current)");

            // Annotate each edit with absolute 1-based old/new line numbers
            var annotated = new List<(char op, string text, int oldLn, int newLn)>();
            int oldLn = 1, newLn = 1;
            foreach (var (op, text) in edits)
            {
                if      (op == ' ') { annotated.Add((op, text, oldLn, newLn)); oldLn++; newLn++; }
                else if (op == '-') { annotated.Add((op, text, oldLn,      0)); oldLn++; }
                else                { annotated.Add((op, text,      0, newLn)); newLn++; }
            }

            // Mark changed indices
            bool[] changed = new bool[annotated.Count];
            bool anyChange = false;
            for (int i = 0; i < annotated.Count; i++)
            {
                changed[i] = annotated[i].op != ' ';
                if (changed[i]) anyChange = true;
            }

            if (!anyChange)
                return $"DIFF: No changes detected in {filename}.";

            int outputLines = 2; // header lines already appended
            bool truncated  = false;

            int idx = 0;
            while (idx < annotated.Count && !truncated)
            {
                if (!changed[idx]) { idx++; continue; }

                // Expand hunk: merge consecutive change regions within 2*Context gap
                int hunkStart = Math.Max(0, idx - Context);
                int hunkEnd   = idx;
                while (hunkEnd < annotated.Count)
                {
                    int nextChange = hunkEnd + 1;
                    while (nextChange < annotated.Count && !changed[nextChange]) nextChange++;
                    if (nextChange < annotated.Count && nextChange - hunkEnd <= 2 * Context + 1)
                        hunkEnd = nextChange;
                    else
                        break;
                }
                hunkEnd = Math.Min(annotated.Count - 1, hunkEnd + Context);

                // Hunk separator
                if (outputLines < MaxOutput) { sb.AppendLine("---"); outputLines++; }

                for (int i = hunkStart; i <= hunkEnd; i++)
                {
                    if (outputLines >= MaxOutput) { truncated = true; break; }
                    var (op, text, oln, nln) = annotated[i];
                    string lineNum = (op == '+') ? nln.ToString() : oln.ToString();
                    sb.AppendLine($"{op} {lineNum,5}: {text}");
                    outputLines++;
                }

                idx = hunkEnd + 1;
            }

            string result = sb.ToString().TrimEnd('\r', '\n');
            if (truncated)
                result += $"\n... (truncated at {MaxOutput} lines — use READ for full file)";
            return result;
        }

        // ── Edit script computation ───────────────────────────────────────────────

        private static List<(char op, string text)> ComputeEditScript(string[] a, string[] b)
        {
            int m = a.Length, n = b.Length;

            // For very large file pairs, fall back to positional comparison to avoid OOM
            if ((long)m * n > LcsSizeLimit)
                return ComputePositionalEditScript(a, b);

            // Standard LCS DP (bottom-up, suffix direction)
            int[,] dp = new int[m + 1, n + 1];
            for (int i = m - 1; i >= 0; i--)
                for (int j = n - 1; j >= 0; j--)
                    dp[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                        ? dp[i + 1, j + 1] + 1
                        : Math.Max(dp[i + 1, j], dp[i, j + 1]);

            // Backtrack
            var result = new List<(char, string)>(m + n);
            int ai = 0, bi = 0;
            while (ai < m && bi < n)
            {
                if (string.Equals(a[ai], b[bi], StringComparison.Ordinal))
                    { result.Add((' ', a[ai])); ai++; bi++; }
                else if (dp[ai + 1, bi] >= dp[ai, bi + 1])
                    { result.Add(('-', a[ai])); ai++; }
                else
                    { result.Add(('+', b[bi])); bi++; }
            }
            while (ai < m) { result.Add(('-', a[ai])); ai++; }
            while (bi < n) { result.Add(('+', b[bi])); bi++; }

            return result;
        }

        // Positional fallback for large files: compares lines by index, not LCS
        private static List<(char op, string text)> ComputePositionalEditScript(string[] a, string[] b)
        {
            var result = new List<(char, string)>(Math.Max(a.Length, b.Length));
            int maxLen = Math.Max(a.Length, b.Length);
            for (int i = 0; i < maxLen; i++)
            {
                if (i < a.Length && i < b.Length)
                {
                    if (string.Equals(a[i], b[i], StringComparison.Ordinal))
                        result.Add((' ', a[i]));
                    else
                        { result.Add(('-', a[i])); result.Add(('+', b[i])); }
                }
                else if (i < a.Length) result.Add(('-', a[i]));
                else                   result.Add(('+', b[i]));
            }
            return result;
        }
    }
}
