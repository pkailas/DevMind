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

        /// <summary>Current per-turn generated-token budget (0 = off).</summary>
        public int TokenBudget { get; set; }

        /// <summary>Called to set the token budget (0 disables).</summary>
        public Action<int> SetTokenBudget { get; set; }

        /// <summary>Called to toggle thinking mode.</summary>
        public Action<bool> SetThinking { get; set; }

        // -- History (session persistence) --------------------------------------

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

        // -- Behavioral rules -----------------------------------------------------

        /// <summary>Current behavioral rules text (from TuiConfig).</summary>
        public string BehavioralRules { get; set; } = "";

        /// <summary>Called to set behavioral rules and persist them.</summary>
        public Action<string> SetBehavioralRules { get; set; }

       /// <summary>Called to rebuild the system prompt after rules change.</summary>
        public Func<string> RebuildSystemPrompt { get; set; }

        // -- Working directory ----------------------------------------------------

        /// <summary>Current working directory.</summary>
        public string WorkingDirectory { get; set; } = "";

        /// <summary>Called to change the working directory and reload context.</summary>
        public Action<string> SetWorkingDirectory { get; set; }

        /// <summary>Opens an interactive directory-only picker starting at the given path and
        /// returns the chosen directory, or null if the user cancelled (or no picker is wired).</summary>
        public Func<string, string> BrowseForDirectory { get; set; }
    }

   /// <summary>
    /// Signature for a command handler.
    /// </summary>
    /// <param name="args">Parsed arguments (space-separated tokens after the command name).</param>
    /// <param name="ctx">Runtime context with callbacks.</param>
    /// <returns>Result with message and error flag.</returns>
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
    /// All builtin commands are registered at startup.
    /// </summary>
    public static class SlashCommand
    {
        static readonly Dictionary<string, RegisteredCommand> _commands =
            new Dictionary<string, RegisteredCommand>(StringComparer.OrdinalIgnoreCase);

        static SlashCommand()
        {
            RegisterBuiltinCommands();
        }

        /// <summary>
        /// Register a slash command. Re-registering the same name overwrites.
        /// </summary>
        public static void RegisterCommand(
            string name,
            string description,
            string usage,
            CommandHandler handler)
        {
            if (!name.StartsWith("/"))
                throw new ArgumentException($"command name must start with '/': got {name}");

            _commands[name] = new RegisteredCommand
            {
                Name = name,
                Description = description,
                Usage = usage,
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
        /// whether to dispatch instead of starting an LLM turn.
        /// </summary>
        public static bool IsSlashCommand(string input)
        {
            return !string.IsNullOrEmpty(input) && input.Trim().StartsWith("/");
        }

        /// <summary>
        /// Parse a raw input line into command name + args. Splits on whitespace.
        /// Returns null if the input is not a slash command.
        /// </summary>
        static (string name, string[] args)? ParseInput(string input)
        {
            string trimmed = input.Trim();
            if (!trimmed.StartsWith("/")) return null;

            int firstWs = trimmed.IndexOfAny(new[] { ' ', '\t' });
            if (firstWs < 0)
                return (trimmed, Array.Empty<string>());

            string name = trimmed.Substring(0, firstWs);
            string rawArgs = trimmed.Substring(firstWs).Trim();

            if (string.IsNullOrEmpty(rawArgs))
                return (name, Array.Empty<string>());

            // Simple whitespace split — commands that need text-with-spaces
            // can recombine args internally.
            string[] args = rawArgs.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return (name, args);
        }

       /// <summary>
        /// Dispatch a slash command. Caller has already determined the input
        /// starts with '/'. Returns a CommandResult; the TUI renders it.
        /// Unknown commands produce an error result suggesting /help.
        /// Handler exceptions are caught and converted to error results.
        /// </summary>
        public static async Task<CommandResult> Dispatch(string input, CommandContext context)
        {
            var parsed = ParseInput(input);
            if (parsed == null)
                return new CommandResult
                {
                    Message = $"not a slash command: {input}",
                    IsError = true,
                };

            var (name, args) = parsed.Value;

            if (!_commands.TryGetValue(name, out var cmd))
                return new CommandResult
                {
                    Message = $"unknown command: {name}. Try /help for the list.",
                    IsError = true,
                };

            try
            {
                return await cmd.Handler(args, context);
            }
            catch (Exception ex)
            {
                return new CommandResult
                {
                    Message = $"{name} failed: {ex.GetType().Name}: {ex.Message}",
                    IsError = true,
                };
            }
        }

        // -- Builtin command handlers ----------------------------------------------

        static void RegisterBuiltinCommands()
        {
            RegisterCommand("/new",
                "Start a new session (clears conversation, resets state)",
                "/new",
                NewHandler);

            RegisterCommand("/clear",
                "Clear screen and reset conversation",
                "/clear",
                ClearHandler);

            RegisterCommand("/think",
                "Toggle session thinking mode (reasoning display) on/off",
                "/think on|off",
                ThinkHandler);

            RegisterCommand("/depth-cap",
                "Show or set agentic depth cap (1-200)",
                "/depth-cap [N]",
                DepthCapHandler);

            RegisterCommand("/token-budget",
                "Show or set the per-turn generated-token budget (pauses to ask once exceeded)",
                "/token-budget [N|off]",
                TokenBudgetHandler);

            RegisterCommand("/system_prompt",
                "Display the assembled system prompt",
                "/system_prompt",
                SystemPromptHandler);

           RegisterCommand("/help",
                "Show this list",
                "/help",
                HelpHandler);

            // /restart is an alias for /new — kept for backward compatibility.
            RegisterCommand("/restart",
                "Restart session (alias for /new)",
                "/restart",
                NewHandler);

           // -- History commands -------------------------------------------------

            RegisterCommand("/history",
                "List past sessions from history",
                "/history",
                HistoryHandler);

            RegisterCommand("/resume",
                "Resume a past session by number (from /history listing)",
                "/resume <n>",
                ResumeHandler);

            RegisterCommand("/title",
                "Set the current session's title",
                "/title <text>",
                TitleHandler);

           RegisterCommand("/compact",
                "Force a context compaction pass now",
                "/compact",
                (args, ctx) => Task.FromResult(new CommandResult
                {
                    Message = "/compact: not yet implemented in TUI — requires context summarizer.",
                    IsError = false,
                }));

           RegisterCommand("/t",
                "One-shot: send a message with thinking ON (does not change the /think default)",
                "/t <message>",
                OneShotThinkHandler);

            RegisterCommand("/reasoning",
                "Toggle reasoning display (alias for /think)",
                "/reasoning on|off",
                ThinkHandler);

           RegisterCommand("/rules",
                "Show, set, or clear behavioral rules",
                "/rules [text|clear]",
                RulesHandler);

            RegisterCommand("/lsp",
                "Show or enable/disable language server tools",
                "/lsp on|off",
                (args, ctx) => Task.FromResult(new CommandResult
                {
                    Message = "/lsp: not yet implemented in TUI — LSP not wired in TUI.",
                    IsError = false,
                }));

           RegisterCommand("/dir",
                "Change working directory",
                "/dir [path|-b]",
                DirHandler);

            RegisterCommand("/output-lines",
                "Show or set tool-call output line limit",
                "/output-lines [N]",
                (args, ctx) => Task.FromResult(new CommandResult
                {
                    Message = "/output-lines: not yet implemented in TUI.",
                    IsError = false,
                }));

            RegisterCommand("/training-delete-last",
                "Delete the training log for the current session",
                "/training-delete-last",
                (args, ctx) => Task.FromResult(new CommandResult
                {
                    Message = "/training-delete-last: not yet implemented in TUI.",
                    IsError = false,
                }));
        }

        // -- /new ------------------------------------------------------------------

       static Task<CommandResult> NewHandler(string[] args, CommandContext ctx)
        {
            ctx.ResetConversation();
            return Task.FromResult(new CommandResult { Message = "New session started." });
        }

        // -- /clear ----------------------------------------------------------------

       static Task<CommandResult> ClearHandler(string[] args, CommandContext ctx)
        {
            ctx.ResetConversation();
            return Task.FromResult(new CommandResult { Message = "Conversation cleared." });
        }

        // -- /think on|off ---------------------------------------------------------

       static Task<CommandResult> ThinkHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
            {
                return Task.FromResult(new CommandResult
                {
                    Message = $"Thinking mode: {(ctx.ThinkingEnabled ? "on" : "off")} (session). Usage: /think on|off",
                });
            }

            string arg = args[0]?.Trim().ToLowerInvariant() ?? "";
            if (arg != "on" && arg != "off")
                return Task.FromResult(new CommandResult
                {
                    Message = "Usage: /think on|off",
                    IsError = true,
                });

            bool next = arg == "on";
            ctx.SetThinking(next);

            return Task.FromResult(new CommandResult
            {
                Message = next
                    ? "Thinking mode ON for this session — reasoning renders in output. Use /think off to disable."
                    : "Thinking mode OFF for this session.",
            });
        }

       // -- /t <message> ----------------------------------------------------------

        static Task<CommandResult> OneShotThinkHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
                return Task.FromResult(new CommandResult
                {
                    Message = "Usage: /t <message> — send with thinking ON for this turn only.",
                    IsError = true,
                });

            ctx.OneShotThinking = true;
            return Task.FromResult(new CommandResult
            {
                Message = "Thinking enabled for this turn only.",
            });
        }

       // -- /rules [text|clear] ---------------------------------------------------

        static Task<CommandResult> RulesHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
            {
                // Show current rules.
                if (string.IsNullOrEmpty(ctx.BehavioralRules))
                    return Task.FromResult(new CommandResult { Message = "No rules set." });
                string top = "--- Behavioral Rules ---";
                string bot = "------------------------";
                return Task.FromResult(new CommandResult
                {
                    Message = $"{top}\n{ctx.BehavioralRules}\n{bot}",
                });
            }

            // Reconstruct the full text from args (preserves spaces within the rule text).
            string text = string.Join(" ", args);

            if (text.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                ctx.SetBehavioralRules("");
                ctx.RebuildSystemPrompt();
                return Task.FromResult(new CommandResult { Message = "Behavioral rules cleared." });
            }

            ctx.SetBehavioralRules(text);
            ctx.RebuildSystemPrompt();
            return Task.FromResult(new CommandResult { Message = $"Behavioral rules set ({text.Length} chars)." });
        }

       // -- /dir [path] ------------------------------------------------------------

        static Task<CommandResult> DirHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
            {
                return Task.FromResult(new CommandResult { Message = $"Working directory: {ctx.WorkingDirectory}" });
            }

            // -b: pick the directory interactively instead of typing it. The chosen path is
            // routed through the exact same change logic as "/dir <path>" below (no duplication).
            if (args.Length == 1 && string.Equals(args[0], "-b", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.BrowseForDirectory == null)
                    return Task.FromResult(new CommandResult { Message = "Directory picker is not available.", IsError = true });

                string picked = ctx.BrowseForDirectory(ctx.WorkingDirectory);
                if (string.IsNullOrEmpty(picked))
                    return Task.FromResult(new CommandResult { Message = "Directory pick cancelled — working directory unchanged." });

                return DirHandler(new[] { picked }, ctx);
            }

            string path = Path.GetFullPath(string.Join(" ", args));

            if (!Directory.Exists(path))
                return Task.FromResult(new CommandResult
                {
                    Message = $"Directory not found: {path}",
                    IsError = true,
                });

            ctx.SetWorkingDirectory(path);
            return Task.FromResult(new CommandResult { Message = $"Working directory changed to: {path}" });
        }

        // -- /depth-cap [N] ------------------------------------------------

        const int DepthCapMin = 1;
        const int DepthCapMax = 200;
       static Task<CommandResult> DepthCapHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
            {
                return Task.FromResult(new CommandResult { Message = $"Current depth cap: {ctx.DepthCap}" });
            }

            if (!int.TryParse(args[0], out int n))
                return Task.FromResult(new CommandResult
                {
                    Message = $"Usage: /depth-cap [{DepthCapMin}-{DepthCapMax}]",
                    IsError = true,
                });

            if (n < DepthCapMin || n > DepthCapMax)
                return Task.FromResult(new CommandResult
                {
                    Message = $"Usage: /depth-cap [{DepthCapMin}-{DepthCapMax}]",
                    IsError = true,
                });

            ctx.SetDepthCap(n);
            return Task.FromResult(new CommandResult { Message = $"Depth cap set to {n}" });
        }

        // -- /token-budget [N|off] -----------------------------------------------
        // Per-turn generated-token budget; the loop pauses to ask once exceeded.
        static Task<CommandResult> TokenBudgetHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
            {
                string cur = ctx.TokenBudget > 0 ? $"{ctx.TokenBudget:N0} tokens" : "off";
                return Task.FromResult(new CommandResult { Message = $"Current token budget: {cur}" });
            }

            int value;
            if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase) || args[0] == "0")
                value = 0;
            else if (!int.TryParse(args[0], out value) || value < 0)
                return Task.FromResult(new CommandResult
                {
                    Message = "Usage: /token-budget [N|off]   (N = generated tokens per turn before pausing)",
                    IsError = true,
                });

            ctx.SetTokenBudget(value);
            return Task.FromResult(new CommandResult
            {
                Message = value > 0 ? $"Token budget set to {value:N0} tokens" : "Token budget disabled",
            });
        }

        // -- /system_prompt --------------------------------------------------------

       static Task<CommandResult> SystemPromptHandler(string[] args, CommandContext ctx)
        {
            string prompt = ctx.SystemPrompt ?? "(not available)";
            string top = "--- Current System Prompt ---";
            string bot = "-----------------------------";
            return Task.FromResult(new CommandResult { Message = $"{top}\n{prompt}\n{bot}" });
        }

        // -- /help -----------------------------------------------------------------

       static Task<CommandResult> HelpHandler(string[] args, CommandContext ctx)
        {
            var all = ListCommands();
            var lines = new System.Text.StringBuilder();
            lines.AppendLine("Available commands:");
            lines.AppendLine();

            // Pad usage column for alignment.
            int width = all.Max(c => c.Usage.Length);

            foreach (var c in all)
            {
                string usage = c.Usage.PadRight(width);
                lines.AppendLine($"  {usage}  {c.Description}");
            }

           return Task.FromResult(new CommandResult { Message = lines.ToString().TrimEnd() });
        }

        // -- /history --------------------------------------------------------------

        static async Task<CommandResult> HistoryHandler(string[] args, CommandContext ctx)
        {
            if (ctx.HistoryStore == null)
                return new CommandResult { Message = "History is not enabled.", IsError = true };

            try
            {
                var sessions = await ctx.HistoryStore.ListSessionsAsync(ctx.MachineName);
                // Cap at 20 most recent.
                int count = Math.Min(sessions.Length, 20);
                if (count == 0)
                    return new CommandResult { Message = "No past sessions found." };

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Past sessions (most recent first):");
                for (int i = 0; i < count; i++)
                {
                    var s = sessions[i];
                    string title = string.IsNullOrEmpty(s.Title) ? "(untitled)" : s.Title;
                    string date = s.LastActiveAt.ToString("MMM dd yyyy HH:mm");
                    sb.AppendLine($"  [{i + 1}] {title}  |  {date}  |  {s.MessageCount} messages");
                }
                if (sessions.Length > 20)
                    sb.AppendLine($"  ... and {sessions.Length - 20} more (showing 20 most recent)");
                return new CommandResult { Message = sb.ToString().TrimEnd() };
            }
            catch (Exception ex)
            {
                return new CommandResult { Message = $"Failed to list sessions: {ex.Message}", IsError = true };
            }
        }

        // -- /resume <n> -----------------------------------------------------------

        static async Task<CommandResult> ResumeHandler(string[] args, CommandContext ctx)
        {
            if (ctx.HistoryStore == null)
                return new CommandResult { Message = "History is not enabled.", IsError = true };

            if (args.Length == 0)
                return new CommandResult
                {
                    Message = "Usage: /resume <n>  (run /history first to see session numbers)",
                    IsError = true,
                };

            if (!int.TryParse(args[0], out int n) || n < 1)
                return new CommandResult
                {
                    Message = "Usage: /resume <n>  (n must be a positive integer from /history listing)",
                    IsError = true,
                };

            try
            {
                var sessions = await ctx.HistoryStore.ListSessionsAsync(ctx.MachineName);
                int count = Math.Min(sessions.Length, 20);
                if (n > count)
                    return new CommandResult
                    {
                        Message = $"Session #{n} not found. Valid range: 1-{count} (run /history to refresh).",
                        IsError = true,
                    };

                var session = sessions[n - 1];
                var messages = await ctx.HistoryStore.LoadSessionMessagesAsync(session.SessionId);
               if (messages.Length == 0)
                {
                    string sessionTitle = string.IsNullOrEmpty(session.Title) ? "(untitled)" : session.Title;
                    return new CommandResult { Message = $"Session \"{sessionTitle}\" has no messages to load." };
                }

                // Convert to role/content arrays for PrependMessages.
                string[] roles = new string[messages.Length];
                string[] contents = new string[messages.Length];
                for (int i = 0; i < messages.Length; i++)
                {
                    roles[i] = messages[i].Role;
                    contents[i] = messages[i].Content;
                }

                ctx.PrependMessages(roles, contents);

                string title = string.IsNullOrEmpty(session.Title) ? "(untitled)" : session.Title;
                return new CommandResult { Message = $"Resumed session: {title} ({messages.Length} messages loaded)." };
            }
            catch (Exception ex)
            {
                return new CommandResult { Message = $"Failed to resume session: {ex.Message}", IsError = true };
            }
        }

        // -- /title <text> ---------------------------------------------------------

        static async Task<CommandResult> TitleHandler(string[] args, CommandContext ctx)
        {
            if (ctx.HistoryStore == null)
                return new CommandResult { Message = "History is not enabled.", IsError = true };

            if (args.Length == 0)
                return new CommandResult { Message = "Usage: /title <text>", IsError = true };

           string text = string.Join(" ", args);
            if (string.IsNullOrWhiteSpace(text))
                return new CommandResult { Message = "Title cannot be empty.", IsError = true };

            try
            {
                await ctx.HistoryStore.SetSessionTitleAsync(ctx.SessionId, text.Trim());
                return new CommandResult { Message = $"Session title set: {text.Trim()}" };
            }
            catch (Exception ex)
            {
                return new CommandResult { Message = $"Failed to set title: {ex.Message}", IsError = true };
            }
        }
    }
}
