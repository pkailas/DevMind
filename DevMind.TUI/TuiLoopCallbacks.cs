// File: TuiLoopCallbacks.cs  v1.0 (SPIKE)
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Terminal.Gui v2 implementation of ILoopCallbacks.
// Routes status/context/input to Terminal.Gui views.

// SPIKE: suppress obsolete warnings for Terminal.Gui v2 legacy APIs.
#pragma warning disable CS0618

using System;
using System.Threading;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace DevMind
{
    /// <summary>
    /// Terminal.Gui v2 implementation of ILoopCallbacks for the DevMind TUI spike.
    /// </summary>
    public sealed class TuiLoopCallbacks : ILoopCallbacks
    {
        private readonly ILlmClient _llmClient;
        private readonly Label _statusLabel;
        private readonly TextView _outputView;
        private readonly TextField _inputField;

        private string _pendingInput = string.Empty;

        private Timer _thinkingTimer;
        private int   _thinkingElapsed;
        private int   _thinkingDepth;
        private int   _thinkingMaxDepth;
        private readonly object _timerLock = new object();

        public TuiLoopCallbacks(ILlmClient llmClient, Label statusLabel, TextView outputView, TextField inputField)
        {
            _llmClient   = llmClient;
            _statusLabel = statusLabel;
            _outputView  = outputView;
            _inputField  = inputField;
        }

        // ── ILoopCallbacks ────────────────────────────────────────────────────────

      public void AppendNewLine()
        {
            _outputView.App.Invoke(() =>
            {
                _outputView.InsertText("\n");
            });
        }

        public void SetStatus(string text)
        {
            _statusLabel.App.Invoke(() =>
            {
                _statusLabel.Text = text ?? string.Empty;
            });
        }

        public void SetContextIndicator(string text) => SetStatus(text);

        public void SetInputText(string text) => _pendingInput = text ?? string.Empty;

        public string GetInputText()
        {
            string value = _pendingInput;
            _pendingInput = string.Empty;
            return value;
        }

      public void FocusInput()
        {
            _inputField.App.Invoke(() =>
            {
                _inputField.SetFocus();
            });
        }

        public void SetInputEnabled(bool enabled)
        {
            _inputField.App.Invoke(() =>
            {
                _inputField.CanFocus = enabled;
                if (enabled)
                    _inputField.SetFocus();
            });
        }

        public void StartThinkingTimer(int depth, int maxDepth)
        {
            lock (_timerLock)
            {
                _thinkingTimer?.Dispose();
                Interlocked.Exchange(ref _thinkingElapsed, 0);
                _thinkingDepth    = depth;
                _thinkingMaxDepth = maxDepth;
                _thinkingTimer = new Timer(_ =>
                {
                    int elapsed = Interlocked.Increment(ref _thinkingElapsed);
                    string depthSuffix = _thinkingMaxDepth > 0
                        ? $" (depth {_thinkingDepth}/{_thinkingMaxDepth})"
                        : string.Empty;
                    SetStatus($"Thinking... {elapsed}s{depthSuffix}");
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }

        public void StopThinkingTimer()
        {
            lock (_timerLock)
            {
                _thinkingTimer?.Dispose();
                _thinkingTimer = null;
            }
            SetStatus("Ready");
        }

        public (int used, int total) GetContextMetrics()
        {
            int used  = _llmClient.LastContextUsed > 0
                ? _llmClient.LastContextUsed
                : _llmClient.EstimateHistoryTokens();
            int total = _llmClient.ServerContextSize;
            return (used, total);
        }
    }
}
