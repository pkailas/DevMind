// File: Program.cs  v1.0 (SPIKE)
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
using System.Collections.Generic;
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

            // Main window.
            using Window window = new() { Title = "DevMind TUI (SPIKE) — Esc to quit" };

           // Output TextView (read-only, scrollable).
            TextView outputView = new()
            {
                ReadOnly = true,
                X = 0, Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 2, // leave room for input + status rows
            };

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
            var callbacks = new TuiLoopCallbacks(llmClient, statusLabel, outputView, inputField);
            var state = new LoopState();
            var driver = new LoopDriver(llmClient, host, callbacks, options, state);

            // Build combined system prompt.
            string combinedSystemPrompt = BuildCombinedSystemPrompt(options, devMindContext);

            // Banner.
            outputView.InsertText("╔══════════════════════════════════════════════════════════╗\n");
            outputView.InsertText("║  DevMind TUI — Phase 1 SPIKE                           ║\n");
            outputView.InsertText($"║  Endpoint: {options.EndpointUrl,-47}║\n");
            outputView.InsertText($"║  Working dir: {options.WorkingDirectory,-43}║\n");
            outputView.InsertText("╚══════════════════════════════════════════════════════════╝\n\n");

            // Focus input field.
            inputField.SetFocus();

            // Handle Enter in input field — submit to LLM.
            inputField.Accepting += async (s, e) =>
            {
                string input = inputField.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(input)) return;

                // Clear input.
                inputField.Text = "";

                // Echo user input.
                outputView.InsertText($"\n> {input}\n");

                // Disable input during processing.
                inputField.CanFocus = false;
                statusLabel.Text = "Processing...";

                // Refresh CTS after a cancelled turn.
                if (cts.IsCancellationRequested)
                {
                    var old = cts;
                    cts = new CancellationTokenSource();
                    old.Dispose();
                }

                // Handle /restart command.
                if (input.Equals("/restart", StringComparison.OrdinalIgnoreCase))
                {
                    state.ResetForUserTurn();
                    llmClient.ClearHistory();
                    host.ResetSession();
                    var old = cts;
                    cts = new CancellationTokenSource();
                    old.Dispose();
                    outputView.InsertText("[REPL] Session restarted.\n");
                    inputField.CanFocus = true;
                    inputField.SetFocus();
                    statusLabel.Text = "Ready";
                    return;
                }

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
