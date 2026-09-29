// File: McpServerConfig.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The "mcpServers" block of %APPDATA%\devmind\devmind.json: the external MCP servers
// DevMind may launch as a CLIENT (stdio only). The shape deliberately mirrors
// claude_desktop_config.json so an entry can be pasted across unchanged:
//
//   "mcpServers": {
//     "comfy": {
//       "command": "C:\\...\\comfy-mcp.exe",
//       "args": [],
//       "env": { "COMFY_BIN": "C:\\...\\comfy.exe" },
//       "tools": ["server_info", "run_workflow"],   // optional allowlist; absent = all
//       "callTimeoutSeconds": 900,                   // optional; default 120
//       "autoStart": true                            // optional; default true (TUI only)
//     }
//   }
//
// Parsing is pure: no process is started and no path is probed (the Sep 9 lesson — a
// UNC existence probe in WriteRootPolicy blew Claude Desktop's 60 s MCP-initialize
// limit). Servers start lazily, on first use, in McpClientManager. A missing or empty
// block yields an empty list: the feature is off and nothing else changes.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>One external MCP server entry from the <c>mcpServers</c> config block.</summary>
    public sealed class McpServerConfig
    {
        /// <summary>Per-call timeout when the entry sets none.</summary>
        public const int DefaultCallTimeoutSeconds = 120;

        /// <summary>The config key; also the <c>&lt;server&gt;</c> part of <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>.</summary>
        public string Name { get; init; } = "";

        /// <summary>Executable to launch. A non-.exe/.com command (npx, a .cmd shim) runs via cmd.exe /c on Windows.</summary>
        public string Command { get; init; } = "";

        public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

        /// <summary>Overrides applied on top of the host's full environment. A null value removes the variable.</summary>
        public IReadOnlyDictionary<string, string?> Env { get; init; } = new Dictionary<string, string?>();

        /// <summary>Tool allowlist; null = every tool the server lists. Empty = none.</summary>
        public IReadOnlyList<string>? Tools { get; init; }

        public int CallTimeoutSeconds { get; init; } = DefaultCallTimeoutSeconds;

        /// <summary>
        /// Whether the TUI starts this server in the background at session start (default
        /// true). Headless jobs ignore it: they start exactly the servers the job names in
        /// devmind_task_start's mcp_servers.
        /// </summary>
        public bool AutoStart { get; init; } = true;

        /// <summary>True when <paramref name="tool"/> passes the allowlist (always, when there is none).</summary>
        public bool AllowsTool(string tool)
        {
            if (Tools == null) return true;
            foreach (var t in Tools)
                if (string.Equals(t, tool, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// Loads the block from the global devmind.json (via <see cref="TuiConfig"/>, so the
        /// DEVMIND_GLOBAL_DIR test seam applies). Reads one file; starts nothing.
        /// </summary>
        public static IReadOnlyList<McpServerConfig> Load(Action<string>? warn = null)
        {
            var raw = TuiConfig.Load().McpServers;
            if (raw == null) return Array.Empty<McpServerConfig>();
            using var doc = JsonDocument.Parse(raw.ToJsonString());
            return ParseBlock(doc.RootElement, warn);
        }

        /// <summary>
        /// Parses the value of <c>mcpServers</c>. Anything but an object yields an empty
        /// list. A bad entry (non-object, invalid name, missing command, wrongly-typed
        /// field) is skipped with a message to <paramref name="warn"/>; its siblings still
        /// load. A bad <c>callTimeoutSeconds</c> falls back to the default with a warning
        /// rather than dropping the whole server.
        /// </summary>
        public static IReadOnlyList<McpServerConfig> ParseBlock(JsonElement block, Action<string>? warn = null)
        {
            var result = new List<McpServerConfig>();
            if (block.ValueKind != JsonValueKind.Object) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in block.EnumerateObject())
            {
                string name = prop.Name;
                string Where() => $"mcpServers entry '{name}'";

                if (!McpToolName.IsValidServerName(name))
                {
                    warn?.Invoke($"{Where()}: server name must match [a-z0-9_-]+ with no '__' and no trailing '_', skipping");
                    continue;
                }
                if (!seen.Add(name))
                {
                    warn?.Invoke($"{Where()}: duplicate name, skipping the later entry");
                    continue;
                }

                var e = prop.Value;
                if (e.ValueKind != JsonValueKind.Object)
                {
                    warn?.Invoke($"{Where()}: not an object, skipping");
                    continue;
                }

                // Pasted HTTP/SSE entries carry a "type" (or a "url" and no command); only stdio exists here.
                if (e.TryGetProperty("type", out var type)
                    && !(type.ValueKind == JsonValueKind.String && type.GetString() == "stdio"))
                {
                    warn?.Invoke($"{Where()}: only stdio servers are supported (type {type.GetRawText()}), skipping");
                    continue;
                }

                if (!e.TryGetProperty("command", out var cmd) || cmd.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(cmd.GetString()))
                {
                    warn?.Invoke($"{Where()}: \"command\" is missing or not a non-empty string, skipping");
                    continue;
                }

                if (!TryStringArray(e, "args", out var args))
                {
                    warn?.Invoke($"{Where()}: \"args\" must be an array of strings, skipping");
                    continue;
                }

                var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (e.TryGetProperty("env", out var envEl) && envEl.ValueKind != JsonValueKind.Null)
                {
                    bool envOk = envEl.ValueKind == JsonValueKind.Object;
                    if (envOk)
                    {
                        foreach (var kv in envEl.EnumerateObject())
                        {
                            if (kv.Value.ValueKind == JsonValueKind.String) env[kv.Name] = kv.Value.GetString();
                            else if (kv.Value.ValueKind == JsonValueKind.Null) env[kv.Name] = null;
                            else { envOk = false; break; }
                        }
                    }
                    if (!envOk)
                    {
                        warn?.Invoke($"{Where()}: \"env\" must be an object of string values, skipping");
                        continue;
                    }
                }

                if (!TryStringArray(e, "tools", out var tools))
                {
                    warn?.Invoke($"{Where()}: \"tools\" must be an array of strings, skipping");
                    continue;
                }
                if (tools != null && tools.Count == 0)
                    warn?.Invoke($"{Where()}: \"tools\" is an empty allowlist, so no tool of this server is exposed");

                int timeout = DefaultCallTimeoutSeconds;
                if (e.TryGetProperty("callTimeoutSeconds", out var to) && to.ValueKind != JsonValueKind.Null)
                {
                    if (to.ValueKind == JsonValueKind.Number && to.TryGetInt32(out int secs) && secs > 0)
                        timeout = secs;
                    else
                        warn?.Invoke($"{Where()}: \"callTimeoutSeconds\" must be a positive integer, using {DefaultCallTimeoutSeconds}");
                }

                bool autoStart = true;
                if (e.TryGetProperty("autoStart", out var auto) && auto.ValueKind != JsonValueKind.Null)
                {
                    if (auto.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        autoStart = auto.GetBoolean();
                    else
                        warn?.Invoke($"{Where()}: \"autoStart\" must be true or false, using true");
                }

                result.Add(new McpServerConfig
                {
                    Name               = name,
                    Command            = cmd.GetString()!,
                    Args               = (IReadOnlyList<string>?)args ?? Array.Empty<string>(),
                    Env                = env,
                    Tools              = tools,
                    CallTimeoutSeconds = timeout,
                    AutoStart          = autoStart,
                });
            }
            return result;
        }

        /// <summary>
        /// Reads an optional string-array property. Absent or null → true with a null list;
        /// a non-array or a non-string element → false.
        /// </summary>
        private static bool TryStringArray(JsonElement obj, string property, out List<string>? values)
        {
            values = null;
            if (!obj.TryGetProperty(property, out var el) || el.ValueKind == JsonValueKind.Null)
                return true;
            if (el.ValueKind != JsonValueKind.Array)
                return false;
            values = new List<string>();
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) { values = null; return false; }
                values.Add(item.GetString()!);
            }
            return true;
        }
    }

    /// <summary>
    /// The agent-facing name of an external MCP tool: <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>
    /// (the Claude Code convention). Server names are <c>[a-z0-9_-]+</c> with no double
    /// underscore and no trailing underscore, so the first <c>__</c> after the prefix always
    /// ends the server part (a trailing '_' would merge into the separator) and a tool name
    /// may itself contain <c>__</c>.
    /// </summary>
    public static class McpToolName
    {
        public const string Prefix = "mcp__";
        private const string Separator = "__";

        private static readonly Regex ServerNamePattern = new Regex("^[a-z0-9_-]+$", RegexOptions.CultureInvariant);

        public static bool IsValidServerName(string? server) =>
            !string.IsNullOrEmpty(server)
            && ServerNamePattern.IsMatch(server)
            && !server.Contains(Separator, StringComparison.Ordinal)
            && !server.EndsWith('_');

        /// <summary>Builds <c>mcp__server__tool</c>. Throws on an invalid server name or an empty tool name.</summary>
        public static string Build(string server, string tool)
        {
            if (!IsValidServerName(server))
                throw new ArgumentException($"Invalid MCP server name '{server}'.", nameof(server));
            if (string.IsNullOrEmpty(tool))
                throw new ArgumentException("Tool name is empty.", nameof(tool));
            return Prefix + server + Separator + tool;
        }

        /// <summary>
        /// The <c>ExecutionResult.ToolResultContents</c> key for one MCP call's result: its
        /// tool_call id. Every other tool keys its result by an argument value (filename, query,
        /// url) or a literal ("run_sql"), which is fine for idempotent reads, but two identical
        /// MCP calls in one turn (run_workflow twice) are two different results, so AgenticExecutor
        /// writes and LoopHelpers reads under the call id.
        /// </summary>
        public static string ResultKey(string toolCallId) => "mcp:" + (toolCallId ?? "");

        /// <summary>Splits <c>mcp__server__tool</c>; false for anything else (including a non-MCP tool name).</summary>
        public static bool TryParse(string? qualified, out string server, out string tool)
        {
            server = "";
            tool = "";
            if (qualified == null || !qualified.StartsWith(Prefix, StringComparison.Ordinal))
                return false;
            string rest = qualified.Substring(Prefix.Length);
            int sep = rest.IndexOf(Separator, StringComparison.Ordinal);
            if (sep <= 0 || sep + Separator.Length >= rest.Length)
                return false;
            string s = rest.Substring(0, sep);
            if (!IsValidServerName(s))
                return false;
            server = s;
            tool = rest.Substring(sep + Separator.Length);
            return true;
        }
    }
}
