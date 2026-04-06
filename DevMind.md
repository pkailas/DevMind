# DevMind — Project Context v7.0

## Project
- **Product**: DevMind v7.0.180
- **Company**: iOnline Consulting LLC
- **Platform**: C#, WPF, .NET Framework 4.8
- **Solution**: DevMind.slnx

## Context
DevMind is a Visual Studio extension that provides an autonomous local LLM coding assistant embedded directly in the IDE. Developers use it to read, analyze, and modify code files through natural language commands. The extension integrates with OpenAI-compatible local LLM servers (LM Studio, llama-server, Ollama) and provides file system access, shell command execution, and build automation through a structured directive system.

**Trust Model**: The user is a developer who intentionally configures all behavior — user-authored prompts and directives are trusted. The LLM acts as an agent executing the user's intent. File writes require explicit user confirmation for files not already known to the task, ensuring the user maintains control over all modifications.

## Architecture

### Entry Points
- **`DevMindPackage.cs`** — VSIX package entry point. Registers the tool window, options page, and menu commands with Visual Studio shell on initialization.
- **`DevMindCommand.cs`** — Command handler for Ctrl+Alt+D shortcut. Shows the DevMind tool window.
- **`DevMindToolWindow.cs`** — Tool window factory. Creates the `DevMindToolWindowControl` with an `LlmClient` instance configured from user settings.

### Core Components

#### UI Layer
- **`DevMindToolWindowControl.xaml.cs`** — Main WPF control hosting the chat interface. Handles user input, LLM response streaming, and coordinates the agentic loop. Manages profile selection, system prompt editing, and training logging.
- **`DevMindToolWindowControl.AgenticHost.cs`** — Implements `IAgenticHost` interface. Provides file read/write operations, shell command execution, and patch application. Contains `DiffHelper` for unified diff generation.
- **`DevMindToolWindowControl.Context.cs`** — Context loading logic. Discovers and loads `DevMind.md`, `.agent.md` profiles, handles `READ` commands, extracts C# outlines for large files.
- **`DevMindToolWindowControl.Patch.cs`** — PATCH directive handling. Parses FIND/REPLACE blocks, performs whitespace-normalized matching with fuzzy fallback (Levenshtein), applies patches with backup/undo support.
- **`DevMindToolWindowControl.Shell.cs`** — Shell command execution. Runs PowerShell commands, captures output, parses TRX test result XML files.

#### Agentic Loop
- **`AgenticExecutor.cs`** — Executes parsed actions from LLM responses. Orchestrates the agentic loop: applies patches, runs shell commands, creates files, loads context, feeds results back to LLM. Supports batch patch operations with diff preview cards.
- **`AgenticActionResolver.cs`** — Static resolver that evaluates `ResponseOutcome` and `ExecutionResult` to determine the next action type (Continue, Stop, Retry, LoadAndResubmit, etc.).
- **`AgenticAction.cs`** — Data class representing the next action in the agentic loop.
- **`ExecutionResult.cs`** — Captures execution outcomes (patches applied, shell exit codes, files created) for feedback into the next iteration.

#### Response Parsing
- **`ResponseParser.cs`** — State machine parser for LLM responses. Extracts structured blocks: `FILE:`, `PATCH`, `READ`, `SHELL:`, `GREP`, `FIND`, `DELETE`, `RENAME`, `DIFF`, `TEST`, `SCRATCHPAD`, `DONE`.
- **`ResponseClassifier.cs`** — Wraps `ResponseParser` output into `ResponseOutcome` with pre-computed flags (HasPatches, HasShellCommands, etc.).
- **`ResponseOutcome.cs`** — Classification result with actionable block list and boolean flags for quick decision-making.

#### LLM Communication
- **`LlmClient.cs`** — HTTP client for OpenAI-compatible `/v1/chat/completions` endpoints. Handles context size detection, token budget tracking, streaming responses, tool call parsing, and context compaction.
  - **`ContextBudget`** — Tracks token allocation across buckets (system prompt, protected turns, working history).
  - **`NearlineCache`** — In-memory cache for content trimmed by `MicroCompactToolResults`, allowing instant recall.
  - **`FileContentCache`** — Caches file content for efficient line-range reads.
- **`ToolCallResult.cs`** — Unified representation of tool calls extracted from LLM responses.
- **`ToolCallMapper.cs`** — Maps `ToolCallResult` objects to `ResponseBlock` instances for the executor.
- **`ToolRegistry.cs`** — Builds the OpenAI-compatible `tools` JSON array for structured tool calling.

#### Configuration & Profiles
- **`DevMindOptionsPage.cs`** — Options page model with settings for endpoint, API key, model name, timeouts, context eviction, directive mode, and training logging. Supports named profiles via `ProfileManager`.
- **`ProfileManager.cs`** — Manages named connection profiles stored in JSON. Supports create, read, update, delete, duplicate, and activate operations.
- **`ProfileData.cs`** — Data class representing a single profile.

#### Memory System
- **`MemoryManager.cs`** — Cross-session memory via `MEMORY.md` index and topic files in `.devmind/memory/`. Supports `recall_memory`, `save_memory`, `list_memory` directives.

#### Diff Preview UI
- **`DiffPreviewCard.xaml.cs`** — Inline diff preview card with Apply/Skip buttons. Shows unified diff with ±3 lines of context.
- **`DiffBatchBar.xaml.cs`** — Batch control bar for applying/skipping all pending diff cards at once.

#### Supporting Types
- **`PatchConfidence.cs`** — `PatchConfidence` enum (Exact, Fuzzy) and `PatchResolveResult` class for resolved patch data.
- **`OutputColor.cs`** — Color categories for output panel text (Normal, Dim, Input, Error, Success, Thinking, Warning).
- **`PackageIds.cs`** — Package GUID and command ID constants.
- **`StringValidator.cs`** — Simple string validation utility.

#### Training & Diagnostics
- **`TrainingLogger.cs`** — Captures fine-tuning training data as JSONL. Logs turns with tool calls, tool results, metrics, and outcome classification.

### Data Flow

1. **User Input**: User types a prompt in the chat input box and presses Enter.
2. **Context Assembly**: `DevMindToolWindowControl` loads `DevMind.md`, selected editor context, and system prompt.
3. **LLM Request**: `LlmClient.SendMessageAsync` sends the conversation to the LLM endpoint with streaming enabled.
4. **Response Streaming**: Tokens are streamed and displayed in the output panel. Thinking tokens (``) are optionally filtered.
5. **Block Parsing**: `ResponseParser.Parse` extracts structured blocks from the complete response.
6. **Action Resolution**: `AgenticActionResolver.Resolve` determines the next action based on parsed blocks and execution history.
7. **Execution**: `AgenticExecutor.ExecuteAsync` performs the action (apply patches, run shell, create files, etc.).
8. **Feedback Loop**: Execution results are fed back to the LLM, and the loop continues until depth limit or completion.

### Build Pipeline

The `.csproj` defines a custom build pipeline:
1. **`IncrementBuildCounter`** (Before `BeforeBuild`): Increments `build.counter`, stamps version into `AssemblyInfo.cs`, `source.extension.vsixmanifest`, and `manifest.json`.
2. **Compile**: Standard .NET SDK compilation.
3. **`StampVsixManifests`** (Before `CreateVsixContainer`): Re-applies version stamps to manifests before VSIX packaging.

## Key Patterns

### Error Handling
- **Async/Await**: All I/O operations use `async/await` with `CancellationToken` support.
- **Try-Catch with Logging**: Errors are caught and displayed in the output panel using `AppendOutput(..., OutputColor.Error)`.
- **Graceful Degradation**: File not found, patch mismatch, and shell command failures are reported non-fatally to allow continuation.

### Configuration Loading
- **Community.VisualStudio.Toolkit**: Uses `BaseOptionModel<T>` for settings persistence with profile support.
- **ProfileManager**: Profiles stored as JSON in `%LOCALAPPDATA%\DevMind\profiles.json`.

### COM Interop
- **Visual Studio SDK**: Uses `Microsoft.VisualStudio.Shell` and `EnvDTE` for editor integration (document access, solution exploration).
- **`ReloadDocumentFromDisk`**: Forces VS to reload document buffers from disk after patch application.

### External Tool Usage
- **MSBuild Discovery**: `FindMSBuildPath` checks `VSINSTALLDIR` env var, then `vswhere.exe`, then fallback paths.
- **PowerShell Execution**: `RunShellCommandCaptureAsync` uses `PowerShell` class for command execution with output capture.
- **TRX Parsing**: `ParseTrxSummary` extracts test results from Visual Studio Test XML format.

### Threading Model
- **UI Thread**: All WPF UI updates occur on the UI thread.
- **Background Work**: LLM communication, file I/O, and shell execution run on background threads via `async/await`.
- **Cancellation**: `CancellationToken` passed through the agentic loop for user-initiated cancellation (Stop button).

### Validation
- **Whitespace-Normalized Matching**: PATCH FIND blocks are matched after collapsing all whitespace runs to single spaces.
- **Fuzzy Fallback**: If exact match fails, Levenshtein similarity scoring with sliding window finds best match (≥85% threshold).
- **File Write Guard**: Files not already known to the task require user confirmation before writing.

### Context Management
- **Token Budgeting**: `ContextBudget` allocates tokens across buckets (system prompt, protected turns, working history, response headroom).
- **Tiered Eviction**: `EvictStaleContext` drops old messages based on age relative to current turn.
- **MicroCompact**: Trims stale tool result content when working budget exceeds threshold (default 85%).
- **Brainwash**: Full context replacement as last resort when compaction thrashing is detected.

## File Summary

| File | Lines | Purpose |
|------|-------|---------|
| `AgenticAction.cs` | 70 | Action types and data class for agentic loop decisions |
| `AgenticActionResolver.cs` | 105 | Resolves next action from response outcome and execution result |
| `AgenticExecutor.cs` | 761 | Executes actions in the agentic loop (patches, shell, files) |
| `DevMindCommand.cs` | 22 | Command handler for showing the tool window |
| `DevMindOptionsPage.cs` | 544 | Options page model with LLM settings and profile management |
| `DevMindPackage.cs` | 38 | VSIX package entry point |
| `DevMindToolWindow.cs` | 56 | Tool window factory and pane definition |
| `DevMindToolWindowControl.AgenticHost.cs` | 1380 | IAgenticHost implementation for file/shell operations |
| `DevMindToolWindowControl.Context.cs` | 955 | Context loading (DevMind.md, agent profiles, READ commands) |
| `DevMindToolWindowControl.Patch.cs` | 1074 | PATCH directive parsing, matching, and application |
| `DevMindToolWindowControl.Shell.cs` | 364 | Shell command execution and TRX parsing |
| `DevMindToolWindowControl.xaml.cs` | 2395 | Main WPF control with chat UI and agentic loop coordination |
| `DiffBatchBar.xaml.cs` | 52 | Batch control bar for applying/skipping all diff cards |
| `DiffPreviewCard.xaml.cs` | 244 | Inline diff preview card with Apply/Skip buttons |
| `ExecutionResult.cs` | 56 | Captures execution outcomes for feedback loop |
| `FileContentCache.cs` | 66 | In-memory cache for file content and line-range reads |
| `IAgenticHost.cs` | 173 | Interface abstracting side effects from agentic logic |
| `LlmClient.cs` | 3165 | HTTP client for LLM communication with context management |
| `MemoryManager.cs` | 250 | Cross-session memory via MEMORY.md and topic files |
| `NearlineCache.cs` | 101 | Cache for content trimmed by MicroCompact |
| `OutputColor.cs` | 21 | Color categories for output panel text |
| `PackageIds.cs` | 20 | Package GUID and command ID constants |
| `PatchConfidence.cs` | 37 | Patch confidence enum and resolve result class |
| `ProfileManager.cs` | 388 | Named connection profile management |
| `ResponseClassifier.cs` | 36 | Wraps ResponseParser output into ResponseOutcome |
| `ResponseOutcome.cs` | 138 | Classification result with actionable blocks and flags |
| `ResponseParser.cs` | 993 | State machine parser for LLM response blocks |
| `StringValidator.cs` | 7 | Simple string validation utility |
| `ToolCallMapper.cs` | 225 | Maps ToolCallResult to ResponseBlock |
| `ToolCallResult.cs` | 27 | Unified tool call representation |
| `ToolRegistry.cs` | 230 | Builds OpenAI-compatible tools JSON array |
| `TrainingLogger.cs` | 339 | Captures fine-tuning training data as JSONL |
| `Properties\AssemblyInfo.cs` | 26 | Assembly metadata and versioning |

## Known Issues

None identified. (No TODO, HACK, or FIXME comments found in the source code.)

## Dependencies

### NuGet Packages
- `Microsoft.VisualStudio.SDK` v17.0.32112.339 — Visual Studio SDK for extension development
- `Microsoft.VSSDK.BuildTools` v17.14.2120 — Build tools for VSIX packaging
- `Community.VisualStudio.Toolkit.17` v17.0.549 — Community toolkit for VS extensions
- `Newtonsoft.Json` v13.0.1 — JSON serialization
- `Microsoft.Extensions.Http` v3.1.8 — HTTP client abstraction

### External Tools (Runtime)
- **MSBuild.exe** — Discovered via `VSINSTALLDIR`, `vswhere.exe`, or fallback paths for building .NET projects
- **PowerShell** — Used for shell command execution
- **vswhere.exe** — Used for MSBuild discovery (optional)

### Core Function
The project uses no third-party SDKs for its core LLM agent functionality — all parsing, execution, and context management logic is custom-built.

## Coding Standards

- **Language version**: C# 8.0 — no newer syntax (targeting .NET Framework 4.8)
- **Naming**: 
  - PascalCase for public types and members
  - `_camelCase` for private fields
  - `static class` for utility types with only static methods
- **Error handling**: Try-catch with output panel logging; non-fatal errors reported as warnings
- **Thread model**: 
  - UI updates on UI thread
  - Background work via `async/await` with `CancellationToken`
  - No explicit thread pooling or task parallelism
- **Build**: `dotnet build` or MSBuild with auto-incremented build counter
- **Documentation**: XML doc comments on public APIs; inline comments for complex logic
- **File organization**: Partial classes used for `DevMindToolWindowControl` to separate concerns (AgenticHost, Context, Patch, Shell)
- **Versioning**: Build counter incremented on each build, stamped into assembly and VSIX manifests

## Efficiency Guidelines

- READ files in full unless they exceed 1000 lines.
- Use parallel tool calls to read multiple files in one turn when possible.
- Write findings incrementally with append_file — do not accumulate large outputs in context.
- Do not re-read output files to verify — trust the tool result confirmation.
- Move to the next step immediately after each action completes.
- Prefer creating a file once with full content over multiple small appends when the content is ready.