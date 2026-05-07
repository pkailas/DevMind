// File: Program.cs  v1.2
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// DevMind.McpServer — MCP server exposing DevMind.Core capabilities as MCP tools.
//
// Transport: stdio (stdin/stdout). stdout is owned exclusively by the MCP JSON-RPC
// transport — application code must NEVER write to stdout (Console.Out / Console.Write /
// Console.WriteLine). All diagnostics go to stderr (Console.Error).
//
// Usage:
//   DevMind.McpServer [--dir <working-directory>]
//   --dir defaults to Environment.CurrentDirectory when omitted.
//
// v1.2: trace startup, exit, and env-dump events via DevMind.Trace.

using DevMind.McpServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using DmTrace = DevMind.Trace;

// ── Parse --dir argument ──────────────────────────────────────────────────────

string workingDirectory = Environment.CurrentDirectory;
for (int i = 0; i < args.Length - 1; i++)
{
    if (string.Equals(args[i], "--dir", StringComparison.OrdinalIgnoreCase))
    {
        string rawValue = args[i + 1];

        if (!string.IsNullOrWhiteSpace(rawValue) && !System.IO.Path.IsPathRooted(rawValue))
        {
            Console.Error.WriteLine(
                $"[McpServer] Error: --dir must be an absolute path. Received: '{rawValue}'. " +
                "Common cause: backslash escape issues in argument passing (e.g., MCP Inspector " +
                "on Windows may strip 'C:\\' before \\t). Try forward slashes (C:/path), " +
                "doubled backslashes (C:\\\\path), or wrapping the path in quotes.");
            DmTrace.Shutdown(2);
            Environment.Exit(2);
        }

        workingDirectory = rawValue;
        break;
    }
}

Console.Error.WriteLine($"[McpServer] Starting. Working directory: {workingDirectory}");

// ── Trace startup ─────────────────────────────────────────────────────────────

// Trace.cs reads DEVMIND_TRACE_* env vars on first call; if tracing is
// disabled, these calls return cheaply.
var startupData = new Dictionary<string, object>
{
    ["argv"]           = args,
    ["cwd"]            = Environment.CurrentDirectory,
    ["working_dir"]    = workingDirectory,
    ["platform"]       = Environment.OSVersion.Platform.ToString(),
    ["os_version"]     = Environment.OSVersion.VersionString,
    ["clr_version"]    = Environment.Version.ToString(),
    ["framework"]      = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ["machine_name"]   = Environment.MachineName,
    ["user_name"]      = Environment.UserName,
    ["process_id"]     = Process.GetCurrentProcess().Id,
    ["working_set_mb"] = Environment.WorkingSet / 1024 / 1024
};
DmTrace.Event("info", "mcp.startup", startupData);

var envData = new Dictionary<string, object>
{
    ["env"] = RedactEnv()
};
DmTrace.Event("debug", "mcp.startup.env", envData);

// ── Host setup ────────────────────────────────────────────────────────────────

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    DmTrace.Shutdown(0);
};

var builder = Host.CreateApplicationBuilder(args);

// Clear all logging providers so the framework never writes to stdout.
// The stdio MCP transport owns stdout; any write from a logger would corrupt
// the JSON-RPC framing and break the client connection.
// Add stderr-only logging in Phase B if debug tracing becomes necessary.
builder.Logging.ClearProviders();

// McpServices: session-scoped DI container (one per stdio connection).
builder.Services.AddSingleton(new McpServices(workingDirectory));

// MCP server: stdio transport + attribute-based tool discovery.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DevMindTools>();

await builder.Build().RunAsync();

// ── Helpers ───────────────────────────────────────────────────────────────────

static IDictionary<string, object> RedactEnv()
{
    var patterns = new[]
    {
        "_KEY", "_TOKEN", "_SECRET", "_PASSWORD",
        "ANTHROPIC_", "OPENAI_", "OPENROUTER_"
    };

    var result = new Dictionary<string, object>();
    var raw = Environment.GetEnvironmentVariables();
    foreach (System.Collections.DictionaryEntry entry in raw)
    {
        string key = entry.Key?.ToString() ?? "";
        string val = entry.Value?.ToString() ?? "";

        bool redact = false;
        foreach (var pat in patterns)
        {
            // ANTHROPIC_, OPENAI_, OPENROUTER_ are prefix matches.
            // _KEY, _TOKEN, _SECRET, _PASSWORD are suffix matches.
            // Case-insensitive throughout.
            if (pat.EndsWith("_"))
            {
                if (key.StartsWith(pat, StringComparison.OrdinalIgnoreCase))
                { redact = true; break; }
            }
            else
            {
                if (key.EndsWith(pat, StringComparison.OrdinalIgnoreCase))
                { redact = true; break; }
            }
        }

        result[key] = redact ? "[REDACTED]" : val;
    }
    return result;
}
