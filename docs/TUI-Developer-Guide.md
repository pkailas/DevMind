# DevMind TUI — Developer's Guide

This guide is for developers working **on** DevMind itself. It covers the
solution layout, the engine architecture, and the conventions that keep the
codebase healthy.

- **Product**: DevMind
- **Brand**: iOnline Consulting LLC
- **Runtime**: .NET 10 (`net10.0`)
- **Language**: C#
- **Default branch**: `master`

---

## Design in one sentence

**Decoupled engine + swappable UI skins.** The durable engine lives in
`DevMind.Core` and is UI-agnostic; each user interface is a thin, replaceable
skin over it.

## Repository layout

| Project | Purpose |
|---|---|
| `DevMind.Core` | **The engine.** UI-agnostic. `LlmClient`, `LoopDriver`, `AgenticExecutor`, `ShellRunner`, `PatchEngine`, `MemoryManager`, `IHistoryStore`, `LanguageServerRouter`, `TuiConfig`. Boundary interfaces: `IAgenticHost` (side effects) and `ILoopCallbacks` (UI-state hooks). |
| `DevMind.TUI` | **Terminal.Gui v2 UI skin.** The active development target. `TuiAgenticHost` implements `IAgenticHost`; slash commands live in `SlashCommand.cs`; global config at `%APPDATA%\devmind\devmind.json`. |
| `DevMind.Cli` | **Console UI skin.** Reference implementation / fallback. Read-only `devmind.json` in the working directory. |
| `DevMind.McpServer` | **MCP server** exposing the tool set to MCP clients. References `DevMind.Core` one-way. See the *DevMind MCP Server — Developer's Guide*. |
| `DevMind.Core.Tests` | Unit tests. |
| `_archive/` | Retired projects (old VSIX, diagnostic harnesses). Do not modify. |

> Historical note: DevMind began life as a Visual Studio VSIX extension
> (WPF tool window, .NET Framework 4.8). That generation is retired and lives
> in `_archive/`. Old docs describing the VSIX (`DEVMIND_DEV.md` v4.6.x,
> the original `DevMind_UserGuide.md`) describe that era, not the current code.

## Build & run

```bash
dotnet build                                                # whole solution

dotnet run --project DevMind.TUI -- --dir <repo> --endpoint <llm-endpoint>
dotnet run --project DevMind.Cli -- --dir <repo> --endpoint <llm-endpoint>
```

Deployment is `run-deploy.ps1`: self-contained single-file publish of the TUI
into `dist\` (what the `devmind`/`dm` launchers on PATH run) and of the MCP
server into `dist\mcp\`. Version is git-driven via `Directory.Build.props`
(`1.0.<git-commit-count>`) — **commit first, then deploy** so the stamped
version matches.

---

## The agentic loop

```
User input → LlmClient.SendMessageAsync() → SSE streaming → onComplete
  → LoopDriver.ProcessIterationAsync()
      → ResponseParser.Parse() → typed blocks (FILE, PATCH, SHELL, READ, …)
      → AgenticExecutor.ExecuteAsync() → IAgenticHost side effects
      → LoopHelpers.InjectToolResultMessages()
      → depth/error/DONE checks
      → returns LoopIterationResult { Kind, ShouldReTrigger, … }
  → ShouldReTrigger → re-send to LLM with tool results
  → Terminal → stop
```

Key classes in `DevMind.Core`:

| Class | Role |
|---|---|
| `LlmClient` | HTTP client with SSE streaming to an OpenAI-compatible `/v1/chat/completions`. Handles context detection, micro-compaction, brainwash. |
| `LoopDriver` | Drives one post-stream-complete agentic iteration. Owns `LoopState` mutations. |
| `AgenticExecutor` | Executes an `AgenticAction` via `IAgenticHost`. The only class with side effects in the pipeline. |
| `IAgenticHost` | Abstracts all file-system/shell/UI side effects from the pipeline. |
| `ShellRunner` | Platform-agnostic shell executor: working-directory state, streaming output, process-tree cancellation. |
| `PatchEngine` | File patching with whitespace-normalized matching, fuzzy fallback, UNDO stack. |
| `MemoryManager` | Persistent knowledge store (`MEMORY.md` + topic files in `.devmind/`). |
| `IHistoryStore` | Conversation history persistence (SqlServer/Sqlite/Null providers). |
| `LanguageServerRouter` | Routes LSP operations (find_symbol, go_to_definition, …) to the right language server. |
| `TuiConfig` | Global TUI config with atomic write-back. |

## LLM directives

The model communicates actions as text directives that `ResponseParser` turns
into typed blocks: `FILE`/`END_FILE`, `PATCH` with `FIND:`/`REPLACE:`, `SHELL:`,
`READ` (with optional line ranges), `GREP:`, `FIND:` (cross-file), `DELETE`,
`RENAME`, `DIFF`, `TEST`, `DONE`, and `SCRATCHPAD` for cross-turn state.

## TUI structure

`DevMind.TUI` is a Terminal.Gui v2 app:

| File | Role |
|---|---|
| `Program.cs` | Startup, option parsing (`TuiOptions.FromArgs`), host wiring |
| `TuiAgenticHost.cs` | `IAgenticHost` implementation for the TUI (+ `TuiAgenticHost.Debug.cs` for `/debug` via netcoredbg/DAP) |
| `TuiLoopCallbacks.cs` | `ILoopCallbacks` implementation (spinner, status updates) |
| `SlashCommand.cs` | Slash-command registry and handlers (`RegisterBuiltinCommands`) |
| `TuiInputBox.cs`, `TuiStatusBar.cs` | Input and status widgets |
| `CodeBlockStreamer.cs`, `SyntaxHighlighter.cs` | Streaming render of code blocks with highlighting |
| `TuiOptions.cs` | Options record: endpoint, model, depth cap, context strategy, timeouts |

### Adding a slash command

Register it in `SlashCommand.RegisterBuiltinCommands()` with a name,
description, usage string, and handler. Handlers receive `(string[] args,
CommandContext ctx)`; `CommandContext` exposes callbacks into the host
(`SetWorkingDirectory`, `SetDepthCap`, `ResetConversation`, `HistoryStore`,
`DebugCommand`, …) so handlers stay UI-independent. Long-running commands that
drive model turns (`/digest`, `/library`) are intercepted and executed by the
host input loop instead; their registry entries exist so `/help` lists them.

---

## Conventions (enforced by review)

- Keep the engine UI-agnostic — UI concerns belong in the skins
  (`DevMind.Cli`, `DevMind.TUI`), never in `DevMind.Core`.
- New UI work goes in `DevMind.TUI`; `DevMind.Cli` is the reference/fallback.
- Do not reintroduce VSIX/WPF/.NET Framework patterns.
- Use `LoggerMessage` for logging where applicable.
- `TreatWarningsAsErrors` is on for C# projects.
- Global TUI config uses atomic write: write to `.tmp` then
  `File.Move(..., overwrite: true)`. The overwrite flag is required — plain
  `File.Move` throws once the file exists, silently no-op'ing every save
  after the first.
- TUI: render the agentic turn **off the UI thread** (`Task.Run`); synchronous
  tool I/O on the UI thread freezes the spinner/redraws. All UI writes marshal
  via `app.Invoke`.
- TUI content searches (`FIND`/`GREP`) must skip cloud placeholders, binaries,
  and oversized files via `ContextEngine.ShouldSkipForContentSearch` — opening
  a OneDrive online-only file hydrates (downloads) it.

## Configuration model

Resolution order (highest wins):

1. Environment variables (`DEVMIND_ENDPOINT`, `DEVMIND_API_KEY`,
   `DEVMIND_SERVER_TYPE`, `DEVMIND_BUILD_COMMAND`, `DEVMIND_CONTEXT_STRATEGY`,
   `DEVMIND_HISTORY_*`, `DEVMIND_TUI_VERBOSE`, `DEVMIND_TUI_DIAG`)
2. `%APPDATA%\devmind\devmind.json` (global TUI config, atomic write-back)
3. `~/.devmind.env` (loaded at startup; applies only if the env var isn't set)
4. Hardcoded defaults in `TuiOptions`

---

## Testing

Unit tests live in `DevMind.Core.Tests`:

```bash
dotnet test
```

Core loop tests use a fake SSE server pattern — scripted multi-iteration
streams (tool call → tool result → final answer) asserting the loop terminates,
actions are recorded, and nothing writes to the console from engine code.

---

*DevMind is a product of iOnline Consulting LLC.*
