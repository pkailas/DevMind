// File: DevMindToolWindowControl.Shell.cs  v5.6
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
#pragma warning disable VSSDK007
                    _ = ApplyPendingFuzzyPatchAsync(pending);
#pragma warning restore VSSDK007
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

            // /reload interception — clears cached context so it's re-discovered on next Ask
            if (command.Equals("/reload", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                _devMindContext = null;
                AppendOutput("Project context cleared — will reload on next Ask.\n", OutputColor.Dim);
                return;
            }

            // /agents — list available .agent.md profiles
            if (command.Equals("/agents", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("agents", StringComparison.OrdinalIgnoreCase))
            {
#pragma warning disable VSSDK007
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate { await HandleAgentsListCommandAsync(); });
#pragma warning restore VSSDK007
                return;
            }

            // /agent load <name> — load a specific agent profile
            if (command.StartsWith("/agent load ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("agent load ", StringComparison.OrdinalIgnoreCase))
            {
                string name = command.StartsWith("/agent")
                    ? command.Substring("/agent load ".Length).Trim()
                    : command.Substring("agent load ".Length).Trim();
#pragma warning disable VSSDK007
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate { await HandleAgentLoadCommandAsync(name); });
#pragma warning restore VSSDK007
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
            string shell     = usePowerShell ? "powershell.exe" : "cmd.exe";

            // Bash && (run-next-if-succeeded) doesn't work in PowerShell; replace with ; (sequential).
            if (usePowerShell)
                command = command.Replace(" && ", "; ");

            string sanitized = SanitizeShellCommand(command);
            string args = usePowerShell
                ? $"-NoProfile -NonInteractive -Command \"{sanitized.Replace("\"", "\\\"")}\""
                : $"/c \"{sanitized}\"";

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
                    var stdoutTask   = Task.Run(() => proc.StandardOutput.ReadToEnd());
                    var stderrTask   = Task.Run(() => proc.StandardError.ReadToEnd());
                    var readBothTask = Task.WhenAll(stdoutTask, stderrTask);

                    // 120-second hard timeout — kill and report if exceeded.
                    bool timedOut = await Task.WhenAny(readBothTask, Task.Delay(120_000)) != readBothTask;
                    if (timedOut)
                    {
                        try { proc.Kill(); } catch { }
                        // Brief window for stream readers to drain now that the process is dead.
                        await Task.WhenAny(readBothTask, Task.Delay(5_000));
                    }

                    // Tasks are confirmed completed — .Result will not block.
#pragma warning disable VSTHRD002, VSTHRD103
                    string stdout = stdoutTask.IsCompleted ? stdoutTask.Result : "";
                    string stderr  = stderrTask.IsCompleted ? stderrTask.Result : "";
#pragma warning restore VSTHRD002, VSTHRD103
                    await Task.Run(() => proc.WaitForExit(10_000));

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    if (timedOut)
                        AppendOutput("[SHELL] Command timed out after 120 seconds.\n", OutputColor.Error);
                    if (!string.IsNullOrEmpty(stdout))
                        AppendOutput(stdout.TrimEnd() + "\n", OutputColor.Normal);
                    if (!string.IsNullOrEmpty(stderr))
                        AppendOutput(stderr.TrimEnd() + "\n", OutputColor.Error);
                    if (!timedOut && string.IsNullOrEmpty(stdout) && string.IsNullOrEmpty(stderr))
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

        /// <summary>
        /// Replaces newlines (and tabs) inside quoted strings with a single space, collapsing
        /// runs of whitespace to one space. Content outside quotes is passed through unchanged.
        /// Prevents PowerShell "missing string terminator" errors from multi-line commit messages.
        /// Handles both single- and double-quoted strings; the closing quote must match the opener.
        /// </summary>
        internal static string SanitizeShellCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return command;

            var  sb           = new StringBuilder(command.Length);
            char quoteChar    = '\0'; // '\0' = not inside a quoted string
            bool lastWasSpace = false;

            foreach (char c in command)
            {
                if (quoteChar == '\0')
                {
                    // Outside quotes — pass through unchanged; track opening quote.
                    if (c == '"' || c == '\'')
                        quoteChar = c;
                    sb.Append(c);
                }
                else if (c == quoteChar)
                {
                    // Matching closing quote — exit quoted context.
                    quoteChar    = '\0';
                    lastWasSpace = false;
                    sb.Append(c);
                }
                else if (c == '\r' || c == '\n' || c == '\t')
                {
                    // Newline / tab inside quoted string — replace with space, collapse runs.
                    if (!lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                    // else: skip to collapse consecutive whitespace
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = (c == ' ');
                }
            }

            return sb.ToString();
        }

        private async Task<(string output, int exitCode)> RunShellCommandCaptureAsync(string command)
        {
            bool usePowerShell = IsPowerShellAvailable();
            string shell     = usePowerShell ? "powershell.exe" : "cmd.exe";

            // Bash && (run-next-if-succeeded) doesn't work in PowerShell; replace with ; (sequential).
            if (usePowerShell)
                command = command.Replace(" && ", "; ");

            string sanitized = SanitizeShellCommand(command);
            string args = usePowerShell
                ? $"-NoProfile -NonInteractive -Command \"{sanitized.Replace("\"", "\\\"")}\""
                : $"/c \"{sanitized}\"";

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
                var stdoutTask   = Task.Run(() => proc.StandardOutput.ReadToEnd());
                var stderrTask   = Task.Run(() => proc.StandardError.ReadToEnd());
                var readBothTask = Task.WhenAll(stdoutTask, stderrTask);

                // 120-second hard timeout — kill and report if exceeded.
                bool timedOut = await Task.WhenAny(readBothTask, Task.Delay(120_000)) != readBothTask;
                if (timedOut)
                {
                    try { proc.Kill(); } catch { }
                    // Brief window for stream readers to drain now that the process is dead.
                    await Task.WhenAny(readBothTask, Task.Delay(5_000));
                }

                // Tasks are confirmed completed — .Result will not block.
#pragma warning disable VSTHRD002, VSTHRD103
                string stdout  = stdoutTask.IsCompleted ? stdoutTask.Result : "";
                string stderr  = stderrTask.IsCompleted ? stderrTask.Result : "";
#pragma warning restore VSTHRD002, VSTHRD103
                await Task.Run(() => proc.WaitForExit(10_000));
                int exitCode   = timedOut ? -1 : proc.ExitCode;

                var result = new StringBuilder();
                if (timedOut)
                    result.AppendLine("[SHELL] Command timed out after 120 seconds.");
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
