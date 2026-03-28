# DevMind Project Context  v1.2

## Platform & Constraints
- VSIX (.NET Framework net48), C# 8.0 only — no C# 9+ syntax (no records, no file-scoped namespaces, no collection expressions, no init-only setters, no top-level statements, no primary constructors)
- UI: WPF inside a ToolWindowPane
- Threading: All VS API calls require SwitchToMainThreadAsync() first
- Async void: only for VS event handlers, must have try/catch wrapping entire body
- PowerShell is the shell — use `;` to chain commands, not `&&`

## Architecture
- DevMindToolWindowControl.xaml.cs — main logic, partial classes split into:
  - .Patch.cs — PATCH parsing, fuzzy matching, ApplyPatchAsync, UNDO
  - .Shell.cs — RunShellCommand, RunShellCommandCaptureAsync, shell routing
  - .Context.cs — FindFileInSolutionAsync, READ handling, editor context, outline generation
- LlmClient.cs — HTTP streaming client, context budget management, 5-phase compression system
- ResponseParser.cs — parses LLM response into FILE/PATCH/SHELL/READ/SCRATCHPAD blocks
- DevMindOptionsPage.cs — VS Tools > Options (Connection, Model, Prompt, Agentic Loop, Context Management, Display)
- FileContentCache.cs — LRU file content cache for READ range access

## LLM Backend
- Local model via llama-server at http://127.0.0.1:1234 (OpenAI-compatible)
- Context: 32,768 tokens with quantized KV cache (K: q4_0, V: q4_0)
- Server type detection supports llama-server, LM Studio, and Custom endpoints

## Context Management (5-Phase Compression)
- Phase 0a: Deduplicate stale READ blocks
- Phase 0b: Collapse PATCH chains (keep only newest snapshot per file)
- Phase 0c: Compress shell output older than 1 prior turn
- Phase 4 (soft pressure): Squeeze PATCH, SHELL, READ blocks
- Phase 2 (hard pressure): Sliding window trim to MaxConversationTurns
- Level 3 Budget Guard: Truncate oversized READ blocks in current message
- Post-turn: Outline-compress READ blocks after LLM responds

## Coding Conventions
- File headers: `// File: FileName.cs  vX.Y` — ALWAYS bump version on changes
- Code-behind only, no MVVM
- No external NuGet beyond VSSDK and Community Toolkit
- Namespace: DevMind
- Block-scoped namespaces (required by C# 8.0)

## Build Command
VSIX projects require msbuild, NOT dotnet build. Use this exact command:

```
dotnet msbuild "C:\Users\pkailas.KAILAS\source\repos\DevMind\DevMind.csproj" /p:DeployExtension=false /verbosity:minimal
```

If `dotnet msbuild` is unavailable, use the full VS2022 path:
```
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\pkailas.KAILAS\source\repos\DevMind\DevMind.csproj" /p:DeployExtension=false /verbosity:minimal
```

Never use `dotnet build` — it fails with VSIX deployment errors.

## Directive Protocol

Every response in an agentic turn MUST end with at least one directive. Prose conclusions without directives will be rejected and retried. The valid directives are:

| Directive | Purpose |
|-----------|---------|
| `PATCH FileName.cs` | Edit an existing file using FIND/REPLACE blocks |
| `FILE: path/to/file.cs` ... `END_FILE` | Create or fully replace a file |
| `SHELL: command` | Run a shell command (PowerShell) |
| `READ FileName.cs` | Load a file or line range into context |
| `GREP: "pattern" file` | Search file for pattern matches — Information-gathering, same as READ |
| `FIND: "pattern" *.cs` | Cross-file search by glob — returns filename:line: content for each match |
| `DELETE filename.cs` | Delete a file from disk — use only when explicitly asked to remove a file |
| `RENAME OldFile.cs NewFile.cs` | Rename a file on disk — does not update references in other files |
| `SCRATCHPAD:` ... `END_SCRATCHPAD` | Internal reasoning state — not shown to user |
| `DONE` | Signal task completion — only emit when all steps are verified complete |

### PATCH Format
```
PATCH FileName.cs
FIND:
<exact text to find — must match file content verbatim>
REPLACE:
<new text to substitute>
END_PATCH
```
Multiple PATCH blocks are allowed in one response. Each is applied in order.

### READ-Before-Assess Rule
**Never assess the state of a file from its outline alone.** An outline shows method signatures but not whether XML doc comments, implementations, or logic are present. Before concluding anything about file content, issue ranged READs to verify:

```
READ FileName.cs:1-50
READ FileName.cs:51-100
```

Violating this rule causes hallucinated conclusions ("all methods are documented") that contradict actual file content.

### Outline-Guided Navigation
When you READ a large file and receive an outline, use the line numbers to target your next READ.

BAD — sequential scanning:
  READ LlmClient.cs:1-200
  READ LlmClient.cs:201-400
  READ LlmClient.cs:401-600

GOOD — outline-guided:
  READ LlmClient.cs              → outline shows "  587: private void TrimHistoryToFit("
  READ LlmClient.cs:580-620      → targeted read around the method you need
  PATCH LlmClient.cs             → apply the change

If the outline doesn't show what you need, use GREP to find the exact line, then READ a targeted range around it.

GOOD — GREP-guided:
  GREP: "TrimHistoryToFit" LlmClient.cs  → line 587
  READ LlmClient.cs:580-620
  PATCH LlmClient.cs

Never scan a file in sequential 200-line chunks. Always use outline line numbers or GREP first.

### GREP — Search File for Pattern
```
GREP: "pattern" filename
GREP: "pattern" filename:100-200
```

Searches for lines containing the pattern (case-insensitive substring match).
Returns matching lines with absolute line numbers. Use GREP to locate code before doing a targeted READ.

**Workflow:**
1. `GREP: "SaveFileAsync" AgenticExecutor.cs`  → finds lines 42, 89, 155
2. `READ AgenticExecutor.cs:85-100`             → reads context around line 89
3. `PATCH AgenticExecutor.cs`                   → applies the change

**Rules:**
- Pattern must be in double quotes.
- Results capped at 50 matches — narrow your pattern or add a line range if truncated.
- Prefer GREP + targeted READ over sequential full-file READs.

### DELETE — Remove File
```
DELETE filename.cs
```

Deletes a file from disk. Closes the file in the VS editor first if it is open.
Does **not** modify `.csproj` or other project references.

**Rules:**
- Use only when the task explicitly requires file removal.
- Do not use DELETE speculatively.
- After DELETE, emit DONE or SHELL: to build/verify as appropriate.

### RENAME — Rename/Move File
```
RENAME OldFile.cs NewFile.cs
```

Renames a file on disk. The old file is closed in the VS editor and the new file is opened after rename.
Does **not** update references in other files — use FIND + PATCH to update imports or usings if needed.

**Rules:**
- Use only when the task explicitly requires a file rename or move.
- If the destination already exists, RENAME is rejected with an error — no overwrite.
- After RENAME, use FIND + PATCH to update any references to the old filename if needed.

### FIND — Cross-File Search
```
FIND: "pattern" *.cs
FIND: "pattern" Services/*.cs
```

Searches all files matching the glob for lines containing the pattern (case-insensitive substring match).
Returns `filename:line: content` for each match across all matching files.

**Rules:**
- Pattern must be in double quotes.
- Results capped at 100 matches total across all files — narrow your pattern if truncated.
- Optional `:start-end` range restricts search within each file (e.g. `FIND: "foo" *.cs:100-200`).
- Use FIND when you need to know where something is used across the project.
- Use GREP when you already know which file to search.

**Workflow:**
1. `FIND: "IAgenticHost" *.cs`  → shows all files that reference the interface
2. `READ AgenticExecutor.cs:40-60`   → targeted read around the hit you need
3. `PATCH AgenticExecutor.cs`        → applies the change

### Nothing-To-Do Case
If after reading the file you determine no changes are needed, emit DONE with an explanation — never emit prose without a directive:

```
DONE
All methods in AgenticExecutor.cs already have XML doc comments. No changes required.
```

### Task-Complete Case
After a successful build with zero errors, emit DONE:

```
DONE
Applied XML doc comments to constructor and ExecuteLoadAndResubmitAsync. Build succeeded (6.0.54.0).
```

### SCRATCHPAD Usage
Use SCRATCHPAD to track multi-step task state across agentic iterations:

```
SCRATCHPAD:
Goal: Add XML doc comments to AgenticExecutor.cs
Status: READ complete — constructor and ExecuteLoadAndResubmitAsync missing docs
Next: PATCH both methods, then SHELL build
END_SCRATCHPAD
```

Update SCRATCHPAD at the start of each iteration to reflect current status.

## Key Parser Notes
- SCRATCHPAD terminated by END_SCRATCHPAD (not ---)
- PATCH separator --- between blocks stripped by AutoExecutePatchAsync
- ParsePatchBlocks uses context-aware --- detection to avoid stripping file content

## Testing
- DevMindTestBed (.NET 10 console app) at C:\Users\pkailas.KAILAS\source\repos\DevMindTestBed\
- TestBed.cs is the scratch file for PATCH and agentic loop testing
- Test with experimental VS instance (F5 from DevMind project)

## Future Roadmap

### VS Copilot .agent.md Compatibility (Bookmarked — March 2026)
Microsoft announced custom agents for VS Copilot defined as `.agent.md` files in `.github/agents/`. Format is preview and may change. Once stabilized, DevMind should evaluate supporting this format so teams can share agent definitions between Copilot (cloud) and DevMind (local). Key considerations:
- Scan `.github/agents/*.agent.md` via existing DTE project detection
- Parse markdown into system prompt context for the local LLM
- Map Copilot tool names to DevMind directives (SHELL/READ/PATCH)
- Ignore model field (DevMind always uses local model)
- MCP tool references would need a stub/warning until MCP support is added
- Value prop: no vendor lock-in, same agent configs work locally and in cloud

Source: https://devblogs.microsoft.com — VS Copilot custom agents announcement, March 2026
