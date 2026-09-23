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
| `DevMind.Core.Tests` | xUnit v2 tests for the engine. Sees `internal`s via `InternalsVisibleTo`. Serial (`xunit.runner.json`). |
| `DevMind.Cli.Tests` | xUnit v2 tests for the CLI skin. Serial — mutates process environment variables. |
| `DevMind.McpServer.Tests` | xUnit v2 tests for the MCP server and the `devmind_task_*` job queue. |
| `DevMind.TUI.Tests` | xUnit **v3** (VSTest-only) tests for the TUI skin. See the csproj comments before touching its package set. |
| `_archive/` | Retired projects (old VSIX, diagnostic harnesses). Do not modify. |

---

## Build & Run

```bash
# Build the entire solution (TreatWarningsAsErrors is on — 0 warnings is the bar)
dotnet build DevMind.slnx

# Clean, non-incremental build — a stale DLL is the usual cause of a false pass
dotnet build DevMind.slnx -t:Rebuild

# Run the whole test suite
dotnet test DevMind.slnx

# Run the TUI
dotnet run --project DevMind.TUI -- --dir <repo> --endpoint <llm-endpoint>

# Reopen a past session under its own id (needs DEVMIND_HISTORY_ENABLED + a provider).
# --continue / -c takes the most recent session on this machine; /history lists the ids.
dotnet run --project DevMind.TUI -- --resume <session-id>
dotnet run --project DevMind.TUI -- --continue

# Run the CLI
dotnet run --project DevMind.Cli -- --dir <repo> --endpoint <llm-endpoint>
```

### Environment Variables

Selected, not exhaustive — the full set is whatever `Environment.GetEnvironmentVariable`
is called with (`DEVMIND_LSP_*`, `DEVMIND_TRACE_*`, `DEVMIND_GLOBAL_DIR`, `DEVMIND_TASKS_DIR`,
`DEVMIND_ALLOWED_WRITE_ROOTS`, `DEVMIND_SHELL_TIMEOUT`, and others).

| Variable | Purpose |
|----------|---------|
| `DEVMIND_ENDPOINT` | LLM endpoint URL (overrides default) |
| `DEVMIND_API_KEY` | API key (overrides default) |
| `DEVMIND_SERVER_TYPE` | Force backend type (`vllm`/`llama`/`lmstudio`/`custom`); overrides startup auto-detection |
| `DEVMIND_HISTORY_*` | History store configuration (SqlServer/Sqlite/Null) |
| `DEVMIND_BUILD_COMMAND` | Explicit build command for `run_build` (overrides auto-detection) |
| `DEVMIND_CONTEXT_STRATEGY` | Force context-management policy (`transformer`/`hybrid`/`auto`); auto (default) picks by measured prompt-cache reuse, seeded by a model-name hint (Qwen3.5/3.6, Mamba, etc.) |
| `DEVMIND_TUI_VERBOSE` | Show the full firehose in the TUI transcript (disables the quiet filter that hides `[CONTEXT]`/`[TOOL_USE]`/`[LLM]`/`[AGENTIC] Iteration` churn) |
| `DEVMIND_TUI_DIAG` | Path to a trace file for the TUI color-stamping/append pipeline (inert when unset) |

### Config Resolution Order

1. `$env:` variables (highest priority)
2. `%APPDATA%\devmind\devmind.json` (global TUI config, atomic write-back)
3. `~/.devmind.env` (loaded at startup, applies only if env var not already set)
4. Hardcoded defaults

The system prompt resolves on its own chain, not the one above: `prompts/system-prompt.md`
in the repo is the source of truth (reviewed, diffed and committed like code),
`%APPDATA%\devmind\system-prompt.md` is the runtime copy every skin actually reads and
hot-reloads, and `deploy.ps1` reconciles the two byte-for-byte before publishing --
installing the repo copy when the live one is absent, and refusing to deploy when the live
one has edits the repo does not. Pass `-OverwriteLivePrompt` to discard those live edits
instead, which keeps them as `system-prompt.md.bak-<timestamp>`.

---

## Core Architecture

### The Agentic Loop

```
User input → LlmClient.SendMessageAsync() → SSE streaming → onComplete
  → LoopDriver.ProcessIterationAsync()
      → ToolCallMapper.Map(LastToolCalls) → typed ResponseBlocks → ResponseOutcome
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
| `PatchEngine` | File patching with whitespace-normalized matching, fuzzy fallback, and a pre-write backup copy under `%TEMP%\DevMind`. The hosts keep those backups on a bounded stack (`PatchBackupCount`, drained per session/job) — it is an internal safety net and a metric; **nothing restores from it and there is no undo command**. |
| `MemoryManager` | Persistent knowledge store (`MEMORY.md` + topic files in `.devmind/` directory). |
| `IHistoryStore` | Conversation history persistence (SqlServer/Sqlite/Null providers). |
| `LanguageServerRouter` | Routes LSP operations (find_symbol, go_to_definition, etc.) to the appropriate language server. |
| `ToolCallMapper` | Maps the model's native `tool_calls` onto typed `ResponseBlock`s — the single entry point to the action pipeline. |
| `ToolRegistry` | The tool catalogue advertised to the model. `ToolCatalogueRegistryParityTests` derives its expected set from here structurally. |
| `BufferedAgenticHost` | Non-interactive `IAgenticHost` (headless jobs, MCP delegation): buffers output to a sink instead of a UI. |
| `HeadlessAgent` / `HeadlessSession` | The no-human-in-the-loop agent behind `devmind_task_*`. |
| `SystemPromptFile` | Loads the global system prompt from `%APPDATA%\devmind\system-prompt.md`, re-read on every assembly (hot-reload). |
| `DevMindPaths` | Single source of truth for `%APPDATA%\devmind`. `DEVMIND_GLOBAL_DIR` overrides it (test seam). |
| `TuiConfig` | Global TUI config at `%APPDATA%\devmind\devmind.json`. Atomic write-back (`File.Move(..., overwrite: true)`). Fields include `BehavioralRules`, `WorkingDirectory`, `DepthCap`, `ContextLimitPercent`, `TrainingLogEnabled`/`TrainingLogFolder`, `SqlConnections`, the `Library*` RAG settings, `AutoAttachImages`, `AllowedWriteRoots`. |

### Model Actions (native tool calls)

The model drives the loop through OpenAI-style `tool_calls`, not through text directives in
its prose. `ToolCallMapper` maps each call onto a typed `ResponseBlock`; the catalogue itself
lives in `ToolRegistry`. The current set:

- **Files** — `read_file`, `create_file`, `append_file`, `patch_file`, `delete_file`, `rename_file`, `diff_file`, `list_files`
- **Search** — `grep_file`, `find_in_files`
- **Execution** — `run_shell`, `run_build`, `run_tests`, `debug`
- **LSP** — `get_diagnostics`, `go_to_definition`, `find_references`, `hover`, `find_symbol`
- **Memory** — `recall_memory`, `save_memory`, `list_memory_topics`, `search_memory`
- **Cache / library** — `recall_cache`, `list_cache`, `query_library`
- **Web / learn** — `web_search`, `web_fetch`, `learn_search`, `learn_fetch`, `learn_code_search`
- **Data** — `run_sql`
- **Control** — `scratchpad` (cross-turn state, injected into the system prompt so it survives
  compaction), `ask_caller` (pause and ask), `task_done` (explicit completion — stops the loop)

---

## TUI Slash Commands

Derived from the `RegisterCommand(...)` calls in `DevMind.TUI/SlashCommand.cs`; each
description is that command’s registered help text, so this table and `/help` say the
same thing. Grouping follows `HelpGroups`.

| Command | Description |
|---------|-------------|
| **Session** | |
| `/new` | Start a new session (clears conversation, resets state) |
| `/restart` | Restart session (alias for /new) |
| `/clear` | Clear screen and reset conversation |
| `/cls` | Clear the screen only — keeps conversation, context, and session (UI reset) |
| `/compact` | Force a context compaction pass now |
| `/history` | List past sessions from history |
| `/resume <n>` | Resume a past session by number (from /history listing) |
| `/title <text>` | Set the current session's title |
| `/steer <text>` | Fold a suggestion into the RUNNING turn at its next iteration boundary (plain text does the same) |
| `/override <text>` | Redirect the RUNNING turn at its next iteration boundary - stop the current approach and change course |
| `/mode [auto\|manual]` | Show or set the approval mode: auto applies mutations, manual asks first (Shift+Tab toggles) |
| `/quit` | Quit DevMind |
| `/exit` | Quit DevMind (alias for /quit) |
| **Model** | |
| `/think on\|off` | Toggle session thinking mode (reasoning display) on/off |
| `/t <message>` | One-shot: send a message with thinking ON (does not change the /think default) |
| `/reasoning on\|off` | Toggle reasoning display (alias for /think) |
| `/rules [text\|clear]` | Show, set, or clear behavioral rules |
| `/system_prompt` | Display the assembled system prompt |
| `/prompt` | Show the global system-prompt file path, existence, and assembled prompt size |
| **Context** | |
| `/depth-cap [N]` | Show or set agentic depth cap (1-200) |
| `/context-limit [1-99\|off]` | Show or set the context-window % at which the loop pauses to ask |
| `/cache` | Show nearline cache stats (in-memory + disk tiers, session counters) |
| `/output-lines [N]` | Show or set the transcript line cap for tool output (0 = uncapped) |
| `/expand [thought\|output]` | Show what the transcript hid: the last collapsed thought or capped tool output |
| **Workspace** | |
| `/dir [path\|-b]` | Change working directory |
| `/lsp on\|off` | Show or enable/disable language server tools |
| `/resolve accept_proposed\|accept_current\|cancel` | Resolve a pending merge conflict (accept proposed/current, or cancel) |
| `/debug launch <proj> \| attach <pid\|name> \| break [clear] <file> <line> \| continue \| step \| stepin \| stepout \| inspect <var> \| stack \| eval <expr> \| detach \| stop` | Debug via netcoredbg (launch/attach, breakpoints, stepping, inspect/eval) |
| **Documents** | |
| `/image <path> [page\|first-last\|all\|p=N]` | Attach an image or rasterized PDF pages to your next message (vision model + mmproj); p=N chunks N pages at a time |
| `/digest <path-to-pdf> [p=N]` | Chunk-summarize an entire PDF on a side conversation, then inject the digest into this session |
| `/library [add <pdf\|md\|txt\|docx> [p=N] \| replace <pdf> r=n-n [p=N] \| list \| remove <id> \| <question>]` | Ask or manage the RAG document library (SQL 2025 vector store): add, replace, list, remove, or question |
| **Training** | |
| `/training-log [on\|off\|folder <path>]` | Show training-log status (enabled/folder/last write), or toggle it |
| `/training-delete-last` | Delete the training log for the current session |
| **Help** | |
| `/help` | Show this list |

---

## Conventions

- Keep the engine UI-agnostic — UI concerns belong in the skins (`DevMind.Cli`, `DevMind.TUI`), never in `DevMind.Core`.
- New UI work goes in `DevMind.TUI`; `DevMind.Cli` is the reference/fallback skin.
- Do not reintroduce VSIX/WPF/.NET Framework patterns.
- Diagnostics: `DevMindLog.Write` for anything worth reading after the fact — any failure a
  `catch` would otherwise swallow, and the once-per-`Configure()` resolved state that makes such
  a failure readable. It appends to `%APPDATA%\devmind\logs\devmind-<date>-pid<n>.log`, never
  throws, and is bounded by size, age and file count (`DEVMIND_LOG=off` disables it).
  `Debug.WriteLine` is for per-turn / per-request tracing ONLY, marked `[DevMind TRACE]`: it
  carries `[Conditional("DEBUG")]`, so the compiler deletes the call and its string literals
  from the `-c Release` build `run-deploy.ps1` actually publishes. `Console.Error` where a skin
  already uses it. There is no `ILogger` pipeline and no `LoggerMessage` — the MCP stdio
  transport owns stdout (`builder.Logging.ClearProviders()`), so nothing may write there.
- `TreatWarningsAsErrors` is on for all solution projects (set in `Directory.Build.props`, which excludes `_archive` builds); NuGet-audit codes `NU1901`–`NU1904` stay warnings via `WarningsNotAsErrors` so a new dependency advisory cannot red-line the build.
- Global TUI config uses atomic write (write to `.tmp` then `File.Move(..., overwrite: true)` — the overwrite flag is required; plain `File.Move` throws once the file exists, silently no-op'ing every save after the first).
- TUI: render the agentic turn **off the UI thread** (`Task.Run`); synchronous tool I/O on the UI thread freezes the spinner/redraws. All UI writes marshal via `app.Invoke`.
- TUI content searches (`FIND`/`GREP`) must skip cloud placeholders, binaries, and oversized files via `ContextEngine.ShouldSkipForContentSearch` — opening a OneDrive online-only file hydrates (downloads) it.
