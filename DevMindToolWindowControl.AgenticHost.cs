// File: DevMindToolWindowControl.AgenticHost.cs  v1.2.0
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

namespace DevMind
{
    /// <summary>
    /// Implements <see cref="IAgenticHost"/> on <see cref="DevMindToolWindowControl"/>,
    /// delegating each method to the existing private partial-class methods.
    /// This partial class is the seam between the pure agentic pipeline and VS/UI side effects.
    /// </summary>
    public partial class DevMindToolWindowControl : UserControl, IAgenticHost
    {
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
}
