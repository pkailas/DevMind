// File: Program.cs  v1.5 (SPIKE)
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Terminal.Gui v2 TUI for DevMind — Phase 1 SPIKE.
// Proves DevMind.Core's engine can drive a TUI through IAgenticHost + ILoopCallbacks.
//
// Layout:
//   Row 0: Output area (gui-cs/Editor, fills remaining space)
//   Row 1: Input line (TextField with "> " prompt)
//   Row 2: Status bar (Label)
//
// Launch:
//   dotnet run --project DevMind.TUI -- --dir C:\path\to\project
//   dotnet run --project DevMind.TUI -- --endpoint http://127.0.0.1:1234/v1 --api-key lm-studio
//
// Environment variables (same as CLI):
//   DEVMIND_ENDPOINT  — LLM endpoint URL (overrides default)
//   DEVMIND_API_KEY   — API key (overrides default)

// SPIKE: suppress obsolete warnings for Terminal.Gui v2 legacy APIs.
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
Application.MaximumIterationsPerSecond = 750;

            // Main window.
            using Window window = new() { Title = "DevMind TUI (SPIKE) — Esc to quit" };

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


            // Input TextField.
            TextField inputField = new()
            {
                X = 1, Y = Pos.AnchorEnd(2), // 2 rows from bottom (input + status)
                Width = Dim.Fill() - 1,
                Text = "",
            };

            // Prompt label ("> ").
            Label promptLabel = new()
            {
                X = 0, Y = Pos.AnchorEnd(2),
                Width = 1,
                Text = ">",
            };

            // Status Label.
            Label statusLabel = new()
            {
                X = 0, Y = Pos.AnchorEnd(1),
                Width = Dim.Fill(),
                Text = "Ready",
            };

            // Add views to window.
            window.Add(outputView, promptLabel, inputField, statusLabel);

            // Construct host and callbacks with references to TUI views.
            var host = new TuiAgenticHost(options.WorkingDirectory, outputView, () => cts.Cancel());
            var callbacks = new TuiLoopCallbacks(llmClient, statusLabel, host, inputField);
            var state = new LoopState();
            var driver = new LoopDriver(llmClient, host, callbacks, options, state);

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

           // Build combined system prompt.
            string combinedSystemPrompt = BuildCombinedSystemPrompt(options, devMindContext, _config.BehavioralRules);

            // Banner. All output MUST go through the host (never outputView.InsertText
            // directly) — TuiAgenticHost tracks the logical append position for color
            // stamping, and a bypassing insert would desync it.
            host.AppendOutputLocal(
                "╔══════════════════════════════════════════════════════════╗\n" +
                "║  DevMind TUI — Phase 1 SPIKE                           ║\n" +
                $"║  Endpoint: {options.EndpointUrl,-47}║\n" +
                $"║  Working dir: {options.WorkingDirectory,-43}║\n" +
                "╚══════════════════════════════════════════════════════════╝\n\n",
                OutputColor.Dim);

            // Focus the input field. Setting focus before the loop runs is unreliable in
            // Terminal.Gui v2 (layout/focus is resolved during app.Run), so also re-assert it
            // once the window is initialized. With outputView non-focusable and the Labels
            // non-focusable by default, inputField is the only tab stop — but this guarantees
            // the cursor lands there on startup.
            inputField.SetFocus();
            window.Initialized += (s, e) => inputField.SetFocus();

            // Ctrl+C → copy the output pane's current mouse selection to the Windows clipboard.
            // Bound app-wide via the instance model's Keyboard (this process uses Application.Create,
            // so the static Application.KeyDown would throw a mixed-model error). It fires regardless
            // of focus (the output Editor is non-focusable, so the input TextField holds focus);
            // marking the key Handled keeps it from reaching the input field. Esc remains the quit
            // key, so Ctrl+C is free here, and the driver delivers it as a normal keystroke
            // (TreatControlCAsInput), not a SIGINT.
            app.Keyboard.KeyDown += (s, key) =>
            {
                if (key.KeyCode != Key.C.WithCtrl.KeyCode) return;
                key.Handled = true;
                CopySelectionToClipboard(outputView, host);
            };

           // Handle Enter in input field — submit to LLM.
            inputField.Accepting += async (s, e) =>
            {
                // Mark the Accept command handled so it does not bubble to the SuperView.
                // In Terminal.Gui v2, an unhandled Accepting raises Command.Accept on the
                // SuperView (per View.RaiseAccepting), which can trigger a default-button /
                // toplevel Accept we don't want. We own Enter on this field.
                e.Handled = true;

                string input = inputField.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(input)) return;

                // Clear input.
                inputField.Text = "";

                // Echo user input (through the host — keeps the stamping tracker in sync).
                host.AppendOutputLocal($"\n> {input}\n", OutputColor.Input);

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
                        ThinkingEnabled = options.ShowLlmThinking,
                        SystemPrompt = llmClient.SystemPromptContent ?? combinedSystemPrompt,
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
                        SetDepthCap = (n) => { options.AgenticLoopMaxDepth = n; },
                        SetThinking = (on) => { options.ShowLlmThinking = on; },
                        // History fields.
                        HistoryStore = historyStore,
                        SessionId = SessionId.Get(),
                        MachineName = SessionId.GetMachineName(),
                       PrependMessages = (roles, contents) => llmClient.PrependMessages(roles, contents),
                        // Behavioral rules.
                        BehavioralRules = _config.BehavioralRules,
                        SetBehavioralRules = (rules) =>
                        {
                            _config.BehavioralRules = rules;
                            _config.Save();
                        },
                       RebuildSystemPrompt = () =>
                        {
                            combinedSystemPrompt = BuildCombinedSystemPrompt(options, devMindContext, _config.BehavioralRules);
                            return combinedSystemPrompt;
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
                            combinedSystemPrompt = BuildCombinedSystemPrompt(options, devMindContext, _config.BehavioralRules);
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

                            inputField.CanFocus = false;
                            statusLabel.Text = "Processing...";

                            await RunTurnAsync(message, options, llmClient, host, driver, state,
                                callbacks, combinedSystemPrompt, cts, app,
                                historyStore, SessionId.Get(), SessionId.GetMachineName());

                            // Revert thinking.
                            options.ShowLlmThinking = previousThinking;

                            inputField.CanFocus = true;
                            inputField.SetFocus();
                            statusLabel.Text = "Ready";
                            return;
                        }
                    }

                    // /restart is an alias handled by the /new handler internally,
                    // but keep backward compatibility by registering it explicitly.
                    // (The /new handler already does the full reset.)

                    inputField.CanFocus = true;
                    inputField.SetFocus();
                    statusLabel.Text = "Ready";
                    return;
                }

                // Disable input during agentic processing.
                inputField.CanFocus = false;
                statusLabel.Text = "Processing...";

               // Run the agentic turn.
                await RunTurnAsync(input, options, llmClient, host, driver, state,
                    callbacks, combinedSystemPrompt, cts, app,
                    historyStore, SessionId.Get(), SessionId.GetMachineName());

                // Re-enable input.
                inputField.CanFocus = true;
                inputField.SetFocus();
                statusLabel.Text = "Ready";
            };

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
            string combinedSystemPrompt,
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

                callbacks.StartThinkingTimer(state.AgenticDepth, options.AgenticLoopMaxDepth);

                using var cancelReg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));

                await llmClient.SendMessageAsync(
                    currentPrompt,
                    onToken: token =>
                    {
                        string visible = thinkFilter.Process(token, options.ShowLlmThinking,
                            out string thinkText);

                       if (!string.IsNullOrEmpty(thinkText))
                        {
                            if (!timerStopped) { callbacks.StopThinkingTimer(); timerStopped = true; }
                            // Thinking text — append to output.
                            app.Invoke(() =>
                            {
                                // Thinking tokens render in the muted Thinking color.
                                ((TuiAgenticHost)host).AppendOutputLocal(thinkText, OutputColor.Thinking);
                            });
                        }

                        if (string.IsNullOrEmpty(visible)) return;

                        if (!timerStopped) { callbacks.StopThinkingTimer(); timerStopped = true; }

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
                            app.Invoke(() =>
                            {
                                ((TuiAgenticHost)host).AppendOutputLocal(visible, OutputColor.Normal);
                            });
                        }
                    },
                    onComplete: () => tcs.TrySetResult(true),
                    onError: ex => tcs.TrySetException(ex),
                    deferCompression: state.ShellLoopPending,
                    combinedSystemPrompt: combinedSystemPrompt,
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
                    iter = await driver.ProcessIterationAsync(responseBuffer.ToString(), null,
                        cts.Token);
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

static string BuildCombinedSystemPrompt(TuiOptions options, string devMindContext)
        => BuildCombinedSystemPrompt(options, devMindContext, "");

    static string BuildCombinedSystemPrompt(TuiOptions options, string devMindContext, string behavioralRules)
    {
        string llmDirective = LoopHelpers.BuildToolUsePrompt(
            buildCommand: null,
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

        return sb.ToString();
    }
    }
}
