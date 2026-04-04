// File: DevMindToolWindowControl.xaml.cs  v7.4
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
        private readonly ProfileManager _profileManager;
        private bool _suppressProfileChange;
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
        // Tracks filenames (filename-only, case-insensitive) that have been READ during the
        // current task. Cleared at the start of each new top-level user request.
        // Used by the unrelated-file write guard in AgenticHost.
        internal readonly HashSet<string> _taskReadFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Action _batchOnComplete;
        private bool _suppressDisplay;
        private bool _diffPreviewPending;
        private int _streamingTokenCount;
        private int _patchCount = 0;
        private int _undoCount = 0;
        private int _readFileCount = 0;
        private static string _cachedMSBuildPath;

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
            _profileManager = new ProfileManager();
            PopulateProfileComboBox();
            DevMindOptions.Saved += OnSettingsSaved;
            DevMindOptions.ProfileChanged += OnProfileChangedFromOptions;
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
            OutputBox.IsUndoEnabled = false;
            OutputBox.Document.Blocks.Clear();
            _spacerParagraph = new Paragraph { LineHeight = 1.0, Margin = new Thickness(0, 2000, 0, 0) };
            OutputBox.Document.Blocks.Add(_spacerParagraph);
            OutputBox.Document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
            OutputBox.IsUndoEnabled = true;
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
                    OutputColor.Warning  => new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x00)),
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

        // ── Profile switching ─────────────────────────────────────────────────

        private void PopulateProfileComboBox()
        {
            _suppressProfileChange = true;
            ProfileComboBox.Items.Clear();
            var profiles = _profileManager.GetAllProfiles();
            var active = _profileManager.GetActiveProfile();
            int selectedIndex = 0;
            for (int i = 0; i < profiles.Count; i++)
            {
                ProfileComboBox.Items.Add(profiles[i]);
                if (active != null && string.Equals(profiles[i].Id, active.Id, StringComparison.OrdinalIgnoreCase))
                    selectedIndex = i;
            }
            if (ProfileComboBox.Items.Count > 0)
                ProfileComboBox.SelectedIndex = selectedIndex;
            _suppressProfileChange = false;
        }

        private void ProfileComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressProfileChange) return;
            if (!(ProfileComboBox.SelectedItem is ProfileData profile)) return;

            var current = _profileManager.GetActiveProfile();
            if (current != null && string.Equals(current.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                _profileManager.ApplyProfile(profile);
                // ApplyProfile calls DevMindOptions.Save() which triggers OnSettingsSaved,
                // which in turn calls Configure + TestConnectionInBackground.
                AppendOutput($"\n[PROFILE] Switched to: {profile.Name}\n", OutputColor.Dim);
            }
            catch (Exception ex)
            {
                AppendOutput($"\n[PROFILE] Failed to switch profile: {ex.Message}\n", OutputColor.Error);
            }
        }

        private void OnProfileChangedFromOptions()
        {
#pragma warning disable VSTHRD001
            _ = Dispatcher.BeginInvoke(new Action(() =>
#pragma warning restore VSTHRD001
            {
                _profileManager.Reload();
                PopulateProfileComboBox();
            }));
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

            // Clear per-conversation file snapshots (for DIFF directive)
            ClearFileSnapshots();

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
            StopButton.IsEnabled = !enabled || _diffPreviewPending;
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
            System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] SendToLlm ENTER — _shellLoopPending={_shellLoopPending}, _agenticDepth={_agenticDepth}");
            string text = InputTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                System.Diagnostics.Debug.WriteLine("[DevMind TRACE] SendToLlm EXIT — text is null/empty");
                return;
            }
            System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] SendToLlm text={text.Substring(0, Math.Min(text.Length, 120))}...");

            // [WAIT] batch: if the input contains a line that is exactly "[WAIT]" (case-insensitive),
            // split into blocks and send each block sequentially, waiting for completion between them.
            if (!_shellLoopPending && ContainsWaitSeparator(text))
            {
                System.Diagnostics.Debug.WriteLine("[DevMind TRACE] SendToLlm EXIT — routed to ProcessBatchInputAsync");
                await ProcessBatchInputAsync(text);
                return;
            }

            // Reset agentic depth for user-initiated calls; preserve it for agentic re-triggers.
            // Increment turn counter on every call so tiered eviction and MicroCompact age tracking
            // see increasing turn numbers during the agentic loop.
            if (!_shellLoopPending)
            {
                _agenticDepth = 0;
            }
            _llmClient.IncrementTurn();

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

            if (text.StartsWith("GREP:", StringComparison.OrdinalIgnoreCase))
            {
                AppendOutput($"\n> {text}\n", OutputColor.Input);
                AppendNewLine();
                var grepMatch = System.Text.RegularExpressions.Regex.Match(
                    text,
                    @"^GREP:\s+""([^""]+)""\s+(\S+\.\S+?)(?::(\d+)(?:-(\d+))?)?\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (grepMatch.Success)
                {
                    string gPattern  = grepMatch.Groups[1].Value;
                    string gFile     = grepMatch.Groups[2].Value;
                    int?   gStart    = grepMatch.Groups[3].Success ? (int?)int.Parse(grepMatch.Groups[3].Value) : null;
                    int?   gEnd      = grepMatch.Groups[4].Success ? (int?)int.Parse(grepMatch.Groups[4].Value) : null;
                    string gResult   = await ((IAgenticHost)this).GrepFileAsync(gPattern, gFile, gStart, gEnd);
                    AppendOutput(gResult + "\n", OutputColor.Normal);
                }
                else
                {
                    AppendOutput("GREP syntax: GREP: \"pattern\" filename[:start-end]\n", OutputColor.Error);
                }
                InputTextBox.Text = "";
                SetInputEnabled(true);
                return;
            }

            if (text.StartsWith("FIND:", StringComparison.OrdinalIgnoreCase))
            {
                AppendOutput($"\n> {text}\n", OutputColor.Input);
                AppendNewLine();
                var findMatch = System.Text.RegularExpressions.Regex.Match(
                    text,
                    @"^FIND:\s+""([^""]+)""\s+(\S+)(?::(\d+)(?:-(\d+))?)?\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (findMatch.Success)
                {
                    string fPattern  = findMatch.Groups[1].Value;
                    string fGlob     = findMatch.Groups[2].Value;
                    int?   fStart    = findMatch.Groups[3].Success ? (int?)int.Parse(findMatch.Groups[3].Value) : null;
                    int?   fEnd      = findMatch.Groups[4].Success ? (int?)int.Parse(findMatch.Groups[4].Value) : null;
                    string fResult   = await ((IAgenticHost)this).FindInFilesAsync(fPattern, fGlob, fStart, fEnd);
                    AppendOutput(fResult + "\n", OutputColor.Normal);
                }
                else
                {
                    AppendOutput("FIND syntax: FIND: \"pattern\" glob[:start-end]  (e.g. FIND: \"foo\" *.cs)\n", OutputColor.Error);
                }
                InputTextBox.Text = "";
                SetInputEnabled(true);
                return;
            }

            if (text.StartsWith("DELETE ", StringComparison.OrdinalIgnoreCase) && !text.Contains('\n'))
            {
                AppendOutput($"\n> {text}\n", OutputColor.Input);
                AppendNewLine();
                string deleteTarget = text.Substring("DELETE ".Length).Trim();
                if (!string.IsNullOrEmpty(deleteTarget))
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dlgResult = System.Windows.MessageBox.Show(
                        $"Delete {deleteTarget}?\n\nThis cannot be undone.",
                        "DevMind — Confirm Delete",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning);
                    if (dlgResult == System.Windows.MessageBoxResult.Yes)
                    {
                        string delResult = await ((IAgenticHost)this).DeleteFileAsync(deleteTarget);
                        bool ok = delResult != null && delResult.StartsWith("Deleted:");
                        AppendOutput(delResult + "\n", ok ? OutputColor.Success : OutputColor.Error);
                    }
                    else
                    {
                        AppendOutput("Delete cancelled.\n", OutputColor.Dim);
                    }
                }
                else
                {
                    AppendOutput("DELETE syntax: DELETE filename.cs\n", OutputColor.Error);
                }
                InputTextBox.Text = "";
                SetInputEnabled(true);
                return;
            }

            if (text.StartsWith("RENAME ", StringComparison.OrdinalIgnoreCase) && !text.Contains('\n'))
            {
                AppendOutput($"\n> {text}\n", OutputColor.Input);
                AppendNewLine();
                string[] renameParts = text.Substring("RENAME ".Length).Trim().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (renameParts.Length == 2)
                {
                    string renameOld = renameParts[0];
                    string renameNew = renameParts[1];
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dlgResult = System.Windows.MessageBox.Show(
                        $"Rename {renameOld} → {renameNew}?",
                        "DevMind — Confirm Rename",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Question);
                    if (dlgResult == System.Windows.MessageBoxResult.Yes)
                    {
                        string renResult = await ((IAgenticHost)this).RenameFileAsync(renameOld, renameNew);
                        bool ok = renResult != null && renResult.StartsWith("Renamed:");
                        AppendOutput(renResult + "\n", ok ? OutputColor.Success : OutputColor.Error);
                    }
                    else
                    {
                        AppendOutput("Rename cancelled.\n", OutputColor.Dim);
                    }
                }
                else
                {
                    AppendOutput("RENAME syntax: RENAME OldFile.cs NewFile.cs\n", OutputColor.Error);
                }
                InputTextBox.Text = "";
                SetInputEnabled(true);
                return;
            }

            if (text.StartsWith("DIFF ", StringComparison.OrdinalIgnoreCase) && !text.Contains('\n'))
            {
                AppendOutput($"\n> {text}\n", OutputColor.Input);
                AppendNewLine();
                string diffTarget = text.Substring("DIFF ".Length).Trim();
                if (!string.IsNullOrEmpty(diffTarget))
                {
                    string diffResult = await ((IAgenticHost)this).GetFileDiffAsync(diffTarget);
                    AppendOutput(diffResult + "\n", OutputColor.Dim);
                }
                else
                {
                    AppendOutput("DIFF syntax: DIFF filename.cs\n", OutputColor.Error);
                }
                InputTextBox.Text = "";
                SetInputEnabled(true);
                return;
            }

            if (text.StartsWith("TEST ", StringComparison.OrdinalIgnoreCase) && !text.Contains('\n'))
            {
                AppendOutput($"\n> {text}\n", OutputColor.Input);
                AppendNewLine();
                string testArgs = text.Substring("TEST ".Length).Trim();
                if (!string.IsNullOrEmpty(testArgs))
                {
                    string[] testParts = testArgs.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    string testProject = testParts[0];
                    string testFilter  = testParts.Length > 1 ? testParts[1] : null;
                    string testResult  = await ((IAgenticHost)this).RunTestsAsync(testProject, testFilter);
                    bool testAllPassed = testResult != null && !testResult.Contains("FAILED:") && !testResult.Contains("failed,");
                    AppendOutput(testResult + "\n", testAllPassed ? OutputColor.Success : OutputColor.Error);
                }
                else
                {
                    AppendOutput("TEST syntax: TEST ProjectName.csproj [filter]\n", OutputColor.Error);
                }
                InputTextBox.Text = "";
                SetInputEnabled(true);
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

                System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] READ pre-processing: {readLineCount} READ line(s) found out of {allLines.Length} total lines");

                if (readLineCount > 0)
                {
                    string readBlock = string.Join("\n", allLines, 0, readLineCount);
                    System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] READ readBlock={readBlock.Substring(0, Math.Min(readBlock.Length, 120))}");
                    await ApplyReadCommandAsync(readBlock, showOutline: true);

                    string remaining = string.Join("\n", allLines, readLineCount, allLines.Length - readLineCount).Trim();

                    // Single-line fix: "READ File.cs do something" → extract instruction after the filename.
                    // ApplyReadCommandAsync already isolates the filename (first token after "READ "),
                    // but the trailing instruction on that same line was being discarded.
                    if (string.IsNullOrEmpty(remaining) && readLineCount == 1)
                    {
                        string readLine = allLines[0].TrimEnd('\r');
                        string afterRead = readLine.Substring("READ ".Length).Trim();
                        // afterRead = "File.cs do something" — skip first token (filename) to get instruction
                        int firstSpace = afterRead.IndexOf(' ');
                        if (firstSpace > 0)
                            remaining = afterRead.Substring(firstSpace + 1).Trim();
                    }

                    System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] READ remaining={remaining.Length} chars: '{remaining.Substring(0, Math.Min(remaining.Length, 120))}'");
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
            {
                _pendingResubmitPrompt = text;
                // Preserve files registered by pre-processed READ lines above —
                // Clear() would wipe them, causing false positives in the write guard.
                var preReadFiles = _taskReadFiles.Count > 0
                    ? new HashSet<string>(_taskReadFiles, StringComparer.OrdinalIgnoreCase)
                    : null;
                _taskReadFiles.Clear();
                if (preReadFiles != null)
                    _taskReadFiles.UnionWith(preReadFiles);
            }

            System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] Post-READ: text='{text.Substring(0, Math.Min(text.Length, 120))}', _pendingResubmitPrompt set={_pendingResubmitPrompt != null}");

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

            // Lazy-load project context once per session (DevMind.md / AGENTS.md / CLAUDE.md)
            if (_devMindContext == null)
            {
                string loaded = await LoadDevMindContextAsync();
                if (loaded != null)
                {
                    _devMindContext = loaded;
                    // Append loaded agent profile if one was selected
                    if (!string.IsNullOrEmpty(_agentProfileContent))
                        _devMindContext += $"\n\n[AGENT PROFILE: {_loadedAgentProfile}]\n{_agentProfileContent}";
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
            _streamingTokenCount = 0;

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
            //
            // During agentic resubmits (_shellLoopPending), skip reconstruction entirely.
            // The combined system prompt was built on the first iteration and is still set
            // in DevMindOptions.Instance.SystemPrompt (restored by the finally block of the
            // previous iteration, then unchanged). Rebuilding from DTE on subsequent
            // iterations risks wobble (null active doc, changed tabs) that would alter the
            // system prompt string and invalidate the server's entire KV cache from position 0.
            string originalSystemPrompt = _shellLoopPending ? null : DevMindOptions.Instance.SystemPrompt;

            // Hoist buildCommand so it's accessible in the onComplete lambda for ToolCallMapper
            // VSIX projects fail with dotnet build — detect via .vsixmanifest sibling and use MSBuild instead
            string buildCommand;
            if (activeProjectPath != null)
            {
                var projectDir = System.IO.Path.GetDirectoryName(activeProjectPath);
                bool isVsix = projectDir != null &&
                    (System.IO.File.Exists(System.IO.Path.Combine(projectDir, "source.extension.vsixmanifest")) ||
                     System.IO.File.Exists(System.IO.Path.Combine(projectDir, "extension.vsixmanifest")));
                var msbuildPath = FindMSBuildPath();
                var msbuildInvoke = msbuildPath.Contains(" ") ? $"& \"{msbuildPath}\"" : msbuildPath;
                buildCommand = isVsix
                    ? $"{msbuildInvoke} \"{activeProjectPath}\" /p:DeployExtension=false /p:Configuration=Release /verbosity:minimal"
                    : $"dotnet build \"{activeProjectPath}\" /p:Configuration=Release";
            }
            else
            {
                var msbuildPath = FindMSBuildPath();
                var msbuildInvoke = msbuildPath.Contains(" ") ? $"& \"{msbuildPath}\"" : msbuildPath;
                buildCommand = $"{msbuildInvoke} \"C:\\Users\\pkailas.KAILAS\\source\\repos\\DevMind\\DevMind.slnx\" /p:DeployExtension=false /p:Configuration=Release /verbosity:minimal";
            }

            if (!_shellLoopPending)
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

                // ── Behavioral rules (included regardless of directive mode) ──
                string behavioralRules = BuildBehavioralPrompt(buildCommand, projectNamespace);

                string llmDirective;
                var directiveMode = DevMindOptions.Instance.DirectiveMode;
                if (directiveMode == DirectiveMode.TextDirective)
                {
                    // Full text directives — syntax descriptions + behavioral rules
                    llmDirective = BuildTextDirectivePrompt(buildCommand) + "\n\n" + behavioralRules;
                }
                else
                {
                    // ToolUse or Auto — tools are in the request body, system prompt has behavioral rules only
                    llmDirective = behavioralRules;
                }

                string combined = $"{originalSystemPrompt}\n\n{llmDirective}";
                if (!string.IsNullOrEmpty(_devMindContext))
                    combined += $"\n\n--- Project Context (DevMind.md) ---\n{_devMindContext}\n---";

                // Load MEMORY.md cross-session memory index
                try
                {
                    var memMgr = new MemoryManager(_terminalWorkingDir);
                    string memoryIndex = memMgr.LoadIndex();
                    if (!string.IsNullOrWhiteSpace(memoryIndex))
                        combined += $"\n\n--- Session Memory (MEMORY.md) ---\n{memoryIndex}\n---";
                }
                catch { /* Memory loading is best-effort */ }

                DevMindOptions.Instance.SystemPrompt = combined;
            }

            System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] About to call SendMessageAsync — contextualMessage length={contextualMessage.Length}");

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

                                _streamingTokenCount++;

                                if (_suppressDisplay) return;

                                if (_thinkingTimer != null)
                                {
                                    _thinkingTimer.Stop();
                                    _thinkingTimer = null;
                                }
                                StatusText.Text = $"Generating... ({_streamingTokenCount} tokens)";
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

                                ResponseOutcome outcome;
                                var lastToolCalls = _llmClient.LastToolCalls;
                                if (DevMindOptions.Instance.ShowDebugOutput)
                                    AppendOutput($"[DIAG] LastToolCalls: {lastToolCalls?.Count ?? 0}\n", OutputColor.Dim);
                                if (lastToolCalls != null && lastToolCalls.Count > 0)
                                {
                                    // Tool use path — map tool calls to response blocks
                                    var toolBlocks = ToolCallMapper.Map(lastToolCalls, buildCommand);

                                    if (DevMindOptions.Instance.ShowDebugOutput)
                                    {
                                        foreach (var tb in toolBlocks)
                                            AppendOutput($"[DIAG] Block: Type={tb.Type}, FileName={tb.FileName}, Command={tb.Command}, MemoryTopic={tb.MemoryTopic}\n", OutputColor.Dim);
                                    }

                                    // Also parse text content for any prose alongside tool_calls
                                    if (!string.IsNullOrWhiteSpace(fullResponse))
                                    {
                                        var textBlocks = ResponseParser.Parse(fullResponse);
                                        // Prepend text blocks (prose) before tool blocks
                                        foreach (var tb in textBlocks)
                                        {
                                            if (tb.Type == BlockType.Text)
                                                toolBlocks.Insert(0, tb);
                                        }
                                    }

                                    outcome = ResponseClassifier.Classify(toolBlocks);
                                    if (DevMindOptions.Instance.ShowDebugOutput)
                                        AppendOutput($"[DIAG] Tool use path: {lastToolCalls.Count} call(s) mapped to {toolBlocks.Count} block(s)\n", OutputColor.Dim);
                                    System.Diagnostics.Debug.WriteLine($"[DEVMIND-DIAG] Tool use path: {lastToolCalls.Count} tool call(s) → {toolBlocks.Count} block(s)");
                                }
                                else
                                {
                                    // Text directive fallback
                                    if (DevMindOptions.Instance.ShowDebugOutput)
                                        AppendOutput("[DIAG] Text directive fallback path\n", OutputColor.Dim);
                                    outcome = ResponseClassifier.Classify(fullResponse);
                                }

                                var executor = new AgenticExecutor(this);
                                executor.SetCancellationToken(_cts?.Token ?? CancellationToken.None);
                                int maxDepth = DevMindOptions.Instance.AgenticLoopMaxDepth;

                                if (DevMindOptions.Instance.ShowDebugOutput)
                                    AppendOutput($"[DIAG] Outcome: HasPatches={outcome.HasPatches}, HasShell={outcome.HasShellCommands}, HasRead={outcome.HasReadRequests}, IsDone={outcome.IsDone}, IsReadOnly={outcome.IsReadOnly}\n", OutputColor.Dim);
                                System.Diagnostics.Debug.WriteLine($"[DEVMIND-DIAG] Outcome: HasPatches={outcome.HasPatches} HasShell={outcome.HasShellCommands} HasFile={outcome.HasFileCreation} HasDelete={outcome.HasDeleteRequests} IsDone={outcome.IsDone} IsReadOnly={outcome.IsReadOnly} IsEmptyOrBareCode={outcome.IsEmptyOrBareCode}");

                                // ── Branch: Tool Use simplified loop vs TextDirective resolver-driven loop ──
                                bool useToolUseLoop = lastToolCalls != null && lastToolCalls.Count > 0
                                    && DevMindOptions.Instance.DirectiveMode != DirectiveMode.TextDirective;

                                if (useToolUseLoop)
                                {
                                    // ══════════════════════════════════════════════════════════════
                                    // Tool Use loop: model decides, we execute
                                    // The model called tools — execute them all, inject results,
                                    // resubmit so the model can decide what's next.
                                    // ══════════════════════════════════════════════════════════════

                                    var action = new AgenticAction { Type = ActionType.ApplyAndBuild };
                                    ExecutionResult result = await executor.ExecuteAsync(action, outcome);

                                    // Inject per-tool result messages so the model sees what happened
                                    InjectToolResultMessages(lastToolCalls, result, outcome.Blocks);

                                    // Check for explicit DONE signal (task_done tool call)
                                    if (outcome.IsDone)
                                    {
                                        AppendOutput("[AGENTIC] Task complete.\n", OutputColor.Success);
                                        _agenticDepth = 0;
                                        _pendingResubmitPrompt = null;
                                        // fall through to completion
                                    }
                                    // Check for run/exec command — treat as task complete
                                    else if (result.ShellExitCode.HasValue && result.ShellExitCode == 0
                                        && !string.IsNullOrEmpty(result.LastShellCommand)
                                        && IsRunOrExecCommand(result.LastShellCommand))
                                    {
                                        AppendOutput("[AGENTIC] Run/exec command succeeded — treating as task complete.\n", OutputColor.Success);
                                        _agenticDepth = 0;
                                        _pendingResubmitPrompt = null;
                                        // fall through to completion
                                    }
                                    // Check depth cap
                                    else if (maxDepth > 0 && _agenticDepth >= maxDepth)
                                    {
                                        if (result.ShellExitCode.HasValue && result.ShellExitCode != 0)
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
                                        // ── Re-trigger: feed results back, let model decide next ──

                                        _agenticDepth++;
                                        {
                                            int agCtx = _llmClient.ServerContextSize > 0 ? _llmClient.ServerContextSize : _llmClient.MaxPromptTokens;
                                            int agUsed = _llmClient.LastContextUsed > 0 ? _llmClient.LastContextUsed : _llmClient.EstimateHistoryTokens();
                                            int agPct = agCtx > 0 ? (int)(agUsed * 100.0 / agCtx) : 0;
                                            string iterLabel = maxDepth > 0
                                                ? $"Iteration {_agenticDepth}/{maxDepth}"
                                                : $"Iteration {_agenticDepth}";
                                            AppendOutput($"[AGENTIC] {iterLabel} — {agUsed:N0} / {agCtx:N0} ({agPct}%)\n", OutputColor.Dim);
                                        }

                                        // Tool results are already in conversation as tool role messages.
                                        // Simple continuation prompt — the model has all the context it needs.
                                        InputTextBox.Text = "Continue with the task.";
                                        _pendingShellContext = null;

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
                                }
                                else if (!useToolUseLoop
                                    && (lastToolCalls == null || lastToolCalls.Count == 0)
                                    && DevMindOptions.Instance.DirectiveMode != DirectiveMode.TextDirective)
                                {
                                    // ══════════════════════════════════════════════════════════════
                                    // No tool calls in non-TextDirective mode — model is done.
                                    // ══════════════════════════════════════════════════════════════

                                    // Check for text-based DONE signal as safety net
                                    if (outcome.IsDone)
                                        AppendOutput("Task complete.\n", OutputColor.Success);

                                    // Stop — model chose not to call any tools
                                    _agenticDepth = 0;
                                    _pendingResubmitPrompt = null;
                                    if (DevMindOptions.Instance.ShowDebugOutput)
                                        AppendOutput("[DIAG] Tool use loop: no tool calls — stopping.\n", OutputColor.Dim);
                                    // fall through to completion
                                }
                                else
                                {
                                    // ══════════════════════════════════════════════════════════════
                                    // TextDirective mode — resolver-driven loop (unchanged)
                                    // ══════════════════════════════════════════════════════════════

                                    AgenticAction action = AgenticActionResolver.Resolve(outcome, null, _agenticDepth, maxDepth);

                                    if (action.Type != ActionType.Stop)
                                    {
                                        ExecutionResult result = await executor.ExecuteAsync(action, outcome);

                                        // ── Inject tool result messages for multi-turn tool use ──
                                        if (lastToolCalls != null && lastToolCalls.Count > 0)
                                        {
                                            InjectToolResultMessages(lastToolCalls, result, outcome.Blocks);
                                        }

                                        // ── Resubmit paths (return immediately; SendToLlm manages its own completion) ──

                                        if (action.Type == ActionType.LoadAndResubmit)
                                        {
                                            // DONE + info-gather: load files but then stop
                                            if (outcome.IsDone)
                                            {
                                                AppendOutput("[AGENTIC] File(s) loaded. Task complete.\n", OutputColor.Success);
                                                _agenticDepth = 0;
                                                _pendingResubmitPrompt = null;
                                                // fall through to completion
                                            }
                                            else if (_agenticDepth > 0)
                                            {
                                                AppendOutput("[AUTO-READ] File(s) loaded during agentic iteration — continuing...\n", OutputColor.Dim);
                                                InputTextBox.Text = "The requested file(s) have been loaded into context. Continue with the task using PATCH/SHELL/FILE directives.";
                                                _shellLoopPending = true;
                                                SendToLlm();
                                                return;
                                            }
                                            else if (!string.IsNullOrEmpty(_pendingResubmitPrompt))
                                            {
                                                string saved = _pendingResubmitPrompt;
                                                _pendingResubmitPrompt = null;
                                                AppendOutput("[AUTO-READ] File(s) loaded — resubmitting original prompt...\n", OutputColor.Dim);
                                                InputTextBox.Text = saved;
                                                _shellLoopPending = true;
                                                SendToLlm();
                                                return;
                                            }
                                            else
                                            {
                                                AppendOutput("[AUTO-READ] File(s) loaded — resubmitting...\n", OutputColor.Dim);
                                                InputTextBox.Text = "The requested file(s) have been loaded. Continue with the task.";
                                                _shellLoopPending = true;
                                                SendToLlm();
                                                return;
                                            }
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

                                            if (hasDone)
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
                                                    int agCtx = _llmClient.ServerContextSize > 0 ? _llmClient.ServerContextSize : _llmClient.MaxPromptTokens;
                                                    int agUsed = _llmClient.LastContextUsed > 0 ? _llmClient.LastContextUsed : _llmClient.EstimateHistoryTokens();
                                                    int agPct = agCtx > 0 ? (int)(agUsed * 100.0 / agCtx) : 0;
                                                    string iterLabel = maxDepth > 0
                                                        ? $"Iteration {_agenticDepth}/{maxDepth}"
                                                        : $"Iteration {_agenticDepth}";
                                                    AppendOutput($"[AGENTIC] {iterLabel} — {agUsed:N0} / {agCtx:N0} ({agPct}%)\n", OutputColor.Dim);
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

                                                    if (result.FilesDeleted.Count > 0)
                                                        agenticContext.AppendLine($"[FILE DELETED] Deleted from disk: {string.Join(", ", result.FilesDeleted.ConvertAll(System.IO.Path.GetFileName))}.");

                                                    if (result.FilesRenamed.Count > 0)
                                                        agenticContext.AppendLine($"[FILE RENAMED] {string.Join("; ", result.FilesRenamed)}.");

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
                                }

                                // ── Completion ────────────────────────────────────────────────

                                if (DevMindOptions.Instance.ShowContextBudget)
                                {
                                    int cbCtx  = _llmClient.ServerContextSize > 0 ? _llmClient.ServerContextSize : _llmClient.MaxPromptTokens;
                                    int cbUsed = _llmClient.LastContextUsed > 0 ? _llmClient.LastContextUsed : _llmClient.EstimateHistoryTokens();
                                    int cbPct  = cbCtx > 0 ? (int)(cbUsed * 100.0 / cbCtx) : 0;
                                    OutputColor cbColor = cbPct < 60 ? OutputColor.Dim
                                                        : cbPct < 80 ? OutputColor.Normal
                                                        : OutputColor.Error;
                                    AppendOutput($"[CONTEXT] {cbUsed:N0} / {cbCtx:N0} ({cbPct}%)\n", cbColor);
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
                    // Do NOT reset _agenticDepth or SetInputEnabled here.
                    // The onComplete handler is dispatched via Dispatcher.BeginInvoke and
                    // may not have executed yet — resetting depth here causes a race where
                    // the counter resets to 0 on every iteration, preventing depth cap from
                    // ever being reached. All terminal paths in onComplete already reset
                    // _agenticDepth = 0 and call SetInputEnabled(true).
                    // _shellLoopPending is safe to clear unconditionally — it's a no-op
                    // when onComplete hasn't run yet (still false), and a cleanup when it has.
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
                // originalSystemPrompt is null during agentic resubmits — skip restore.
                if (originalSystemPrompt != null)
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
                else if (block.StartsWith("GREP:", StringComparison.OrdinalIgnoreCase))
                {
                    AppendOutput($"[BATCH] Direct execute: {block.Split('\n')[0].Trim()}\n", OutputColor.Dim);
                    var grepMatch = System.Text.RegularExpressions.Regex.Match(
                        block,
                        @"^GREP:\s+""([^""]+)""\s+(\S+\.\S+?)(?::(\d+)(?:-(\d+))?)?\s*$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (grepMatch.Success)
                    {
                        string gPattern = grepMatch.Groups[1].Value;
                        string gFile    = grepMatch.Groups[2].Value;
                        int?   gStart   = grepMatch.Groups[3].Success ? (int?)int.Parse(grepMatch.Groups[3].Value) : null;
                        int?   gEnd     = grepMatch.Groups[4].Success ? (int?)int.Parse(grepMatch.Groups[4].Value) : null;
                        string gResult  = await ((IAgenticHost)this).GrepFileAsync(gPattern, gFile, gStart, gEnd);
                        AppendOutput(gResult + "\n", OutputColor.Normal);
                    }
                    else
                    {
                        AppendOutput("GREP syntax: GREP: \"pattern\" filename[:start-end]\n", OutputColor.Error);
                    }
                }
                else if (block.StartsWith("FIND:", StringComparison.OrdinalIgnoreCase))
                {
                    AppendOutput($"[BATCH] Direct execute: {block.Split('\n')[0].Trim()}\n", OutputColor.Dim);
                    var findMatch = System.Text.RegularExpressions.Regex.Match(
                        block,
                        @"^FIND:\s+""([^""]+)""\s+(\S+)(?::(\d+)(?:-(\d+))?)?\s*$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (findMatch.Success)
                    {
                        string fPattern = findMatch.Groups[1].Value;
                        string fGlob    = findMatch.Groups[2].Value;
                        int?   fStart   = findMatch.Groups[3].Success ? (int?)int.Parse(findMatch.Groups[3].Value) : null;
                        int?   fEnd     = findMatch.Groups[4].Success ? (int?)int.Parse(findMatch.Groups[4].Value) : null;
                        string fResult  = await ((IAgenticHost)this).FindInFilesAsync(fPattern, fGlob, fStart, fEnd);
                        AppendOutput(fResult + "\n", OutputColor.Normal);
                    }
                    else
                    {
                        AppendOutput("FIND syntax: FIND: \"pattern\" glob[:start-end]  (e.g. FIND: \"foo\" *.cs)\n", OutputColor.Error);
                    }
                }
                else if (block.StartsWith("DELETE ", StringComparison.OrdinalIgnoreCase) && !block.Contains('\n'))
                {
                    AppendOutput($"[BATCH] Direct execute: {block.Trim()}\n", OutputColor.Dim);
                    string batchDeleteFile = block.Substring("DELETE ".Length).Trim();
                    if (!string.IsNullOrEmpty(batchDeleteFile))
                    {
                        string batchDelResult = await ((IAgenticHost)this).DeleteFileAsync(batchDeleteFile);
                        bool batchOk = batchDelResult != null && batchDelResult.StartsWith("Deleted:");
                        AppendOutput(batchDelResult + "\n", batchOk ? OutputColor.Success : OutputColor.Error);
                    }
                    else
                    {
                        AppendOutput("DELETE syntax: DELETE filename.cs\n", OutputColor.Error);
                    }
                }
                else if (block.StartsWith("RENAME ", StringComparison.OrdinalIgnoreCase) && !block.Contains('\n'))
                {
                    AppendOutput($"[BATCH] Direct execute: {block.Trim()}\n", OutputColor.Dim);
                    string[] batchRenameParts = block.Substring("RENAME ".Length).Trim().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (batchRenameParts.Length == 2)
                    {
                        string batchRenResult = await ((IAgenticHost)this).RenameFileAsync(batchRenameParts[0], batchRenameParts[1]);
                        bool batchRenOk = batchRenResult != null && batchRenResult.StartsWith("Renamed:");
                        AppendOutput(batchRenResult + "\n", batchRenOk ? OutputColor.Success : OutputColor.Error);
                    }
                    else
                    {
                        AppendOutput("RENAME syntax: RENAME OldFile.cs NewFile.cs\n", OutputColor.Error);
                    }
                }
                else if (block.StartsWith("DIFF ", StringComparison.OrdinalIgnoreCase) && !block.Contains('\n'))
                {
                    AppendOutput($"[BATCH] Direct execute: {block.Trim()}\n", OutputColor.Dim);
                    string batchDiffFile = block.Substring("DIFF ".Length).Trim();
                    if (!string.IsNullOrEmpty(batchDiffFile))
                    {
                        string batchDiffResult = await ((IAgenticHost)this).GetFileDiffAsync(batchDiffFile);
                        AppendOutput(batchDiffResult + "\n", OutputColor.Dim);
                    }
                    else
                    {
                        AppendOutput("DIFF syntax: DIFF filename.cs\n", OutputColor.Error);
                    }
                }
                else if (block.StartsWith("TEST ", StringComparison.OrdinalIgnoreCase) && !block.Contains('\n'))
                {
                    AppendOutput($"[BATCH] Direct execute: {block.Trim()}\n", OutputColor.Dim);
                    string batchTestArgs = block.Substring("TEST ".Length).Trim();
                    if (!string.IsNullOrEmpty(batchTestArgs))
                    {
                        string[] batchTestParts = batchTestArgs.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        string batchTestProject = batchTestParts[0];
                        string batchTestFilter  = batchTestParts.Length > 1 ? batchTestParts[1] : null;
                        string batchTestResult  = await ((IAgenticHost)this).RunTestsAsync(batchTestProject, batchTestFilter);
                        bool batchTestPassed    = batchTestResult != null && !batchTestResult.Contains("FAILED:") && !batchTestResult.Contains("failed,");
                        AppendOutput(batchTestResult + "\n", batchTestPassed ? OutputColor.Success : OutputColor.Error);
                    }
                    else
                    {
                        AppendOutput("TEST syntax: TEST ProjectName.csproj [filter]\n", OutputColor.Error);
                    }
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

        // ── Tool Result Injection ────────────────────────────────────────────

        /// <summary>
        /// After the executor processes tool calls, injects tool result messages into
        /// conversation history so the model sees the outcome on the next turn.
        /// </summary>
        private void InjectToolResultMessages(List<ToolCallResult> toolCalls, ExecutionResult result,
            List<ResponseBlock> executedBlocks)
        {
            foreach (var tc in toolCalls)
            {
                string resultContent = BuildToolResultContent(tc, result, executedBlocks);
                _llmClient.AddToolResultMessage(tc.Id, resultContent);
            }
        }

        /// <summary>
        /// Builds the result content string for a single tool call based on the execution result.
        /// </summary>
        private static string BuildToolResultContent(ToolCallResult tc, ExecutionResult result,
            List<ResponseBlock> executedBlocks)
        {
            switch (tc.Name)
            {
                case "read_file":
                case "grep_file":
                case "find_in_files":
                case "diff_file":
                    // Read-like operations — content was injected into context by the executor.
                    // The model sees it via the next user message's context injection.
                    return "[File content loaded into context]";

                case "patch_file":
                    if (result.PatchedPaths != null && result.PatchedPaths.Count > 0)
                        return $"[PATCH applied to {string.Join(", ", result.PatchedPaths)}]";
                    if (result.Errors != null && result.Errors.Count > 0)
                        return $"[PATCH-FAILED: {string.Join("; ", result.Errors)}]";
                    return "[PATCH processed]";

                case "create_file":
                    if (result.FilesCreated != null && result.FilesCreated.Count > 0)
                        return $"[File created: {string.Join(", ", result.FilesCreated)}]";
                    return "[File created]";

                case "append_file":
                    if (result.FilesAppended != null && result.FilesAppended.Count > 0)
                        return $"[Content appended to {string.Join(", ", result.FilesAppended)}]";
                    return "[Content appended]";

                case "run_shell":
                case "run_build":
                    if (!string.IsNullOrEmpty(result.ShellOutput))
                        return result.ShellOutput;
                    return result.ShellExitCode.HasValue
                        ? $"[Shell exited with code {result.ShellExitCode}]"
                        : "[Shell command executed]";

                case "run_tests":
                    return !string.IsNullOrEmpty(result.ShellOutput)
                        ? result.ShellOutput
                        : "[Tests executed]";

                case "delete_file":
                    if (result.FilesDeleted != null && result.FilesDeleted.Count > 0)
                        return $"[Deleted: {string.Join(", ", result.FilesDeleted)}]";
                    return "[File deleted]";

                case "rename_file":
                    if (result.FilesRenamed != null && result.FilesRenamed.Count > 0)
                        return $"[Renamed: {string.Join(", ", result.FilesRenamed)}]";
                    return "[File renamed]";

                case "scratchpad":
                    return "[Scratchpad updated]";

                case "task_done":
                    return "[Task complete]";

                case "recall_memory":
                    {
                        var memBlock = executedBlocks?.FirstOrDefault(b => b.Type == BlockType.RecallMemory);
                        return memBlock?.MemoryContent ?? "[Topic not found]";
                    }

                case "save_memory":
                    {
                        var memBlock = executedBlocks?.FirstOrDefault(b => b.Type == BlockType.SaveMemory);
                        return memBlock?.MemoryDescription ?? "[Memory saved]";
                    }

                case "list_memory_topics":
                    {
                        var memBlock = executedBlocks?.FirstOrDefault(b => b.Type == BlockType.ListMemory);
                        return memBlock?.MemoryContent ?? "[No memory topics found]";
                    }

                default:
                    return "[Executed]";
            }
        }

        // ── System Prompt Builders ──────────────────────────────────────────

        /// <summary>
        /// Builds the text directive syntax descriptions for TextDirective mode.
        /// Contains the FILE:, PATCH, SHELL:, READ, GREP:, FIND:, DELETE, RENAME, DIFF, TEST syntax.
        /// </summary>
        private static string BuildTextDirectivePrompt(string buildCommand)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("## Directives\n");
            sb.Append("FILE: <filename>\n<raw source>\nEND_FILE\n\n");
            sb.Append("PATCH <filename>\nFIND:\n<exact text>\nREPLACE:\n<replacement>\nEND_PATCH\n");
            sb.Append("Multiple FIND/REPLACE pairs are allowed before END_PATCH. END_PATCH is required and must appear on its own line after the last REPLACE block.\n\n");
            sb.Append("SHELL: <command>\n\n");
            sb.Append("READ <filename>  — full if <100 lines, outline otherwise\n");
            sb.Append("READ <filename>:<start>-<end>  — targeted line range (1-based)\n");
            sb.Append("READ! <filename>  — force full content (expensive)\n");
            sb.Append("READ git log [N]  — show recent commit history (default 10, max 50)\n");
            sb.Append("READ git diff [ref]  — show working changes, staged changes, or diff against a commit\n");
            sb.Append("  Examples: READ git diff, READ git diff --staged, READ git diff HEAD~1, READ git diff filename.cs\n\n");
            sb.Append("### GREP — Search File for Pattern\n");
            sb.Append("GREP: \"pattern\" filename\n");
            sb.Append("GREP: \"pattern\" filename:100-200\n\n");
            sb.Append("Searches for lines containing the pattern (case-insensitive substring match) in a single file.\n");
            sb.Append("Returns matching lines with line numbers. Use GREP to locate code before doing a targeted READ.\n\n");
            sb.Append("Example workflow:\n");
            sb.Append("1. GREP: \"SaveFileAsync\" AgenticExecutor.cs     → finds lines 42, 89, 155\n");
            sb.Append("2. READ AgenticExecutor.cs:85-100               → reads context around line 89\n");
            sb.Append("3. PATCH AgenticExecutor.cs                      → applies the change\n\n");
            sb.Append("Rules:\n");
            sb.Append("- Pattern must be in double quotes.\n");
            sb.Append("- Results are capped at 50 matches. If truncated, narrow your pattern or add a line range.\n");
            sb.Append("- GREP does not modify files. It is information-gathering only.\n");
            sb.Append("- Prefer GREP + targeted READ over sequential full-file READs.\n\n");
            sb.Append("### FIND — Cross-File Search\n");
            sb.Append("FIND: \"pattern\" *.cs\n");
            sb.Append("FIND: \"pattern\" Services/*.cs\n\n");
            sb.Append("Searches all files matching the glob for lines containing the pattern (case-insensitive substring match).\n");
            sb.Append("Returns filename:line: content for each match, capped at 100 results across all files.\n\n");
            sb.Append("Use FIND when you need to know where something is used across the project.\n");
            sb.Append("Use GREP when you already know which file to search.\n\n");
            sb.Append("### DELETE — Remove File\n");
            sb.Append("DELETE filename.cs\n\n");
            sb.Append("Deletes a file from disk. Use only when explicitly asked to remove a file.\n");
            sb.Append("Do not use DELETE speculatively — only when the task requires file removal.\n\n");
            sb.Append("### RENAME — Rename/Move File\n");
            sb.Append("RENAME OldFile.cs NewFile.cs\n\n");
            sb.Append("Renames a file on disk. The old file is closed in the editor and the new file is opened.\n");
            sb.Append("Does not update references in other files — use FIND + PATCH to update imports or usings if needed.\n\n");
            sb.Append("### DIFF — Show File Changes\n");
            sb.Append("DIFF filename.cs\n\n");
            sb.Append("Shows all changes made to a file during this conversation as a unified-style diff.\n");
            sb.Append("Use DIFF to review cumulative modifications before confirming task completion.\n");
            sb.Append("Use DIFF after multiple PATCHes to verify the overall result is correct.\n");
            sb.Append("DIFF is information-gathering only — it does not modify files.\n\n");
            sb.Append("### TEST — Run Tests\n");
            sb.Append("TEST ProjectName.csproj\n");
            sb.Append("TEST ProjectName.csproj ClassName.MethodName\n");
            sb.Append("TEST ProjectName.csproj --filter \"FullyQualifiedName~SomeTest\"\n\n");
            sb.Append("Runs dotnet test and returns structured pass/fail results.\n");
            sb.Append("Output is compact — only failed tests show details. Much cheaper than SHELL: dotnet test.\n");
            sb.Append("Use TEST after making changes to verify correctness.\n");
            sb.Append("If tests fail, fix the code with PATCH and TEST again.\n\n");
            sb.Append("## Build Verification\n");
            sb.Append($"After ANY code change emit: SHELL: {buildCommand}");
            return sb.ToString();
        }

        /// <summary>
        /// Builds the behavioral rules that apply regardless of directive mode.
        /// Includes: After PATCH, Before PATCH, Large File Strategy, Core rules,
        /// Task Completion, Scratchpad, Block-by-Block, and namespace guidance.
        /// </summary>
        private static string BuildBehavioralPrompt(string buildCommand, string projectNamespace)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("## Build Verification\n");
            sb.Append($"Build command: {buildCommand}\n");
            sb.Append("After ANY code change, run the build to verify it still passes.\n\n");
            sb.Append("## After PATCH\n");
            sb.Append("You receive [PATCH-RESULT:filename] with ±3 lines of context and >>> CHANGED:/>>> ADDED: markers.\n");
            sb.Append("Full file is cached — use READ filename:start-end for more context.\n\n");
            sb.Append("## Before PATCH\n");
            sb.Append("Never output raw code blocks. Use FILE: for new files, PATCH for edits.\n");
            sb.Append("READ the file first if you have not seen it. You may combine FILE, PATCH, SHELL in one response.\n");
            sb.Append("FIND text must be copied verbatim from READ output — never reconstructed from memory.\n");
            sb.Append("Do not read the same file multiple times. If you have an outline and a line range, that is sufficient context to write a PATCH. Act immediately.\n\n");
            sb.Append("## Large File Strategy\n");
            sb.Append("For files over 100 lines:\n");
            sb.Append("1. First READ gets an outline (types, methods, signatures with line numbers).\n");
            sb.Append("2. Use the outline to identify the exact line range you need.\n");
            sb.Append("3. READ filename:start-end for just that section.\n");
            sb.Append("4. PATCH using only the content from that range.\n");
            sb.Append("Never READ! an entire large file unless explicitly asked. Work from outline → range → patch.\n\n");
            sb.Append("## Core rules\n");
            sb.Append("After a READ is loaded, act on it immediately in the same response. Never emit only READ directives and stop. Every response must include at least one PATCH, FILE, or SHELL directive unless you are responding to a question.\n\n");
            sb.Append("## Task Completion\n");
            sb.Append("When all steps of the task are complete and nothing remains to do, emit:\nDONE\n");
            sb.Append("Only emit DONE when the task is truly finished. Do not emit DONE mid-task.\n\n");
            sb.Append("## Scratchpad\n");
            sb.Append("Emit a SCRATCHPAD: block (end with END_SCRATCHPAD on its own line) to track state across turns:\n");
            sb.Append("SCRATCHPAD:\nGoal: <task>\nFiles: <file> (lines N-M)\nStatus: <PLANNING|PATCHING|BUILDING|DONE>\nLast: <action>\nNext: <step>\nEND_SCRATCHPAD");

            if (DevMindOptions.Instance.BlockByBlockMode != BlockByBlockModeType.Off)
            {
                sb.Append("\n\n## Block-by-Block Mode (Active)\n");
                sb.Append("You are operating in block-by-block mode for memory-constrained environments.\n");
                sb.Append("Rules:\n");
                sb.Append("1. Start each task by READing the file outline only — do not request full content.\n");
                sb.Append("2. Each turn: READ one range, emit one PATCH, update SCRATCHPAD with remaining steps.\n");
                sb.Append("3. Do not attempt multiple file sections in a single response.\n");
                sb.Append("4. After each PATCH, mark that step done in SCRATCHPAD before continuing.\n");
                sb.Append("5. If more steps remain, state the next step clearly and wait for the next turn.\n");
                sb.Append("Work incrementally: outline → one range → one patch → repeat until done.");
            }

            if (!string.IsNullOrEmpty(projectNamespace))
                sb.Append($"\n- When creating new files, use the namespace '{projectNamespace}'.");

            return sb.ToString();
        }

        /// <summary>
        /// Discovers MSBuild.exe at runtime. Checks VSINSTALLDIR env var, then vswhere.exe,
        /// then known VS installation directories. Caches the result for the session.
        /// </summary>
        private static string FindMSBuildPath()
        {
            if (_cachedMSBuildPath != null)
                return _cachedMSBuildPath;

            // 1. Check VSINSTALLDIR environment variable
            var vsInstallDir = Environment.GetEnvironmentVariable("VSINSTALLDIR");
            if (!string.IsNullOrEmpty(vsInstallDir))
            {
                var candidate = System.IO.Path.Combine(vsInstallDir, "MSBuild", "Current", "Bin", "MSBuild.exe");
                if (System.IO.File.Exists(candidate))
                {
                    _cachedMSBuildPath = candidate;
                    return _cachedMSBuildPath;
                }
            }

            // 2. Try vswhere.exe
            var vswherePath = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
            if (System.IO.File.Exists(vswherePath))
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = vswherePath,
                        Arguments = "-latest -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var proc = System.Diagnostics.Process.Start(psi))
                    {
                        var output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(5000);
                        var firstLine = output?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                        if (!string.IsNullOrEmpty(firstLine) && System.IO.File.Exists(firstLine))
                        {
                            _cachedMSBuildPath = firstLine;
                            return _cachedMSBuildPath;
                        }
                    }
                }
                catch
                {
                    // vswhere failed — fall through to directory scan
                }
            }

            // 3. Scan known VS installation directories
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var versions = new[] { "18", "2022", "2019" };
            var editions = new[] { "Enterprise", "Professional", "Community", "BuildTools" };
            foreach (var ver in versions)
            {
                foreach (var edition in editions)
                {
                    var candidate = System.IO.Path.Combine(programFiles, "Microsoft Visual Studio", ver, edition, "MSBuild", "Current", "Bin", "MSBuild.exe");
                    if (System.IO.File.Exists(candidate))
                    {
                        _cachedMSBuildPath = candidate;
                        return _cachedMSBuildPath;
                    }
                }
            }

            // 4. Last resort — bare msbuild, hope it's on PATH
            _cachedMSBuildPath = "msbuild";
            return _cachedMSBuildPath;
        }

    }
}
