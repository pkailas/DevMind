// File: Program.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    internal static class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (HasFlag(args, "--help") || HasFlag(args, "-h"))
            {
                PrintUsage();
                return 0;
            }

            var options = CliOptions.FromArgs(args);

            // Context file discovery: check working dir then git root.
            string devMindContext = LoadContextFile(options.WorkingDirectory);

            // Construct core objects
            var llmClient = new LlmClient(options);
            llmClient.Configure(options.EndpointUrl, options.ApiKey);

            var cts = new CancellationTokenSource();
            var host = new ConsoleAgenticHost(options.WorkingDirectory, () => cts.Cancel());
            var callbacks = new ConsoleLoopCallbacks(llmClient);
            var state = new LoopState();
            var driver = new LoopDriver(llmClient, host, callbacks, options, state);

            // Ctrl+C cancels the current turn without terminating the process.
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            // Build combined system prompt once for the session lifetime.
            string combinedSystemPrompt = BuildCombinedSystemPrompt(options, devMindContext);

            PrintBanner(options, devMindContext != null);

            // ── REPL ─────────────────────────────────────────────────────────────

            while (true)
            {
                Console.Write("\n> ");
                string input = Console.ReadLine();

                if (input == null) break; // EOF (piped input exhausted)
                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    input.Equals("quit", StringComparison.OrdinalIgnoreCase))
                    break;
                if (string.IsNullOrWhiteSpace(input)) continue;

                // Refresh CTS after a cancelled turn so the session stays alive.
                if (cts.IsCancellationRequested)
                {
                    var old = cts;
                    cts = new CancellationTokenSource();
                    old.Dispose();
                }

                if (input.Equals("/restart", StringComparison.OrdinalIgnoreCase))
                {
                    state.ResetForUserTurn();
                    llmClient.ClearHistory();
                    host.ResetSession();
                    var old = cts;
                    cts = new CancellationTokenSource();
                    old.Dispose();
                    Console.WriteLine("[REPL] Session restarted.");
                    continue;
                }

                if (input.Equals("/context", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[REPL] /context: not yet implemented in v1.");
                    continue;
                }

                // Direct PATCH routing — equivalent to the extension's input.StartsWith("PATCH ") check.
                if (input.StartsWith("PATCH ", StringComparison.OrdinalIgnoreCase))
                {
                    await RunDirectPatchAsync(input, host, cts.Token);
                    continue;
                }

                await RunTurnAsync(input, options, llmClient, host, driver, state,
                    callbacks, combinedSystemPrompt, cts);
            }

            return 0;
        }

        // ── Agentic turn loop ─────────────────────────────────────────────────────

        static async Task RunTurnAsync(
            string userInput,
            CliOptions options,
            LlmClient llmClient,
            ConsoleAgenticHost host,
            LoopDriver driver,
            LoopState state,
            ConsoleLoopCallbacks callbacks,
            string combinedSystemPrompt,
            CancellationTokenSource cts)
        {
            state.ResetForUserTurn();
            host.ResetTaskContext();

            var thinkFilter = new ThinkFilter();
            string currentPrompt = userInput;
            bool firstIteration = true;

            while (true)
            {
                if (cts.Token.IsCancellationRequested) return;

                // Mirror extension: IncrementTurn() on every SendMessageAsync call,
                // including agentic re-triggers. (IncrementTurn is on LlmClient, not ILlmClient.)
                llmClient.IncrementTurn();
                host.CancellationToken = cts.Token;

                if (!firstIteration) thinkFilter.Reset();
                firstIteration = false;

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var responseBuffer = new StringBuilder();
                bool timerStopped = false;
                bool suppressDisplay = false; // FILE: / END_FILE suppression
                string lineAccum = string.Empty;

                callbacks.StartThinkingTimer(state.AgenticDepth, options.AgenticLoopMaxDepth);

                // LlmClient swallows OperationCanceledException silently without calling
                // onComplete or onError. Without this registration, await tcs.Task below
                // would block forever after Ctrl+C.
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
                            Console.Write("\x1b[35m" + thinkText + "\x1b[0m");
                        }

                        if (string.IsNullOrEmpty(visible)) return;

                        if (!timerStopped) { callbacks.StopThinkingTimer(); timerStopped = true; }

                        responseBuffer.Append(visible);

                        // Check completed lines for FILE: / END_FILE at newline boundaries.
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
                            Console.Write(visible);
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
                    Console.WriteLine("\n[Stopped]");
                    return;
                }
                catch (Exception ex)
                {
                    if (!timerStopped) callbacks.StopThinkingTimer();
                    Console.Error.WriteLine($"\n[ERROR] {ex.Message}");
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
                    Console.WriteLine("[Stopped]");
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

        // ── Direct PATCH routing (CLI equivalent of extension's input.StartsWith("PATCH ")) ──

        static async Task RunDirectPatchAsync(string patchInput, ConsoleAgenticHost host,
            CancellationToken ct)
        {
            IAgenticHost agenticHost = host;

            var resolved = await agenticHost.ResolvePatchAsync(patchInput, fromToolCall: false);
            if (resolved == null)
            {
                Console.Error.WriteLine("[PATCH] Resolve failed — check file name and FIND/REPLACE syntax.");
                return;
            }

            try
            {
                var approved = await agenticHost.ShowDiffPreviewAsync(
                    new List<PatchResolveResult> { resolved }, ct);

                if (approved.Contains(0))
                    await agenticHost.ApplyResolvedPatchAsync(resolved);
                else
                    Console.WriteLine("[PATCH] Skipped.");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\n[PATCH] Cancelled.");
            }
        }

        // ── System prompt assembly (mirrors extension's SendToLlm combinedSystemPrompt block) ──

        static string BuildCombinedSystemPrompt(CliOptions options, string devMindContext)
        {
            // buildCommand and projectNamespace are null in CLI context — no VS project loaded.
            string llmDirective = LoopHelpers.BuildToolUsePrompt(
                buildCommand: null, projectNamespace: null);

            string combined = $"{options.SystemPrompt}\n\n{llmDirective}";

            if (!string.IsNullOrEmpty(devMindContext))
               combined += $"\n\n--- Project Context (AGENTS.md) ---\n{devMindContext}\n---";

            // Best-effort memory index injection (same as extension)
            try
            {
                var memMgr = new MemoryManager(options.WorkingDirectory);
                string memoryIndex = memMgr.LoadIndex();
                if (!string.IsNullOrWhiteSpace(memoryIndex))
                    combined += $"\n\n--- Session Memory (MEMORY.md) ---\n{memoryIndex}\n---";
            }
            catch { }

            return combined;
        }

        // ── Context file discovery (replaces DTE solution-directory lookup) ──────

        static string LoadContextFile(string workingDir)
        {
            // Check working directory first, then walk up to git root.
            var searchDirs = new List<string> { workingDir };
            string gitRoot = FindGitRoot(workingDir);
            if (gitRoot != null &&
                !string.Equals(gitRoot, workingDir, StringComparison.OrdinalIgnoreCase))
                searchDirs.Add(gitRoot);

           foreach (string dir in searchDirs)
            {
                string path = Path.Combine(dir, "AGENTS.md");
                if (!File.Exists(path)) continue;

                try
                {
                    string content = File.ReadAllText(path);
                    Console.Error.WriteLine($"[CONTEXT] Loaded AGENTS.md from {dir}");
                    return content;
                }
                catch { }
            }

            return null;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        static string FindGitRoot(string dir)
        {
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
                string parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            return null;
        }

        static bool HasFlag(string[] args, string flag)
            => Array.IndexOf(args, flag) >= 0;

        static void PrintBanner(CliOptions options, bool contextLoaded)
        {
            Console.WriteLine("DevMind CLI v1.0  |  iOnline Consulting LLC");
            Console.WriteLine($"Endpoint : {options.EndpointUrl}");
            Console.WriteLine($"Model    : {(string.IsNullOrEmpty(options.ModelName) ? "(server default)" : options.ModelName)}");
            Console.WriteLine($"Directory: {options.WorkingDirectory}");
            Console.WriteLine($"MaxDepth : {options.AgenticLoopMaxDepth}  |  Eviction: {options.ContextEviction}");
            if (!contextLoaded)
               Console.Error.WriteLine("[CONTEXT] No AGENTS.md found.");
            Console.WriteLine("Commands: /restart  exit  quit  |  Ctrl+C cancels current turn");
        }

        static void PrintUsage()
        {
            Console.WriteLine("DevMind CLI — local LLM coding assistant");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  DevMind.Cli [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --endpoint <url>        LLM endpoint base URL (default: http://127.0.0.1:1234/v1)");
            Console.WriteLine("  --api-key <key>         Bearer token (default: lm-studio)");
            Console.WriteLine("  --model <name>          Model name (default: server default)");
            Console.WriteLine("  --dir <path>            Working directory for file operations (default: CWD)");
            Console.WriteLine("  --max-depth <n>         Agentic loop max depth (default: 5)");
            Console.WriteLine("  --always-confirm        Pause for diff preview on all PATCH blocks");
            Console.WriteLine("  --context-size <n>      Override context window size (default: auto-detect)");
            Console.WriteLine("  --eviction <mode>       Context eviction: Off, Balanced, Aggressive (default: Balanced)");
            Console.WriteLine("  --server-type <type>    Server type: LlamaServer, LmStudio, Custom (default: LlamaServer)");
            Console.WriteLine("  --system-prompt <text>  Override system prompt");
            Console.WriteLine("  --timeout <minutes>     Request timeout in minutes (default: 10)");
            Console.WriteLine("  --debug                 Enable debug output");
            Console.WriteLine("  --thinking              Show <think> tokens");
            Console.WriteLine("  --brainwash             Enable micro-compact brainwash escalation");
            Console.WriteLine("  --no-budget             Suppress context budget display");
            Console.WriteLine("  --help, -h              Show this help");
            Console.WriteLine();
            Console.WriteLine("Config file:");
            Console.WriteLine("  Place devmind.json in the working directory to set persistent defaults.");
            Console.WriteLine("  Command-line args override devmind.json values.");
            Console.WriteLine();
            Console.WriteLine("REPL commands:");
            Console.WriteLine("  /restart    Clear conversation history and reset session");
            Console.WriteLine("  /context    (v1: not yet implemented)");
            Console.WriteLine("  exit / quit Exit");
            Console.WriteLine("  Ctrl+C      Cancel current LLM generation (session stays alive)");
            Console.WriteLine();
            Console.WriteLine("Direct PATCH:");
            Console.WriteLine("  Paste a PATCH block (starting with 'PATCH <filename>') to apply");
            Console.WriteLine("  directly with diff preview — no LLM round-trip needed.");
        }
    }
}
