# DevMind TUI — Installation Guide

DevMind is a local, LLM-powered agentic coding assistant for Windows. It runs
entirely against a local model server (LM Studio, Ollama, llama-server, or any
OpenAI-compatible endpoint) — your code never leaves your machine.

This guide installs the **DevMind TUI**, the terminal user interface you run
day-to-day. The same installer also builds and registers the DevMind MCP server;
see the *DevMind MCP Server — Installation Guide* for that side of things.

---

## What you need before you start

| Requirement | Notes |
|---|---|
| Windows 10/11 or Windows Server | The installer and published binaries target `win-x64`. |
| Git for Windows | The installer clones/updates the repo. Get it from https://git-scm.com/download/win |
| .NET 10 SDK | The installer will install this via `winget` if it's missing. On Windows Server, winget is often absent — install the SDK manually from https://dotnet.microsoft.com/download/dotnet/10.0 |
| A local model server | Any OpenAI-compatible endpoint: LM Studio, Ollama, or llama-server. DevMind defaults to `http://127.0.0.1:1234/v1` (LM Studio's default port). Nothing in DevMind runs without a model behind it. |
| Access to the DevMind repository | The installer clones from the configured `RepoUrl` (default: `https://github.com/pkailas/DevMind.git`). You need read access to that repo. |

Optional:

| Requirement | Notes |
|---|---|
| netcoredbg | Debug adapter used by the `/debug` commands. The installer downloads it automatically unless you pass `-SkipDebugger`. |
| Claude Code CLI | If `claude` is on your PATH, the installer registers the DevMind MCP server with it automatically. |

---

## Quick install

Open PowerShell and run the bootstrap installer:

```powershell
.\install.ps1
```

By default this:

1. Verifies git and the .NET 10 SDK (installing the SDK via winget if needed).
2. Clones the repository to `%USERPROFILE%\source\repos\DevMind` (or updates it
   if it's already there).
3. Publishes self-contained, single-file builds into the repo's `dist\` folder
   by running `run-deploy.ps1` — this produces both the TUI (`devmind`) and the
   MCP server (`dist\mcp\DevMind.McpServer.exe`).
4. Creates the `dm` short alias next to `devmind`.
5. Adds `dist\` to your **user PATH**.
6. Registers the MCP server with Claude Code, if the `claude` CLI is found.
7. Installs the netcoredbg debug adapter to
   `%LOCALAPPDATA%\netcoredbg\netcoredbg\netcoredbg.exe`.

The installer is idempotent — safe to re-run at any time to update.

### Installer options

```powershell
.\install.ps1 -InstallDir C:\Projects\DevMind   # custom location
.\install.ps1 -SkipDebugger                     # skip netcoredbg
.\install.ps1 -Branch master                    # branch to install (default: master)
```

After installation, **open a new console** (the PATH change doesn't apply to
already-open windows) and run:

```powershell
devmind        # or 'dm' for short
```

---

## Setting up the model server

DevMind talks to an OpenAI-compatible `/v1/chat/completions` endpoint. Point it
at whichever server you run:

```powershell
devmind --dir C:\path\to\your\repo --endpoint http://127.0.0.1:1234/v1
```

If you omit `--endpoint`, DevMind uses `http://127.0.0.1:1234/v1`.

You can also set defaults with environment variables so you don't repeat them:

| Variable | Purpose |
|---|---|
| `DEVMIND_ENDPOINT` | LLM endpoint URL |
| `DEVMIND_API_KEY` | API key, if your endpoint requires one (local servers usually don't) |
| `DEVMIND_SERVER_TYPE` | Force backend type: `vllm`, `llama`, `lmstudio`, or `custom` (otherwise auto-detected at startup) |

Settings resolve in this order (highest wins): environment variables →
`%APPDATA%\devmind\devmind.json` (the TUI's global config) → `~/.devmind.env` →
built-in defaults.

---

## Installing the debugger (manual path)

If the automatic netcoredbg install failed or you skipped it:

1. Download the latest `netcoredbg-win64.zip` from
   https://github.com/Samsung/netcoredbg/releases
2. Extract it so the executable lives at
   `%LOCALAPPDATA%\netcoredbg\netcoredbg\netcoredbg.exe` (the default location
   DevMind probes), **or** extract anywhere and set `DEVMIND_DAP_PATH` to the
   full path of `netcoredbg.exe`.

DevMind resolves the adapter in this order: `DEVMIND_DAP_PATH` →
`%LOCALAPPDATA%\netcoredbg\...` → `PATH`. The debugger is optional — everything
except `/debug` works without it.

---

## Updating

Re-run the installer, or from the repo directory:

```powershell
git pull
.\run-deploy.ps1
```

The published version number is git-driven (`1.0.<commit-count>`), so commit
first if you want your local changes reflected in the stamped version.

---

## Verifying the install

1. Open a **new** console.
2. `devmind --dir <some-repo>` — the TUI should start and show its banner.
3. Type `/help` — you should see the slash-command list.
4. Ask it something small ("read README.md and summarize it"). If the model
   server isn't running you'll get a connection error — start LM Studio /
   Ollama / llama-server and try again.

---

*DevMind is a product of iOnline Consulting LLC.*
