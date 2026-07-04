// File: TuiAgenticHost.cs  v2.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Terminal.Gui v2 implementation of IAgenticHost.
// Cribbed from ConsoleAgenticHost — file/shell/patch/memory logic is identical;
// only AppendOutput routes to a Terminal.Gui.Editor.Editor instead of Console.Write.
//
// (output-view migration): the output transcript was a [Obsolete] TextView
// whose WordWrap rebuilds the ENTIRE wrapped model on every grapheme insert (O(n)
// per token — sluggish on long transcripts) and whose per-cell color required
// reflection into _wrapManager/WrapTextModel plus a hand-written fix for an upstream
// wrap-attribute-copy bug. This version drives gui-cs/Editor's Editor instead:
//   * append  → Document.Insert(Document.TextLength, text) on a rope-backed model
//               (O(log n)); CaretOffset = TextLength auto-scrolls to the newest line.
//   * color   → one IVisualLineTransformer on Editor.LineTransformers that sets
//               element.Attribute by document offset (offset space, no reflection,
//               survives wrap/resize). VisualLineBuilder emits one element per
//               grapheme, so color boundaries are exact with zero bleed.
//   * readonly→ ReadOnly = true; Document.Insert bypasses the command guard, so the
//               view is a non-editable programmatic log (no ReadOnly=false hack).
// ResolveAttribute (OutputColor→RGB) is the only piece of the old color path kept.

// Suppress obsolete warnings for Terminal.Gui v2 legacy APIs.
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Rendering;
using GuiEditor = Terminal.Gui.Editor.Editor;

namespace DevMind
{
    /// <summary>
    /// Terminal.Gui v2 implementation of IAgenticHost for the DevMind TUI.
    /// All file/shell/patch/memory logic cribbed verbatim from ConsoleAgenticHost.
    /// AppendOutput routes to a Terminal.Gui.Editor.Editor via IApplication.Invoke.
    /// </summary>
    public sealed partial class TuiAgenticHost : IAgenticHost
    {
        // ── Fields ───────────────────────────────────────────────────────────────

        private readonly ShellRunner _shellRunner;
        private readonly FileContentCache _fileCache = new FileContentCache();

        private readonly HashSet<string> _filesRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _taskReadFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _fileSnapshots =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // LRU bookkeeping for _fileSnapshots: each entry holds a whole file's original content, so the
        // map is capped to bound memory. Oldest-accessed entry is evicted past the cap.
        private const int MaxFileSnapshots = 20;
        private readonly Dictionary<string, long> _fileSnapshotUse =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private long _fileSnapshotUseCounter;

       private MemoryManager _memoryManager;

        // Shared Core facade for the five LSP tools — gating and error wrapping live there.
        private readonly LspToolService _lspTools;

        private const int PatchBackupStackLimit = 10;
        private readonly Stack<(string filePath, string backupPath)> _patchBackupStack =
            new Stack<(string, string)>();

       private readonly Action _cancelTurn;

        // Task scratchpad — stores cross-turn state from SCRATCHPAD directives.
        // Injected into the system prompt each turn by Program.cs.
        private string _taskScratchpad = "";

        // Pending merge conflict state — stored so /resolve can handle it without blocking input.
        private PendingConflictState _pendingConflict;

        // The Editor that receives all output. Rope-backed document, append-only.
        private readonly GuiEditor _outputView;

        // Color is decoupled from the document: each InsertSpan call records the
        // [start, start+len) document offset range it inserted plus the resolved
        // Attribute. OffsetColorTransformer (registered on _outputView.LineTransformers)
        // reads this list during visual-line construction and stamps element.Attribute
        // by offset. Append-only and strictly increasing in Start, so lookups binary-
        // search. Mutated by InsertSpan and read by the transformer — both run on the
        // Terminal.Gui UI thread (the flush timer drains via App.Invoke; Transform runs in Draw),
        // so no locking is required.
        private readonly List<ColorSpan> _colorSpans = new List<ColorSpan>();

        // ── Coalesced append buffer + UI render pump ─────────────────────────────
        // Streamed output (one SSE token per call) used to queue one App.Invoke — and so one
        // InsertSpan + one full window redraw — per token. Worse, the Terminal.Gui Windows main
        // loop parks in its input-wait during a turn and is NOT reliably woken by a background
        // thread's App.Invoke: nothing rendered (no token text, no spinner advance) until the
        // turn's teardown ran on the UI thread and drove the loop, which dumped the whole backlog
        // at once — the "frozen spinner, then a burst of text at end of round" symptom.
        //
        // Fix: producers enqueue spans here (no Invoke), and a persistent main-loop timeout — the
        // render pump, registered via IApplication.AddTimeout — drains the backlog on the UI thread
        // every FlushIntervalMs. AddTimeout is part of the loop's wait computation, so the loop is
        // GUARANTEED to wake on that cadence (unlike App.Invoke). Those same wakes also service the
        // status ticker's queued Invoke, so the spinner animates throughout generation.
        //
        // 100 ms (10 fps), NOT faster: flush+redraw are serialized on the UI thread, and the
        // word-wrapped Editor's repaint costs ~50 ms (climbing with document size). A 40 ms pump
        // tried ~25 redraws/s × 50 ms = 1.25 s of work per second — it fell behind and the backlog
        // became multi-second freezes. 10 fps leaves the redraw comfortable headroom; combined with
        // the scrollback cap (which keeps repaint cheap) the pump stays ahead of the stream.
        private const int FlushIntervalMs = 100; // 10 fps — must stay >= Editor repaint cost
        private readonly object _pendingLock = new object();
        private readonly List<(string text, Terminal.Gui.Drawing.Attribute attr)> _pending =
            new List<(string, Terminal.Gui.Drawing.Attribute)>();
        private object _renderPumpToken; // AddTimeout handle; non-null once the pump is registered

        // ── Diagnostics ──────────────────────────────────────────────────────────
        // Set DEVMIND_TUI_DIAG to a file path to trace the color-stamping pipeline
        // (reflection handle resolution, append path taken, spans, exceptions). Inert
        // when unset. Never writes to the UI.

        private static readonly string DiagPath =
            Environment.GetEnvironmentVariable("DEVMIND_TUI_DIAG");

        internal static void Diag(string message)
        {
            if (string.IsNullOrEmpty(DiagPath)) return;
            try
            {
                File.AppendAllText(DiagPath,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { }
        }

        // Set by the REPL loop before each agentic turn.
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;

        // ── Construction ─────────────────────────────────────────────────────────

        public TuiAgenticHost(string workingDirectory, GuiEditor outputView, Action cancelTurn = null)
        {
            _shellRunner  = new ShellRunner(workingDirectory);
            _lspTools     = new LspToolService(workingDirectory);
            _outputView   = outputView;
            _cancelTurn   = cancelTurn ?? (() => { });
            if (!string.IsNullOrEmpty(workingDirectory))
                _memoryManager = new MemoryManager(workingDirectory);

            // Register the color transformer. It reads _colorSpans (populated by InsertSpan)
            // and stamps element.Attribute by document offset during visual-line construction.
            _outputView.LineTransformers.Add(new OffsetColorTransformer(_colorSpans));

            // Scroll lock, tracked as a pure wheel-notch counter — NO geometry. Two earlier
            // designs failed against this Editor: position sampling lost the 100 ms-flush
            // race to one-row wheel notches, and geometry checks read a stale ContentSize
            // (the Editor recomputes it at draw time, not at insert time), mis-unpinning
            // mid-read. Each wheel-up notch scrolls one row and increments the counter; each
            // wheel-down decrements it (with an at-bottom geometry HINT that only ever zeroes
            // the counter on a down-notch — the benign direction). While the counter is
            // positive the document is completely frozen (see FlushPending), so nothing can
            // race. This observer fires before the wheel's scroll command executes
            // (View.RaiseMouseEvent order) and only observes — Handled stays false, so the
            // Editor's own scrolling is untouched.
            _outputView.MouseEvent += (s, mouse) =>
            {
                if (mouse.Flags.HasFlag(Terminal.Gui.Input.MouseFlags.WheeledUp))
                {
                    if (_pinnedScrollRows < int.MaxValue)
                        SetPinnedScrollRows(_pinnedScrollRows + 1);
                }
                else if (mouse.Flags.HasFlag(Terminal.Gui.Input.MouseFlags.WheeledDown)
                         && _pinnedScrollRows > 0)
                {
                    // Notches past the top inflate the counter without moving the view, so a
                    // pure countdown could leave the user pinned AT the bottom; the at-bottom
                    // hint (post-scroll, +1 row) zeroes it out in that case.
                    int next = _pinnedScrollRows - 1;
                    if (next <= 0 || IsScrolledToBottom(pendingScrollRows: 1))
                        next = 0;
                    SetPinnedScrollRows(next);
                }
            };
        }

        // Scroll-lock state: > 0 while the user has wheeled up to read scrollback (the
        // net count of wheel-up notches). While positive, FlushPending freezes the
        // document — no inserts, no trims, no caret moves — so the view cannot shift
        // under the reader; streamed output buffers in _pending meanwhile. UI thread
        // only (wheel events, FlushPending, and the input loop all run there).
        private int _pinnedScrollRows;

        /// <summary>
        /// Raised on the UI thread when the transcript's scroll pin engages (true) or
        /// releases (false). The host UI uses it to show/hide the "jump to bottom" toast.
        /// </summary>
        public event Action<bool> ScrollPinChanged;

        // All pin-state mutations funnel through here so 0↔positive transitions raise
        // ScrollPinChanged exactly once per edge. UI thread only.
        private void SetPinnedScrollRows(int value)
        {
            bool wasPinned = _pinnedScrollRows > 0;
            _pinnedScrollRows = value;
            if (wasPinned != (value > 0))
            {
                try { ScrollPinChanged?.Invoke(value > 0); }
                catch { /* a subscriber failure must never break scrolling */ }
            }
        }

        // ── Context lifecycle helpers ────────────────────────────────────────────

        /// <summary>LSP availability for the status bar (delegates to Core's LspToolService).</summary>
        public (bool enabled, string languages) GetLspStatus() => _lspTools.GetStatus();

        public void ResetTaskContext() => _taskReadFiles.Clear();

       public void ResetSession()
        {
            _filesRead.Clear();
            _fileSnapshots.Clear();
            _fileSnapshotUse.Clear();
            _fileSnapshotUseCounter = 0;
            _fileCache.InvalidateAll();
           _taskReadFiles.Clear();
            _taskScratchpad = "";
            _pendingConflict = null;
            CleanupDap();
            _pendingBreaks.Reset();
        }

        // ── IAgenticHost.AppendOutput ─────────────────────────────────────────────

       void IAgenticHost.AppendOutput(string text, OutputColor color)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Quiet transcript (DevMindShell parity): the engine still emits per-iteration
            // churn ([CONTEXT] usage / [TOOL_USE] / [LLM] / [AGENTIC] Iteration) for the CLI
            // skin and history, but the TUI keeps that state in the status bar — not the
            // scrollback. The actual tool-call lines (green/amber: [READ]/[SHELL]/[FILE]/…) are
            // KEPT. Swallow only the churn here unless verbose output is enabled.
            if (IsSuppressedNoise(text)) return;

            EnqueueSpan(text, ResolveAttribute(color));
        }

        // ── Coalesced append pipeline ─────────────────────────────────────────────
        // Versus the old TextView path the underlying insert is dramatically simpler and cheaper:
        //   * Document.Insert at TextLength is an O(log n) rope splice — no whole-document
        //     re-wrap per token (the TextView cost that hurt long, fast-streaming transcripts).
        //   * Color is NOT stamped into the model. We record (start, len, attr); the registered
        //     IVisualLineTransformer applies it per visible element at draw time, in document-
        //     offset space — so colors survive wrap and resize with no reflection and no
        //     hand-patched wrap-attribute-copy bug.
        //   * CaretOffset = TextLength scrolls to the newest line (auto-scroll). The caret is
        //     navigation, not an edit, so it works under ReadOnly and CanFocus=false.

        /// <summary>
        /// Register the UI render pump — a recurring main-loop timeout that drains the coalesced
        /// append buffer on the UI thread. Call once, on the UI thread, with the live application
        /// (the output view's App is still null before app.Run, so it's passed in). Idempotent.
        /// </summary>
        public void StartRenderPump(IApplication app)
        {
            if (app == null || _renderPumpToken != null) return;

            // ── Fullscreen-snap fix: disarm WindowsOutput's "maximize workaround" ──
            // Terminal.Gui's WindowsOutput.GetSize() (verified by decompilation, present
            // unchanged through 2.4.16) remembers the window size whenever the reported
            // size equals GetLargestConsoleWindowSize, and on the next differing report
            // FORCES that remembered size back — a workaround for legacy-conhost
            // Alt+Enter that misfires under Windows Terminal: after F11 fullscreen the
            // app can be forced back to its pre-fullscreen dimensions and never fills
            // the terminal. The trap lives in the private field
            // _lastWindowSizeBeforeMaximized; nulling it every pump tick keeps it
            // permanently disarmed so the driver always follows the REAL reported size.
            // Worst case a resize transition slips through within one 100 ms tick — the
            // next poll then takes the normal path and self-corrects. All reflection is
            // best-effort: if the field vanishes in an upgrade this becomes a no-op.
            object driverOutput = null;
            System.Reflection.FieldInfo maximizeTrap = null;
            try
            {
                driverOutput = app.Driver?.GetOutput();
                maximizeTrap = driverOutput?.GetType().GetField(
                    "_lastWindowSizeBeforeMaximized",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            }
            catch { /* driver shape changed — feature degrades to no-op */ }

            var lastScreen = System.Drawing.Rectangle.Empty;
            _renderPumpToken = app.AddTimeout(
                TimeSpan.FromMilliseconds(FlushIntervalMs),
                () =>
                {
                    if (maximizeTrap != null)
                    {
                        try { maximizeTrap.SetValue(driverOutput, null); }
                        catch { maximizeTrap = null; } // never let the pump die over this
                    }
                    try
                    {
                        // Diag-only breadcrumb (DEVMIND_TUI_DIAG): trace terminal size changes
                        // so fullscreen/resize misbehavior is observable in the field.
                        var screen = app.Driver?.Screen ?? default;
                        if (screen != lastScreen)
                        {
                            Diag($"[SCREEN] {lastScreen.Width}x{lastScreen.Height} -> {screen.Width}x{screen.Height}");
                            lastScreen = screen;
                        }
                    }
                    catch { /* diagnostics only */ }
                    FlushPending();
                    return true; // keep pumping for the app's life
                });
        }

        // Enqueue one colored span. Producers call this from any thread — no App.Invoke; the render
        // pump drains the buffer on the next UI tick. App is null before app.Run() attaches the
        // window (startup banner): at that point we are on the main thread with no running loop and
        // no pump, so insert directly to keep ordering with the banner.
        private void EnqueueSpan(string text, Terminal.Gui.Drawing.Attribute attr)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (_outputView.App == null)
            {
                InsertSpan(text, attr, scroll: true);
                return;
            }

            lock (_pendingLock)
                _pending.Add((text, attr));
        }

        // Drain the whole pending backlog into the document in ONE UI-thread pass: a single
        // Document.Insert followed by exactly one auto-scroll. MUST run on the UI thread (invoked by
        // the render pump). Cheap when idle (lock + count check).
        private void FlushPending()
        {
            // Scroll lock: while the user is pinned reading scrollback, the document is
            // FROZEN — no insert (so no re-wrap, no row growth), no trim (a front-trim
            // would yank the text being read), no caret move (whose EnsureCaretVisible is
            // the snap-back). The stream keeps buffering in _pending and pours in on
            // unpin. The backlog is bounded to the scrollback cap: older spans past it
            // would be trimmed the moment they landed anyway, so drop them here instead
            // of letting a walked-away-while-pinned session grow without limit.
            if (_pinnedScrollRows > 0)
            {
                lock (_pendingLock)
                {
                    int total = 0;
                    foreach (var p in _pending) total += p.text.Length;
                    if (total > MaxDocChars)
                    {
                        int drop = 0, dropped = 0;
                        while (dropped < total - KeepDocChars && drop < _pending.Count - 1)
                            dropped += _pending[drop++].text.Length;
                        _pending.RemoveRange(0, drop);
                    }
                }
                return;
            }

            (string text, Terminal.Gui.Drawing.Attribute attr)[] batch;
            lock (_pendingLock)
            {
                if (_pending.Count == 0) return;
                batch = _pending.ToArray();
                _pending.Clear();
            }

            TextDocument doc = _outputView.Document;
            if (doc == null)
            {
                Diag($"[FLUSH] SKIP (no document) batch={batch.Length}");
                return;
            }

            // Lever A — one Document.Insert per flush, not one per color run. Each Document.Insert
            // under WordWrap triggers a full-document re-wrap in the Editor (no incremental-wrap
            // API), so inserting once per color RUN multiplied that O(doc) cost by the number of
            // runs — the reason syntax-highlighted code (many colors/line) froze worst. Instead we
            // concatenate the entire drained batch into ONE string and record each color run as an
            // offset SUB-RANGE of that single insert: exactly one re-wrap per flush regardless of
            // how many colors the batch contains. Consecutive same-Attribute spans still coalesce
            // into one ColorSpan (keeps the span list small). The coloring path is UNCHANGED — the
            // transformer simply reads sub-offsets of one insert instead of one-insert-per-span.
            //
            // Per-run newline normalization is applied before measuring each run's length, so the
            // recorded sub-offsets match the normalized text that actually lands in the document
            // (identical to the old per-run InsertSpan normalization). Spans are committed to
            // _colorSpans only after a successful insert, so a throwing insert can never leave the
            // span list pointing past the document end.
            int start = doc.TextLength;
            var combined = new StringBuilder();
            var pendingSpans = new List<ColorSpan>();
            int i = 0;
            while (i < batch.Length)
            {
                Terminal.Gui.Drawing.Attribute attr = batch[i].attr;
                int runStart = start + combined.Length;
                int runLen = 0;
                int j = i;
                while (j < batch.Length && batch[j].attr.Equals(attr))
                {
                    string piece = NormalizeNewlines(batch[j].text);
                    combined.Append(piece);
                    runLen += piece.Length;
                    j++;
                }
                if (runLen > 0) pendingSpans.Add(new ColorSpan(runStart, runLen, attr));
                i = j;
            }

            if (combined.Length == 0)
            {
                Diag($"[FLUSH] spans={batch.Length} empty-after-normalize");
                return;
            }

            try
            {
                doc.Insert(start, combined.ToString());
            }
            catch (Exception ex)
            {
                // Keep the app alive — an append failure must never take down the UI loop.
                Diag($"[FLUSH] INSERT EXCEPTION len={combined.Length} ex={ex}");
                return;
            }
            _colorSpans.AddRange(pendingSpans);

            TrimScrollbackIfNeeded(doc);

            // A live mouse selection owns the caret: setting CaretOffset while a selection
            // anchor is active EXTENDS the selection to the end of the document on every
            // flush, corrupting what the user is trying to select mid-stream. Skip the
            // auto-scroll until the selection is gone — copy (Ctrl+C), a plain click, and
            // send all clear it, and following resumes on the next flush.
            if (!_outputView.HasSelection)
                _outputView.CaretOffset = doc.TextLength; // one auto-scroll for the whole batch
            // The output view is read-only and unfocusable, so its undo history is dead weight:
            // doc.Insert (above) and doc.Remove (in the trim) each push an undo entry, so the
            // UndoStack grows unbounded across a session — the one piece of render state the
            // scrollback cap does NOT bound. Clear it every flush (negligible cost) so it stays
            // at zero permanently; /cls (ClearOutputView) does the same on demand.
            try { doc.UndoStack?.ClearAll(); } catch { /* best effort — never break the UI loop */ }
            Diag($"[FLUSH] spans={batch.Length} runs={pendingSpans.Count} insert=1 total={doc.TextLength}");
        }

        // Normalize line endings to the document's '\n' basis — a stray '\r' would render as a
        // visible glyph and skew color-span offsets. Fast path: skip the allocations when clean.
        private static string NormalizeNewlines(string text)
            => text.IndexOf('\r') < 0 ? text : text.Replace("\r\n", "\n").Replace("\r", "\n");

        // True when the viewport (shifted by pendingScrollRows, for predicting where a
        // not-yet-executed wheel scroll will land) shows the last visual row.
        // GetContentSize().Height is the Editor's total visual-row count (wrap-map space,
        // same coordinate system as Viewport.Y) — but it is recomputed at DRAW time, so it
        // can be stale between draws. That's why this is only used as a benign HINT (the
        // wheel-down counter-zeroing in the ctor observer), never as the pin/follow
        // decision itself. UI thread only.
        private bool IsScrolledToBottom(int pendingScrollRows)
        {
            try
            {
                var viewport = _outputView.Viewport;
                return viewport.Y + pendingScrollRows + viewport.Height
                       >= _outputView.GetContentSize().Height;
            }
            catch
            {
                return true; // detection must never break following
            }
        }

        /// <summary>
        /// Jumps the output view to the newest line and resumes stream-following (the
        /// scroll lock in FlushPending pins the view while the user reads scrollback).
        /// Called by the host input loop when a new message is sent — typing implies
        /// wanting to see the reply. UI thread only.
        /// </summary>
        public void ScrollOutputToEnd()
        {
            SetPinnedScrollRows(0);
            _outputView.ClearSelection(); // a stale selection anchor would re-highlight the stream
            TextDocument doc = _outputView.Document;
            if (doc != null)
                _outputView.CaretOffset = doc.TextLength;
        }

        // Keep the transcript bounded. Each Document.Insert under WordWrap triggers a FULL-document
        // re-wrap in the Editor (O(doc length); there is no incremental-wrap API — verified by
        // decompilation), so an unbounded transcript makes every flush's re-wrap cost climb without
        // limit and the render pump falls behind (multi-second freezes late in long sessions). When
        // the document exceeds MaxDocChars, drop the oldest text down to KeepDocChars and rebase the
        // color spans into the shrunk offset space. Runs on the UI thread inside FlushPending, so it
        // never races the transformer's reads of _colorSpans. Trims are infrequent (only after
        // MaxDocChars-KeepDocChars more chars stream in), so the O(spans) rebase is cheap amortized.
        //
        // The cap is sourced once at startup from DEVMIND_SCROLLBACK_CAP (chars). Default 64000.
        // Smaller caps keep each re-wrap cheaper (more responsive) at the cost of less retained
        // scrollback; a floor of 16000 prevents trim thrashing. KeepDocChars trims to 75% of the
        // cap. (Lever B of the freeze fix; Lever A is the single-insert batching in FlushPending.)
        private const int ScrollbackCapDefault = 64_000;
        private const int ScrollbackCapFloor   = 16_000;
        private static readonly int MaxDocChars;
        private static readonly int KeepDocChars;
        private static readonly string _scrollbackCapDescription;

        /// <summary>One-line description of the effective scrollback cap and its source, for the
        /// startup banner — e.g. "scrollback cap: 64000 chars (default)" or
        /// "scrollback cap: 96000 chars (DEVMIND_SCROLLBACK_CAP)".</summary>
        public static string ScrollbackCapDescription => _scrollbackCapDescription;

        // Resolve the scrollback cap once (env read is not per-flush). Defensive parse: unset /
        // non-numeric / non-positive → default; below the floor → clamp up and note it.
        static TuiAgenticHost()
        {
            string raw = Environment.GetEnvironmentVariable("DEVMIND_SCROLLBACK_CAP");
            int cap;
            string source;
            if (int.TryParse((raw ?? string.Empty).Trim(), out int parsed) && parsed > 0)
            {
                if (parsed < ScrollbackCapFloor)
                {
                    cap = ScrollbackCapFloor;
                    source = $"DEVMIND_SCROLLBACK_CAP={parsed} clamped up to floor {ScrollbackCapFloor}";
                }
                else
                {
                    cap = parsed;
                    source = "DEVMIND_SCROLLBACK_CAP";
                }
            }
            else
            {
                cap = ScrollbackCapDefault;
                source = "default";
            }

            MaxDocChars  = cap;
            KeepDocChars = (int)Math.Round(cap * 0.75);
            _scrollbackCapDescription = $"scrollback cap: {cap} chars ({source})";
            Diag($"[INIT] {_scrollbackCapDescription} keep={KeepDocChars}");
        }

        private void TrimScrollbackIfNeeded(TextDocument doc)
        {
            int len = doc.TextLength;
            if (len <= MaxDocChars) return;

            int cut = len - KeepDocChars; // remove the front [0, cut)
            try
            {
                doc.Remove(0, cut);
            }
            catch (Exception ex)
            {
                Diag($"[TRIM] EXCEPTION cut={cut} ex={ex.Message}");
                return;
            }

            // Rebase spans: drop those fully before the cut, clamp the one straddling it, shift the
            // rest down by cut. Mutate the list in place — the transformer holds this same instance.
            var rebased = new List<ColorSpan>(_colorSpans.Count);
            foreach (ColorSpan s in _colorSpans)
            {
                int end = s.Start + s.Length;
                if (end <= cut) continue;                 // fully trimmed away
                int newStart = s.Start - cut;
                int newLen   = s.Length;
                if (newStart < 0) { newLen += newStart; newStart = 0; } // straddles the cut
                if (newLen <= 0) continue;
                rebased.Add(new ColorSpan(newStart, newLen, s.Attr));
            }
            _colorSpans.Clear();
            _colorSpans.AddRange(rebased);
            Diag($"[TRIM] cut={cut} newTotal={doc.TextLength} spans={_colorSpans.Count}");
        }

        // Shared insert: normalize newlines, splice at document end, record the color span. When
        // scroll is true the caret follows the newest line (direct/pre-init path); the batched
        // flush sets the caret once for the whole batch instead. MUST run on the UI thread.
        private void InsertSpan(string text, Terminal.Gui.Drawing.Attribute attr, bool scroll)
        {
            // Normalize line endings — the document is '\n'-based; a stray '\r' would render
            // as a visible glyph and skew offsets.
            text = NormalizeNewlines(text);
            if (text.Length == 0) return;

            TextDocument doc = _outputView.Document;
            if (doc == null)
            {
                // Document is assigned in Program.cs before the window runs; guard anyway so a
                // stray pre-init append can never NRE the UI loop.
                Diag($"[APPEND] SKIP (no document) len={text.Length}");
                return;
            }

            int start = doc.TextLength;

            try
            {
                doc.Insert(start, text);
                _colorSpans.Add(new ColorSpan(start, text.Length, attr));
                if (scroll) _outputView.CaretOffset = doc.TextLength; // auto-scroll to newest line
                // No per-insert Diag here: during fast streaming the per-call File.AppendAllText
                // (~1 ms each) dominated and skewed timing. FlushPending logs one [FLUSH] line/batch.
            }
            catch (Exception ex)
            {
                // Keep the app alive — an append failure must never take down the UI loop.
                Diag($"[APPEND] EXCEPTION len={text.Length} ex={ex}");
            }
        }

        // ── /cls — UI-only screen clear ───────────────────────────────────────────
        // Resets the output view's render state without touching conversation history,
        // context, or session (those live in LlmClient / the caches, not the view).
        // Beyond cosmetics this recovers input responsiveness in long sessions: every
        // append triggers a WordWrap re-wrap that is O(document length), and the document
        // UndoStack grows with every append. The transcript text and _colorSpans are
        // already bounded by the scrollback cap, but the UndoStack is NOT — so we clear
        // all three here. Runs on the UI thread (app.Invoke) so it never races the color
        // transformer's reads of _colorSpans.
        internal void ClearOutputView()
        {
            var app = _outputView.App;

            void DoClear()
            {
                SetPinnedScrollRows(0);                 // a cleared view has nothing to stay pinned to
                lock (_pendingLock) _pending.Clear();   // drop queued, not-yet-rendered spans

                TextDocument doc = _outputView.Document;
                if (doc != null && doc.TextLength > 0)
                {
                    try { doc.Remove(0, doc.TextLength); }   // clear text → scrollback to 0
                    catch (Exception ex) { Diag($"[CLS] remove ex={ex.Message}"); }
                }
                _colorSpans.Clear();                          // reset accumulated color spans

                // One-line confirmation so the cleared view isn't ambiguous.
                InsertSpan("[screen cleared — conversation and context preserved]\n",
                    ResolveAttribute(OutputColor.Dim), scroll: true);

                // Reset the document undo history — the one piece of render state the
                // scrollback cap does not bound (cleared after the re-seed so it stays empty).
                try { doc?.UndoStack?.ClearAll(); } catch (Exception ex) { Diag($"[CLS] undo ex={ex.Message}"); }

                app?.LayoutAndDraw();   // Terminal.Gui v2 equivalent of a forced Refresh
            }

            if (app != null) app.Invoke(DoClear); else DoClear();
        }

        // ── IAgenticHost.ConfirmContinueAsync ─────────────────────────────────────
        // Mid-turn yes/no prompt for the token-budget guard. Marshals to the UI thread,
        // runs a modal Continue/Stop dialog, and resolves the awaiting loop with the choice.
        Task<bool> IAgenticHost.ConfirmContinueAsync(string message)
        {
            IApplication app = _outputView.App;
            if (app == null) return Task.FromResult(true); // pre-init / non-interactive — don't block

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            app.Invoke(() =>
            {
                bool answer = false;
                var dlg = new Dialog
                {
                    Title  = "Token budget",
                    Width  = Dim.Percent(70),
                    Height = 9,
                };
                var label = new Label
                {
                    Text   = message,
                    X = 1, Y = 1,
                    Width  = Dim.Fill(2),
                    Height = Dim.Fill(2),
                };
                var contBtn = new Button { Text = "_Continue" };
                var stopBtn = new Button { Text = "_Stop", IsDefault = true };
                contBtn.Accepting += (s, e) => { answer = true;  e.Handled = true; app.RequestStop(); };
                stopBtn.Accepting += (s, e) => { answer = false; e.Handled = true; app.RequestStop(); };
                dlg.Add(label);
                dlg.AddButton(contBtn);
                dlg.AddButton(stopBtn);

                try { app.Run(dlg); }
                finally { dlg.Dispose(); tcs.TrySetResult(answer); }
            });
            return tcs.Task;
        }

        // ── Syntax-highlighted code append ────────────────────────────────────────
        // Tokenizes `code` for `language` and paints each token its VS Code Dark+ color. Tokens
        // enqueue onto the shared coalesced buffer — the same queue prose uses — so code and prose
        // keep strict arrival order and the whole block drains in the next flush rather than one
        // UI hop per token. Bypasses the quiet-transcript filter — this is real code, not churn.
        internal void AppendCode(string code, string language)
        {
            if (string.IsNullOrEmpty(code)) return;
            var tokens = SyntaxHighlighter.Highlight(code, language);
            Terminal.Gui.Drawing.Color bg = _outputView.GetScheme().Normal.Background;
            foreach (var t in tokens)
                EnqueueSpan(t.Text, new Terminal.Gui.Drawing.Attribute(SyntaxColor(t.Kind), bg));
        }

        // VS Code Dark+ palette, matching the reference screenshot.
        private static readonly Terminal.Gui.Drawing.Color SynKeyword = new Terminal.Gui.Drawing.Color(0x56, 0x9C, 0xD6); // #569CD6 blue
        private static readonly Terminal.Gui.Drawing.Color SynControl = new Terminal.Gui.Drawing.Color(0xC5, 0x86, 0xC0); // #C586C0 purple
        private static readonly Terminal.Gui.Drawing.Color SynType    = new Terminal.Gui.Drawing.Color(0x4E, 0xC9, 0xB0); // #4EC9B0 teal
        private static readonly Terminal.Gui.Drawing.Color SynMethod  = new Terminal.Gui.Drawing.Color(0xDC, 0xDC, 0xAA); // #DCDCAA yellow
        private static readonly Terminal.Gui.Drawing.Color SynString  = new Terminal.Gui.Drawing.Color(0xCE, 0x91, 0x78); // #CE9178 orange
        private static readonly Terminal.Gui.Drawing.Color SynComment = new Terminal.Gui.Drawing.Color(0x6A, 0x99, 0x55); // #6A9955 green
        private static readonly Terminal.Gui.Drawing.Color SynNumber  = new Terminal.Gui.Drawing.Color(0xB5, 0xCE, 0xA8); // #B5CEA8 pale green
        private static readonly Terminal.Gui.Drawing.Color SynPlain   = new Terminal.Gui.Drawing.Color(0xD4, 0xD4, 0xD4); // #D4D4D4 light

        private static Terminal.Gui.Drawing.Color SyntaxColor(TokenKind kind)
        {
            switch (kind)
            {
                case TokenKind.Keyword:        return SynKeyword;
                case TokenKind.ControlKeyword: return SynControl;
                case TokenKind.Type:           return SynType;
                case TokenKind.Method:         return SynMethod;
                case TokenKind.StringLit:      return SynString;
                case TokenKind.Comment:        return SynComment;
                case TokenKind.Number:         return SynNumber;
                default:                       return SynPlain;
            }
        }

        // Max lines of a file echoed into the transcript on a full read. Beyond this we
        // highlight the head and note the remainder, so a large file (or a busy agentic
        // loop reading many files) cannot flood the scrollback.
        private const int MaxListingLines = 400;

        private void AppendHighlightedListing(string content, string fullPath, int lineCount)
        {
            string lang = SyntaxHighlighter.LanguageFromExtension(fullPath);

            if (lineCount > MaxListingLines)
            {
                string[] lines = content.Replace("\r\n", "\n").Split('\n');
                string head = string.Join("\n", lines, 0, MaxListingLines);
                AppendCode(head + "\n", lang);
                AppendOutputLocal(
                    $"… ({lineCount - MaxListingLines:N0} more lines — {lineCount:N0} total)\n",
                    OutputColor.Dim);
            }
            else
            {
                AppendCode(content.EndsWith("\n", StringComparison.Ordinal) ? content : content + "\n", lang);
            }
        }

        // ── Quiet-transcript filter ───────────────────────────────────────────────
        // DevMindShell kept the scrollback clean — model output + tool-output regions only —
        // with agentic/context/token state in the status bar. The C# engine emits the same
        // state as bracketed status lines (for the CLI skin + history); we suppress the noisy
        // subset in the TUI so the transcript matches DevMindShell. Set DEVMIND_TUI_VERBOSE to
        // restore the full firehose (parity with the roadmap's /verbose).
        private static readonly bool VerboseOutput =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DEVMIND_TUI_VERBOSE"));

        private static bool IsSuppressedNoise(string text)
        {
            if (VerboseOutput) return false;

            // Engine status lines arrive as a complete "[TAG] …\n" call, often with a leading
            // "\n" (Core's onToken lines). Find the first non-whitespace char and match there.
            int i = 0;
            while (i < text.Length && (text[i] == '\n' || text[i] == '\r' || text[i] == ' ' || text[i] == '\t'))
                i++;
            if (i >= text.Length) return false;

            // [TOOL_USE] / [LLM] — always per-turn churn.
            if (StartsAt(text, i, "[TOOL_USE]")) return true;
            if (StartsAt(text, i, "[LLM]")) return true;

            // [AGENTIC] — drop only the per-iteration counter; KEEP terminal states
            // (Task complete / Depth cap / Aborted / Cancelled / Run-succeeded).
            if (StartsAt(text, i, "[AGENTIC] Iteration")) return true;

            // NOTE: [READ] / [SHELL] / [FILE] / [PATCH] / [GREP] / [FIND] / [LSP] are the
            // actual tool-call lines (green Success / amber Warning) — KEEP them all. Only the
            // white [TOOL_USE] placeholder above is dropped; the real tool action stays.

            // [CONTEXT] — drop the routine usage meter (numeric / "~" estimate / "Working:");
            // KEEP signal lines (CRITICAL, Hard/Soft trim, Warning, Compacting, Brainwash, …).
            if (StartsAt(text, i, "[CONTEXT] "))
            {
                int j = i + "[CONTEXT] ".Length;
                if (j < text.Length)
                {
                    char c = text[j];
                    if (char.IsDigit(c) || c == '~') return true;
                    if (StartsAt(text, j, "Working:")) return true;
                }
            }

            return false;
        }

        private static bool StartsAt(string s, int offset, string prefix)
            => offset + prefix.Length <= s.Length
               && string.CompareOrdinal(s, offset, prefix, 0, prefix.Length) == 0;

        // ── IAgenticHost.RunShellAsync ────────────────────────────────────────────

       async Task<(int exitCode, string output)> IAgenticHost.RunShellAsync(string command, int? timeoutSeconds)
        {
            AppendOutputLocal($"[SHELL] > {command}\n", OutputColor.Dim);
            var progress = new Progress<ShellOutputLine>(line =>
                AppendOutputLocal(line.Line + "\n", line.IsError ? OutputColor.Error : OutputColor.Normal));
            var (output, exitCode) = await _shellRunner.ExecuteAsync(command, CancellationToken, timeoutSeconds, progress);
            return (exitCode, output);
        }

        // ── IAgenticHost.SaveFileAsync ────────────────────────────────────────────

        async Task<string> IAgenticHost.SaveFileAsync(string fileName, string content, bool fromToolCall)
        {
            string fileNameOnly = SafeGetFileName(fileName);

            if (!IsFileKnownToTask(fileNameOnly))
            {
                bool approved = await ConfirmUnreadFileWriteAsync(fileNameOnly);
                if (!approved)
                {
                    AppendOutputLocal($"[WRITE GUARD] File write to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                    return null;
                }
                _taskReadFiles.Add(fileNameOnly);
            }

            // Block if a conflict is pending
            if (_pendingConflict != null)
            {
                AppendOutputLocal($"[MERGE CONFLICT] Cannot write to \"{fileNameOnly}\" — pending conflict on \"{_pendingConflict.FilePath}\" must be resolved first. Use /resolve accept_proposed, /resolve accept_current, or /resolve cancel.\n", OutputColor.Error);
                return null;
            }

            string fileContent = fromToolCall ? content : PatchEngine.StripOuterCodeFence(content);

            try
            {
                string fullPath = ResolveWritePath(fileName);
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // New file — no merge gate needed
                if (!File.Exists(fullPath))
                {
                    File.WriteAllText(fullPath, fileContent);
                    _fileCache.Store(FileCacheKey(fullPath), fileContent);
                    int newFileLines = fileContent.Split('\n').Length;
                    AppendOutputLocal($"[FILE] Saved {fileNameOnly} ({newFileLines} lines)\n", OutputColor.Success);
                    return fullPath;
                }

                // Existing file — run three-way merge gate
                string currentText = File.ReadAllText(fullPath);
                string baseText = _fileCache.GetFull(FileCacheKey(fullPath));

                MergeCheckResult merge = ThreeWayMergeCheck.CheckAndMerge(baseText, fileContent, currentText);

                if (merge.UsedFallback)
                {
                    Trace.Event("merge_fallback", $"TUI SaveFileAsync: two-way fallback for \"{fileNameOnly}\" — no base cache entry. Overwrite detection only.");
                }

                if (merge.HasConflicts)
                {
                    _pendingConflict = new PendingConflictState
                    {
                        FilePath = fullPath,
                        BaseContent = baseText ?? string.Empty,
                        ProposedContent = fileContent,
                        CurrentContent = currentText,
                        MergeResult = merge,
                        UsedFallback = merge.UsedFallback
                    };

                    AppendOutputLocal($"\n[MERGE CONFLICT] Write to \"{fileNameOnly}\" blocked by merge conflict.\n", OutputColor.Error);
                    for (int i = 0; i < merge.Conflicts.Count; i++)
                    {
                        var c = merge.Conflicts[i];
                        AppendOutputLocal($"  Conflict #{i + 1} at line {c.LineNumber}:\n", OutputColor.Warning);
                        AppendOutputLocal($"    Base:      {ThreeWayMergeCheck.Truncate(c.BaseText, 60)}\n", OutputColor.Dim);
                        AppendOutputLocal($"    Proposed:  {ThreeWayMergeCheck.Truncate(c.ProposedText, 60)}\n", OutputColor.Success);
                        AppendOutputLocal($"    Current:   {ThreeWayMergeCheck.Truncate(c.CurrentText, 60)}\n", OutputColor.Error);
                    }
                    AppendOutputLocal($"  Resolution: type /resolve accept_proposed, /resolve accept_current, or /resolve cancel\n\n", OutputColor.Warning);
                    return null;
                }

                // No conflicts — write the merged text
                string finalContent = merge.MergedText;
                File.WriteAllText(fullPath, finalContent);
                _fileCache.Store(FileCacheKey(fullPath), finalContent);
                int savedLines = finalContent.Split('\n').Length;
                AppendOutputLocal($"[FILE] Saved {fileNameOnly} ({savedLines} lines){(merge.UsedFallback ? " [two-way fallback]" : "")}\n", OutputColor.Success);
                return fullPath;
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[FILE ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return null;
            }
        }

        // ── IAgenticHost.AppendFileAsync ──────────────────────────────────────────

        async Task<string> IAgenticHost.AppendFileAsync(string fileName, string content)
        {
            string fileNameOnly = SafeGetFileName(fileName);

            if (!IsFileKnownToTask(fileNameOnly))
            {
                bool approved = await ConfirmUnreadFileWriteAsync(fileNameOnly);
                if (!approved)
                {
                    AppendOutputLocal($"[WRITE GUARD] File append to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                    return null;
                }
                _taskReadFiles.Add(fileNameOnly);
            }

            // Block if a conflict is pending
            if (_pendingConflict != null)
            {
                AppendOutputLocal($"[MERGE CONFLICT] Cannot append to \"{fileNameOnly}\" — pending conflict on \"{_pendingConflict.FilePath}\" must be resolved first. Use /resolve accept_proposed, /resolve accept_current, or /resolve cancel.\n", OutputColor.Error);
                return null;
            }

            try
            {
                string resolvedPath = FindFile(fileNameOnly, fileName.Replace('\\', '/'))
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileName);

                // New file — no merge gate needed
                if (!File.Exists(resolvedPath))
                {
                    string dir = Path.GetDirectoryName(resolvedPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(resolvedPath, content);
                    _fileCache.Store(FileCacheKey(resolvedPath), content);
                    AppendOutputLocal($"[APPEND] Created {fileNameOnly}\n", OutputColor.Success);
                    return resolvedPath;
                }

                // Existing file — always re-read from disk and refresh cache (requirement #3)
                string currentText = File.ReadAllText(resolvedPath);
                _fileCache.Store(FileCacheKey(resolvedPath), currentText);

                string separator = currentText.Length > 0 && !currentText.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "";
                string proposedText = currentText + separator + content;

                string baseText = _fileCache.GetFull(FileCacheKey(resolvedPath));

                MergeCheckResult merge = ThreeWayMergeCheck.CheckAndMerge(baseText, proposedText, currentText);

                if (merge.UsedFallback)
                {
                    Trace.Event("merge_fallback", $"TUI AppendFileAsync: two-way fallback for \"{fileNameOnly}\" — overwrite detection only.");
                }

                if (merge.HasConflicts)
                {
                    _pendingConflict = new PendingConflictState
                    {
                        FilePath = resolvedPath,
                        BaseContent = baseText ?? string.Empty,
                        ProposedContent = proposedText,
                        CurrentContent = currentText,
                        MergeResult = merge,
                        UsedFallback = merge.UsedFallback
                    };

                    AppendOutputLocal($"\n[MERGE CONFLICT] Append to \"{fileNameOnly}\" blocked by merge conflict.\n", OutputColor.Error);
                    for (int i = 0; i < merge.Conflicts.Count; i++)
                    {
                        var c = merge.Conflicts[i];
                        AppendOutputLocal($"  Conflict #{i + 1} at line {c.LineNumber}:\n", OutputColor.Warning);
                        AppendOutputLocal($"    Base:      {ThreeWayMergeCheck.Truncate(c.BaseText, 60)}\n", OutputColor.Dim);
                        AppendOutputLocal($"    Proposed:  {ThreeWayMergeCheck.Truncate(c.ProposedText, 60)}\n", OutputColor.Success);
                        AppendOutputLocal($"    Current:   {ThreeWayMergeCheck.Truncate(c.CurrentText, 60)}\n", OutputColor.Error);
                    }
                    AppendOutputLocal($"  Resolution: type /resolve accept_proposed, /resolve accept_current, or /resolve cancel\n\n", OutputColor.Warning);
                    return null;
                }

                File.WriteAllText(resolvedPath, merge.MergedText);
                _fileCache.Store(FileCacheKey(resolvedPath), merge.MergedText);
                AppendOutputLocal($"[APPEND] Appended to {fileNameOnly}{(merge.UsedFallback ? " [two-way fallback]" : "")}\n", OutputColor.Success);
                return resolvedPath;
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[APPEND ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return null;
            }
        }

        // ── IAgenticHost.GetWorkingDirectory ──────────────────────────────────────

       string IAgenticHost.GetWorkingDirectory() => _shellRunner.WorkingDirectory;

        /// <summary>Change the working directory (used by /dir slash command).</summary>
        public void SetWorkingDirectory(string dir)
        {
            if (_shellRunner.ChangeDirectory(dir))
            {
                // Also update the memory manager for the new directory.
                if (!string.IsNullOrEmpty(dir))
                    _memoryManager = new MemoryManager(dir);
                // Retarget LSP — disposes the old router; a new one spawns lazily on next use.
                _lspTools.SetWorkingDirectory(dir);
            }
        }

       // ── IAgenticHost scratchpad ──────────────────────────────────────────────

        void IAgenticHost.UpdateScratchpad(string content)
        {
            _taskScratchpad = string.IsNullOrWhiteSpace(content) ? "" : content.Trim();
        }

       string IAgenticHost.TaskScratchpad => TaskScratchpad;

        /// <summary>Gets the current task scratchpad content.</summary>
        public string TaskScratchpad => _taskScratchpad;

        // ── IAgenticHost.DeleteFileAsync ──────────────────────────────────────────

        Task<string> IAgenticHost.DeleteFileAsync(string filename)
        {
            string fileNameOnly = SafeGetFileName(filename);
            string resolvedPath = FindFile(fileNameOnly, filename.Replace('\\', '/'))
                ?? Path.Combine(_shellRunner.WorkingDirectory, filename);

            if (!File.Exists(resolvedPath))
                return Task.FromResult(BuildFileNotFoundMessage("DELETE", filename));

            try
            {
                File.Delete(resolvedPath);
                return Task.FromResult($"Deleted: {resolvedPath}");
            }
            catch (Exception ex)
            {
                return Task.FromResult($"DELETE: failed to delete {resolvedPath} — {ex.Message}");
            }
        }

        // ── IAgenticHost.RenameFileAsync ──────────────────────────────────────────

        Task<string> IAgenticHost.RenameFileAsync(string oldFilename, string newFilename)
        {
            string oldNameOnly = SafeGetFileName(oldFilename);
            string oldPath = FindFile(oldNameOnly, oldFilename.Replace('\\', '/'))
                ?? Path.Combine(_shellRunner.WorkingDirectory, oldFilename);

            if (!File.Exists(oldPath))
                return Task.FromResult(BuildFileNotFoundMessage("RENAME", oldFilename));

            bool newHasDir = newFilename.Contains('/') || newFilename.Contains('\\');
            string newPath = newHasDir
                ? Path.Combine(Path.GetDirectoryName(oldPath) ?? _shellRunner.WorkingDirectory,
                               newFilename.Replace('/', Path.DirectorySeparatorChar))
                : Path.Combine(Path.GetDirectoryName(oldPath) ?? _shellRunner.WorkingDirectory, newFilename);

            if (File.Exists(newPath))
                return Task.FromResult($"RENAME: destination already exists — {newPath}");

            try
            {
                File.Move(oldPath, newPath);
                _fileCache.Invalidate(FileCacheKey(oldPath));
                return Task.FromResult($"Renamed: {oldPath} → {newPath}");
            }
            catch (Exception ex)
            {
                return Task.FromResult($"RENAME: failed to rename {oldPath} → {newPath} — {ex.Message}");
            }
        }

        // ── IAgenticHost.GetPatchBackupCount ──────────────────────────────────────

        int IAgenticHost.GetPatchBackupCount() => _patchBackupStack.Count;

        // ── IAgenticHost.RecallMemoryAsync ────────────────────────────────────────

        Task<string> IAgenticHost.RecallMemoryAsync(string topic)
        {
            if (_memoryManager == null)
                return Task.FromResult("Memory not available: no working directory");

            string content = _memoryManager.LoadTopic(topic);
            if (content == null)
            {
                AppendOutputLocal($"[MEMORY] Topic not found: {topic}\n", OutputColor.Dim);
                return Task.FromResult($"Topic not found: {topic}");
            }

            AppendOutputLocal($"[MEMORY] Recalled: {topic}\n", OutputColor.Dim);
            return Task.FromResult(content);
        }

        // ── IAgenticHost.SaveMemoryAsync ──────────────────────────────────────────

        Task<string> IAgenticHost.SaveMemoryAsync(string topic, string content, string description)
        {
            if (_memoryManager == null)
                return Task.FromResult("Memory not available: no working directory");

            _memoryManager.SaveTopic(topic, content, description);
            string desc = string.IsNullOrEmpty(description) ? topic : description;
            AppendOutputLocal($"[MEMORY] Saved: [{topic}] {desc}\n", OutputColor.Success);
            return Task.FromResult($"Memory saved: [{topic}] {desc}");
        }

        // ── IAgenticHost.ListMemoryTopicsAsync ────────────────────────────────────

        Task<string> IAgenticHost.ListMemoryTopicsAsync()
        {
            if (_memoryManager == null)
                return Task.FromResult("Memory not available: no working directory");

            string index = _memoryManager.LoadIndex();
            if (string.IsNullOrWhiteSpace(index))
            {
                var topics = _memoryManager.ListTopics();
                if (topics.Count == 0)
                {
                    AppendOutputLocal("[MEMORY] No memory topics found.\n", OutputColor.Dim);
                    return Task.FromResult("No memory topics found. Use save_memory to create one.");
                }
                string list = string.Join("\n", topics.Select(t => $"- [{t}]"));
                AppendOutputLocal($"[MEMORY] {topics.Count} topic(s) available.\n", OutputColor.Dim);
                return Task.FromResult(list);
            }

            AppendOutputLocal("[MEMORY] Topics listed.\n", OutputColor.Dim);
            return Task.FromResult(index);
        }

        // ── IAgenticHost LSP tools (delegate to shared Core LspToolService) ───────

        async Task<string> IAgenticHost.GetDiagnosticsAsync(string filename)
        {
            string fullPath = ResolveLspPath(filename);
            if (fullPath == null) return BuildFileNotFoundMessage("get_diagnostics", filename);
            AppendOutputLocal($"[LSP] get_diagnostics {SafeGetFileName(fullPath)}\n", OutputColor.Dim);
            return await _lspTools.GetDiagnosticsAsync(fullPath, CancellationToken);
        }

        async Task<string> IAgenticHost.GoToDefinitionAsync(string filename, int line, int character)
        {
            string fullPath = ResolveLspPath(filename);
            if (fullPath == null) return BuildFileNotFoundMessage("go_to_definition", filename);
            AppendOutputLocal($"[LSP] go_to_definition {SafeGetFileName(fullPath)}:{line}:{character}\n", OutputColor.Dim);
            return await _lspTools.GoToDefinitionAsync(fullPath, line, character, CancellationToken);
        }

        async Task<string> IAgenticHost.FindReferencesAsync(string filename, int line, int character)
        {
            string fullPath = ResolveLspPath(filename);
            if (fullPath == null) return BuildFileNotFoundMessage("find_references", filename);
            AppendOutputLocal($"[LSP] find_references {SafeGetFileName(fullPath)}:{line}:{character}\n", OutputColor.Dim);
            return await _lspTools.FindReferencesAsync(fullPath, line, character, CancellationToken);
        }

        async Task<string> IAgenticHost.HoverAsync(string filename, int line, int character)
        {
            string fullPath = ResolveLspPath(filename);
            if (fullPath == null) return BuildFileNotFoundMessage("hover", filename);
            AppendOutputLocal($"[LSP] hover {SafeGetFileName(fullPath)}:{line}:{character}\n", OutputColor.Dim);
            return await _lspTools.HoverAsync(fullPath, line, character, CancellationToken);
        }

        async Task<string> IAgenticHost.FindSymbolAsync(string query, int maxResults, string language)
        {
            AppendOutputLocal($"[LSP] find_symbol \"{query}\"\n", OutputColor.Dim);
            return await _lspTools.FindSymbolAsync(query, maxResults, language, CancellationToken);
        }

        // ── IAgenticHost web tools (delegate to shared Core WebTools) ─────────────

        async Task<string> IAgenticHost.WebSearchAsync(string query, int? maxResults)
        {
            AppendOutputLocal($"[WEB] search: {query}\n", OutputColor.Dim);
            return await WebTools.WebSearchAsync(query, maxResults, CancellationToken);
        }

      async Task<string> IAgenticHost.WebFetchAsync(string url)
        {
            AppendOutputLocal($"[WEB] fetch: {url}\n", OutputColor.Dim);
            return await WebTools.WebFetchAsync(url, CancellationToken);
        }

        // Last connection that opened successfully this session — sticky reuse so the model
        // doesn't have to re-supply the connection on every stateless run_sql call. Session-scoped
        // (instance field), never a process-static, so it can't leak across DM sessions.
        private string _lastSuccessfulSqlConnectionString;

        /// <summary>The LLM's nearline cache, wired from Program.cs — used by recall_cache. May be null.</summary>
        public NearlineCache NearlineCache { get; set; }

        /// <summary>Max characters of a recalled result returned to the model (mirrors the history cap).</summary>
        private const int MaxRecallChars = 50_000;

        async Task<string> IAgenticHost.RecallCacheAsync(string handle)
        {
            await Task.CompletedTask; // keep signature async; cache access is synchronous

            if (NearlineCache == null)
                return "[recall_cache] nearline cache is not available in this host.";
            if (string.IsNullOrWhiteSpace(handle))
                return "[recall_cache] no handle provided. Pass a handle like \"nl-7\".";

            string key = NearlineCache.GetKeyForHandle(handle);
            if (key == null)
                return $"[recall_cache] unknown handle '{handle}'. It may be from a previous session or never existed.";

            string content = NearlineCache.Retrieve(key);
            if (content == null)
                return $"[recall_cache] content for handle '{handle}' is no longer available (evicted or unreadable).";

            if (content.Length > MaxRecallChars)
            {
                int originalLength = content.Length;
                content = content.Substring(0, MaxRecallChars) + $"\n[truncated — {originalLength} chars]";
            }

            AppendOutputLocal($"[RECALL] {handle} → {key} ({content.Length} chars)\n", OutputColor.Dim);
            return content;
        }

        async Task<string> IAgenticHost.RunSqlAsync(string query, string connectionString, string connectionName, bool allowWrite,
            int maxRows, int commandTimeout)
        {
            // Resolve by precedence (explicit -> named -> session sticky -> cwd appsettings).
            var workingDir = ((IAgenticHost)this).GetWorkingDirectory();
            var namedConnections = TuiConfig.Load().SqlConnections;
            var resolved = SqlExecutor.ResolveConnectionString(
                connectionString, connectionName, namedConnections, _lastSuccessfulSqlConnectionString, workingDir, out var resolveError);
            if (resolved == null)
            {
                AppendOutputLocal($"[SQL ERROR] {resolveError}\n", OutputColor.Error);
                return $"[ERROR] {resolveError}";
            }

            // Mask for logging (never echo the real connection string)
            var masked = SqlExecutor.MaskConnectionString(resolved);
            AppendOutputLocal($"[SQL] executing query (connection: {masked})\n", OutputColor.Dim);

            var result = SqlExecutor.ExecuteQuery(query, resolved, allowWrite, maxRows, commandTimeout, out var connectionOpened);
            if (connectionOpened)
                _lastSuccessfulSqlConnectionString = resolved; // cache the known-good connection for this session

            // Write to file if output is very large
            if (result.Length > 4000)
            {
                var outputDir = Path.Combine(Path.GetTempPath(), "devmind");
                Directory.CreateDirectory(outputDir);
                var outputPath = Path.Combine(outputDir, "dm_sql_output.txt");
                File.WriteAllText(outputPath, result);
                result = $"[SQL] Result too large ({result.Length} chars). Written to: {outputPath}\n{result.Substring(0, Math.Min(200, result.Length))}...\n[See file for full output]";
            }

            AppendOutputLocal($"[SQL] {result}\n", OutputColor.Success);
            return result;
        }

        /// <summary>Resolves an LSP tool's filename argument to an existing full path, or null.</summary>
        private string ResolveLspPath(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return null;
            return FindFile(SafeGetFileName(filename), filename.Replace('\\', '/'));
        }

        // ── IAgenticHost.LoadFileContentAsync ────────────────────────────────────

        Task<string> IAgenticHost.LoadFileContentAsync(
            string fileName, int rangeStart, int rangeEnd, bool forceFullRead)
        {
            if (fileName.StartsWith("git ", StringComparison.OrdinalIgnoreCase))
                return LoadGitContentAsync(fileName, rangeStart);

            return LoadFileContentCoreAsync(fileName, rangeStart, rangeEnd, forceFullRead);
        }

        // ── IAgenticHost.GrepFileAsync ────────────────────────────────────────────

        Task<string> IAgenticHost.GrepFileAsync(string pattern, string filename, int? startLine, int? endLine)
        {
            const int MaxMatches = 50;

            string fileNameOnly = SafeGetFileName(filename);
            string resolvedPath = FindFile(fileNameOnly, filename.Replace('\\', '/'));
            if (resolvedPath == null || !File.Exists(resolvedPath))
                return Task.FromResult(BuildFileNotFoundMessage("GREP", filename));

            string cacheKey = FileCacheKey(resolvedPath);
            _fileCache.InvalidateIfStale(cacheKey, resolvedPath); // out-of-band writes
            if (!_fileCache.Contains(cacheKey))
            {
                string diskContent;
                try { diskContent = File.ReadAllText(resolvedPath); }
                catch (Exception ex) { return Task.FromResult($"GREP: error reading {filename} — {ex.Message}"); }
                _fileCache.Store(cacheKey, diskContent);
            }

            int totalFileLines = _fileCache.GetLineCount(cacheKey);
            int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
            int scanEnd   = endLine.HasValue   ? Math.Min(totalFileLines, endLine.Value) : totalFileLines;

            // Full call parameters + effective window in the transcript line — a bare
            // "0 matches" summary made the job-8 false negative undiagnosable from logs.
            string grepScope = $"[lines {scanStart}-{scanEnd} of {totalFileLines}" +
                $"{(startLine.HasValue || endLine.HasValue ? $", requested start_line={(startLine?.ToString() ?? "-")} end_line={(endLine?.ToString() ?? "-")}" : "")}]";

            var matcher = SearchPattern.BuildMatcher(pattern);
            var matches = new List<(int lineNum, string lineText)>();
            for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
            {
               string lineContent = _fileCache.GetLineRange(cacheKey, lineNum, lineNum);
                if (lineContent == null) continue;
                if (matcher(lineContent))
                    matches.Add((lineNum, lineContent));
            }

            if (matches.Count == 0)
            {
                AppendOutputLocal($"[GREP] no matches for \"{pattern}\" in {filename} {grepScope}\n", OutputColor.Dim);
                return Task.FromResult($"GREP: no matches for \"{pattern}\" in {filename}");
            }

            int totalMatches = matches.Count;
            bool truncated = totalMatches > MaxMatches;
            if (truncated) matches = matches.GetRange(0, MaxMatches);

            int maxLineNum = matches[matches.Count - 1].lineNum;
            int numWidth = maxLineNum.ToString().Length;

            string header = truncated
                ? $"GREP results for \"{pattern}\" in {filename} ({MaxMatches} of {totalMatches} matches — narrow your pattern or use a line range):"
                : $"GREP results for \"{pattern}\" in {filename} ({totalMatches} match{(totalMatches == 1 ? "" : "es")}):";

            var sb = new StringBuilder();
            sb.AppendLine(header);
            foreach (var (lineNum, lineText) in matches)
                sb.AppendLine($"  {lineNum.ToString().PadLeft(numWidth)}: {lineText.TrimEnd()}");

            _taskReadFiles.Add(fileNameOnly);
            AppendOutputLocal($"[GREP] {totalMatches} match{(totalMatches == 1 ? "" : "es")} for \"{pattern}\" in {filename} {grepScope}\n", OutputColor.Success);
            return Task.FromResult(sb.ToString().TrimEnd('\r', '\n'));
        }

        // ── IAgenticHost.FindInFilesAsync ─────────────────────────────────────────

        Task<string> IAgenticHost.FindInFilesAsync(string pattern, string globPattern, int? startLine, int? endLine)
        {
            const int MaxMatches = 100;

            string searchDir = _shellRunner.WorkingDirectory;
            string normalizedGlob = globPattern.Replace('\\', '/');
            string filePattern = normalizedGlob;
            string effectiveRoot = searchDir;
            int lastSlash = normalizedGlob.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                string dirPart = normalizedGlob.Substring(0, lastSlash);
                filePattern = normalizedGlob.Substring(lastSlash + 1);
                string candidate = Path.Combine(searchDir, dirPart.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(candidate)) effectiveRoot = candidate;
            }

            IEnumerable<string> files;
            try
            {
                files = ContextEngine.SafeEnumerateFilesGlob(effectiveRoot, filePattern)
                    .Where(f => !ContextEngine.IsNoisePath(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                return Task.FromResult($"FIND: error enumerating files for {globPattern} — {ex.Message}");
            }

            var findMatcher = SearchPattern.BuildMatcher(pattern);
            var allMatches = new List<(string fileLabel, int lineNum, string lineText)>();
            bool hitCap = false;

            foreach (string filePath in files)
            {
                if (hitCap) break;
                string fileNameOnly = SafeGetFileName(filePath);
                string cacheKey = FileCacheKey(filePath);

                _fileCache.InvalidateIfStale(cacheKey, filePath); // out-of-band writes
                if (!_fileCache.Contains(cacheKey))
                {
                    // Never open cloud/OneDrive placeholders (would download), binaries, or
                    // oversized files for a text search — checks metadata only, no hydration.
                    if (ContextEngine.ShouldSkipForContentSearch(filePath)) continue;

                    string diskContent;
                    try { diskContent = File.ReadAllText(filePath); }
                    catch { continue; }
                    _fileCache.Store(cacheKey, diskContent);
                }

                int totalFileLines = _fileCache.GetLineCount(cacheKey);
                int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
                int scanEnd   = endLine.HasValue   ? Math.Min(totalFileLines, endLine.Value) : totalFileLines;

                for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
                {
                   string lineContent = _fileCache.GetLineRange(cacheKey, lineNum, lineNum);
                    if (lineContent == null) continue;
                    if (findMatcher(lineContent))
                    {
                        allMatches.Add((fileNameOnly, lineNum, lineContent));
                        if (allMatches.Count >= MaxMatches) { hitCap = true; break; }
                    }
                }
            }

            if (allMatches.Count == 0)
            {
                AppendOutputLocal($"[FIND] no matches for \"{pattern}\" in {globPattern}\n", OutputColor.Dim);
                return Task.FromResult($"FIND: no matches for \"{pattern}\" in {globPattern}");
            }

            int shownCount = allMatches.Count;
            string findHeader = hitCap
                ? $"FIND results for \"{pattern}\" in {globPattern} ({MaxMatches}+ matches — narrow your pattern or add a line range):"
                : $"FIND results for \"{pattern}\" in {globPattern} ({shownCount} match{(shownCount == 1 ? "" : "es")}):";

            var sb = new StringBuilder();
            sb.AppendLine(findHeader);
            foreach (var (fileLabel, lineNum, lineText) in allMatches)
                sb.AppendLine($"  {fileLabel}:{lineNum}: {lineText.TrimEnd()}");

            AppendOutputLocal($"[FIND] {(hitCap ? MaxMatches + "+" : shownCount.ToString())} match{(shownCount == 1 ? "" : "es")} for \"{pattern}\" in {globPattern}\n", OutputColor.Success);
            return Task.FromResult(sb.ToString().TrimEnd('\r', '\n'));
        }

        // ── IAgenticHost.ListFilesAsync ───────────────────────────────────────────

        Task<string> IAgenticHost.ListFilesAsync(string glob, bool recursive, CancellationToken cancellationToken)
        {
            const int Cap = 200;

            string searchDir = _shellRunner.WorkingDirectory;
            if (string.IsNullOrEmpty(searchDir))
                return Task.FromResult("[ERROR: working directory not set]");

            string normalizedGlob = (glob ?? "").Replace('\\', '/');
            string filePattern = normalizedGlob;
            string effectiveRoot = searchDir;
            int lastSlash = normalizedGlob.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                string dirPart = normalizedGlob.Substring(0, lastSlash);
                filePattern = normalizedGlob.Substring(lastSlash + 1);
                string candidate = Path.Combine(searchDir, dirPart.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(candidate)) effectiveRoot = candidate;
            }

            if (string.IsNullOrWhiteSpace(filePattern))
                return Task.FromResult("[ERROR: glob pattern is empty]");

            IEnumerable<string> matches;
            try
            {
                matches = recursive
                    ? ContextEngine.SafeEnumerateFilesGlob(effectiveRoot, filePattern).Where(f => !ContextEngine.IsNoisePath(f))
                    : Directory.EnumerateFiles(effectiveRoot, filePattern, SearchOption.TopDirectoryOnly).Where(f => !ContextEngine.IsNoisePath(f));
            }
            catch (Exception ex)
            {
                return Task.FromResult($"[ERROR: {ex.Message}]");
            }

            var sorted = matches
                .Select(Path.GetFullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sorted.Count == 0)
                return Task.FromResult("[no matches]");

            var sb = new StringBuilder();
            int shown = Math.Min(sorted.Count, Cap);
            for (int i = 0; i < shown; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sb.AppendLine(sorted[i]);
            }
            if (sorted.Count > Cap)
                sb.AppendLine($"[truncated — {sorted.Count - Cap} more matches]");

            AppendOutputLocal($"[LIST] {shown} file{(shown == 1 ? "" : "s")} matching \"{glob}\"\n", OutputColor.Dim);
            return Task.FromResult(sb.ToString().TrimEnd());
        }

        // ── IAgenticHost.RunTestsAsync ────────────────────────────────────────────

       async Task<string> IAgenticHost.RunTestsAsync(string project, string filter, int? timeoutSeconds)
        {
            if (string.IsNullOrWhiteSpace(project))
            {
                try
                {
                    string[] csprojFiles = Directory.GetFiles(_shellRunner.WorkingDirectory, "*.csproj",
                        SearchOption.TopDirectoryOnly);
                    if (csprojFiles.Length == 1)
                    {
                        project = csprojFiles[0];
                        AppendOutputLocal($"[TEST] Auto-detected project: {Path.GetFileName(project)}\n", OutputColor.Dim);
                    }
                    else if (csprojFiles.Length > 1)
                    {
                        project = csprojFiles[0];
                        AppendOutputLocal($"[TEST] Multiple .csproj files found — using {Path.GetFileName(project)}\n", OutputColor.Dim);
                    }
                    else return "[TEST] No project specified and no .csproj found in working directory.";
                }
                catch { return "[TEST] No project specified."; }
            }

            bool looksLikeBare = !project.Contains('/') && !project.Contains('\\');
            if (looksLikeBare && !string.IsNullOrEmpty(_shellRunner.WorkingDirectory))
            {
                string searchName = project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    ? project : project + ".csproj";
                try
                {
                    string[] found = Directory.GetFiles(_shellRunner.WorkingDirectory, searchName,
                        SearchOption.AllDirectories);
                    if (found.Length > 0) project = found[0];
                }
                catch { }
            }

            string filterArg = !string.IsNullOrWhiteSpace(filter) ? $" --filter \"{filter.Trim('\"')}\"" : "";
            string quotedProject = project.Contains(' ') ? $"\"{project}\"" : project;
            string cmd = $"dotnet test {quotedProject} --no-build --verbosity normal{filterArg}";

            AppendOutputLocal($"[TEST] > {cmd}\n", OutputColor.Dim);

            try
            {
                var (output, exitCode) = await _shellRunner.ExecuteAsync(cmd, CancellationToken, timeoutSeconds);
                return string.IsNullOrWhiteSpace(output)
                    ? $"TEST: no output (exit code {exitCode})"
                    : output;
            }
            catch (Exception ex)
            {
                return $"[TEST] Failed to run tests: {ex.Message}";
            }
        }

        // ── IAgenticHost.GetFileDiffAsync ─────────────────────────────────────────

        Task<string> IAgenticHost.GetFileDiffAsync(string filename)
        {
            string fileNameOnly = SafeGetFileName(filename);
            string resolvedPath = FindFile(fileNameOnly, filename.Replace('\\', '/'))
                ?? Path.Combine(_shellRunner.WorkingDirectory, filename);

            if (!_fileSnapshots.ContainsKey(resolvedPath))
            {
                AppendOutputLocal($"[DIFF] {filename}: not modified this session\n", OutputColor.Dim);
                return Task.FromResult($"DIFF: No changes — {filename} has not been modified this session.");
            }

            string original = _fileSnapshots[resolvedPath];
            _fileSnapshotUse[resolvedPath] = ++_fileSnapshotUseCounter; // touch for LRU
            string current;
            try { current = File.ReadAllText(resolvedPath); }
            catch (Exception ex) { return Task.FromResult($"DIFF: error reading {filename} — {ex.Message}"); }

            string normOld = original.Replace("\r\n", "\n").Replace("\r", "\n");
            string normNew = current.Replace("\r\n", "\n").Replace("\r", "\n");

            if (string.Equals(normOld, normNew, StringComparison.Ordinal))
            {
                AppendOutputLocal($"[DIFF] {filename}: no changes\n", OutputColor.Dim);
                return Task.FromResult($"DIFF: No changes detected in {filename}.");
            }

            string[] oldLines = normOld.Split('\n');
            string[] newLines = normNew.Split('\n');
            string diffResult = DiffHelper.GenerateUnifiedDiff(filename, oldLines, newLines);

            AppendOutputLocal($"[DIFF] {filename}: changes shown ({oldLines.Length} → {newLines.Length} lines)\n", OutputColor.Dim);
            return Task.FromResult(diffResult);
        }

        // ── IAgenticHost.ResolvePatchAsync ────────────────────────────────────────

        async Task<PatchResolveResult> IAgenticHost.ResolvePatchAsync(string patchContent, bool fromToolCall)
        {
            try
            {
                string firstLine = (patchContent ?? string.Empty).Split('\n')[0];
                string blockFileName = firstLine.Length > 5 ? firstLine.Substring(5).Trim() : string.Empty;

                if (string.IsNullOrEmpty(blockFileName))
                {
                    AppendOutputLocal("[PATCH] No filename specified.\n", OutputColor.Error);
                    return null;
                }

                string normalizedHint = blockFileName.Replace('\\', '/');
                string fileNameOnly   = SafeGetFileName(blockFileName);

                if (!IsFileKnownToTask(fileNameOnly))
                {
                    bool approved = await ConfirmUnreadFileWriteAsync(fileNameOnly);
                    if (!approved)
                    {
                        AppendOutputLocal($"[WRITE GUARD] Patch to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                        return null;
                    }
                    _taskReadFiles.Add(fileNameOnly);
                }

                string fullPath = FindFile(fileNameOnly, normalizedHint)
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileNameOnly);

                if (!File.Exists(fullPath))
                {
                    AppendOutputLocal($"[PATCH] File not found: {fullPath}\n", OutputColor.Warning);
                    return null;
                }

                _fileCache.InvalidateIfStale(FileCacheKey(fullPath), fullPath); // out-of-band writes
                if (!_fileCache.Contains(FileCacheKey(fullPath)))
                {
                    AppendOutputLocal($"[AUTO-READ] Loading {fileNameOnly} before patch...\n", OutputColor.Dim);
                    var (cached, _enc) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                    _fileCache.Store(FileCacheKey(fullPath), cached);
                    _filesRead.Add(fileNameOnly);
                    _taskReadFiles.Add(fileNameOnly);
                }

                CaptureFileSnapshot(fullPath);

                var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                return PatchEngine.ResolvePatch(patchContent, fullPath, blockFileName, content, encoding,
                    fromToolCall, (text, color) => AppendOutputLocal(text, color));
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return null;
            }
        }

        // ── IAgenticHost.ApplyResolvedPatchAsync ──────────────────────────────────

        Task<string> IAgenticHost.ApplyResolvedPatchAsync(PatchResolveResult resolved)
        {
            try
            {
                // Block if a conflict is pending
                if (_pendingConflict != null)
                {
                    AppendOutputLocal($"[MERGE CONFLICT] Cannot apply patch — pending conflict on \"{_pendingConflict.FilePath}\" must be resolved first. Use /resolve accept_proposed, /resolve accept_current, or /resolve cancel.\n", OutputColor.Error);
                    return Task.FromResult<string>(null);
                }

                string fileNameOnly = SafeGetFileName(resolved.FullPath);
                string currentText = File.ReadAllText(resolved.FullPath);
                string baseText = _fileCache.GetFull(FileCacheKey(resolved.FullPath));
                string proposedText = ComputePatchedContent(resolved);

                MergeCheckResult merge = ThreeWayMergeCheck.CheckAndMerge(baseText, proposedText, currentText);

                if (merge.UsedFallback)
                {
                    Trace.Event("merge_fallback", $"TUI ApplyResolvedPatchAsync: two-way fallback for \"{fileNameOnly}\" — overwrite detection only.");
                }

                if (merge.HasConflicts)
                {
                    _pendingConflict = new PendingConflictState
                    {
                        FilePath = resolved.FullPath,
                        BaseContent = baseText ?? string.Empty,
                        ProposedContent = proposedText,
                        CurrentContent = currentText,
                        MergeResult = merge,
                        UsedFallback = merge.UsedFallback
                    };

                    AppendOutputLocal($"\n[MERGE CONFLICT] Patch to \"{fileNameOnly}\" blocked by merge conflict.\n", OutputColor.Error);
                    for (int i = 0; i < merge.Conflicts.Count; i++)
                    {
                        var c = merge.Conflicts[i];
                        AppendOutputLocal($"  Conflict #{i + 1} at line {c.LineNumber}:\n", OutputColor.Warning);
                        AppendOutputLocal($"    Base:      {ThreeWayMergeCheck.Truncate(c.BaseText, 60)}\n", OutputColor.Dim);
                        AppendOutputLocal($"    Proposed:  {ThreeWayMergeCheck.Truncate(c.ProposedText, 60)}\n", OutputColor.Success);
                        AppendOutputLocal($"    Current:   {ThreeWayMergeCheck.Truncate(c.CurrentText, 60)}\n", OutputColor.Error);
                    }
                    AppendOutputLocal($"  Resolution: type /resolve accept_proposed, /resolve accept_current, or /resolve cancel\n\n", OutputColor.Warning);
                    return Task.FromResult<string>(null);
                }

                // No conflicts — apply patch to disk
                string backupDir = Path.Combine(Path.GetTempPath(), "DevMind");
                var result = PatchEngine.ApplyPatch(resolved, backupDir);

                if (!result.Success)
                {
                    AppendOutputLocal($"[PATCH] Error: {result.Error}\n", OutputColor.Error);
                    return Task.FromResult<string>(null);
                }

                if (result.BackupPath != null)
                {
                    if (_patchBackupStack.Count >= PatchBackupStackLimit)
                    {
                        var entries = _patchBackupStack.ToArray();
                        var oldest  = entries[entries.Length - 1];
                        try { File.Delete(oldest.backupPath); } catch { }
                        _patchBackupStack.Clear();
                        for (int i = entries.Length - 2; i >= 0; i--)
                            _patchBackupStack.Push(entries[i]);
                    }
                    _patchBackupStack.Push((resolved.FullPath, result.BackupPath));
                }

                _fileCache.Store(FileCacheKey(resolved.FullPath), result.UpdatedContent);

                int undosAvailable = _patchBackupStack.Count;
                AppendOutputLocal($"[PATCH] Applied to {resolved.FullPath} (undo depth: {undosAvailable}){(merge.UsedFallback ? " [two-way fallback]" : "")}\n",
                    OutputColor.Success);

                // Show what changed as a colored unified diff
                AppendPatchDiff(resolved.OriginalContent, result.UpdatedContent, fileNameOnly);

                return Task.FromResult(resolved.FullPath);
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return Task.FromResult<string>(null);
            }
        }

        /// <summary>
        /// Resolves a pending merge conflict. Call from /resolve slash command handler.
        /// </summary>
        public string ResolvePendingConflict(string choice)
        {
            if (_pendingConflict == null)
                return "[MERGE] No pending conflict to resolve.";

            var pc = _pendingConflict;

            if (choice == "cancel")
            {
                _pendingConflict = null;
                return "[MERGE] Conflict cancelled — pending patch discarded.";
            }

            if (choice == "accept_proposed")
            {
                try
                {
                    File.WriteAllText(pc.FilePath, pc.ProposedContent);
                    string fileNameOnly = SafeGetFileName(pc.FilePath);
                    _fileCache.Store(FileCacheKey(pc.FilePath), pc.ProposedContent);
                    _pendingConflict = null;
                    // Output is rendered once by the /resolve dispatcher from the returned message.
                    return $"[MERGE] Accepted proposed content for {fileNameOnly}";
                }
                catch (Exception ex)
                {
                    return $"[MERGE ERROR] Failed to write: {ex.Message}";
                }
            }

            if (choice == "accept_current")
            {
                _pendingConflict = null;
                // Output is rendered once by the /resolve dispatcher from the returned message.
                return $"[MERGE] Kept current content — change discarded.";
            }

            return "[MERGE] Unknown choice. Usage: /resolve accept_proposed | accept_current | cancel";
        }

        // Render a unified diff with per-line color: − removed (red), + added (green),
        // @@ hunk header (blue), context (dim). Capped so a large edit can't flood.
        private const int MaxPatchDiffLines = 80;
        private void AppendPatchDiff(string oldContent, string newContent, string fileName)
        {
            try
            {
                string[] oldLines = (oldContent ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                string[] newLines = (newContent ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                string diff = DiffHelper.GenerateUnifiedDiff(fileName, oldLines, newLines);
                if (string.IsNullOrWhiteSpace(diff)) return;

                string[] lines = diff.Replace("\r\n", "\n").Split('\n');
                int shown = 0;
                foreach (string line in lines)
                {
                    // The [PATCH] line already names the file — drop the diff's file headers.
                    if (line.StartsWith("---", StringComparison.Ordinal) ||
                        line.StartsWith("+++", StringComparison.Ordinal))
                        continue;

                    if (shown >= MaxPatchDiffLines)
                    {
                        AppendOutputLocal($"  … ({lines.Length - shown} more diff lines)\n", OutputColor.Dim);
                        break;
                    }

                    OutputColor color;
                    if (line.StartsWith("@@", StringComparison.Ordinal)) color = OutputColor.Input;   // hunk header
                    else if (line.StartsWith("+", StringComparison.Ordinal)) color = OutputColor.Success; // added
                    else if (line.StartsWith("-", StringComparison.Ordinal)) color = OutputColor.Error;   // removed
                    else color = OutputColor.Dim;                                                          // context
                    AppendOutputLocal(line + "\n", color);
                    shown++;
                }
            }
            catch { /* diff display is best-effort — never break a successful patch */ }
        }

        // ── IAgenticHost.ShowDiffPreviewAsync ─────────────────────────────────────
        // Auto-approve all patches. No interactive y/n/a/q prompt in TUI.

        Task<List<int>> IAgenticHost.ShowDiffPreviewAsync(
            List<PatchResolveResult> resolvedPatches, CancellationToken cancellationToken)
        {
            var approved = new List<int>();

            for (int i = 0; i < resolvedPatches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var r = resolvedPatches[i];
                string badge = r.Confidence == PatchConfidence.Fuzzy ? " [Fuzzy ⚠]" : " [Exact ✓]";

                AppendOutputLocal($"\n[PATCH] {r.FileName}{badge}\n", OutputColor.Dim);
                string patched  = ComputePatchedContent(r);
                string[] oldLns = r.OriginalContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                string[] newLns = patched.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                AppendOutputLocal(DiffHelper.GenerateUnifiedDiff(r.FileName, oldLns, newLns) + "\n",
                    OutputColor.Normal);

                // Auto-approve — no interactive prompt in TUI.
                approved.Add(i);
                AppendOutputLocal($"[PATCH] Auto-approved ({i + 1}/{resolvedPatches.Count})\n", OutputColor.Dim);
            }

            return Task.FromResult(approved);
        }

        // ── Private helpers ───────────────────────────────────────────────────────

       internal void AppendOutputLocal(string text, OutputColor color)
        {
            ((IAgenticHost)this).AppendOutput(text, color);
        }

        // ── Color mapping (Terminal.Gui 2.4.4) ────────────────────────────────────
        // Foreground RGB per OutputColor, matching the hex values documented on the
        // OutputColor enum. Background is taken from the view's own Scheme — Program.cs
        // pins the output view's Normal/Editable roles to black, so stamped cells render
        // on the same black background as unstamped cells and fill areas.
        private Terminal.Gui.Drawing.Attribute ResolveAttribute(OutputColor color)
        {
            Terminal.Gui.Drawing.Color fg;
            switch (color)
            {
                case OutputColor.Dim:      fg = new Terminal.Gui.Drawing.Color(0x88, 0x88, 0x88); break; // #888888
                case OutputColor.Input:    fg = new Terminal.Gui.Drawing.Color(0x56, 0x9C, 0xD6); break; // #569CD6
                case OutputColor.Error:    fg = new Terminal.Gui.Drawing.Color(0xF4, 0x47, 0x47); break; // #F44747
                case OutputColor.Success:  fg = new Terminal.Gui.Drawing.Color(0x4E, 0xC9, 0x4E); break; // #4EC94E
                case OutputColor.Thinking: fg = new Terminal.Gui.Drawing.Color(0x6A, 0x6A, 0x8A); break; // #6A6A8A
                case OutputColor.Warning:  fg = new Terminal.Gui.Drawing.Color(0xFF, 0xB9, 0x00); break; // #FFB900
                case OutputColor.Normal:
                default:                   fg = new Terminal.Gui.Drawing.Color(0xCC, 0xCC, 0xCC); break; // #CCCCCC
            }

            Terminal.Gui.Drawing.Color bg = _outputView.GetScheme().Normal.Background;
            return new Terminal.Gui.Drawing.Attribute(fg, bg);
        }

        // ── Color model ───────────────────────────────────────────────────────────

        // One recorded append: the document offset range it occupies and the Attribute to
        // paint it with. Spans are append-only and strictly increasing in Start (every insert
        // lands at the document end), so they form a sorted, contiguous cover of [0, TextLength).
        private readonly struct ColorSpan
        {
            public readonly int Start;
            public readonly int Length;
            public readonly Terminal.Gui.Drawing.Attribute Attr;

            public ColorSpan(int start, int length, Terminal.Gui.Drawing.Attribute attr)
            {
                Start  = start;
                Length = length;
                Attr   = attr;
            }
        }

        // Applies recorded color spans to visual-line elements at draw time. Registered on
        // Editor.LineTransformers; Editor calls Transform(line) for each DocumentLine as it
        // builds the line's elements (VisualLineBuilder emits one element per grapheme, so a
        // color boundary can fall between any two characters with zero bleed). For each element
        // we look up the span covering its DocumentOffset and set element.Attribute.
        //
        // Spans are sorted by Start, so we binary-search the span covering the line's first
        // element, then advance a cursor as element offsets increase — O(elements + spans-in-
        // line) per line, only for VISIBLE lines. Runs on the UI thread (Draw), same thread as
        // InsertSpan's writes, so reading the shared list needs no lock.
        private sealed class OffsetColorTransformer : IVisualLineTransformer
        {
            private readonly List<ColorSpan> _spans;

            public OffsetColorTransformer(List<ColorSpan> spans) => _spans = spans;

            public void Transform(CellVisualLine line)
            {
                IReadOnlyList<CellVisualLineElement> elements = line.Elements;
                int count = elements.Count;
                int spanCount = _spans.Count;
                if (count == 0 || spanCount == 0) return;

                int idx = FindSpanIndex(elements[0].DocumentOffset);
                for (int e = 0; e < count; e++)
                {
                    CellVisualLineElement el = elements[e];
                    int off = el.DocumentOffset;
                    // Advance past spans that end at/before this offset.
                    while (idx < spanCount && off >= _spans[idx].Start + _spans[idx].Length)
                        idx++;
                    if (idx >= spanCount) break;
                    if (off >= _spans[idx].Start)
                        el.Attribute = _spans[idx].Attr;
                }
            }

            // First span whose end (Start+Length) is strictly greater than offset.
            private int FindSpanIndex(int offset)
            {
                int lo = 0, hi = _spans.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (_spans[mid].Start + _spans[mid].Length <= offset) lo = mid + 1;
                    else hi = mid;
                }
                return lo;
            }
        }

        private bool IsFileKnownToTask(string fileNameOnly)
            => _taskReadFiles.Contains(fileNameOnly) || _taskReadFiles.Count == 0;

        private Task<bool> ConfirmUnreadFileWriteAsync(string fileNameOnly)
        {
            // Auto-approve all writes — no interactive prompt.
            return Task.FromResult(true);
        }

        private string ResolveWritePath(string fileName)
        {
            if (Path.IsPathRooted(fileName)) return fileName;
            return Path.Combine(_shellRunner.WorkingDirectory ?? Directory.GetCurrentDirectory(), fileName);
        }

        private string FindFile(string fileNameOnly, string hintPath)
        {
            if (Path.IsPathRooted(hintPath) && File.Exists(hintPath)) return hintPath;

            if (!string.IsNullOrEmpty(_shellRunner.WorkingDirectory))
            {
                string byHint = Path.Combine(_shellRunner.WorkingDirectory,
                    hintPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(byHint)) return byHint;

                string byName = Path.Combine(_shellRunner.WorkingDirectory, fileNameOnly);
                if (File.Exists(byName)) return byName;

                try
                {
                    string[] found = Directory.GetFiles(_shellRunner.WorkingDirectory, fileNameOnly,
                        SearchOption.AllDirectories);
                    string[] clean = found.Where(f => !ContextEngine.IsNoisePath(f)).ToArray();
                    if (clean.Length == 1) return clean[0];
                    if (clean.Length > 1)
                    {
                        string normalized = hintPath.Replace('\\', '/');
                        string best = clean.FirstOrDefault(f =>
                            f.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
                        return best ?? clean[0];
                    }
                }
                catch { }
            }

            return null;
        }

        private string BuildFileNotFoundMessage(string directive, string filename)
        {
            const int MaxFiles = 50;
            string searchDir = _shellRunner.WorkingDirectory;

            List<string> csFiles = null;
            try
            {
                csFiles = Directory.GetFiles(searchDir, "*.cs", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { }

            if (csFiles == null || csFiles.Count == 0)
                return $"{directive}: file not found — {filename}";

            var sb = new StringBuilder();
            sb.AppendLine($"{directive}: file not found — {filename}");
            sb.AppendLine("Project files:");
            int shown = Math.Min(csFiles.Count, MaxFiles);
            for (int i = 0; i < shown; i++) sb.AppendLine($"  {csFiles[i]}");
            if (csFiles.Count > MaxFiles) sb.AppendLine($"  ... and {csFiles.Count - MaxFiles} more");
            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static string ComputePatchedContent(PatchResolveResult r)
        {
            var blocks = r.ResolvedBlocks
                .OrderByDescending(b => b.origStart)
                .ToList();
            string updated = r.OriginalContent;
            foreach (var (origStart, origEnd, finalReplace) in blocks)
                updated = updated.Substring(0, origStart) + finalReplace + updated.Substring(origEnd);
            return updated;
        }

        private void CaptureFileSnapshot(string fullPath)
        {
            // Snapshot is the ORIGINAL pre-edit content — never overwrite an existing one, just touch
            // its LRU stamp so it is not evicted ahead of colder entries.
            if (_fileSnapshots.ContainsKey(fullPath))
            {
                _fileSnapshotUse[fullPath] = ++_fileSnapshotUseCounter;
                return;
            }
            try
            {
                string content = File.ReadAllText(fullPath);
                if (_fileSnapshots.Count >= MaxFileSnapshots)
                    EvictLruFileSnapshot();
                _fileSnapshots[fullPath] = content;
                _fileSnapshotUse[fullPath] = ++_fileSnapshotUseCounter;
            }
            catch { }
        }

        private void EvictLruFileSnapshot()
        {
            string lru = null;
            long min = long.MaxValue;
            foreach (var kvp in _fileSnapshotUse)
            {
                if (kvp.Value < min) { min = kvp.Value; lru = kvp.Key; }
            }
            if (lru != null)
            {
                _fileSnapshots.Remove(lru);
                _fileSnapshotUse.Remove(lru);
            }
        }

        private static string SafeGetFileName(string path)
        {
            try { return Path.GetFileName(path.Replace('\\', '/')); }
            catch { return path; }
        }

        /// <summary>
        /// Canonical <see cref="_fileCache"/> key: the FULL path, never the bare file
        /// name. Repos routinely hold many same-named files (Program.cs); bare-name keys
        /// let a FIND scan of one Program.cs poison GREP/READ/merge-base of every other
        /// one (the job-8 false-negative postmortem — same fix as BufferedAgenticHost).
        /// </summary>
        private static string FileCacheKey(string fullPath)
        {
            try { return Path.GetFullPath(fullPath); }
            catch { return fullPath; }
        }

        // ── Private async helpers ─────────────────────────────────────────────────

        private async Task<string> LoadFileContentCoreAsync(
            string fileName, int rangeStart, int rangeEnd, bool forceFullRead)
        {
            try
            {
                string fileNameOnly = SafeGetFileName(fileName);
                string fullPath = FindFile(fileNameOnly, fileName.Replace('\\', '/'));

                if (fullPath == null || !File.Exists(fullPath))
                {
                    AppendOutputLocal($"[READ] File not found: {fileName}\n", OutputColor.Warning);
                    return BuildFileNotFoundMessage("READ", fileName);
                }

                CaptureFileSnapshot(fullPath);

                if (rangeStart > 0)
                {
                    string cacheKey = FileCacheKey(fullPath);
                    _fileCache.InvalidateIfStale(cacheKey, fullPath); // out-of-band writes
                    if (!_fileCache.Contains(cacheKey))
                    {
                        var (diskContent, _) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                        _fileCache.Store(cacheKey, diskContent);
                    }

                    _taskReadFiles.Add(fileNameOnly);
                    int totalLines = _fileCache.GetLineCount(cacheKey);

                    if (rangeStart > rangeEnd) { int t = rangeStart; rangeStart = rangeEnd; rangeEnd = t; }
                    int clampedEnd   = Math.Min(rangeEnd,   totalLines);
                    int clampedStart = Math.Max(1, rangeStart);

                    string rangeContent = _fileCache.GetLineRange(cacheKey, clampedStart, clampedEnd);
                    if (rangeContent == null)
                    {
                        AppendOutputLocal($"[READ] Range {rangeStart}-{rangeEnd} out of bounds for {fileNameOnly} ({totalLines} lines)\n", OutputColor.Error);
                        return $"[READ] Range {rangeStart}-{rangeEnd} out of bounds for {fileNameOnly} ({totalLines} lines)";
                    }

                    var rawLines = rangeContent.Split('\n');
                    var numbered = new StringBuilder();
                    for (int i = 0; i < rawLines.Length; i++)
                        numbered.AppendLine($"{clampedStart + i}: {rawLines[i].TrimEnd('\r')}");

                    bool clamped = clampedEnd < rangeEnd;
                    string rangeBlock = ContextEngine.RenderReadRangeBlock(
                        fileNameOnly, clampedStart, clampedEnd, totalLines, numbered.ToString(), clamped);

                    AppendOutputLocal(
                        $"[READ] {fileNameOnly}:{clampedStart}-{clampedEnd} ({clampedEnd - clampedStart + 1} lines){(clamped ? " [clamped]" : "")}\n",
                        OutputColor.Success);

                    // Show the requested lines syntax-highlighted (same as a full read).
                    AppendHighlightedListing(rangeContent, fullPath, clampedEnd - clampedStart + 1);

                    return rangeBlock;
                }

                var (content, _enc) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                _fileCache.Store(FileCacheKey(fullPath), content);
                _taskReadFiles.Add(fileNameOnly);
                int lineCount = content.Split('\n').Length;

                bool alreadyRead = _filesRead.Contains(fileNameOnly);
                _filesRead.Add(fileNameOnly);

                string rendered = ContextEngine.RenderReadBlock(
                    fileNameOnly, content, lineCount, forceFullRead, alreadyRead, out bool wasOutline);

                AppendOutputLocal(wasOutline
                    ? $"[READ] {fullPath} ({lineCount} lines — outline{(alreadyRead ? ", re-read" : "")})\n"
                    : $"[READ] Loaded {fullPath} ({lineCount} lines)\n",
                    OutputColor.Success);

                // List the source syntax-highlighted (DevMindShell-style). Full reads only —
                // outline re-reads stay terse so agentic loops don't flood the transcript.
                if (!wasOutline)
                    AppendHighlightedListing(content, fullPath, lineCount);

                return rendered;
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[READ ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return $"[ERROR reading {fileName}: {ex.Message}]";
            }
        }

        private async Task<string> LoadGitContentAsync(string fileName, int rangeStart)
        {
            string gitRoot = FindGitRoot();
            if (gitRoot == null)
            {
                AppendOutputLocal("[READ] git: not a git repository\n", OutputColor.Error);
                return "[READ] git: not a git repository\n";
            }

            string command, header;

            if (fileName.StartsWith("git log", StringComparison.OrdinalIgnoreCase))
            {
                int count;
                if (rangeStart > 0)
                {
                    count = rangeStart;
                }
                else
                {
                    string countPart = fileName.Substring("git log".Length).Trim();
                    count = 10;
                    if (!string.IsNullOrEmpty(countPart)) int.TryParse(countPart, out count);
                }
                count = Math.Max(1, Math.Min(count, 50));
                command = $"git log --oneline --no-decorate -{count}";
                header  = $"[READ] git log (last {count} commits)";
            }
            else if (fileName.StartsWith("git diff", StringComparison.OrdinalIgnoreCase))
            {
                string diffArgs = fileName.Substring("git diff".Length).Trim();
                command = string.IsNullOrEmpty(diffArgs) ? "git diff" : $"git diff {diffArgs}";
                header  = string.IsNullOrEmpty(diffArgs) ? "[READ] git diff (working changes)" : $"[READ] git diff {diffArgs}";
            }
            else
            {
                string errMsg = $"[READ] Unrecognized git command: {fileName}";
                AppendOutputLocal(errMsg + "\n", OutputColor.Error);
                return errMsg + "\n";
            }

            string savedDir = _shellRunner.WorkingDirectory;
            _shellRunner.ChangeDirectory(gitRoot);
            string output;
            int exitCode;
            try
            {
                (output, exitCode) = await _shellRunner.ExecuteAsync(command, CancellationToken);
            }
            finally
            {
                _shellRunner.ChangeDirectory(savedDir);
            }

            if (exitCode != 0)
            {
                string errMsg = $"{header}\n(error — exit code {exitCode})\n{output}\n";
                AppendOutputLocal(errMsg, OutputColor.Error);
                return errMsg;
            }

            const int MaxDiffLines = 500;
            string[] outputLines = output.Split('\n');
            string truncatedOutput;
            if (outputLines.Length > MaxDiffLines)
            {
                int omitted = outputLines.Length - MaxDiffLines;
                truncatedOutput = string.Join("\n", outputLines.Take(MaxDiffLines))
                    + $"\n[... {omitted} lines omitted — use READ git diff <filename> for specific files]";
            }
            else
            {
                truncatedOutput = output;
            }

            if (string.IsNullOrWhiteSpace(truncatedOutput)) truncatedOutput = "(no output)";

            AppendOutputLocal($"{header}\n", OutputColor.Success);
            return $"{header}\n```\n{truncatedOutput}\n```\n\n";
        }

        private string FindGitRoot()
        {
            string dir = _shellRunner.WorkingDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
                string parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            return null;
        }
    }
}
