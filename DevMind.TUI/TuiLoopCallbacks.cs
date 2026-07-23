// File: TuiLoopCallbacks.cs  v3.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Terminal.Gui v2 implementation of ILoopCallbacks.
// Routes status/context/input to the TUI status bar and input field.
//
// v3.0 (Phase 3): unified per-turn ticker. One 100 ms Timer runs for the whole
// agentic turn (BeginTurn → EndTurn) and renders both phases:
//   thinking   — "⠹ Thinking... 00:02.4 (depth 1/10)"          (orange)
//   generating — "⠹ Generating... 00:05.1 (312 tok · 41.2 tok/s)" (pale yellow)
// plus a LIVE context meter: the per-iteration server anchor (n_past from the
// previous response) + tokens streamed this iteration, pushed at ~3 Hz. The
// ticker only ever sets status-bar Label text — it never touches the output
// Editor (TuiStatusBar marshals via IApplication.Invoke; strict-FIFO with the
// token appends, so it cannot reorder or disturb the transcript draw).
//
// ILoopCallbacks (StartThinkingTimer/StopThinkingTimer) keeps its meaning at
// the Core boundary: Start = an LLM iteration began (reset per-iteration
// counters, show thinking); Stop = tokens are flowing (switch to generating).
// BeginTurn/EndTurn/OnStreamToken are TUI-side extensions called by Program.cs.

// Suppress obsolete warnings for Terminal.Gui v2 legacy APIs.
#pragma warning disable CS0618

using System;
using System.Diagnostics;
using System.Threading;
using Terminal.Gui.ViewBase;

namespace DevMind
{
    /// <summary>
    /// Terminal.Gui v2 implementation of ILoopCallbacks for the DevMind TUI.
    /// </summary>
    public sealed class TuiLoopCallbacks : ILoopCallbacks
    {
        private static readonly string[] BrailleFrames =
            { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

        private readonly ILlmClient _llmClient;
        private readonly TuiStatusBar _statusBar;
        private readonly TuiAgenticHost _host;
        private readonly View _inputView;

        private string _pendingInput = string.Empty;

        // ── Turn ticker state ────────────────────────────────────────────────────
        private readonly object _tickerLock = new object();
        private Timer _turnTicker;                       // 100 ms; whole turn
        private readonly Stopwatch _turnClock = new Stopwatch();
        private int  _tickCount;
        private bool _generating;                        // false = thinking phase
        private int  _streamedTokens;                    // this ITERATION (reset per iteration)

        private long _firstTokenMs = -1;                 // turn-clock ms at first token of this iteration
        private int  _thinkingDepth;
        private int  _thinkingMaxDepth;

        public TuiLoopCallbacks(ILlmClient llmClient, TuiStatusBar statusBar, TuiAgenticHost host, View inputView)
        {
            _llmClient = llmClient;
            _statusBar = statusBar;
            _host      = host;
            _inputView = inputView;

            // Detection resolves mid-send (before any response); refresh the meter then so the
            // total appears without waiting for the first turn to complete.
            if (llmClient is LlmClient concrete)
                concrete.ContextSizeDetected += RefreshContextMeter;
        }

        // ── ILoopCallbacks ────────────────────────────────────────────────────────

        public void AppendNewLine()
        {
            // Through the host — TuiAgenticHost tracks the logical append position for
            // color stamping; a direct outputView.InsertText would desync it.
            _host.AppendOutputLocal("\n", OutputColor.Normal);
        }

        public void SetStatus(string text)
        {
            // Generic status text from the loop boundary — render in the busy color.
            _statusBar.SetBusy(text ?? string.Empty);
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
            _inputView.App.Invoke(() =>
            {
                _inputView.SetFocus();
            });
        }

        public void SetInputEnabled(bool enabled)
        {
            _inputView.App.Invoke(() =>
            {
                _inputView.CanFocus = enabled;
                if (enabled)
                    _inputView.SetFocus();
            });
        }

        /// <summary>An LLM iteration is starting: reset per-iteration counters and
        /// show the thinking phase. The turn ticker itself is started by BeginTurn.</summary>
        public void StartThinkingTimer(int depth, int maxDepth)
        {
            _thinkingDepth    = depth;
            _thinkingMaxDepth = maxDepth;
            Interlocked.Exchange(ref _streamedTokens, 0);
            Interlocked.Exchange(ref _firstTokenMs, -1);
            lock (_tickerLock)
            {
                _generating = false;
                // Defensive: if a caller starts an iteration without BeginTurn
                // (e.g. a future path), make sure the ticker is alive.
                if (_turnTicker == null) StartTickerLocked();
            }

            // Push iteration to the status bar chip (1-based display).
            _statusBar.SetIteration(depth + 1, maxDepth);
        }

        /// <summary>Tokens are flowing for the current iteration — switch the
        /// ticker to the generating phase. Does not stop the ticker.</summary>
        public void StopThinkingTimer()
        {
            lock (_tickerLock)
            {
                _generating = true;
            }
        }

        public (int used, int total) GetContextMetrics()
        {
            int used  = _llmClient.LastContextUsed > 0
                ? _llmClient.LastContextUsed
                : _llmClient.EstimateHistoryTokens();
            int total = _llmClient.ServerContextSize;
            return (used, total);
        }

        // ── TUI-side turn lifecycle (called by Program.cs) ───────────────────────

        /// <summary>A user turn is starting: reset the turn clock and start the ticker.</summary>
        public void BeginTurn()
        {
            lock (_tickerLock)
            {
                _generating = false;
                Interlocked.Exchange(ref _streamedTokens, 0);
                Interlocked.Exchange(ref _firstTokenMs, -1);
                _tickCount = 0;
                _turnClock.Restart();
                StartTickerLocked();
            }
        }

        /// <summary>The turn is over (done, cancelled, or errored): stop the ticker,
        /// sync the meter to server truth, publish the turn's tok/s, show Ready.</summary>
        public void EndTurn()
        {
            lock (_tickerLock)
            {
                _turnTicker?.Dispose();
                _turnTicker = null;
                _turnClock.Stop();
            }

            RefreshContextMeter();

            // Server-true generation rate from the last response's timings.
            int gen = _llmClient.LastGeneratedTokens;
            double ms = _llmClient.LastGeneratedMs;
            if (gen > 0 && ms > 0)
                _statusBar.SetTokRate(gen * 1000.0 / ms);

            _statusBar.SetReady();
            _statusBar.ClearIteration();
        }

        /// <summary>One streamed token arrived (called from the SSE onToken path —
        /// must stay cheap; an Interlocked increment and a one-time timestamp).</summary>
        public void OnStreamToken()
        {
            if (Interlocked.Increment(ref _streamedTokens) == 1)
                Interlocked.Exchange(ref _firstTokenMs, _turnClock.ElapsedMilliseconds);
        }

        /// <summary>Push current context metrics to the status bar's meter.</summary>
        public void RefreshContextMeter()
        {
            var (used, total) = GetContextMetrics();
            _statusBar.SetContextMeter(used, total);
        }

        // ── Ticker ───────────────────────────────────────────────────────────────

        private void StartTickerLocked()
        {
            _turnTicker?.Dispose();
            _turnTicker = new Timer(_ => Tick(), null,
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        }

        private void Tick()
        {
            int tick = Interlocked.Increment(ref _tickCount);
            string frame = BrailleFrames[tick % BrailleFrames.Length];
            string elapsed = FormatElapsed(_turnClock.ElapsedMilliseconds);

            bool generating;
            lock (_tickerLock) { generating = _generating; }

            // Round counter (DevMindShell parity): the agentic loop is 1-based
            // (round 1..depthCap), so display _thinkingDepth + 1 against the configured
            // cap (_thinkingMaxDepth = options.AgenticLoopMaxDepth, the persistent /depth-cap).
            int round = _thinkingDepth + 1;
            string rounds = _thinkingMaxDepth > 0
                ? $"round {round}/{_thinkingMaxDepth}"
                : $"round {round}";

            // Server-true live token counts populate during BOTH the thinking and generating
            // phases — llama-server's per-chunk timings (and vLLM usage) advance on reasoning
            // and tool_call chunks, not just visible content. Compute them here and render them
            // regardless of the Thinking/Generating label, so reasoning-heavy turns (hidden
            // <think>) and tool-call-only turns still show tok/s instead of a blank rate.
            // Prefer the server's running usage count; fall back to the content-delta count for
            // endpoints that don't report per-chunk usage.
            int serverLive = _llmClient.LiveGeneratedTokens;
            int outTok = serverLive > 0 ? serverLive : Volatile.Read(ref _streamedTokens);
            int inTok  = _llmClient.LivePromptTokens; // prompt tokens for this round (server usage)
            long firstMs = Interlocked.Read(ref _firstTokenMs);
            double genSecs = firstMs >= 0
                ? Math.Max(0.1, (_turnClock.ElapsedMilliseconds - firstMs) / 1000.0)
                : 0;
            // DevMindShell parity: show input/output split rather than a single count.
            string tokPart = (inTok > 0 || outTok > 0)
                ? $", {inTok:N0} in / {outTok:N0} out"
                : string.Empty;

            // Keep the Thinking/Generating label distinction; only the numeric readout is
            // ungated (the tokPart suffix now shows in both states).
            if (!generating)
            {
                _statusBar.SetState($"{frame} Thinking... ({elapsed}, {rounds}{tokPart})",
                    StatusState.Thinking);
            }
            else
            {
                _statusBar.SetState($"{frame} Generating... ({elapsed}, {rounds}{tokPart})",
                    StatusState.Busy);
            }

            // Live tok/s on the far-right rate chip — updates every tick and persists after
            // EndTurn (which finalizes it to the server-true rate), so there's always a tok/s
            // readout to glance at. Prefer llama-server's per-chunk timings.predicted_per_second
            // (spec-decode aware — the client wall-clock division undercounts 5-12x with spec
            // decode); fall back to outTok/genSecs only when the server reports no per-chunk
            // rate. Rendered in both phases so the rate shows during hidden reasoning and
            // tool-call-only turns, not just visible output.
            double serverRate = _llmClient.LiveTokensPerSecond;
            if (serverRate > 0)
                _statusBar.SetTokRate(serverRate);
            else if (outTok > 0 && genSecs > 0)
                _statusBar.SetTokRate(outTok / genSecs);

            // Live context meter at ~3 Hz: per-iteration server anchor + tokens
            // streamed this iteration. Resyncs to pure server truth at each
            // iteration boundary (RefreshContextMeter in RunTurnAsync) and at EndTurn.
            if (tick % 3 == 0)
            {
                var (anchor, total) = GetContextMetrics();
                // outTok (computed above) already resolves to server-true LiveGeneratedTokens,
                // or the streamed-delta fallback when the server reports no per-chunk usage.
                _statusBar.SetContextMeter(anchor + outTok, total);
            }
        }

        // mm:ss.t (DevMindShell formatElapsedMs parity).
        private static string FormatElapsed(long elapsedMs)
        {
            long totalTenths = elapsedMs / 100;
            long mins   = totalTenths / 600;
            long secs   = (totalTenths / 10) % 60;
            long tenths = totalTenths % 10;
            return $"{mins:00}:{secs:00}.{tenths}";
        }
    }
}
