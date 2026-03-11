// File: DevMindToolWindowControl.xaml.cs  v5.0.2
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
        private string _pendingShellContext;
        private bool _shellLoopPending;
        private int _agenticDepth;
        private int _lastShellExitCode;
        private int _generatingTokenCount;
        private bool _inThinkBlock;
        private readonly StringBuilder _thinkBuffer = new StringBuilder();
        private readonly Stack<(string originalPath, string backupPath)> _patchBackupStack = new Stack<(string, string)>();
        private const int PatchBackupStackLimit = 10;
        private Paragraph _spacerParagraph;
        private string _pendingResubmitPrompt;
        private bool _inFileCapture;
        private string _fileCaptureFileName;
        private StringBuilder _fileCaptureBuffer;

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
#pragma warning disable VSTHRD001
            _ = Dispatcher.BeginInvoke(new Action(AppendBanner), System.Windows.Threading.DispatcherPriority.Loaded);
#pragma warning restore VSTHRD001
        }

        private void AppendBanner()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string versionStr = $"{version.Major}.{version.Minor}.{version.Build}";
            AppendOutput($"DevMind v{versionStr} — local LLM assistant\nType a message and click Ask, or a shell command and click Run.\n", OutputColor.Dim);
            // Explicit spacer recalc after banner text is committed to layout
#pragma warning disable VSTHRD001
            _ = OutputBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                double viewportH = OutputBox.ViewportHeight;
                double contentH  = OutputBox.ExtentHeight - _spacerParagraph.Margin.Top;
                double newTop    = Math.Max(0, viewportH - contentH);
                if (Math.Abs(newTop - _spacerParagraph.Margin.Top) > 1.0)
                    _spacerParagraph.Margin = new Thickness(0, newTop, 0, 0);
                OutputBox.ScrollToEnd();
            }), System.Windows.Threading.DispatcherPriority.Background);
#pragma warning restore VSTHRD001
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
#pragma warning disable VSTHRD001
            _ = OutputBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                double viewportH = OutputBox.ViewportHeight;
                double contentH  = OutputBox.ExtentHeight - _spacerParagraph.Margin.Top;
                double newTop    = Math.Max(0, viewportH - contentH);
                if (Math.Abs(newTop - _spacerParagraph.Margin.Top) > 1.0)
                    _spacerParagraph.Margin = new Thickness(0, newTop, 0, 0);
                OutputBox.ScrollToEnd();
            }), System.Windows.Threading.DispatcherPriority.Background);
#pragma warning restore VSTHRD001
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
            _pendingResubmitPrompt = null;

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

            // Reset agentic depth for user-initiated calls; preserve it for agentic re-triggers
            if (!_shellLoopPending)
                _agenticDepth = 0;

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
                    await ApplyReadCommandAsync(readBlock, showOutline: true);

                    string remaining = string.Join("\n", allLines, readLineCount, allLines.Length - readLineCount).Trim();
                    if (string.IsNullOrEmpty(remaining))
                        return;

                    text = remaining;
                }
            }

            _inThinkBlock = false;
            _thinkBuffer.Clear();

            // Capture original prompt for auto-resubmit after READ-only responses
            // Only save when not already in an agentic or resubmit cycle
            if (!_shellLoopPending && _pendingResubmitPrompt == null)
                _pendingResubmitPrompt = text;

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
                // _readContext intentionally kept alive — persists until /context clear or Restart clears it
            }

            if (!string.IsNullOrEmpty(_pendingShellContext))
            {
                contextualMessage = _pendingShellContext + "\n\n" + contextualMessage;
                _pendingShellContext = null;
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

            StatusText.Text = _agenticDepth > 0
                ? $"Thinking... (agentic {_agenticDepth}/{DevMindOptions.Instance.AgenticLoopMaxDepth})"
                : "Thinking...";
            _cts = new CancellationTokenSource();

            // Reset file capture state
            _inFileCapture = false;
            _fileCaptureFileName = null;
            _fileCaptureBuffer = null;

            var streamPara = new Paragraph { Margin = new Thickness(0) };
            OutputBox.Document.Blocks.Add(streamPara);
            var streamRun = new Run { Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)) };
            streamPara.Inlines.Add(streamRun);
            var responseBuffer = new StringBuilder();

            // Temporarily inject the LLM directive and DevMind.md into the system prompt.
            // UpdateSystemPrompt() in LlmClient runs synchronously at the start of
            // SendMessageAsync, before the first await, so restoring immediately after
            // RunAsync() returns is safe — no race condition.
            string originalSystemPrompt = DevMindOptions.Instance.SystemPrompt;

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

            string llmDirective = "To create new files, wrap content in FILE: / END_FILE markers:\n" +
                "FILE: <filename>\n<raw source code>\nEND_FILE\n\n" +
                "To edit existing files, use PATCH blocks:\n" +
                "PATCH <filename>\nFIND:\n<exact text>\nREPLACE:\n<replacement text>\n\n" +
                "To run commands: SHELL: <command>\n" +
                "To read files before editing: READ <filename>\n\n" +
                "Rules:\n" +
                "- You may combine FILE, PATCH, SHELL in a single response.\n" +
                "- After ANY code change, always emit SHELL: dotnet build to verify.\n" +
                "- Never output raw code blocks. Use FILE: for new files, PATCH for edits.\n" +
                "- If you need to see a file before editing, say READ <filename>.";
            if (!string.IsNullOrEmpty(projectNamespace))
                llmDirective += $"\n- When creating new files, use the namespace '{projectNamespace}'.";
            string combined = $"{originalSystemPrompt}\n\n{llmDirective}";
            if (!string.IsNullOrEmpty(_devMindContext))
                combined += $"\n\n--- Project Context (DevMind.md) ---\n{_devMindContext}\n---";
            DevMindOptions.Instance.SystemPrompt = combined;

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
                                var visible = FilterChunk(token);
                                if (string.IsNullOrEmpty(visible)) return;

                                responseBuffer.Append(visible);

                                if (_inFileCapture)
                                {
                                    // Check for END_FILE on its own line
                                    string bufTail = responseBuffer.ToString();
                                    int endIdx = bufTail.LastIndexOf("\nEND_FILE", StringComparison.Ordinal);
                                    if (endIdx < 0) endIdx = bufTail.LastIndexOf("\r\nEND_FILE", StringComparison.Ordinal);
                                    if (endIdx >= 0)
                                    {
                                        _inFileCapture = false;
                                        StopGeneratingAnimation();
                                    }
                                    else
                                    {
                                        _fileCaptureBuffer.Append(visible);
                                        _generatingTokenCount++;
                                        StatusText.Text = $"Generating {_fileCaptureFileName}... ({_generatingTokenCount} tokens)";
                                    }
                                    return;
                                }

                                // Check if the latest content starts a FILE: block
                                string buf = responseBuffer.ToString();
                                var fileMatch = Regex.Match(buf, @"\nFILE:\s*(\S+\.\w+)\s*\n", RegexOptions.None);
                                if (!fileMatch.Success)
                                    fileMatch = Regex.Match(buf, @"^FILE:\s*(\S+\.\w+)\s*\n", RegexOptions.None);
                                if (fileMatch.Success && fileMatch.Index + fileMatch.Length >= buf.Length - visible.Length)
                                {
                                    _inFileCapture = true;
                                    _fileCaptureFileName = fileMatch.Groups[1].Value;
                                    _fileCaptureBuffer = new StringBuilder();
                                    _generatingTokenCount = 0;
                                    StartGeneratingAnimation(_fileCaptureFileName);
                                    return;
                                }

                                streamRun.Text += visible;
                                OutputBox.ScrollToEnd();
                            });
                        },
                        onComplete: () =>
                        {
                            ThreadHelper.JoinableTaskFactory.Run(async delegate
                            {
                                try
                                {
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                                AppendNewLine();

                                // Save any file captured during streaming (FILE:/END_FILE)
                                if (_fileCaptureBuffer != null && _fileCaptureBuffer.Length > 0)
                                {
                                    // Strip trailing END_FILE sentinel fragments caused by token boundary splits.
                                    // The model emits END_FILE but it may arrive as "END" + "_FILE" across two tokens,
                                    // causing "END" to be captured into the buffer before the sentinel is detected.
                                    string fileContent = _fileCaptureBuffer.ToString().TrimEnd();
                                    if (fileContent.EndsWith("\nEND_FILE") || fileContent.EndsWith("\r\nEND_FILE"))
                                        fileContent = fileContent.Substring(0, fileContent.LastIndexOf("END_FILE")).TrimEnd();
                                    else if (fileContent.EndsWith("\nEND") || fileContent.EndsWith("\r\nEND"))
                                        fileContent = fileContent.Substring(0, fileContent.LastIndexOf("\nEND")).TrimEnd();
                                    await SaveGeneratedFileAsync(_fileCaptureFileName, fileContent);
                                    _fileCaptureBuffer = null;
                                }

                                // Parse the full response for directives
                                string fullResponse = responseBuffer.ToString();
                                var blocks = ResponseParser.Parse(fullResponse);

                                var patchedPaths = new List<string>();
                                var shellOutputs = new StringBuilder();
                                bool ranShell = false;
                                bool hadReadRequest = false;
                                bool createdFile = blocks.Any(b => b.Type == BlockType.File);

                                foreach (var block in blocks)
                                {
                                    switch (block.Type)
                                    {
                                        case BlockType.File:
                                            // Already saved during streaming via _fileCaptureBuffer.
                                            // If streaming capture missed it (token boundary edge case),
                                            // save it now as fallback.
                                            if (_fileCaptureFileName == null || _fileCaptureFileName != block.FileName)
                                                await SaveGeneratedFileAsync(block.FileName, block.Content);
                                            break;

                                        case BlockType.Patch:
                                            AppendOutput($"[AUTO-PATCH] Executing PATCH {block.FileName}...\n", OutputColor.Dim);
                                            // Auto-READ the target file if not already in context
                                            string patchFileOnly = Path.GetFileName(block.FileName.Replace('\\', '/'));
                                            if (!string.IsNullOrEmpty(patchFileOnly))
                                            {
                                                string resolvedPath = await FindFileInSolutionAsync(patchFileOnly, block.FileName.Replace('\\', '/'))
                                                    ?? Path.Combine(_terminalWorkingDir, patchFileOnly);
                                                bool alreadyLoaded = _readContext != null && _readContext.Contains(resolvedPath);
                                                if (!alreadyLoaded)
                                                {
                                                    AppendOutput($"[AUTO-READ] Loading {patchFileOnly} before patch...\n", OutputColor.Dim);
                                                    await ApplyReadCommandAsync($"READ {block.FileName}");
                                                }
                                            }
                                            var appliedPath = await ApplyPatchAsync(block.Content, clearInput: false);
                                            if (appliedPath != null && !patchedPaths.Contains(appliedPath))
                                                patchedPaths.Add(appliedPath);
                                            break;

                                        case BlockType.Shell:
                                            AppendOutput($"[SHELL] > {block.Command}\n", OutputColor.Dim);
                                            var (output, exitCode) = await RunShellCommandCaptureAsync(block.Command);
                                            _lastShellExitCode = exitCode;
                                            AppendOutput(output + "\n", OutputColor.Normal);
                                            shellOutputs.AppendLine($"Shell command: {block.Command}\nOutput:\n{output}\n---");
                                            ranShell = true;
                                            break;

                                        case BlockType.ReadRequest:
                                            hadReadRequest = true;
                                            await ApplyReadCommandAsync("READ " + block.FileName);
                                            break;

                                        case BlockType.Text:
                                            // Already displayed during streaming
                                            break;
                                    }
                                }

                                // ── Agentic loop decision ─────────────────────────────────────

                                bool hasActions = patchedPaths.Count > 0 || ranShell || createdFile;

                                // Auto-READ resubmit: response was just a READ request, no other actions
                                if (hadReadRequest && !hasActions && !string.IsNullOrEmpty(_pendingResubmitPrompt))
                                {
                                    string saved = _pendingResubmitPrompt;
                                    _pendingResubmitPrompt = null;
                                    AppendOutput($"[AUTO-READ] File(s) loaded — resubmitting original prompt...\n", OutputColor.Dim);
                                    InputTextBox.Text = saved;
                                    SendToLlm();
                                    return;
                                }

                                // Build succeeded — done
                                if (ranShell && _lastShellExitCode == 0)
                                {
                                    AppendOutput("[AGENTIC] Build succeeded — task complete.\n", OutputColor.Success);
                                    _agenticDepth = 0;
                                    _pendingResubmitPrompt = null;
                                    // fall through to completion
                                }
                                // Build failed or actions taken without build — re-trigger if depth allows
                                else if (hasActions)
                                {
                                    int maxDepth = DevMindOptions.Instance.AgenticLoopMaxDepth;
                                    if (maxDepth <= 0 || _agenticDepth >= maxDepth)
                                    {
                                        if (ranShell && _lastShellExitCode != 0)
                                        {
                                            int undoDepth = _patchBackupStack.Count;
                                            AppendOutput($"[AGENTIC] Depth cap reached ({_agenticDepth}) — build still failing.\n", OutputColor.Error);
                                            AppendOutput("Type UNDO to revert all changes, or continue editing manually.\n", OutputColor.Dim);
                                            AppendOutput($"({undoDepth} change(s) can be undone)\n", OutputColor.Dim);
                                        }
                                        else
                                        {
                                            AppendOutput($"[AGENTIC] Depth cap reached ({_agenticDepth}). Stopping.\n", OutputColor.Dim);
                                        }
                                        _agenticDepth = 0;
                                    }
                                    else
                                    {
                                        _agenticDepth++;
                                        var agenticContext = new StringBuilder();
                                        agenticContext.AppendLine($"[AGENTIC LOOP — iteration {_agenticDepth} of {maxDepth}]");

                                        if (createdFile)
                                            agenticContext.AppendLine($"[FILE CREATED] File was generated and added to the project.");

                                        agenticContext.Append(shellOutputs);

                                        // Inject current file state for patched files
                                        foreach (var pp in patchedPaths)
                                        {
                                            try
                                            {
                                                string content = File.ReadAllText(pp);
                                                agenticContext.AppendLine($"\n[Current state — {Path.GetFileName(pp)}]\n{content}");
                                            }
                                            catch { }
                                        }

                                        agenticContext.AppendLine("Continue with any remaining steps. If all steps are complete and the build succeeded, respond with DONE.");
                                        _pendingShellContext = agenticContext.ToString().TrimEnd();

                                        InputTextBox.Text = ranShell
                                            ? "The shell output above shows build errors. Analyze the specific error messages and apply targeted PATCH fixes. Do NOT re-add code that already exists in the file. Do NOT replace code with unrelated code."
                                            : hasActions && !ranShell
                                                ? "Continue with remaining steps (modify other files, run builds). If done, respond with DONE."
                                                : "Continue with remaining steps.";

                                        // Check cancellation before re-triggering
                                        if (_cts.IsCancellationRequested)
                                        {
                                            _shellLoopPending = false;
                                            _agenticDepth = 0;
                                            AppendOutput("[AGENTIC] Cancelled.\n", OutputColor.Dim);
                                            StatusText.Text = "Stopped";
                                            SetInputEnabled(true);
                                            return;
                                        }
                                        _shellLoopPending = true;
                                        try
                                        {
                                            SendToLlm();
                                        }
                                        catch
                                        {
                                            _shellLoopPending = false;
                                            _agenticDepth = 0;
                                            StatusText.Text = "Error";
                                            SetInputEnabled(true);
                                            throw;
                                        }
                                        return;
                                    }
                                }
                                else
                                {
                                    // No actions — check for bare code block (model ignored directives)
                                    bool hasFencedCode = fullResponse.Contains("```");
                                    bool hasDirectives = blocks.Any(b => b.Type != BlockType.Text);
                                    if (!hasDirectives && hasFencedCode && _agenticDepth == 0
                                        && !string.IsNullOrEmpty(_pendingResubmitPrompt))
                                    {
                                        string saved = _pendingResubmitPrompt;
                                        _pendingResubmitPrompt = null;
                                        AppendOutput("[FORMAT] Response was a code block instead of directives — retrying...\n", OutputColor.Dim);
                                        _pendingShellContext = "Your previous response contained a raw code block instead of PATCH/SHELL/FILE directives. " +
                                            "You MUST use FILE: for new files, PATCH for edits, and SHELL: for commands. " +
                                            "Re-attempt the task now using the correct format.";
                                        InputTextBox.Text = saved;
                                        _shellLoopPending = true;
                                        _agenticDepth = 1;
                                        SendToLlm();
                                        return;
                                    }
                                }

                                // ── Completion ────────────────────────────────────────────────

                                _agenticDepth = 0;
                                _pendingResubmitPrompt = null;
                                StatusText.Text = "Ready";
                                ContextIndicator.Text = "";
                                SetInputEnabled(true);
                                InputTextBox.Focus();
                                }
                                catch (Exception onCompleteEx) when (!(onCompleteEx is OperationCanceledException))
                                {
                                    _shellLoopPending = false;
                                    _agenticDepth = 0;
                                    AppendOutput($"\n[onComplete error: {onCompleteEx.Message}]\n", OutputColor.Error);
                                    StatusText.Text = "Error";
                                    SetInputEnabled(true);
                                }
                            });
                        },
                        onError: ex =>
                        {
                            ThreadHelper.JoinableTaskFactory.Run(async delegate
                            {
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                StopGeneratingAnimation();
                                _shellLoopPending = false;
                                _agenticDepth = 0;
                                _pendingResubmitPrompt = null;
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
                        StopGeneratingAnimation();
                        AppendOutput("\n[Stopped]\n", OutputColor.Dim);
                        StatusText.Text = "Stopped";
                        _shellLoopPending = false;
                        _agenticDepth = 0;
                        _pendingResubmitPrompt = null;
                    }
                    // Skip re-enabling input when the agentic loop is still active.
                    // The re-triggered SendToLlm() manages its own SetInputEnabled(false) call;
                    // calling SetInputEnabled(true) here would disable Stop and re-enable input
                    // between agentic iterations.
                    if (!_shellLoopPending)
                    {
                        _agenticDepth = 0;
                        SetInputEnabled(true);
                    }
                    _shellLoopPending = false;
                }
            });
            // Restore original system prompt now that UpdateSystemPrompt() has already
            // captured the combined value into conversation history.
            DevMindOptions.Instance.SystemPrompt = originalSystemPrompt;
#pragma warning restore VSTHRD100
#pragma warning restore VSSDK007
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

    }
}
