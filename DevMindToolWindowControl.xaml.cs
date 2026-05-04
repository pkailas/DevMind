// File: DevMindToolWindowControl.xaml.cs  v7.20
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
    public partial class DevMindToolWindowControl : UserControl, ILoopCallbacks
    {
        private readonly LlmClient _llmClient;
        private readonly ProfileManager _profileManager;
        private bool _suppressProfileChange;
        private CancellationTokenSource _cts;
        private (string fullPath, Encoding fileEncoding, string fileName, string content, List<(int origStart, int origEnd, string replaceText)> resolvedBlocks)? _pendingFuzzyPatch;
        private bool _suppressSystemPromptSave;
        private ShellRunner _shellRunner;
        private readonly List<string> _terminalHistory = new List<string>();
        private int _terminalHistoryIndex = -1;
        private System.Windows.Threading.DispatcherTimer _generatingTimer;
        private Run _generatingRun;
        private System.Windows.Threading.DispatcherTimer _thinkingTimer;
        private int _thinkingSeconds;
        private string _devMindContext;
        private readonly LoopState _loopState = new LoopState();
        private int _lastShellExitCode;
        private string _lastShellCommand;
        private readonly ThinkFilter _thinkFilter = new ThinkFilter();
        private string _pendingThinkText;
        private readonly Stack<(string originalPath, string backupPath)> _patchBackupStack = new Stack<(string, string)>();
        private const int PatchBackupStackLimit = 10;
        private Paragraph _spacerParagraph;
        // Tracks filenames (filename-only, case-insensitive) that have been READ during the
        // current task. Cleared at the start of each new top-level user request.
        // Used by the unrelated-file write guard in AgenticHost.
        internal readonly HashSet<string> _taskReadFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _diffPreviewPending;
        private int _streamingTokenCount;
        private int _patchCount = 0;
        private int _undoCount = 0;
        private int _readFileCount = 0;
        private TrainingLogger _trainingLogger;
        // Combined system prompt for the current task (user prompt + tool catalog + DevMind.md).
        // Built on the first user-initiated turn; reused on agentic resubmits to keep the KV
        // cache prefix stable. Never written back to DevMindOptions.Instance.SystemPrompt.
        private string _currentCombinedPrompt;
        private LoopDriver _loopDriver;

        public DevMindToolWindowControl(LlmClient llmClient)
        {
            InitializeComponent();
            Themes.SetUseVsTheme(this, true);
            OutputBox.Document.PagePadding = new Thickness(0);
            InitOutputDocument();
            _llmClient = llmClient;
            _shellRunner = new ShellRunner(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            _loopDriver = new LoopDriver(_llmClient, this, this, DevMindOptions.Instance, _loopState);

#pragma warning disable VSSDK007 // Fire-and-forget to resolve solution directory is intentional
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
#pragma warning restore VSSDK007
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = await VS.GetServiceAsync<DTE, DTE>();
                    if (dte?.Solution?.FullName is string sln && !string.IsNullOrEmpty(sln))
                        _shellRunner.ChangeDirectory(Path.GetDirectoryName(sln));
                }
                catch { }
            });

            ResetTrainingLogger();
            LoadSystemPromptText();
            _profileManager = new ProfileManager();
            PopulateProfileComboBox();
            DevMindOptions.Saved += OnSettingsSaved;
            DevMindOptions.ProfileChanged += OnProfileChangedFromOptions;
            // Defer banner until after first layout pass so ViewportHeight is known for spacer calc
#pragma warning disable VSTHRD001
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendBanner();
                // Attach profile-notification sink AFTER banner renders so any
                // messages queued during early ProfileManager construction
                // (e.g. corrupt profiles.json detected at startup) appear
                // just after the banner rather than before it.
                ProfileManager.AttachNotificationSink(msg =>
                {
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AppendOutput(msg + "\n", OutputColor.Warning);
                    }));
                });
            }), System.Windows.Threading.DispatcherPriority.Loaded);
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
            // Cancel any in-flight LLM request or ProcessIterationAsync before resetting state.
            _cts?.Cancel();

            // Clear output
            InitOutputDocument();

            _thinkFilter.Reset();
            _loopState.ResetForUserTurn();

            // Clear terminal history
            _terminalHistory.Clear();
            _terminalHistoryIndex = 0;

            // Clear LLM conversation history
            _llmClient.ClearHistory();

            // Force DevMind.md reload on next Ask
            _devMindContext = null;

            // Clear read file count
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

            ResetTrainingLogger();

            AppendOutput("DevMind restarted.\n", OutputColor.Dim);
            SetInputEnabled(true);

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

        private void ResetTrainingLogger()
        {
            string folder = DevMindOptions.Instance.TrainingLogFolder;
            System.Diagnostics.Debug.WriteLine($"[ResetTrainingLogger] TrainingLogFolder='{folder}'");
            _trainingLogger = new TrainingLogger(Guid.NewGuid().ToString("N").Substring(0, 12),
                string.IsNullOrWhiteSpace(folder) ? null : folder);
        }

        private void LogTrainingTurn(
            string userMessage,
            string assistantResponse,
            ResponseOutcome outcome,
            ExecutionResult result,
            List<ToolCallResult> toolCalls)
        {
            if (!DevMindOptions.Instance.TrainingLogEnabled || _trainingLogger == null)
                return;

            try
            {
                bool hasErrors = result?.Errors?.Count > 0
                    || (result?.ShellExitCode.HasValue == true && result.ShellExitCode != 0);
                bool hasToolCalls = toolCalls?.Count > 0
                    || (outcome?.Blocks?.Any(b => b.Type != BlockType.Text && b.Type != BlockType.Scratchpad) == true);

                int nCtx = _llmClient.ServerContextSize > 0 ? _llmClient.ServerContextSize : _llmClient.MaxPromptTokens;
                int nPast = _llmClient.LastContextUsed;
                int pct = nCtx > 0 ? (int)(nPast * 100.0 / nCtx) : 0;
                double tokPerSec = _llmClient.LastGeneratedMs > 0
                    ? _llmClient.LastGeneratedTokens * 1000.0 / _llmClient.LastGeneratedMs
                    : 0;

                var data = new TrainingTurnData
                {
                    TurnNumber = _llmClient.CurrentTurn,
                    SystemPrompt = _llmClient.SystemPromptContent,
                    UserMessage = userMessage,
                    AssistantResponse = assistantResponse,
                    ToolCalls = TrainingLogger.ExtractToolCalls(outcome?.Blocks),
                    ToolResults = TrainingLogger.ExtractToolResults(result),
                    SummaryContext = _llmClient.LastCompactionSummary,
                    Metrics = new MetricsEntry
                    {
                        NPast = nPast,
                        NCtx = nCtx,
                        PredictedTokens = _llmClient.LastGeneratedTokens,
                        PromptTokens = _llmClient.LastPromptTokens,
                        TokPerSec = Math.Round(tokPerSec, 1),
                        Iteration = _loopState.AgenticDepth,
                        ContextPercent = pct
                    },
                    Outcome = TrainingLogger.ClassifyOutcome(
                        outcome?.IsDone == true,
                        hasErrors,
                        outcome?.HasReadRequests == true,
                        outcome?.HasPatches == true,
                        outcome?.HasFileCreation == true,
                        outcome?.HasShellCommands == true,
                        hasToolCalls)
                };

                _trainingLogger.LogTurn(data);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrainingLogger] LogTrainingTurn failed: {ex.Message}");
            }
        }

        private void SetInputEnabled(bool enabled)
        {
            InputTextBox.IsEnabled = enabled;
            AskButton.IsEnabled = enabled;
            RunButton.IsEnabled = enabled;
            StopButton.IsEnabled = !enabled || _diffPreviewPending;
        }

        // ── LLM ───────────────────────────────────────────────────────────────

#pragma warning disable VSSDK007 // async void UI event handler is intentional
#pragma warning disable VSTHRD100
        private async void SendToLlm()
#pragma warning restore VSTHRD100
#pragma warning restore VSSDK007
        {
            System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] SendToLlm ENTER — _loopState.ShellLoopPending={_loopState.ShellLoopPending}, _loopState.AgenticDepth={_loopState.AgenticDepth}");
            string text = InputTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                System.Diagnostics.Debug.WriteLine("[DevMind TRACE] SendToLlm EXIT — text is null/empty");
                return;
            }
            System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] SendToLlm text={text.Substring(0, Math.Min(text.Length, 120))}...");

            // Reset agentic depth for user-initiated calls; preserve it for agentic re-triggers.
            // Increment turn counter on every call so tiered eviction and MicroCompact age tracking
            // see increasing turn numbers during the agentic loop.
            if (!_loopState.ShellLoopPending)
            {
                _loopState.ResetForUserTurn();
                _taskReadFiles.Clear();
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

            _thinkFilter.Reset();

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
            if (!_loopState.ShellLoopPending)
                await AutoReadReferencedFilesAsync(text);

            string activeProjectPath = await GetActiveProjectPathAsync();
            string contextualMessage = ContextEngine.BuildMessageWithContext(text, selectedText, fileName, fullContent, activeProjectPath);

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

            StatusText.Text = _loopState.AgenticDepth > 0
                ? $"Thinking... (agentic {_loopState.AgenticDepth}/{DevMindOptions.Instance.AgenticLoopMaxDepth})"
                : "Thinking...";
            _thinkingSeconds = 0;
            _thinkingTimer?.Stop();
            _thinkingTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _thinkingTimer.Tick += (s, e) =>
            {
                _thinkingSeconds++;
                string agenticSuffix = _loopState.AgenticDepth > 0
                    ? $" (agentic {_loopState.AgenticDepth}/{DevMindOptions.Instance.AgenticLoopMaxDepth})"
                    : "";
                StatusText.Text = $"Thinking{agenticSuffix} ({_thinkingSeconds}s)";
            };
            _thinkingTimer.Start();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _streamingTokenCount = 0;

            var streamPara = new Paragraph { Margin = new Thickness(0) };
            OutputBox.Document.Blocks.Add(streamPara);
            var thinkRun = new Run { Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x8A)) };
            streamPara.Inlines.Add(thinkRun);
            var streamRun = new Run { Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)) };
            streamPara.Inlines.Add(streamRun);
            var responseBuffer = new StringBuilder();

            // Build the combined system prompt on the first user-initiated turn and store it
            // in _currentCombinedPrompt for the lifetime of this agentic task. Agentic
            // resubmits reuse the stored value — no DTE re-fetch — so the KV cache prefix
            // stays stable across iterations. The combined prompt is passed explicitly to
            // SendMessageAsync; DevMindOptions.Instance.SystemPrompt is never overwritten.

            // Hoist buildCommand so it's accessible in the onComplete lambda for ToolCallMapper
            // VSIX projects fail with dotnet build — detect via .vsixmanifest sibling and use MSBuild instead
            string buildCommand;
            if (activeProjectPath != null)
            {
                var projectDir = System.IO.Path.GetDirectoryName(activeProjectPath);
                bool isVsix = projectDir != null &&
                    (System.IO.File.Exists(System.IO.Path.Combine(projectDir, "source.extension.vsixmanifest")) ||
                     System.IO.File.Exists(System.IO.Path.Combine(projectDir, "extension.vsixmanifest")));
                var msbuildPath = LoopHelpers.FindMSBuildPath();
                var msbuildInvoke = msbuildPath.Contains(" ") ? $"& \"{msbuildPath}\"" : msbuildPath;
                buildCommand = isVsix
                    ? $"{msbuildInvoke} \"{activeProjectPath}\" /p:DeployExtension=false /p:Configuration=Release /verbosity:minimal"
                    : $"dotnet build \"{activeProjectPath}\" /p:Configuration=Release";
            }
            else
            {
                var msbuildPath = LoopHelpers.FindMSBuildPath();
                var msbuildInvoke = msbuildPath.Contains(" ") ? $"& \"{msbuildPath}\"" : msbuildPath;
                buildCommand = $"{msbuildInvoke} \"C:\\Users\\pkailas.KAILAS\\source\\repos\\DevMind\\DevMind.slnx\" /p:DeployExtension=false /p:Configuration=Release /verbosity:minimal";
            }

            if (!_loopState.ShellLoopPending)
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

                string llmDirective = LoopHelpers.BuildToolUsePrompt(buildCommand, projectNamespace);

                string combined = $"{DevMindOptions.Instance.SystemPrompt}\n\n{llmDirective}";
                if (!string.IsNullOrEmpty(_devMindContext))
                    combined += $"\n\n--- Project Context (DevMind.md) ---\n{_devMindContext}\n---";

                // Load MEMORY.md cross-session memory index
                try
                {
                    var memMgr = new MemoryManager(_shellRunner.WorkingDirectory);
                    string memoryIndex = memMgr.LoadIndex();
                    if (!string.IsNullOrWhiteSpace(memoryIndex))
                        combined += $"\n\n--- Session Memory (MEMORY.md) ---\n{memoryIndex}\n---";
                }
                catch { /* Memory loading is best-effort */ }

                _currentCombinedPrompt = combined;
            }

            System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] About to call SendMessageAsync — contextualMessage length={contextualMessage.Length}");

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
                                var visible = _thinkFilter.Process(token, DevMindOptions.Instance.ShowLlmThinking, out _pendingThinkText);

                                // Route thinking tokens to the dim think run
                                if (_pendingThinkText != null)
                                {
                                    thinkRun.Text += _pendingThinkText;
                                    OutputBox.ScrollToEnd();
                                }

                                if (string.IsNullOrEmpty(visible)) return;

                                responseBuffer.Append(visible);

                                _streamingTokenCount++;

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

                                string fullResponse = responseBuffer.ToString();
                                System.Diagnostics.Debug.WriteLine($"[DEVMIND-DIAG] responseBuffer length={fullResponse.Length}");

                                var iter = await _loopDriver.ProcessIterationAsync(fullResponse, buildCommand, _cts?.Token ?? CancellationToken.None);

                                if (iter.ShouldLogTurn)
                                    LogTrainingTurn(text, iter.AssistantResponse, iter.Outcome, iter.Result, iter.ToolCalls);

                                switch (iter.Kind)
                                {
                                    case LoopIterationKind.Terminal:
                                        _thinkingTimer?.Stop();
                                        _thinkingTimer = null;
                                        StatusText.Text = "Ready";
                                        ContextIndicator.Text = "";
                                        SetInputEnabled(true);
                                        InputTextBox.Focus();
                                        break;

                                    case LoopIterationKind.Cancelled:
                                        StatusText.Text = "Stopped";
                                        SetInputEnabled(true);
                                        break;

                                    case LoopIterationKind.ShouldReTrigger:
                                        if (_cts?.IsCancellationRequested == true)
                                        {
                                            _loopState.ShellLoopPending    = false;
                                            _loopState.AgenticDepth        = 0;
                                            _loopState.PromptedForTaskDone = false;
                                            StatusText.Text = "Stopped";
                                            SetInputEnabled(true);
                                            break;
                                        }
                                        InputTextBox.Text = iter.NextContextualMessage;
                                        try { SendToLlm(); }
                                        catch
                                        {
                                            _loopState.ShellLoopPending    = false;
                                            _loopState.AgenticDepth        = 0;
                                            _loopState.PromptedForTaskDone = false;
                                            StatusText.Text = "Error";
                                            SetInputEnabled(true);
                                            throw;
                                        }
                                        return;
                                }
                                }
                                catch (Exception onCompleteEx) when (!(onCompleteEx is OperationCanceledException))
                                {
                                    _loopState.ShellLoopPending = false;
                                    _loopState.AgenticDepth = 0;
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
                                StopGeneratingAnimation();
                                _thinkingTimer?.Stop();
                                _thinkingTimer = null;
                                _loopState.ShellLoopPending = false;
                                _loopState.AgenticDepth = 0;
                                AppendOutput($"\n[Error: {ex.Message}]\n", OutputColor.Error);
                                StatusText.Text = "Error";
                                SetInputEnabled(true);
                            }));
                        },
                        deferCompression: _loopState.ShellLoopPending,
                        combinedSystemPrompt: _currentCombinedPrompt,
                        cancellationToken: _cts.Token);
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (_cts.IsCancellationRequested)
                    {
                        // When cancelled, LlmClient swallows the OCE and never calls onComplete.
                        // Nobody else will re-enable input, so we must do it here.
                        StopGeneratingAnimation();
                        _thinkingTimer?.Stop();
                        _thinkingTimer = null;
                        AppendOutput("\n[Stopped]\n", OutputColor.Dim);
                        StatusText.Text = "Stopped";
                        _loopState.ShellLoopPending = false;
                        _loopState.AgenticDepth = 0;
                        SetInputEnabled(true);
                    }
                    // Do NOT call SetInputEnabled(true) unconditionally here.
                    // In the non-cancelled path the onComplete handler was dispatched via
                    // Dispatcher.BeginInvoke and may not have executed yet — calling
                    // SetInputEnabled here before it runs would race with its own call and
                    // could reset state mid-iteration. All terminal paths in onComplete already
                    // call SetInputEnabled(true). The cancellation branch above is exempt from
                    // this constraint because onComplete is never dispatched when cancelled.
                    // _loopState.ShellLoopPending is safe to clear unconditionally — it's a no-op
                    // when onComplete hasn't run yet (still false), and a cleanup when it has.
                    _loopState.ShellLoopPending = false;
                }
            });
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
                string saveDir = projectDir ?? _shellRunner.WorkingDirectory;

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

        #region ILoopCallbacks

        void ILoopCallbacks.AppendNewLine() => AppendNewLine();

        void ILoopCallbacks.SetStatus(string text) => StatusText.Text = text;

        void ILoopCallbacks.SetContextIndicator(string text) => ContextIndicator.Text = text;

        void ILoopCallbacks.SetInputText(string text) => InputTextBox.Text = text;

        string ILoopCallbacks.GetInputText() => InputTextBox.Text;

        void ILoopCallbacks.FocusInput() => InputTextBox.Focus();

        void ILoopCallbacks.SetInputEnabled(bool enabled) => SetInputEnabled(enabled);

        void ILoopCallbacks.StartThinkingTimer(int depth, int maxDepth)
        {
            _thinkingSeconds = 0;
            _thinkingTimer?.Stop();
            _thinkingTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _thinkingTimer.Tick += (s, e) =>
            {
                _thinkingSeconds++;
                string agenticSuffix = depth > 0
                    ? $" (agentic {depth}/{maxDepth})"
                    : "";
                StatusText.Text = $"Thinking{agenticSuffix} ({_thinkingSeconds}s)";
            };
            StatusText.Text = depth > 0
                ? $"Thinking... (agentic {depth}/{maxDepth})"
                : "Thinking...";
            _thinkingTimer.Start();
        }

        void ILoopCallbacks.StopThinkingTimer()
        {
            _thinkingTimer?.Stop();
            _thinkingTimer = null;
        }

        (int used, int total) ILoopCallbacks.GetContextMetrics()
        {
            int used  = _llmClient.LastContextUsed > 0
                ? _llmClient.LastContextUsed
                : _llmClient.EstimateHistoryTokens();
            int total = _llmClient.ServerContextSize > 0
                ? _llmClient.ServerContextSize
                : _llmClient.MaxPromptTokens;
            return (used, total);
        }

        #endregion

    }
}
