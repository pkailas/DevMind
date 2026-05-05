// File: ConsoleLoopCallbacks.cs  v1.2
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
    /// All console writes during streaming — both the token write path and the generating-status
    /// timer — go through <c>_consoleLock</c> so the two threads never interleave output.
    /// </para>
    /// <para>
    /// <b>Partial-line tracking</b>: <c>_partialLine</c> accumulates streaming content on the
    /// current (incomplete) line. When the 500ms timer overwrites the partial line with a status
    /// string, <c>WriteStreamingToken</c> restores <c>_partialLine</c> before writing the next
    /// token so no streaming content is lost. This removes the need for cursor-position guessing
    /// and lets the status render unconditionally every 500ms.
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
        // _statusOnScreen: generating status string is the last thing written on the current line.
        // _partialLine: streaming content accumulated on the current (incomplete) line since
        //               last \n. Restored before each new token write when _statusOnScreen is true.
        private bool   _statusOnScreen;
        private string _partialLine = string.Empty;

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
        /// Starts the 500ms generating-status timer. Resets token count, elapsed clock,
        /// and partial-line buffer.
        /// Must be called after <see cref="StopThinkingTimer"/> on the first visible token.
        /// </summary>
        /// <param name="depth">1-based agentic iteration number (AgenticDepth + 1).</param>
        /// <param name="maxDepth">AgenticLoopMaxDepth from ILlmOptions (0 = unlimited).</param>
        public void StartGeneratingTimer(int depth, int maxDepth)
        {
            lock (_consoleLock)
            {
                _statusOnScreen = false;
                _partialLine    = string.Empty;
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
        /// Does NOT restore the partial line — callers are responsible for their own newlines.
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
        /// timer via <c>_consoleLock</c>. If the status line is on screen, restores the
        /// buffered partial-line content first so no streaming text is lost, then writes the
        /// new token and updates the partial-line buffer.
        /// </summary>
        public void WriteStreamingToken(string token)
        {
            lock (_consoleLock)
            {
                if (_statusOnScreen && !Console.IsOutputRedirected)
                {
                    // Restore the partial streaming line that the status overwrote,
                    // so the new token continues seamlessly after it.
                    Console.Write($"\r\x1b[2K{_partialLine}");
                    _statusOnScreen = false;
                }
                Console.Write(token);

                // Track content on the current partial line (since the last \n).
                int lastNl = token.LastIndexOf('\n');
                if (lastNl >= 0)
                    _partialLine = token.Substring(lastNl + 1);
                else
                    _partialLine += token;
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
                if (Console.IsOutputRedirected) return;

                // Interlocked.Add(ref, 0) is a full-fence read — stronger than Volatile.Read.
                int    tokens  = Interlocked.Add(ref _tokenCount, 0);
                double elapsed = (Environment.TickCount64 - _generateStartMs) / 1000.0;
                int    rate    = elapsed > 0.01 ? (int)(tokens / elapsed) : 0;
                string iter    = _generatingMaxDepth > 0
                    ? $", iter {_generatingDepth}/{_generatingMaxDepth}"
                    : string.Empty;
                string status  =
                    $"[Generating {tokens} tokens, {elapsed:F1}s, {rate} tok/s{iter}]";

                // \r\x1b[2K clears the current line (replacing any partial streaming content
                // or the previous status string). The partial-line content is preserved in
                // _partialLine and will be restored by WriteStreamingToken on the next token.
                Console.Write($"\r\x1b[2K{status}");
                _statusOnScreen = true;
            }
        }
    }
}
