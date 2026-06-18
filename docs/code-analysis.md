# DevMind Code Analysis

## 1. Overview

DevMind is a local, LLM-powered agentic coding assistant written in C# on .NET 10. It runs an agentic loop — read, search, patch, create, run shell/build — against a target codebase, driven by a local LLM model.

**Architecture**: Decoupled engine + swappable UI skins. The core engine is UI-agnostic; the user interface is a thin, replaceable skin over it.

---

## 2. Project Structure

| Project | Type | Purpose |
|---------|------|---------|
| **DevMind.Core** | Class library | UI-agnostic engine — agentic loop, LLM client, patch engine, LSP router, shell runner, memory, history stores |
| **DevMind.McpServer** | Class library | MCP server exposing the tool set; references Core one-way |
| **DevMind.Cli** | Console app | Console UI skin — reference implementation of host interfaces |
| **DevMind.TUI** | Console app | Terminal.Gui v2 UI skin — active development target |
| **DevMind.Core.Tests** | Test project | Unit tests for PatchEngine |
| **_archive/** | — | Retired projects (old VSIX, diagnostic harnesses) — do not modify |

---

## 3. Core Architecture

### 3.1 Boundary Interfaces

Two interfaces decouple the engine from UI/concerns:

- **`IAgenticHost`** — abstracts all side effects: file I/O, shell commands, LSP queries, memory, web tools, diff preview. Implemented by each skin (`ConsoleAgenticHost`, `TuiAgenticHost`).
- **`ILoopCallbacks`** — loop-lifecycle and UI-state callbacks: status bar, input box, thinking timer, context metrics. Distinct from IAgenticHost — single-responsibility split.

### 3.2 The Agentic Loop

```
User Input → LlmClient → LLM Response → LoopDriver → AgenticExecutor → IAgenticHost actions → (loop back to LLM) → ILoopCallbacks → UI updates
```

1. **LlmClient** — HTTP client for OpenAI-compatible `/v1/chat/completions` endpoints. Handles streaming, context budget management, compaction (EvictStaleContext, MicroCompactToolResults, BrainwashContext), and file content caching.
2. **LoopDriver** — drives one post-stream-complete iteration of the agentic pipeline. Parses LLM responses, matches narration claims (e.g. "build succeeded", "0 errors"), and produces a `LoopIterationResult`.
3. **AgenticExecutor** — executes prescribed actions by calling `IAgenticHost` methods. Processes blocks: PATCH, SHELL, FILE, SCRATCHPAD, MEMORY, TASK_DONE, DIFF, LSP tools, web tools.
4. **LoopState** — carries mutable loop-control fields: agentic depth, shell loop pending, consecutive error tracking, narration retry flag.

### 3.3 Context Budget

`ContextBudget` allocates the LLM context window into named buckets:
- **SystemPromptLimit** (10%) — system prompt
- **ProtectedTurnsLimit** (10%) — protected conversation turns
- **WorkingHistoryLimit** (70%) — working conversation history
- **ResponseHeadroomLimit** (10%) — reserved for LLM response generation

Compaction strategies when budget is over:
1. **MicroCompactToolResults** — trims stale tool result content in-place using watermark strategy
2. **EvictStaleContext** — drops stale messages based on age relative to current turn
3. **BrainwashContext** — nuclear option: replaces entire conversation with synthetic minimal conversation

---

## 4. Key Components

### 4.1 PatchEngine (542 lines)

Pure static patch logic — no VS SDK, no WPF, no DTE. Handles:

- **Fuzzy matching**: sliding window over file content scored by similarity ratio
- **Find/Replace parsing**: extracts `FIND:`/`REPLACE:` pairs from PATCH blocks
- **Whitespace normalization**: collapses all whitespace runs with mapping back to original indices
- **Encoding preservation**: reads files detecting and preserving BOM/encoding; strips BOM for script files (JS/TS/Python)
- **Confidence scoring**: returns `PatchConfidence` (High, Medium, Low) based on match quality
- **Backup system**: creates timestamped backups before applying patches, supporting undo

### 4.2 ToolRegistry (333 lines)

Builds the OpenAI-compatible tools JSON array for structured tool calling. Defines 22 tools:

**File Operations**: read_file, create_file, patch_file, append_file, delete_file, rename_file, diff_file, list_files, grep_file, find_in_files

**Code Intelligence**: get_diagnostics, go_to_definition, find_references, hover, find_symbol

**Execution**: run_shell, run_build, run_tests

**State**: scratchpad

**Memory**: recall_memory, save_memory, list_memory_topics

**Web**: web_search, web_fetch

### 4.3 LlmClient (3329 lines — largest file)

HTTP client with sophisticated context management:

- **Endpoint detection**: auto-detects context size from llama-server, LM Studio, or custom endpoints
- **Streaming**: sends messages via SSE stream with token-level callbacks
- **Context caching**: `FileContentCache` stores file reads; `[READ:filename]` blocks are compacted to placeholders after first use
- **Compaction**: watermark-based micro-compaction of tool results; full evication by staleness; nuclear "brainwash" with thrashing detection
- **Shell result summarization**: classifies `[SHELL-RESULT:]` blocks and builds compact summaries

### 4.4 ShellRunner (480 lines)

Platform-agnostic shell executor:
- Auto-detects PowerShell vs cmd.exe
- Handles .cmd shim commands (npm, dotnet, etc.) specially
- Working directory tracking
- Timeout resolution from `DEVMIND_SHELL_TIMEOUT` env var or explicit parameter
- Directive parsing for tool output

### 4.5 LanguageServerRouter + LspToolService

Semantic code intelligence via LSP:
- **LanguageServerHost** — spawns and manages language server processes (csharp-ls, typescript-language-server)
- **LanguageServerRouter** — routes requests to the correct server by file extension
- **LspToolService** — exposes getDiagnostics, goToDefinition, findReferences, hover, findSymbol

### 4.6 MemoryManager (250 lines)

Cross-session knowledge persistence via a file-based system:
- `MEMORY.md` index file in the project root
- Individual `.md` topic files for each saved topic
- Slug sanitization, upsert semantics

### 4.7 IHistoryStore

Conversation history persistence with three providers:
- **NullHistoryStore** — in-memory only (default for CLI/TUI)
- **SqliteHistoryStore** — lightweight SQLite persistence
- **SqlServerHistoryStore** — SQL Server for production-scale scenarios

### 4.8 ContextEngine (301 lines)

Utility for file enumeration and rendering:
- Noise path filtering (bin/, obj/, .git/, node_modules/, etc.)
- Glob pattern enumeration with recursive descent
- Language hint detection from file extensions
- YAML frontmatter stripping
- C# outline extraction (classes, methods, interfaces, enums, records)
- Read block rendering with outline-first behavior for large files

---

## 5. UI Skin Pattern

Both skins follow the same pattern:

```
IAgenticHost     → ConsoleAgenticHost / TuiAgenticHost    (side effects)
ILoopCallbacks   → ConsoleLoopCallbacks / TuiLoopCallbacks (UI state)
```

### 5.1 ConsoleAgenticHost (1151 lines)

Reference implementation with ANSI color output, Git fallback for deleted files, and file write guards (confirms writes to files not known to the current task).

### 5.2 TuiAgenticHost (1321 lines)

Terminal.Gui v2 implementation with:
- Color-span based syntax highlighting via `IVisualLineTransformer`
- Diff preview cards in the TUI
- Slash command system (`SlashCommand.cs`)
- Status bar integration

### 5.3 Slash Commands (TUI)

TUI-specific commands: `/restart`, `/dir`, `/help`, `/compact`, `/brainwash`, `/memory`, `/files`, etc.

---

## 6. MCP Server

`DevMind.McpServer` exposes the tool set via the Model Context Protocol:
- **DevMindTools** — defines the MCP tool schema
- **McpServices** — MCP service registration and initialization

---

## 7. File System Conventions

### Write Guards

Both host implementations implement a "task read files" set:
- When a file is read during the current task, it's added to `_taskReadFiles`
- Before writing to a file NOT in this set, the host prompts for user confirmation
- This prevents accidental writes to files the LLM hasn't acknowledged

### Patch Backup System

Every patch application creates a timestamped backup file before modification. The backup stack supports undo via `GetPatchBackupCount()`.

### File Resolution

Relative paths are resolved against the working directory. The `FindFile` method searches by name across the solution when a file name is ambiguous.

---

## 8. Execution Flow

### 8.1 Action Types

`ActionType` enum defines the pipeline:
- **ApplyAndBuild** — apply patches, then run shell command
- **RunShell** — execute a standalone shell command
- **CreateFile** — save FILE: content to disk
- **Stop** — task complete or depth cap

### 8.2 Response Block Processing

AgenticExecutor processes blocks in order:
1. SCRATCHPAD → update state tracking
2. MEMORY → recall/save memory topics
3. FILE → create new files
4. PATCH → batch patch resolution with diff preview
5. SHELL → execute shell commands
6. LSP tools → getDiagnostics, goToDefinition, etc.
7. Web tools → webSearch, webFetch
8. TASK_DONE → terminate the agentic loop

### 8.3 Loop Control

- **MaxDepth** configurable, defaults enforced per turn
- **ShellLoopPending** flag allows iterative shell execution until success
- **ConsecutiveErrorCount** tracks repeated failures and triggers task_done
- **NarrationRetryUsed** prevents infinite retry loops — one forced retry per turn

---

## 9. Metrics & Observability

- **ContextBudget** tracks token usage per bucket in real-time
- **LoopState** tracks agentic depth, errors, retries
- **Trace.cs** provides structured logging
- **LlmClient** exposes LastPromptTokens, LastGeneratedTokens, LastPromptMs, LastGeneratedMs, LastContextUsed, LastContextDelta

---

## 10. Dependencies

- .NET 10 (net10.0) runtime
- **Terminal.Gui v2** — TUI skin
- **OpenAI-compatible API** — LLM communication (supports llama.cpp, LM Studio, Ollama, etc.)
- **csharp-ls** — C# language server (via npm)
- **typescript-language-server** — TS language server (via npm)
- **SQLite** / **SQL Server** — optional persistence via IHistoryStore
