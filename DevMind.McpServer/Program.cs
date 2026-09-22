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
//   DevMind.McpServer [--root <working-directory>] [--dir <write-root>]...
//   --dir is repeatable. Without --root the first --dir is also the working directory,
//   which defaults to Environment.CurrentDirectory when no --dir is given.
//   --root sets the working directory WITHOUT granting write access to it.
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

// ── Parse --root / --dir arguments ───────────────────────────────
// ServerRootArgs owns the whole shape (see that file): --root sets the working
// directory without granting write, --dir contributes write roots, and with --root
// absent nothing about the old behaviour changes.

var rootArgs = ServerRootArgs.Parse(args, Environment.GetEnvironmentVariable("DEVMIND_ALLOWED_WRITE_ROOTS"));

if (rootArgs.Error != null)
{
    Console.Error.WriteLine(rootArgs.Error);
    DmTrace.Shutdown(2);
    Environment.Exit(2);
}

foreach (var message in rootArgs.Messages)
    Console.Error.WriteLine(message);

string workingDirectory = rootArgs.WorkingDirectory;
var additionalWriteRoots = rootArgs.AdditionalWriteRoots;
var allWriteRoots = rootArgs.EffectiveWriteRoots;

// ~/.devmind.env is the machine-local config convention (endpoints, DB connections,
// SSH hosts, opt-in gates like DEVMIND_ALLOW_ELEVATION / DEVMIND_DB_ALLOW_WRITE).
// The TUI and CLI have always loaded it; the MCP server previously did NOT, so its
// DEVMIND_* config silently depended on whatever environment the MCP client happened
// to launch it with. Load BEFORE McpServices/AgentJobManager read any env vars;
// existing process env vars still win (EnvFileLoader never overwrites).
DevMind.EnvFileLoader.Load();

// GUI MCP clients (Claude Desktop) launch this process with a minimal PATH — dotnet
// and dotnet-ef were unresolvable during live delegations. Enrich once; every
// ShellRunner child (run_shell/run_build/run_tests and headless agents) inherits it.
string? envChanges = DevMind.DevEnvironment.EnrichProcessEnvironment();
if (envChanges != null)
    Console.Error.WriteLine($"[McpServer] Environment enriched: {envChanges}");

// ── Trace startup ─────────────────────────────────────────────────────────────

// Trace.cs reads DEVMIND_TRACE_* env vars on first call; if tracing is
// disabled, these calls return cheaply.
var startupData = new Dictionary<string, object>
{
    ["argv"]           = args,
    ["cwd"]            = Environment.CurrentDirectory,
    ["working_dir"]    = workingDirectory,
    ["write_roots"]    = allWriteRoots,
    ["platform"]       = Environment.OSVersion.Platform.ToString(),
    ["os_version"]     = Environment.OSVersion.VersionString,
    ["clr_version"]    = Environment.Version.ToString(),
    ["framework"]      = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ["machine_name"]   = Environment.MachineName,
    ["user_name"]      = Environment.UserName,
    ["process_id"]     = Environment.ProcessId,
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
var mcpServices = new McpServices(workingDirectory, additionalWriteRoots,
    rootArgs.WorkingDirectoryIsWriteRoot);
builder.Services.AddSingleton(mcpServices);

// AgentJobManager: the devmind_task_* headless-agent job queue (one job at a time,
// own worker — deliberately NOT routed through McpServices' tool dispatcher, so
// status/cancel stay responsive while a task runs for minutes). A finished job may
// have rewritten anything, so it drops the session file cache — read_file/grep_file
// must never serve pre-task content (live false-negative reports).
builder.Services.AddSingleton(new AgentJobManager(
    onJobFinished: () => mcpServices.FileCache.InvalidateAll()));

// Reclaim PATCH backups orphaned by a previous run. A host deletes its own backups
// when its turn ends, but a force-killed process never reaches that, and nothing else
// owns the files afterwards — they accumulate in %TEMP%\DevMind without bound. Skipped
// entirely while another agent is mid-job, since it may own live backups in that same
// flat folder. Fire-and-forget and never throws: startup must not wait on it or fail
// because of it.
_ = System.Threading.Tasks.Task.Run(() =>
{
    try
    {
        if (AgentJobManager.IsJobActiveElsewhere()) return;
        DevMind.PatchBackupSweeper.CleanupStale(DevMind.PatchBackupSweeper.DefaultMaxAge);
    }
    catch { /* best-effort cleanup — must never affect startup */ }
});

// MCP server: stdio transport + attribute-based tool discovery.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DevMindTools>()
    .WithTools<AgentTaskTools>();

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
