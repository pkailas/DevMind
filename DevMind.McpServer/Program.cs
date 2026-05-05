// File: Program.cs  v1.1
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

using DevMind.McpServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;

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
            Environment.Exit(2);
        }

        workingDirectory = rawValue;
        break;
    }
}

Console.Error.WriteLine($"[McpServer] Starting. Working directory: {workingDirectory}");

// ── Host setup ────────────────────────────────────────────────────────────────

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
