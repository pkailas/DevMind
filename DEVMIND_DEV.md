# DevMind — Developer & Architecture Guide

> **Version:** 4.6.x  
> **Copyright:** iOnline Consulting LLC  
> **Solution:** `C:\Users\pkailas.KAILAS\source\repos\DevMind\`

---

## Table of Contents

1. [Overview](#1-overview)
2. [Solution Structure](#2-solution-structure)
3. [Architecture](#3-architecture)
4. [Key Classes & Files](#4-key-classes--files)
5. [UI Layout (XAML)](#5-ui-layout-xaml)
6. [Input Processing Pipeline](#6-input-processing-pipeline)
7. [PATCH Engine](#7-patch-engine)
8. [Fuzzy Match Engine](#8-fuzzy-match-engine)
9. [Undo / Backup Stack](#9-undo--backup-stack)
10. [READ Context System](#10-read-context-system)
11. [Agentic Loop (Auto-Execute)](#11-agentic-loop-auto-execute)
12. [New File Generation](#12-new-file-generation)
13. [Shell Command Execution](#13-shell-command-execution)
14. [LLM Client](#14-llm-client)
15. [VSIX Build & Version Stamping Pipeline](#15-vsix-build--version-stamping-pipeline)
16. [State Lifecycle](#16-state-lifecycle)
17. [Adding a New Command](#17-adding-a-new-command)
18. [Adding a New Directive (e.g. SHELL:)](#18-adding-a-new-directive-eg-shell)
19. [Testing](#19-testing)
20. [Known Limitations & Future Work](#20-known-limitations--future-work)

---

## 1. Overview

DevMind is a VSIX extension for Visual Studio 2022/2026 that provides a local LLM coding assistant backed by LM Studio. It is a single WPF tool window with no cloud dependencies.

**Design philosophy:**
- No external NuGet packages beyond the VSSDK
- All file editing done via pure string manipulation (no LLM round-trips for PATCH)
- Agentic: LLM responses are scanned for PATCH blocks and auto-executed
- Privacy-first: all inference runs locally via LM Studio's OpenAI-compatible API

**Primary workflow:**  
`READ file → Ask LLM → LLM emits PATCH block → DevMind auto-applies → file updated on disk`

---

## 2. Solution Structure

```
DevMind\
├── DevMind.csproj                        # .NET Framework 4.8, VSIX project
├── DevMind.sln
├── build.counter                         # Monotonically incrementing build number (committed to Git)
├── source.extension.vsixmanifest         # Source manifest — stamped at build time
├── extension.vsixmanifest                # Bin manifest — stamped at build time
├── Properties\
│   └── AssemblyInfo.cs                   # Stamped at build time (version = 4.6.N.0)
├── DevMindToolWindow.cs                  # Tool window host (VSPackage boilerplate)
├── DevMindToolWindowControl.xaml         # UI layout
├── DevMindToolWindowControl.xaml.cs      # All logic (~1600 lines)
├── LlmClient.cs                          # HTTP streaming client for LM Studio API
├── DevMindPackage.cs                     # Package entry point, options registration
├── DevMindOptions.cs                     # Options page (Tools → Options → DevMind)
├── DevMind.md                            # Optional solution-level context file (not in repo root)
└── CLAUDE.md                             # CC instructions for this repo
```

**Test harness:**  
`C:\Users\pkailas.KAILAS\source\repos\DevMindTestBed\` — a .NET 8 Console App used for PATCH/fuzzy testing. `TestBed.cs` is the scratch file.

---

## 3. Architecture

```
┌─────────────────────────────────────────────────────────┐
│  Visual Studio Process                                  │
│                                                         │
│  ┌───────────────────────────────────────────────────┐  │
│  │  DevMindToolWindowControl (WPF UserControl)       │  │
│  │                                                   │  │
│  │  ┌──────────────┐   ┌────────────────────────┐   │  │
│  │  │  Input Box   │   │  Output Stream (RTB)   │   │  │
│  │  │  (TextBox)   │   │  + Terminal Strip      │   │  │
│  │  └──────┬───────┘   └────────────────────────┘   │  │
│  │         │                                         │  │
│  │  SendToLlm()                                      │  │
│  │    ├── /command → RunShellCommand()               │  │
│  │    ├── PATCH    → ApplyPatchAsync()               │  │
│  │    ├── UNDO     → ApplyUndoAsync()                │  │
│  │    ├── READ     → ApplyReadCommandAsync()         │  │
│  │    └── LLM msg  → LlmClient.StreamAsync()        │  │
│  │                       │                           │  │
│  │              onToken callback                     │  │
│  │              onComplete callback                  │  │
│  │                → AutoExecutePatchAsync()          │  │
│  └───────────────────────────────────────────────────┘  │
│                                                         │
│  LlmClient.cs  ──HTTP──►  LM Studio :1234              │
└─────────────────────────────────────────────────────────┘
```

---

## 4. Key Classes & Files

### `DevMindToolWindowControl.xaml.cs`

The monolithic controller — all command handling, LLM streaming, PATCH engine, fuzzy match, undo stack, shell execution, and output rendering live here.

**Key fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_llmClient` | `LlmClient` | HTTP streaming client |
| `_cts` | `CancellationTokenSource` | Cancels in-flight LLM requests |
| `_patchBackupStack` | `Stack<(string, string)>` | Undo backup paths, cap 10 |
| `_pendingFuzzyPatch` | `(...)? ` | Suspended PATCH awaiting 1/2 confirmation |
| `_readContext` | `string` | Accumulated READ file contents, prepended to every LLM prompt |
| `_devMindContext` | `string` | DevMind.md contents, lazy-loaded once per session |
| `_terminalHistory` | `List<string>` | Shell command history for Up/Down navigation |
| `_inThinkBlock` | `bool` | Strips `<think>...</think>` tokens from streaming output |
| `_spacerParagraph` | `Paragraph` | Top spacer for bottom-anchored output layout |

### `LlmClient.cs`

Thin HTTP client targeting LM Studio's OpenAI-compatible `/v1/chat/completions` endpoint. Supports streaming (`stream: true`) via SSE. Maintains conversation history as a `List<ChatMessage>`. Thinking is suppressed via `["thinking"] = { "type": "disabled" }` in the request body.

### `DevMindOptions.cs`

VS Options page (`Tools → Options → DevMind`) — endpoint URL, model name, max tokens, temperature. Changes fire `OnSettingsSaved` in the control.

---

## 5. UI Layout (XAML)

```
Row 0: System Prompt (collapsible StackPanel)
Row 1: Input Box (TextBox, multi-line, MinHeight=120)
Row 2: Toolbar (Ask | Run | Stop | Restart | Clear | ⌫ | [context] | [status])
Row 3: DockPanel
    └── OutputBox (RichTextBox, fills space, IsReadOnly=True)
    └── Terminal Strip (Border docked Bottom)
            └── ">" TextBlock + TerminalInputBox (TextBox, single-line)
```

**Output colors** (`OutputColor` enum):

| Color | RGB | Usage |
|-------|-----|-------|
| Normal | `#CCCCCC` | LLM response text |
| Dim | `#888888` | Status messages, PATCH results |
| Input | `#569CD6` | User input echo (`> command`) |
| Error | `#F44847` | Errors |
| Success | `#4EC94E` | Success confirmations |

**Terminal strip** — `BorderBrush="#FF569CD6"` (blue top accent), `BorderThickness="0,2,0,0"`. The `>` prompt is `#FFCCCCCC`. `TerminalInputBox` is transparent, Consolas 13pt, no border.

---

## 6. Input Processing Pipeline

`SendToLlm()` is the main dispatch method. Called by Ask button and Enter key in the input box.

```
SendToLlm()
│
├─ Starts with "/" and single line?
│    └─ Strip slash → RunShellCommand(cmd)
│
├─ Starts with "PATCH "?
│    └─ ApplyPatchAsync(text)
│
├─ Equals "UNDO"?
│    └─ ApplyUndoAsync()
│
├─ Top lines start with "READ "?
│    ├─ ApplyReadCommandAsync(readBlock)
│    └─ Remaining text → continue to LLM
│
└─ Everything else → LlmClient.StreamAsync()
```

**Terminal strip** (`ExecuteTerminalInput`) has its own dispatch:

```
ExecuteTerminalInput()
│
├─ _pendingFuzzyPatch.HasValue?
│    ├─ "1" → ApplyPendingFuzzyPatch()
│    ├─ "2" → cancel, clear pending
│    └─ else → print reminder, return
│
└─ else → RunShellCommand(command)
```

---

## 7. PATCH Engine

**Entry point:** `ApplyPatchAsync(string input, bool clearInput = true)`

### Algorithm

1. **Parse** — `ParsePatchBlocks(input)` extracts all `FIND:`/`REPLACE:` pairs. Supports multiple blocks per PATCH command.

2. **Resolve file** — `FindFileInSolutionAsync(fileName, hint)` walks the solution directory tree using `SafeEnumerateFiles` (silently skips inaccessible dirs). Partial path hints (e.g., `Services/Foo.cs`) are used to filter ambiguous matches.

3. **Read file** — `ReadFilePreservingEncoding(path)` detects BOM/encoding and returns the text + encoding. CRLF vs LF detected from content.

4. **Validate ALL blocks first** (all-or-nothing semantics):
   - Normalize: `NormalizeWithMap(content)` — collapses all whitespace runs to single space, returns a `normToOrig[]` position mapping array
   - Exact match: `normContent.IndexOf(normFind)`
   - Ambiguity check: ensure no second match exists
   - Fuzzy fallback: `FindFuzzyMatch(content, findText, normFind)` if exact fails
   - If fuzzy fires: suspend via `_pendingFuzzyPatch`, return — do not apply

5. **Apply in reverse order** — resolved blocks are sorted by `origStart` descending so later offsets don't shift earlier ones.

6. **Line ending preservation** — REPLACE text is normalized to `\n`, then converted to `\r\n` if the file uses CRLF.

7. **Indentation fix** — `origStart` is walked back to the start of its line to prevent indentation doubling on exact match.

8. **Backup** — original file is copied to `%TEMP%\DevMind\<guid>_<filename>` before write. Pushed onto `_patchBackupStack`.

9. **Write** — `File.WriteAllText(fullPath, newContent, fileEncoding)`.

### `ParsePatchBlocks`

```
input:
  PATCH Foo.cs          ← first line skipped
  FIND:
    old text
  REPLACE:
    new text
  FIND:                 ← second block starts
    old text 2
  REPLACE:
    new text 2
```

Cursor scans for `FIND:` / `REPLACE:` pairs. The end of a REPLACE block is either the next `FIND:` or end of string. Trailing `\r\n` or `\n` is stripped from both blocks.

### `NormalizeWithMap`

Collapses all `\s+` sequences to a single space. Returns `(string normalized, int[] normToOrig)` where `normToOrig[i]` maps normalized index `i` back to the original string position. Used to map exact match positions back to the original content for replacement.

---

## 8. Fuzzy Match Engine

**Entry point:** `FindFuzzyMatch(string content, string findText, string normFind, double threshold = 0.85)`

### Algorithm

1. Split `content` into lines with absolute char offsets `(start, end)`.
2. Determine window size = line count of `findText`.
3. Slide an N-line window over all lines. For each window:
   - Extract substring, normalize (collapse whitespace)
   - Compute `sim = 1.0 - LevenshteinDistance(normFind, normWindow) / max(len(normFind), len(normWindow))`
   - Track `bestSim` and `secondSim`
4. Return `null` if `bestSim < threshold` (85%)
5. Return `null` if `bestSim - secondSim < 0.05` (ambiguity guard — best must clearly outrank runner-up)
6. Return `(origStart, origEnd, similarity)`

**Gap threshold of 0.05** was tuned via debugger — a real ambiguous case had a gap of 0.0621 which was too close; 0.05 was chosen to reject it cleanly.

### Fuzzy Confirmation Flow

When fuzzy fires during `ApplyPatchAsync`:
1. Store all previously-validated blocks + file state in `_pendingFuzzyPatch`
2. Display matched text preview (truncated to 120 chars)
3. Append `[FUZZY] Fuzzy match — press 1 to apply or 2 to cancel.`
4. Focus `TerminalInputBox`
5. Return (PATCH suspended)

In `ExecuteTerminalInput`:
- `Key.D1` / `Key.NumPad1` → `ApplyPendingFuzzyPatch(pending)` — no Enter required (handled in `PreviewKeyDown`)
- `Key.D2` / `Key.NumPad2` → cancel, clear `_pendingFuzzyPatch`

`ApplyPendingFuzzyPatch` receives the full saved state: `(fullPath, fileEncoding, fileName, content, resolvedBlocks)` and applies all blocks in reverse order.

---

## 9. Undo / Backup Stack

- **Field:** `_patchBackupStack` — `Stack<(string originalPath, string backupPath)>`, capped at 10
- **Backup location:** `%TEMP%\DevMind\<guid>_<filename>`
- **On PATCH:** `File.Copy(fullPath, backupPath)` before write, then `_patchBackupStack.Push(...)`
- **On UNDO:** pop stack, `File.Copy(backupPath, originalPath)`, `File.Delete(backupPath)`
- **On Restart:** pop entire stack, delete all backup files

---

## 10. READ Context System

**Field:** `_readContext` — persists across multiple Ask calls until explicitly cleared.

**`ApplyReadCommandAsync(string input)`**

- Parses consecutive `READ <filename>` lines
- For each: calls `FindFileInSolutionAsync`, reads file, appends to `_readContext` formatted as:

```
[Context: Filename.cs]
<file contents>
[End Context: Filename.cs]
```

- `_readContext` is prepended to every LLM message in `SendToLlm`

**Slash commands** (handled in `RunShellCommand`):
- `/context` — lists loaded files from `_readContext` header lines
- `/context clear` — sets `_readContext = null`
- `/reload` — clears `_devMindContext` (forces DevMind.md reload on next Ask)

---

## 11. Agentic Loop (Auto-Execute)

During LLM streaming (`SendToLlm`), all visible tokens are accumulated in `responseBuffer` (a `StringBuilder`).

In the `onComplete` callback:
```csharp
var patchBlocks = ParsePatchBlocks(responseBuffer.ToString());
if (patchBlocks.Count > 0)
{
    AppendOutput($"[AUTO-PATCH] Detected {patchBlocks.Count} PATCH block(s) — executing...\n", OutputColor.Dim);
    await AutoExecutePatchAsync(responseBuffer.ToString());
}
```

`AutoExecutePatchAsync` calls `ApplyPatchAsync(llmResponse, clearInput: false)` — `clearInput: false` prevents it from clearing the input box (the user didn't type this PATCH, the LLM did).

**Think block filtering** — tokens between `<think>` and `</think>` are suppressed from the output stream and from `responseBuffer` via `FilterChunk` + `_inThinkBlock` state tracking.

---

## 12. New File Generation

`ExtractFileName(string prompt)` — looks for patterns like "create a file called Foo.cs", "new file named Bar.cs" in the prompt. Returns the filename or `null`.

When a target filename is detected:
1. A `fileGenBuffer` (`StringBuilder`) is used to accumulate the raw LLM output instead of the normal output stream
2. The LLM is instructed via an appended system note to output only raw source (no markdown fences)
3. Project namespace is read from DTE (`DefaultNamespace` property) and injected as a hint
4. In `onComplete`: `SaveGeneratedFileAsync(targetFileName, fileGenBuffer.ToString())`

`SaveGeneratedFileAsync`:
- Strips markdown fences if present
- Resolves destination directory from the active document's project
- Writes file
- Calls `dte.ItemOperations.AddExistingItem` to add to the project

---

## 13. Shell Command Execution

**`RunShellCommand(string command)`**

- Detects PowerShell via `IsPowerShellAvailable()` (checks `pwsh` then `powershell`)
- Launches process with `ProcessStartInfo`, `RedirectStandardOutput/Error`, working dir = `_terminalWorkingDir`
- Captures stdout + stderr, appends to output
- Working directory defaults to solution root; can be changed by `cd` commands (partially — not fully tracked)

**Built-in commands** (checked before shell execution):
- `/context`, `/context clear` — READ context management
- `/reload` — clear DevMind.md cache

**Terminal history** — every command is prepended to `_terminalHistory`. Up/Down in `TerminalInputBox_KeyDown` navigates history via `_terminalHistoryIndex`.

---

## 14. LLM Client

**`LlmClient.cs`** — targets `POST /v1/chat/completions` (OpenAI-compatible, LM Studio).

```csharp
StreamAsync(
    string systemPrompt,
    string userMessage,
    Action<string> onToken,
    Action onComplete,
    CancellationToken ct)
```

- Maintains `List<ChatMessage> _history` — full conversation included in every request
- Streams SSE (`data: {...}`) lines, parses `delta.content` tokens
- Thinking suppression: `["thinking"] = new JObject { ["type"] = "disabled" }`
- `ClearHistory()` — called on Restart

**Request shape:**
```json
{
  "model": "<from options>",
  "max_tokens": 4096,
  "stream": true,
  "thinking": { "type": "disabled" },
  "messages": [
    { "role": "system", "content": "<system prompt>" },
    { "role": "user", "content": "..." },
    { "role": "assistant", "content": "..." },
    ...
  ]
}
```

---

## 15. VSIX Build & Version Stamping Pipeline

Version format: `4.6.N` where N is a monotonically incrementing counter from `build.counter`.

### MSBuild Targets (in `DevMind.csproj`)

**`IncrementBuildCounter`** (`BeforeTargets="BeforeBuild"`):
1. Read `build.counter`, increment (wraps at 65535)
2. Write back to `build.counter`
3. Stamp `Properties\AssemblyInfo.cs` — replaces `AssemblyVersion` and `AssemblyFileVersion` with `4.6.N.0`
4. Stamp `source.extension.vsixmanifest` — replaces `<Identity Version=...>`
5. Stamp `extension.vsixmanifest` (bin copy) — same

**`StampVsixManifests`** (`BeforeTargets="CreateVsixContainer"`):
1. Re-read counter (no increment)
2. Re-stamp all manifest copies in `bin/` and `obj/`
3. Patch `manifest.json` in the obj folder via regex — this is the file the VSIX installer reads; VSSDK generates it from an intermediate copy, so it must be re-stamped after compile

**`build.counter`** must be committed to Git to persist across sessions.

### InstallationTargets

Both manifests declare:
```xml
<InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[17.0,20.0)" />
<InstallationTarget Id="Microsoft.VisualStudio.Pro" Version="[17.0,20.0)" />
```

### Banner Version

Read dynamically at startup:
```csharp
var version = Assembly.GetExecutingAssembly().GetName().Version;
string versionStr = $"{version.Major}.{version.Minor}.{version.Build}";
```

---

## 16. State Lifecycle

| State | Init | Cleared on Restart | Cleared on Clear |
|-------|------|--------------------|------------------|
| LLM conversation history | empty | ✅ `_llmClient.ClearHistory()` | ❌ |
| `_readContext` | null | ✅ | ❌ |
| `_devMindContext` | null | ✅ | ❌ |
| `_patchBackupStack` | empty | ✅ (files deleted) | ❌ |
| `_pendingFuzzyPatch` | null | ✅ | ❌ |
| `_terminalHistory` | empty | ❌ | ❌ |
| Output stream | banner | ✅ `InitOutputDocument()` | ✅ |

---

## 17. Adding a New Command

Example: adding a `FORMAT <filename>` command that runs `dotnet format` on a file.

1. **In `SendToLlm()`**, add a branch before the LLM call:
```csharp
if (text.StartsWith("FORMAT ", StringComparison.OrdinalIgnoreCase))
{
    await ApplyFormatAsync(text);
    return;
}
```

2. **Implement `ApplyFormatAsync(string input)`**:
```csharp
private async Task ApplyFormatAsync(string input)
{
    string fileName = input.Substring("FORMAT ".Length).Trim();
    string fullPath = await FindFileInSolutionAsync(Path.GetFileName(fileName), fileName);
    if (fullPath == null) { AppendOutput("[FORMAT] File not found.\n", OutputColor.Error); return; }
    
    RunShellCommand($"dotnet format --include \"{fullPath}\"");
}
```

3. **Update `CLAUDE.md` and `DevMind.md`** with the new command syntax.

---

## 18. Adding a New Directive (e.g. SHELL:)

`SHELL:` is planned as a first-class directive that executes a shell command and injects stdout into the LLM prompt as context.

**Planned implementation:**

In `SendToLlm()`, after READ processing, scan for `SHELL:` lines:
```csharp
// Process SHELL: directives — execute and inject output as context
var shellOutputs = new StringBuilder();
var remaining2Lines = new List<string>();
foreach (var line in allLines)
{
    if (line.TrimStart().StartsWith("SHELL:", StringComparison.OrdinalIgnoreCase))
    {
        string cmd = line.Substring(line.IndexOf("SHELL:", StringComparison.OrdinalIgnoreCase) + 6).Trim();
        string output = ExecuteShellCapture(cmd); // new: returns stdout as string
        shellOutputs.AppendLine($"[Shell: {cmd}]");
        shellOutputs.AppendLine(output);
        shellOutputs.AppendLine($"[End Shell: {cmd}]");
    }
    else
    {
        remaining2Lines.Add(line);
    }
}
if (shellOutputs.Length > 0)
    contextualMessage = shellOutputs.ToString() + contextualMessage;
text = string.Join("\n", remaining2Lines).Trim();
```

Add `ExecuteShellCapture(string command)` — same as `RunShellCommand` but returns stdout as a string instead of appending to output.

---

## 19. Testing

**Test harness:** `DevMindTestBed` — .NET 8 Console App at  
`C:\Users\pkailas.KAILAS\source\repos\DevMindTestBed\`

**`TestBed.cs`** is the scratch file used for PATCH/fuzzy tests. Current content:
```csharp
public class TestBed
{
    public void MethodA() { var x = 1; }
    public void MethodB() { var y = 777; }
    public void MethodC() { var x = 1; }
}
```

### Standard Test Matrix

| Test | How to trigger |
|------|----------------|
| Basic PATCH | PATCH TestBed.cs with exact match |
| Fuzzy PATCH | Use `var y=777;` (no spaces) — forces fuzzy |
| Fuzzy confirm Apply | Press 1 in terminal strip after fuzzy fires |
| Fuzzy confirm Cancel | Press 2 in terminal strip |
| Multi-block PATCH | Two FIND:/REPLACE: pairs in one PATCH |
| UNDO | Apply PATCH, then type UNDO |
| UNDO empty stack | Type UNDO with no prior PATCH |
| READ + Ask | READ TestBed.cs then ask a question |
| /context | Type /context in terminal strip |
| /context clear | Type /context clear |
| Agentic loop | Ask LLM to make a change — auto-apply fires |
| New file | "Create a new file called Foo.cs with..." |
| /shell command | /Get-Item ... in input box |
| Shell in terminal strip | Get-Item ... in terminal strip |
| VSIX upgrade | Build, install over existing — no uninstall required |

### Rebuilding & Installing

1. `Build → Build Solution` (or `Ctrl+Shift+B`)
2. `Build → Deploy DevMind` or press F5 to launch Experimental Instance
3. For production install: find `.vsix` in `bin\Debug\` or `bin\Release\`, double-click

---

## 20. Known Limitations & Future Work

### Current Limitations

- **Bottom-anchored output** — `ScrollToEnd()` with deferred `BeginInvoke` approximates bottom anchoring but text still renders from the top on first load. True bottom anchoring in WPF RichTextBox requires a different layout approach.
- **Working directory tracking** — `cd` commands in the terminal strip do not update `_terminalWorkingDir`. Each shell command spawns a new process.
- **Single-file PATCH per command** — PATCH operates on one file per command. Multiple files require multiple PATCH commands.
- **Fuzzy match: single-block suspension** — when a multi-block PATCH hits fuzzy on block N, the already-validated blocks 1..N-1 are stored in `_pendingFuzzyPatch`. If the user cancels (2), those blocks are discarded rather than applied.

### Planned Features

| Feature | Description |
|---------|-------------|
| `SHELL:` directive | Execute shell command inline, inject stdout as LLM context |
| Build error feedback cycle | `SHELL: dotnet build` → Qwen sees errors → generates PATCH → auto-executes → rebuild |
| Levenshtein escalation | Exact → fuzzy → LLM advisor with diff preview before apply |
| Working dir tracking | Parse `cd` commands and update `_terminalWorkingDir` |
| Bottom-anchored output | True terminal-style text anchoring |

---

*DevMind is a product of iOnline Consulting LLC. For support, see the User Guide.*
