// File: McpTui.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The TUI's side of the MCP client: background autostart at session start, and the text
// of the /mcp command.
//
// Startup must never wait on a server. comfy-mcp takes seconds to start and a dead server
// can take the whole 60 s MCP initialize timeout to fail (the Sep 9 lesson: a slow probe at
// startup blew Claude Desktop's limit). So BeginAutoStart hands every start to the thread
// pool and returns at once. The tools reach the model on the first request after a server
// reports ready, because LlmClient reads the manager's exposed-tool snapshot per request.
// Each server reports exactly once: "[MCP] comfy ready (N tools)" or "[MCP] comfy failed
// to start: <reason>". Nothing is reported per turn.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    public static class McpTui
    {
        /// <summary>
        /// Starts <paramref name="names"/> in the background and returns immediately. Each
        /// outcome is reported once through <paramref name="report"/>, from a pool thread; the
        /// caller marshals it to the UI. The returned task completes when every start has
        /// finished. Startup discards it; tests observe it. Never throws.
        /// </summary>
        public static Task BeginAutoStart(IMcpClientManager clients, IEnumerable<string> names,
            Action<string, OutputColor> report)
        {
            var starts = names.Select(name => Task.Run(async () =>
            {
                try
                {
                    string? err = await clients.StartAsync(name, CancellationToken.None).ConfigureAwait(false);
                    ReportOutcome(clients, name, err, report);
                }
                catch (Exception ex)
                {
                    report(FailedLine(name, ex.Message), OutputColor.Error);
                }
            })).ToArray();
            return Task.WhenAll(starts);
        }

        /// <summary>The names of the servers the TUI starts on its own: those with autoStart (default true).</summary>
        public static IReadOnlyList<string> AutoStartNames(IEnumerable<McpServerConfig> configs) =>
            configs.Where(c => c.AutoStart).Select(c => c.Name).ToList();

        public static string ReadyLine(string name, int tools) =>
            $"[MCP] {name} ready ({tools} tool{(tools == 1 ? "" : "s")})\n";

        public static string FailedLine(string name, string reason) =>
            $"[MCP] {name} failed to start: {reason}\n";

        private static string ShortReason(string error) => McpClientManager.ShortReason(error);

        private static void ReportOutcome(IMcpClientManager clients, string name, string? err,
            Action<string, OutputColor> report)
        {
            if (err == null)
            {
                int tools = clients.GetStatuses().FirstOrDefault(s => s.Name == name)?.ToolCount ?? 0;
                report(ReadyLine(name, tools), OutputColor.Success);
            }
            else
            {
                report(FailedLine(name, ShortReason(err)), OutputColor.Error);
            }
        }

        // ── /mcp ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// /mcp with no arguments: one line per configured server. The columns are name, state,
        /// tool count, whether an allowlist is active and whether it autostarts, then the
        /// failure reason for a failed server.
        /// </summary>
        public static string FormatStatus(IReadOnlyList<McpServerStatus> statuses)
        {
            if (statuses == null || statuses.Count == 0)
                return NotConfigured;

            int nameWidth = Math.Max(6, statuses.Max(s => s.Name.Length));
            var sb = new StringBuilder("MCP servers:\n");
            foreach (var s in statuses)
            {
                string state = s.State.ToString().ToLowerInvariant();
                string tools = s.State == McpServerState.Ready
                    ? $"{s.ToolCount} tool{(s.ToolCount == 1 ? "" : "s")}"
                    : "-";
                sb.Append("  ").Append(s.Name.PadRight(nameWidth))
                  .Append("  ").Append(state.PadRight(8))
                  .Append("  ").Append(tools.PadRight(9))
                  .Append("  ").Append(s.HasAllowlist ? "allowlist" : "all tools")
                  .Append(s.AutoStart ? "" : "  (manual start)");
                if (s.State == McpServerState.Failed && !string.IsNullOrEmpty(s.Error))
                    sb.Append("  — ").Append(s.Error);
                sb.Append('\n');
            }
            sb.Append("/mcp restart <name> restarts one server.");
            return sb.ToString();
        }

        public const string NotConfigured =
            "No MCP servers configured. Add an \"mcpServers\" block to %APPDATA%\\devmind\\devmind.json " +
            "(same shape as claude_desktop_config.json) and restart DevMind.";

        /// <summary>
        /// The whole /mcp command: no args, list; "restart &lt;name&gt;", restart and say how it went.
        /// Everything else is a usage error.
        /// </summary>
        public static async Task<CommandResult> HandleAsync(string[] args, IMcpClientManager? clients)
        {
            if (clients == null)
                return new CommandResult { Message = NotConfigured };

            if (args.Length == 0)
                return new CommandResult { Message = FormatStatus(clients.GetStatuses()) };

            if (args.Length == 2 && string.Equals(args[0], "restart", StringComparison.OrdinalIgnoreCase))
            {
                string name = args[1];
                if (!clients.ServerNames.Contains(name, StringComparer.Ordinal))
                    return new CommandResult
                    {
                        Message = $"/mcp: no server named '{name}'. Configured: {string.Join(", ", clients.ServerNames)}.",
                        IsError = true,
                    };

                string? err = await clients.RestartAsync(name).ConfigureAwait(false);
                if (err != null)
                    return new CommandResult { Message = FailedLine(name, ShortReason(err)).TrimEnd(), IsError = true };
                int tools = clients.GetStatuses().FirstOrDefault(s => s.Name == name)?.ToolCount ?? 0;
                return new CommandResult { Message = ReadyLine(name, tools).TrimEnd() };
            }

            return new CommandResult { Message = "usage: /mcp  |  /mcp restart <name>", IsError = true };
        }
    }
}
