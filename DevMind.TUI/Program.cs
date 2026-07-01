// File: Program.cs  v3.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Terminal.Gui v2 TUI for DevMind.
// Drives DevMind.Core's agentic engine through IAgenticHost + ILoopCallbacks.
//
// Layout:
//   Row 0: Output area (gui-cs/Editor, fills remaining space)
//   Row 1: Input box (TuiInputBox, bordered, grows 1..6 rows)
//   Row 2: Status bar (TuiStatusBar, composed labels)
//
// Launch:
//   dotnet run --project DevMind.TUI -- --dir C:\path\to\project
//   dotnet run --project DevMind.TUI -- --endpoint http://127.0.0.1:1234/v1 --api-key lm-studio
//
// Environment variables (same as CLI):
//   DEVMIND_ENDPOINT     — LLM endpoint URL (overrides default)
//   DEVMIND_API_KEY      — API key (overrides default)
//   DEVMIND_SERVER_TYPE  — force backend (vllm|llama|lmstudio|custom); overrides auto-detection

// Suppress obsolete warnings for Terminal.Gui v2 legacy APIs.
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Terminal.Gui.Editor.Document;
using GuiEditor = Terminal.Gui.Editor.Editor;

namespace DevMind
{
   internal static class Program
    {
        // Global TUI config — loaded at startup, persisted by slash commands.
        static TuiConfig _config;

        // ── Ctrl+C / Esc state machine ──────────────────────────────────────────
        // Tracks whether a turn is currently running (set around RunTurnAsync calls).
        static bool _isTurnRunning;
        // Double-press-to-exit: armed after first Ctrl+C when idle.
        static bool _ctrlCArmed;
        static DateTime _ctrlCArmedTime;
        // Armed-state expiry (seconds). Second press must land within this window.
        const int CtrlCArmedExpirySeconds = 3;

        // Clear the armed state and any warning message. Called on any user activity
        // (typing, submitting, state change) so the warning doesn't persist.
        static void ClearCtrlCArmed(IApplication app, TuiStatusBar statusBar)
        {
            if (_ctrlCArmed)
            {
                _ctrlCArmed = false;
                _ctrlCArmedTime = default;
                app.Invoke(() => statusBar.SetReady());
            }
        }
       static async Task<int> Main(string[] args)
        {
            // Load ~/.devmind.env before any env var reads.
            EnvFileLoader.Load();

            // Load global TUI config from %APPDATA%\devmind\devmind.json.
            _config = TuiConfig.Load();

            // Parse args — same pattern as CLI.
            var options = TuiOptions.FromArgs(args);

            // Apply config working directory (lower priority than CLI --dir).
            if (!string.IsNullOrEmpty(_config.WorkingDirectory) &&
                Directory.Exists(_config.WorkingDirectory))
            {
                // Only apply if --dir was not passed on the command line.
                if (Array.IndexOf(args, "--dir") < 0)
                    options.WorkingDirectory = _config.WorkingDirectory;
            }

            // Apply persisted depth cap (lower priority than CLI --max-depth). 0 means
            // "not set" in config; only override the default when a valid value was saved.
            if (_config.DepthCap > 0 && Array.IndexOf(args, "--max-depth") < 0)
                options.AgenticLoopMaxDepth = _config.DepthCap;

            // Apply persisted context limit (lower priority than CLI --context-limit). -1 means
            // "not set"; 0 = explicitly disabled; 1-99 = the limit percent.
            if (_config.ContextLimitPercent >= 0 && Array.IndexOf(args, "--context-limit") < 0)
                options.AgenticContextLimitPercent = _config.ContextLimitPercent;

            // Context file discovery.
            string devMindContext = LoadContextFile(options.WorkingDirectory);

           // Construct core objects.
            var llmClient = new LlmClient(options);
            llmClient.Configure(options.EndpointUrl, options.ApiKey);

            // History store — created from DEVMIND_HISTORY_* env vars.
            // Falls back to NullHistoryStore when history is disabled.
            var historyStore = HistoryStoreFactory.Create();
            try { historyStore.InitAsync().Wait(); } catch { /* non-fatal — NullHistoryStore fallback */ }

            var cts = new CancellationTokenSource();

            // ── Terminal.Gui v2 Application ────────────────────────────────────────

            IApplication app = Application.Create();
            app.Init();

            // Input responsiveness. Terminal.Gui 2.4.4 throttles the main loop — which both
            // drains queued input (InputProcessor.ProcessQueue) and redraws — to
            // Application.MaximumIterationsPerSecond (default 25). ApplicationMainLoop.Iteration
            // enforces that cap with an UNCONDITIONAL Task.Delay(1000/IPS - iterationTime).Wait(),
            // so at 25/s a keystroke sits in the input queue up to ~40 ms before it is processed
            // and the field repaints — perceptible per-character lag, present on a fresh session.
            //
            // Verified against the decompiled 2.4.4 assembly (not guessed): a TextField keystroke
            // routes OnKeyDownNotHandled → InsertText(Key) → Text setter → Adjust(), which calls
            // only SetNeedsDraw() — never SetNeedsLayout(). So View.Layout returns false and
            // LayoutAndDraw issues a non-forced View.Draw that repaints ONLY the dirty input field;
            // the WordWrap output TextView is a clean sibling and is skipped (no per-key re-wrap).
            // The lag is therefore the iteration throttle, not a full-screen redraw.
            //
            // Raise the cap so input is serviced within a few ms. 200/s ⇒ a ~5 ms floor:
            // imperceptible, negligible idle CPU (an idle iteration draws nothing), and well clear
            // of the sub-millisecond range where the loop's `delay.Milliseconds > 0` guard would
            // collapse into a CPU-pinning busy spin.
            //
            // 200 (not the previous 750) is the documented sweet spot above: the main loop runs an
            // UNCONDITIONAL Task.Delay(...).Wait() every iteration, so each iteration allocates a Task
            // + timer registration inside Terminal.Gui. At 750/s that is ~5.4M allocations over a 2-hour
            // idle session feeding the framework's loop; 200/s cuts that ~3.75× while keeping the floor
            // at ~5 ms — already below the threshold of perceptible per-keystroke lag. Going lower
            // (30–60 Hz) would reintroduce the ~40 ms input lag this cap was raised to fix.
            Application.MaximumIterationsPerSecond = 200;

            // Paste-pipeline diagnostics (inert unless DEVMIND_TUI_DIAG is set).
            // Three rungs of the bracketed-paste ladder, outermost first:
            //   Driver.Paste  — the driver's input processor parsed ESC[200~…201~
            //   App.Paste     — ApplicationImpl.RaisePasteEvent reached (also logs which
            //                   view is focused — Command.Paste is invoked on THAT view)
            // The remaining rungs (View.Pasting, KeyDown Ctrl+V, InsertAtCaret) are
            // traced inside TuiInputBox. One paste attempt should light up a contiguous
            // prefix of this ladder; where the trace stops is where the chain is broken.
            if (app.Driver != null)
                app.Driver.Paste += (s, text) =>
                    TuiAgenticHost.Diag($"[PASTE] Driver.Paste len={text?.Length ?? -1}");

            // Main window.
            using Window window = new() { Title = "DevMind TUI — Enter send · Ctrl+Enter newline · Ctrl+C copies · F10 quits · Esc interrupts" };

           // Output pane — programmatically-written, non-interactive log (gui-cs/Editor).
            //
            // ReadOnly = true. Editor's ReadOnly is a COMMAND guard only — it blocks user
            // edit commands but NOT direct document mutation, so TuiAgenticHost appends via
            // Document.Insert(Document.TextLength, …) and the pane behaves as a true read-only
            // log. (This is the opposite of TextView 2.4.4, whose ReadOnly short-circuited
            // InsertText and forced the old ReadOnly=false hack.)
            //
            // CanFocus = false keeps the pane out of the tab order (input field is the only
            // tab stop) and non-interactive. WordWrap = true soft-wraps long lines; Editor
            // wraps per-viewport lazily (rope + incremental visual lines), so appends do not
            // re-wrap the whole transcript the way TextView did.
            //
            // Document MUST be assigned — Editor's backing TextDocument is null by default and
            // every append/CaretOffset call guards on it.
            GuiEditor outputView = new()
            {
                ReadOnly = true,
                CanFocus = false,
                Document = new TextDocument(), // assign before WordWrap so the wrap map builds against a real doc
                WordWrap = true,
                X = 0, Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 2, // leave room for input + status rows
            };

            // Black background. Editor's draw resolves the base/fill attribute from
            // GetAttributeForRole(Normal) (its Scheme.Normal); unstyled cells and the fill use
            // it. Pin Normal/Editable to light-gray-on-black so the pane is black; the per-span
            // colors set by OffsetColorTransformer take their black background from this same
            // scheme (TuiAgenticHost.ResolveAttribute reads Scheme.Normal.Background).
            var outputFg = new Terminal.Gui.Drawing.Color(0xCC, 0xCC, 0xCC);
            var outputBg = new Terminal.Gui.Drawing.Color(0x00, 0x00, 0x00);
            outputView.SetScheme(new Terminal.Gui.Drawing.Scheme(outputView.GetScheme())
            {
                Normal   = new Terminal.Gui.Drawing.Attribute(outputFg, outputBg),
                Editable = new Terminal.Gui.Drawing.Attribute(outputFg, outputBg),
            });


            // Multi-line bordered input box: Enter submits, Ctrl+Enter inserts a newline,
            // grows 1..6 content rows. Border blue when active.
            var inputBox = new TuiInputBox();

            // Output pane fills everything above the input box + status row.
            // The Dim.Height reference keeps the layout correct as the input box grows.
            outputView.Height = Dim.Fill(Dim.Height(inputBox.View) + 1);

            // WT bracketed-paste consumption (app level). Windows Terminal owns Ctrl+V
            // and injects the clipboard as a bracketed paste (ESC[200~…201~); the
            // keystroke never reaches the app. With the aligned 2.4.3/2.5.2 pairing,
            // Editor's own Command.Paste handler replaces core's DefaultPasteHandler
            // and reads the DRIVER clipboard, ignoring the bracketed payload — an
            // unnecessary failure surface. Consume the payload here instead:
            // IApplication.Paste fires before routing to the focused view and its
            // PasteEventArgs is cancellable, so insert the payload text directly into
            // the input box and mark the event handled. While a turn is running the
            // input is disabled (CanFocus=false) and the paste is swallowed.
            app.Paste += (s, e) =>
            {
                TuiAgenticHost.Diag($"[PASTE] App.Paste len={e.Text?.Length ?? -1} " +
                    $"focused={app.Navigation?.GetFocused()?.GetType().Name ?? "(null)"}");
                e.Handled = true;
                if (inputBox.View.CanFocus)
                    inputBox.InsertAtCaret(e.Text);
            };

            // Construct the host first — the status bar needs its LSP availability.
            var host = new TuiAgenticHost(options.WorkingDirectory, outputView, () => cts.Cancel());
            host.NearlineCache = llmClient.NearlineCache; // for the recall_cache tool

            // Status bar: composed-Labels row — state + hints left, LSP chip + context meter right.
            var (lspEnabled, lspLanguages) = host.GetLspStatus();
            var statusBar = new TuiStatusBar(ToolRegistry.ToolCount, lspEnabled, lspLanguages);

            // Add views to window.
            window.Add(outputView, inputBox.View, statusBar.Root);

            // Build-version chip, pinned to the FAR RIGHT of the window's top border
            // (the title row). Lets the user confirm which binary is running — the git
            // short-hash in the suffix distinguishes builds. Read from the running .exe's
            // AssemblyInformationalVersion (git-stamped in Directory.Build.props, e.g.
            // "1.0.232+cb7a7927"). The border view is accessed the same way TuiInputBox
            // does (Border?.GetOrCreateView), and right-aligned with Pos.AnchorEnd like
            // TuiStatusBar's right group; dim gray on black matches the other chrome.
            string buildVersion = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrEmpty(buildVersion))
            {
                string verText = "v" + buildVersion;
                View titleBar = window.Border?.GetOrCreateView();
                if (titleBar != null)
                {
                    var verLabel = new Label
                    {
                        Text = verText,
                        // Left edge at (Width - len - 1): text ends one column shy of the
                        // right corner glyph, which is left intact.
                        X = Pos.AnchorEnd(verText.Length + 1),
                        Y = 0,
                        Width = Dim.Auto(DimAutoStyle.Text, null, null),
                        Height = 1,
                        CanFocus = false,
                    };
                    verLabel.SetScheme(new Terminal.Gui.Drawing.Scheme(verLabel.GetScheme())
                    {
                        Normal = new Terminal.Gui.Drawing.Attribute(
                            new Terminal.Gui.Drawing.Color(0x88, 0x88, 0x88),
                            new Terminal.Gui.Drawing.Color(0x00, 0x00, 0x00)),
                    });
                    titleBar.Add(verLabel);
                }
            }

            // Construct callbacks with references to TUI views.
            var callbacks = new TuiLoopCallbacks(llmClient, statusBar, host, inputBox.View);
            var state = new LoopState();
            // Training-turn capture: Core owns the WHEN/call; the host supplies the logger from
            // config. Session id resolves at write time (so /new rolls to a fresh file); a blank
            // folder falls back to training_logs/ beside the exe.
            var trainingLogger = new JsonlTrainingLogger(
                SessionId.Get, _config.TrainingLogEnabled, _config.TrainingLogFolder);
            var driver = new LoopDriver(llmClient, host, callbacks, options, state, trainingLogger);

            // Word wrap is set directly at construction (WordWrap = true). Unlike TextView
            // 2.4.4 — whose wrap setter at viewport width 0 degenerated the wrap map and forced
            // a deferred SubViewsLaidOut enable plus an InsertionPoint reset — Editor computes
            // its wrap map lazily per draw against the live viewport, so enabling it before the
            // first layout is harmless. The SubViewsLaidOut re-fix handler is gone with the
            // reflection-based color stamping: OffsetColorTransformer re-applies colors during
            // every visual-line build, so wrap re-layouts (including window resize) recolor
            // automatically with no extra wiring.

            // Diagnostic self-test (active only when DEVMIND_TUI_DIAG is set): append colored
            // sample lines through the exact streaming path (background thread → App.Invoke)
            // shortly after startup, so the color pipeline can be verified end to end in the
            // real app without an LLM turn.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DEVMIND_TUI_DIAG")))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    host.AppendOutputLocal("[DIAG] success green\n", OutputColor.Success);
                    host.AppendOutputLocal("[DIAG] error red\n", OutputColor.Error);
                    host.AppendOutputLocal("[DIAG] input blue\n", OutputColor.Input);
                    host.AppendOutputLocal(
                        "[DIAG] warning amber, long line to test wrapping: " + new string('x', 200) + "\n",
                        OutputColor.Warning);

                    // After the next draw, dump the DRIVER's screen buffer — ground truth of
                    // what was actually painted, including per-cell foreground colors.
                    await Task.Delay(1500);
                    app.Invoke(() =>
                    {
                        var contents = app.Driver?.Contents;
                        if (contents == null)
                        {
                            TuiAgenticHost.Diag("[SCREEN] driver Contents is null");
                            return;
                        }
                        int rows = contents.GetLength(0);
                        int cols = contents.GetLength(1);
                        for (int r = 0; r < Math.Min(rows, 18); r++)
                        {
                            var rowText = new StringBuilder();
                            var fgs = new System.Collections.Generic.HashSet<string>();
                            for (int c = 0; c < cols; c++)
                            {
                                var cell = contents[r, c];
                                rowText.Append(cell.Grapheme);
                                if (cell.Attribute.HasValue)
                                    fgs.Add(cell.Attribute.Value.Foreground.ToString());
                            }
                            string txt = rowText.ToString().TrimEnd();
                            if (txt.Length > 60) txt = txt.Substring(0, 60);
                            TuiAgenticHost.Diag($"[SCREEN] row={r} fgs=[{string.Join(",", fgs)}] \"{txt}\"");
                        }
                    });
                });
            }

           // Build combined system prompt (initial — scratchpad is empty at startup).
             string combinedSystemPrompt = BuildCombinedSystemPrompt(options, devMindContext, _config.BehavioralRules, host.TaskScratchpad);

            // Banner. All output MUST go through the host (never outputView.InsertText
            // directly) - TuiAgenticHost tracks the logical append position for color
            // stamping, and a bypassing insert would desync it.
            host.AppendOutputLocal(
                $"DevMind TUI  ·  {options.EndpointUrl}  ·  {options.WorkingDirectory}\n",
                OutputColor.Dim);
            host.AppendOutputLocal($"{TuiAgenticHost.ScrollbackCapDescription}\n\n", OutputColor.Dim);

            // Focus the input field. Setting focus before the loop runs is unreliable in
            // Terminal.Gui v2 (layout/focus is resolved during app.Run), so also re-assert it
            // once the window is initialized. With outputView non-focusable and the status
            // bar non-focusable, the input box is the only tab stop - but this guarantees
            // the cursor lands there on startup.
            inputBox.View.SetFocus();
            window.Initialized += (s, e) => inputBox.View.SetFocus();

            // Ctrl+C state machine + Esc handler (app-wide, bound on the instance keyboard).
            // Ctrl+C: (a) copy selection, (b) cancel turn, (c) double-press to exit.
            // Esc: cancel turn if running, otherwise no-op (must NOT quit).
            app.Keyboard.KeyDown += (s, key) =>
            {
                // ── Quit ── F10 or Ctrl+Q, unconditional, in any state (idle or mid-turn).
                // F10 is the dependable one: Ctrl+Q is XON flow-control on many terminals and may
                // be swallowed before it reaches the app. (/quit and /exit also work as commands.)
                if (key.KeyCode == Key.F10.KeyCode || key.KeyCode == Key.Q.WithCtrl.KeyCode)
                {
                    key.Handled = true;
                    cts.Cancel();        // ask any running turn to stop
                    app.RequestStop();   // exit the main loop → process returns
                    return;
                }

                // ── Ctrl+C ──────────────────────────────────────────────────────
                if (key.KeyCode == Key.C.WithCtrl.KeyCode)
                {
                    key.Handled = true;

                    // (a) If output pane has a text selection → copy to clipboard.
                    if (!string.IsNullOrEmpty(outputView.SelectedText))
                    {
                        CopySelectionToClipboard(outputView, host);
                        ClearCtrlCArmed(app, statusBar);
                        return;
                    }

                    // (b) If a turn is running → cancel it.
                    if (_isTurnRunning)
                    {
                        cts.Cancel();
                        ClearCtrlCArmed(app, statusBar);
                        return;
                    }

                    // (c) Idle, no selection → double-press to exit.
                    if (_ctrlCArmed && (DateTime.UtcNow - _ctrlCArmedTime).TotalSeconds < CtrlCArmedExpirySeconds)
                    {
                        // Second press within window → exit.
                        _ctrlCArmed = false;
                        app.RequestStop();
                        return;
                    }

                    // First press → arm the warning.
                    _ctrlCArmed = true;
                    _ctrlCArmedTime = DateTime.UtcNow;
                    statusBar.SetBusy("Press Ctrl+C again to exit");
                }

                // ── Esc ─────────────────────────────────────────────────────────
                if (key.KeyCode == Key.Esc.KeyCode)
                {
                    key.Handled = true;

                    if (_isTurnRunning)
                    {
                        cts.Cancel();
                        return;
                    }

                    // Idle: clear input if non-empty, do NOT exit.
                    if (!string.IsNullOrEmpty(inputBox.Text?.Trim()))
                    {
                        inputBox.Clear();
                    }
                }
            };

            // Clear Ctrl+C armed state on any keystroke in the input box — EXCEPT Ctrl+C
            // itself. The app-level handler arms the exit on Ctrl+C; if this handler then
            // cleared it on the same keystroke, the second press would re-arm instead of
            // exiting (the "Ctrl+C to exit is hijacked" bug). Only a DIFFERENT key disarms.
            inputBox.View.KeyDown += (s, key) =>
            {
                if (key.KeyCode == Key.C.WithCtrl.KeyCode) return;
                ClearCtrlCArmed(app, statusBar);
            };

           // Handle Enter in input field — submit to LLM.
            inputBox.View.Accepting += async (s, e) =>
            {
                // Any user activity clears the Ctrl+C armed state.
                ClearCtrlCArmed(app, statusBar);

                // Mark the Accept command handled so it does not bubble to the SuperView.
                // In Terminal.Gui v2, an unhandled Accepting raises Command.Accept on the
                // SuperView (per View.RaiseAccepting), which can trigger a default-button /
                // toplevel Accept we don't want. We own Enter on this field.
                e.Handled = true;

                string input = inputBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(input)) return;

                // Clear input.
                inputBox.Clear();

                // Echo user input (through the host — keeps the stamping tracker in sync).
                host.AppendOutputLocal($"\n> {input}\n", OutputColor.Input);

                // ── Reliable exit ───────────────────────────────────────────────
                // Ctrl+Q is XON flow-control on many terminals and never reaches the app, so a
                // typed command is the dependable way out. Submitted as normal input → no special-
                // key interception possible.
                if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
                    input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                {
                    cts.Cancel();        // stop any running turn
                    app.RequestStop();   // exit the main loop → process returns
                    return;
                }

                // Refresh CTS after a cancelled turn.
                if (cts.IsCancellationRequested)
                {
                    var old = cts;
                    cts = new CancellationTokenSource();
                    old.Dispose();
                }

                // ── Slash-command dispatch ──────────────────────────────────────
                // Intercept slash commands at the input boundary so they never
                // burn model tokens. The dispatcher routes to registered handlers.
                if (SlashCommand.IsSlashCommand(input))
                {
                   // Build the command context with callbacks into TUI state.
                    var cmdCtx = new CommandContext
                    {
                        DepthCap = options.AgenticLoopMaxDepth,
                        ContextLimitPercent = options.AgenticContextLimitPercent,
                        ThinkingEnabled = options.ShowLlmThinking,
                       SystemPrompt = llmClient.SystemPromptContent ?? BuildCombinedSystemPrompt(options, devMindContext, _config.BehavioralRules, host.TaskScratchpad),
                       ResetConversation = () =>
                        {
                            state.ResetForUserTurn();
                            llmClient.ClearHistory();
                            host.ResetSession();
                            SessionId.Reset(); // new session ID for next turn
                            var oldCts = cts;
                            cts = new CancellationTokenSource();
                            oldCts.Dispose();
                        },
                        SetDepthCap = (n) =>
                        {
                            options.AgenticLoopMaxDepth = n;
                            _config.DepthCap = n;
                            _config.Save();
                        },
                        SetContextLimitPercent = (n) =>
                        {
                            options.AgenticContextLimitPercent = n;
                            _config.ContextLimitPercent = n;
                            _config.Save();
                        },
                        SetThinking = (on) => { options.ShowLlmThinking = on; },
                        // History fields.
                        HistoryStore = historyStore,
                        SessionId = SessionId.Get(),
                        MachineName = SessionId.GetMachineName(),
                       PrependMessages = (roles, contents) => llmClient.PrependMessages(roles, contents),
                        // Nearline cache (for the /cache command).
                        NearlineCache = llmClient.NearlineCache,
                        // Behavioral rules.
                        BehavioralRules = _config.BehavioralRules,
                        SetBehavioralRules = (rules) =>
                        {
                            _config.BehavioralRules = rules;
                            _config.Save();
                        },
                      RebuildSystemPrompt = () =>
                         {
                             return BuildCombinedSystemPrompt(options, devMindContext, _config.BehavioralRules, host.TaskScratchpad);
                         },
                        // Working directory.
                        WorkingDirectory = options.WorkingDirectory,
                       SetWorkingDirectory = (dir) =>
                        {
                            options.WorkingDirectory = dir;
                            _config.WorkingDirectory = dir;
                            _config.Save();
                            // Reload AGENTS.md from new directory.
                            devMindContext = LoadContextFile(dir);
                            // Update the host's shell runner working directory.
                             host.SetWorkingDirectory(dir);
                             // Rebuild system prompt with new context.
                             // (The captured Func<string> in RunTurnAsync will pick this up automatically.)
                        },
                        // /dir -b: interactive directory-only picker, run modally on the UI thread.
                        // Returns the chosen directory, or null on cancel/Esc (handler no-ops).
                        BrowseForDirectory = (startDir) =>
                        {
                            string start = !string.IsNullOrEmpty(startDir) && Directory.Exists(startDir)
                                ? startDir : Directory.GetCurrentDirectory();
                            using var dlg = new OpenDialog
                            {
                                Title = "Select working directory",
                                OpenMode = OpenMode.Directory,
                                AllowsMultipleSelection = false,
                                Path = start,
                            };
                            app.Run(dlg);
                            return dlg.Canceled || string.IsNullOrEmpty(dlg.Path) ? null : dlg.Path;
                        },
                        // Training log — status snapshot + live/persisted toggles.
                        TrainingLogEnabled = trainingLogger.Enabled,
                        TrainingLogFolder = trainingLogger.ResolvedFolder,
                        TrainingLogLastWriteUtc = trainingLogger.GetLastWriteUtc(),
                        SetTrainingLogEnabled = (on) =>
                        {
                            trainingLogger.Enabled = on;      // live, this session
                            _config.TrainingLogEnabled = on;  // persist across restart
                            _config.Save();
                        },
                        SetTrainingLogFolder = (path) =>
                        {
                            trainingLogger.Folder = path;     // live, this session
                            _config.TrainingLogFolder = path;
                            _config.Save();
                        },
                        // Merge conflict resolution (/resolve).
                        ResolveConflict = (choice) => host.ResolvePendingConflict(choice),
                        // DAP debugging (/debug).
                        DebugCommand = (dargs) => host.HandleDebugCommandAsync(dargs),
                        // UI-only screen clear (/cls).
                        ClearScreen = () => host.ClearOutputView(),
                    };

                   CommandResult result = await SlashCommand.Dispatch(input, cmdCtx);

                    // Render the result.
                    OutputColor resultColor = result.IsError ? OutputColor.Error : OutputColor.Dim;
                    host.AppendOutputLocal($"{result.Message}\n", resultColor);

                    // /t: one-shot thinking — run the agentic turn with thinking ON, then revert.
                    if (cmdCtx.OneShotThinking)
                    {
                        // Extract the message from "/t <message>".
                        string message = input.Substring(2).Trim();
                        if (!string.IsNullOrEmpty(message))
                        {
                            // Echo the actual message.
                            host.AppendOutputLocal($"\n> {message}\n", OutputColor.Input);

                            // Enable thinking for this turn.
                            bool previousThinking = options.ShowLlmThinking;
                            options.ShowLlmThinking = true;

                            inputBox.View.CanFocus = false;
                            inputBox.SetActive(false);
                            statusBar.SetBusy("Processing...");
                            _isTurnRunning = true;
                            callbacks.BeginTurn();

                            try
                            {
                                // Run off the UI thread so synchronous tool I/O (FIND/READ/LIST)
                                // can't block the main loop and freeze the spinner. All UI writes
                                // already marshal via app.Invoke; this await resumes the finally
                                // back on the UI thread.
                                await Task.Run(() => RunTurnAsync(message, options, llmClient, host, driver, state,
                                    callbacks, () => BuildCombinedSystemPrompt(options, devMindContext, _config.BehavioralRules, host.TaskScratchpad), cts, app,
                                    historyStore, SessionId.Get(), SessionId.GetMachineName()));
                            }
                            finally
                            {
                                // Guaranteed teardown on completion, cancellation, AND
                                // exception — EndTurn disposes the turn ticker (otherwise
                                // it re-orphans and "Generating" returns) and shows Ready.
                                options.ShowLlmThinking = previousThinking; // revert one-shot thinking
                                _isTurnRunning = false;
                                inputBox.View.CanFocus = true;
                                inputBox.SetActive(true);
                                inputBox.View.SetFocus();
                                callbacks.EndTurn();
                            }
                            return;
                        }
                    }

                    // /restart is an alias handled by the /new handler internally,
                    // but keep backward compatibility by registering it explicitly.
                    // (The /new handler already does the full reset.)

                    inputBox.View.CanFocus = true;
                    inputBox.SetActive(true);
                    inputBox.View.SetFocus();
                    statusBar.SetReady();
                    return;
                }

                // Disable input during agentic processing.
                inputBox.View.CanFocus = false;
                inputBox.SetActive(false);
                statusBar.SetBusy("Processing...");
                _isTurnRunning = true;
                callbacks.BeginTurn();

                try
                {
                    // Run the agentic turn OFF the UI thread — synchronous tool I/O
                    // (FIND/READ/LIST/GREP) would otherwise block the main loop and freeze the
                    // animated spinner. All UI writes marshal via app.Invoke; this await resumes
                    // the finally back on the UI thread for the teardown.
                    await Task.Run(() => RunTurnAsync(input, options, llmClient, host, driver, state,
                        callbacks, () => BuildCombinedSystemPrompt(options, devMindContext, _config.BehavioralRules, host.TaskScratchpad), cts, app,
                        historyStore, SessionId.Get(), SessionId.GetMachineName()));
                }
                finally
                {
                    // Guaranteed teardown on completion, cancellation, AND exception.
                    // EndTurn disposes the turn ticker (otherwise a cancelled turn
                    // re-orphans it and "Generating" comes right back), syncs the
                    // context meter to server truth, publishes the turn's tok/s, and
                    // shows Ready.
                    _isTurnRunning = false;
                    inputBox.View.CanFocus = true;
                    inputBox.SetActive(true);
                    inputBox.View.SetFocus();
                    callbacks.EndTurn();
                }
            };

            // Start the UI render pump before the loop runs: a recurring main-loop timeout that
            // drains streamed output on the UI thread at a fixed cadence. This is what keeps the
            // spinner animating and text flowing during a turn — a background thread's App.Invoke
            // does not reliably wake the parked Windows input-wait, but an AddTimeout does.
            host.StartRenderPump(app);

            // Fire-and-forget: prune nearline spill folders left by previous runs whose session is
            // not resumable from history. Runs off the startup path so it never blocks the UI.
            _ = Task.Run(async () =>
            {
                try
                {
                    var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        SessionId.Get() // never touch the current session's folder
                    };
                    var sessions = await historyStore.ListSessionsAsync(SessionId.GetMachineName());
                    foreach (var s in sessions)
                        keep.Add(s.SessionId);
                    NearlineCache.CleanupOrphaned(keep);
                }
                catch { /* best-effort cleanup — must never affect startup */ }
            });

            // Run the application.
            app.Run(window);

            return 0;
        }

        // ── Agentic turn loop ─────────────────────────────────────────────────────

    static async Task RunTurnAsync(
             string userInput,
             TuiOptions options,
             LlmClient llmClient,
             TuiAgenticHost host,
             LoopDriver driver,
             LoopState state,
             TuiLoopCallbacks callbacks,
             Func<string> buildSystemPrompt,
             CancellationTokenSource cts,
             IApplication app,
             IHistoryStore historyStore,
             string sessionId,
             string machineName)
        {
            state.ResetForUserTurn();
            host.ResetTaskContext();

           var thinkFilter = new ThinkFilter();
            string currentPrompt = userInput;
            bool firstIteration = true;
            bool forceToolChoiceRequired = false; // Layer 2 narration-retry flag

            // A DevMind-internal status/diagnostic line ([CONTEXT], [LLM], [TOOL_USE], [DIAG…],
            // [DROPPED], [SQUEEZED], …) emitted through onToken. The engine injects these — some
            // BEFORE the model streams a single token — so they must not count as model output or
            // flip the thinking→generating phase (that flipped "Thinking…" to "Generating…" ~50ms
            // into the turn, before any reasoning, making "Thinking…" effectively invisible).
            // Matches a leading all-caps bracket tag; mixed-case model brackets (e.g. "[Fact]")
            // are intentionally NOT matched, so real model content still counts.
            static bool IsInternalStatusLine(string token)
            {
                if (string.IsNullOrEmpty(token)) return false;
                int i = 0;
                while (i < token.Length && char.IsWhiteSpace(token[i])) i++;
                if (i >= token.Length || token[i] != '[') return false;
                i++;
                if (i >= token.Length || !char.IsUpper(token[i])) return false;
                for (; i < token.Length; i++)
                {
                    char c = token[i];
                    if (c == ']') return true;
                    if (!(char.IsUpper(c) || char.IsDigit(c) || c == '_' || c == '-' || c == ' ')) return false;
                }
                return false;
            }

            while (true)
            {
                if (cts.Token.IsCancellationRequested) return;

                llmClient.IncrementTurn();
                host.CancellationToken = cts.Token;

                if (!firstIteration) thinkFilter.Reset();
                firstIteration = false;

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var responseBuffer = new StringBuilder();
                bool timerStopped = false;
                bool suppressDisplay = false;
                string lineAccum = string.Empty;

                // Render prose live, but buffer ```lang fenced blocks and emit them syntax-
                // highlighted (markers hidden). One instance per LLM iteration. Prose and code
                // both marshal to the UI thread inside the host (FIFO), so order is preserved.
                var codeStreamer = new CodeBlockStreamer(
                    prose: text => ((TuiAgenticHost)host).AppendOutputLocal(text, OutputColor.Normal),
                    code:  (code, lang) => ((TuiAgenticHost)host).AppendCode(code, lang));

                callbacks.StartThinkingTimer(state.AgenticDepth, options.AgenticLoopMaxDepth);

                using var cancelReg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));

               await llmClient.SendMessageAsync(
                    currentPrompt,
                    forceToolChoiceRequired: forceToolChoiceRequired,
                    onToken: token =>
                    {
                        // DevMind injects its own status/diagnostic lines ([CONTEXT], [LLM],
                        // [TOOL_USE], [DIAG…], [DROPPED], …) through this same callback — some
                        // BEFORE the model streams a single token. They must not be counted as
                        // output, nor flip the thinking→generating phase. Only real model tokens
                        // drive the counter and the transition.
                        bool isStatus = IsInternalStatusLine(token);

                        // Feed the live token counter (drives the status bar's "N tok ·
                        // X tok/s" suffix) for real model tokens only; counts thinking and
                        // visible alike, matching the engine's LastGeneratedTokens.
                        if (!isStatus) callbacks.OnStreamToken();

                        string visible = thinkFilter.Process(token, options.ShowLlmThinking,
                            out string thinkText);

                       if (!string.IsNullOrEmpty(thinkText))
                        {
                            if (!timerStopped) { callbacks.StopThinkingTimer(); timerStopped = true; }
                            // Thinking text — append to output in the muted Thinking color.
                            // No App.Invoke: AppendOutputLocal buffers and the render pump drains it.
                            ((TuiAgenticHost)host).AppendOutputLocal(thinkText, OutputColor.Thinking);
                        }

                        if (string.IsNullOrEmpty(visible)) return;

                        // Real visible model content marks the start of generation; DevMind's own
                        // status lines flow through here too but must not flip the phase (so the
                        // "Thinking…" state now renders during prompt-processing + hidden reasoning).
                        if (!isStatus && !timerStopped) { callbacks.StopThinkingTimer(); timerStopped = true; }

                        responseBuffer.Append(visible);

                        // FILE: / END_FILE suppression.
                        lineAccum += visible;
                        int nlIdx;
                        while ((nlIdx = lineAccum.IndexOf('\n')) >= 0)
                        {
                            string completedLine = lineAccum.Substring(0, nlIdx).TrimEnd('\r').TrimEnd();
                            lineAccum = lineAccum.Substring(nlIdx + 1);
                            if (completedLine.StartsWith("FILE: ", StringComparison.Ordinal) &&
                                completedLine.Length > 6)
                                suppressDisplay = true;
                            else if (completedLine.Equals("END_FILE", StringComparison.Ordinal))
                                suppressDisplay = false;
                        }

                      if (!suppressDisplay)
                        {
                            // Route through the code-block streamer: prose appends live,
                            // fenced code is buffered and emitted highlighted on close.
                            codeStreamer.Feed(visible);
                        }
                    },
                    onComplete: () => tcs.TrySetResult(true),
                    onError: ex => tcs.TrySetException(ex),
                   deferCompression: state.ShellLoopPending,
                    combinedSystemPrompt: buildSystemPrompt(),
                    cancellationToken: cts.Token);

                try
                {
                    await tcs.Task;
                }
              catch (OperationCanceledException)
                {
                    if (!timerStopped) callbacks.StopThinkingTimer();
                    app.Invoke(() =>
                    {
                        ((TuiAgenticHost)host).AppendOutputLocal("\n[Stopped]\n", OutputColor.Dim);
                    });
                    return;
                }
                catch (Exception ex)
                {
                    if (!timerStopped) callbacks.StopThinkingTimer();
                    app.Invoke(() =>
                    {
                        ((TuiAgenticHost)host).AppendOutputLocal($"\n[ERROR] {ex.Message}\n", OutputColor.Error);
                    });
                    return;
                }
                finally
                 {
                     if (!timerStopped) callbacks.StopThinkingTimer();
                     // Release any buffered code block / held prose (terminated or not).
                     codeStreamer.Flush();
                 }

                // Save this turn to history (user message + assistant response).
                if (historyStore != null && !cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        string userMsg = currentPrompt;
                        string assistantMsg = responseBuffer.ToString();
                        var messages = new[]
                        {
                            new HistoryMessage
                            {
                                SessionId = sessionId,
                                MachineName = machineName,
                                TurnIndex = llmClient.CurrentTurn,
                                Role = "user",
                                Content = userMsg,
                                CreatedAt = DateTime.UtcNow,
                            },
                            new HistoryMessage
                            {
                                SessionId = sessionId,
                                MachineName = machineName,
                                TurnIndex = llmClient.CurrentTurn,
                                Role = "assistant",
                                Content = assistantMsg,
                                CreatedAt = DateTime.UtcNow,
                            },
                        };
                        await historyStore.SaveMessagesAsync(messages);
                        // Upsert the session record.
                        await historyStore.UpsertSessionAsync(sessionId, machineName);
                    }
                    catch
                    {
                        // Non-fatal — history save failures should not break the turn.
                    }
                }

                LoopIterationResult iter;
                try
                {
                    iter = await driver.ProcessIterationAsync(currentPrompt, responseBuffer.ToString(),
                        ResolveBuildCommand(options), cts.Token);
                }
              catch (OperationCanceledException)
                {
                    app.Invoke(() =>
                    {
                        ((TuiAgenticHost)host).AppendOutputLocal("[Stopped]\n", OutputColor.Dim);
                    });
                    return;
                }

                switch (iter.Kind)
                {
                    case LoopIterationKind.Terminal:
                    case LoopIterationKind.Cancelled:
                        return;

                   case LoopIterationKind.ShouldReTrigger:
                        if (cts.Token.IsCancellationRequested) return;
                        currentPrompt = iter.NextContextualMessage ?? callbacks.GetInputText();
                        forceToolChoiceRequired = iter.ForceToolChoiceRequired;
                        callbacks.SetInputText(string.Empty);
                        break;
                }
            }
        }

       // ── Copy selection to the Windows clipboard ───────────────────────────────
        //
        // The output Editor's in-process clipboard needs an STA thread and its context-menu Copy
        // is gated on ReadOnly, so we shell out instead. The mouse-drag SelectedText keeps working
        // under ReadOnly = true (selection commands are exempt from the ReadOnly guard), so we read
        // it and pipe it as UTF-8 into a one-shot STA PowerShell that calls Set-Clipboard. That
        // process reads the RAW stdin byte stream and decodes it as UTF-8 itself, so box-drawing
        // glyphs and code survive (plain clip.exe in the console codepage mangles them), and
        // streaming via stdin handles arbitrary length. ShellRunner is not used here: it closes the
        // child's stdin immediately for EOF, and its env-var clipboard path caps at the ~32 KB
        // per-variable limit.
        static void CopySelectionToClipboard(GuiEditor outputView, TuiAgenticHost host)
        {
            string text = outputView.SelectedText;
            if (string.IsNullOrEmpty(text))
            {
                host.AppendOutputLocal("Nothing selected to copy.\n", OutputColor.Dim);
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -NonInteractive -STA -Command " +
                                "\"$ms = New-Object System.IO.MemoryStream; " +
                                "[Console]::OpenStandardInput().CopyTo($ms); " +
                                "Set-Clipboard -Value ([System.Text.Encoding]::UTF8.GetString($ms.ToArray()))\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardInputEncoding = new UTF8Encoding(false),
                };

                using var proc = Process.Start(psi);
                proc.StandardInput.Write(text);
                proc.StandardInput.Close();
                proc.WaitForExit(5000);

                host.AppendOutputLocal($"Copied {text.Length} chars to clipboard.\n", OutputColor.Dim);
            }
            catch (Exception ex)
            {
                host.AppendOutputLocal($"[ERROR] Clipboard copy failed: {ex.Message}\n", OutputColor.Error);
            }
        }

       // ── Context file loading ──────────────────────────────────────────────────

static string LoadContextFile(string workingDirectory)
{
    if (string.IsNullOrEmpty(workingDirectory)) return null;

    // Search working dir + git root (if different)
    var searchDirs = new List<string> { workingDirectory };
    string gitRoot = ContextEngine.FindGitRoot(workingDirectory);
    if (!string.IsNullOrEmpty(gitRoot) &&
        !string.Equals(gitRoot, workingDirectory, StringComparison.OrdinalIgnoreCase))
        searchDirs.Add(gitRoot);

    foreach (string dir in searchDirs)
    {
        string path = Path.Combine(dir, "AGENTS.md");
        if (!File.Exists(path)) continue;
        try
        {
            string content = File.ReadAllText(path);
            return content;
        }
        catch { }
    }

    return null;
}

        // ── System prompt builder ─────────────────────────────────────────────────

   /// <summary>
    /// Build command for run_build: explicit --build-command override, else
    /// auto-detected from the working directory (cached per directory, so the
    /// per-iteration call in RunTurnAsync is cheap and /dir changes re-detect).
    /// </summary>
    static string ResolveBuildCommand(TuiOptions options)
        => !string.IsNullOrWhiteSpace(options.BuildCommand)
            ? options.BuildCommand
            : BuildCommandResolver.Resolve(options.WorkingDirectory);

   static string BuildCombinedSystemPrompt(TuiOptions options, string devMindContext, string behavioralRules, string scratchpad = "")
    {
        string llmDirective = LoopHelpers.BuildToolUsePrompt(
            buildCommand: ResolveBuildCommand(options),
            projectNamespace: null);

        var sb = new StringBuilder();
        sb.Append(options.SystemPrompt);
        sb.Append("\n\n");
        sb.Append(llmDirective);

        // Behavioral rules — after base prompt, before project context.
        if (!string.IsNullOrEmpty(behavioralRules))
        {
            sb.Append("\n\n--- BEHAVIORAL RULES ---\n");
            sb.Append(behavioralRules);
            sb.Append("\n---");
        }

        if (!string.IsNullOrEmpty(devMindContext))
        {
            sb.Append("\n\n--- PROJECT CONTEXT ---\n");
            sb.Append(devMindContext);
            sb.Append("\n---");
        }

        try
        {
            var memMgr = new MemoryManager(options.WorkingDirectory);
            string memoryIndex = memMgr.LoadIndex();
            if (!string.IsNullOrWhiteSpace(memoryIndex))
            {
                sb.Append("\n\n--- Session Memory (MEMORY.md) ---\n");
                sb.Append(memoryIndex);
                sb.Append("\n---");
            }
        }
        catch { }

        // Scratchpad — model's cross-turn state tracking.
        // Injected into the system prompt so it survives context compaction.
        if (!string.IsNullOrEmpty(scratchpad))
        {
            sb.Append("\n\n--- CURRENT SCRATCHPAD ---\n");
            sb.Append(scratchpad);
            sb.Append("\n---");
        }

        return sb.ToString();
    }
    }
}
