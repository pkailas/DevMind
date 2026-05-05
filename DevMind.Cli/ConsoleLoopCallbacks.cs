// File: ConsoleLoopCallbacks.cs  v1.1
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
    /// <para>
    /// All console writes during streaming — both the token write path and the generating-status
    /// timer — go through <c>_consoleLock</c> so the two threads never interleave output.
    /// The generating status line renders only when <c>_cursorAtLineStart</c> is true (after a \n
    /// in the stream) so it never overwrites mid-line streaming content.
    /// </para>
    /// </summary>
    public sealed class ConsoleLoopCallbacks : ILoopCallbacks
    {
        private readonly ILlmClient _llmClient;

        private string _pendingInput = string.Empty;

        // ── Thinking timer (pre-first-token) ─────────────────────────────────────

        private Timer _thinkingTimer;
        private int   _thinkingElapsed;   // written via Interlocked from timer thread
        private int   _thinkingDepth;
        private int   _thinkingMaxDepth;
        private readonly object _timerLock = new object();

        // ── Generating timer (post-first-token) ──────────────────────────────────

        private readonly object _consoleLock = new object();

        private Timer  _generatingTimer;
        private int    _tokenCount;         // written via Interlocked
        private long   _generateStartMs;    // Environment.TickCount64
        private int    _generatingDepth;
        private int    _generatingMaxDepth;

        // Both guarded by _consoleLock.
        private bool _statusOnScreen;       // generating status line is the last thing on screen
        private bool _cursorAtLineStart;    // last char written to console was \n

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

        // ── Generating-status API (called from Program.RunTurnAsync) ──────────────

        /// <summary>
        /// Starts the 500ms generating-status timer. Resets token count and elapsed clock.
        /// Must be called after <see cref="StopThinkingTimer"/> on the first visible token.
        /// </summary>
        /// <param name="depth">1-based agentic iteration number (AgenticDepth + 1).</param>
        /// <param name="maxDepth">AgenticLoopMaxDepth from ILlmOptions (0 = unlimited).</param>
        public void StartGeneratingTimer(int depth, int maxDepth)
        {
            lock (_consoleLock)
            {
                _statusOnScreen    = false;
                _cursorAtLineStart = false;
            }
            lock (_timerLock)
            {
                _generatingTimer?.Dispose();
                Interlocked.Exchange(ref _tokenCount, 0);
                _generateStartMs    = Environment.TickCount64;
                _generatingDepth    = depth;
                _generatingMaxDepth = maxDepth;
                _generatingTimer = new Timer(_ => RenderGeneratingStatus(), null,
                    TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
            }
        }

        /// <summary>
        /// Stops the generating-status timer and erases the status line from the console.
        /// Idempotent — safe to call multiple times (e.g. from both catch and finally).
        /// </summary>
        public void StopGeneratingTimer()
        {
            lock (_timerLock)
            {
                _generatingTimer?.Dispose();
                _generatingTimer = null;
            }
            lock (_consoleLock)
            {
                if (_statusOnScreen && !Console.IsOutputRedirected)
                    Console.Write("\r\x1b[2K");
                _statusOnScreen = false;
            }
        }

        /// <summary>
        /// Writes a streaming token to the console, coordinating with the generating-status
        /// timer via <c>_consoleLock</c>. If the status line is on screen, it is erased first
        /// so the token lands on the correct row, then the status re-renders on the next tick.
        /// </summary>
        public void WriteStreamingToken(string token)
        {
            lock (_consoleLock)
            {
                if (_statusOnScreen && !Console.IsOutputRedirected)
                {
                    Console.Write("\r\x1b[2K");
                    _statusOnScreen = false;
                }
                Console.Write(token);
                if (token.Length > 0)
                    _cursorAtLineStart = token[token.Length - 1] == '\n';
            }
        }

        /// <summary>
        /// Increments the token counter used by the generating-status timer.
        /// Call once per non-empty visible chunk in the onToken callback.
        /// </summary>
        public void IncrementTokenCount() => Interlocked.Increment(ref _tokenCount);

        // ── Private ───────────────────────────────────────────────────────────────

        private void RenderGeneratingStatus()
        {
            lock (_consoleLock)
            {
                // Only render when cursor is genuinely at a line start — prevents overwriting
                // mid-line streaming content that hasn't wrapped to a new line yet.
                if (Console.IsOutputRedirected || !_cursorAtLineStart) return;

                int    tokens  = Volatile.Read(ref _tokenCount);
                double elapsed = (Environment.TickCount64 - _generateStartMs) / 1000.0;
                int    rate    = elapsed > 0.01 ? (int)(tokens / elapsed) : 0;
                string iter    = _generatingMaxDepth > 0
                    ? $", iter {_generatingDepth}/{_generatingMaxDepth}"
                    : string.Empty;
                string status  =
                    $"[Generating {tokens} tokens, {elapsed:F1}s, {rate} tok/s{iter}]";

                // \r\x1b[2K clears the current line regardless of whether status was
                // already there. Trailing \r returns cursor to col 0 so the next token
                // write (via WriteStreamingToken) erases this line cleanly.
                Console.Write($"\r\x1b[2K{status}\r");
                _statusOnScreen = true;
            }
        }
    }
}
