# CLAUDE.md — DevMind Developer Reference

## Project Overview

**DevMind** is a local, LLM-powered agentic coding assistant. It runs an agentic loop — read, search, patch, create, run shell/build — against a target codebase, driven by a local model (LM Studio, Ollama, or any OpenAI-compatible endpoint). Code never leaves your machine.

**Architecture: decoupled engine + swappable UI skins.** The durable engine lives in C# and is UI-agnostic; the user interface is a thin, replaceable skin over it.

- **Product**: DevMind
- **Brand**: iOnline Consulting LLC
- **Runtime**: .NET 10 (`net10.0`)
- **Language**: C#
- **Default branch**: `master`

---

## Repository Layout

| Project | Purpose |
|---------|---------|
| `DevMind.Core` | **The engine.** UI-agnostic. Contains `LlmClient`, `LoopDriver`, `AgenticExecutor`, `ShellRunner`, `PatchEngine`, `MemoryManager`, `IHistoryStore`, `LanguageServerRouter`, `TuiConfig`. Boundary interfaces: `IAgenticHost` (side effects) and `ILoopCallbacks` (UI-state hooks). |
| `DevMind.TUI` | **Terminal.Gui v2 UI skin.** Active development target. Uses `TuiAgenticHost` (implements `IAgenticHost`), slash commands (`/new`, `/think`, `/rules`, `/dir`, etc.), global config at `%APPDATA%\devmind\devmind.json`. |
| `DevMind.Cli` | **Console UI skin.** Reference implementation. Read-only `devmind.json` in working directory. |
| `DevMind.McpServer` | **MCP server** exposing the tool set. References `DevMind.Core` one-way. |
| `_archive/` | Retired projects (old VSIX, diagnostic harnesses). Do not modify. |

---

## Build & Run

```bash
# Build the entire solution
dotnet build

# Run the TUI
dotnet run --project DevMind.TUI -- --dir <repo> --endpoint <llm-endpoint>

# Run the CLI
dotnet run --project DevMind.Cli -- --dir <repo> --endpoint <llm-endpoint>
```

### Environment Variables

| Variable | Purpose |
|----------|---------|
| `DEVMIND_ENDPOINT` | LLM endpoint URL (overrides default) |
| `DEVMIND_API_KEY` | API key (overrides default) |
| `DEVMIND_HISTORY_*` | History store configuration (SqlServer/Sqlite/Null) |

### Config Resolution Order

1. `$env:` variables (highest priority)
2. `%APPDATA%\devmind\devmind.json` (global TUI config, atomic write-back)
3. `~/.devmind.env` (loaded at startup, applies only if env var not already set)
4. Hardcoded defaults

---

## Core Architecture

### The Agentic Loop

```
User input → LlmClient.SendMessageAsync() → SSE streaming → onComplete
  → LoopDriver.ProcessIterationAsync()
      → ResponseParser.Parse() → typed blocks (FILE, PATCH, SHELL, READ, etc.)
      → AgenticExecutor.ExecuteAsync() → IAgenticHost side effects
      → LoopHelpers.InjectToolResultMessages()
      → depth/error/DONE checks
      → returns LoopIterationResult { Kind, ShouldReTrigger, ... }
  → ShouldReTrigger → re-send to LLM with tool results
  → Terminal → stop
```

### Key Classes in DevMind.Core

| Class | Role |
|-------|------|
| `LlmClient` | HTTP client with SSE streaming to OpenAI-compatible `/v1/chat/completions`. Handles context detection, micro-compaction, brainwash. |
| `LoopDriver` | Drives one post-stream-complete agentic iteration. Owns `LoopState` mutations. |
| `AgenticExecutor` | Executes an `AgenticAction` via `IAgenticHost`. The only class with side effects in the pipeline. |
| `IAgenticHost` | Interface abstracting all VS/file-system/UI side effects from the pipeline. |
| `ShellRunner` | Platform-agnostic shell executor. `WorkingDirectory` state, streaming output, process-tree cancellation. |
| `PatchEngine` | File patching with whitespace-normalized matching, fuzzy fallback, UNDO stack. |
| `MemoryManager` | Persistent knowledge store (`MEMORY.md` + topic files in `.devmind/` directory). |
| `IHistoryStore` | Conversation history persistence (SqlServer/Sqlite/Null providers). |
| `LanguageServerRouter` | Routes LSP operations (find_symbol, go_to_definition, etc.) to the appropriate language server. |
| `TuiConfig` | Global TUI config at `%APPDATA%\devmind\devmind.json`. Atomic write-back. Fields: `BehavioralRules`, `WorkingDirectory`. |

### LLM Directives

The model communicates actions through directives in its response:

- **`FILE: filename` / `END_FILE`** — Create a new file
- **`PATCH filename` / `FIND:` / `REPLACE:` / `END_PATCH`** — Edit an existing file
- **`SHELL: command`** — Run a shell command
- **`READ filename`** — Request file context (supports line ranges: `READ file.cs:100-150`)
- **`GREP: "pattern" filename`** — Search a file for a pattern
- **`FIND: "pattern" *.cs`** — Cross-file search by glob
- **`DELETE filename`** — Remove a file
- **`RENAME old.cs new.cs`** — Rename/move a file
- **`DIFF filename`** — Show file changes this session
- **`TEST Project.csproj`** — Run tests
- **`DONE`** — Explicit task completion (stops agentic loop)
- **`SCRATCHPAD:` / `END_SCRATCHPAD`** — Model state tracking across turns

---

## TUI Slash Commands

| Command | Description |
|---------|-------------|
| `/new` | Start a new session (clears conversation) |
| `/clear` | Clear screen and reset conversation |
| `/think on\|off` | Toggle thinking (reasoning) display |
| `/t <message>` | One-shot: send with thinking ON for this turn only |
| `/rules [text\|clear]` | Show, set, or clear behavioral rules (persisted) |
| `/dir [path]` | Show or change working directory (persisted) |
| `/depth-cap [N]` | Show or set agentic depth cap (1-10) |
| `/system_prompt` | Display the assembled system prompt |
| `/history` | List past sessions from history |
| `/resume <n>` | Resume a past session |
| `/title <text>` | Set the current session's title |
| `/help` | Show command list |

---

## Conventions

- Keep the engine UI-agnostic — UI concerns belong in the skins (`DevMind.Cli`, `DevMind.TUI`), never in `DevMind.Core`.
- New UI work goes in `DevMind.TUI`; `DevMind.Cli` is the reference/fallback skin.
- Do not reintroduce VSIX/WPF/.NET Framework patterns.
- Use `LoggerMessage` for logging where applicable.
- `TreatWarningsAsErrors` is on for C# projects.
- Global TUI config uses atomic write (write to `.tmp` then `File.Move`).
