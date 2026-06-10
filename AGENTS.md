# AGENTS.md

> Agent context for the **DevMind** repository. This file explains what the
> project is, how it is structured, and how to build it. Tool-use rules and the
> task-completion contract are supplied separately in the system prompt — this
> file is **project context only** and should not repeat them.

## What DevMind is

DevMind is a local, LLM-powered agentic coding assistant. It runs an agentic
loop — read, search, patch, create, run shell/build — against a target codebase,
driven by a local model.

**Architecture: decoupled engine + swappable UI skins.** The durable engine
lives in C# and is UI-agnostic; the user interface is a thin, replaceable skin
over it. When a UI framework hits a wall, the skin is swapped — the engine does
not move.

> DevMind is **not** a Visual Studio VSIX extension. That was an earlier, retired
> architecture (now under `_archive/`). Disregard any VSIX, WPF, .NET Framework,
> or `msbuild DevMind.csproj` references you encounter — they are obsolete.

## Stack

- **Language / runtime:** C# on **.NET 10** (`net10.0`)
- **Persistence:** SQL Server for conversation history, via `IHistoryStore`
  (SqlServer / Sqlite / Null providers)
- **Default branch:** `master`

## Repository layout

- **`DevMind.Core`** — the engine. UI-agnostic. Agentic loop (`LoopDriver`,
  `AgenticExecutor`), `LlmClient`, context management, `PatchEngine`, LSP router,
  `ShellRunner`, `MemoryManager`, `IHistoryStore`. Boundary interfaces:
  `IAgenticHost` (side effects) and `ILoopCallbacks` (UI-state hooks).
- **`DevMind.McpServer`** — the MCP server exposing the tool set. References
  `DevMind.Core` one-way.
- **`DevMind.Cli`** — console UI skin; reference implementation of the host
  interfaces (`ConsoleAgenticHost`).
- **`DevMind.TUI`** — Terminal.Gui v2 UI skin. **Active development target.**
- **`_archive/`** — retired projects (old VSIX, diagnostic harnesses). Do not
  modify and do not treat as current.

## Build & run

Build the solution:

```
dotnet build
```

Run a skin from the solution root:

```
dotnet run --project DevMind.TUI -- --dir <repo> --endpoint <llm-endpoint>
dotnet run --project DevMind.Cli -- --dir <repo> --endpoint <llm-endpoint>
```

After any code change, build to verify it still compiles before treating the
task as done.

## Conventions

- Keep the engine UI-agnostic — UI concerns belong in the skins
  (`DevMind.Cli`, `DevMind.TUI`), never in `DevMind.Core`.
- New UI work goes in `DevMind.TUI`; `DevMind.Cli` is the reference / fallback skin.
- Do not reintroduce VSIX / WPF / .NET Framework patterns.
