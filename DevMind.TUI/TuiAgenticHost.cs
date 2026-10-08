// File: TuiAgenticHost.cs  v3.2
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
        private readonly TaskReadSet _taskReadFiles = new TaskReadSet();

        // diff_file baselines — the shared bounded LRU store (FileSnapshotStore).
        private readonly FileSnapshotStore _fileSnapshots = new FileSnapshotStore();

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
        private readonly ColorSpanList _colorSpans = new ColorSpanList();

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
        // ── Live tail line ───────────────────────────────────────────────────────
        // The one piece of transcript text that is rewritten rather than appended: the
        // "∵ Thinking… 11s" line that ticks while the model reasons. Without it the
        // transcript is silent for the whole reasoning phase and a long think is
        // indistinguishable from a hang.
        //
        // The rewrite is safe because of where it is done, not because the document is
        // forgiving. The desired text is held here; the render pump — already the only
        // writer, already on the UI thread — removes whatever tail it rendered last, inserts
        // the batch, and re-appends the tail. So the live line is ALWAYS the document's last
        // characters and its span is ALWAYS the last span: removing it is a pop, not
        // offset surgery, and no ordering can put an append behind it. While the user is
        // pinned to scrollback the pump does not run and the tail simply stops ticking,
        // which is exactly what a frozen document should do.
        private string _liveTail;          // desired text (null = none); guarded by _pendingLock
        private int _liveTailRendered;     // chars of it currently in the document; UI thread only

        private readonly object _pendingLock = new object();
        private readonly List<(string text, Terminal.Gui.Drawing.Attribute attr, bool nonCopyable)> _pending =
            new List<(string, Terminal.Gui.Drawing.Attribute, bool)>();
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

        // The in-flight run_shell / run_build / run_tests call's interrupt window (shared with
        // BufferedAgenticHost). CancelInFlightShell runs on the UI thread when the operator
        // submits /override; the call runs on the turn's worker thread.
        private readonly ShellCallInterrupt _shellInterrupt =
            new ShellCallInterrupt(ShellCallInterrupt.OperatorSteerMessage);

        /// <summary>Transcript line after a call an override steer cancelled.</summary>
        internal const string OverrideSteerCancelledLine = "↳ override steer cancelled the running command";

        /// <summary>
        /// Cancels the in-flight run_shell / run_build / run_tests call, if there is one, so
        /// the turn reaches its next iteration boundary now and folds the operator's override
        /// instead of waiting out the command. Only the call — never the turn token, so Esc and
        /// Ctrl+C are unaffected. Returns whether a call was cancelled. Thread-safe.
        /// </summary>
        public bool CancelInFlightShell(string reason) => _shellInterrupt.Cancel(reason);

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
            _fileCache.InvalidateAll();
           _taskReadFiles.Clear();
            _taskScratchpad = "";
            _pendingConflict = null;
            CleanupDap();
            _pendingBreaks.Reset();
            DrainPatchBackups();
            Expansions.Clear();

            // The retained source goes with the session. Without this a /new would keep
            // re-rendering the previous conversation on every resize.
            _model.Clear(); _droppedSinceRender = 0;
            Renderer.Reset();
        }

        /// <summary>
        /// Removes every PATCH backup from the undo stack, deleting each backup
        /// file as it goes. Session-scoped: backups are undo state, and a new
        /// session has no business undoing the last one's patches.
        /// </summary>
        public void DrainPatchBackups()
        {
            while (_patchBackupStack.Count > 0)
            {
                var (_, backupPath) = _patchBackupStack.Pop();
                try { File.Delete(backupPath); } catch { /* a locked or already-deleted backup must not abort the drain */ }
            }
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

            // Retained, then drawn. Every emitter below is now "record the call, render the
            // one entry" — so the live path and a rebuild at a new width are the same code
            // given one entry or all of them, and cannot drift apart at a width nobody tried.
            Record(TranscriptEntry.Output(text, color));
        }

        // ── The retained transcript ───────────────────────────────────────────────

        private readonly TranscriptModel _model = new TranscriptModel();

        /// <summary>
        /// Keep an entry and draw it. The trim is the model's now: the document is a
        /// projection of what survives, so bounding the source bounds the document.
        /// </summary>
        private void Record(TranscriptEntry entry)
        {
            _model.Append(entry);
            int dropped = _model.Trim(MaxDocChars, KeepDocChars);
            if (dropped > 0)
            {
                // The oldest entries went; what is painted no longer matches the model, and
                // only a rebuild can make it match again. Outside a turn that is now, on the
                // UI thread; inside one it waits for the turn, like a resize does.
                _droppedSinceRender += dropped;
                ScheduleRebuild();
                return;
            }

            Renderer.Render(entry);
        }

        // Entries dropped from the front of the model since the document was last rendered
        // from it. The scroll anchor is an index into the rendered list, so it shifts by this.
        private int _droppedSinceRender;

        /// <summary>A rebuild, now if the transcript is quiet, at turn end if it is not.</summary>
        private void ScheduleRebuild()
        {
            if (_isStreaming) { RequestRebuild(); return; }
            InvokeOnUi(RebuildFromModel);
        }

        /// <summary>
        /// Run on the UI thread. The document, the spans and the viewport belong to it; the
        /// callers of this are the pump (already there), the turn loop (a worker) and the
        /// emitters (whichever thread the engine is on), so the rebuild itself never assumes.
        /// </summary>
        private void InvokeOnUi(Action action)
        {
            IApplication app = _outputView.App;
            if (app == null) action(); else app.Invoke(action);
        }

        /// <summary>The diamond, and the hanging indent that lines the block up underneath it.</summary>
        public const string ProseLead = TranscriptRenderer.ProseLead;
        public const string ProseHangingIndent = TranscriptRenderer.ProseHangingIndent;

        /// <summary>
        /// The model has finished reasoning: the prose that follows is its decision, and so
        /// starts its own block — which is what earns it the diamond. A blank Prose entry is
        /// how that reaches the renderer AND the model, so a rebuild puts the diamond back in
        /// the same place.
        /// </summary>
        public void MarkThoughtBoundary() => Record(TranscriptEntry.Prose("\n"));

        /// <summary>
        /// Draw the one-line stand-in for a collapsed thought. Its own entry rather than
        /// AppendOutput, so it renders at column 0 instead of under the last call — on a
        /// rebuild as well as live.
        /// </summary>
        public void AppendThoughtSummary(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Record(TranscriptEntry.ThoughtSummary(text));
        }

        // ── The renderer ──────────────────────────────────────────────────────────

        private TranscriptRenderer _renderer;

        /// <summary>
        /// The one thing that draws a transcript entry, live or on a rebuild. Built lazily
        /// because it closes over the view's scheme, which is not set until the window is.
        /// </summary>
        private TranscriptRenderer Renderer =>
            _renderer ??= new TranscriptRenderer(
                sink:        EnqueueSpan,
                width:       AvailableProseWidth,
                proseCap:    ProseMaxWidth,
                resolve:     ResolveAttribute,
                prose:       style => ProseAttribute(style, ViewBackground),
                syntax:      kind => new Terminal.Gui.Drawing.Attribute(SyntaxColor(kind), ViewBackground),
                diffPalette: BuildDiffPalette,
                verbose:     TranscriptNoise.Verbose,
                resolveColor: ResolveAttribute);

        private Terminal.Gui.Drawing.Color ViewBackground
        {
            get
            {
                try { return _outputView.GetScheme().Normal.Background; }
                catch { return new Terminal.Gui.Drawing.Color(0, 0, 0); }
            }
        }

        private DiffPalette BuildDiffPalette() => new DiffPalette
        {
            Foreground = SyntaxColor,
            Gutter     = new Terminal.Gui.Drawing.Color(0x88, 0x88, 0x88),
            ContextBg  = ViewBackground,
            RemovedBg  = DiffRemovedBg,
            AddedBg    = DiffAddedBg,
        };

        // ── Re-rendering when the width changes ───────────────────────────────────

        // The width the document was last drawn at, the width seen on the previous pump
        // tick, and whether a rebuild is owed but could not be taken yet.
        private int _renderedWidth = -1;
        private int _lastSeenWidth = -1;
        private int _stableTicks;
        private bool _rebuildPending;

        /// <summary>
        /// A rebuild is owed. It is not taken here: mid-turn the streamer, the table buffer
        /// and the live tail hold state the model has not been told about yet, and rebuilding
        /// over it would drop the partial line the operator is watching arrive.
        /// </summary>
        private void RequestRebuild() => _rebuildPending = true;

        /// <summary>Take an owed rebuild now, if one is owed. Called at end of turn.</summary>
        internal void PerformPendingRebuild()
        {
            if (!_rebuildPending) return;
            _rebuildPending = false;
            RebuildFromModel();
        }

        /// <summary>
        /// Called from the render pump. A resize is only acted on once it has settled, so a
        /// drag rebuilds once at the end rather than on every pixel.
        /// </summary>
        private void CheckForResize()
        {
            int width = CurrentViewWidth();
            if (width <= 0) return;

            if (width != _lastSeenWidth)
            {
                _lastSeenWidth = width;
                _stableTicks = 0;
                return;
            }

            if (width == _renderedWidth) return;

            // Two ticks of the 100 ms pump: long enough that a drag is one rebuild, short
            // enough that letting go feels immediate.
            if (++_stableTicks < ResizeSettleTicks) return;

            _stableTicks = 0;
            if (_isStreaming) { RequestRebuild(); return; }
            RebuildFromModel();
        }

        /// <summary>Pump ticks a width must hold before the transcript is re-laid out.</summary>
        private const int ResizeSettleTicks = 2;

        // True between the first token of a turn and its end. A rebuild during that window
        // would discard the partial state the emitters hold.
        private volatile bool _isStreaming;

        // What AppendAnswer drew this turn: task_done's summary, ask_caller's questions.
        private readonly AnswerCapture _answers = new AnswerCapture();

        // Non-whitespace characters of visible prose drawn in the CURRENT iteration. The
        // turn loop resets it at each iteration boundary (BeginProseIteration), and
        // AppendProse adds to it, so at the moment the executor draws the terminal
        // iteration's task_done summary this holds exactly what the user SAW as prose in
        // that iteration — the measure AnswerDedup.ShouldCollapse compares against its
        // threshold. It is the terminal iteration's count, not a running total, which is
        // what keeps prose from an earlier "let me read…" iteration from suppressing the
        // summary.
        private int _proseCharsThisIteration;

        // The kind of answer the executor is about to draw, set by SetAnswerKind immediately
        // before AppendAnswer. Both task_done and ask_caller arrive with identical signatures;
        // only the kind tells them apart, and only a task_done summary is ever collapsed.
        private AnswerKind _pendingAnswerKind = AnswerKind.TaskDone;

        /// <summary>The answers drawn since the last take, or null; clears them.</summary>
        internal string TakeAnswersForHistory() => _answers.Take();

        /// <summary>Told by the turn loop, so a resize mid-answer waits for the answer.</summary>
        public void SetStreaming(bool streaming)
        {
            _isStreaming = streaming;
            // A turn starting forgets any answer a previous one drew but never took.
            if (streaming) _answers.Clear();
            // The turn ends on its worker thread; the rebuild does not run there.
            if (!streaming && _rebuildPending) InvokeOnUi(PerformPendingRebuild);
        }

        /// <summary>
        /// Begin a new LLM iteration: reset the per-iteration prose counter so the terminal
        /// iteration's task_done summary is judged against THIS iteration's prose, not a
        /// running total across the turn. The turn loop calls this once per iteration, before
        /// its stream starts.
        /// </summary>
        public void BeginProseIteration() => _proseCharsThisIteration = 0;

        /// <summary>
        /// Record the kind of answer the executor is about to draw, immediately before
        /// <see cref="AppendAnswer"/>. Both task_done and ask_caller reach AppendAnswer with
        /// identical signatures; the kind is the only thing that lets a skin tell them apart.
        /// </summary>
        public void SetAnswerKind(AnswerKind kind) => _pendingAnswerKind = kind;

        /// <summary>
        /// Draw the whole retained transcript again at the current width.
        /// <para>
        /// This is the in-place rewrite every brief since 09 declined — made the design
        /// rather than the exception, because the alternative was a table laid out at 200
        /// columns being shredded by the Editor at 70 with nothing left to re-lay it out
        /// from. It is safe now for the reason the live tail was safe: the document is a
        /// projection of something else, and that something else is still here.
        /// </para>
        /// </summary>
        internal void RebuildFromModel()
        {
            TextDocument doc = _outputView.Document;
            if (doc == null) return;

            // Where the reader was. An offset means nothing after a re-layout, so the anchor
            // is the ENTRY that offset belonged to.
            var before = new List<int>(Renderer.EntryOffsets);
            int topOffset = ApproximateOffsetOfTopRow(doc);
            int anchorEntry = ScrollAnchor.Find(before, topOffset) - _droppedSinceRender;
            if (anchorEntry < 0) anchorEntry = 0;
            _droppedSinceRender = 0;
            bool pinned = _pinnedScrollRows > 0;

            try
            {
                doc.BeginUpdate();
                try
                {
                    if (doc.TextLength > 0) doc.Remove(0, doc.TextLength);
                    _colorSpans.Clear();
                    _liveTailRendered = 0;

                    // Straight to the document: a rebuild is not a stream, and routing it
                    // through the coalescing queue would have the pump redraw it in pieces.
                    _rebuilding = true;
                    Renderer.RenderAll(_model.Entries);
                }
                finally
                {
                    _rebuilding = false;
                    doc.EndUpdate();
                }

                AppendLiveTail(doc, _liveTail);
                try { doc.UndoStack?.ClearAll(); } catch { /* best effort */ }
            }
            catch (Exception ex)
            {
                Diag($"[REBUILD] EXCEPTION ex={ex.Message}");
                return;
            }

            _renderedWidth = CurrentViewWidth();

            if (!pinned)
            {
                if (!_outputView.HasSelection) _outputView.CaretOffset = doc.TextLength;
            }
            else
            {
                int restored = ScrollAnchor.Restore(Renderer.EntryOffsets, anchorEntry);
                ScrollTopRowToOffset(doc, restored);
            }

            Diag($"[REBUILD] width={_renderedWidth} entries={_model.Entries.Count} chars={doc.TextLength}");
        }

        // True while RenderAll is writing straight to the document.
        private bool _rebuilding;

        private int CurrentViewWidth()
        {
            int width;
            try { width = _outputView.Viewport.Width; }
            catch { width = 0; }

            int consoleWidth;
            try { consoleWidth = Console.WindowWidth; }
            catch { consoleWidth = 0; }

            return PipeTable.ResolveWidth(width, consoleWidth);
        }

        // The Editor scrolls in visual rows and the anchor works in document offsets, and
        // under word wrap there is no exposed map between the two. The bridge is proportion:
        // a row this far down the content is, near enough, an offset this far into the text.
        // It is approximate by one wrapped paragraph at most, and the anchor rounds it to an
        // entry, so the reader lands on the block they were reading rather than on a
        // character index that now belongs to a different one.

        private int ApproximateOffsetOfTopRow(TextDocument doc)
        {
            try
            {
                int rows = Math.Max(1, _outputView.GetContentSize().Height);
                int row = Math.Max(0, _outputView.Viewport.Y);
                long offset = (long)doc.TextLength * row / rows;
                return (int)Math.Min(doc.TextLength, offset);
            }
            catch
            {
                return 0;
            }
        }

        private void ScrollTopRowToOffset(TextDocument doc, int offset)
        {
            try
            {
                int rows = Math.Max(1, _outputView.GetContentSize().Height);
                int length = Math.Max(1, doc.TextLength);
                int row = (int)((long)rows * Math.Min(offset, length) / length);
                var vp = _outputView.Viewport;
                int maxRow = Math.Max(0, rows - vp.Height);
                _outputView.Viewport = new System.Drawing.Rectangle(vp.X, Math.Min(row, maxRow), vp.Width, vp.Height);
            }
            catch
            {
                // Fall back to the caret: the view scrolls to keep it visible, which puts
                // the reader at the block even if not at the same row.
                try { _outputView.CaretOffset = Math.Min(offset, doc.TextLength); } catch { }
            }
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

                    // The transcript is a projection of retained source, so a width change is
                    // answerable now: re-lay it out rather than leaving a table drawn for a
                    // window that no longer exists.
                    try { CheckForResize(); }
                    catch (Exception ex) { Diag($"[REBUILD] check ex={ex.Message}"); }

                    return true; // keep pumping for the app's life
                });
        }

        // Enqueue one colored span. Producers call this from any thread — no App.Invoke; the render
        // pump drains the buffer on the next UI tick. App is null before app.Run() attaches the
        // window (startup banner): at that point we are on the main thread with no running loop and
        // no pump, so insert directly to keep ordering with the banner.
        private void EnqueueSpan(string text, Terminal.Gui.Drawing.Attribute attr, bool nonCopyable = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            // nonCopyable rides the span to the document with the text: the flag a copy skips
            // lives where the color a draw reads lives, so both survive a rebuild identically.
            // A rebuild is not a stream. It runs on the UI thread inside one update group,
            // so its spans go straight into the document — routing them through the
            // coalescing queue would have the pump redraw the transcript in pieces, which is
            // the flicker the debounce exists to avoid.
            if (_rebuilding)
            {
                InsertSpan(text, attr, nonCopyable, scroll: false);
                return;
            }

            if (_outputView.App == null)
            {
                InsertSpan(text, attr, nonCopyable, scroll: true);
                return;
            }

            lock (_pendingLock)
                _pending.Add((text, attr, nonCopyable));
        }

        // Drain the whole pending backlog into the document in ONE UI-thread pass: a single
        // Document.Insert followed by exactly one auto-scroll. MUST run on the UI thread (invoked by
        // the render pump). Cheap when idle (lock + count check).
        /// <summary>
        /// Drain the append backlog now, on the UI thread, and complete when it has landed.
        /// <para>
        /// The pump runs on a 100 ms timeout, so text appended a moment ago is still in the
        /// queue. Anything that puts a modal in front of the transcript has to wait for that
        /// queue first, or it asks about a diff the reader cannot see yet. IApplication.Invoke
        /// is FIFO, so a flush invoked before the dialog is invoked has already run when the
        /// dialog opens.
        /// </para>
        /// <para>
        /// While the user is pinned to scrollback the document is frozen by design and this
        /// changes nothing — the backlog stays buffered, as it would have anyway.
        /// </para>
        /// </summary>
        internal Task FlushNowAsync()
        {
            IApplication app = _outputView.App;
            if (app == null) { FlushPending(); return Task.CompletedTask; }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            app.Invoke(() =>
            {
                try { FlushPending(); }
                finally { tcs.TrySetResult(true); }
            });
            return tcs.Task;
        }

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

            (string text, Terminal.Gui.Drawing.Attribute attr, bool nonCopyable)[] batch;
            string liveTail;
            lock (_pendingLock)
            {
                liveTail = _liveTail;
                // Nothing queued and the live line unchanged: the common idle tick.
                if (_pending.Count == 0 && _liveTailRendered == LiveTailLength(liveTail)) return;
                batch = _pending.ToArray();
                _pending.Clear();
            }

            TextDocument doc = _outputView.Document;
            if (doc == null)
            {
                Diag($"[FLUSH] SKIP (no document) batch={batch.Length}");
                return;
            }

            // Take the live line out of the document before anything else lands, so the batch
            // appends behind real transcript text rather than behind a line that is about to
            // be replaced. It goes back on at the end of this same pass.
            RemoveLiveTail(doc);

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
            var runs = new List<(string Text, bool NonCopyable, Terminal.Gui.Drawing.Attribute Attr)>();
            for (int k = 0; k < batch.Length; k++)
                runs.Add((NormalizeNewlines(batch[k].text), batch[k].nonCopyable, batch[k].attr));

            var pendingSpans = CoalesceFlushRuns(runs, start);
            foreach (var (text, _, _) in runs) combined.Append(text);

            if (combined.Length == 0)
            {
                Diag($"[FLUSH] spans={batch.Length} empty-after-normalize");
                AppendLiveTail(doc, liveTail);
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
                AppendLiveTail(doc, liveTail);
                return;
            }
            _colorSpans.AddRange(pendingSpans);

            TrimScrollbackIfNeeded(doc);

            // Re-attach the live line last, so it is again the document's final characters.
            AppendLiveTail(doc, liveTail);

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

        /// <summary>
        /// Fold the drained append batch into the one insert's color spans: consecutive runs
        /// with the same attribute AND the same non-copyable flag coalesce into one
        /// <see cref="ColorSpan"/>. Pure — no document, no list — because the flag a COPY
        /// skips (NonCopyable) rides each run and this is where it is carried into the span.
        /// <para>
        /// The flag is part of the coalescing key, not an afterthought: a gutter run (dim,
        /// non-copyable) and a prose run (dim, copyable) paint identically but copy
        /// differently, so they must be separate spans. Merging on attribute alone — what
        /// this used to do — dropped the flag (the 3-arg ColorSpan ctor defaults it false),
        /// and a copy of a live block kept its gutters.
        /// </para>
        /// </summary>
        internal static List<ColorSpan> CoalesceFlushRuns(
            List<(string Text, bool NonCopyable, Terminal.Gui.Drawing.Attribute Attr)> runs, int start)
        {
            var spans = new List<ColorSpan>();
            int offset = start;
            int i = 0;
            while (i < runs.Count)
            {
                Terminal.Gui.Drawing.Attribute attr = runs[i].Attr;
                bool nonCopyable = runs[i].NonCopyable;
                int runLen = 0;
                int j = i;
                while (j < runs.Count && runs[j].Attr.Equals(attr) && runs[j].NonCopyable == nonCopyable)
                {
                    runLen += runs[j].Text.Length;
                    j++;
                }
                if (runLen > 0) spans.Add(new ColorSpan(offset, runLen, attr, nonCopyable));
                offset += runLen;
                i = j;
            }
            return spans;
        }

        /// <summary>
        /// Set (or replace) the live tail line — a single line, always the last thing in the
        /// transcript, redrawn on the next render-pump tick. Null or empty removes it.
        /// <para>
        /// Only one such line exists at a time and it is not part of the record: as soon as
        /// real output arrives it is replaced, and whatever should be kept is appended
        /// normally. Callers may set it as often as they like; the pump only touches the
        /// document when the text actually changed.
        /// </para>
        /// </summary>
        public void SetLiveTail(string text)
        {
            string value = string.IsNullOrEmpty(text) ? null : NormalizeNewlines(text.TrimEnd('\n'));
            lock (_pendingLock) _liveTail = value;
        }

        /// <summary>Remove the live tail line, leaving the transcript as it was without it.</summary>
        public void ClearLiveTail() => SetLiveTail(null);

        // Rendered length of a live tail, including the newline that keeps it on its own line.
        private static int LiveTailLength(string tail)
            => tail == null ? 0 : tail.Length + 1;

        // Remove what was rendered for the live line. It is by construction the document's
        // last characters and the last color span, so this is a truncation plus a pop — the
        // span-rebasing the front-trim needs has no equivalent here.
        private void RemoveLiveTail(TextDocument doc)
        {
            if (_liveTailRendered <= 0) return;

            int n = _liveTailRendered;
            _liveTailRendered = 0;

            int start = doc.TextLength - n;
            if (start < 0)
            {
                // The document lost text underneath us (a trim that cut further than expected,
                // or a clear that raced the pump). Drop the claim rather than removing text
                // that belongs to somebody else.
                Diag($"[LIVETAIL] STALE len={n} docLen={doc.TextLength}");
                return;
            }

            try { doc.Remove(start, n); }
            catch (Exception ex)
            {
                Diag($"[LIVETAIL] REMOVE EXCEPTION len={n} ex={ex.Message}");
                return;
            }

            // Drop the spans that covered the removed range (one, in practice).
            for (int s = _colorSpans.Count - 1; s >= 0 && _colorSpans[s].Start >= start; s--)
                _colorSpans.RemoveAt(s);
        }

        private void AppendLiveTail(TextDocument doc, string tail)
        {
            if (tail == null) return;

            string text = tail + "\n";
            int start = doc.TextLength;
            try
            {
                doc.Insert(start, text);
            }
            catch (Exception ex)
            {
                Diag($"[LIVETAIL] INSERT EXCEPTION len={text.Length} ex={ex.Message}");
                return;
            }

            _colorSpans.Add(new ColorSpan(start, text.Length, ResolveAttribute(OutputColor.Thinking)));
            _liveTailRendered = text.Length;

            if (!_outputView.HasSelection)
                _outputView.CaretOffset = doc.TextLength;
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
            for (int i = 0; i < _colorSpans.Count; i++)
            {
                ColorSpan s = _colorSpans[i];
                int end = s.Start + s.Length;
                if (end <= cut) continue;                 // fully trimmed away
                int newStart = s.Start - cut;
                int newLen   = s.Length;
                if (newStart < 0) { newLen += newStart; newStart = 0; } // straddles the cut
                if (newLen <= 0) continue;
                rebased.Add(new ColorSpan(newStart, newLen, s.Attr, s.NonCopyable));
            }
            _colorSpans.Clear();
            _colorSpans.AddRange(rebased);
            Diag($"[TRIM] cut={cut} newTotal={doc.TextLength} spans={_colorSpans.Count}");
        }

        /// <summary>
        /// True when <paramref name="offset"/> is inside display chrome a copy must skip —
        /// the line-number gutter on the model's fenced code. The copy path (Program.cs) walks
        /// the selection through this; the flag is on the same spans the draw path paints from,
        /// so it survives a resize rebuild and a resume replay without a second store.
        /// </summary>
        internal bool IsNonCopyableAt(int offset) => _colorSpans.IsNonCopyable(offset);

        // Shared insert: normalize newlines, splice at document end, record the color span. When
        // scroll is true the caret follows the newest line (direct/pre-init path); the batched
        // flush sets the caret once for the whole batch instead. MUST run on the UI thread.
        // nonCopyable marks display chrome (the code gutter) that a copy must skip; it is
        // recorded on the span with the text, exactly like the attribute the draw path reads.
        private void InsertSpan(string text, Terminal.Gui.Drawing.Attribute attr, bool nonCopyable, bool scroll)
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
                _colorSpans.Add(new ColorSpan(start, text.Length, attr, nonCopyable));
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
                lock (_pendingLock)
                {
                    _pending.Clear();                   // drop queued, not-yet-rendered spans
                    _liveTail = null;                   // and the live line, whose text is about to go
                }

                // (the pending tuple is now 3-wide; nothing else changes here)

                // /cls clears what is PAINTED and what it was painted from — otherwise the
                // next resize would bring the cleared transcript back.
                _model.Clear(); _droppedSinceRender = 0;
                Renderer.Reset();
                _liveTailRendered = 0;

                TextDocument doc = _outputView.Document;
                if (doc != null && doc.TextLength > 0)
                {
                    try { doc.Remove(0, doc.TextLength); }   // clear text → scrollback to 0
                    catch (Exception ex) { Diag($"[CLS] remove ex={ex.Message}"); }
                }
                _colorSpans.Clear();                          // reset accumulated color spans

                // One-line confirmation so the cleared view isn't ambiguous.
                InsertSpan("[screen cleared — conversation and context preserved]\n",
                    ResolveAttribute(OutputColor.Dim), nonCopyable: false, scroll: true);

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
            => ConfirmAsync("Token budget", message, "_Continue", "_Stop");

        // ── IAgenticHost.ConfirmActionAsync ────────────────────────────────────────
        // Manual-approval gate for mutating actions (run_shell, file writes, MCP tool
        // calls). Distinct from the token-budget dialog above: the operator has to see
        // WHICH action is being approved, under a title and buttons that say so. The
        // interface default routes to ConfirmContinueAsync, so overriding here only
        // changes what the TUI shows — headless hosts keep the auto-continue path.
        Task<bool> IAgenticHost.ConfirmActionAsync(string action)
            => ConfirmAsync("Approve action", action, "_Approve", "_Skip");

        /// <summary>
        /// A modal yes/no on the UI thread, resolving the awaiting worker with the answer.
        /// <para>
        /// The title and the button words are parameters because the one caller this used to
        /// have was the token-budget guard, and its title was baked in. A patch prompt posed
        /// under the heading "Token budget" with buttons reading Continue and Stop asks a
        /// different question from the one it means — and the operator has to answer it in
        /// the second it appears.
        /// </para>
        /// <para>
        /// The negative button is the default, here and for the budget guard: Enter on a
        /// dialog you did not expect should decline, not act.
        /// </para>
        /// </summary>
        private Task<bool> ConfirmAsync(string title, string message, string yesText, string noText)
        {
            IApplication app = _outputView.App;
            if (app == null) return Task.FromResult(true); // pre-init / non-interactive — don't block

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            app.Invoke(() =>
            {
                // ── Size the dialog to the message ──────────────────────────────
                // The old code used a fixed Height=9 and Label{Height=Dim.Fill(2)},
                // which resolved the label to ZERO rows (the dialog's button bar eats
                // the content area) — an empty body. Now: word-wrap the text, set the
                // label's height explicitly, and size the dialog to fit.
                // Strip \r before wrapping: Windows CRLF in the message would otherwise leave a
                // stray \r at the end of every line (WrapText splits on \n only), and a trailing
                // \r on the last line of the label would render past the label's right edge.
                string text = (message ?? string.Empty).Replace("\r", string.Empty).Trim();
                if (text.Length == 0) text = "(no details)";

                var drv = app.Driver;
                int screenRows = drv?.Rows ?? 25;
                int screenCols = drv?.Cols ?? 80;

                // ── One width, by construction ──────────────────────────────────
                // The wrap width and the label width MUST be the same number, or any wrapped
                // line longer than the label's resolved Frame.Width is clipped on screen. The
                // old code derived them independently — bodyWidth from a Math.Max(40, …) floor
                // the dialog never had, and the label from Dim.Fill(2) — and they disagreed by
                // 1–2 columns in a 120-col run (label 78, wrap 80), clipping 79–80-char lines.
                //
                // dialogCols: 70% of the screen, floored at 40, capped at the screen so a
                // narrow terminal can't be asked for more than it has.
                int dialogCols = Math.Min(screenCols, Math.Max(40, (int)(screenCols * 0.7)));

                // bodyWidth: the label's real width, derived from dialogCols minus the dialog's
                // horizontal chrome — border (1) + left padding (1) + the label's X offset (1)
                // on each side = 5 columns, measured in the headless harness (a dialog with
                // these dimensions lays out its label at Frame.X=1 with
                // Frame.Width == dialogCols - 5; ApprovalDialogTests asserts the laid-out
                // label is at least as wide as every wrapped line). We set the label to this
                // EXACT width below, so wrap width == label width is guaranteed by
                // construction rather than approximated.
                int bodyWidth = Math.Max(1, dialogCols - 5);

                string[] lines = WrapText(text, bodyWidth);

                // Cap to a sensible fraction of the screen so a huge prompt can't
                // fill the whole terminal.
                int maxBodyRows = Math.Max(2, screenRows / 3);
                if (lines.Length > maxBodyRows)
                {
                    var capped = new string[maxBodyRows];
                    Array.Copy(lines, capped, maxBodyRows - 1);
                    string last = lines[maxBodyRows - 1];
                    capped[maxBodyRows - 1] = last.Length > bodyWidth - 1
                        ? last.Substring(0, bodyWidth - 1) + "\u2026"
                        : last + "\u2026";
                    lines = capped;
                }

                int bodyRows = lines.Length;
                string displayText = string.Join("\n", lines);

                var dlg = new Dialog
                {
                    Title  = title,
                    Width  = dialogCols, // absolute — the SAME number bodyWidth was derived from
                    Height = bodyRows + 5, // body + top border/title + button bar + bottom border
                };
                var label = new Label
                {
                    Text   = displayText,
                    X = 1, Y = 1,
                    Width  = bodyWidth, // absolute — the EXACT width WrapText wrapped to
                    Height = bodyRows, // explicit — Dim.Fill(2) resolved to 0 in the old layout
                };
                var yesBtn = new Button { Text = yesText };
                var noBtn  = new Button { Text = noText, IsDefault = true };
                dlg.Add(label);
                dlg.AddButton(yesBtn);
                dlg.AddButton(noBtn);

                // Non-blocking modal session. The old code called app.Run(dlg) — a NESTED modal
                // loop — from inside this app.Invoke callback, which parked the main loop for the
                // dialog's whole life: the render-pump AddTimeout, every other Invoke, and the
                // input queue were all starved until the nested Run returned. An OS
                // focus-out/focus-in arriving in that window left the nested loop wedged, and the
                // TCS only ever resolved in the finally after the (never-returning) Run — the
                // total freeze. Begin() is the fix: it lays out the dialog, focuses its first
                // control, draws it, and RETURNS, so the main loop keeps pumping underneath and
                // keys and the render pump stay live no matter what the terminal does.
                SessionToken token = null;
                object pollToken = null;

                // Close the session exactly once from ANY exit path. In the Begin model nothing
                // else calls app.End (Run's finally is the only caller, and we are not in a Run),
                // so we own the teardown: End unwinds the session stack and restores the main
                // window's modal state, then we dispose and hand focus back to the input view so
                // keys work again. TrySetResult gives exactly-once.
                void Close(bool answer)
                {
                    if (!tcs.TrySetResult(answer)) return; // already resolved — exactly-once
                    if (pollToken != null) app.RemoveTimeout(pollToken);
                    if (token != null && dlg.IsRunning) app.End(token);
                    dlg.Dispose();
                    FocusInputView?.SetFocus();
                }

                yesBtn.Accepting += (s, e) => { e.Handled = true; Close(true); };
                noBtn.Accepting  += (s, e) => { e.Handled = true; Close(false); };

                token = app.Begin(dlg);
                if (token == null)
                {
                    // Begin was cancelled (an IsRunningChanging veto). The dialog never ran.
                    dlg.Dispose();
                    tcs.TrySetResult(false);
                    return;
                }

                // Esc (Command.Quit) and any other non-button dismissal call RequestStop, which in
                // the Begin model only sets StopRequested — nothing calls End. Poll for it (and for
                // IsRunning dropping, e.g. app shutdown) and resolve a decline so the awaiting loop
                // is never left pending. 100 ms matches the render-pump cadence.
                pollToken = app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
                {
                    if (!dlg.IsRunning || dlg.StopRequested)
                    {
                        Close(false);
                        return false; // stop polling
                    }
                    return true; // keep polling
                });
            });
            return tcs.Task;
        }

        /// <summary>
        /// Word-wraps <paramref name="text"/> to <paramref name="width"/> columns so a confirm
        /// dialog's message always fits its label. Breaks at spaces where possible; a single
        /// token longer than the width (compact JSON has no spaces) is hard-broken so no line
        /// overflows. Embedded newlines in the input are kept as line breaks, and a blank input
        /// yields a single empty line (the caller substitutes a placeholder before wrapping).
        /// </summary>
        private static string[] WrapText(string text, int width)
        {
            if (width < 1) width = 1;
            var lines = new List<string>();
            foreach (string raw in text.Split('\n'))
            {
                var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                {
                    lines.Add(""); // preserve a blank line from the input
                    continue;
                }
                string cur = "";
                foreach (string w in words)
                {
                    // Hard-break an over-long token (nothing to wrap at inside it) into a local
                    // so the foreach variable is never reassigned.
                    string word = w;
                    while (word.Length > width)
                    {
                        if (cur.Length > 0) { lines.Add(cur); cur = ""; }
                        lines.Add(word.Substring(0, width));
                        word = word.Substring(width);
                    }
                    if (cur.Length == 0)
                        cur = word;
                    else if (cur.Length + 1 + word.Length <= width)
                        cur += " " + word;
                    else
                    {
                        lines.Add(cur);
                        cur = word;
                    }
                }
                lines.Add(cur);
            }
            return lines.ToArray();
        }

        // ── Model-authored answer (task_done summary, ask_caller questions) ─────
        // Routes the whole block through a one-shot CodeBlockStreamer so fenced code
        // stays fenced (not line-split and inlined) and prose gets the same per-line
        // inline-markdown rendering as streamed prose.
        public void AppendAnswer(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Held for history. The turn's row is written before the executor draws this,
            // so without the capture a tool-driven turn is saved with an empty answer.
            // UNCHANGED by the display collapse below: the history row is always the summary.
            _answers.Append(text);

            // The terminal iteration streamed the report as prose and the executor now draws
            // the same report as the task_done summary. When the prose was real (>= the
            // threshold) the summary is a repeat, so it is drawn as one dim line and parked
            // for /expand instead of duplicated. ask_caller questions are never collapsed,
            // and a summary with no prose above it is drawn in full exactly as before.
            AnswerKind kind = _pendingAnswerKind;
            _pendingAnswerKind = AnswerKind.TaskDone; // reset for the next answer
            if (AnswerDedup.ShouldCollapse(_proseCharsThisIteration, text, kind))
            {
                Expansions.ParkAnswer(text);
                Record(TranscriptEntry.AnswerCollapsed(text));
                return;
            }

            var streamer = new CodeBlockStreamer(
                prose: AppendProse,
                code:  AppendCodeLine);
            streamer.Feed(text);
            streamer.Flush();
            FlushProse();
        }

        /// <summary>
        /// Draw a task_done summary that was already streamed as prose as one dim stand-in
        /// line, parking the summary for /expand. Used by the resume-replay path, which has
        /// already decided (from the two consecutive assistant rows in history) that this
        /// answer is a repeat — so the prose count is not consulted here. The live path makes
        /// that decision inside <see cref="AppendAnswer"/> and takes this same two-step shape.
        /// </summary>
        public void AppendCollapsedAnswer(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Expansions.ParkAnswer(text);
            Record(TranscriptEntry.AnswerCollapsed(text));
        }

        // ── Styled prose append (inline markdown) ─────────────────────────────────────
        // CodeBlockStreamer now emits prose per COMPLETED line, which lets each line's
        // inline markdown be rendered with its markers consumed: one '#'–'######' heading,
        // **bold** spans, `inline code` spans. Each rendered run enqueues onto the same
        // coalesced buffer as AppendCode, so prose keeps strict arrival order with code.
        // Thinking text never flows here — Program.cs appends it directly in the
        // muted Thinking color — so its output is unaffected by this path.
        /// <summary>
        /// One completed line of model prose. Pipe-table lines are held here until the table
        /// ends, because a column width depends on every row — everything else goes straight
        /// through. The buffer decides; this only carries out what it says.
        /// </summary>
        internal void AppendProse(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            _proseCharsThisIteration += AnswerDedup.CountVisibleProse(line);
            Record(TranscriptEntry.Prose(line));
        }

        /// <summary>
        /// Release anything the table buffer is holding. Called wherever the prose streamer is
        /// flushed: a table that ends a response has no line after it to close it, and without
        /// this it would be held until the process exited — which is to say, lost.
        /// </summary>
        internal void FlushProse()
        {
            Record(TranscriptEntry.ProseFlush());

            // End of a response: the streamer and the table buffer are empty, so a resize
            // that arrived mid-turn can now be honoured without losing partial state.
            PerformPendingRebuild();
        }



        /// <summary>
        /// The rule characters tables are drawn with. One constant, because no box drawing
        /// existed anywhere in this TUI before and no terminal here has been asked to render
        /// any — swapping to <see cref="TableGlyphs.Ascii"/> is a one-line change if a live
        /// check turns up boxes.
        /// </summary>
        private static readonly TableGlyphs TableRules = TableGlyphs.Unicode;

        /// <summary>
        /// Columns a table may occupy: the view's width now, less the prose block's hanging
        /// indent and one column of slack so a full-width row cannot itself trigger a wrap.
        /// </summary>
        private int AvailableProseWidth()
        {
            int width;
            try { width = _outputView.Viewport.Width; }
            catch { width = 0; }

            int consoleWidth;
            try { consoleWidth = Console.WindowWidth; }
            catch { consoleWidth = 0; }

            width = PipeTable.ResolveWidth(width, consoleWidth);

            return Math.Max(PipeTable.MinColumnWidth * 2,
                            width - ProseHangingIndent.Length - 1);
        }


        // Inline-markdown palette — VS Code Dark+ values already used by the code path
        // (Syn* constants below) so the styled spans read as part of the same scheme:
        //   Normal     → the host's Normal output color (ResolveAttribute)
        //   Heading    → #569CD6 blue + TextStyle.Bold (headings stand out; bold is
        //                Terminal.Gui's real SGR 1 attribute, not a faked bright color)
        //   Bold       → #D4D4D4 light (SynPlain) + TextStyle.Bold
        //   InlineCode → #4EC9B0 teal (SynType), no bold — matches code-ish text in code
        private Terminal.Gui.Drawing.Attribute ProseAttribute(InlineTextStyle style, Terminal.Gui.Drawing.Color bg)
        {
            switch (style)
            {
                case InlineTextStyle.Heading:
                    return new Terminal.Gui.Drawing.Attribute(SynKeyword, bg, Terminal.Gui.Drawing.TextStyle.Bold);
                case InlineTextStyle.Bold:
                    return new Terminal.Gui.Drawing.Attribute(SynPlain, bg, Terminal.Gui.Drawing.TextStyle.Bold);
                case InlineTextStyle.InlineCode:
                    return new Terminal.Gui.Drawing.Attribute(SynType, bg, Terminal.Gui.Drawing.TextStyle.None);
                case InlineTextStyle.Normal:
                default:
                    return ResolveAttribute(OutputColor.Normal);
            }
        }

        // ── Syntax-highlighted code append ────────────────────────────────────────────────
        // Tokenizes `code` for `language` and paints each token its VS Code Dark+ color. Tokens
        // enqueue onto the shared coalesced buffer — the same queue prose uses — so code and prose
        // keep strict arrival order and the whole block drains in the next flush rather than one
        // UI hop per token. Bypasses the quiet-transcript filter — this is real code, not churn.
        internal void AppendCode(string code, string language)
            => AppendCode(code, language, nestUnderCall: false);

        /// <summary>
        /// Paint highlighted code. <paramref name="nestUnderCall"/> is true for a TOOL RESULT
        /// — a file a Read call returned — which hangs under that call at the same four
        /// columns shell output does; false when the MODEL wrote the fence in its own prose.
        /// </summary>
        internal void AppendCode(string code, string language, bool nestUnderCall)
        {
            if (string.IsNullOrEmpty(code)) return;
            Record(TranscriptEntry.Code(code, language, nestUnderCall));
        }

        /// <summary>
        /// One line of the model's own fenced block, as the streamer emits them per line.
        /// <paramref name="blockStart"/> is true for the block's first line only: it is what
        /// makes the renderer's gutter count follow the FENCED BLOCK (1..N across entries) rather
        /// than restart on every entry. A per-line entry is always the model's fence, never a
        /// nested tool listing.
        /// </summary>
        internal void AppendCodeLine(string code, string language, bool blockStart)
        {
            if (string.IsNullOrEmpty(code)) return;
            Record(TranscriptEntry.CodeLine(code, language, blockStart));
        }

        private void AppendHighlightedListing(string content, string fullPath, int lineCount)
            => Record(TranscriptEntry.Listing(content, fullPath, lineCount));



        // VS Code Dark+ palette, matching the reference screenshot.
        private static readonly Terminal.Gui.Drawing.Color SynKeyword = new Terminal.Gui.Drawing.Color(0x56, 0x9C, 0xD6); // #569CD6 blue
        private static readonly Terminal.Gui.Drawing.Color SynControl = new Terminal.Gui.Drawing.Color(0xC5, 0x86, 0xC0); // #C586C0 purple
        private static readonly Terminal.Gui.Drawing.Color SynType    = new Terminal.Gui.Drawing.Color(0x4E, 0xC9, 0xB0); // #4EC9B0 teal
        private static readonly Terminal.Gui.Drawing.Color SynMethod  = new Terminal.Gui.Drawing.Color(0xDC, 0xDC, 0xAA); // #DCDCAA yellow
        private static readonly Terminal.Gui.Drawing.Color SynString  = new Terminal.Gui.Drawing.Color(0xCE, 0x91, 0x78); // #CE9178 orange
        private static readonly Terminal.Gui.Drawing.Color SynComment = new Terminal.Gui.Drawing.Color(0x6A, 0x99, 0x55); // #6A9955 green
        private static readonly Terminal.Gui.Drawing.Color SynNumber  = new Terminal.Gui.Drawing.Color(0xB5, 0xCE, 0xA8); // #B5CEA8 pale green
        private static readonly Terminal.Gui.Drawing.Color SynPlain   = new Terminal.Gui.Drawing.Color(0xD4, 0xD4, 0xD4); // #D4D4D4 light

        // Diff tints. Dark enough that SynComment green (#6A9955) and SynString orange
        // (#CE9178) — the two token colours closest to the tints themselves — stay readable
        // on top, which is the whole point of tinting the line rather than recolouring it.
        private static readonly Terminal.Gui.Drawing.Color DiffRemovedBg = new Terminal.Gui.Drawing.Color(0x4B, 0x1F, 0x1F); // #4B1F1F
        private static readonly Terminal.Gui.Drawing.Color DiffAddedBg   = new Terminal.Gui.Drawing.Color(0x1F, 0x3A, 0x1F); // #1F3A1F

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


        // ── Quiet-transcript filter ───────────────────────────────────────────────
        // The decision itself lives in TranscriptNoise, because the host is only one of the
        // two doors engine status lines arrive through: [LLM], [CONTEXT] and [TOOL_USE] are
        // emitted on the streaming token path in Program.cs and never reach AppendOutput at
        // all. Both doors call the same function so they cannot drift.

        private static bool IsSuppressedNoise(string text) => TranscriptNoise.IsSuppressed(text);

        // ── Transcript expansion (/expand) ────────────────────────────────────────

        /// <summary>What <c>/expand</c> draws on: the last hidden thought and tool output.</summary>
        public ExpandBuffer Expansions { get; } = new ExpandBuffer();

        /// <summary>
        /// Transcript lines per tool call (0 = uncapped). Settable at runtime by
        /// <c>/output-lines</c>; the model's copy of the output is never capped.
        /// </summary>
        public int OutputLineCap { get; set; } = CappedOutputWriter.DefaultCap;

        /// <summary>
        /// Maximum width in columns the model's prose wraps to. 0 (or negative) means no
        /// cap — prose uses the full view width. Tables, code and diffs are never capped;
        /// only the renderer's ProseWrap call narrows to this. Read at render time, so a
        /// rebuild after a resize sees the same value as the live path did.
        /// </summary>
        public int ProseMaxWidth { get; set; } = 110;

        /// <summary>
        /// Serve an <c>/expand</c> request: append the parked lines in their original colours
        /// and return the message for the command result.
        /// </summary>
        public ExpandResult Expand(string argument)
        {
            ExpandResult result = Expansions.Resolve(argument);
            foreach (TranscriptLine line in result.Lines)
                AppendOutputLocal(line.Text + "\n", line.Color);
            return result;
        }

        // ── IAgenticHost.RunShellAsync ────────────────────────────────────────────

       async Task<(int exitCode, string output)> IAgenticHost.RunShellAsync(string command, int? timeoutSeconds, bool detach)
        {
            AppendOutputLocal($"[SHELL] > {command}\n", OutputColor.Dim);

            // The transcript gets a capped head/tail view; `output` — what the model and the
            // history see — is untouched by the cap.
            var writer = new CappedOutputWriter(OutputLineCap,
                line => AppendOutputLocal(line.Text + "\n", line.Color));
            var progress = new SynchronousProgress<ShellOutputLine>(line =>
                writer.Write(line.Line, line.IsError ? OutputColor.Error : OutputColor.Normal));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            int exit = -1;
            string steerCancel = null;
            try
            {
                // The turn token still reaps the whole tree on Esc/Ctrl+C; the call's own
                // token is the override-steer interrupt, which spares a detach call's children.
                using var call = _shellInterrupt.Begin(CancellationToken);
                var (output, exitCode) = await _shellRunner.ExecuteAsync(
                    command, CancellationToken, timeoutSeconds, progress, detach, interruptToken: call.Token);
                exit = exitCode;
                steerCancel = call.End();
                return (exitCode, steerCancel != null ? _shellInterrupt.ResultFor(output) : output);
            }
            finally
            {
                // Release the held tail even when the command threw or was cancelled — the
                // lines are already on screen's doorstep and losing them would read as output
                // silently going missing.
                writer.Flush();
                Expansions.ParkOutput(writer.Hidden);

                // The outcome comes last, because DevMind streams: unlike a runner that
                // reports when it is done, the call line is already on screen with output
                // behind it by the time the exit code exists, and rewriting a line that is no
                // longer the document's tail is not something this transcript can do. So the
                // verdict is its own line, nested with the output it concludes.
                clock.Stop();
                var (line, color) = ShellOutcome.Line(exit, clock.Elapsed);
                AppendOutputLocal(line + "\n", color);
                if (steerCancel != null)
                    AppendOutputLocal(OverrideSteerCancelledLine + "\n", OutputColor.Warning);
            }
        }

        // ── IAgenticHost.SaveFileAsync ────────────────────────────────────────────

        async Task<string> IAgenticHost.SaveFileAsync(string fileName, string content, bool fromToolCall)
        {
            string fileNameOnly = SafeGetFileName(fileName);

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

                // Write guard — AFTER resolution, on the path that will actually be written
                // (bare-name keying let an unread same-named file pass; see TaskReadSet).
                if (!_taskReadFiles.IsKnown(fullPath))
                {
                    bool approved = await ConfirmUnreadFileWriteAsync(fullPath);
                    if (!approved)
                    {
                        AppendOutputLocal($"[WRITE GUARD] File write to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                        return null;
                    }
                    _taskReadFiles.MarkKnown(fullPath);
                }

                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // New file — no merge gate needed
                if (!File.Exists(fullPath))
                {
                    CSharpSyntaxGate.Enforce(fullPath, null, fileContent);          // H-04
                    fileContent = TextFileFormat.WriteNew(fullPath, fileContent);   // H-30: repo line ending
                    _fileCache.Store(FileCacheKey(fullPath), fileContent);
                    int newFileLines = fileContent.Split('\n').Length;
                    var created = WriteEcho.Describe(fileNameOnly, fullPath, _shellRunner.WorkingDirectory,
                                                     MergeMode.CleanNoDivergence, $"({newFileLines} lines)");
                    AppendOutputLocal($"[FILE] Saved {created.Detail}\n", created.Color);
                    return fullPath;
                }

                // Existing file — run three-way merge gate
                string currentText = File.ReadAllText(fullPath);
                string baseText = _fileCache.GetFull(FileCacheKey(fullPath));

                MergeCheckResult merge = ThreeWayMergeCheck.CheckAndMerge(baseText, fileContent, currentText);

                MergeReport.TraceFallback("TUI SaveFileAsync", fileNameOnly, merge);

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
                // Existing file: keep its BOM/encoding and dominant line ending.
                CSharpSyntaxGate.Enforce(fullPath, currentText, merge.MergedText);   // H-04
                string finalContent = TextFileFormat.WritePreserving(fullPath, merge.MergedText);
                _fileCache.Store(FileCacheKey(fullPath), finalContent);
                int savedLines = finalContent.Split('\n').Length;
                var saved = WriteEcho.Describe(fileNameOnly, fullPath, _shellRunner.WorkingDirectory,
                                               merge.Mode, $"({savedLines} lines)");
                AppendOutputLocal($"[FILE] Saved {saved.Detail}\n", saved.Color);
                return fullPath;
            }
            catch (CSharpSyntaxGateException)
            {
                throw;   // H-04: the executor turns it into the tool error the model reads
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

            // Block if a conflict is pending
            if (_pendingConflict != null)
            {
                AppendOutputLocal($"[MERGE CONFLICT] Cannot append to \"{fileNameOnly}\" — pending conflict on \"{_pendingConflict.FilePath}\" must be resolved first. Use /resolve accept_proposed, /resolve accept_current, or /resolve cancel.\n", OutputColor.Error);
                return null;
            }

            try
            {
                FileResolution res = FindFile(fileNameOnly, fileName.Replace('\\', '/'));
                string resolvedPath = res.Path
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileName);

                // Write guard — AFTER resolution, on the path that will actually be written
                // (bare-name keying let an unread same-named file pass; see TaskReadSet).
                if (!_taskReadFiles.IsKnown(resolvedPath))
                {
                    bool approved = await ConfirmUnreadFileWriteAsync(resolvedPath);
                    if (!approved)
                    {
                        AppendOutputLocal($"[WRITE GUARD] File append to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                        return null;
                    }
                    _taskReadFiles.MarkKnown(resolvedPath);
                }

                // New file — no merge gate needed
                if (!File.Exists(resolvedPath))
                {
                    string dir = Path.GetDirectoryName(resolvedPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    CSharpSyntaxGate.Enforce(resolvedPath, null, content);          // H-04
                    content = TextFileFormat.WriteNew(resolvedPath, content);       // H-30: repo line ending
                    _fileCache.Store(FileCacheKey(resolvedPath), content);
                    AppendOutputLocal($"[APPEND] Created {fileNameOnly}\n", OutputColor.Success);
                    return resolvedPath;
                }

                // Existing file — always re-read from disk and refresh cache (requirement #3)
                string currentText = File.ReadAllText(resolvedPath);
                _fileCache.Store(FileCacheKey(resolvedPath), currentText);

                // The appended text takes the file's dominant line ending, and the write keeps
                // its BOM/encoding.
                var format = TextFileFormat.Detect(resolvedPath);
                string separator = currentText.Length > 0 && !currentText.EndsWith("\n", StringComparison.Ordinal) ? (format.NewLine ?? "\n") : "";
                string proposedText = currentText + separator + format.NormalizeLineEndings(content);

                string baseText = _fileCache.GetFull(FileCacheKey(resolvedPath));

                MergeCheckResult merge = ThreeWayMergeCheck.CheckAndMerge(baseText, proposedText, currentText);

                MergeReport.TraceFallback("TUI AppendFileAsync", fileNameOnly, merge);

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

                CSharpSyntaxGate.Enforce(resolvedPath, currentText, merge.MergedText);   // H-04
                format.Write(resolvedPath, merge.MergedText);
                _fileCache.Store(FileCacheKey(resolvedPath), merge.MergedText);
                var appended = WriteEcho.Describe(fileNameOnly, resolvedPath, _shellRunner.WorkingDirectory,
                                                  merge.Mode);
                AppendOutputLocal($"[APPEND] Appended to {appended.Detail}\n", appended.Color);
                return resolvedPath;
            }
            catch (CSharpSyntaxGateException)
            {
                throw;   // H-04: the executor turns it into the tool error the model reads
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
            FileResolution res = FindFile(fileNameOnly, filename.Replace('\\', '/'));
            string resolvedPath = res.Path
                ?? Path.Combine(_shellRunner.WorkingDirectory, filename);

            if (!File.Exists(resolvedPath))
                return Task.FromResult(BuildFileNotFoundMessage("DELETE", filename, res));

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
            FileResolution res = FindFile(oldNameOnly, oldFilename.Replace('\\', '/'));
            string oldPath = res.Path
                ?? Path.Combine(_shellRunner.WorkingDirectory, oldFilename);

            if (!File.Exists(oldPath))
                return Task.FromResult(BuildFileNotFoundMessage("RENAME", oldFilename, res));

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

        /// <summary>Count of pending PATCH backups on the undo stack (public mirror
        /// of the interface member, for skin-side bookkeeping and tests).</summary>
        public int PatchBackupCount => _patchBackupStack.Count;

        // ── IAgenticHost.RecallMemoryAsync ────────────────────────────────────────

        Task<string> IAgenticHost.RecallMemoryAsync(string topic)
        {
            if (_memoryManager == null)
                return Task.FromResult("Memory not available: no working directory");

            // Layered recall (repo-default; "global:<slug>" for the machine-level
            // version). A same-slug collision resolves to the requested layer and
            // appends a visible note — never silently first-matched.
            var result = _memoryManager.RecallTopic(topic);
            if (result == null)
            {
                AppendOutputLocal($"[MEMORY] Topic not found: {topic}\n", OutputColor.Dim);
                return Task.FromResult($"Topic not found: {topic}");
            }

            AppendOutputLocal($"[MEMORY] Recalled: {topic}\n", OutputColor.Dim);
            return Task.FromResult(
                string.IsNullOrEmpty(result.CollisionNote)
                    ? result.Content
                    : result.Content + "\n\n" + result.CollisionNote);
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

            // Machine-level topics, when any exist, are appended in a labelled
            // "global:<slug>" section — byte-identical to the legacy repo-only
            // listing when none do.
            var globalTopics = _memoryManager.ListGlobalTopics();

            string index = _memoryManager.LoadIndex();
            if (string.IsNullOrWhiteSpace(index))
            {
                var topics = _memoryManager.ListTopics();
                if (topics.Count == 0 && globalTopics.Count == 0)
                {
                    AppendOutputLocal("[MEMORY] No memory topics found.\n", OutputColor.Dim);
                    return Task.FromResult("No memory topics found. Use save_memory to create one.");
                }
                var sb = new StringBuilder();
                if (topics.Count > 0)
                {
                    if (globalTopics.Count > 0) sb.AppendLine("Repo topics:");
                    sb.Append(string.Join("\n", topics.Select(t => $"- [{t}]")));
                }
                if (globalTopics.Count > 0)
                    sb.Append(BuildGlobalTopicSection(globalTopics));
                AppendOutputLocal($"[MEMORY] {topics.Count + globalTopics.Count} topic(s) available.\n", OutputColor.Dim);
                return Task.FromResult(sb.ToString().TrimEnd());
            }

            AppendOutputLocal("[MEMORY] Topics listed.\n", OutputColor.Dim);
            return Task.FromResult(globalTopics.Count == 0 ? index : index + BuildGlobalTopicSection(globalTopics));
        }

        /// <summary>
        /// The labelled machine-level topic section for list_memory_topics output
        /// (host-side mirror of the MCP tool's section). Appended only when a global
        /// topic exists, so output stays byte-identical to the legacy repo-only list
        /// otherwise. Model-facing prose — no code parses it.
        /// </summary>
        private static string BuildGlobalTopicSection(List<string> globalTopics)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"Global (machine-level) topics — recall with recall_memory \"{MemoryManager.GlobalTopicPrefix}<slug>\":");
            foreach (var t in globalTopics)
                sb.AppendLine($"- [{MemoryManager.GlobalTopicPrefix}{t}]");
            return sb.ToString().TrimEnd('\n');
        }

        // ── IAgenticHost.SearchMemoryAsync ────────────────────────────────────────

        Task<string> IAgenticHost.SearchMemoryAsync(string pattern)
        {
            if (_memoryManager == null)
                return Task.FromResult("Memory not available: no working directory");

            // BOTH layers: repo topics first, global topics tagged "global:<slug>"
            // so every hit's layer is visible. Byte-identical to the legacy
            // repo-only result when no global topics exist.
            string result = _memoryManager.SearchTopicsAllLayers(pattern);
            if (result == null)
            {
                AppendOutputLocal("[MEMORY] No memory topics to search.\n", OutputColor.Dim);
                return Task.FromResult("No memory topics found. Use save_memory to create one.");
            }

            AppendOutputLocal($"[MEMORY] Searched topics for \"{pattern}\"\n", OutputColor.Dim);
            return Task.FromResult(result);
        }

        // ── IAgenticHost.QueryLibraryAsync ────────────────────────────────────────

        async Task<string> IAgenticHost.QueryLibraryAsync(string question, int topK, CancellationToken cancellationToken)
        {
            var config = TuiConfig.Load();
            AppendOutputLocal($"[LIBRARY] Query: \"{question}\"\n", OutputColor.Dim);
            return await DocumentLibrarian.QueryAsTextAsync(
                config.LibraryEmbeddingEndpoint, config.LibraryConnectionString,
                question, topK, docFilter: null, cancellationToken).ConfigureAwait(false);
        }

        async Task<string> IAgenticHost.QueryLibraryAsync(string question, int topK, string docFilter, CancellationToken cancellationToken)
        {
            var config = TuiConfig.Load();
            AppendOutputLocal($"[LIBRARY] Query: \"{question}\" (doc_filter: {docFilter ?? "-"})\n", OutputColor.Dim);
            return await DocumentLibrarian.QueryAsTextAsync(
                config.LibraryEmbeddingEndpoint, config.LibraryConnectionString,
                question, topK, docFilter, cancellationToken).ConfigureAwait(false);
        }

        // ── IAgenticHost LSP tools (delegate to shared Core LspToolService) ───────

        async Task<string> IAgenticHost.GetDiagnosticsAsync(string filename)
        {
            FileResolution lspRes = ResolveLspPath(filename);
            string fullPath = lspRes.Path;
            if (fullPath == null) return BuildFileNotFoundMessage("get_diagnostics", filename, lspRes);
            AppendOutputLocal($"[LSP] get_diagnostics {SafeGetFileName(fullPath)}\n", OutputColor.Dim);
            return await _lspTools.GetDiagnosticsAsync(fullPath, CancellationToken);
        }

        async Task<string> IAgenticHost.GoToDefinitionAsync(string filename, int line, int character)
        {
            FileResolution lspRes = ResolveLspPath(filename);
            string fullPath = lspRes.Path;
            if (fullPath == null) return BuildFileNotFoundMessage("go_to_definition", filename, lspRes);
            AppendOutputLocal($"[LSP] go_to_definition {SafeGetFileName(fullPath)}:{line}:{character}\n", OutputColor.Dim);
            return await _lspTools.GoToDefinitionAsync(fullPath, line, character, CancellationToken);
        }

        async Task<string> IAgenticHost.FindReferencesAsync(string filename, int line, int character)
        {
            FileResolution lspRes = ResolveLspPath(filename);
            string fullPath = lspRes.Path;
            if (fullPath == null) return BuildFileNotFoundMessage("find_references", filename, lspRes);
            AppendOutputLocal($"[LSP] find_references {SafeGetFileName(fullPath)}:{line}:{character}\n", OutputColor.Dim);
            return await _lspTools.FindReferencesAsync(fullPath, line, character, CancellationToken);
        }

        async Task<string> IAgenticHost.HoverAsync(string filename, int line, int character)
        {
            FileResolution lspRes = ResolveLspPath(filename);
            string fullPath = lspRes.Path;
            if (fullPath == null) return BuildFileNotFoundMessage("hover", filename, lspRes);
            AppendOutputLocal($"[LSP] hover {SafeGetFileName(fullPath)}:{line}:{character}\n", OutputColor.Dim);
            return await _lspTools.HoverAsync(fullPath, line, character, CancellationToken);
        }

        async Task<string> IAgenticHost.FindSymbolAsync(string query, int maxResults, string language, string path)
        {
            AppendOutputLocal($"[LSP] find_symbol \"{query}\"\n", OutputColor.Dim);
            return await _lspTools.FindSymbolAsync(query, maxResults, language, CancellationToken, path);
        }

        // ── IAgenticHost web tools (delegate to shared Core WebTools) ─────────────

        async Task<string> IAgenticHost.WebSearchAsync(string query, int? maxResults)
        {
            AppendOutputLocal($"[WEB] search: {query}\n", OutputColor.Dim);
            return await WebTools.WebSearchAsync(query, maxResults, CancellationToken);
        }

      async Task<string> IAgenticHost.WebFetchAsync(string url, int offset)
        {
            AppendOutputLocal(offset > 0 ? $"[WEB] fetch: {url} (offset {offset})\n" : $"[WEB] fetch: {url}\n", OutputColor.Dim);
            return await WebTools.WebFetchAsync(url, offset, CancellationToken);
        }

        // ── IAgenticHost Learn tools (delegate to shared Core LearnTools) ─────────────

        async Task<string> IAgenticHost.LearnSearchAsync(string query, int? maxResults)
        {
            AppendOutputLocal($"[LEARN] search: {query}\n", OutputColor.Dim);
            return await LearnTools.LearnSearchAsync(query, maxResults, CancellationToken);
        }

        async Task<string> IAgenticHost.LearnFetchAsync(string url)
        {
            AppendOutputLocal($"[LEARN] fetch: {url}\n", OutputColor.Dim);
            return await LearnTools.LearnFetchAsync(url, CancellationToken);
        }

        async Task<string> IAgenticHost.LearnCodeSearchAsync(string query, int? maxResults)
        {
            AppendOutputLocal($"[LEARN] code_search: {query}\n", OutputColor.Dim);
            return await LearnTools.LearnCodeSearchAsync(query, maxResults, CancellationToken);
        }

        // Last connection that opened successfully this session — sticky reuse so the model
        // doesn't have to re-supply the connection on every stateless run_sql call. Session-scoped
        // (instance field), never a process-static, so it can't leak across DM sessions.
        private string _lastSuccessfulSqlConnectionString;

        /// <summary>The LLM's nearline cache, wired from Program.cs — used by recall_cache. May be null.</summary>
        public NearlineCache NearlineCache { get; set; }

        /// <summary>The input view to refocus after a modal confirm dialog closes, wired from
        /// Program.cs. May be null (e.g. a test host that never opens a dialog). Setting focus here
        /// is best-effort: the important part is that the non-blocking dialog leaves the main loop
        /// pumping, so keys work again even if this is null.</summary>
        public View FocusInputView { get; set; }

        /// <summary>Max characters of a recalled result returned to the model (mirrors the history cap).</summary>
        private const int MaxRecallChars = 50_000;

        async Task<string> IAgenticHost.RecallCacheAsync(string handle)
        {
            await Task.CompletedTask; // keep signature async; cache access is synchronous

            if (NearlineCache == null)
                return "[recall_cache] nearline cache is not available in this host.";
            if (string.IsNullOrWhiteSpace(handle))
                return "[recall_cache] no handle provided. Pass a handle like \"nl-7\" or a cache key like \"read:file.cs\".";

            // Accept either a breadcrumb handle ("nl-7") or a raw cache key ("read:file.cs",
            // "tool:call_3") — after a brainwash the breadcrumbs are gone, so keys advertised
            // in the synthetic prompt must be recallable directly.
            string key = NearlineCache.GetKeyForHandle(handle) ?? handle;

            string content = NearlineCache.Retrieve(key);
            if (content == null)
                return $"[recall_cache] no cached content for '{handle}' — unknown handle/key, or the entry was evicted.";

            if (content.Length > MaxRecallChars)
            {
                int originalLength = content.Length;
                content = content.Substring(0, MaxRecallChars) + $"\n[truncated — {originalLength} chars]";
            }

            AppendOutputLocal($"[RECALL] {handle} → {key} ({content.Length} chars)\n", OutputColor.Dim);
            return content;
        }

        async Task<string> IAgenticHost.ListCacheAsync()
        {
            await Task.CompletedTask; // keep signature async; cache access is synchronous

            if (NearlineCache == null)
                return "[list_cache] nearline cache is not available in this host.";
            return NearlineCache.BuildManifest();
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
        private FileResolution ResolveLspPath(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return null;
            return FindFile(SafeGetFileName(filename), filename.Replace('\\', '/'));
        }

        // ── Read-side tools: thin adapters over FileReadTools ────────────────────
        // read_file / grep_file / find_in_files / list_files / diff_file behave identically
        // on every surface (FileReadTools owns them). What stays here is the TUI's own
        // rendering (transcript line, syntax-highlighted listing, painted diff) and the
        // write-guard set.

        private FileReadTools ReadTools => new FileReadTools(FileReadPolicy.Agent,
            _shellRunner.WorkingDirectory, _fileCache, _filesRead, _fileSnapshots, _shellRunner);

        /// <summary>Prints a read-side result's transcript line and returns its text.</summary>
        private string Report(FileReadResult r)
        {
            if (r.LogLine != null) AppendOutputLocal(r.LogLine + "\n", r.LogColor);
            return r.Text;
        }

        // ── IAgenticHost.LoadFileContentAsync ────────────────────────────────────

        async Task<string> IAgenticHost.LoadFileContentAsync(
            string fileName, int rangeStart, int rangeEnd, bool forceFullRead)
        {
            if (FileReadTools.IsGitRequest(fileName))
                return Report(await ReadTools.ReadGitAsync(fileName, rangeStart, CancellationToken));

            try
            {
                // The tool call maps an absent start_line/end_line to 0.
                FileReadResult r = ReadTools.Read(fileName,
                    rangeStart > 0 ? rangeStart : null, rangeEnd > 0 ? rangeEnd : null, forceFullRead);
                if (r.FileLoaded) _taskReadFiles.MarkKnown(r.ResolvedPath);
                string text = Report(r);
                // List what was read syntax-highlighted (range or full read). Outline reads
                // stay terse so agentic loops don't flood the transcript.
                if (r.ListingContent != null)
                    AppendHighlightedListing(r.ListingContent, r.ResolvedPath, r.ListingLineCount);
                return text;
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[READ ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return $"[ERROR reading {fileName}: {ex.Message}]";
            }
        }

        // ── IAgenticHost.GrepFileAsync ────────────────────────────────────────────

        Task<string> IAgenticHost.GrepFileAsync(string pattern, string filename, int? startLine, int? endLine)
        {
            FileReadResult r = ReadTools.Grep(pattern, filename, startLine, endLine);
            if (r.MatchCount > 0) _taskReadFiles.MarkKnown(r.ResolvedPath);
            return Task.FromResult(Report(r));
        }

        // ── IAgenticHost.FindInFilesAsync ─────────────────────────────────────────

        Task<string> IAgenticHost.FindInFilesAsync(string pattern, string globPattern, int? startLine, int? endLine)
            => Task.FromResult(Report(ReadTools.Find(pattern, globPattern, root: null, startLine, endLine)));

        // ── IAgenticHost.ListFilesAsync ───────────────────────────────────────────

        Task<string> IAgenticHost.ListFilesAsync(string glob, bool recursive, CancellationToken cancellationToken)
            => Task.FromResult(Report(ReadTools.List(glob, recursive, root: null, cancellationToken)));

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

            // Builds before testing (H-11) and carries the blame-hang guard (H-08).
            string cmd = DotnetTestCommand.CommandLine(project, filter);

            AppendOutputLocal($"[TEST] > {cmd}\n", OutputColor.Dim);

            try
            {
                using var call = _shellInterrupt.Begin(CancellationToken);
                var (output, exitCode) = await _shellRunner.ExecuteAsync(
                    cmd, CancellationToken, timeoutSeconds, interruptToken: call.Token);
                if (call.End() != null)
                {
                    AppendOutputLocal(OverrideSteerCancelledLine + "\n", OutputColor.Warning);
                    return _shellInterrupt.ResultFor(output);
                }
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
            FileReadResult r = ReadTools.Diff(filename);
            if (r.DiffOld == null) return Task.FromResult(Report(r));

            // The TUI paints the diff (same model a patch uses) under a count line; what the
            // model receives is still the unified-diff text, untouched.
            AppendOutputLocal($"[DIFF] {filename} ({r.DiffOld.Split('\n').Length} → {r.DiffNew.Split('\n').Length} lines)\n", OutputColor.Dim);
            AppendDiff(r.DiffOld, r.DiffNew, filename);
            return Task.FromResult(r.Text);
        }

        // ── IAgenticHost.ResolvePatchAsync ────────────────────────────────────────

        async Task<(PatchResolveResult, string)> IAgenticHost.ResolvePatchAsync(string patchContent, bool fromToolCall)
        {
            try
            {
                string firstLine = (patchContent ?? string.Empty).Split('\n')[0];
                string blockFileName = firstLine.Length > 5 ? firstLine.Substring(5).Trim() : string.Empty;

                if (string.IsNullOrEmpty(blockFileName))
                {
                    AppendOutputLocal("[PATCH] No filename specified.\n", OutputColor.Error);
                    return (null, "No filename specified in the PATCH header — the first line must be 'PATCH <filename>'.");
                }

                string normalizedHint = blockFileName.Replace('\\', '/');
                string fileNameOnly   = SafeGetFileName(blockFileName);

                FileResolution res = FindFile(fileNameOnly, normalizedHint);
                string fullPath = res.Path
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileNameOnly);

                if (!File.Exists(fullPath))
                {
                    AppendOutputLocal($"[PATCH] File not found: {fullPath}\n", OutputColor.Warning);
                    return (null, $"File not found: {fullPath}");
                }

                // Write guard — AFTER resolution, on the path that will actually be written
                // (bare-name keying let an unread same-named file pass; see TaskReadSet).
                if (!_taskReadFiles.IsKnown(fullPath))
                {
                    bool approved = await ConfirmUnreadFileWriteAsync(fullPath);
                    if (!approved)
                    {
                        AppendOutputLocal($"[WRITE GUARD] Patch to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                        return (null, $"Write guard declined — READ {fileNameOnly} first, then retry the patch against its current content.");
                    }
                    _taskReadFiles.MarkKnown(fullPath);
                }

                _fileCache.InvalidateIfStale(FileCacheKey(fullPath), fullPath); // out-of-band writes
                if (!_fileCache.Contains(FileCacheKey(fullPath)))
                {
                    AppendOutputLocal($"[AUTO-READ] Loading {fileNameOnly} before patch...\n", OutputColor.Dim);
                    var (cached, _enc) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                    _fileCache.Store(FileCacheKey(fullPath), cached);
                    _filesRead.Add(FileReadTools.FileKey(fullPath)); // read-set is keyed by full path
                    _taskReadFiles.MarkKnown(fullPath); // same keying as the guard above
                }

                CaptureFileSnapshot(fullPath);

                var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                var patchResult = PatchEngine.ResolvePatch(patchContent, fullPath, blockFileName, content, encoding,
                    fromToolCall, (text, color) => AppendOutputLocal(text, color));
                return (patchResult, patchResult == null
                    ? "FIND/REPLACE could not be resolved against the file (FIND text not matching, ambiguous, or no-op). The diagnostic above names the cause; act on it — do not retry the identical patch."
                    : null);
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return (null, $"Resolve error: {ex.Message}");
            }
        }

        // ── IAgenticHost.ApplyResolvedPatchAsync ──────────────────────────────────

        Task<(string, string)> IAgenticHost.ApplyResolvedPatchAsync(PatchResolveResult resolved)
        {
            try
            {
                // Block if a conflict is pending
                if (_pendingConflict != null)
                {
                    AppendOutputLocal($"[MERGE CONFLICT] Cannot apply patch — pending conflict on \"{_pendingConflict.FilePath}\" must be resolved first. Use /resolve accept_proposed, /resolve accept_current, or /resolve cancel.\n", OutputColor.Error);
                    return Task.FromResult<(string, string)>((null,
                        $"Blocked by a pending merge conflict on {_pendingConflict.FilePath} — that conflict must be resolved before this patch can apply."));
                }

                string fileNameOnly = SafeGetFileName(resolved.FullPath);
                string currentText = File.ReadAllText(resolved.FullPath);
                string baseText = _fileCache.GetFull(FileCacheKey(resolved.FullPath));
                string proposedText = ComputePatchedContent(resolved);

                MergeCheckResult merge = ThreeWayMergeCheck.CheckAndMerge(baseText, proposedText, currentText);

                MergeReport.TraceFallback("TUI ApplyResolvedPatchAsync", fileNameOnly, merge);

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
                    return Task.FromResult<(string, string)>((null,
                        $"Merge conflict — the file changed outside this patch since it was read. The conflict regions are shown above; re-READ {fileNameOnly} and re-issue the patch against its current content."));
                }

                // No conflicts — apply patch to disk
                string backupDir = Path.Combine(Path.GetTempPath(), "DevMind");
                var result = PatchEngine.ApplyPatch(resolved, backupDir);

                if (!result.Success)
                {
                    AppendOutputLocal($"[PATCH] Error: {result.Error}\n", OutputColor.Error);
                    // H-34: a non-landing edit is reported as what it is ("edit N did not land …"),
                    // never as a write failure the model might simply retry.
                    return Task.FromResult<(string, string)>((null,
                        result.NotLanded || result.Rejected ? result.Error : $"Write failed: {result.Error}"));
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

                // This line deliberately reports no backup-stack depth. The stack is an internal
                // safety net that is only ever pushed to, evicted from, and drained — nothing
                // restores from it, and there is no operator command or tool that can. Naming a
                // depth here told the model it held N reversals it had no way to spend.
                var patched = WriteEcho.Describe(resolved.FullPath, resolved.FullPath,
                                                 _shellRunner.WorkingDirectory, merge.Mode);
                AppendOutputLocal($"[PATCH] Applied to {patched.Detail}\n", patched.Color);

                // Show what changed, painted rather than printed
                AppendDiff(resolved.OriginalContent, result.UpdatedContent, fileNameOnly);

                return Task.FromResult((resolved.FullPath, (string)null));
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return Task.FromResult<(string, string)>((null, $"Apply error: {ex.Message}"));
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
                    string written = TextFileFormat.WritePreserving(pc.FilePath, pc.ProposedContent);
                    string fileNameOnly = SafeGetFileName(pc.FilePath);
                    _fileCache.Store(FileCacheKey(pc.FilePath), written);
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

        // ── Painted diffs ─────────────────────────────────────────────────────────

        /// <summary>
        /// Paint a diff into the transcript: dim line-number gutter, <c>+</c>/<c>-</c> marker,
        /// and the source still syntax-highlighted on a red or green tint.
        /// <para>
        /// The decisions live in <see cref="DiffPainter"/>, which holds no view, so what colour
        /// a line gets is a testable question. All this does is wire the span sink and own the
        /// palette.
        /// </para>
        /// </summary>
        internal void AppendDiff(string oldContent, string newContent, string path)
            => Record(TranscriptEntry.Diff(oldContent, newContent, path));

        // ── Approval mode ─────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the approval mode currently in force. A delegate, not a stored value: the
        /// executor reads <c>options.ApprovalMode</c> fresh at every dispatch, and /mode and
        /// Shift+Tab both write it through <see cref="ApprovalModeControl.Apply"/> while a turn
        /// may be running. A copy taken when the host was built would be the mode at launch,
        /// which is a different and silently wrong answer. Unset reads as
        /// <see cref="ApprovalMode.Auto"/>, the long-standing behaviour.
        /// </summary>
        public Func<ApprovalMode> ApprovalModeProvider { get; set; }

        private ApprovalMode CurrentApprovalMode
            => ApprovalModeProvider != null ? ApprovalModeProvider() : ApprovalMode.Auto;

        // ── IAgenticHost.ShowDiffPreviewAsync ─────────────────────────────────────
        // The patch card. In Auto it shows a fuzzy match and approves it; in Manual it shows
        // every patch and ASKS — which is the whole of what Manual mode means for a patch,
        // because the executor gates patches by forcing this card rather than by asking a
        // question of its own.

        async Task<List<int>> IAgenticHost.ShowDiffPreviewAsync(
            List<PatchResolveResult> resolvedPatches, CancellationToken cancellationToken)
        {
            var approved = new List<int>();
            ApprovalMode mode = CurrentApprovalMode;

            // Plan mode: no card at all. The executor normally refuses patches before the
            // card is reached (and tests pin that); this is the second line, so a host that
            // is ever driven by a path that skips the executor's gate still cannot apply a
            // patch in plan mode. Nothing is approved, and no question is asked.
            if (mode == ApprovalMode.Plan)
            {
                foreach (var r in resolvedPatches)
                    AppendOutputLocal(
                        $"[PLAN MODE] Refused: patch {r.FileName}\n", OutputColor.Dim);
                return approved;
            }

            for (int i = 0; i < resolvedPatches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var r = resolvedPatches[i];
                string badge = r.Confidence == PatchConfidence.Fuzzy ? " [Fuzzy ⚠]" : " [Exact ✓]";

                AppendOutputLocal($"\n[PATCH] {r.FileName}{badge}\n", OutputColor.Dim);
                // The same painter as an applied patch. A preview that renders differently from
                // the thing it previews is worse than no preview: the reader learns to check
                // twice, once for the change and once for the rendering.
                AppendDiff(r.OriginalContent, ComputePatchedContent(r), r.FileName);

                bool ok = await PatchCardDecision.ApproveAsync(mode, r.Confidence, async () =>
                {
                    // The diff has to be ON SCREEN before the question covers it. Appends are
                    // queued for the render pump, so without this the dialog can open over a
                    // transcript that has not caught up.
                    await FlushNowAsync();
                    return await ConfirmAsync(
                        "Apply patch",
                        PatchCardDecision.Question(r.FileName, r.Confidence),
                        "_Apply", "_Skip");
                });

                if (ok)
                {
                    approved.Add(i);
                    // Only Auto says "auto-approved". In Manual the operator approved it, and
                    // the [PATCH] Applied line the executor prints next is the record.
                    if (mode != ApprovalMode.Manual)
                        AppendOutputLocal($"[PATCH] Auto-approved ({i + 1}/{resolvedPatches.Count})\n", OutputColor.Dim);
                }
                else
                {
                    // The model is told by the executor, which injects a SKIPPED tool result
                    // for every index this list leaves out.
                    AppendOutputLocal(PatchCardDecision.DeclinedLine(r.FileName), OutputColor.Warning);
                }
            }

            return approved;
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
        // Internal (not private) so the test seam CoalesceFlushRuns can return the list:
        // the test reads Start/Length/NonCopyable off the spans the live flush records.
        internal readonly struct ColorSpan
        {
            public readonly int Start;
            public readonly int Length;
            public readonly Terminal.Gui.Drawing.Attribute Attr;

            /// <summary>
            /// True for display chrome the reader should not copy — the line-number gutter on
            /// the model's fenced code. A copy of a selection skips these offsets, so the
            /// clipboard gets the code the model wrote, not the gutter it was drawn with.
            /// </summary>
            public readonly bool NonCopyable;

            public ColorSpan(int start, int length, Terminal.Gui.Drawing.Attribute attr, bool nonCopyable = false)
            {
                Start       = start;
                Length      = length;
                Attr        = attr;
                NonCopyable = nonCopyable;
            }
        }

        // The recorded appends, append-only and strictly increasing in Start (every insert
        // lands at the document end), so a binary search finds the span covering an offset.
        // One class, because a flag the COPY path needs (NonCopyable) belongs on the data the
        // DRAW path paints from — the two read the same list and cannot drift.
        private sealed class ColorSpanList
        {
            private readonly List<ColorSpan> _spans = new List<ColorSpan>();

            public void Add(ColorSpan span) => _spans.Add(span);

            public void AddRange(IEnumerable<ColorSpan> spans) => _spans.AddRange(spans);

            public void Clear() => _spans.Clear();

            public int Count => _spans.Count;

            public ColorSpan this[int index] => _spans[index];

        public void RemoveAt(int index) => _spans.RemoveAt(index);

        public IEnumerator<ColorSpan> GetEnumerator() => _spans.GetEnumerator();

            /// <summary>True when <paramref name="offset"/> falls inside a non-copyable span.</summary>
            public bool IsNonCopyable(int offset)
            {
                int idx = FindSpanIndex(offset);
                return idx < _spans.Count && _spans[idx].NonCopyable;
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
            private readonly ColorSpanList _spans;

            public OffsetColorTransformer(ColorSpanList spans) => _spans = spans;

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

        /// <summary>Write guard for files never read this task. The TUI auto-approves —
        /// a human is watching — but still echoes the resolved path so an unread write
        /// (e.g. to a hallucinated absolute path) is visible in the transcript, which is
        /// the only audit surface this host has. Mirrors the headless journal line.</summary>
        private Task<bool> ConfirmUnreadFileWriteAsync(string resolvedPath)
        {
            AppendOutputLocal(
                $"[WRITE GUARD] \"{resolvedPath}\" was not read during this task — auto-approved.\n",
                OutputColor.Warning);
            return Task.FromResult(true);
        }

        private string ResolveWritePath(string fileName)
        {
            if (Path.IsPathRooted(fileName)) return fileName;
            return Path.Combine(_shellRunner.WorkingDirectory ?? Directory.GetCurrentDirectory(), fileName);
        }

        /// <summary>
        /// Resolves a filename to its full path. Delegates to the shared
        /// <see cref="FilePathResolver"/> (same rules as the MCP server and headless host):
        /// absolute path → hint-relative to workingDir → bare name in workingDir →
        /// git-root fallback (repo-root-relative hints) → recursive basename search
        /// that refuses to guess an arbitrary same-named file.
        /// </summary>
        private FileResolution FindFile(string fileNameOnly, string hintPath)
            => FilePathResolver.Resolve(fileNameOnly, hintPath,
                _shellRunner.WorkingDirectory);

        /// <summary>
        /// "File not found" message that states the resolution SCOPE (which directories
        /// were searched, including the git root) and lists same-named candidates. Never
        /// presents the working directory's top-level files as the project — the model
        /// reads that as ground truth and concludes source files are missing (job-471).
        /// </summary>
        private string BuildFileNotFoundMessage(string directive, string filename, FileResolution resolution)
            => FilePathResolver.BuildFileNotFoundMessage(directive, filename, resolution);

        /// <summary>
        /// Fallback for call sites that only hold a path — delegates to the shared
        /// re-resolving overload, whose normalization is guarded so an empty or
        /// invalid-character filename yields a clean "not found" instead of throwing
        /// (SafeGetFileName already tolerated such input at the first-resolve sites).
        /// </summary>
        private string BuildFileNotFoundMessage(string directive, string filename)
            => FilePathResolver.BuildFileNotFoundMessage(directive, filename,
                _shellRunner.WorkingDirectory);

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

        private void CaptureFileSnapshot(string fullPath) => _fileSnapshots.Capture(fullPath);

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
    }
}
