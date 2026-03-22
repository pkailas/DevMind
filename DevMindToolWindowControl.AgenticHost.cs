// File: DevMindToolWindowControl.AgenticHost.cs  v1.0.2
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Microsoft.VisualStudio.Shell;
using System;
using System.IO;
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
            var result = MessageBox.Show(
                message, "DevMind",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
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

    }
}
