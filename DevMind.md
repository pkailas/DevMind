# DevMind Project Context

## Project Overview
DevMind is a Visual Studio extension (VSIX) that provides local LLM assistance directly inside Visual Studio 2022+. It is a product of iOnline Consulting LLC. The UI is a CC-style command executor (single dark RichTextBox stream), not a bubble chat interface.

**Current Version**: v5.2

## Platform & Constraints
- **Project type**: VSIX (.NET Framework net48)
- **C# language version**: 8.0 — no C# 9+ syntax (no `or` pattern combinators, no records, no init-only setters, no top-level statements)
- **UI framework**: WPF inside a ToolWindowPane
- **Threading**: All VS API calls require `await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync()` first
- **Async void**: Permitted only for fire-and-forget VS event handlers; must have try/catch wrapping the entire body
- **Dispatcher.BeginInvoke**: Used for `onToken`/`onComplete`/`onError` callbacks (FIFO ordering, non-blocking)

## Architecture
- `DevMindToolWindowControl.xaml` — WPF layout: Input TextBox (top), toolbar row (Ask/Run/Stop/Clear), OutputBox RichTextBox (dark #1E1E1E, Consolas)
- `DevMindToolWindowControl.xaml.cs` — All logic; current version is tracked in the file header comment
- `DevMindToolWindowControl.Context.cs` — Editor context, READ command, line-range reads, outline injection, `BuildMessageWithContext`, `GetActiveProjectPathAsync`
- `DevMindToolWindowControl.Patch.cs` — PATCH engine, `BuildPatchDiffView`, `_patchDiffCache`
- `DevMindToolWindowControl.Shell.cs` — Shell execution with 120-second timeout, `RunShellCommandCaptureAsync`
- `LlmClient.cs` — HTTP streaming client; `ContextBudget`, `DetectContextSizeAsync`, `FileContentCache`, scratchpad management
- `FileContentCache.cs` — In-memory line-indexed file cache; powers `READ filename:start-end`
- `ResponseParser.cs` — Parses LLM responses into typed blocks (Text, File, Patch, Shell, ReadRequest, Scratchpad)
- `DevMindOptionsPage.cs` — VS Tools > Options page

## Key Fields & Methods
- `AppendOutput(text, OutputColor)` — appends colored Run to OutputBox; never replaces, always appends
- `OutputColor` enum: Normal, Dim, Input (blue), Error (red), Success (green), Thinking (muted purple)
- `SendToLlm()` — async void, handles Ask button and Enter key
- `RunShellCommand()` — handles Run button and Ctrl+Enter; intercepts `/reload` command
- `GetEditorContextAsync()` — reads active VS editor selection and/or full file (≤300 lines)
- `BuildMessageWithContext()` — prepends fenced code block + active project path to LLM message if context exists
- `ResponseParser.Parse(response)` — converts full LLM response string into ordered `List<ResponseBlock>`
- `SaveGeneratedFileAsync(fileName, code)` — strips `<think>` blocks, resolves active project dir via DTE, writes file, calls `ProjectItems.AddFromFile()`
- `ApplyReadRangeAsync(fileHint, startLine, endLine)` — loads a specific line range using `FileContentCache`
- `ProcessBatchInputAsync(text)` — executes [WAIT]-separated blocks sequentially
- `BuildPatchDiffView(...)` — generates ±3 line diff context with `>>> CHANGED:`/`>>> ADDED:` markers
- `GetActiveProjectPathAsync()` — resolves the active document's containing `.csproj` path for build commands
- `StartGeneratingAnimation(fileName)` / `StopGeneratingAnimation()` — animated dots in OutputBox during file gen
- `LoadDevMindContextAsync()` — reads DevMind.md from solution root; cached in `_devMindContext`

## LLM Backend
- Local model via LM Studio or llama-server at `http://localhost:1234` (OpenAI-compatible)
- Current model: Qwen3 27B on RTX 4000 SFF Ada (20GB VRAM)
- Models may produce `<think>...</think>` blocks — stripped by default; shown with `[THINKING]` prefix when `ShowLlmThinking` is enabled

## Context Budget
Context window size is auto-detected at startup:
- **llama-server**: GET `/props` → `default_generation_settings.n_ctx`
- **LM Studio**: GET `/api/v0/models` → `loaded_context_length` for the loaded model
- **Custom**: Configurable endpoint path (relative or absolute URL)

Detected size is divided into buckets by `ContextBudget`:
- SystemPrompt: 25%, ResponseHeadroom: 15% (hard reserved), ProtectedTurns: 15%, WorkingHistory: 45%

## LLM Directives
The model can use five directives in any combination within a single response:

### FILE: / END_FILE — Create a new file
```
FILE: <filename>
<raw source code>
END_FILE
```

### PATCH — Edit an existing file
```
PATCH <filename>
FIND:
<exact text from the file>
REPLACE:
<replacement text>
```
- Uses whitespace-normalized matching — CRLF and indentation differences ignored
- Supports path hints to disambiguate same-named files: `PATCH Services/Program.cs`
- Multiple FIND/REPLACE pairs per PATCH block are supported
- **FIND text must be copied verbatim from READ output — never reconstructed from memory**

### SHELL: — Run a shell command
```
SHELL: dotnet build "path/to/project.csproj"
```
- Output is captured and fed back into the agentic loop context
- 120-second hard timeout; process is killed and reported if exceeded

### READ — Request file context before editing
```
READ <filename>                  — full content if <100 lines, outline otherwise
READ <filename>:<start>-<end>   — targeted line range (1-based, inclusive)
READ! <filename>                 — force full content (expensive, bypasses outline threshold)
```
- Files ≥100 lines get an outline (class/method/property declarations) to conserve tokens
- Triggers auto-READ resubmit: file is loaded and original prompt resubmitted automatically
- Full content is available in cache — use line-range READ for targeted inspection

### SCRATCHPAD — Track task state across turns
```
SCRATCHPAD:
Goal: <task>
Files: <file> (lines N-M)
Status: PLANNING|PATCHING|BUILDING|DONE
Last: <last action>
Next: <next step>
END_SCRATCHPAD
```
- Terminated by `END_SCRATCHPAD` on its own line
- Stored in `LlmClient._taskScratchpad` (capped at 200 tokens)
- Injected into next turn's context so the model remembers what it's doing

## PATCH-RESULT Feedback
After each PATCH, the agentic loop injects a compact diff view instead of the full file:
```
[PATCH-RESULT:filename] Applied successfully (undo depth: N)
--- Changed region (lines X-Y) ---
N:     <context line>
N+1: >>> CHANGED: <replaced line>
N+2: >>> ADDED:   <new line>
N+3:     <context line>
--- End of changes ---
```
Full file is still in cache — use `READ filename:start-end` for targeted inspection.

## PATCH Command (user-typed)
- Syntax: `PATCH <filename> / FIND: / <text> / REPLACE: / <text>`
- Bypasses LLM entirely — instant local file edit, no tokens consumed
- Uses whitespace-normalized matching — CRLF and indentation differences ignored

## READ Command
- Syntax: `READ <filename>` or `READ path/hint/filename.cs` for disambiguation
- `READ filename:start-end` — loads just those lines (1-based, inclusive)
- `READ! filename` — forces full content regardless of line count
- Loads file into `_readContext`, prepended to next Ask — no tokens consumed
- Multiple READs accumulate before a single Ask
- Context persists across multiple Asks — use `/context` (Run) to see loaded files, `/context clear` to wipe

## UNDO Command
- Syntax: `UNDO`
- Restores the last PATCHed file from a timestamped backup in `%TEMP%\DevMind\`
- Stack depth: 10 — oldest backup discarded when limit reached
- Stack cleared on Restart

## Batch Input ([WAIT] separator)
Multi-block input using `[WAIT]` as a separator (case-insensitive):
- `READ`, `SHELL:`, `PATCH` blocks execute directly without the LLM
- All other blocks are sent to the LLM sequentially — waits for completion before sending next block

## Build Command
The build command is resolved dynamically from the active project:
- If the active document belongs to a `.csproj`, uses: `dotnet build "path/to/project.csproj"`
- Fallback: `msbuild "DevMind.slnx" /p:DeployExtension=false /verbosity:minimal`

## Coding Conventions
- All file headers include: `// File: FileName.cs  vX.Y` — always bump version on changes
- MVVM not used in this project — code-behind only
- No external NuGet dependencies beyond VSSDK and Community Toolkit
- No third-party markdown renderers — plain RichTextBox with Run/Paragraph appends only
- Namespace: `DevMind`
- C# 8.0 only — no records, no init-only setters, no top-level statements, no `or` patterns

## Shell Commands (Run Button)
- PowerShell is the shell — use `;` to chain commands, NOT `&&` (`&&` is invalid in PowerShell)
- Example: `git commit -am "msg"; git push`
- `dotnet build` works directly: `dotnet build C:\path\to\Project.csproj`

## Shell Shortcuts
- `/reload` — clears cached DevMind.md, reloads on next Ask
- `/context` — shows currently loaded READ files
- `/context clear` — wipes READ context without restarting
- `/stats` — shows context budget statistics

## DevMind.md Reload
Type `/reload` in the input box and press Run (Ctrl+Enter) to force a fresh read of this file on the next Ask.
