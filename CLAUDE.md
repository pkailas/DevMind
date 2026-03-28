# CLAUDE.md — DevMind Developer Reference  v1.6

## Project Overview

**DevMind** is a Visual Studio extension (VSIX) that provides a local LLM coding assistant inside the VS IDE tool window. It targets developers who want Claude Code-style assistance using a privately hosted model (LM Studio, Ollama, or any OpenAI-compatible endpoint) without sending code to external servers.

- **Product**: DevMind
- **Brand**: iOnline Consulting LLC
- **Current Version**: v6.0.33
- **Platform**: Visual Studio 2022+ (VSIX), .NET Framework (VSSDK requirement)
- **Language**: C# with WPF UI

---

## Architecture

### Entry Points

| File | Purpose |
|------|---------|
| `DevMindPackage.cs` | Main VS package — registers tool window, options page, commands |
| `DevMindToolWindow.cs` | Tool window host — creates `LlmClient` and `DevMindToolWindowControl` on startup |
| `DevMindToolWindowControl.xaml/.cs` | WPF UI — single-stream output, input bar, system prompt panel, streaming, agentic pipeline entry point |
| `DevMindToolWindowControl.AgenticHost.cs` | `IAgenticHost` implementation — bridges pipeline to VS/UI side effects; calls `StripOuterCodeFence` on saved file content |
| `DevMindToolWindowControl.Context.cs` | Editor context, DevMind.md loading, READ command, file search, `BuildMessageWithContext` |
| `DevMindToolWindowControl.Patch.cs` | PATCH command — parsing, whitespace-normalized matching, fuzzy matching, UNDO, backup stack; `StripOuterCodeFence` applied to FIND/REPLACE text |
| `DevMindToolWindowControl.Shell.cs` | Shell execution — `RunShellCommand`, `RunShellCommandCaptureAsync`, `ParseShellDirectives` |
| `ResponseParser.cs` | Parses complete LLM responses into typed blocks (File, Patch, Shell, ReadRequest, Text) |
| `ResponseClassifier.cs` | Wraps `ResponseParser.Parse()` and returns `ResponseOutcome` — abstraction boundary |
| `ResponseOutcome.cs` | Wraps parsed blocks with pre-computed boolean flags (HasPatches, IsDone, IsReadOnly, etc.) |
| `AgenticActionResolver.cs` | Pure static: maps `ResponseOutcome` + `ExecutionResult` → `AgenticAction` (9 priority rules) |
| `AgenticExecutor.cs` | Executes an `AgenticAction` via `IAgenticHost`; the only class with side effects in the pipeline |
| `AgenticAction.cs` / `ActionType` | Decision output — describes the next step the agentic loop should take |
| `ExecutionResult.cs` | Captures what happened during execution (patches applied/failed, shell result, files created) |
| `IAgenticHost.cs` | Interface abstracting all VS/file-system/UI side effects from the pipeline |
| `OutputColor.cs` | Public enum for output panel color categories |
| `LlmClient.cs` | HTTP client — SSE streaming to OpenAI-compatible `/v1/chat/completions` |
| `DevMindOptionsPage.cs` | VS Tools > Options settings — EndpointUrl, ApiKey, ModelName, ServerType, CustomContextEndpoint, SystemPrompt, AgenticLoopMaxDepth, ShowLlmThinking |
| `FileContentCache.cs` | In-memory line-indexed file cache — powers `READ filename:start-end` line-range access |

### Data Flow

```
User types → InputTextBox
  Enter      → SendToLlm() → LlmClient.SendMessageAsync()
                → onToken: filter → accumulate into responseBuffer → display (suppressed during FILE: blocks)
                → onComplete: classify → decide → execute → continue-or-stop
  Ctrl+Enter → RunShellCommand() → powershell.exe / cmd.exe → stdout/stderr → AppendOutput()
```

### Agentic Pipeline (v6.0+)

The `onComplete` handler runs the classify → decide → execute pipeline:

```
fullResponse
  → ResponseClassifier.Classify()    → ResponseOutcome  (HasPatches, IsDone, IsReadOnly, …)
  → AgenticActionResolver.Resolve()  → AgenticAction    (ApplyAndBuild, LoadAndResubmit, Stop, …)
  → AgenticExecutor.ExecuteAsync()   → ExecutionResult  (PatchedPaths, ShellExitCode, Errors, …)
  → AgenticActionResolver.Resolve()  → next AgenticAction  (Continue or Stop)
  → re-trigger SendToLlm() or fall through to completion
```

`AgenticExecutor` calls `IAgenticHost` methods (implemented on `DevMindToolWindowControl.AgenticHost.cs`)
to perform side effects without knowing about VS, WPF, or file system details.

### Response Dispatcher (v5.0+)

The LLM response is classified AFTER it arrives, not before. All tokens stream into a single `responseBuffer`. After streaming completes, `ResponseParser.Parse()` splits the response into typed blocks which are executed in order:

```
ResponseBlock
├── TextBlock        — plain text (already displayed during streaming)
├── FileBlock        — FILE:/END_FILE content → IAgenticHost.SaveFileAsync()
├── PatchBlock       — PATCH directive → IAgenticHost.ApplyPatchAsync()
├── ShellBlock       — SHELL: directive → IAgenticHost.RunShellAsync()
├── ReadRequest      — model asking to READ a file → IAgenticHost.LoadFileContentAsync()
├── GrepBlock        — GREP: directive → IAgenticHost.GrepFileAsync()
├── FindBlock        — FIND: directive → IAgenticHost.FindInFilesAsync()
├── DeleteBlock      — DELETE directive → IAgenticHost.DeleteFileAsync()
├── RenameBlock      — RENAME directive → IAgenticHost.RenameFileAsync()
├── DiffBlock        — DIFF directive → IAgenticHost.GetFileDiffAsync()
├── Scratchpad       — SCRATCHPAD: block → IAgenticHost.UpdateScratchpad()
└── DoneBlock        — DONE directive → explicit task completion signal, stops agentic loop
```

A single LLM response can contain any combination of FILE:, PATCH, SHELL:, READ, and SCRATCHPAD directives. They execute in the order they appear.

### UI Layout

```
Row 0  Height="Auto"   — System prompt collapsible panel (ToggleButton + Border)
Row 1  Height="Auto"   — Input area (Border + TextBox, AcceptsReturn, MinHeight=60)
Row 2  Height="Auto"   — Toolbar (Ask | Run | Stop | Clear | ContextIndicator | StatusText)
Row 3  Height="*"      — OutputBox (RichTextBox, dark theme, Consolas, read-only)
```

### Output Rendering

All output is appended to a single `RichTextBox` (`OutputBox`) using `AppendOutput(text, OutputColor)`. There are no view models, DataTemplates, or bubble UI. Each call appends a `Run` with a color-coded `Foreground` to the last `Paragraph` in the `FlowDocument`.

```
OutputColor.Normal   — #CCCCCC  (LLM response text, shell stdout)
OutputColor.Dim      — #888888  (startup banner, status messages, [Stopped] notice)
OutputColor.Input    — #569CD6  (echoed user input lines prefixed with "> ")
OutputColor.Error    — #F44747  (shell stderr, LLM errors, build failures)
OutputColor.Success  — #4EC94E  (PATCH applied, file created, build succeeded)
OutputColor.Thinking — #6A6A8A  (LLM thinking tokens when ShowLlmThinking is enabled)
```

LLM streaming tokens are appended directly to a pre-allocated `Run` (`streamRun.Text += token`) to avoid creating a new `Run` per token.

---

## Key Design Decisions

### No External Dependencies
All rendering uses native WPF primitives only. No MdXaml, no third-party markdown libraries. No Roslyn.

### Two-Button Routing
- **Ask** (Enter) — sends input to the LLM via `SendToLlm()`. Editor context (selection or full file ≤300 lines) is automatically injected.
- **Run** (Ctrl+Enter) — executes input as a shell command via `RunShellCommand()`. `cd` is intercepted client-side; all other commands run via `powershell.exe` or `cmd.exe`.

### Response Classification — Post-hoc, Not Pre-committed
Prior to v5.0, DevMind pre-committed to a response strategy by scanning the user's prompt for filenames (`ExtractFileName`). This caused file content, PATCH blocks, and SHELL directives to bleed into each other when the LLM combined them in a single response.

v5.0 eliminates this. All tokens stream into a single buffer. `ResponseParser.Parse()` classifies the complete response after streaming. File creation is triggered by an explicit `FILE:` directive from the LLM, not by guessing from the prompt.

### Shell Command Handling
`_terminalWorkingDir` tracks the current working directory. `cd` is intercepted before spawning a process — relative and absolute paths resolved via `Path.GetFullPath`, `~` / bare `cd` reset to user profile. Commands run via `powershell.exe` (standard Windows path) or fall back to `cmd.exe`. History maintained in `_terminalHistory` (deduped, appended) with `_terminalHistoryIndex` for Up/Down navigation in InputTextBox. stdout and stderr are read concurrently to prevent deadlocks. A **120-second hard timeout** kills the process and reports a timeout error; both interactive and captured variants share this logic.

### Cancellation
`_cts` (`CancellationTokenSource`) is created fresh in `SendToLlm()` and passed to `LlmClient.SendMessageAsync()`. `OperationCanceledException` is swallowed silently in `LlmClient` — cancellation is not treated as an error. Stop button is enabled (`IsEnabled`) during generation; Ask/Run are disabled. Cancellation is checked before agentic re-triggers to prevent frozen loops.

### Thread Model
All UI updates must be dispatched to the main thread. The `onToken`, `onComplete`, and `onError` callbacks from `LlmClient` use `Dispatcher.BeginInvoke` (FIFO queue) rather than `ThreadHelper.JoinableTaskFactory.Run` to avoid blocking the SSE streaming reader and to guarantee FIFO token ordering. VSSDK007/VSTHRD001/VSTHRD110 pragmas suppress fire-and-forget and dispatcher warnings for these intentional patterns.

---

## LLM Directives

The model communicates actions through five directives, all injected into the system prompt at runtime:

### FILE: / END_FILE — Create New Files
```
FILE: DateHelper.cs
namespace MyProject;
public static class DateHelper { ... }
END_FILE
```
- During streaming, `FILE:` triggers display suppression (`_suppressDisplay = true`); generating animation plays, tokens are suppressed from the OutputBox.
- `END_FILE` (or an implicit directive-line terminator) ends capture mode. File content is written to disk by `AgenticExecutor` → `IAgenticHost.SaveFileAsync` after streaming completes — **not** during streaming.
- `SaveFileAsync` calls `StripOuterCodeFence()` before writing, removing any wrapping ` ``` ` fence the model may have emitted around the file content.
- Multiple `FILE:` blocks can appear in a single response.

### PATCH — Edit Existing Files
```
PATCH Program.cs
FIND:
<exact text from file>
REPLACE:
<replacement text>
END_PATCH
```
- `END_PATCH` is required and must appear on its own line after the last REPLACE block. It is the explicit terminator for the PATCH block.
- Multiple FIND/REPLACE pairs are supported within a single PATCH block, before END_PATCH.
- **PATCH blocks are opaque** — inside a PATCH block (between the header and END_PATCH), directive keywords like `FILE:`, `SHELL:`, `SCRATCHPAD:`, and `DONE` are treated as literal FIND/REPLACE content, not new directives. Only `FIND:`, `REPLACE:`, `END_PATCH`, and a new `PATCH` header are meaningful inside a PATCH block.
- Whitespace-normalized matching — CRLF and indentation differences ignored.
- Fuzzy matching fallback (Levenshtein similarity ≥85%) with confirmation prompt (auto-accepted during agentic loop).
- Ambiguity detection — if FIND matches multiple locations, PATCH is rejected with guidance.
- `StripOuterCodeFence()` is applied to both FIND and REPLACE text before matching, removing any wrapping ` ``` ` fence the model may have emitted.
- Auto-READ — target file is loaded into context before patching if not already present.
- UNDO stack — 10-deep timestamped backups in `%TEMP%\DevMind\`.

### SHELL: — Run Commands
```
SHELL: dotnet build
```
- Executed via `RunShellCommandCaptureAsync()`.
- Output captured and fed back into the agentic loop.
- Consecutive duplicate commands are deduplicated.

### READ — Request File Context
```
READ Program.cs
READ Program.cs:100-150    — targeted line range (1-based, inclusive)
READ! Program.cs           — force full content (bypasses outline-first for large files)
```
- Files ≥ 100 lines receive an **outline** (class/method/property declarations) instead of full content by default, to conserve tokens.
- `READ!` bypasses the threshold and forces full content.
- Line-range reads use `FileContentCache` (keyed by filename); the cache is populated on first READ and updated after each PATCH.
- When the model responds with only READ requests (no PATCH/SHELL/FILE), DevMind auto-loads the files and resubmits the original prompt.
- `_pendingResubmitPrompt` stores the original prompt; cleared after use or on cancel.
- If the file is not found, READ returns a list of `*.cs` files in the project directory so you can identify the correct filename.

### GREP — Search File for Pattern
```
GREP: "pattern" filename
GREP: "pattern" filename:100-200
```
- Pattern must be enclosed in double quotes. Matching is case-insensitive substring (`IndexOf`), not regex.
- Optional `:start-end` line range restricts the search window — same syntax as `READ filename:start-end`.
- Results return matching lines with absolute 1-based line numbers (usable directly in a follow-up `READ`).
- Capped at 50 matches. If truncated, the header notes the total count and suggests narrowing the pattern.
- Results are injected into `_readContext` as a side effect (same mechanism as `ApplyReadCommandAsync`).
- A GREP-only response (no PATCH/SHELL/FILE) triggers auto-resubmit via `IsReadOnly` — same path as READ.
- If the file is not found, GREP returns a list of `*.cs` files in the project directory so you can identify the correct filename.

**Typical workflow:**
```
GREP: "SaveFileAsync" AgenticExecutor.cs     → finds lines 42, 89, 155
READ AgenticExecutor.cs:85-100               → reads context around line 89
PATCH AgenticExecutor.cs                      → applies the change
```
Prefer GREP + targeted READ over sequential full-file READs for large files.

### FIND — Cross-File Search
```
FIND: "pattern" *.cs
FIND: "pattern" Services/*.cs
```
- Cross-file search by glob pattern. Returns `filename:lineNum: content` for each hit across all matching files.
- Capped at 100 total matches across all files. If truncated, narrow the pattern.
- Pattern must be in double quotes. Matching is case-insensitive substring, not regex.
- Optional `:start-end` line range restricts the search window within each matched file.
- Results injected into `_readContext` (same as GREP). FIND-only responses trigger auto-resubmit via `IsReadOnly`.
- Use FIND when you need to know where something is used across the project. Use GREP when you already know which file to search.

### DELETE — Remove File
```
DELETE filename.cs
```
- Deletes a file from disk. Does **not** modify `.csproj` or other project references.
- If the file is open in the VS editor, it is closed (without saving) before deletion.
- If the file is not found, returns a list of `*.cs` files in the project directory.
- Resets the repetition guard (same as PATCH/SHELL) — treated as a mutating action.
- Direct user input (`DELETE filename`) prompts a Yes/No confirmation dialog before deleting.
- In batch input (`[WAIT]` separated) and agentic loop, deletion is auto-confirmed.

### RENAME — Rename/Move File
```
RENAME OldFile.cs NewFile.cs
```
- Renames a file on disk. The old file is closed in the VS editor and the new file is opened after rename.
- If `NewFile.cs` contains path separators, it is resolved relative to the project directory.
- If the destination already exists, RENAME is rejected with an error — no overwrite.
- Does **not** update references in other files — use FIND + PATCH to update imports or usings if needed.
- Direct user input (`RENAME old new`) prompts a Yes/No confirmation dialog before renaming.
- In batch input (`[WAIT]` separated) and agentic loop, rename is auto-confirmed.
- Invalidates `FileContentCache` for the old filename after renaming.

### DIFF — Show File Changes
```
DIFF filename.cs
```
- Shows all changes made to the file during this conversation as a unified-style diff.
- Compares the current disk content against the snapshot captured on the file's first READ or PATCH this session.
- Returns `"No changes"` if the file has not been modified or read this session.
- Output capped at 200 lines with a truncation notice if exceeded.
- Information-gathering only — same category as READ/GREP/FIND. A DIFF-only response triggers `IsReadOnly → LoadAndResubmit`.
- Use DIFF after multiple PATCHes to verify cumulative changes before confirming task completion.

### DONE — Explicit Task Completion
```
DONE
```
- Emitted by the model when a multi-step task is fully complete.
- Stops the agentic loop immediately; no further re-triggers.
- Preferred over relying on build exit codes alone for tasks that don't involve a build step.

### SCRATCHPAD — Model State Tracking
```
SCRATCHPAD:
Goal: <task>
Files: <file> (lines N-M)
Status: PLANNING|PATCHING|BUILDING|DONE
Last: <last action>
Next: <next step>
END_SCRATCHPAD
```
- Terminated by `END_SCRATCHPAD` on its own line.
- Stored in `LlmClient._taskScratchpad`; injected into context on subsequent turns (≤200 tokens).
- Helps the model track multi-step task state across turns without repeating context.

---

## Agentic Loop

After `onComplete` processes all directive blocks, DevMind decides whether to re-trigger:

1. **DONE directive** — model emitted `DONE` → stop immediately, task complete.
2. **Build succeeded** (`SHELL:` ran, exit code 0) → inject success prompt, re-trigger once so model can confirm or continue (replaces prior auto-stop).
3. **Run/exec command** (`IsRunOrExecCommand` — `dotnet run`, `start`, etc.) → clean stop after output; no re-trigger.
4. **Build failed or PATCH-only** → increment `_agenticDepth`, inject shell output + **PATCH diff view** (±3 lines context, `>>> CHANGED:`/`>>> ADDED:` markers) into context, re-trigger `SendToLlm()`. Full file is no longer re-injected — the diff-only view keeps tokens lean.
5. **PATCH-FAILED** — failed patch injects `PATCH-FAILED:` error into `shellOutputs` so model sees the failure on next turn.
6. **Auto-READ resubmit** — response was only a READ request → load files, resubmit original prompt (works at depth 0 and depth > 0).
7. **Bare code block** — response had fenced code but no directives → retry once with correction prompt.
8. **Depth cap** (`AgenticLoopMaxDepth`, default 5) → stop, suggest UNDO.

**Post-turn READ compression**: After each agentic turn, `CompressLastUserReadBlocks()` replaces full READ file content in the completed user message with an outline, so the LLM had full content during its turn but history stays lean for the next iteration.

State fields: `_agenticDepth`, `_shellLoopPending`, `_lastShellExitCode`, `_pendingShellContext`, `_pendingResubmitPrompt`.

**File reloading**: After applying a PATCH, `ReloadDocData` is used to refresh open VS editor buffers — replaces prior `VS.Documents.OpenAsync` to avoid spurious "file changed on disk" dialogs.

Error recovery: `onError` and `finally` both reset `_agenticDepth` and `_shellLoopPending` unconditionally when no re-trigger is active, preventing permanent UI freezes.

---

## Streaming (onToken)

`onToken` has exactly three jobs: **filter**, **accumulate**, **display**.

1. Token arrives → `FilterChunk()` strips `<think>` blocks → appended to `responseBuffer`.
2. **Display suppression**: only at newline boundaries (when `visible` contains `\n`), inspect the last completed line in `responseBuffer`:
   - Matches `^FILE: \S+` → set `_suppressDisplay = true`, start the generating animation.
   - Equals `END_FILE` → set `_suppressDisplay = false`, stop the generating animation.
3. If `_suppressDisplay` is true, return without appending to `streamRun`. Otherwise append to `streamRun` for display.

No PATCH tracking. No implicit termination checks. No filename field. `_suppressDisplay` is a display-only flag — even a false positive (e.g. a `FILE: ` line inside PATCH FIND text) only affects what the user sees, not what `onComplete` parses. `responseBuffer` receives every token unconditionally and is the sole input to `ResponseParser.Parse()`.

Fields: `_suppressDisplay`.

---

## Settings (DevMindOptions)

| Property | Default | Description |
|----------|---------|-------------|
| `EndpointUrl` | `http://127.0.0.1:1234/v1` | Base URL for OpenAI-compatible API |
| `ApiKey` | `lm-studio` | Bearer token (use `lm-studio` for LM Studio default) |
| `ServerType` | `LlamaServer` | LLM server type for context-size detection (`LlamaServer`, `LmStudio`, `Custom`) |
| `CustomContextEndpoint` | `` | Endpoint path for context-size detection when `ServerType` is `Custom` |
| `ModelName` | `` (empty) | Model name sent in request; empty = server default |
| `SystemPrompt` | `You are a helpful coding assistant. Be concise and precise.` | Injected as first message in every conversation |
| `OpenFileAfterGeneration` | `true` | Auto-open generated files in VS editor |
| `AgenticLoopMaxDepth` | `5` | Max autonomous iterations (0 = disabled) |
| `ShowLlmThinking` | `false` | Show `<think>` tokens with `[THINKING]` prefix in muted color when `true` |

Settings are accessed via `DevMindOptions.Instance` (synchronous) or `GetLiveInstanceAsync()` (async). The `DevMindOptions.Saved` event fires when the user saves options, triggering `LlmClient.Configure()` and a background connection test.

---

## Runtime System Prompt Injection

At the start of each `SendToLlm()` call, DevMind temporarily injects into the system prompt:

1. **LLM directives** — FILE:/END_FILE, PATCH, SHELL:, READ, DONE syntax and rules.
2. **DefaultNamespace** — the active project's namespace (e.g. "When creating new files, use the namespace 'DevMindTestBed'").
3. **Active project path** — DTE walks active document → containing `.csproj`; path injected so the model knows which project to target for builds and file creation.
4. **DevMind.md** — project-specific context from solution root (lazy-loaded, cached until `/reload`).

The original system prompt is restored immediately after `RunAsync()` returns (safe because `UpdateSystemPrompt()` in LlmClient runs synchronously before the first await).

---

## LlmClient API

```csharp
// Configure endpoint (called on startup and settings change)
void Configure(string endpointUrl, string apiKey)

// Stream a message. All callbacks are invoked on the caller's thread.
// OperationCanceledException is swallowed — onError is NOT called on cancel.
Task SendMessageAsync(
    string userMessage,
    Action<string> onToken,
    Action onComplete,
    Action<Exception> onError,
    CancellationToken cancellationToken = default)

// Test connectivity — queries /v1/models
Task<bool> TestConnectionAsync()

// Reset conversation history to system prompt only
// preserveScratchpad: if true, retains SCRATCHPAD state across reset
void ClearHistory(bool preserveScratchpad = false)

// Connection health check — gated by idle time to avoid hammering the server
Task<bool> CheckConnectionHealthAsync()
```

---

## Batch Input ([WAIT] separator)

Multi-block input can be typed into the input box using `[WAIT]` as a separator line (case-insensitive). Each block is processed sequentially:
- `READ filename` / `SHELL: cmd` / `PATCH file` / `GREP: "pattern" filename` blocks are executed directly without the LLM.
- All other blocks are sent to the LLM; execution pauses until `onComplete` fires before sending the next block.
- Implemented via `ProcessBatchInputAsync()`, signaled by `_batchOnComplete` TaskCompletionSource callback.

## Squeeze Algorithm (LlmClient — v5.25+)

Before trimming history by token count, `LlmClient` runs a **squeeze pass** to structurally compress history without losing semantic content:

| Pass | What it does |
|------|-------------|
| READ deduplication | Collapses repeated reads of the same file into the most recent copy |
| Patch chain collapse | Merges sequential PATCH results to the same file into a single diff summary |
| Eager shell compression | Compresses verbose shell output (build logs, stack traces) early, not just on overflow |
| Outline collapse | Strips doc comments from outline blocks; retains signatures only |
| Compression metadata | Injects steering signals (e.g. `[COMPRESSED]` markers) so the LLM knows history was trimmed |

Squeeze runs before the hard-trim (`TrimHistoryToFit`) and after each agentic turn via `RunDeferredCompression`. During active agentic iterations, full content is preserved for the current turn; compression is deferred to `onComplete` to keep tokens lean for the next iteration.

## Context Budget (ContextBudget class)

`ContextBudget` divides the detected context window into named buckets:

| Bucket | % of context | Purpose |
|--------|-------------|---------|
| SystemPrompt | 25% | System prompt allocation |
| ResponseHeadroom | 15% | Hard reservation for LLM response generation |
| ProtectedTurns | 15% | Last 2 user/assistant pairs — never trimmed |
| WorkingHistory | 45% | All other history — trimmed on soft/hard triggers |

- Context size is auto-detected at startup via `DetectContextSizeAsync()` using server-specific endpoints:
  - **LmStudio**: queries `/api/v0/models`, reads `loaded_context_length` from the model with `state=loaded`.
  - **LlamaServer**: queries `/props` for `n_ctx`.
  - **Custom**: uses `CustomContextEndpoint` from settings.
- Detection is awaited (up to 5 seconds) before the first `SendMessageAsync` call.
- `HistoryHardLimit = TotalLimit - ResponseHeadroomLimit` — history must never exceed this.

## Shell Shortcuts

- `/reload` — clears cached DevMind.md, reloads on next Ask
- `/context` — shows currently loaded READ files
- `/context clear` — wipes READ context without restarting

---

## Coding Standards

- **File version headers**: Always bump `// File: FileName.cs  vX.Y` when making changes.
- **Copyright header**: `// Copyright (c) iOnline Consulting LLC. All rights reserved.`
- **C# language version**: 8.0 — no C# 9+ syntax (no `or` pattern combinators, no records, no init-only setters, no top-level statements). This is a .NET Framework net48 VSIX project.
- **CancellationToken parameter**: Always last parameter (CA1068).
- **No `DispatcherTimer.Dispose()`**: DispatcherTimer does not implement IDisposable — do not call it.
- **Primary constructors**: Do not use in WPF code-behind files. Standard constructors only.
- **Null handling**: Prefer null-conditional (`?.`) and null-coalescing (`??`) over explicit null checks.
- **VSSDK007**: Suppress with `#pragma warning disable/restore VSSDK007` for intentional fire-and-forget patterns in VS event handlers.
- **No external NuGet dependencies** beyond VSSDK and Community Toolkit.
- **No third-party markdown renderers** — plain RichTextBox with Run/Paragraph appends only.

---

## Common Patterns

### Adding a new toolbar button

1. Add `<Button>` to the toolbar `Grid` (Row 2) in `DevMindToolWindowControl.xaml`. Update `<Grid.ColumnDefinitions>` if needed.
2. Add `Click` handler in `DevMindToolWindowControl.xaml.cs`.
3. If button state changes with input enabled/disabled, update `SetInputEnabled(bool enabled)`.

### Adding a new setting

1. Add property to `DevMindOptions` in `DevMindOptionsPage.cs` with `[Category]`, `[DisplayName]`, `[Description]`, `[DefaultValue]` attributes.
2. Access via `DevMindOptions.Instance.YourProperty` anywhere in the codebase.
3. React to changes via the `DevMindOptions.Saved` event in `DevMindToolWindowControl`.

### Adding a new directive

1. Add a new `BlockType` value to `ResponseParser.cs`.
2. Add parsing logic in `ResponseParser.Parse()`.
3. Add a `case` in the `onComplete` execute loop in `DevMindToolWindowControl.xaml.cs`.
4. Update the runtime `llmDirective` string to teach the model the new syntax.
5. Document in `DevMind.md`.

---

## Feature Roadmap

1. **READ outline** — When manually typing READ, display method/property/class outline alongside line count.
2. **Diff preview** — When the LLM suggests code changes, show a diff view before applying.
3. **Syntax highlighting** — Color-code fenced code blocks in `OutputBox` using span-level `Run` coloring.
4. **Multi-turn context control** — Button to include/exclude file context per message.
5. **Self-modification** — DevMind building DevMind through its own UI.
6. **Smart READ targeting** — Model frequently does linear search through large files in 200-line increments (e.g., five sequential READs to scan a 1,473-line file), consuming excessive context. Investigate system prompt hints or outline-guided targeting so the model reads the relevant line range on the first try instead of brute-force scanning.
7. ~~**GREP directive**~~ — **Implemented v6.0.34**. Single-file substring search with line numbers. Eliminates sequential READ scanning. Model does one GREP to find the target, then one targeted READ for context.
8. ~~**RENAME directive**~~ — **Implemented**. `RENAME OldFile.cs NewFile.cs`. Renames file on disk, closes old editor tab, opens new file. Does not update project references (separate concern).
9. ~~**DELETE directive**~~ — **Implemented v6.0.65**. `DELETE filename.cs`. Removes file from disk, closes open editor tab. Does not update `.csproj` references (separate concern).
10. ~~**FIND directive**~~ — **Implemented v6.0.64**. `FIND: "pattern" *.cs`. Cross-file search by glob pattern. Returns filename + line number + match for each hit across all matching files, capped at 100. Solves the "where is this used?" problem without sequential READs.
11. **TEST directive** — `TEST ProjectName.csproj` or `TEST ClassName.MethodName`. Structured test execution with per-test pass/fail results instead of raw console output. Cheaper on context than SHELL: dotnet test.
12. ~~**DIFF directive**~~ — **Implemented**. `DIFF Program.cs`. Shows changes since conversation start as a unified-style diff. Uses LCS-based algorithm for small files, positional fallback for large files. Helps the model verify cumulative modifications across multiple agentic turns.
