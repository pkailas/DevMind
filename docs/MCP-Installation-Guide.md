# DevMind MCP Server — Installation Guide

`DevMind.McpServer` exposes DevMind's tool set — file operations, search,
shell, build/test, memory, LSP navigation, and whole-task delegation to a local
GPU model — to any MCP client over stdio. Register it once and clients like
Claude Code (CLI) or the Claude desktop app can use your machine's DevMind
tools, including **headless**: Claude plans the work, DevMind's local model
executes it at zero API token cost.

---

## Prerequisites

| Requirement | Notes |
|---|---|
| A completed DevMind install | The MCP server is built and deployed by the same `install.ps1` / `run-deploy.ps1` used for the TUI — see the *DevMind TUI — Installation Guide*. |
| A local model server | The task-delegation tools (`devmind_task_*`) drive a local OpenAI-compatible model server. `devmind_task_start` health-probes it and fails fast with a clear message if it's down. |
| An MCP client | Claude Code CLI, Claude desktop app, or any MCP-capable client. |

The server binary lands at:

```
<InstallDir>\dist\mcp\DevMind.McpServer.exe
```

(default `InstallDir` is `%USERPROFILE%\source\repos\DevMind`). It is published
into its own `mcp\` folder deliberately — the TUI's single-file publish would
otherwise clobber shared DLL names.

**Important:** the server speaks JSON-RPC on stdout. Never wrap it in scripts
that print to stdout, and don't try to run it interactively — it will just sit
there waiting for a client.

---

## Registering with Claude Code (CLI)

`install.ps1` does this automatically when the `claude` CLI is on PATH. To do
it manually:

```powershell
claude mcp add --scope user devmind -- "C:\Users\<you>\source\repos\DevMind\dist\mcp\DevMind.McpServer.exe"
```

`--scope user` registers it once for your whole account; the working directory
is passed per-call by the tools that need one, so no `--dir` is required at
registration.

Verify: start `claude`, run `/mcp` — `devmind` should be listed with its tools.

## Registering with the Claude desktop app (headless Cowork use)

To use DevMind from the Claude desktop app — including cloud Cowork sessions
that reach back to your machine through the app — add the server to the desktop
app's MCP configuration (`claude_desktop_config.json`, reachable via
Settings → Developer → Edit Config):

```json
{
  "mcpServers": {
    "devmind": {
      "command": "C:\\Users\\<you>\\source\\repos\\DevMind\\dist\\mcp\\DevMind.McpServer.exe"
    }
  }
}
```

Restart the desktop app afterwards. The devmind tools then appear in your
Claude sessions (proxied through the desktop app), and Claude can delegate
whole coding tasks to your local model with `devmind_task_start` while you're
working from anywhere the app is signed in.

> Note the double backslashes — JSON requires them in Windows paths.

---

## Updating

The server is redeployed every time you run `run-deploy.ps1` (or re-run
`install.ps1`). Registration doesn't need to be repeated — it points at the
exe path, which stays stable.

Note that MCP clients launch the server on demand and hold it while connected.
If a redeploy fails because the exe is locked, close the client (or its
session) and redeploy again.

---

## Verifying the install

From Claude Code or a Claude session with the server connected:

1. Ask for a trivial lookup: *"use devmind's `list_files` with glob `*.md` on
   `<some repo>`"* — you should get a file list back.
2. With your model server running, ask it to *"start a devmind task that adds a
   comment to X"* — `devmind_task_start` should return a `job_id`; if the model
   server is down you'll get the fail-fast health-probe error instead, which
   confirms the wiring works.

---

*DevMind is a product of iOnline Consulting LLC.*
