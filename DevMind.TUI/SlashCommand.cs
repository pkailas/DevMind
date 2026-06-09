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
using System.Linq;

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
    }

    /// <summary>
    /// Signature for a command handler.
    /// </summary>
    /// <param name="args">Parsed arguments (space-separated tokens after the command name).</param>
    /// <param name="ctx">Runtime context with callbacks.</param>
    /// <returns>Result with message and error flag.</returns>
    public delegate CommandResult CommandHandler(string[] args, CommandContext ctx);

   /// <summary>
    /// A registered slash command with metadata.
    /// </summary>
    public sealed class RegisteredCommand
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Usage { get; set; } = string.Empty;
        public CommandHandler Handler { get; set; } = (args, ctx) => new CommandResult { Message = "not implemented" };
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
        public static CommandResult Dispatch(string input, CommandContext context)
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
                return cmd.Handler(args, context);
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

        // ── Builtin command handlers ──────────────────────────────────────────────

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
                "Show or set agentic depth cap (1-10)",
                "/depth-cap [N]",
                DepthCapHandler);

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

            // ── Stubs (not yet implemented in TUI) ──────────────────────────────

            RegisterCommand("/history",
                "List past sessions from history",
                "/history",
                (args, ctx) => new CommandResult
                {
                    Message = "/history: not yet implemented in TUI — requires SQL history backend.",
                    IsError = false,
                });

            RegisterCommand("/resume",
                "Resume a past session by number (from /history listing)",
                "/resume <n>",
                (args, ctx) => new CommandResult
                {
                    Message = "/resume: not yet implemented in TUI — requires SQL history backend.",
                    IsError = false,
                });

            RegisterCommand("/title",
                "Set the current session's title",
                "/title <text>",
                (args, ctx) => new CommandResult
                {
                    Message = "/title: not yet implemented in TUI — requires SQL history backend.",
                    IsError = false,
                });

            RegisterCommand("/compact",
                "Force a context compaction pass now",
                "/compact",
                (args, ctx) => new CommandResult
                {
                    Message = "/compact: not yet implemented in TUI — requires context summarizer.",
                    IsError = false,
                });

            RegisterCommand("/t",
                "One-shot: send a message with thinking ON (does not change the /think default)",
                "/t <message>",
                (args, ctx) => new CommandResult
                {
                    Message = "/t: not yet implemented in TUI — requires one-shot thinking integration with agentic turn.",
                    IsError = false,
                });

            RegisterCommand("/reasoning",
                "Toggle reasoning display (alias for /think)",
                "/reasoning on|off",
                ThinkHandler);

            RegisterCommand("/rules",
                "Show, set, or clear behavioral rules",
                "/rules [text|clear]",
                (args, ctx) => new CommandResult
                {
                    Message = "/rules: not yet implemented in TUI — requires behavioral rules persistence.",
                    IsError = false,
                });

            RegisterCommand("/lsp",
                "Show or enable/disable language server tools",
                "/lsp on|off",
                (args, ctx) => new CommandResult
                {
                    Message = "/lsp: not yet implemented in TUI — LSP not wired in TUI.",
                    IsError = false,
                });

            RegisterCommand("/dir",
                "Change working directory",
                "/dir [path]",
                (args, ctx) => new CommandResult
                {
                    Message = "/dir: not yet implemented in TUI — would need MCP reconnect.",
                    IsError = false,
                });

            RegisterCommand("/output-lines",
                "Show or set tool-call output line limit",
                "/output-lines [N]",
                (args, ctx) => new CommandResult
                {
                    Message = "/output-lines: not yet implemented in TUI.",
                    IsError = false,
                });

            RegisterCommand("/training-delete-last",
                "Delete the training log for the current session",
                "/training-delete-last",
                (args, ctx) => new CommandResult
                {
                    Message = "/training-delete-last: not yet implemented in TUI.",
                    IsError = false,
                });
        }

        // ── /new ──────────────────────────────────────────────────────────────────

        static CommandResult NewHandler(string[] args, CommandContext ctx)
        {
            ctx.ResetConversation();
            return new CommandResult { Message = "New session started." };
        }

        // ── /clear ────────────────────────────────────────────────────────────────

        static CommandResult ClearHandler(string[] args, CommandContext ctx)
        {
            ctx.ResetConversation();
            return new CommandResult { Message = "Conversation cleared." };
        }

        // ── /think on|off ─────────────────────────────────────────────────────────

        static CommandResult ThinkHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
            {
                return new CommandResult
                {
                    Message = $"Thinking mode: {(ctx.ThinkingEnabled ? "on" : "off")} (session). Usage: /think on|off",
                };
            }

            string arg = args[0]?.Trim().ToLowerInvariant() ?? "";
            if (arg != "on" && arg != "off")
                return new CommandResult
                {
                    Message = "Usage: /think on|off",
                    IsError = true,
                };

            bool next = arg == "on";
            ctx.SetThinking(next);

            return new CommandResult
            {
                Message = next
                    ? "Thinking mode ON for this session — reasoning renders in output. Use /think off to disable."
                    : "Thinking mode OFF for this session.",
            };
        }

        // ── /depth-cap [N] ────────────────────────────────────────────────────────

        const int DepthCapMin = 1;
        const int DepthCapMax = 10;

        static CommandResult DepthCapHandler(string[] args, CommandContext ctx)
        {
            if (args.Length == 0)
            {
                return new CommandResult { Message = $"Current depth cap: {ctx.DepthCap}" };
            }

            if (!int.TryParse(args[0], out int n))
                return new CommandResult
                {
                    Message = $"Usage: /depth-cap [{DepthCapMin}-{DepthCapMax}]",
                    IsError = true,
                };

            if (n < DepthCapMin || n > DepthCapMax)
                return new CommandResult
                {
                    Message = $"Usage: /depth-cap [{DepthCapMin}-{DepthCapMax}]",
                    IsError = true,
                };

            ctx.SetDepthCap(n);
            return new CommandResult { Message = $"Depth cap set to {n}" };
        }

        // ── /system_prompt ────────────────────────────────────────────────────────

        static CommandResult SystemPromptHandler(string[] args, CommandContext ctx)
        {
            string prompt = ctx.SystemPrompt ?? "(not available)";
            string top = "─── Current System Prompt ───";
            string bot = "─────────────────────────────";
            return new CommandResult { Message = $"{top}\n{prompt}\n{bot}" };
        }

        // ── /help ─────────────────────────────────────────────────────────────────

        static CommandResult HelpHandler(string[] args, CommandContext ctx)
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

            return new CommandResult { Message = lines.ToString().TrimEnd() };
        }
    }
}
