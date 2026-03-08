// File: DevMindToolWindowControl.xaml.cs  v4.7.2
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
        private (string fullPath, Encoding fileEncoding, string fileName, string content, List<(int origStart, int origEnd, string replaceText)> resolvedBlocks)? _pendingFuzzyPatch;
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
        private readonly Stack<(string originalPath, string backupPath)> _patchBackupStack = new Stack<(string, string)>();
        private const int PatchBackupStackLimit = 10;
        private Paragraph _spacerParagraph;

        public DevMindToolWindowControl(LlmClient llmClient)
        {
            InitializeComponent();
            Themes.SetUseVsTheme(this, true);
            OutputBox.Document.PagePadding = new Thickness(0);
            InitOutputDocument();
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
            // Defer banner until after first layout pass so ViewportHeight is known for spacer calc
            Dispatcher.BeginInvoke(new Action(AppendBanner), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void AppendBanner()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string versionStr = $"{version.Major}.{version.Minor}.{version.Build}";
            AppendOutput($"DevMind v{versionStr} — local LLM assistant\nType a message and click Ask, or a shell command and click Run.\n", OutputColor.Dim);
            // Explicit spacer recalc after banner text is committed to layout
            OutputBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                double viewportH = OutputBox.ViewportHeight;
                double contentH  = OutputBox.ExtentHeight - _spacerParagraph.Margin.Top;
                double newTop    = Math.Max(0, viewportH - contentH);
                if (Math.Abs(newTop - _spacerParagraph.Margin.Top) > 1.0)
                    _spacerParagraph.Margin = new Thickness(0, newTop, 0, 0);
                OutputBox.ScrollToEnd();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // ── Output color ──────────────────────────────────────────────────────

        private enum OutputColor { Normal, Dim, Input, Error, Success }

        private void InitOutputDocument()
        {
            OutputBox.Document.Blocks.Clear();
            _spacerParagraph = new Paragraph { LineHeight = 1.0, Margin = new Thickness(0, 2000, 0, 0) };
            OutputBox.Document.Blocks.Add(_spacerParagraph);
            OutputBox.Document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        }

        private void AppendOutput(string text, OutputColor color = OutputColor.Normal)
        {
            // Never append into the spacer — ensure there is always a real content paragraph last
            if (!(OutputBox.Document.Blocks.LastBlock is Paragraph para) || para == _spacerParagraph)
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
            OutputBox.CaretPosition = OutputBox.Document.ContentEnd;
            OutputBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                double viewportH = OutputBox.ViewportHeight;
                double contentH  = OutputBox.ExtentHeight - _spacerParagraph.Margin.Top;
                double newTop    = Math.Max(0, viewportH - contentH);
                if (Math.Abs(newTop - _spacerParagraph.Margin.Top) > 1.0)
                    _spacerParagraph.Margin = new Thickness(0, newTop, 0, 0);
                OutputBox.ScrollToEnd();
            }), System.Windows.Threading.DispatcherPriority.Background);
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
                    string cmd = InputTextBox.Text?.Trim();
                    if (!string.IsNullOrEmpty(cmd)) InputTextBox.Text = "";
                    RunShellCommand(cmd);
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

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            string cmd = InputTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(cmd)) InputTextBox.Text = "";
            RunShellCommand(cmd);
        }

        private void StopButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            // Clear output
            InitOutputDocument();

            // Reset think filter state
            _inThinkBlock = false;
            _thinkBuffer.Clear();

            // Reset token counter
            _generatingTokenCount = 0;

            // Clear terminal history
            _terminalHistory.Clear();
            _terminalHistoryIndex = 0;

            // Clear LLM conversation history
            _llmClient.ClearHistory();

            // Force DevMind.md reload on next Ask
            _devMindContext = null;

            // Clear any READ-loaded file context
            _readContext = null;

            // Discard pending fuzzy confirmation
            _pendingFuzzyPatch = null;

            // Discard all PATCH backups and clean up temp files
            while (_patchBackupStack.Count > 0)
            {
                var (_, backupPath) = _patchBackupStack.Pop();
                try { File.Delete(backupPath); } catch { }
            }

            AppendOutput("DevMind restarted.\n", OutputColor.Dim);
        }

        private void ClearPromptButton_Click(object sender, RoutedEventArgs e)
        {
            InputTextBox.Text = "";
            InputTextBox.Focus();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            InitOutputDocument();
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

            // Single-line /command → route to shell, stripping the leading /
            if (text.StartsWith("/") && !text.Contains('\n'))
            {
                string cmd = text.Substring(1);
                InputTextBox.Text = "";
                RunShellCommand(cmd);
                return;
            }

            if (text.StartsWith("PATCH ", StringComparison.OrdinalIgnoreCase))
            {
                await ApplyPatchAsync(text);
                return;
            }

            if (text.Equals("UNDO", StringComparison.OrdinalIgnoreCase))
            {
                await ApplyUndoAsync();
                return;
            }

            // Process consecutive READ lines from the top of the input
            {
                var allLines = text.Split('\n');
                int readLineCount = 0;
                while (readLineCount < allLines.Length &&
                       allLines[readLineCount].TrimEnd('\r').StartsWith("READ ", StringComparison.OrdinalIgnoreCase))
                {
                    readLineCount++;
                }

                if (readLineCount > 0)
                {
                    string readBlock = string.Join("\n", allLines, 0, readLineCount);
                    await ApplyReadCommandAsync(readBlock);

                    string remaining = string.Join("\n", allLines, readLineCount, allLines.Length - readLineCount).Trim();
                    if (string.IsNullOrEmpty(remaining))
                        return;

                    text = remaining;
                }
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
                // _readContext intentionally kept alive — persists until /reload or Restart clears it
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
            var responseBuffer = new StringBuilder();
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
                                        responseBuffer.Append(visible);
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
                                {
                                    await SaveGeneratedFileAsync(targetFileName, fileGenBuffer.ToString());
                                }
                                else
                                {
                                    AppendNewLine();
                                    var patchBlocks = ParsePatchBlocks(responseBuffer.ToString());
                                    if (patchBlocks.Count > 0)
                                    {
                                        AppendOutput($"[AUTO-PATCH] Detected {patchBlocks.Count} PATCH block(s) — executing...\n", OutputColor.Dim);
                                        await AutoExecutePatchAsync(responseBuffer.ToString());
                                    }
                                }
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

        private static readonly HashSet<string> _noisePathSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", ".vs", ".git", "node_modules", "packages", ".idea" };

        private static bool IsNoisePath(string fullPath) =>
            fullPath.Replace('\\', '/').Split('/')
                .Any(seg => _noisePathSegments.Contains(seg));

        /// <summary>
        /// Recursively enumerates files matching a pattern, silently skipping
        /// any directories that are inaccessible (permission errors, symlink loops, etc.).
        /// </summary>
        private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
        {
            // Strip glob characters the LLM might accidentally include in a filename
            string safePattern = pattern.Replace("*", "").Replace("?", "");
            if (string.IsNullOrWhiteSpace(safePattern)) yield break;

            var queue = new Queue<string>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                string dir = queue.Dequeue();
                IEnumerable<string> files = Enumerable.Empty<string>();
                try { files = Directory.EnumerateFiles(dir, safePattern); } catch { }
                foreach (var f in files) yield return f;

                IEnumerable<string> subdirs = Enumerable.Empty<string>();
                try { subdirs = Directory.EnumerateDirectories(dir); } catch { }
                foreach (var sub in subdirs)
                {
                    if (!_noisePathSegments.Contains(Path.GetFileName(sub)))
                        queue.Enqueue(sub);
                }
            }
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

                // Exclude output/tooling folders that could contain stale compiled copies.
                // Use safe recursive enumeration — Directory.GetFiles(AllDirectories) throws
                // on any inaccessible subdirectory (symlinks, ACL-denied folders, etc.)
                var matches = SafeEnumerateFiles(solutionDir, fileName)
                    .Where(m => !IsNoisePath(m))
                    .ToArray();

                // If hint contains a path separator, prefer matches whose path contains the hint
                if (!string.IsNullOrEmpty(hint) && (hint.Contains('/') || hint.Contains('\\')))
                {
                    string normalizedHint = hint.Replace('\\', '/');
                    var hintMatches = matches
                        .Where(m => m.Replace('\\', '/').IndexOf(normalizedHint, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Where(File.Exists)
                        .ToArray();
                    if (hintMatches.Length > 1)
                        AppendOutput($"[FIND] Warning: {hintMatches.Length} matches for '{fileName}' after hint filtering — using first: {hintMatches[0]}\n", OutputColor.Dim);
                    if (hintMatches.Length > 0)
                        return hintMatches[0];
                }

                var existingMatches = matches.Where(File.Exists).ToArray();
                if (existingMatches.Length > 1)
                    AppendOutput($"[FIND] Warning: {existingMatches.Length} matches for '{fileName}' — using first: {existingMatches[0]}\n", OutputColor.Dim);

                return existingMatches.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }


        // ── Terminal strip ────────────────────────────────────────────────────

        private void TerminalInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            // Single-keypress fuzzy confirmation — no Enter required
            if (_pendingFuzzyPatch.HasValue)
            {
                if (e.Key == Key.D1 || e.Key == Key.NumPad1)
                {
                    e.Handled = true;
                    TerminalInputBox.Text = "";
                    AppendOutput("\n> 1\n", OutputColor.Input);
                    var pending = _pendingFuzzyPatch.Value;
                    _pendingFuzzyPatch = null;
                    ApplyPendingFuzzyPatch(pending);
                    return;
                }
                if (e.Key == Key.D2 || e.Key == Key.NumPad2)
                {
                    e.Handled = true;
                    TerminalInputBox.Text = "";
                    AppendOutput("\n> 2\n", OutputColor.Input);
                    _pendingFuzzyPatch = null;
                    AppendOutput("[PATCH] Fuzzy match cancelled.\n", OutputColor.Dim);
                    return;
                }
                // All other keys pass through — user can still type
                return;
            }

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ExecuteTerminalInput();
            }
            else if (e.Key == Key.Up)
            {
                if (_terminalHistory.Count == 0) return;
                _terminalHistoryIndex = Math.Max(0, _terminalHistoryIndex - 1);
                TerminalInputBox.Text = _terminalHistory[_terminalHistoryIndex];
                TerminalInputBox.CaretIndex = TerminalInputBox.Text.Length;
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (_terminalHistory.Count == 0) return;
                _terminalHistoryIndex = Math.Min(_terminalHistory.Count, _terminalHistoryIndex + 1);
                TerminalInputBox.Text = _terminalHistoryIndex < _terminalHistory.Count
                    ? _terminalHistory[_terminalHistoryIndex]
                    : "";
                TerminalInputBox.CaretIndex = TerminalInputBox.Text.Length;
                e.Handled = true;
            }
        }

        private void ExecuteTerminalInput()
        {
            string command = TerminalInputBox.Text?.Trim();
            if (string.IsNullOrEmpty(command)) return;
            TerminalInputBox.Text = "";
            RunShellCommand(command);
        }

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
            if (command.Equals("/reload", StringComparison.OrdinalIgnoreCase))
            {
                _devMindContext = null;
                AppendOutput("DevMind.md context cleared — will reload on next Ask.\n", OutputColor.Dim);
                return;
            }

            // /context — show or clear READ-loaded file context
            if (command.Equals("/context", StringComparison.OrdinalIgnoreCase))
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

            if (command.Equals("/context clear", StringComparison.OrdinalIgnoreCase))
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

        // ── UNDO command ──────────────────────────────────────────────────────

        private Task ApplyUndoAsync()
        {
            if (_patchBackupStack.Count == 0)
            {
                AppendOutput("[UNDO] Nothing to undo.\n", OutputColor.Error);
                return Task.CompletedTask;
            }

            var (originalPath, backupPath) = _patchBackupStack.Pop();
            try
            {
                if (!File.Exists(backupPath))
                {
                    AppendOutput($"[UNDO] Backup file missing: {backupPath}\n", OutputColor.Error);
                    return Task.CompletedTask;
                }
                File.Copy(backupPath, originalPath, overwrite: true);
                try { File.Delete(backupPath); } catch { }
                int remaining = _patchBackupStack.Count;
                AppendOutput($"[UNDO] Restored {Path.GetFileName(originalPath)} (undo depth remaining: {remaining})\n", OutputColor.Success);
                InputTextBox.Text = "";
            }
            catch (Exception ex)
            {
                AppendOutput($"[UNDO] Failed: {ex.Message}\n", OutputColor.Error);
            }
            return Task.CompletedTask;
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

        /// <summary>
        /// Reads a file detecting and preserving its BOM/encoding.
        /// Returns the text content and the encoding to use when writing back.
        /// </summary>
        private static (string content, Encoding encoding) ReadFilePreservingEncoding(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return (new UTF8Encoding(true).GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode);

            return (Encoding.UTF8.GetString(bytes), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>
        /// Standard dynamic-programming Levenshtein edit distance.
        /// </summary>
        private static int LevenshteinDistance(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = a[i - 1] == b[j - 1]
                        ? d[i - 1, j - 1]
                        : 1 + Math.Min(d[i - 1, j - 1], Math.Min(d[i - 1, j], d[i, j - 1]));
            return d[a.Length, b.Length];
        }

        /// <summary>
        /// Slides an N-line window over content (N = line count of findText) and scores
        /// each window against normFind using Levenshtein similarity.
        /// Returns the best match only when it exceeds the threshold AND is unambiguous.
        /// </summary>
        private static (int origStart, int origEnd, double similarity)? FindFuzzyMatch(
            string content, string findText, string normFind, double threshold = 0.85)
        {
            int windowSize = findText.Split('\n').Length;

            // Build line list with absolute char offsets
            var lines = new List<(int start, int end)>();
            int pos = 0;
            while (pos < content.Length)
            {
                int nl = content.IndexOf('\n', pos);
                int end = nl >= 0 ? nl + 1 : content.Length;
                lines.Add((pos, end));
                pos = end;
                if (nl < 0) break;
            }

            double bestSim = -1, secondSim = -1;
            int bestStart = -1, bestEnd = -1;

            for (int i = 0; i <= lines.Count - windowSize; i++)
            {
                int wStart = lines[i].start;
                int wEnd   = lines[i + windowSize - 1].end;
                string window     = content.Substring(wStart, wEnd - wStart);
                string normWindow = Regex.Replace(window, @"\s+", " ").Trim();

                int maxLen = Math.Max(normFind.Length, normWindow.Length);
                if (maxLen == 0) continue;
                double sim = 1.0 - (double)LevenshteinDistance(normFind, normWindow) / maxLen;

                if (sim > bestSim)
                {
                    secondSim = bestSim;
                    bestSim   = sim;
                    bestStart = wStart;
                    bestEnd   = wEnd;
                }
                else if (sim > secondSim)
                {
                    secondSim = sim;
                }
            }

            if (bestSim < threshold) return null;
            // Require a meaningful gap over the runner-up to avoid ambiguous fuzzy matches.
            // Gap of 0.05 (5 percentage points) means the best must clearly outrank second-best.
            if (bestSim - secondSim < 0.05) return null;

            return (bestStart, bestEnd, bestSim);
        }

        private static List<(string findText, string replaceText)> ParsePatchBlocks(string input)
        {
            var results = new List<(string, string)>();
            // Skip first line (PATCH <filename>)
            int cursor = input.IndexOf('\n');
            if (cursor < 0) return results;
            cursor++;

            while (cursor < input.Length)
            {
                int findIdx = input.IndexOf("FIND:", cursor, StringComparison.OrdinalIgnoreCase);
                if (findIdx < 0) break;

                int replaceIdx = input.IndexOf("REPLACE:", findIdx, StringComparison.OrdinalIgnoreCase);
                if (replaceIdx < 0) break;

                int findContentStart = input.IndexOf('\n', findIdx) + 1;
                string findText = input.Substring(findContentStart, replaceIdx - findContentStart);
                if (findText.EndsWith("\r\n")) findText = findText.Substring(0, findText.Length - 2);
                else if (findText.EndsWith("\n")) findText = findText.Substring(0, findText.Length - 1);

                int replaceContentStart = input.IndexOf('\n', replaceIdx) + 1;

                // Next FIND: or end of string marks the end of this REPLACE block
                int nextFindIdx = input.IndexOf("FIND:", replaceContentStart, StringComparison.OrdinalIgnoreCase);
                string replaceText = nextFindIdx >= 0
                    ? input.Substring(replaceContentStart, nextFindIdx - replaceContentStart)
                    : input.Substring(replaceContentStart);

                if (replaceText.EndsWith("\r\n")) replaceText = replaceText.Substring(0, replaceText.Length - 2);
                else if (replaceText.EndsWith("\n")) replaceText = replaceText.Substring(0, replaceText.Length - 1);

                results.Add((findText, replaceText));
                cursor = nextFindIdx >= 0 ? nextFindIdx : input.Length;
            }
            return results;
        }

        private async Task ApplyPatchAsync(string input, bool clearInput = true)
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

                // Parse all FIND/REPLACE pairs — supports multi-block patches
                var blocks = ParsePatchBlocks(input);
                if (blocks.Count == 0)
                {
                    AppendOutput("[PATCH] Invalid syntax — must contain at least one FIND: and REPLACE: pair.\n", OutputColor.Error);
                    return;
                }

                // Resolve file path — support partial path hints (e.g. "Services/Foo.cs")
                string normalizedFileName = fileName.Replace('\\', '/');
                string fileNameOnly = Path.GetFileName(normalizedFileName);
                string fullPath = await FindFileInSolutionAsync(fileNameOnly, normalizedFileName)
                    ?? Path.Combine(_terminalWorkingDir, fileNameOnly);

                if (!File.Exists(fullPath))
                {
                    AppendOutput($"[PATCH] File not found: {fullPath}\n", OutputColor.Error);
                    return;
                }

                var (content, fileEncoding) = ReadFilePreservingEncoding(fullPath);
                bool fileUsesCrlf = content.Contains("\r\n");

                // Validate ALL blocks first — all-or-nothing semantics
                var (normContent, normToOrig) = NormalizeWithMap(content);
                var resolvedBlocks = new List<(int origStart, int origEnd, string replaceText)>();

                for (int i = 0; i < blocks.Count; i++)
                {
                    var (findText, replaceText) = blocks[i];
                    string normFind = Regex.Replace(findText, @"\s+", " ").Trim();
                    if (string.IsNullOrEmpty(normFind))
                    {
                        AppendOutput($"[PATCH] Block {i + 1}: FIND is empty — no changes made.\n", OutputColor.Error);
                        return;
                    }

                    int normIdx = normContent.IndexOf(normFind, StringComparison.Ordinal);
                    int origStart, origEnd;

                    if (normIdx < 0)
                    {
                        // Exact normalized match failed — try fuzzy line-window fallback
                        var fuzzy = FindFuzzyMatch(content, findText, normFind);
                        if (fuzzy == null)
                        {
                            AppendOutput($"[PATCH] Block {i + 1}: FIND text not found in {fileName} — no changes made.\n", OutputColor.Error);
                            return;
                        }
                        int fuzzyLine = content.Substring(0, fuzzy.Value.origStart).Count(c => c == '\n') + 1;
                        string matchedText = content.Substring(fuzzy.Value.origStart, fuzzy.Value.origEnd - fuzzy.Value.origStart).Trim();
                        string fuzzyPreview = matchedText.Length > 120 ? matchedText.Substring(0, 120) + "…" : matchedText;
                        AppendOutput($"[PATCH] Block {i + 1}: Fuzzy match at line {fuzzyLine} ({fuzzy.Value.similarity:P0} similarity).\n", OutputColor.Dim);
                        AppendOutput($"  Matched text: {fuzzyPreview}\n", OutputColor.Dim);
                        // Resolve line endings for the fuzzy block and add to prior resolved blocks
                        string fuzzyNormReplace = replaceText.Replace("\r\n", "\n");
                        string fuzzyFinalReplace = fileUsesCrlf ? fuzzyNormReplace.Replace("\n", "\r\n") : fuzzyNormReplace;
                        resolvedBlocks.Add((fuzzy.Value.origStart, fuzzy.Value.origEnd, fuzzyFinalReplace));
                        // Suspend — keyboard confirmation required
                        _pendingFuzzyPatch = (fullPath, fileEncoding, fileName, content, resolvedBlocks);
                        AppendOutput("\n[FUZZY] Fuzzy match — press 1 to apply or 2 to cancel.\n", OutputColor.Dim);
                        TerminalInputBox.Focus();
                        return;
                    }
                    else
                    {
                        // Ambiguity check on exact match
                        int secondNormIdx = normContent.IndexOf(normFind, normIdx + 1, StringComparison.Ordinal);
                        if (secondNormIdx >= 0)
                        {
                            int line1 = content.Substring(0, normToOrig[normIdx]).Count(c => c == '\n') + 1;
                            int line2 = content.Substring(0, normToOrig[secondNormIdx]).Count(c => c == '\n') + 1;
                            AppendOutput(
                                $"[PATCH] Block {i + 1}: Ambiguous FIND — matched at line {line1} and line {line2} in {fileName}. " +
                                $"Add more surrounding context to make the match unique.\n",
                                OutputColor.Error);
                            return;
                        }
                        origStart = normToOrig[normIdx];
                        // Walk back to include leading indentation on the same line,
                        // otherwise the indentation from content and from finalReplace double up
                        while (origStart > 0 && content[origStart - 1] != '\n')
                            origStart--;
                        origEnd   = (normIdx + normFind.Length < normToOrig.Length)
                            ? normToOrig[normIdx + normFind.Length]
                            : content.Length;
                    }

                    // Normalize line endings to match file
                    string normalizedReplace = replaceText.Replace("\r\n", "\n");
                    string finalReplace = fileUsesCrlf
                        ? normalizedReplace.Replace("\n", "\r\n")
                        : normalizedReplace;

                    resolvedBlocks.Add((origStart, origEnd, finalReplace));
                }

                // Apply in reverse order so earlier positions aren't shifted by later edits
                resolvedBlocks.Sort((a, b) => b.origStart.CompareTo(a.origStart));
                var updated = content;
                foreach (var (origStart, origEnd, finalReplace) in resolvedBlocks)
                    updated = updated.Substring(0, origStart) + finalReplace + updated.Substring(origEnd);

                // Back up original before writing — enables UNDO
                try
                {
                    string backupDir = Path.Combine(Path.GetTempPath(), "DevMind");
                    Directory.CreateDirectory(backupDir);
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    string backupPath = Path.Combine(backupDir, $"{Path.GetFileName(fullPath)}.{timestamp}.bak");
                    File.Copy(fullPath, backupPath, overwrite: true);
                    if (_patchBackupStack.Count >= PatchBackupStackLimit)
                    {
                        // Discard oldest backup file to keep temp storage bounded
                        var oldest = _patchBackupStack.ToArray().Last();
                        try { File.Delete(oldest.backupPath); } catch { }
                        // Rebuild stack without the oldest entry
                        var entries = _patchBackupStack.ToArray().Reverse().Skip(1).ToArray();
                        _patchBackupStack.Clear();
                        foreach (var e in entries) _patchBackupStack.Push(e);
                    }
                    _patchBackupStack.Push((fullPath, backupPath));
                }
                catch { /* backup failure is non-fatal — patch still applies */ }

                File.WriteAllText(fullPath, updated, fileEncoding);
                int undosAvailable = _patchBackupStack.Count;
                AppendOutput($"[PATCH] Applied to {fullPath} (undo depth: {undosAvailable})\n", OutputColor.Success);
                if (clearInput)
                    InputTextBox.Text = "";
            }
            catch (Exception ex)
            {
                AppendOutput($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
            }
        }

        private void ApplyPendingFuzzyPatch(
            (string fullPath, Encoding fileEncoding, string fileName, string content, List<(int origStart, int origEnd, string replaceText)> resolvedBlocks) pending)
        {
            try
            {
                pending.resolvedBlocks.Sort((a, b) => b.origStart.CompareTo(a.origStart));
                var updated = pending.content;
                foreach (var (origStart, origEnd, finalReplace) in pending.resolvedBlocks)
                    updated = updated.Substring(0, origStart) + finalReplace + updated.Substring(origEnd);

                try
                {
                    string backupDir = Path.Combine(Path.GetTempPath(), "DevMind");
                    Directory.CreateDirectory(backupDir);
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    string backupPath = Path.Combine(backupDir, $"{Path.GetFileName(pending.fullPath)}.{timestamp}.bak");
                    File.Copy(pending.fullPath, backupPath, overwrite: true);
                    if (_patchBackupStack.Count >= PatchBackupStackLimit)
                    {
                        var oldest = _patchBackupStack.ToArray().Last();
                        try { File.Delete(oldest.backupPath); } catch { }
                        var entries = _patchBackupStack.ToArray().Reverse().Skip(1).ToArray();
                        _patchBackupStack.Clear();
                        foreach (var e in entries) _patchBackupStack.Push(e);
                    }
                    _patchBackupStack.Push((pending.fullPath, backupPath));
                }
                catch { /* backup failure is non-fatal */ }

                File.WriteAllText(pending.fullPath, updated, pending.fileEncoding);
                int undosAvailable = _patchBackupStack.Count;
                AppendOutput($"[PATCH] Applied to {pending.fullPath} (undo depth: {undosAvailable})\n", OutputColor.Success);
            }
            catch (Exception ex)
            {
                AppendOutput($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
            }
        }

        // ── AUTO-PATCH ────────────────────────────────────────────────────────

        private async Task AutoExecutePatchAsync(string llmResponse)
        {
            var patchStartPattern = new Regex(@"^PATCH\s+\S+", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var matches = patchStartPattern.Matches(llmResponse);
            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : llmResponse.Length;
                string block = llmResponse.Substring(start, end - start).TrimEnd();
                await ApplyPatchAsync(block, clearInput: false);
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

                    var (content, _) = ReadFilePreservingEncoding(fullPath);
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
