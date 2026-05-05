// File: Program.cs  v1.0
// DevMind.SpectrePoc — Spectre.Console streaming UX proof-of-concept.
//
// Tests whether Spectre.Console can deliver:
//   1. Scrolling content region — streaming tokens accumulate here
//   2. Live status row — updates every ~100ms with token count / rate
//   3. Input row — accepts keystrokes concurrently while streaming
//
// Type-ahead design compromise (surfaced in discovery):
//   Spectre.Console docs explicitly warn that Live display is not thread-safe
//   with other interactive components (prompts, status, etc.). The approach
//   here uses Console.KeyAvailable polling on a background thread at 50 Hz,
//   with Console.ReadKey(intercept: true) so keystrokes are NOT echoed to
//   the terminal. All display goes through Live's 10 Hz refresh loop.
//   Consequence: typed characters appear in the input row on the next refresh
//   tick (≤100ms latency), and the cursor visibly jumps during redraws.
//   Whether this latency and cursor flicker is acceptable is what this POC
//   tests. If not, the signal is: use Terminal.Gui or hand-rolled ANSI.
//
// Usage:
//   dotnet run
//   Type any prompt → Enter → watch streaming → type DURING streaming
//   Type "exit" or "quit" to end. Ctrl+C also exits cleanly.
//
// Standalone — no DevMind.Core / DevMind.Cli references.

using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// ── Cancellation ──────────────────────────────────────────────────────────────

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── Shared model ──────────────────────────────────────────────────────────────

var model = new PocModel();

// ── Input-reader task: polls Console.KeyAvailable at 50 Hz ───────────────────
//
// Reads keystroke into model._inputBuffer without echoing to console.
// Live display renders _inputBuffer in the input row on each refresh tick.

var inputTask = Task.Run(() =>
{
    while (!cts.IsCancellationRequested && !model.ShouldExit)
    {
        if (!Console.KeyAvailable) { Thread.Sleep(20); continue; }

        var key = Console.ReadKey(intercept: true);

        if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.C)
        {
            cts.Cancel();
            break;
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:     model.SubmitInput(); break;
            case ConsoleKey.Backspace: model.DeleteChar();  break;
            case ConsoleKey.Escape:    model.ClearInput();  break;
            default:
                if (!char.IsControl(key.KeyChar)) model.AppendChar(key.KeyChar);
                break;
        }
    }
});

// ── Token-emitter task: 30 tok/s via 3-token batches every 100ms ─────────────

var streamTask = Task.Run(async () =>
{
    try
    {
        while (!cts.IsCancellationRequested && !model.ShouldExit)
        {
            if (model.IsStreamPending)
            {
                model.StartStream();
                while (model.IsStreaming && !cts.IsCancellationRequested)
                {
                    model.EmitTokenBatch(3);
                    await Task.Delay(100, cts.Token);
                }
            }
            else
            {
                await Task.Delay(50, cts.Token);
            }
        }
    }
    catch (OperationCanceledException) { }
});

// ── Live display: 10 Hz refresh on main thread ────────────────────────────────
//
// Layout:  ┌──────────────────────────────────────┐
//          │ content (fills remaining height)     │
//          ├──────────────────────────────────────┤
//          │ status  (3 rows — 1 content line)    │
//          ├──────────────────────────────────────┤
//          │ input   (3 rows — 1 content line)    │
//          └──────────────────────────────────────┘

var layout = new Layout("root")
    .SplitRows(
        new Layout("content"),
        new Layout("status").Size(3),
        new Layout("input").Size(3));

// Seed the layout with initial panels so Live has something to measure.
layout["content"].Update(new Panel(new Markup(
    "[dim]Type a prompt below and press Enter to start streaming...[/]"))
    .Header("[bold blue] DevMind CLI — Spectre.Console POC [/]")
    .Expand());
layout["status"].Update(new Panel(new Markup(
    "[dim][Idle | iter 0/5 — type a prompt and press Enter][/]"))
    .Expand());
layout["input"].Update(new Panel(new Markup("[blue bold]>[/]  [dim]▌[/]"))
    .Expand());

try
{
    AnsiConsole.Live(layout)
        .Start(ctx =>
        {
            while (!cts.IsCancellationRequested && !model.ShouldExit)
            {
                var s = model.GetDisplayState();

                // Content: last N lines that fit in the available region.
                int maxContentLines = Math.Max(2, Console.WindowHeight - 8);
                var allLines = new List<string>(s.Lines);
                if (!string.IsNullOrEmpty(s.CurrentLine))
                    allLines.Add(s.CurrentLine + " …");

                var visible = allLines.Count > maxContentLines
                    ? allLines.GetRange(allLines.Count - maxContentLines, maxContentLines)
                    : allLines;

                string contentMarkup = visible.Count > 0
                    ? string.Join("\n", visible.Select(Markup.Escape))
                    : "[dim]Type a prompt below and press Enter to start streaming...[/]";

                layout["content"].Update(
                    new Panel(new Markup(contentMarkup))
                        .Header("[bold blue] DevMind CLI — Spectre.Console POC [/]")
                        .Expand());

                // Status: colored by streaming state.
                string statusMarkup = s.IsStreaming
                    ? $"[yellow]{Markup.Escape(s.Status)}[/]"
                    : s.HadStream
                        ? $"[green]{Markup.Escape(s.Status)}[/]"
                        : $"[dim]{Markup.Escape(s.Status)}[/]";

                layout["status"].Update(
                    new Panel(new Markup(statusMarkup))
                        .Expand());

                // Input: prompt + buffered text + cursor indicator.
                string inputMarkup = $"[blue bold]>[/]  {Markup.Escape(s.InputBuffer)}[dim]▌[/]";
                layout["input"].Update(
                    new Panel(new Markup(inputMarkup))
                        .Expand());

                ctx.Refresh();
                Thread.Sleep(100);
            }
        });
}
catch (OperationCanceledException) { }

cts.Cancel();
try { await Task.WhenAll(inputTask, streamTask); }
catch (OperationCanceledException) { }

AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[dim]Session ended. Goodbye.[/]");

// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Thread-safe POC model. Every field is guarded by <c>_lock</c>.
/// All three tasks (input, stream, display) read/write through public methods.
/// </summary>
internal sealed class PocModel
{
    // 230-word fake "LLM response" about TaskCompletionSource — matches the
    // test prompt from the Phase D manual verification spec.
    private const string FakeText =
        "A TaskCompletionSource in C# provides a way to manually control the completion of a Task. " +
        "Unlike awaiting an async method, a TCS gives you an explicit handle to produce a result, " +
        "set an exception, or signal cancellation from external code. This is the mechanism that " +
        "DevMind uses internally to bridge LlmClient streaming callbacks with the async REPL loop. " +
        "Here is how it works. First, create an instance: var tcs = new TaskCompletionSource. " +
        "The Task property returns a Task that will complete when you call TrySetResult, " +
        "TrySetException, or TrySetCanceled. Calling TrySetResult transitions the Task to the " +
        "Completed state — any awaiter resumes with the value. TrySetException transitions to " +
        "the Faulted state — any await rethrows the exception. TrySetCanceled transitions to " +
        "the Canceled state — any await throws OperationCanceledException. The Try variants are " +
        "safe to call from multiple threads because only the first call wins and subsequent calls " +
        "return false. This is exactly why DevMind uses TrySetResult in the onComplete callback " +
        "and TrySetCanceled in the cancellation-token registration. Both can fire concurrently " +
        "but only one wins and the Task completes exactly once. A common pattern is to use a TCS " +
        "as a bridge between event-driven code and async code: create a TCS, pass " +
        "tcs.TrySetResult as the callback to a legacy API, then await tcs.Task from your async " +
        "caller. The caller suspends until the callback fires, then resumes with the result. " +
        "DevMind uses exactly this pattern for its LlmClient streaming bridge. The registration " +
        "using var cancelReg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token)) ensures " +
        "that if the user presses Ctrl+C and LlmClient swallows the OperationCanceledException " +
        "without calling onComplete, the TCS is still resolved and the await does not hang forever. " +
        "This was the root cause of the Ctrl+C wedge bug fixed in Stage 9 Phase D.";

    private static readonly string[] Tokens =
        FakeText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private readonly object       _lock           = new();
    private readonly List<string> _completedLines = [];
    private          string       _currentLine    = string.Empty;
    private          string       _inputBuffer    = string.Empty;
    private          bool         _isStreaming;
    private          bool         _streamPending;
    private          bool         _shouldExit;
    private          bool         _hadStream;
    private          int          _tokenCount;
    private          long         _streamStartMs;
    private          long         _streamEndMs;
    private          int          _wordIndex;

    public bool IsStreaming     { get { lock (_lock) return _isStreaming;   } }
    public bool IsStreamPending { get { lock (_lock) return _streamPending; } }
    public bool ShouldExit      { get { lock (_lock) return _shouldExit;    } }

    public void AppendChar(char c) { lock (_lock) { _inputBuffer += c; } }

    public void DeleteChar()
    {
        lock (_lock)
        {
            if (_inputBuffer.Length > 0)
                _inputBuffer = _inputBuffer[..^1];
        }
    }

    public void ClearInput() { lock (_lock) { _inputBuffer = string.Empty; } }

    public void SubmitInput()
    {
        lock (_lock)
        {
            string trimmed = _inputBuffer.Trim();
            _inputBuffer = string.Empty;

            if (string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "quit", StringComparison.OrdinalIgnoreCase))
            {
                _shouldExit = true;
                return;
            }

            // Only start a new stream if one isn't already running.
            if (!string.IsNullOrEmpty(trimmed) && !_isStreaming)
                _streamPending = true;
        }
    }

    public void StartStream()
    {
        lock (_lock)
        {
            _isStreaming   = true;
            _streamPending = false;
            _tokenCount    = 0;
            _streamStartMs = Environment.TickCount64;
            _streamEndMs   = 0;
            _wordIndex     = 0;
            _completedLines.Clear();
            _currentLine   = string.Empty;
        }
    }

    /// <summary>
    /// Emits up to <paramref name="count"/> word-tokens, soft-wrapping at 78 chars.
    /// Sets <c>IsStreaming = false</c> when all tokens are consumed.
    /// </summary>
    public void EmitTokenBatch(int count)
    {
        lock (_lock)
        {
            if (!_isStreaming) return;

            for (int i = 0; i < count && _wordIndex < Tokens.Length; i++)
            {
                _currentLine += Tokens[_wordIndex++] + " ";
                _tokenCount++;

                if (_currentLine.Length >= 78)
                {
                    _completedLines.Add(_currentLine.TrimEnd());
                    _currentLine = string.Empty;
                }
            }

            if (_wordIndex >= Tokens.Length)
            {
                if (_currentLine.Length > 0)
                    _completedLines.Add(_currentLine.TrimEnd());
                _currentLine = string.Empty;
                _streamEndMs = Environment.TickCount64;
                _isStreaming = false;
                _hadStream   = true;
            }
        }
    }

    public DisplayState GetDisplayState()
    {
        lock (_lock)
        {
            string status;
            if (_isStreaming)
            {
                double el   = (Environment.TickCount64 - _streamStartMs) / 1000.0;
                int    rate = el > 0.01 ? (int)(_tokenCount / el) : 0;
                status = $"[Generating {_tokenCount} tokens, {el:F1}s, {rate} tok/s, iter 1/5]";
            }
            else if (_hadStream)
            {
                double el   = (_streamEndMs - _streamStartMs) / 1000.0;
                int    rate = el > 0.01 ? (int)(_tokenCount / el) : 0;
                status = $"[Done — {_tokenCount} tokens in {el:F1}s, {rate} tok/s  |  type to stream again]";
            }
            else
            {
                status = "[Idle | iter 0/5 — type a prompt and press Enter]";
            }

            return new DisplayState(
                new List<string>(_completedLines),
                _currentLine,
                _inputBuffer,
                status,
                _isStreaming,
                _hadStream);
        }
    }
}

/// <summary>Snapshot of model state consumed by the Live display loop.</summary>
internal record DisplayState(
    List<string> Lines,
    string       CurrentLine,
    string       InputBuffer,
    string       Status,
    bool         IsStreaming,
    bool         HadStream);
