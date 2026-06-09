// File: Program.cs  v1.5 (SPIKE)
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Terminal.Gui v2 TUI for DevMind — Phase 1 SPIKE.
// Proves DevMind.Core's engine can drive a TUI through IAgenticHost + ILoopCallbacks.
//
// Layout:
//   Row 0: Output area (TextView, fills remaining space)
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
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevMind
{
    internal static class Program
    {
       static async Task<int> Main(string[] args)
        {
            // Parse args — same pattern as CLI.
            var options = TuiOptions.FromArgs(args);

            // Context file discovery.
            string devMindContext = LoadContextFile(options.WorkingDirectory);

            // Construct core objects.
            var llmClient = new LlmClient(options);
            llmClient.Configure(options.EndpointUrl, options.ApiKey);

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
            Application.MaximumIterationsPerSecond = 200;

            // Main window.
            using Window window = new() { Title = "DevMind TUI (SPIKE) — Esc to quit" };

           // Output pane — programmatically-written, non-interactive log.
            //
            // ReadOnly MUST be false. In Terminal.Gui v2 (2.4.4) TextView.InsertText(Key)
            // short-circuits with `if (_isReadOnly) return;` BEFORE mutating the model — so
            // ReadOnly blocks not just user typing but every PROGRAMMATIC InsertText too. With
            // ReadOnly=true the entire output-rendering path (banner, user echo, streamed
            // tokens — all routed through InsertText) was a silent no-op and the pane stayed
            // blank. InsertText also calls SetNeedsDraw() internally, so the marshaled
            // UI-thread mutation repaints on the next run-loop iteration without extra work.
            //
            // CanFocus MUST be false. It keeps the pane out of the tab order (so it can't
            // steal focus from the input field on startup) AND makes it non-interactive: with
            // no focus the user can never send edit commands, so a writable TextView behaves
            // as a read-only log in practice without ReadOnly's InsertText block.
            TextView outputView = new()
            {
                ReadOnly = false,
                CanFocus = false,
                X = 0, Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 2, // leave room for input + status rows
            };

            // Black background. A non-ReadOnly TextView paints unstamped cells and fill via
            // the Editable visual role (Normal is redirected to Editable in
            // TextView.OnGettingAttributeForRole), and the default Scheme DERIVES Editable
            // from Normal by dimming the FOREGROUND into the background — that derivation is
            // where the gray pane came from. Pin both roles to light-gray-on-black; per-Cell
            // Attributes stamped by TuiAgenticHost override the foreground per span and take
            // their black background from this scheme (see ResolveAttribute).
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

            // Word wrap — enable only once the view has a real width. Enabling at width 0
            // (before the first layout) is destructive in 2.4.4: TextFormatter.Format(width:0)
            // returns a single empty line per logical line, so the wrap mapping degenerates
            // (every wrapped→model column maps to 0) and subsequent appends would insert at
            // column 0, reversing the text. The WordWrap setter also calls ResetPosition()
            // (cursor to 0,0), so the insertion point is restored to the document end —
            // appends must continue at the end. The InsertionPoint setter clamps to the last
            // line/column and calls AdjustViewport(), preserving auto-scroll.
            //
            // SubViewsLaidOut also fires after every layout re-wrap (window resize), which
            // rebuilds the wrapped model with Terminal.Gui's flawed attribute copy — re-fix
            // the colors from the unwrapped model each time (no-op until word wrap is on).
            bool wordWrapPending = true;
            outputView.SubViewsLaidOut += (s, e) =>
            {
                if (wordWrapPending && outputView.Viewport.Width > 0)
                {
                    wordWrapPending = false;
                    outputView.WordWrap = true;
                    outputView.InsertionPoint =
                        new System.Drawing.Point(int.MaxValue, int.MaxValue);
                    TuiAgenticHost.Diag($"[WRAP-ENABLE] viewport={outputView.Viewport} " +
                        $"wordWrap={outputView.WordWrap} insertionPoint={outputView.InsertionPoint}");
                }
                host.RefreshWrappedColors();
            };

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
            string combinedSystemPrompt = BuildCombinedSystemPrompt(options, devMindContext);

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
                            var oldCts = cts;
                            cts = new CancellationTokenSource();
                            oldCts.Dispose();
                        },
                        SetDepthCap = (n) => { options.AgenticLoopMaxDepth = n; },
                        SetThinking = (on) => { options.ShowLlmThinking = on; },
                    };

                    CommandResult result = SlashCommand.Dispatch(input, cmdCtx);

                    // Render the result.
                    OutputColor resultColor = result.IsError ? OutputColor.Error : OutputColor.Dim;
                    host.AppendOutputLocal($"{result.Message}\n", resultColor);

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
                    callbacks, combinedSystemPrompt, cts, app);

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
            IApplication app)
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
                                // We can't do color in TextView easily; just append.
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

       // ── Context file loading ──────────────────────────────────────────────────

        static string LoadContextFile(string workingDirectory)
        {
            if (string.IsNullOrEmpty(workingDirectory)) return null;

            // Check for DevMind.md or .agent.md in working directory, then walk up.
            string dir = workingDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                foreach (string name in new[] { "DevMind.md", ".agent.md" })
                {
                    string path = Path.Combine(dir, name);
                    if (File.Exists(path))
                    {
                        try { return File.ReadAllText(path); }
                        catch { }
                    }
                }
                string parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            return null;
        }

        // ── System prompt builder ─────────────────────────────────────────────────

        static string BuildCombinedSystemPrompt(TuiOptions options, string devMindContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine(options.SystemPrompt);

            if (!string.IsNullOrEmpty(devMindContext))
            {
                sb.AppendLine("\n--- PROJECT CONTEXT ---");
                sb.AppendLine(devMindContext);
            }

            // Tool catalog (same as CLI).
            sb.AppendLine("\n--- TOOLS ---");
            sb.AppendLine("You have access to the following tools. Use them by emitting the corresponding directives:");
            sb.AppendLine("- READ filename — read a file into context");
            sb.AppendLine("- PATCH filename — edit an existing file (FIND/REPLACE pairs)");
            sb.AppendLine("- FILE: filename — create a new file");
            sb.AppendLine("- SHELL: command — run a shell command");
            sb.AppendLine("- GREP: \"pattern\" filename — search a file for a pattern");
            sb.AppendLine("- FIND: \"pattern\" *.cs — search across files");
            sb.AppendLine("- DELETE filename — delete a file");
            sb.AppendLine("- RENAME old new — rename a file");
            sb.AppendLine("- DIFF filename — show file changes");
            sb.AppendLine("- TEST project — run tests");
            sb.AppendLine("- RECALL_MEMORY topic — recall saved memory");
            sb.AppendLine("- SAVE_MEMORY topic content — save memory");
            sb.AppendLine("- LIST_MEMORY — list memory topics");
            sb.AppendLine("- DONE — signal task completion");
            sb.AppendLine("- SCRATCHPAD — track multi-step task state");

            return sb.ToString();
        }
    }
}
