# DevMind.TUI — Full Source Dump

> Generated from `DevMind.TUI/` excluding `bin/`, `obj/`.

## File Index

| # | File | Lines |
|---|------|-------|
| 1 | `DevMind.TUI.csproj` | 20 |
| 2 | `Program.cs` | 758 |
| 3 | `TuiAgenticHost.cs` | 1,238 |
| 4 | `TuiLoopCallbacks.cs` | 127 |
| 5 | `TuiOptions.cs` | 85 |
| 6 | `SlashCommand.cs` | 664 |

---
## 1. `DevMind.TUI.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>12</LangVersion>
    <RootNamespace>DevMind</RootNamespace>
    <AssemblyName>DevMind.TUI</AssemblyName>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Terminal.Gui.Editor" Version="2.5.0" />
    <ProjectReference Include="..\DevMind.Core\DevMind.Core.csproj" />
    <PackageReference Include="Terminal.Gui" Version="2.4.4" />
  </ItemGroup>

</Project>
```

---
## 2. `Program.cs`

```csharp
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
            bool forceToolChoiceRequired = false; // Layer 2 narration-retry flag

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
                    forceToolChoiceRequired: forceToolChoiceRequired,
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
```

---

---


---

## 3. `TuiAgenticHost.cs`

```csharp
// File: TuiAgenticHost.cs  v2.0 (SPIKE — Editor migration)
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Terminal.Gui v2 implementation of IAgenticHost.
// Cribbed from ConsoleAgenticHost — file/shell/patch/memory logic is identical;
// only AppendOutput routes to a Terminal.Gui.Editor.Editor instead of Console.Write.
//
// SPIKE (output-view migration): the output transcript was a [Obsolete] TextView
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

// SPIKE: suppress obsolete warnings for Terminal.Gui v2 legacy APIs.
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Rendering;
using GuiEditor = Terminal.Gui.Editor.Editor;

namespace DevMind
{
    /// <summary>
    /// Terminal.Gui v2 implementation of IAgenticHost for the DevMind TUI spike.
    /// All file/shell/patch/memory logic cribbed verbatim from ConsoleAgenticHost.
    /// AppendOutput routes to a Terminal.Gui.Editor.Editor via IApplication.Invoke.
    /// </summary>
    public sealed class TuiAgenticHost : IAgenticHost
    {
        // ── Fields ───────────────────────────────────────────────────────────────

        private readonly ShellRunner _shellRunner;
        private readonly FileContentCache _fileCache = new FileContentCache();

        private readonly HashSet<string> _filesRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _taskReadFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _fileSnapshots =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

       private MemoryManager _memoryManager;

        private const int PatchBackupStackLimit = 10;
        private readonly Stack<(string filePath, string backupPath)> _patchBackupStack =
            new Stack<(string, string)>();

       private readonly Action _cancelTurn;

        // The Editor that receives all output. Rope-backed document, append-only.
        private readonly GuiEditor _outputView;

        // Color is decoupled from the document: each AppendCore call records the
        // [start, start+len) document offset range it inserted plus the resolved
        // Attribute. OffsetColorTransformer (registered on _outputView.LineTransformers)
        // reads this list during visual-line construction and stamps element.Attribute
        // by document offset. Append-only and strictly increasing in Start, so lookups binary-
        // search. Mutated by AppendCore and read by the transformer — both run on the
        // Terminal.Gui UI thread (append marshals via App.Invoke; Transform runs in Draw),
        // so no locking is required.
        private readonly List<ColorSpan> _colorSpans = new List<ColorSpan>();

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
            _outputView   = outputView;
            _cancelTurn   = cancelTurn ?? (() => { });
            if (!string.IsNullOrEmpty(workingDirectory))
                _memoryManager = new MemoryManager(workingDirectory);

            // Register the color transformer. It reads _colorSpans (populated by AppendCore)
            // and stamps element.Attribute by document offset during visual-line construction.
            _outputView.LineTransformers.Add(new OffsetColorTransformer(_colorSpans));
        }

        // ── Context lifecycle helpers ────────────────────────────────────────────

        public void ResetTaskContext() => _taskReadFiles.Clear();

        public void ResetSession()
        {
            _filesRead.Clear();
            _fileSnapshots.Clear();
            _fileCache.InvalidateAll();
           _taskReadFiles.Clear();
        }

        // ── IAgenticHost.AppendOutput ─────────────────────────────────────────────

       void IAgenticHost.AppendOutput(string text, OutputColor color)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Terminal.Gui v2: all view mutations must be on the main UI thread.
            // Use the instance-based IApplication.Invoke (not the deprecated static).
            // Invoke marshals to the UI thread via TimedEvents (zero-delay timeouts keyed by
            // a monotonically-increasing unique tick), so callbacks drain strictly FIFO —
            // streamed tokens stay in arrival order.
            //
            // App is null before app.Run() attaches the window (startup banner). At that
            // point we are still on the main thread, so append directly.
            IApplication app = _outputView.App;
            if (app == null)
                AppendCore(text, color);
            else
                app.Invoke(() => AppendCore(text, color));
        }

        // Appends text to the end of the Editor's rope-backed document and records the color
        // span for OffsetColorTransformer. MUST run on the UI thread. ALL output flows through
        // here (banner, echo, stream tokens, status lines).
        //
        // Versus the old TextView path this is dramatically simpler and cheaper:
        //   * Document.Insert at TextLength is an O(log n) rope splice — no whole-document
        //     re-wrap per token (the TextView cost that hurt long, fast-streaming transcripts).
        //   * Color is NOT stamped into the model. We record (start, len, attr); the registered
        //     IVisualLineTransformer applies it per visible element at draw time, in document-
        //     offset space — so colors survive wrap and resize with no reflection and no
        //     hand-patched wrap-attribute-copy bug.
        //   * CaretOffset = TextLength scrolls to the newest line (auto-scroll). The caret is
        //     navigation, not an edit, so it works under ReadOnly and CanFocus=false.
        private void AppendCore(string text, OutputColor color)
        {
            // Normalize line endings — the document is '\n'-based; a stray '\r' would render
            // as a visible glyph and skew offsets.
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (text.Length == 0) return;

            TextDocument doc = _outputView.Document;
            if (doc == null)
            {
                // Document is assigned in Program.cs before the window runs; guard anyway so a
                // stray pre-init append can never NRE the UI loop.
                Diag($"[APPEND] SKIP (no document) color={color} len={text.Length}");
                return;
            }

            int start = doc.TextLength;
            Terminal.Gui.Drawing.Attribute attr = ResolveAttribute(color);

            try
            {
                doc.Insert(start, text);
                _colorSpans.Add(new ColorSpan(start, text.Length, attr));
                _outputView.CaretOffset = doc.TextLength; // auto-scroll to newest line
                Diag($"[APPEND] color={color} len={text.Length} start={start} total={doc.TextLength} spans={_colorSpans.Count}");
            }
            catch (Exception ex)
            {
                // Keep the app alive — an append failure must never take down the UI loop.
                Diag($"[APPEND] EXCEPTION color={color} len={text.Length} ex={ex}");
            }
        }

        // ── IAgenticHost.RunShellAsync ────────────────────────────────────────────

        async Task<(int exitCode, string output)> IAgenticHost.RunShellAsync(string command)
        {
            AppendOutputLocal($"[SHELL] > {command}\n", OutputColor.Dim);
            var progress = new Progress<ShellOutputLine>(line =>
                AppendOutputLocal(line.Line + "\n", line.IsError ? OutputColor.Error : OutputColor.Normal));
            var (output, exitCode) = await _shellRunner.ExecuteAsync(command, CancellationToken, onLine: progress);
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

            string fileContent = fromToolCall ? content : PatchEngine.StripOuterCodeFence(content);

            try
            {
                string fullPath = ResolveWritePath(fileName);
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(fullPath, fileContent);
                int lineCount = fileContent.Split('\n').Length;
                AppendOutputLocal($"[FILE] Saved {fileNameOnly} ({lineCount} lines)\n", OutputColor.Success);
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

            try
            {
                string resolvedPath = FindFile(fileNameOnly, fileName.Replace('\\', '/'))
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileName);

                if (File.Exists(resolvedPath))
                {
                    string existing = File.ReadAllText(resolvedPath);
                    string separator = existing.Length > 0 && !existing.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "";
                    File.WriteAllText(resolvedPath, existing + separator + content);
                    AppendOutputLocal($"[APPEND] Appended to {fileNameOnly}\n", OutputColor.Success);
                }
                else
                {
                    string dir = Path.GetDirectoryName(resolvedPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(resolvedPath, content);
                    AppendOutputLocal($"[APPEND] Created {fileNameOnly}\n", OutputColor.Success);
                }

                _fileCache.Invalidate(fileNameOnly);
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
            }
        }

        // ── IAgenticHost.UpdateScratchpad ─────────────────────────────────────────

        void IAgenticHost.UpdateScratchpad(string content) { }

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
                _fileCache.Invalidate(oldNameOnly);
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

            if (!_fileCache.Contains(fileNameOnly))
            {
                string diskContent;
                try { diskContent = File.ReadAllText(resolvedPath); }
                catch (Exception ex) { return Task.FromResult($"GREP: error reading {filename} — {ex.Message}"); }
                _fileCache.Store(fileNameOnly, diskContent);
            }

            int totalFileLines = _fileCache.GetLineCount(fileNameOnly);
            int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
            int scanEnd   = endLine.HasValue   ? Math.Min(totalFileLines, endLine.Value) : totalFileLines;

            var matches = new List<(int lineNum, string lineText)>();
            for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
            {
               string lineContent = _fileCache.GetLineRange(fileNameOnly, lineNum, lineNum);
                if (lineContent == null) continue;
                if (lineContent.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    matches.Add((lineNum, lineContent));
            }

            if (matches.Count == 0)
            {
                AppendOutputLocal($"[GREP] no matches for \"{pattern}\" in {filename}\n", OutputColor.Dim);
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
            AppendOutputLocal($"[GREP] {totalMatches} match{(totalMatches == 1 ? "" : "es")} for \"{pattern}\" in {filename}\n", OutputColor.Success);
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

            var allMatches = new List<(string fileLabel, int lineNum, string lineText)>();
            bool hitCap = false;

            foreach (string filePath in files)
            {
                if (hitCap) break;
                string fileNameOnly = SafeGetFileName(filePath);

                if (!_fileCache.Contains(fileNameOnly))
                {
                    string diskContent;
                    try { diskContent = File.ReadAllText(filePath); }
                    catch { continue; }
                    _fileCache.Store(fileNameOnly, diskContent);
                }

                int totalFileLines = _fileCache.GetLineCount(fileNameOnly);
                int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
                int scanEnd   = endLine.HasValue   ? Math.Min(totalFileLines, endLine.Value) : totalFileLines;

                for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
                {
                   string lineContent = _fileCache.GetLineRange(fileNameOnly, lineNum, lineNum);
                    if (lineContent == null) continue;
                    if (lineContent.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
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

        async Task<string> IAgenticHost.RunTestsAsync(string project, string filter)
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
                var (output, exitCode) = await _shellRunner.ExecuteAsync(cmd, CancellationToken);
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

                if (!_fileCache.Contains(fileNameOnly))
                {
                    AppendOutputLocal($"[AUTO-READ] Loading {fileNameOnly} before patch...\n", OutputColor.Dim);
                    var (cached, _enc) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                    _fileCache.Store(fileNameOnly, cached);
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

                _fileCache.Store(SafeGetFileName(resolved.FullPath), result.UpdatedContent);

                int undosAvailable = _patchBackupStack.Count;
                AppendOutputLocal($"[PATCH] Applied to {resolved.FullPath} (undo depth: {undosAvailable})\n",
                    OutputColor.Success);
                return Task.FromResult(resolved.FullPath);
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return Task.FromResult<string>(null);
            }
        }

        // ── IAgenticHost.ShowDiffPreviewAsync ─────────────────────────────────────
        // SPIKE: auto-approve all patches. No interactive y/n/a/q prompt in TUI.

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

                // SPIKE: auto-approve — no interactive prompt in TUI.
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
        // AppendCore's writes, so reading the shared list needs no lock.
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
            // SPIKE: auto-approve all writes — no interactive prompt.
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
            if (_fileSnapshots.ContainsKey(fullPath)) return;
            try { _fileSnapshots[fullPath] = File.ReadAllText(fullPath); }
            catch { }
        }

        private static string SafeGetFileName(string path)
        {
            try { return Path.GetFileName(path.Replace('\\', '/')); }
            catch { return path; }
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
                    if (!_fileCache.Contains(fileNameOnly))
                    {
                        var (diskContent, _) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                        _fileCache.Store(fileNameOnly, diskContent);
                    }

                    _taskReadFiles.Add(fileNameOnly);
                    int totalLines = _fileCache.GetLineCount(fileNameOnly);

                    if (rangeStart > rangeEnd) { int t = rangeStart; rangeStart = rangeEnd; rangeEnd = t; }
                    int clampedEnd   = Math.Min(rangeEnd,   totalLines);
                    int clampedStart = Math.Max(1, rangeStart);

                    string rangeContent = _fileCache.GetLineRange(fileNameOnly, clampedStart, clampedEnd);
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
                    return rangeBlock;
                }

                var (content, _enc) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                _fileCache.Store(fileNameOnly, content);
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
```

---
## 4. `TuiLoopCallbacks.cs`

```csharp
// File: TuiLoopCallbacks.cs  v1.1 (SPIKE)
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Terminal.Gui v2 implementation of ILoopCallbacks.
// Routes status/context/input to Terminal.Gui views.

// SPIKE: suppress obsolete warnings for Terminal.Gui v2 legacy APIs.
#pragma warning disable CS0618

using System;
using System.Threading;
using Terminal.Gui.Views;

namespace DevMind
{
    /// <summary>
    /// Terminal.Gui v2 implementation of ILoopCallbacks for the DevMind TUI spike.
    /// </summary>
    public sealed class TuiLoopCallbacks : ILoopCallbacks
    {
        private readonly ILlmClient _llmClient;
        private readonly Label _statusLabel;
        private readonly TuiAgenticHost _host;
        private readonly TextField _inputField;

        private string _pendingInput = string.Empty;

        private Timer _thinkingTimer;
        private int   _thinkingElapsed;
        private int   _thinkingDepth;
        private int   _thinkingMaxDepth;
        private readonly object _timerLock = new object();

        public TuiLoopCallbacks(ILlmClient llmClient, Label statusLabel, TuiAgenticHost host, TextField inputField)
        {
            _llmClient   = llmClient;
            _statusLabel = statusLabel;
            _host        = host;
            _inputField  = inputField;
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
            _statusLabel.App.Invoke(() =>
            {
                _statusLabel.Text = text ?? string.Empty;
            });
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
            _inputField.App.Invoke(() =>
            {
                _inputField.SetFocus();
            });
        }

        public void SetInputEnabled(bool enabled)
        {
            _inputField.App.Invoke(() =>
            {
                _inputField.CanFocus = enabled;
                if (enabled)
                    _inputField.SetFocus();
            });
        }

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
            SetStatus("Ready");
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
```

---
## 5. `TuiOptions.cs`

```csharp
// File: TuiOptions.cs  v1.0 (SPIKE)
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Minimal ILlmOptions implementation for the TUI spike.
// Mirrors CliOptions but without the CLI-specific arg parsing.

using System;
using System.IO;

namespace DevMind
{
    /// <summary>
    /// TUI implementation of ILlmOptions. Property values resolved from
    //  environment variables → command-line args → hardcoded defaults.
    /// </summary>
    public sealed class TuiOptions : ILlmOptions
    {
        // CLI-only properties (not part of ILlmOptions).
        public string EndpointUrl { get; set; } = "http://127.0.0.1:1234/v1";
        public string ApiKey { get; set; } = "lm-studio";
        public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();

        // ILlmOptions.
        public string SystemPrompt             { get; set; } = "You are a helpful coding assistant. Be concise and precise.";
        public string ModelName                { get; set; } = "";
        public int    RequestTimeoutMinutes    { get; set; } = 10;
        public int    FirstTokenTimeoutMinutes { get; set; } = 5;
        public bool   ShowDebugOutput          { get; set; } = false;
        public bool   ShowContextBudget        { get; set; } = true;
        public bool   ShowLlmThinking          { get; set; } = false;
        public ContextEvictionMode ContextEviction { get; set; } = ContextEvictionMode.Balanced;
        public int    ManualContextSize        { get; set; } = 0;
        public LlmServerType ServerType        { get; set; } = LlmServerType.LlamaServer;
        public string CustomContextEndpoint    { get; set; } = "";
        public int    MicroCompactThreshold    { get; set; } = 85;
        public bool   MicroCompactSummarize    { get; set; } = true;
        public bool   MicroCompactBrainwash    { get; set; } = false;
        public bool   AlwaysConfirmPatch       { get; set; } = false;
        public int    AgenticLoopMaxDepth      { get; set; } = 5;

        /// <summary>Builds a TuiOptions from command-line args and environment variables.</summary>
        public static TuiOptions FromArgs(string[] args)
        {
            var opts = new TuiOptions();

            // Environment variable defaults.
            string envEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            string envApiKey = Environment.GetEnvironmentVariable("DEVMIND_API_KEY");
            if (!string.IsNullOrEmpty(envEndpoint)) opts.EndpointUrl = envEndpoint;
            if (!string.IsNullOrEmpty(envApiKey)) opts.ApiKey = envApiKey;

            // Pass 1: resolve --dir first.
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--dir")
                {
                    string dir = args[i + 1];
                    if (Directory.Exists(dir))
                        opts.WorkingDirectory = Path.GetFullPath(dir);
                    break;
                }
            }

            // Pass 2: CLI args override everything.
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--endpoint"     when i + 1 < args.Length: opts.EndpointUrl             = args[++i]; break;
                    case "--api-key"      when i + 1 < args.Length: opts.ApiKey                  = args[++i]; break;
                    case "--model"        when i + 1 < args.Length: opts.ModelName               = args[++i]; break;
                    case "--system-prompt" when i + 1 < args.Length: opts.SystemPrompt           = args[++i]; break;
                    case "--dir"          when i + 1 < args.Length: i++; break;
                    case "--max-depth"    when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int md)) opts.AgenticLoopMaxDepth = md; break;
                    case "--context-size" when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int cs)) opts.ManualContextSize   = cs; break;
                    case "--timeout"      when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int to)) opts.RequestTimeoutMinutes = to; break;
                    case "--thinking"      : opts.ShowLlmThinking    = true;  break;
                    case "--no-thinking"   : opts.ShowLlmThinking    = false; break;
                }
            }

            return opts;
        }
    }
}
```

---
## 6. `SlashCommand.cs`

```csharp
// File: SlashCommand.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Slash-command registry and dispatcher for DevMind.TUI.
// Ported from DevMindShell's commands/registry.ts + commands/builtins.ts.
//
// Architecture:
//   * Commands are intercepted at the input boundary (before the agentic
//     turn runs) so a slash command never burns model tokens.
//   * The registry is a Dictionary<name, RegisteredCommand>. Adding a
//     command is one RegisterCommand call — no dispatcher edits.
//   * Handlers receive a CommandContext with mutator callbacks for the
//     pieces of runtime state they need. Direct state access from
//     handlers would couple them to Program's internals.
//   * CommandResult carries a message string + an isError flag. The
//     dispatcher does no rendering — that's Program's job.
//   * Errors in handlers are caught and converted to error CommandResults
//     so a buggy handler doesn't crash the TUI.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// Result of executing a slash command.
    /// </summary>
    public sealed class CommandResult
    {
        public string Message { get; set; } = string.Empty;
        public bool IsError { get; set; }
    }

   /// <summary>
    /// Context passed to command handlers. Provides callbacks into the
    /// TUI's runtime state without exposing internal types directly.
    /// </summary>
    public sealed class CommandContext
    {
        /// <summary>Current agentic loop max depth setting.</summary>
        public int DepthCap { get; set; }

        /// <summary>Whether thinking (reasoning) display is currently enabled.</summary>
        public bool ThinkingEnabled { get; set; }

        /// <summary>The current system prompt text.</summary>
        public string SystemPrompt { get; set; } = string.Empty;

       /// <summary>Called to reset the conversation (clear history, reset state).</summary>
        public Action ResetConversation { get; set; }

        /// <summary>Called to set the depth cap.</summary>
        public Action<int> SetDepthCap { get; set; }

        /// <summary>Called to toggle thinking mode.</summary>
        public Action<bool> SetThinking { get; set; }

        // ── History (session persistence) ──────────────────────────────────────

        /// <summary>History store for session management commands. Null when history is disabled.</summary>
        public IHistoryStore HistoryStore { get; set; }

        /// <summary>Current session ID (for /title, /resume).</summary>
        public string SessionId { get; set; } = "";

        /// <summary>Machine name for history queries.</summary>
        public string MachineName { get; set; } = "";

       /// <summary>Prepend messages into the conversation history (for /resume).</summary>
        public Action<string[], string[]> PrependMessages { get; set; }

       /// <summary>Set to true by /t to enable one-shot thinking for the next turn only.</summary>
        public bool OneShotThinking { get; set; }

        // ── Behavioral rules ─────────────────────────────────────────────────────

        /// <summary>Current behavioral rules text (from TuiConfig).</summary>
        public string BehavioralRules { get; set; } = "";

        /// <summary>Called to set behavioral rules and persist them.</summary>
        public Action<string> SetBehavioralRules { get; set; }

       /// <summary>Rebuild the system prompt after rules change.</summary>
        public Func<string> RebuildSystemPrompt { get; set; }

        // ── Working directory ────────────────────────────────────────────────────

        /// <summary>Current working directory.</summary>
        public string WorkingDirectory { get; set; } = "";

        /// <summary>Called to change the working directory and reload context.</summary>
        public Action<string> SetWorkingDirectory { get; set; }

        /// <summary>Opens an interactive directory-only picker starting at the given path and
        /// returns the chosen directory, or null on cancel.</summary>
        public Func<string, string> BrowseForDirectory { get; set; }
    }

    /// <summary>
    /// Signature for a command handler.
    /// </summary>
    /// <param name="args">Command arguments (not including the command name).</param>
    /// <param name="ctx">Mutable context with callbacks into TUI state.</param>
    /// <returns>Result message and error flag.</returns>
    public delegate Task<CommandResult> CommandHandler(string[] args, CommandContext ctx);

    /// <summary>
    /// A registered slash command with metadata.
    /// </summary>
    public sealed class RegisteredCommand
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Usage { get; set; } = string.Empty;
        public CommandHandler Handler { get; set; } = (args, ctx) => Task.FromResult(new CommandResult { Message = "not implemented" });
    }

    /// <summary>
    /// Slash command registry and dispatcher.
    /// </summary>
    public static class SlashCommand
    {
        static readonly Dictionary<string, RegisteredCommand> _commands =
            new Dictionary<string, RegisteredCommand>(StringComparer.OrdinalIgnoreCase);

        static SlashCommand()
        {
            RegisterBuiltinCommands();
        }

        /// <summary>Register a slash command. Re-registering the same name overwrites.</summary>
        public static void RegisterCommand(
            string name,
            string description,
            string usage,
            CommandHandler handler)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name required", nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _commands[name] = new RegisteredCommand
            {
                Name = name,
                Description = description ?? string.Empty,
                Usage = usage ?? $"/{name}",
                Handler = handler,
            };
        }

        /// <summary>
        /// Returns all registered commands in registration order.
        /// </summary>
        public static RegisteredCommand[] ListCommands()
        {
            return _commands.Values.ToArray();
        }

        /// <summary>
        /// True when input begins with '/'. Used by the input handler to decide
        /// whether to dispatch to the command system.
        /// </summary>
        public static bool IsSlashCommand(string input)
        {
            return !string.IsNullOrEmpty(input) && input.StartsWith(" ");
        }

        /// <summary>
        /// Parse a raw input line into command name + args. Splits on whitespace.
        /// </summary>
        static (string name, string[] args)? ParseInput(string input)
        {
            if (string.IsNullOrEmpty(input) || input[0] != '/')
                return null;

            string trimmed = input.Substring(1).Trim();
            if (string.IsNullOrEmpty(trimmed))
                return null;

            int spaceIdx = trimmed.IndexOf(' ');
            string name;
            string[] args;
            if (spaceIdx < 0)
            {
                name = trimmed;
                args = Array.Empty<string>();
            }
            else
            {
                name = trimmed.Substring(0, spaceIdx);
                args = trimmed.Substring(spaceIdx + 1).Trim()
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            }

            if (!_commands.ContainsKey(name))
                return null;

            return (name, args);
        }

        /// <summary>
        /// Dispatch a slash command. Caller has already determined the input
        /// starts with '/'.
        /// </summary>
        public static async Task<CommandResult> Dispatch(string input, CommandContext context)
        {
            var parsed = ParseInput(input);
            if (parsed == null)
                return new CommandResult { Message = $"Unknown command: {input}", IsError = true };

            var (name, args) = parsed.Value;
            var cmd = _commands[name];

            try
            {
                return await cmd.Handler(args, context);
            }
            catch (Exception ex)
            {
                return new CommandResult
                {
                    Message = $"Command /{name} failed: {ex.Message}",
                    IsError = true,
                };
            }
        }

        // ── Built-in command registration ─────────────────────────────────────────

        static void RegisterBuiltinCommands()
        {
            RegisterCommand("new", "Start a new session (clear conversation)", "/new", NewHandler);
            RegisterCommand("clear", "Clear screen and reset conversation", "/clear", ClearHandler);
            RegisterCommand("think", "Toggle thinking (reasoning) display", "/think on|off", ThinkHandler);
            RegisterCommand("t", "One-shot: send with thinking ON for this turn only", "/t <message>", OneShotThinkHandler);
            RegisterCommand("rules", "Show, set, or clear behavioral rules", "/rules [text|clear]", RulesHandler);
            RegisterCommand("dir", "Show or change working directory", "/dir [path|-b]", DirHandler);
            RegisterCommand("depth-cap", "Show or set agentic depth cap (1-10)", "/depth-cap [N]", DepthCapHandler);
            RegisterCommand("system_prompt", "Display the assembled system prompt", "/system_prompt", SystemPromptHandler);
            RegisterCommand("help", "Show command list", "/help", HelpHandler);
            RegisterCommand("history", "List past sessions from history", "/history", HistoryHandler);
            RegisterCommand("resume", "Resume a past session", "/resume <N>", ResumeHandler);
            RegisterCommand("title", "Set the current session's title", "/title <text>", TitleHandler);

            // Alias: /restart → /new
            RegisterCommand("restart", "Restart (alias for /new)", "/restart", NewHandler);
        }

        // ── Built-in handlers ─────────────────────────────────────────────────────

        static Task<CommandResult> NewHandler(string[] args, CommandContext ctx)
        {
            ctx.ResetConversation();
            return Task.FromResult(new CommandResult { Message = "New session started." });
        }

        static Task<CommandResult> ClearHandler(string[] args, CommandContext ctx)
        {
            ctx.ResetConversation();
            return Task.FromResult(new CommandResult { Message = "Screen cleared, conversation reset." });
        }

        static Task<CommandResult> ThinkHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
            {
                string state = ctx.ThinkingEnabled ? "ON" : "OFF";
                return Task.FromResult(new CommandResult { Message = $"Thinking display: {state}" });
            }

            switch (args[0].ToLowerInvariant())
            {
                case "on":
                    ctx.SetThinking(true);
                    return Task.FromResult(new CommandResult { Message = "Thinking display: ON" });
                case "off":
                    ctx.SetThinking(false);
                    return Task.FromResult(new CommandResult { Message = "Thinking display: OFF" });
                default:
                    return Task.FromResult(new CommandResult { Message = "Usage: /think on|off", IsError = true });
            }
        }

        static Task<CommandResult> OneShotThinkHandler(string[] args, CommandContext ctx)
        {
            ctx.OneShotThinking = true;
            return Task.FromResult(new CommandResult { Message = "" });
        }

        static Task<CommandResult> RulesHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
            {
                string rules = string.IsNullOrEmpty(ctx.BehavioralRules)
                    ? "(no behavioral rules set)"
                    : ctx.BehavioralRules;
                return Task.FromResult(new CommandResult { Message = $"Behavioral rules:\n{rules}" });
            }

            if (args[0].ToLowerInvariant() == "clear")
            {
                ctx.SetBehavioralRules("");
                ctx.RebuildSystemPrompt();
                return Task.FromResult(new CommandResult { Message = "Behavioral rules cleared." });
            }

            string rules = string.Join(" ", args);
            ctx.SetBehavioralRules(rules);
            ctx.RebuildSystemPrompt();
            return Task.FromResult(new CommandResult { Message = $"Behavioral rules set ({rules.Length} chars)." });
        }

        static Task<CommandResult> DirHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
            {
                return Task.FromResult(new CommandResult { Message = $"Working directory: {ctx.WorkingDirectory}" });
            }

            if (args[0] == "-b")
            {
                // Interactive directory picker.
                string chosen = ctx.BrowseForDirectory(ctx.WorkingDirectory);
                if (chosen != null)
                {
                    ctx.SetWorkingDirectory(chosen);
                    return Task.FromResult(new CommandResult { Message = $"Working directory: {chosen}" });
                }
                return Task.FromResult(new CommandResult { Message = "Directory selection cancelled." });
            }

            string dir = args[0];
            if (!Directory.Exists(dir))
                return Task.FromResult(new CommandResult { Message = $"Directory not found: {dir}", IsError = true });

            ctx.SetWorkingDirectory(Path.GetFullPath(dir));
            return Task.FromResult(new CommandResult { Message = $"Working directory: {dir}" });
        }

        static Task<CommandResult> DepthCapHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
            {
                return Task.FromResult(new CommandResult { Message = $"Agentic depth cap: {ctx.DepthCap}" });
            }

            if (int.TryParse(args[0], out int n) && n >= 1 && n <= 10)
            {
                ctx.SetDepthCap(n);
                return Task.FromResult(new CommandResult { Message = $"Depth cap set to {n}." });
            }

            return Task.FromResult(new CommandResult { Message = "Usage: /depth-cap [1-10]", IsError = true });
        }

        static Task<CommandResult> SystemPromptHandler(string[] args, CommandContext ctx)
        {
            return Task.FromResult(new CommandResult
            {
                Message = $"System prompt ({ctx.SystemPrompt.Length} chars):\n{ctx.SystemPrompt}",
            });
        }

        static Task<CommandResult> HelpHandler(string[] args, CommandContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Slash commands:");
            foreach (var cmd in SlashCommand.ListCommands())
            {
                sb.AppendLine($"  {cmd.Usage,-30} {cmd.Description}");
            }
            return Task.FromResult(new CommandResult { Message = sb.ToString().TrimEnd() });
        }

        static async Task<CommandResult> HistoryHandler(string[] args, CommandContext ctx)
        {
            if (ctx.HistoryStore == null)
                return new CommandResult { Message = "History is disabled." };

            try
            {
                var sessions = await ctx.HistoryStore.ListSessionsAsync(ctx.MachineName, 20);
                if (sessions.Count == 0)
                    return new CommandResult { Message = "No past sessions found." };

                var sb = new StringBuilder();
                sb.AppendLine("Recent sessions:");
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    sb.AppendLine($"  [{i + 1}] {s.CreatedAt:yyyy-MM-dd HH:mm} — {s.Title ?? "(untitled)"} ({s.MessageCount} messages)");
                }
                return new CommandResult { Message = sb.ToString().TrimEnd() };
            }
            catch (Exception ex)
            {
                return new CommandResult { Message = $"History error: {ex.Message}", IsError = true };
            }
        }

        static async Task<CommandResult> ResumeHandler(string[] args, CommandContext ctx)
        {
            if (ctx.HistoryStore == null)
                return new CommandResult { Message = "History is disabled.", IsError = true };

            if (args.Length == 0 || !int.TryParse(args[0], out int index))
                return new CommandResult { Message = "Usage: /resume <N>", IsError = true };

            try
            {
                var sessions = await ctx.HistoryStore.ListSessionsAsync(ctx.MachineName, 20);
                if (index < 1 || index > sessions.Count)
                    return new CommandResult { Message = $"Index out of range (1-{sessions.Count}).", IsError = true };

                var session = sessions[index - 1];
                var messages = await ctx.HistoryStore.GetMessagesAsync(session.SessionId);

                // Reset conversation first.
                ctx.ResetConversation();

                // Prepend historical messages.
                var roles = new List<string>();
                var contents = new List<string>();
                foreach (var m in messages)
                {
                    roles.Add(m.Role);
                    contents.Add(m.Content);
                }
                ctx.PrependMessages(roles.ToArray(), contents.ToArray());

                return new CommandResult
                {
                    Message = $"Resumed session: {session.Title ?? "(untitled)"} ({messages.Count} messages)",
                };
            }
            catch (Exception ex)
            {
                return new CommandResult { Message = $"Resume error: {ex.Message}", IsError = true };
            }
        }

        static async Task<CommandResult> TitleHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
                return new CommandResult { Message = "Usage: /title <text>", IsError = true };

            string title = string.Join(" ", args);

            if (ctx.HistoryStore != null)
            {
                try
                {
                    await ctx.HistoryStore.UpdateSessionTitleAsync(ctx.SessionId, title);
                }
                catch { /* non-fatal */ }
            }

            return new CommandResult { Message = $"Session title: {title}" };
        }
    }
}
```

---
