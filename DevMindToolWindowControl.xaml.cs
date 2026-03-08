// File: DevMindToolWindowControl.xaml.cs  v4.4.4
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
    /// <summary>
    /// WPF control for the DevMind tool window.
    /// Displays a single-stream output view with Ask (LLM) and Run (shell) commands.
    /// </summary>
    public partial class DevMindToolWindowControl : UserControl
    {
        private readonly LlmClient _llmClient;
        private CancellationTokenSource _cts;
        private bool _suppressSystemPromptSave;
        private string _terminalWorkingDir;
        private readonly List<string> _terminalHistory = new List<string>();
        private int _terminalHistoryIndex = -1;
        private System.Windows.Threading.DispatcherTimer _generatingTimer;
        private Run _generatingRun;
        private string _devMindContext;
        private string _readContext;
        private int _generatingTokenCount;
        private bool _inThinkBlock;
        private readonly StringBuilder _thinkBuffer = new StringBuilder();

        public DevMindToolWindowControl(LlmClient llmClient)
        {
            InitializeComponent();
            Themes.SetUseVsTheme(this, true);
            _llmClient = llmClient;
            _terminalWorkingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

#pragma warning disable VSSDK007 // Fire-and-forget to resolve solution directory is intentional
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
#pragma warning restore VSSDK007
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = await VS.GetServiceAsync<DTE, DTE>();
                    if (dte?.Solution?.FullName is string sln && !string.IsNullOrEmpty(sln))
                        _terminalWorkingDir = Path.GetDirectoryName(sln);
                }
                catch { }
            });

            LoadSystemPromptText();
            DevMindOptions.Saved += OnSettingsSaved;
            AppendOutput("DevMind v3.0 — local LLM assistant\nType a message and click Ask, or a shell command and click Run.\n", OutputColor.Dim);
        }

        // ── Output color ──────────────────────────────────────────────────────

        private enum OutputColor { Normal, Dim, Input, Error, Success }

        private void AppendOutput(string text, OutputColor color = OutputColor.Normal)
        {
            if (!(OutputBox.Document.Blocks.LastBlock is Paragraph para))
            {
                para = new Paragraph { Margin = new Thickness(0) };
                OutputBox.Document.Blocks.Add(para);
            }

            var run = new Run(text)
            {
                Foreground = color switch
                {
                    OutputColor.Dim     => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    OutputColor.Input   => new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)),
                    OutputColor.Error   => new SolidColorBrush(Color.FromRgb(0xF4, 0x48, 0x47)),
                    OutputColor.Success => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x4E)),
                    _                   => new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                }
            };
            para.Inlines.Add(run);
            OutputBox.ScrollToEnd();
        }

        private void AppendNewLine()
        {
            OutputBox.Document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        }

        // ── Status ────────────────────────────────────────────────────────────

        public void SetStatus(string status) => StatusText.Text = status;

        // ── Settings ──────────────────────────────────────────────────────────

        private void OnSettingsSaved(DevMindOptions options)
        {
            _llmClient.Configure(options.EndpointUrl, options.ApiKey);
            LoadSystemPromptText();
            TestConnectionInBackground();
        }

        private void LoadSystemPromptText()
        {
            _suppressSystemPromptSave = true;
            SystemPromptTextBox.Text = DevMindOptions.Instance.SystemPrompt ?? "";
            _suppressSystemPromptSave = false;
        }

        private void TestConnectionInBackground()
        {
#pragma warning disable VSSDK007 // Fire-and-forget from settings change handler is intentional
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
#pragma warning restore VSSDK007
            {
                bool connected = await _llmClient.TestConnectionAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusText.Text = connected ? "Connected" : "Disconnected";
            });
        }

        // ── System prompt panel ───────────────────────────────────────────────

        private void SystemPromptToggle_Checked(object sender, RoutedEventArgs e)
        {
            SystemPromptPanel.Visibility = Visibility.Visible;
            SystemPromptToggle.Content = "System Prompt \u25B2";
        }

        private void SystemPromptToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            SystemPromptPanel.Visibility = Visibility.Collapsed;
            SystemPromptToggle.Content = "System Prompt \u25BC";
        }

        private void SystemPromptTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSystemPromptSave)
                return;

            DevMindOptions.Instance.SystemPrompt = SystemPromptTextBox.Text;
            DevMindOptions.Instance.Save();
        }

        // ── Input handling ────────────────────────────────────────────────────

        private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                bool ctrl  = Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl);

                if (ctrl)
                {
                    e.Handled = true;
                    RunShellCommand();
                }
                else if (!shift)
                {
                    e.Handled = true;
                    SendToLlm();
                }
                // shift+enter falls through — inserts newline naturally
            }
            else if (e.Key == Key.Up)
            {
                if (_terminalHistory.Count == 0) return;
                _terminalHistoryIndex = Math.Max(0, _terminalHistoryIndex - 1);
                InputTextBox.Text = _terminalHistory[_terminalHistoryIndex];
                InputTextBox.CaretIndex = InputTextBox.Text.Length;
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (_terminalHistory.Count == 0) return;
                _terminalHistoryIndex = Math.Min(_terminalHistory.Count, _terminalHistoryIndex + 1);
                InputTextBox.Text = _terminalHistoryIndex < _terminalHistory.Count
                    ? _terminalHistory[_terminalHistoryIndex]
                    : "";
                InputTextBox.CaretIndex = InputTextBox.Text.Length;
                e.Handled = true;
            }
        }

        private void AskButton_Click(object sender, RoutedEventArgs e) => SendToLlm();

        private void RunButton_Click(object sender, RoutedEventArgs e) => RunShellCommand();

        private void StopButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            // Clear output
            OutputBox.Document.Blocks.Clear();
            OutputBox.Document.Blocks.Add(new Paragraph());

            // Reset think filter state
            _inThinkBlock = false;
            _thinkBuffer.Clear();

            // Reset token counter
            _generatingTokenCount = 0;

            // Clear terminal history
            _terminalHistory.Clear();
            _terminalHistoryIndex = 0;

            // Force DevMind.md reload on next Ask
            _devMindContext = null;

            AppendOutput("DevMind restarted.\n", OutputColor.Dim);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            OutputBox.Document.Blocks.Clear();
            _llmClient.ClearHistory();
            StatusText.Text = "Cleared";
        }

        private void SetInputEnabled(bool enabled)
        {
            InputTextBox.IsEnabled = enabled;
            AskButton.IsEnabled = enabled;
            RunButton.IsEnabled = enabled;
            StopButton.IsEnabled = !enabled;
        }

        // ── Think-block filter ────────────────────────────────────────────────

        private string FilterChunk(string chunk)
        {
            if (_inThinkBlock)
            {
                _thinkBuffer.Append(chunk);
                int closeIdx = _thinkBuffer.ToString().IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (closeIdx >= 0)
                {
                    string after = _thinkBuffer.ToString().Substring(closeIdx + "</think>".Length);
                    _inThinkBlock = false;
                    _thinkBuffer.Clear();
                    return after;
                }
                return string.Empty;
            }

            int openIdx = chunk.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (openIdx >= 0)
            {
                string before = chunk.Substring(0, openIdx);
                string rest = chunk.Substring(openIdx + "<think>".Length);
                _inThinkBlock = true;
                _thinkBuffer.Clear();
                _thinkBuffer.Append(rest);

                int closeIdx = _thinkBuffer.ToString().IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (closeIdx >= 0)
                {
                    string after = _thinkBuffer.ToString().Substring(closeIdx + "</think>".Length);
                    _inThinkBlock = false;
                    _thinkBuffer.Clear();
                    return before + after;
                }
                return before;
            }

            return chunk;
        }

        // ── LLM ───────────────────────────────────────────────────────────────

#pragma warning disable VSSDK007 // async void UI event handler is intentional
#pragma warning disable VSTHRD100
        private async void SendToLlm()
#pragma warning restore VSTHRD100
#pragma warning restore VSSDK007
        {
            string text = InputTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (text.StartsWith("PATCH ", StringComparison.OrdinalIgnoreCase))
            {
                await ApplyPatchAsync(text);
                return;
            }

            if (text.StartsWith("READ ", StringComparison.OrdinalIgnoreCase))
            {
                await ApplyReadCommandAsync(text);
                return;
            }

            _inThinkBlock = false;
            _thinkBuffer.Clear();

            InputTextBox.Text = "";
            SetInputEnabled(false);

            var (selectedText, fileName, fullContent) = await GetEditorContextAsync();

            if (!string.IsNullOrEmpty(selectedText))
            {
                int lines = selectedText.Split('\n').Length;
                ContextIndicator.Text = $"\u2295 {lines} lines from {fileName}";
            }
            else if (!string.IsNullOrEmpty(fullContent))
            {
                int lines = fullContent.Split('\n').Length;
                ContextIndicator.Text = $"\uD83D\uDCC4 {fileName} ({lines} lines)";
            }
            else if (!string.IsNullOrEmpty(fileName))
            {
                ContextIndicator.Text = $"\uD83D\uDCC4 {fileName}";
            }
            else
            {
                ContextIndicator.Text = "";
            }

            string contextualMessage = BuildMessageWithContext(text, selectedText, fileName, fullContent);

            if (!string.IsNullOrEmpty(_readContext))
            {
                contextualMessage = _readContext + contextualMessage;
                _readContext = null;
            }

            // File generation detection
            string targetFileName = ExtractFileName(text);
            bool isFileGeneration = targetFileName != null;
            var fileGenBuffer = isFileGeneration ? new StringBuilder() : null;

            if (isFileGeneration)
            {
                string projectNamespace = null;
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                    var activeDoc = dte?.ActiveDocument;
                    if (activeDoc != null)
                    {
                        var project = activeDoc.ProjectItem?.ContainingProject;
                        projectNamespace = project?.Properties?.Item("DefaultNamespace")?.Value?.ToString();
                    }
                }
                catch { }

                string nsHint = string.IsNullOrEmpty(projectNamespace)
                    ? ""
                    : $" Use the namespace '{projectNamespace}'.";
                contextualMessage += $"\n\nIMPORTANT: Respond with ONLY the raw source code. No explanations, no markdown code fences, no preamble, no comments unless they are meaningful code comments. Output only what should be in the file.{nsHint}";
            }

            // Lazy-load DevMind.md context once per session
            if (_devMindContext == null)
            {
                string loaded = await LoadDevMindContextAsync();
                if (loaded != null)
                {
                    _devMindContext = loaded;
                    AppendOutput("📄 DevMind.md loaded.\n", OutputColor.Dim);
                }
                else
                {
                    _devMindContext = ""; // mark as checked so we don't retry every message
                }
            }

            AppendOutput($"\n> {text}\n", OutputColor.Input);
            AppendNewLine();

            if (isFileGeneration)
            {
                _generatingTokenCount = 0;
                StartGeneratingAnimation(targetFileName);
                StatusText.Text = $"Generating {targetFileName}...";
            }
            else
            {
                StatusText.Text = "Thinking...";
            }
            _cts = new CancellationTokenSource();

            Run streamRun = null;
            if (!isFileGeneration)
            {
                var streamPara = new Paragraph { Margin = new Thickness(0) };
                OutputBox.Document.Blocks.Add(streamPara);
                streamRun = new Run { Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)) };
                streamPara.Inlines.Add(streamRun);
            }

            // Temporarily inject DevMind.md into the system prompt.
            // UpdateSystemPrompt() in LlmClient runs synchronously at the start of
            // SendMessageAsync, before the first await, so restoring immediately after
            // RunAsync() returns is safe — no race condition.
            string originalSystemPrompt = DevMindOptions.Instance.SystemPrompt;
            if (!string.IsNullOrEmpty(_devMindContext))
            {
                DevMindOptions.Instance.SystemPrompt =
                    $"{originalSystemPrompt}\n\n--- Project Context (DevMind.md) ---\n{_devMindContext}\n---";
            }

#pragma warning disable VSSDK007
#pragma warning disable VSTHRD100
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    await _llmClient.SendMessageAsync(
                        contextualMessage,
                        onToken: token =>
                        {
                            ThreadHelper.JoinableTaskFactory.Run(async delegate
                            {
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                if (isFileGeneration)
                                {
                                    fileGenBuffer.Append(token);
                                    _generatingTokenCount++;
                                    StatusText.Text = $"Generating {targetFileName}... ({_generatingTokenCount} tokens)";
                                    var statusBar = await VS.GetServiceAsync<SVsStatusbar, IVsStatusbar>();
                                    statusBar?.SetText($"DevMind: Generating {targetFileName}... ({_generatingTokenCount} tokens)");
                                }
                                else
                                {
                                    var visible = FilterChunk(token);
                                    if (!string.IsNullOrEmpty(visible))
                                    {
                                        streamRun.Text += visible;
                                        OutputBox.ScrollToEnd();
                                    }
                                }
                            });
                        },
                        onComplete: () =>
                        {
                            ThreadHelper.JoinableTaskFactory.Run(async delegate
                            {
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                if (isFileGeneration)
                                    await SaveGeneratedFileAsync(targetFileName, fileGenBuffer.ToString());
                                else
                                    AppendNewLine();
                                StatusText.Text = "Ready";
                                ContextIndicator.Text = "";
                                SetInputEnabled(true);
                                InputTextBox.Focus();
                            });
                        },
                        onError: ex =>
                        {
                            ThreadHelper.JoinableTaskFactory.Run(async delegate
                            {
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                if (isFileGeneration)
                                {
                                    StopGeneratingAnimation();
                                    var statusBar = await VS.GetServiceAsync<SVsStatusbar, IVsStatusbar>();
                                    statusBar?.SetText("DevMind: Ready");
                                }
                                AppendOutput($"\n[Error: {ex.Message}]\n", OutputColor.Error);
                                StatusText.Text = "Error";
                                SetInputEnabled(true);
                            });
                        },
                        _cts.Token);
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (_cts.IsCancellationRequested)
                    {
                        if (isFileGeneration)
                        {
                            StopGeneratingAnimation();
                            var statusBar = await VS.GetServiceAsync<SVsStatusbar, IVsStatusbar>();
                            statusBar?.SetText("DevMind: Ready");
                        }
                        AppendOutput("\n[Stopped]\n", OutputColor.Dim);
                        StatusText.Text = "Stopped";
                    }
                    SetInputEnabled(true);
                }
            });
            // Restore original system prompt now that UpdateSystemPrompt() has already
            // captured the combined value into conversation history.
            DevMindOptions.Instance.SystemPrompt = originalSystemPrompt;
#pragma warning restore VSTHRD100
#pragma warning restore VSSDK007
        }

        private async Task<string> FindFileInSolutionAsync(string fileName, string hint = null)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await VS.GetServiceAsync<DTE, DTE>();
                if (dte?.Solution?.FileName == null) return null;
                var solutionDir = Path.GetDirectoryName(dte.Solution.FileName);
                if (string.IsNullOrEmpty(solutionDir)) return null;
                var matches = Directory.GetFiles(solutionDir, fileName, SearchOption.AllDirectories);

                // If hint contains a path separator, filter matches where the full path contains the hint
                if (!string.IsNullOrEmpty(hint) && (hint.Contains('/') || hint.Contains('\\')))
                {
                    string normalizedHint = hint.Replace('\\', '/');
                    var hintMatches = matches
                        .Where(m => m.Replace('\\', '/').IndexOf(normalizedHint, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToArray();
                    if (hintMatches.Length > 0)
                        return hintMatches.FirstOrDefault(File.Exists);
                }

                return matches.FirstOrDefault(File.Exists);
            }
            catch
            {
                return null;
            }
        }

        private static string FindFileInProject(EnvDTE.ProjectItems items, string fileName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items == null) return null;
            foreach (EnvDTE.ProjectItem item in items)
            {
                for (short i = 1; i <= item.FileCount; i++)
                {
                    var path = item.FileNames[i];
                    if (Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        return path;
                }
                var sub = FindFileInProject(item.ProjectItems, fileName);
                if (sub != null) return sub;
            }
            return null;
        }

        // ── Shell ─────────────────────────────────────────────────────────────

        private void RunShellCommand()
        {
            string command = InputTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(command)) return;

            InputTextBox.Text = "";

            _terminalHistory.RemoveAll(h => h == command);
            _terminalHistory.Add(command);
            _terminalHistoryIndex = _terminalHistory.Count;

            AppendOutput($"\n> {command}\n", OutputColor.Input);

            // /reload interception — clears cached DevMind.md so it's re-read on next Ask
            if (command.Equals("/reload", StringComparison.OrdinalIgnoreCase))
            {
                _devMindContext = null;
                AppendOutput("DevMind.md context cleared — will reload on next Ask.\n", OutputColor.Dim);
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
                    string stdout = await Task.Run(() => proc.StandardOutput.ReadToEnd());
                    string stderr = await Task.Run(() => proc.StandardError.ReadToEnd());
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

        // ── Editor context ────────────────────────────────────────────────────

        private async Task<(string selectedText, string fileName, string fullContent)> GetEditorContextAsync()
        {
            try
            {
                var docView = await VS.Documents.GetActiveDocumentViewAsync();
                if (docView?.TextView == null) return (null, null, null);

                string fileName = Path.GetFileName(docView.FilePath ?? "");

                string selectedText = null;
                var selection = docView.TextView.Selection;
                if (selection != null && !selection.IsEmpty)
                {
                    string raw = string.Concat(selection.SelectedSpans.Select(s => s.GetText()));
                    if (!string.IsNullOrWhiteSpace(raw))
                        selectedText = raw;
                }

                string fullContent = null;
                if (selectedText == null)
                {
                    var snapshot = docView.TextView.TextSnapshot;
                    if (snapshot != null && snapshot.LineCount <= 300)
                        fullContent = snapshot.GetText();
                }

                return (selectedText, fileName, fullContent);
            }
            catch
            {
                return (null, null, null);
            }
        }

        private static string GetLanguageHint(string fileName)
        {
            string ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
            return ext switch
            {
                "cs"   => "csharp",
                "vb"   => "vbnet",
                "ts"   => "typescript",
                "js"   => "javascript",
                "py"   => "python",
                "xml"  => "xml",
                "xaml" => "xml",
                "json" => "json",
                "sql"  => "sql",
                "cpp"  => "cpp",
                "cc"   => "cpp",
                "h"    => "cpp",
                "hpp"  => "cpp",
                _      => ext
            };
        }

        // ── DevMind.md context ────────────────────────────────────────────────

        private async Task<string> LoadDevMindContextAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                string solutionDir = System.IO.Path.GetDirectoryName(dte?.Solution?.FullName);
                if (string.IsNullOrEmpty(solutionDir)) return null;

                string mdPath = Directory.GetFiles(solutionDir, "DevMind.md",
                    SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();

                return mdPath != null ? File.ReadAllText(mdPath) : null;
            }
            catch
            {
                return null;
            }
        }

        // ── File generation ───────────────────────────────────────────────────

        private void StartGeneratingAnimation(string fileName)
        {
            var para = new Paragraph { Margin = new Thickness(0) };
            _generatingRun = new Run($"  Generating {fileName}.")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))
            };
            para.Inlines.Add(_generatingRun);
            OutputBox.Document.Blocks.Add(para);
            OutputBox.ScrollToEnd();

            int dotCount = 1;
            _generatingTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _generatingTimer.Tick += (s, e) =>
            {
                dotCount = dotCount % 3 + 1;
                if (_generatingRun != null)
                    _generatingRun.Text = $"  Generating {fileName}{new string('.', dotCount)}";
            };
            _generatingTimer.Start();
        }

        private void StopGeneratingAnimation()
        {
            _generatingTimer?.Stop();
            _generatingTimer = null;
            _generatingRun = null;
        }

        private static string ExtractFileName(string prompt)
        {
            // Only trigger file generation if a filename appears in the first 60 characters
            // of the prompt (i.e. the prompt starts with a generation intent like
            // "create Foo.cs" or "generate Bar.ts"). Prompts that merely mention a filename
            // somewhere in the body (e.g. "In this method, change X to Y in Foo.cs") are
            // intentionally excluded so that file generation is not accidentally triggered.
            string head = prompt.Length > 60 ? prompt.Substring(0, 60) : prompt;

            var extensions = new[] { ".cs", ".ts", ".js", ".py", ".xml", ".json", ".sql",
                                      ".html", ".css", ".xaml", ".cpp", ".h", ".vb", ".fs",
                                      ".md", ".txt", ".yaml", ".yml", ".toml", ".ini",
                                      ".bat", ".sh", ".ps1", ".csproj", ".sln" };
            foreach (var word in head.Split(new[] { ' ', '\t', '\n', '\r', ',', ';' },
                                            StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var ext in extensions)
                {
                    if (word.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        return word.Trim('\'', '"', '`', '(', ')', '[', ']');
                }
            }
            return null;
        }

        private async Task SaveGeneratedFileAsync(string fileName, string code)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

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
                            projectDir = System.IO.Path.GetDirectoryName(projFile);
                    }
                }
                catch { }
                string saveDir = projectDir ?? _terminalWorkingDir;

                string fullPath = Path.Combine(saveDir, fileName);

                // Remove complete <think>...</think> blocks
                code = System.Text.RegularExpressions.Regex.Replace(
                    code, @"<think>[\s\S]*?</think>", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                // Remove unclosed opening <think> block still streaming
                int openIdx = code.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (openIdx >= 0)
                    code = code.Substring(0, openIdx).Trim();

                File.WriteAllText(fullPath, code.Trim(), Encoding.UTF8);

                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                    var project = dte?.ActiveDocument?.ProjectItem?.ContainingProject;
                    project?.ProjectItems?.AddFromFile(fullPath);
                }
                catch { /* non-fatal — file was written, just not added to project tree */ }

                StopGeneratingAnimation();
                var statusBar = await VS.GetServiceAsync<SVsStatusbar, IVsStatusbar>();
                statusBar?.SetText("DevMind: Ready");
                int lineCount = code.Split('\n').Length;
                AppendOutput($"✓ Created {fileName} ({lineCount} lines)\n", OutputColor.Success);
                AppendOutput($"  → Added to project: {fileName}\n", OutputColor.Dim);

                if (DevMindOptions.Instance.OpenFileAfterGeneration)
                {
                    try { await VS.Documents.OpenAsync(fullPath); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                StopGeneratingAnimation();
                try
                {
                    var statusBar = await VS.GetServiceAsync<SVsStatusbar, IVsStatusbar>();
                    statusBar?.SetText("DevMind: Ready");
                }
                catch { }
                AppendOutput($"✗ Failed to create {fileName}: {ex.Message}\n", OutputColor.Error);
            }
        }

        // ── PATCH command ─────────────────────────────────────────────────────

        /// <summary>
        /// Collapses all whitespace runs to a single space character and returns
        /// a mapping array where normToOrig[i] is the position in the original
        /// string that corresponds to position i in the normalized string.
        /// </summary>
        private static (string normalized, int[] normToOrig) NormalizeWithMap(string text)
        {
            var sb = new StringBuilder(text.Length);
            var map = new List<int>(text.Length);
            bool inWhitespace = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    if (!inWhitespace)
                    {
                        sb.Append(' ');
                        map.Add(i);
                        inWhitespace = true;
                    }
                }
                else
                {
                    sb.Append(text[i]);
                    map.Add(i);
                    inWhitespace = false;
                }
            }
            return (sb.ToString(), map.ToArray());
        }

        private async Task ApplyPatchAsync(string input)
        {
            try
            {
                // Parse first line: "PATCH <filename>"
                var lines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
                string fileName = lines[0].Substring("PATCH ".Length).Trim();
                if (string.IsNullOrEmpty(fileName))
                {
                    AppendOutput("[PATCH] No filename specified.\n", OutputColor.Error);
                    return;
                }

                // Extract FIND: and REPLACE: blocks
                string joined = input;
                int findIdx = joined.IndexOf("FIND:", StringComparison.OrdinalIgnoreCase);
                int replaceIdx = joined.IndexOf("REPLACE:", StringComparison.OrdinalIgnoreCase);
                if (findIdx < 0 || replaceIdx < 0 || replaceIdx <= findIdx)
                {
                    AppendOutput("[PATCH] Invalid syntax — must contain FIND: and REPLACE: markers.\n", OutputColor.Error);
                    return;
                }

                // Text between "FIND:\n" and "REPLACE:"
                int findContentStart = joined.IndexOf('\n', findIdx) + 1;
                string findText = joined.Substring(findContentStart, replaceIdx - findContentStart);
                // Strip trailing newline that separates FIND block from REPLACE:
                if (findText.EndsWith("\r\n")) findText = findText.Substring(0, findText.Length - 2);
                else if (findText.EndsWith("\n")) findText = findText.Substring(0, findText.Length - 1);

                // Text after "REPLACE:\n"
                int replaceContentStart = joined.IndexOf('\n', replaceIdx) + 1;
                string replaceText = replaceContentStart > 0 && replaceContentStart <= joined.Length
                    ? joined.Substring(replaceContentStart)
                    : "";

                // Resolve file path — search solution for matching filename
                string fullPath = null;
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                    string solutionDir = System.IO.Path.GetDirectoryName(dte?.Solution?.FullName);
                    if (!string.IsNullOrEmpty(solutionDir))
                    {
                        var matches = Directory.GetFiles(solutionDir, fileName, SearchOption.AllDirectories);
                        fullPath = matches.FirstOrDefault();
                    }
                }
                catch { }

                fullPath ??= Path.Combine(_terminalWorkingDir, fileName);

                if (!File.Exists(fullPath))
                {
                    AppendOutput($"[PATCH] File not found: {fullPath}\n", OutputColor.Error);
                    return;
                }

                string content = File.ReadAllText(fullPath);

                // Whitespace-normalized matching: collapse all whitespace runs to single space,
                // then map the match position back to the original content for replacement.
                var (normContent, normToOrig) = NormalizeWithMap(content);
                string normFind = Regex.Replace(findText, @"\s+", " ").Trim();
                AppendOutput($"[PATCH-DEBUG] normFind: {normFind}\n", OutputColor.Dim);
                AppendOutput($"[PATCH-DEBUG] normContent (first 500): {normContent.Substring(0, Math.Min(500, normContent.Length))}\n", OutputColor.Dim);
                int normIdx = normContent.IndexOf(normFind, StringComparison.Ordinal);
                if (normIdx < 0)
                {
                    AppendOutput($"[PATCH] FIND text not found in {fileName} — no changes made.\n", OutputColor.Error);
                    return;
                }

                // Map normalized positions back to original content positions
                int origStart = normToOrig[normIdx];
                int origEnd = (normIdx + normFind.Length < normToOrig.Length)
                    ? normToOrig[normIdx + normFind.Length]
                    : content.Length;

                var updated = content.Substring(0, origStart) + replaceText + content.Substring(origEnd);
                File.WriteAllText(fullPath, updated, Encoding.UTF8);
                AppendOutput($"[PATCH] Applied to {fullPath}\n", OutputColor.Success);
            }
            catch (Exception ex)
            {
                AppendOutput($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
            }
        }

        // ── READ command ──────────────────────────────────────────────────────

        private async Task ApplyReadCommandAsync(string input)
        {
            try
            {
                // Support multi-line input: process each line starting with "READ "
                var lines = input.Split('\n');
                foreach (var rawLine in lines)
                {
                    var line = rawLine.TrimEnd('\r');
                    if (!line.StartsWith("READ ", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string hint = line.Substring("READ ".Length).Trim().TrimEnd('\r');
                    if (string.IsNullOrEmpty(hint))
                    {
                        AppendOutput("[READ] No filename specified.\n", OutputColor.Error);
                        continue;
                    }

                    // Normalize separators; extract just the filename for directory search
                    string normalizedHint = hint.Replace('\\', '/');
                    string fileNameOnly = Path.GetFileName(normalizedHint);

                    string fullPath = await FindFileInSolutionAsync(fileNameOnly, normalizedHint)
                        ?? Path.Combine(_terminalWorkingDir, hint);

                    if (!File.Exists(fullPath))
                    {
                        AppendOutput($"[READ] File not found: {hint}\n", OutputColor.Error);
                        continue;
                    }

                    string content = File.ReadAllText(fullPath);
                    int lineCount = content.Split('\n').Length;

                    _readContext = (_readContext ?? "") +
                        $"The following files have been loaded for context:\n\n{fileNameOnly}\n```\n{content}\n```\n\n";

                    AppendOutput($"[READ] Loaded {fullPath} ({lineCount} lines)\n", OutputColor.Success);
                }

                InputTextBox.Text = "";
            }
            catch (Exception ex)
            {
                AppendOutput($"[READ] Error: {ex.Message}\n", OutputColor.Error);
            }
        }

        private static string BuildMessageWithContext(
            string userMessage,
            string selectedText,
            string fileName,
            string fullContent = null)
        {
            if (!string.IsNullOrEmpty(selectedText))
            {
                string header = string.IsNullOrEmpty(fileName)
                    ? "Selected code:"
                    : $"Selected code from {fileName}:";
                return $"{header}\n```{GetLanguageHint(fileName)}\n{selectedText}\n```\n\n{userMessage}";
            }

            if (!string.IsNullOrEmpty(fullContent) && !string.IsNullOrEmpty(fileName))
                return $"Active file ({fileName}):\n```{GetLanguageHint(fileName)}\n{fullContent}\n```\n\n{userMessage}";

            if (!string.IsNullOrEmpty(fileName))
                return $"[Active file: {fileName}]\n\n{userMessage}";

            return userMessage;
        }
    }
}
