// File: ConsoleLoopCallbacks.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Threading;

namespace DevMind
{
    /// <summary>
    /// Console implementation of <see cref="ILoopCallbacks"/> for the DevMind CLI REPL.
    /// <para>
    /// Status writes use \r to overwrite the current console line — works correctly when
    /// output is a terminal; silently skipped when redirected.
    /// </para>
    /// <para>
    /// Input text (_pendingInput) is written by <see cref="SetInputText"/> and consumed by
    /// <see cref="GetInputText"/> (read-and-clear). The REPL reads it after LoopDriver
    /// returns ShouldReTrigger to get the next message to send.
    /// </para>
    /// </summary>
    public sealed class ConsoleLoopCallbacks : ILoopCallbacks
    {
        private readonly ILlmClient _llmClient;

        private string _pendingInput = string.Empty;

        private Timer _thinkingTimer;
        private int   _thinkingElapsed;   // written via Interlocked from timer thread
        private int   _thinkingDepth;
        private int   _thinkingMaxDepth;
        private readonly object _timerLock = new object();

        public ConsoleLoopCallbacks(ILlmClient llmClient)
        {
            _llmClient = llmClient;
        }

        // ── ILoopCallbacks ────────────────────────────────────────────────────────

        public void AppendNewLine() => Console.WriteLine();

        public void SetStatus(string text)
        {
            if (Console.IsOutputRedirected) return;
            Console.Write($"\r{text,-60}");
        }

        // Fold into SetStatus — no dedicated context-indicator surface in CLI.
        public void SetContextIndicator(string text) => SetStatus(text);

        public void SetInputText(string text) => _pendingInput = text ?? string.Empty;

        // Read-and-clear: caller consumes the pending input once per ShouldReTrigger turn.
        public string GetInputText()
        {
            string value = _pendingInput;
            _pendingInput = string.Empty;
            return value;
        }

        public void FocusInput() { }

        public void SetInputEnabled(bool enabled) { }

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
            // Erase the status line so subsequent output starts on a clean line.
            if (!Console.IsOutputRedirected)
                Console.Write($"\r{new string(' ', 60)}\r");
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
