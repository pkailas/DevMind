// File: DevMindToolWindowControl.Shell.cs  v5.9
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

            // cd interception
            if (command.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("cd", StringComparison.OrdinalIgnoreCase))
            {
                string target = command.Length > 3 ? command.Substring(3).Trim() : null;
                if (string.IsNullOrEmpty(target) || target == "~")
                    _shellRunner.ChangeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                else if (!_shellRunner.ChangeDirectory(target))
                    AppendOutput($"cd: directory not found: {target}\n", OutputColor.Error);
                return;
            }

            bool usePowerShell = ShellRunner.IsPowerShellAvailable();
            string shell     = usePowerShell ? "powershell.exe" : "cmd.exe";

            // Bash && (run-next-if-succeeded) doesn't work in PowerShell; replace with ; (sequential).
            if (usePowerShell)
                command = command.Replace(" && ", "; ");

            string sanitized = ShellRunner.SanitizeCommand(command);
            string args = usePowerShell
                ? $"-NoProfile -NonInteractive -Command \"{sanitized.Replace("\"", "\\\"")}\""
                : $"/c \"{sanitized}\"";

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            SetInputEnabled(false);
            StatusText.Text = "Running...";

            // Progress<T> captures the current (UI) SynchronizationContext so each Report()
            // is marshalled back to the UI thread — AppendOutput is safe to call directly.
            var progress = new System.Progress<ShellOutputLine>(o =>
                AppendOutput(o.Line + "\n", o.IsError ? OutputColor.Error : OutputColor.Normal));

#pragma warning disable VSSDK007 // Fire-and-forget for shell execution is intentional
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
#pragma warning restore VSSDK007
            {
                try
                {
                    await _shellRunner.ExecuteAsync(
                        command,
                        _cts?.Token ?? CancellationToken.None,
                        onLine: progress);
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

    }
}
