# CLAUDE.md — DevMind Developer Reference

## Project Overview

**DevMind** is a Visual Studio extension (VSIX) that provides a local LLM coding assistant inside the VS IDE tool window. It targets developers who want Claude Code-style assistance using a privately hosted model (LM Studio, Ollama, or any OpenAI-compatible endpoint) without sending code to external servers.

- **Product**: DevMind
- **Brand**: iOnline Consulting LLC
- **Current Version**: v5.2
- **Platform**: Visual Studio 2022+ (VSIX), .NET Framework (VSSDK requirement)
- **Language**: C# with WPF UI

---

## Architecture

### Entry Points

| File | Purpose |
|------|---------|
| `DevMindPackage.cs` | Main VS package — registers tool window, options page, commands |
| `DevMindToolWindow.cs` | Tool window host — creates `LlmClient` and `DevMindToolWindowControl` on startup |
| `DevMindToolWindowControl.xaml/.cs` | WPF UI — single-stream output, input bar, system prompt panel, streaming, agentic loop |
| `DevMindToolWindowControl.Context.cs` | Editor context, DevMind.md loading, READ command, file search, `BuildMessageWithContext` |
| `DevMindToolWindowControl.Patch.cs` | PATCH command — parsing, whitespace-normalized matching, fuzzy matching, UNDO, backup stack |
| `DevMindToolWindowControl.Shell.cs` | Shell execution — `RunShellCommand`, `RunShellCommandCaptureAsync`, `ParseShellDirectives` |
| `ResponseParser.cs` | Parses complete LLM responses into typed blocks (File, Patch, Shell, ReadRequest, Text) |
| `LlmClient.cs` | HTTP client — SSE streaming to OpenAI-compatible `/v1/chat/completions` |
| `DevMindOptionsPage.cs` | VS Tools > Options settings — EndpointUrl, ApiKey, ModelName, ServerType, CustomContextEndpoint, SystemPrompt, AgenticLoopMaxDepth, ShowLlmThinking |
| `FileContentCache.cs` | In-memory line-indexed file cache — powers `READ filename:start-end` line-range access |

### Data Flow

```
User types → InputTextBox
  Enter      → SendToLlm() → LlmClient.SendMessageAsync()
                → onToken: streams to OutputBox, captures FILE: blocks into _fileCaptureBuffer
                → onComplete: ResponseParser.Parse() → execute blocks in order → agentic loop
  Ctrl+Enter → RunShellCommand() → powershell.exe / cmd.exe → stdout/stderr → AppendOutput()
```

### Response Dispatcher (v5.0)

The LLM response is classified AFTER it arrives, not before. All tokens stream into a single `responseBuffer`. After streaming completes, `ResponseParser.Parse()` splits the response into typed blocks which are executed in order:

```
ResponseBlock
├── TextBlock        — plain text (already displayed during streaming)
├── FileBlock        — FILE:/END_FILE content → SaveGeneratedFileAsync()
├── PatchBlock       — PATCH directive → ApplyPatchAsync()
├── ShellBlock       — SHELL: directive → RunShellCommandCaptureAsync()
├── ReadRequest      — model asking to READ a file → ApplyReadCommandAsync() / ApplyReadRangeAsync()
└── Scratchpad       — SCRATCHPAD: block → LlmClient.UpdateScratchpad()
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
- During streaming, `FILE:` triggers silent file capture mode (generating animation plays).
- `END_FILE` ends capture. File is saved via `SaveGeneratedFileAsync()`.
- Token boundary defense: `_fileCaptureBuffer` is trimmed of trailing `END` fragments before saving.
- Multiple `FILE:` blocks can appear in a single response.

### PATCH — Edit Existing Files
```
PATCH Program.cs
FIND:
<exact text from file>
REPLACE:
<replacement text>
```
- Whitespace-normalized matching — CRLF and indentation differences ignored.
- Fuzzy matching fallback (Levenshtein similarity ≥85%) with confirmation prompt (auto-accepted during agentic loop).
- Ambiguity detection — if FIND matches multiple locations, PATCH is rejected with guidance.
- Multi-block support — multiple FIND/REPLACE pairs in one PATCH.
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

1. **Build succeeded** (`SHELL:` ran, exit code 0) → stop, display "Build succeeded — task complete."
2. **Build failed or PATCH-only** → increment `_agenticDepth`, inject shell output + **PATCH diff view** (±3 lines context, `>>> CHANGED:`/`>>> ADDED:` markers) into context, re-trigger `SendToLlm()`. Full file is no longer re-injected — the diff-only view keeps tokens lean.
3. **Auto-READ resubmit** — response was only a READ request → load files, resubmit original prompt.
4. **Bare code block** — response had fenced code but no directives → retry once with correction prompt.
5. **Depth cap** (`AgenticLoopMaxDepth`, default 5) → stop, suggest UNDO.

**Post-turn READ compression**: After each agentic turn, `CompressLastUserReadBlocks()` replaces full READ file content in the completed user message with an outline, so the LLM had full content during its turn but history stays lean for the next iteration.

State fields: `_agenticDepth`, `_shellLoopPending`, `_lastShellExitCode`, `_pendingShellContext`, `_pendingResubmitPrompt`.

Error recovery: `onError` and `finally` both reset `_agenticDepth` and `_shellLoopPending` unconditionally when no re-trigger is active, preventing permanent UI freezes.

---

## Streaming (onToken)

Single streaming path with inline FILE: detection:

1. Token arrives → `FilterChunk()` strips `<think>` blocks → appended to `responseBuffer`.
2. If `_inFileCapture` is true: tokens accumulate in `_fileCaptureBuffer`, generating animation plays. `END_FILE` line exits capture mode.
3. If `_inFileCapture` is false: check `responseBuffer` tail for `FILE: <filename>` pattern. If found, enter capture mode. Otherwise, append to `streamRun` for display.

Fields: `_inFileCapture`, `_fileCaptureFileName`, `_fileCaptureBuffer`.

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

1. **LLM directives** — FILE:/END_FILE, PATCH, SHELL:, READ syntax and rules.
2. **DefaultNamespace** — the active project's namespace (e.g. "When creating new files, use the namespace 'DevMindTestBed'").
3. **DevMind.md** — project-specific context from solution root (lazy-loaded, cached until `/reload`).

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
void ClearHistory()
```

---

## Batch Input ([WAIT] separator)

Multi-block input can be typed into the input box using `[WAIT]` as a separator line (case-insensitive). Each block is processed sequentially:
- `READ filename` / `SHELL: cmd` / `PATCH file` blocks are executed directly without the LLM.
- All other blocks are sent to the LLM; execution pauses until `onComplete` fires before sending the next block.
- Implemented via `ProcessBatchInputAsync()`, signaled by `_batchOnComplete` TaskCompletionSource callback.

## Context Budget (ContextBudget class)

`ContextBudget` divides the detected context window into named buckets:

| Bucket | % of context | Purpose |
|--------|-------------|---------|
| SystemPrompt | 25% | System prompt allocation |
| ResponseHeadroom | 15% | Hard reservation for LLM response generation |
| ProtectedTurns | 15% | Last 2 user/assistant pairs — never trimmed |
| WorkingHistory | 45% | All other history — trimmed on soft/hard triggers |

- Context size is auto-detected at startup via `DetectContextSizeAsync()` using server-specific endpoints.
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
