// File: DevMindToolWindowControl.Shell.cs  v5.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using EnvDTE;

namespace DevMind
{
    public partial class DevMindToolWindowControl : UserControl
    {
        // ── Shell ─────────────────────────────────────────────────────────────

        private void RunShellCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return;

            // Fuzzy confirmation intercept — must be handled before any other routing
            if (_pendingFuzzyPatch.HasValue)
            {
                AppendOutput($"\n> {command}\n", OutputColor.Input);
                if (command == "1")
                {
                    var pending = _pendingFuzzyPatch.Value;
                    _pendingFuzzyPatch = null;
                    ApplyPendingFuzzyPatch(pending);
                }
                else if (command == "2")
                {
                    _pendingFuzzyPatch = null;
                    AppendOutput("[PATCH] Fuzzy match cancelled.\n", OutputColor.Dim);
                }
                else
                {
                    AppendOutput("[FUZZY] Pending confirmation — type 1 to apply or 2 to cancel.\n", OutputColor.Dim);
                }
                return;
            }

            _terminalHistory.RemoveAll(h => h == command);
            _terminalHistory.Add(command);
            _terminalHistoryIndex = _terminalHistory.Count;

            AppendOutput($"\n> {command}\n", OutputColor.Input);

            // /reload interception — clears cached DevMind.md so it's re-read on next Ask
            if (command.Equals("/reload", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                _devMindContext = null;
                AppendOutput("DevMind.md context cleared — will reload on next Ask.\n", OutputColor.Dim);
                return;
            }

            // /context — show or clear READ-loaded file context
            if (command.Equals("/context", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("context", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(_readContext))
                {
                    AppendOutput("No READ context loaded.\n", OutputColor.Dim);
                }
                else
                {
                    // Extract filenames from the context headers
                    var contextFiles = System.Text.RegularExpressions.Regex
                        .Matches(_readContext, @"The following files have been loaded for context:\r?\n\r?\n(.+?)\r?\n")
                        .Cast<System.Text.RegularExpressions.Match>()
                        .Select(m => m.Groups[1].Value.Trim())
                        .ToList();
                    AppendOutput($"READ context: {contextFiles.Count} file(s) loaded:\n", OutputColor.Dim);
                    foreach (var f in contextFiles)
                        AppendOutput($"  • {f}\n", OutputColor.Dim);
                    AppendOutput("Type /context clear to remove.\n", OutputColor.Dim);
                }
                return;
            }

            if (command.Equals("/context clear", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("context clear", StringComparison.OrdinalIgnoreCase))
            {
                _readContext = null;
                AppendOutput("READ context cleared.\n", OutputColor.Dim);
                return;
            }

            // cd interception
            if (command.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("cd", StringComparison.OrdinalIgnoreCase))
            {
                string target = command.Length > 3 ? command.Substring(3).Trim() : null;
                if (string.IsNullOrEmpty(target) || target == "~")
                    _terminalWorkingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                else
                {
                    string newDir = Path.IsPathRooted(target)
                        ? target
                        : Path.Combine(_terminalWorkingDir, target);
                    if (Directory.Exists(newDir))
                        _terminalWorkingDir = Path.GetFullPath(newDir);
                    else
                        AppendOutput($"cd: directory not found: {target}\n", OutputColor.Error);
                }
                return;
            }

            bool usePowerShell = IsPowerShellAvailable();
            string shell = usePowerShell ? "powershell.exe" : "cmd.exe";
            string args = usePowerShell
                ? $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\""
                : $"/c \"{command}\"";

            SetInputEnabled(false);
            StatusText.Text = "Running...";

#pragma warning disable VSSDK007 // Fire-and-forget for shell execution is intentional
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
#pragma warning restore VSSDK007
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(shell, args)
                    {
                        WorkingDirectory = _terminalWorkingDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var proc = System.Diagnostics.Process.Start(psi);
                    var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
                    var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());
                    string stdout = await stdoutTask;
                    string stderr = await stderrTask;
                    await Task.Run(() => proc.WaitForExit());

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    if (!string.IsNullOrEmpty(stdout))
                        AppendOutput(stdout.TrimEnd() + "\n", OutputColor.Normal);
                    if (!string.IsNullOrEmpty(stderr))
                        AppendOutput(stderr.TrimEnd() + "\n", OutputColor.Error);
                    if (string.IsNullOrEmpty(stdout) && string.IsNullOrEmpty(stderr))
                        AppendOutput("(no output)\n", OutputColor.Dim);
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AppendOutput($"Error: {ex.Message}\n", OutputColor.Error);
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusText.Text = "Ready";
                    SetInputEnabled(true);
                    InputTextBox.Focus();
                }
            });
        }

        private static bool IsPowerShellAvailable()
        {
            try
            {
                string ps = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                return File.Exists(ps);
            }
            catch { return false; }
        }

        private async Task<(string output, int exitCode)> RunShellCommandCaptureAsync(string command)
        {
            bool usePowerShell = IsPowerShellAvailable();
            string shell = usePowerShell ? "powershell.exe" : "cmd.exe";
            string args = usePowerShell
                ? $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\""
                : $"/c \"{command}\"";

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(shell, args)
                {
                    WorkingDirectory = _terminalWorkingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
                var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());
                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                await Task.Run(() => proc.WaitForExit());
                int exitCode = proc.ExitCode;

                var result = new StringBuilder();
                if (!string.IsNullOrEmpty(stdout)) result.AppendLine(stdout.TrimEnd());
                if (!string.IsNullOrEmpty(stderr)) result.AppendLine(stderr.TrimEnd());
                string output = result.Length > 0 ? result.ToString().TrimEnd() : "(no output)";
                return (output, exitCode);
            }
            catch (Exception ex)
            {
                return ($"(error: {ex.Message})", -1);
            }
        }

        private static List<string> ParseShellDirectives(string response)
        {
            var result = new List<string>();
            foreach (var line in response.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r').Trim();
                if (trimmed.StartsWith("SHELL:", StringComparison.OrdinalIgnoreCase))
                {
                    string cmd = trimmed.Substring("SHELL:".Length).Trim();
                    // Deduplicate consecutive identical commands — skip if same as last added.
                    if (!string.IsNullOrEmpty(cmd) && (result.Count == 0 || result[result.Count - 1] != cmd))
                        result.Add(cmd);
                }
            }
            return result;
        }
    }
}
