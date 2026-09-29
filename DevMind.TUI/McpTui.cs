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
using Newtonsoft.Json.Linq;

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
            sb.Append("/mcp tools <name> lists a server's tools; /mcp restart <name> restarts one.");
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

            if (args.Length >= 1 && args.Length <= 2 && string.Equals(args[0], "tools", StringComparison.OrdinalIgnoreCase))
            {
                string? name = args.Length == 2 ? args[1]
                    : clients.ServerNames.Count == 1 ? clients.ServerNames[0]
                    : null;
                if (name == null)
                    return new CommandResult { Message = Usage, IsError = true };
                if (!clients.ServerNames.Contains(name, StringComparer.Ordinal))
                    return new CommandResult
                    {
                        Message = $"/mcp tools: unknown server '{name}' — configured: {string.Join(", ", clients.ServerNames)}.",
                        IsError = true,
                    };
                var status = clients.GetStatuses().First(s => s.Name == name);
                return new CommandResult { Message = FormatTools(status, clients.GetExposedTools()) };
            }

            return new CommandResult { Message = Usage, IsError = true };
        }

        public const string Usage = "usage: /mcp  |  /mcp tools <name>  |  /mcp restart <name>";

        /// <summary>Longest description shown per tool in /mcp tools, before the ellipsis.</summary>
        public const int ToolDescriptionChars = 80;

        /// <summary>
        /// /mcp tools &lt;name&gt;: exactly what the model is offered from that server — the
        /// manager's cached, allowlisted OpenAI tool objects (the set LlmClient sends), filtered by
        /// the server's mcp__&lt;name&gt;__ prefix. No round-trip to the server. A server that is
        /// not ready shows its state and "no tools available".
        /// </summary>
        public static string FormatTools(McpServerStatus status, IReadOnlyList<JObject> exposed)
        {
            string scope = status.HasAllowlist ? "(allowlist active)" : "(all tools)";
            if (status.State != McpServerState.Ready)
            {
                string state = status.State.ToString().ToLowerInvariant();
                string why = status.State == McpServerState.Failed && !string.IsNullOrEmpty(status.Error)
                    ? $" ({status.Error})" : "";
                return $"{status.Name} — {state}{why}; no tools available {scope}";
            }

            string prefix = $"mcp__{status.Name}__";
            string descPrefix = $"[{status.Name} MCP] ";
            var tools = exposed
                .Select(t => t["function"] as JObject)
                .Where(f => f != null && ((string?)f["name"])?.StartsWith(prefix, StringComparison.Ordinal) == true)
                .Select(f => (name: (string)f!["name"]!, desc: OneLine((string?)f["description"] ?? "", descPrefix)))
                .OrderBy(t => t.name, StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder($"{status.Name} — {tools.Count} tool{(tools.Count == 1 ? "" : "s")} {scope}");
            int width = tools.Count == 0 ? 0 : tools.Max(t => t.name.Length);
            foreach (var (name, desc) in tools)
            {
                sb.Append("\n  ").Append(name.PadRight(width));
                if (desc.Length > 0) sb.Append("  ").Append(desc);
            }
            return sb.ToString();
        }

        /// <summary>The description as the operator should read it: without the "[server MCP] " tag the model sees, first line only, cut at <see cref="ToolDescriptionChars"/>.</summary>
        private static string OneLine(string description, string serverTag)
        {
            string d = description.StartsWith(serverTag, StringComparison.Ordinal)
                ? description.Substring(serverTag.Length)
                : description;
            d = d.TrimStart().Split('\n')[0].TrimEnd('\r', ' ');
            return d.Length <= ToolDescriptionChars ? d : d.Substring(0, ToolDescriptionChars).TrimEnd() + "…";
        }
    }
}
