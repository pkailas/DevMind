# CLAUDE.md — DevMind Developer Reference

## Project Overview

**DevMind** is a Visual Studio extension (VSIX) that provides a local LLM coding assistant inside the VS IDE tool window. It targets developers who want Claude Code-style assistance using a privately hosted model (LM Studio, Ollama, or any OpenAI-compatible endpoint) without sending code to external servers.

- **Product**: DevMind
- **Brand**: iOnline Consulting LLC
- **Current Version**: v4.5
- **Platform**: Visual Studio 2022+ (VSIX), .NET Framework (VSSDK requirement)
- **Language**: C# with WPF UI

---

## Architecture

### Entry Points

| File | Purpose |
|------|---------|
| `DevMindPackage.cs` | Main VS package — registers tool window, options page, commands |
| `DevMindToolWindow.cs` | Tool window host — creates `LlmClient` and `DevMindToolWindowControl` on startup |
| `DevMindToolWindowControl.xaml/.cs` | WPF UI — single-stream output, input bar, system prompt panel, all UI logic |
| `LlmClient.cs` | HTTP client — SSE streaming to OpenAI-compatible `/v1/chat/completions` |
| `DevMindOptionsPage.cs` | VS Tools > Options settings — EndpointUrl, ApiKey, ModelName, SystemPrompt |

### Data Flow

```
User types → InputTextBox
  Enter     → SendToLlm()   → LlmClient.SendMessageAsync() → onToken appends to streamRun in OutputBox
  Ctrl+Enter → RunShellCommand() → powershell.exe / cmd.exe → stdout/stderr → AppendOutput()
```

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
OutputColor.Normal  — #CCCCCC  (LLM response text, shell stdout)
OutputColor.Dim     — #888888  (startup banner, [Stopped] notice)
OutputColor.Input   — #569CD6  (echoed user input lines prefixed with "> ")
OutputColor.Error   — #F44747  (shell stderr, LLM errors)
OutputColor.Success — #4EC94E  (future use)
```

LLM streaming tokens are appended directly to a pre-allocated `Run` (`streamRun.Text += token`) to avoid creating a new `Run` per token.

---

## Key Design Decisions

### No External Dependencies
All rendering uses native WPF primitives only. No MdXaml, no third-party markdown libraries.

### Two-Button Routing
- **Ask** (Enter) — sends input to the LLM via `SendToLlm()`. Editor context (selection or full file ≤300 lines) is automatically injected.
- **Run** (Ctrl+Enter) — executes input as a shell command via `RunShellCommand()`. `cd` is intercepted client-side; all other commands run via `powershell.exe` or `cmd.exe`.

### Shell Command Handling
`_terminalWorkingDir` tracks the current working directory. `cd` is intercepted before spawning a process — relative and absolute paths resolved via `Path.GetFullPath`, `~` / bare `cd` reset to user profile. Commands run via `powershell.exe` (standard Windows path) or fall back to `cmd.exe`. History maintained in `_terminalHistory` (deduped, appended) with `_terminalHistoryIndex` for Up/Down navigation in InputTextBox.

### Cancellation
`_cts` (`CancellationTokenSource`) is created fresh in `SendToLlm()` and passed to `LlmClient.SendMessageAsync()`. `OperationCanceledException` is swallowed silently in `LlmClient` — cancellation is not treated as an error. Stop button is enabled (`IsEnabled`) during generation; Ask/Run are disabled.

### Thread Model
All UI updates must be dispatched via `ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync()`. The `onToken`, `onComplete`, and `onError` callbacks from `LlmClient` all switch to the main thread before touching UI elements. VSSDK007 pragma suppresses fire-and-forget warnings for intentional patterns.

---

## Settings (DevMindOptions)

| Property | Default | Description |
|----------|---------|-------------|
| `EndpointUrl` | `http://127.0.0.1:1234/v1` | Base URL for OpenAI-compatible API |
| `ApiKey` | `lm-studio` | Bearer token (use `lm-studio` for LM Studio default) |
| `ModelName` | `` (empty) | Model name sent in request; empty = server default |
| `SystemPrompt` | `You are a helpful coding assistant. Be concise and precise.` | Injected as first message in every conversation |

Settings are accessed via `DevMindOptions.Instance` (synchronous) or `GetLiveInstanceAsync()` (async). The `DevMindOptions.Saved` event fires when the user saves options, triggering `LlmClient.Configure()` and a background connection test.

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

## Coding Standards

- **File version headers**: Always bump `// File: FileName.cs  vX.Y` when making changes.
- **Copyright header**: `// Copyright (c) iOnline Consulting LLC. All rights reserved.`
- **CancellationToken parameter**: Always last parameter (CA1068).
- **No `DispatcherTimer.Dispose()`**: DispatcherTimer does not implement IDisposable — do not call it.
- **Primary constructors**: Do not use in WPF code-behind files. Standard constructors only.
- **Null handling**: Prefer null-conditional (`?.`) and null-coalescing (`??`) over explicit null checks.
- **[LoggerMessage]**: Use source-generated logging where applicable.
- **VSSDK007**: Suppress with `#pragma warning disable/restore VSSDK007` for intentional fire-and-forget patterns in VS event handlers.

---

## File Generation

When the user's prompt contains a word ending in a recognized source file extension, DevMind silently generates the file to disk instead of streaming the code to the output.

### Detection — `ExtractFileName(string prompt)`
Splits the prompt on whitespace and punctuation separators and returns the first token whose suffix matches a known extension (`.cs`, `.ts`, `.js`, `.py`, `.xml`, `.json`, `.sql`, `.html`, `.css`, `.xaml`, `.cpp`, `.h`, `.vb`, `.fs`). Surrounding quote/bracket characters are trimmed. Returns `null` if no filename is found.

### Prompt injection
When a filename is detected, the instruction `"\n\nIMPORTANT: Respond with ONLY the raw source code…"` is appended to `contextualMessage` before sending to the LLM. This suppresses markdown fences and prose from the model.

### Silent accumulation pattern
- `isFileGeneration` (local bool) gates all streaming behavior in `SendToLlm()`.
- No `streamPara`/`streamRun` is created; a `StringBuilder fileGenBuffer` is used instead.
- `onToken`: appends to `fileGenBuffer` (no UI update).
- `onComplete`: calls `SaveGeneratedFileAsync(fileName, fileGenBuffer.ToString())` instead of `AppendNewLine()`.
- The output stream shows only `⚙ Generating {fileName}...` (Dim) during generation.

### `SaveGeneratedFileAsync(string fileName, string code)`
- Resolves full path as `Path.Combine(_terminalWorkingDir, fileName)`.
- Writes the trimmed code as UTF-8 via `File.WriteAllText`.
- Appends a Success-colored confirmation line and a Dim path line.
- Attempts to open the file in VS via `VS.Documents.OpenAsync` (failure is non-fatal).
- On any exception, appends an Error-colored failure message.

---

## Feature Roadmap

1. **Diff preview** — When the LLM suggests code changes, show a diff view before applying.
2. **Syntax highlighting** — Color-code fenced code blocks in `OutputBox` using span-level `Run` coloring.
3. **Multi-turn context control** — Button to include/exclude file context per message.

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
