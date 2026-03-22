// File: DevMindToolWindowControl.xaml.cs  v5.0.52
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
        private System.Windows.Threading.DispatcherTimer _thinkingTimer;
        private int _thinkingSeconds;
        private string _devMindContext;
        private string _readContext;
        private string _pendingShellContext;
        private bool _shellLoopPending;
        private int _agenticDepth;
        private int _blockByBlockStep;   // current step number (1-based); 0 = not in block-by-block mode
        private int _blockByBlockTotal;  // total step count for current block-by-block task
        private int _lastShellExitCode;
        private string _lastShellCommand;
        private bool _inThinkBlock;
        private readonly StringBuilder _thinkBuffer = new StringBuilder();
        private string _pendingThinkText;   // set by FilterChunk when ShowLlmThinking is true
        private readonly Stack<(string originalPath, string backupPath)> _patchBackupStack = new Stack<(string, string)>();
        private const int PatchBackupStackLimit = 10;
        private Paragraph _spacerParagraph;
        private string _pendingResubmitPrompt;
        private Action _batchOnComplete;
        private bool _suppressDisplay;
        private int _patchCount = 0;
        private int _undoCount = 0;
        private int _readFileCount = 0;

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
                    OutputColor.Dim      => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    OutputColor.Input    => new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)),
                    OutputColor.Error    => new SolidColorBrush(Color.FromRgb(0xF4, 0x48, 0x47)),
                    OutputColor.Success  => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x4E)),
                    OutputColor.Thinking => new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x8A)),
                    _                    => new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
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
            _readFileCount = 0;

            // Discard pending fuzzy confirmation
            _pendingFuzzyPatch = null;

            // Discard all PATCH backups and clean up temp files
            while (_patchBackupStack.Count > 0)
            {
                var (_, backupPath) = _patchBackupStack.Pop();
                try { File.Delete(backupPath); } catch { }
            }

    // Reset stats counters
            _patchCount = 0;
            _undoCount = 0;

            AppendOutput("DevMind restarted.\n", OutputColor.Dim);

            // Re-detect context size after restart/reconnect
#pragma warning disable VSSDK007 // Fire-and-forget is intentional for background detection
            _ = _llmClient.DetectContextSizeAsync();
#pragma warning restore VSSDK007
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
            _pendingThinkText = null;
            bool showThinking = DevMindOptions.Instance.ShowLlmThinking;

            if (_inThinkBlock)
            {
                _thinkBuffer.Append(chunk);
                string bufStr = _thinkBuffer.ToString();
                int closeIdx = bufStr.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (closeIdx >= 0)
                {
                    string after = bufStr.Substring(closeIdx + "</think>".Length);
                    _inThinkBlock = false;
                    // Show only the portion of the current chunk that falls before </think>
                    int chunkCloseIdx = chunk.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                    if (showThinking && chunkCloseIdx > 0)
                        _pendingThinkText = chunk.Substring(0, chunkCloseIdx);
                    _thinkBuffer.Clear();
                    return after;
                }
                if (showThinking)
                    _pendingThinkText = chunk;
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

                int closeIdx = rest.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (closeIdx >= 0)
                {
                    string thinkContent = rest.Substring(0, closeIdx);
                    string after = rest.Substring(closeIdx + "</think>".Length);
                    _inThinkBlock = false;
                    _thinkBuffer.Clear();
                    if (showThinking && !string.IsNullOrEmpty(thinkContent))
                        _pendingThinkText = "[THINKING] " + thinkContent;
                    return before + after;
                }
                if (showThinking && !string.IsNullOrEmpty(rest))
                    _pendingThinkText = "[THINKING] " + rest;
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

            // [WAIT] batch: if the input contains a line that is exactly "[WAIT]" (case-insensitive),
            // split into blocks and send each block sequentially, waiting for completion between them.
            if (!_shellLoopPending && ContainsWaitSeparator(text))
            {
                await ProcessBatchInputAsync(text);
                return;
            }

            // Reset agentic depth for user-initiated calls; preserve it for agentic re-triggers
            if (!_shellLoopPending)
                _agenticDepth = 0;

            // Known /command handlers — must be checked before the generic shell router below
            if (text.Equals("/stats", StringComparison.OrdinalIgnoreCase))
            {
                InputTextBox.Text = "";
                await DisplayStatsAsync();
                return;
            }

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

            // Auto-read files referenced in the prompt (user-initiated turns only)
            if (!_shellLoopPending)
                await AutoReadReferencedFilesAsync(text);

            string activeProjectPath = await GetActiveProjectPathAsync();
            string contextualMessage = BuildMessageWithContext(text, selectedText, fileName, fullContent, activeProjectPath);

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

            StatusText.Text = _blockByBlockStep > 0
                ? $"Thinking... (step {_blockByBlockStep}/{_blockByBlockTotal})"
                : _agenticDepth > 0
                    ? $"Thinking... (agentic {_agenticDepth}/{DevMindOptions.Instance.AgenticLoopMaxDepth})"
                    : "Thinking...";
            _thinkingSeconds = 0;
            _thinkingTimer?.Stop();
            _thinkingTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _thinkingTimer.Tick += (s, e) =>
            {
                _thinkingSeconds++;
                string agenticSuffix = _blockByBlockStep > 0
                    ? $" (step {_blockByBlockStep}/{_blockByBlockTotal})"
                    : _agenticDepth > 0
                        ? $" (agentic {_agenticDepth}/{DevMindOptions.Instance.AgenticLoopMaxDepth})"
                        : "";
                StatusText.Text = $"Thinking{agenticSuffix} ({_thinkingSeconds}s)";
            };
            _thinkingTimer.Start();
            _cts = new CancellationTokenSource();

            // Reset display-suppression state
            _suppressDisplay = false;

            var streamPara = new Paragraph { Margin = new Thickness(0) };
            OutputBox.Document.Blocks.Add(streamPara);
            var thinkRun = new Run { Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x8A)) };
            streamPara.Inlines.Add(thinkRun);
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

            string buildCommand = activeProjectPath != null
                ? $"dotnet build \"{activeProjectPath}\""
                : "msbuild \"C:\\Users\\pkailas.KAILAS\\source\\repos\\DevMind\\DevMind.slnx\" /p:DeployExtension=false /verbosity:minimal";

            string llmDirective =
                "## Directives\n" +
                "FILE: <filename>\n<raw source>\nEND_FILE\n\n" +
                "PATCH <filename>\nFIND:\n<exact text>\nREPLACE:\n<replacement>\nEND_PATCH\n" +
                "Multiple FIND/REPLACE pairs are allowed before END_PATCH. END_PATCH is required and must appear on its own line after the last REPLACE block.\n\n" +
                "SHELL: <command>\n\n" +
                "READ <filename>  — full if <100 lines, outline otherwise\n" +
                "READ <filename>:<start>-<end>  — targeted line range (1-based)\n" +
                "READ! <filename>  — force full content (expensive)\n\n" +
                "## Build Verification\n" +
                $"After ANY code change emit: SHELL: {buildCommand}\n\n" +
                "## After PATCH\n" +
                "You receive [PATCH-RESULT:filename] with ±3 lines of context and >>> CHANGED:/>>> ADDED: markers.\n" +
                "Full file is cached — use READ filename:start-end for more context.\n\n" +
                "## Before PATCH\n" +
                "Never output raw code blocks. Use FILE: for new files, PATCH for edits.\n" +
                "READ the file first if you have not seen it. You may combine FILE, PATCH, SHELL in one response.\n" +
                "FIND text must be copied verbatim from READ output — never reconstructed from memory.\n" +
                "Do not read the same file multiple times. If you have an outline and a line range, that is sufficient context to write a PATCH. Act immediately.\n\n" +
                "## Large File Strategy\n" +
                "For files over 100 lines:\n" +
                "1. First READ gets an outline (types, methods, signatures with line numbers).\n" +
                "2. Use the outline to identify the exact line range you need.\n" +
                "3. READ filename:start-end for just that section.\n" +
                "4. PATCH using only the content from that range.\n" +
                "Never READ! an entire large file unless explicitly asked. Work from outline → range → patch.\n\n" +
                "## Core rules\n" +
                "After a READ is loaded, act on it immediately in the same response. Never emit only READ directives and stop. Every response must include at least one PATCH, FILE, or SHELL directive unless you are responding to a question.\n\n" +
                "## Task Completion\n" +
                "When all steps of the task are complete and nothing remains to do, emit:\nDONE\n" +
                "Only emit DONE when the task is truly finished. Do not emit DONE mid-task.\n\n" +
                "## Scratchpad\n" +
                "Emit a SCRATCHPAD: block (end with END_SCRATCHPAD on its own line) to track state across turns:\n" +
                "SCRATCHPAD:\nGoal: <task>\nFiles: <file> (lines N-M)\nStatus: <PLANNING|PATCHING|BUILDING|DONE>\nLast: <action>\nNext: <step>\nEND_SCRATCHPAD";
            if (DevMindOptions.Instance.BlockByBlockMode != BlockByBlockModeType.Off)

                llmDirective +=
                    "\n\n## Block-by-Block Mode (Active)\n" +
                    "You are operating in block-by-block mode for memory-constrained environments.\n" +
                    "Rules:\n" +
                    "1. Start each task by READing the file outline only — do not request full content.\n" +
                    "2. Each turn: READ one range, emit one PATCH, update SCRATCHPAD with remaining steps.\n" +
                    "3. Do not attempt multiple file sections in a single response.\n" +
                    "4. After each PATCH, mark that step done in SCRATCHPAD before continuing.\n" +
                    "5. If more steps remain, state the next step clearly and wait for the next turn.\n" +
                    "Work incrementally: outline → one range → one patch → repeat until done.";
            if (!string.IsNullOrEmpty(projectNamespace))
                llmDirective += $"\n- When creating new files, use the namespace '{projectNamespace}'.";
            string combined = $"{originalSystemPrompt}\n\n{llmDirective}";
            if (!string.IsNullOrEmpty(_devMindContext))
                combined += $"\n\n--- Project Context (DevMind.md) ---\n{_devMindContext}\n---";
            DevMindOptions.Instance.SystemPrompt = combined;

            // Guard: always restore the original system prompt, even if RunAsync throws
            // synchronously or an exception propagates before RunAsync returns.
            try
            {
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
                            // Fire-and-forget UI dispatch is intentional — streaming reader must not block on UI thread.
                            // BeginInvoke queues work items in FIFO order so tokens are always rendered in arrival order.
#pragma warning disable VSTHRD001, VSTHRD110
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
#pragma warning restore VSTHRD001, VSTHRD110
                                if (_cts?.IsCancellationRequested == true) return;
                                var visible = FilterChunk(token);

                                // Route thinking tokens to the dim think run
                                if (_pendingThinkText != null)
                                {
                                    thinkRun.Text += _pendingThinkText;
                                    OutputBox.ScrollToEnd();
                                }

                                if (string.IsNullOrEmpty(visible)) return;

                                responseBuffer.Append(visible);

                                // Lightweight display-suppression for FILE: blocks.
                                // Only inspect at newline boundaries — partial lines are never checked.
                                // onComplete / ResponseParser.Parse() is the sole authority for all parsing;
                                // _suppressDisplay only affects what the user sees in the OutputBox during streaming.
                                if (visible.Contains('\n'))
                                {
                                    string buf = responseBuffer.ToString();
                                    int lastNl = buf.LastIndexOf('\n');
                                    int prevNl = lastNl > 0 ? buf.LastIndexOf('\n', lastNl - 1) : -1;
                                    string completedLine = buf.Substring(prevNl + 1, lastNl - prevNl - 1).TrimEnd('\r');

                                    if (!_suppressDisplay && Regex.IsMatch(completedLine, @"^FILE:\s*\S", RegexOptions.IgnoreCase))
                                    {
                                        _suppressDisplay = true;
                                        StartGeneratingAnimation(completedLine.Substring("FILE:".Length).Trim());
                                    }
                                    else if (_suppressDisplay && string.Equals(completedLine, "END_FILE", StringComparison.OrdinalIgnoreCase))
                                    {
                                        _suppressDisplay = false;
                                        StopGeneratingAnimation();
                                    }
                                }

                                if (_suppressDisplay) return;

                                if (_thinkingTimer != null)
                                {
                                    _thinkingTimer.Stop();
                                    _thinkingTimer = null;
                                    StatusText.Text = "Generating...";
                                }
                                streamRun.Text += visible;
                                OutputBox.ScrollToEnd();
                            }));
                        },
                        onComplete: () =>
                        {
                            // Fire-and-forget dispatch to ensure FIFO ordering after all onToken dispatches.
                            // BeginInvoke guarantees responseBuffer is fully populated before the completion logic reads it.
#pragma warning disable VSTHRD001, VSTHRD110
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
#pragma warning restore VSTHRD001, VSTHRD110
#pragma warning disable VSSDK007, VSTHRD110
                            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                            {
                                try
                                {
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                                AppendNewLine();

                                // ── Classify → Decide → Execute pipeline ──────────────────────

                                string fullResponse = responseBuffer.ToString();
                                System.Diagnostics.Debug.WriteLine($"[DEVMIND-DIAG] responseBuffer length={fullResponse.Length}");
                                System.Diagnostics.Debug.WriteLine($"[DEVMIND-DIAG] responseBuffer first 500 chars: {(fullResponse.Length > 500 ? fullResponse.Substring(0, 500) : fullResponse)}");

                                var outcome  = ResponseClassifier.Classify(fullResponse);
                                var executor = new AgenticExecutor(this);
                                int maxDepth = DevMindOptions.Instance.AgenticLoopMaxDepth;

                                System.Diagnostics.Debug.WriteLine($"[DEVMIND-DIAG] Outcome: HasPatches={outcome.HasPatches} HasShell={outcome.HasShellCommands} HasFile={outcome.HasFileCreation} IsDone={outcome.IsDone} IsReadOnly={outcome.IsReadOnly} IsEmptyOrBareCode={outcome.IsEmptyOrBareCode}");

                                // Initial resolve — no previousResult on the first call
                                AgenticAction action = AgenticActionResolver.Resolve(outcome, null, _agenticDepth, maxDepth);

                                if (action.Type != ActionType.Stop)
                                {
                                    ExecutionResult result = await executor.ExecuteAsync(action, outcome);

                                    // ── Resubmit paths (return immediately; SendToLlm manages its own completion) ──

                                    if (action.Type == ActionType.LoadAndResubmit)
                                    {
                                        if (_agenticDepth > 0)
                                        {
                                            AppendOutput("[AUTO-READ] File(s) loaded during agentic iteration — continuing...\n", OutputColor.Dim);
                                            InputTextBox.Text = "The requested file(s) have been loaded into context. Continue with the task using PATCH/SHELL/FILE directives.";
                                        }
                                        else if (!string.IsNullOrEmpty(_pendingResubmitPrompt))
                                        {
                                            string saved = _pendingResubmitPrompt;
                                            _pendingResubmitPrompt = null;
                                            AppendOutput("[AUTO-READ] File(s) loaded — resubmitting original prompt...\n", OutputColor.Dim);
                                            InputTextBox.Text = saved;
                                        }
                                        else
                                        {
                                            AppendOutput("[AUTO-READ] File(s) loaded — resubmitting...\n", OutputColor.Dim);
                                            InputTextBox.Text = "The requested file(s) have been loaded. Continue with the task.";
                                        }
                                        _shellLoopPending = true;
                                        SendToLlm();
                                        return;
                                    }

                                    if (action.Type == ActionType.RetryWithCorrection)
                                    {
                                        AppendOutput("[FORMAT] Response was a code block instead of directives — retrying...\n", OutputColor.Dim);
                                        _pendingShellContext =
                                            "Your previous response contained a raw code block instead of PATCH/SHELL/FILE directives. " +
                                            "You MUST use FILE: for new files, PATCH for edits, and SHELL: for commands. " +
                                            "Re-attempt the task now using the correct format.";
                                        string retryPrompt = _pendingResubmitPrompt ?? InputTextBox.Text;
                                        _pendingResubmitPrompt = null;
                                        string fmtPostTurn = _llmClient.CompressLastUserReadBlocks();
                                        if (fmtPostTurn != null) AppendOutput(fmtPostTurn, OutputColor.Dim);
                                        InputTextBox.Text = retryPrompt;
                                        _shellLoopPending = true;
                                        _agenticDepth = 1;
                                        SendToLlm();
                                        return;
                                    }

                                    // ── Actions that may loop ────────────────────────────────────

                                    if (action.Type == ActionType.ApplyAndBuild
                                        || action.Type == ActionType.CreateFile
                                        || action.Type == ActionType.RunShell
                                        || action.Type == ActionType.ContinueAgentic)
                                    {
                                        bool hasDone  = outcome.IsDone;
                                        bool ranShell = result.ShellExitCode.HasValue;
                                        bool isRunExec = ranShell && result.ShellExitCode == 0
                                            && !string.IsNullOrEmpty(result.LastShellCommand)
                                            && IsRunOrExecCommand(result.LastShellCommand);

                                        if (hasDone && !ranShell && result.PatchesApplied == 0 && result.FilesCreated.Count == 0)
                                        {
                                            AppendOutput("[AGENTIC] Task complete.\n", OutputColor.Success);
                                            _agenticDepth = 0;
                                            _pendingResubmitPrompt = null;
                                            // fall through to completion
                                        }
                                        else if (isRunExec)
                                        {
                                            AppendOutput("[AGENTIC] Run/exec command succeeded — treating as task complete.\n", OutputColor.Success);
                                            _agenticDepth = 0;
                                            _pendingResubmitPrompt = null;
                                            // fall through to completion
                                        }
                                        else if (hasDone)
                                        {
                                            AppendOutput("[AGENTIC] Task complete.\n", OutputColor.Success);
                                            _agenticDepth = 0;
                                            _pendingResubmitPrompt = null;
                                            // fall through to completion
                                        }
                                        else if (maxDepth <= 0 || _agenticDepth >= maxDepth)
                                        {
                                            if (ranShell && result.ShellExitCode != 0)
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
                                            // fall through to completion
                                        }
                                        else
                                        {
                                            // ── Re-trigger agentic loop ───────────────────────────

                                            _agenticDepth++;
                                            {
                                                int agTokens  = _llmClient.EstimateHistoryTokens();
                                                int agBudget  = _llmClient.MaxPromptTokens;
                                                int agHeadroom = _llmClient.ResponseHeadroomTokens;
                                                int agPct     = agBudget > 0 ? (int)((agTokens * 100.0) / agBudget) : 0;
                                                AppendOutput($"[AGENTIC] Iteration {_agenticDepth}/{maxDepth} — Working: {agTokens:N0} / {agBudget:N0} ({agPct}%) | Response headroom: {agHeadroom:N0} reserved\n", OutputColor.Dim);
                                                if (DevMindOptions.Instance.ShowContextBudget)
                                                {
                                                    int cbPct = _llmClient.ContextBudgetPercent;
                                                    OutputColor cbColor = cbPct < 60 ? OutputColor.Dim
                                                                        : cbPct < 80 ? OutputColor.Normal
                                                                        : OutputColor.Error;
                                                    AppendOutput($"[CONTEXT] Working: {agTokens:N0} / {agBudget:N0} ({cbPct}%) | Response headroom: {agHeadroom:N0} reserved\n", cbColor);
                                                }
                                            }

                                            // Block-by-block mode (preserved)
                                            bool blockByBlockAllDone = false;
                                            if (result.BuildSucceeded && DevMindOptions.Instance.BlockByBlockMode != BlockByBlockModeType.Off)
                                            {
                                                var steps = ParseScratchpadSteps(_llmClient.TaskScratchpad);
                                                var nextStep = steps.Find(s => !s.IsDone);
                                                if (nextStep.StepNumber > 0)
                                                {
                                                    _blockByBlockStep  = nextStep.StepNumber;
                                                    _blockByBlockTotal = steps.Count;
                                                    AppendOutput($"[AGENTIC] Block-by-block: advancing to step {nextStep.StepNumber}/{steps.Count}\n", OutputColor.Dim);
                                                    _llmClient.ClearHistory(preserveScratchpad: true);
                                                    InputTextBox.Text = $"Continue with step {nextStep.StepNumber}: {nextStep.Description}. READ the relevant file range first, then PATCH. Build when done.";
                                                    _pendingShellContext = null;
                                                    _agenticDepth = 0;
                                                    if (_cts.IsCancellationRequested)
                                                    {
                                                        _shellLoopPending = false;
                                                        _blockByBlockStep = 0;
                                                        AppendOutput("[AGENTIC] Cancelled.\n", OutputColor.Dim);
                                                        StatusText.Text = "Stopped";
                                                        SetInputEnabled(true);
                                                        return;
                                                    }
                                                    _shellLoopPending = true;
                                                    try { SendToLlm(); }
                                                    catch
                                                    {
                                                        _shellLoopPending = false;
                                                        _blockByBlockStep = 0;
                                                        _agenticDepth = 0;
                                                        StatusText.Text = "Error";
                                                        SetInputEnabled(true);
                                                        throw;
                                                    }
                                                    return;
                                                }
                                                else if (steps.Count > 0)
                                                {
                                                    // All numbered steps are DONE — task complete
                                                    AppendOutput("[AGENTIC] Block-by-block: all steps complete.\n", OutputColor.Success);
                                                    _blockByBlockStep = 0;
                                                    string bbDeferredLog = _llmClient.RunDeferredCompression();
                                                    if (bbDeferredLog != null) AppendOutput(bbDeferredLog, OutputColor.Dim);
                                                    blockByBlockAllDone = true;
                                                }
                                            }

                                            if (!blockByBlockAllDone)
                                            {
                                                // Build continuation context from ExecutionResult
                                                var agenticContext = new StringBuilder();
                                                agenticContext.AppendLine($"[AGENTIC LOOP — iteration {_agenticDepth} of {maxDepth}]");

                                                if (result.FilesCreated.Count > 0)
                                                    agenticContext.AppendLine("[FILE CREATED] File was generated and added to the project.");

                                                if (!string.IsNullOrEmpty(result.ShellOutput))
                                                {
                                                    string shellTag = result.LastShellCommand?.Length > 60
                                                        ? result.LastShellCommand.Substring(0, 60) : result.LastShellCommand;
                                                    agenticContext.AppendLine($"[SHELL-RESULT:{shellTag}]\nShell command: {result.LastShellCommand}\nOutput:\n{result.ShellOutput}\n---");
                                                }

                                                // Inject diff-only PATCH-RESULT (changed region ± context lines)
                                                foreach (string pp in result.PatchedPaths)
                                                {
                                                    try
                                                    {
                                                        string fn = Path.GetFileName(pp);
                                                        int undoDepth = _patchBackupStack.Count;
                                                        string diffView = _patchDiffCache.TryGetValue(pp, out string dv) ? dv : null;
                                                        if (diffView != null)
                                                            agenticContext.AppendLine($"\n[PATCH-RESULT:{fn}] Applied successfully (undo depth: {undoDepth})\n{diffView}");
                                                        else
                                                            agenticContext.AppendLine($"\n[PATCH-RESULT:{fn}] Applied successfully (undo depth: {undoDepth})");
                                                    }
                                                    catch { }
                                                }

                                                // Per-block failure messages (includes PATCH-FAILED lines)
                                                foreach (string err in result.Errors)
                                                    agenticContext.AppendLine(err);

                                                agenticContext.AppendLine("Continue with any remaining steps. When there is nothing left to do, emit DONE on a line by itself.");
                                                _pendingShellContext = agenticContext.ToString().TrimEnd();

                                                // Set continuation prompt
                                                if (result.BuildSucceeded)
                                                    InputTextBox.Text = "Build succeeded (exit code 0). Continue with any remaining steps from the original task, or emit DONE if the task is complete.";
                                                else if (ranShell && result.ShellExitCode != 0)
                                                    InputTextBox.Text = "The shell output above shows build errors. Analyze the specific error messages and apply targeted PATCH fixes. Do NOT re-add code that already exists in the file. Do NOT replace code with unrelated code.";
                                                else
                                                    InputTextBox.Text = "Continue with remaining steps (modify other files, run builds). When done, emit DONE.";

                                                // Post-turn: outline-compress READ blocks in the completed user message
                                                string postTurnMsg = _llmClient.CompressLastUserReadBlocks();
                                                if (postTurnMsg != null) AppendOutput(postTurnMsg, OutputColor.Dim);

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
                                                try { SendToLlm(); }
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
                                            // blockByBlockAllDone == true → fall through to completion
                                        }
                                    }
                                }
                                else
                                {
                                    // Stop action — display reason if meaningful
                                    if (!string.IsNullOrEmpty(action.StopReason) && action.StopReason != "Response complete.")
                                    {
                                        bool isSuccess = action.StopReason.Contains("complete") || action.StopReason.Contains("succeeded");
                                        AppendOutput(action.StopReason + "\n", isSuccess ? OutputColor.Success : OutputColor.Dim);
                                    }
                                }

                                // ── Completion ────────────────────────────────────────────────

                                if (DevMindOptions.Instance.ShowContextBudget)
                                {
                                    int cbUsed     = _llmClient.EstimateHistoryTokens();
                                    int cbBudget   = _llmClient.MaxPromptTokens;
                                    int cbHeadroom = _llmClient.ResponseHeadroomTokens;
                                    int cbPct      = _llmClient.ContextBudgetPercent;
                                    OutputColor cbColor = cbPct < 60 ? OutputColor.Dim
                                                        : cbPct < 80 ? OutputColor.Normal
                                                        : OutputColor.Error;
                                    AppendOutput($"[CONTEXT] Working: {cbUsed:N0} / {cbBudget:N0} ({cbPct}%) | Response headroom: {cbHeadroom:N0} reserved\n", cbColor);
                                }

                                // Deferred compression: run all five phases once the agentic loop
                                // has completed to keep history lean for the next user turn,
                                // without invalidating the KV cache during agentic iterations.
                                if (_shellLoopPending)
                                {
                                    string deferredLog = _llmClient.RunDeferredCompression();
                                    if (deferredLog != null)
                                        AppendOutput(deferredLog, OutputColor.Dim);
                                }

                                _agenticDepth = 0;
                                _blockByBlockStep = 0;
                                _pendingResubmitPrompt = null;
                                _thinkingTimer?.Stop();
                                _thinkingTimer = null;
                                StatusText.Text = "Ready";
                                ContextIndicator.Text = "";
                                SetInputEnabled(true);
                                InputTextBox.Focus();
                                _batchOnComplete?.Invoke();
                                _batchOnComplete = null;
                                }
                                catch (Exception onCompleteEx) when (!(onCompleteEx is OperationCanceledException))
                                {
                                    _shellLoopPending = false;
                                    _agenticDepth = 0;
                                    _blockByBlockStep = 0;
                                    _thinkingTimer?.Stop();
                                    _thinkingTimer = null;
                                    AppendOutput($"\n[onComplete error: {onCompleteEx.Message}]\n", OutputColor.Error);
                                    AppendOutput($"{onCompleteEx.StackTrace}\n", OutputColor.Error);
                                    StatusText.Text = "Error";
                                    SetInputEnabled(true);
                                }
                            });
#pragma warning restore VSSDK007, VSTHRD110
                            }));
                        },
                        onError: ex =>
                        {
                            // Fire-and-forget UI cleanup is intentional — error callback must not block the streaming reader.
#pragma warning disable VSTHRD001, VSTHRD110
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
#pragma warning restore VSTHRD001, VSTHRD110
                                _suppressDisplay = false;
                                StopGeneratingAnimation();
                                _thinkingTimer?.Stop();
                                _thinkingTimer = null;
                                _shellLoopPending = false;
                                _agenticDepth = 0;
                                _blockByBlockStep = 0;
                                _pendingResubmitPrompt = null;
                                AppendOutput($"\n[Error: {ex.Message}]\n", OutputColor.Error);
                                StatusText.Text = "Error";
                                SetInputEnabled(true);
                                _batchOnComplete?.Invoke();
                                _batchOnComplete = null;
                            }));
                        },
                        deferCompression: _shellLoopPending,
                        cancellationToken: _cts.Token);
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (_cts.IsCancellationRequested)
                    {
                        _suppressDisplay = false;
                        StopGeneratingAnimation();
                        _thinkingTimer?.Stop();
                        _thinkingTimer = null;
                        AppendOutput("\n[Stopped]\n", OutputColor.Dim);
                        StatusText.Text = "Stopped";
                        _shellLoopPending = false;
                        _agenticDepth = 0;
                        _blockByBlockStep = 0;
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
#pragma warning restore VSTHRD100
#pragma warning restore VSSDK007
            }
            finally
            {
                // Restore original system prompt now that RunAsync has started and
                // UpdateSystemPrompt() has captured the combined value synchronously
                // before the first await in SendMessageAsync.
                DevMindOptions.Instance.SystemPrompt = originalSystemPrompt;
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
#pragma warning disable VSSDK007
                    _ = ApplyPendingFuzzyPatchAsync(pending);
#pragma warning restore VSSDK007
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

        private async Task DisplayStatsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            int historyTokens = _llmClient.EstimateHistoryTokens();
            int budgetPct    = _llmClient.ContextBudgetPercent;

            AppendOutput("\n", OutputColor.Dim);
            AppendOutput("─────────────────────────────────────────\n", OutputColor.Dim);
            AppendOutput($"PATCH operations this session: {_patchCount}\n", OutputColor.Normal);
            AppendOutput($"UNDO operations this session:  {_undoCount}\n", OutputColor.Normal);
            AppendOutput($"Files in READ context:         {_readFileCount}\n", OutputColor.Normal);
            AppendOutput($"Context budget:                ~{historyTokens} tokens ({budgetPct}% of {_llmClient.MaxPromptTokens})\n", OutputColor.Normal);
            AppendOutput("─────────────────────────────────────────\n", OutputColor.Dim);
        }

        /// <summary>
        /// Returns true if the shell command is a run/exec invocation (not a build command).
        /// These commands complete a task when they exit 0 and should not trigger agentic continuation.
        /// </summary>
        private static bool IsRunOrExecCommand(string command)
        {
            if (string.IsNullOrEmpty(command))
                return false;
            string cmd = command.Trim().ToLowerInvariant();
            return cmd.Contains("dotnet run")
                || cmd.Contains("dotnet exec")
                || (cmd.EndsWith(".exe") && !cmd.Contains("msbuild") && !cmd.Contains("dotnet build"));
        }

        /// <summary>
        /// Represents a single parsed step from the SCRATCHPAD block.
        /// </summary>
        private struct ScratchpadStep
        {
            public int    StepNumber;
            public bool   IsDone;
            public string Description;
        }

        /// <summary>
        /// Parses numbered steps from a SCRATCHPAD block.
        /// Recognises lines of the form: "N. [DONE] description" / "N. DONE: description" / "N. description"
        /// A step is done when the text immediately after "N." contains "DONE" (case-insensitive).
        /// Returns an empty list when the scratchpad is blank or contains no numbered steps.
        /// </summary>
        private static System.Collections.Generic.List<ScratchpadStep> ParseScratchpadSteps(string scratchpad)
        {
            var result = new System.Collections.Generic.List<ScratchpadStep>();
            if (string.IsNullOrWhiteSpace(scratchpad))
                return result;

            var stepLine = new System.Text.RegularExpressions.Regex(
                @"^\s*(\d+)\.\s+(.+)$",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            foreach (System.Text.RegularExpressions.Match m in stepLine.Matches(scratchpad))
            {
                int  num  = int.Parse(m.Groups[1].Value);
                string raw = m.Groups[2].Value.Trim();
                // Strip leading "[DONE]" / "DONE:" markers to get clean description
                bool done = System.Text.RegularExpressions.Regex.IsMatch(
                    raw, @"^(\[DONE\]|DONE\s*:)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string desc = done
                    ? System.Text.RegularExpressions.Regex.Replace(
                        raw, @"^(\[DONE\]|DONE\s*:)\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim()
                    : raw;
                result.Add(new ScratchpadStep { StepNumber = num, IsDone = done, Description = desc });
            }
            return result;
        }

        private static bool ContainsWaitSeparator(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                if (line.Trim('\r').Trim().Equals("[WAIT]", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

#pragma warning disable VSSDK007 // async void UI event handler is intentional
#pragma warning disable VSTHRD100
        private async Task ProcessBatchInputAsync(string text)
#pragma warning restore VSTHRD100
#pragma warning restore VSSDK007
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Split on lines that are exactly "[WAIT]" (case-insensitive, trims CR)
            var allLines = text.Split('\n');
            var blocks = new List<string>();
            var current = new StringBuilder();

            foreach (var raw in allLines)
            {
                string line = raw.TrimEnd('\r');
                if (line.Trim().Equals("[WAIT]", StringComparison.OrdinalIgnoreCase))
                {
                    string block = current.ToString().Trim();
                    if (!string.IsNullOrEmpty(block))
                        blocks.Add(block);
                    current.Clear();
                }
                else
                {
                    current.AppendLine(line);
                }
            }
            // Capture any trailing block after the last [WAIT]
            {
                string trailing = current.ToString().Trim();
                if (!string.IsNullOrEmpty(trailing))
                    blocks.Add(trailing);
            }

            if (blocks.Count == 0)
            {
                InputTextBox.Text = "";
                return;
            }

            AppendOutput($"\n[BATCH] {blocks.Count} block(s) queued.\n", OutputColor.Dim);

            for (int i = 0; i < blocks.Count; i++)
            {
                string block = blocks[i];
                AppendOutput($"[BATCH] Block {i + 1}/{blocks.Count}\n", OutputColor.Dim);

                // Direct commands bypass LLM — execute immediately
                if (block.StartsWith("READ ", StringComparison.OrdinalIgnoreCase))
                {
                    AppendOutput($"[BATCH] Direct execute: {block.Split('\n')[0].Trim()}\n", OutputColor.Dim);
                    string readArg = block.Substring("READ ".Length).Trim();
                    await ApplyReadCommandAsync(readArg);
                }
                else if (block.StartsWith("SHELL:", StringComparison.OrdinalIgnoreCase))
                {
                    string cmd = block.Substring("SHELL:".Length).Trim();
                    AppendOutput($"[BATCH] Direct execute: SHELL: {cmd}\n", OutputColor.Dim);
                    var (output, exitCode) = await RunShellCommandCaptureAsync(cmd);
                    if (!string.IsNullOrEmpty(output))
                        AppendOutput(output, exitCode == 0 ? OutputColor.Normal : OutputColor.Error);
                }
                else if (block.StartsWith("PATCH ", StringComparison.OrdinalIgnoreCase) &&
                         block.IndexOf("FIND:", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         block.IndexOf("REPLACE:", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string patchFile = block.Split('\n')[0].Substring("PATCH ".Length).Trim();
                    AppendOutput($"[BATCH] Direct execute: PATCH {patchFile}\n", OutputColor.Dim);
                    await ApplyPatchAsync(block, clearInput: false);
                }
                else
                {
                    AppendOutput("[BATCH] Sending to LLM...\n", OutputColor.Dim);
                    var tcs = new TaskCompletionSource<bool>();
                    _batchOnComplete = () => tcs.TrySetResult(true);

                    InputTextBox.Text = block;
                    SendToLlm();

                    // Wait for onComplete (or onError) to fire the callback
                    await tcs.Task;
                }

                // Brief pause to let UI settle before next block
                await System.Threading.Tasks.Task.Delay(300);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            }

            AppendOutput("[BATCH] All blocks sent.\n", OutputColor.Dim);
            InputTextBox.Text = "";
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

                // Strip markdown section headings — models occasionally emit "## Fix 2:" etc.
                // inside FILE blocks; they are not valid C# and cause CS1024 build errors.
                var fileHeadingWarnings = new List<string>();
                code = StripMarkdownHeadingLines(code, fileHeadingWarnings);
                foreach (var w in fileHeadingWarnings)
                    AppendOutput($"[WARNING] Stripped markdown heading from FILE content: {w}\n", OutputColor.Error);

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
