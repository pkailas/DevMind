# DevMind — Project Context v7.0

## Project
- **Product**: DevMind v7.0.181
- **Company**: iOnline Consulting LLC
- **Platform**: C#, WPF, .NET Framework 4.8
- **Solution**: Single-project VSIX extension (no .sln file)

## Context
DevMind is a Visual Studio extension that provides a local LLM-powered coding assistant interface. It enables developers to interact with local LLM servers (LM Studio, Ollama, llama.cpp) to read code files, apply patches, create new files, run shell commands, and perform autonomous multi-step coding tasks. The extension integrates directly with the Visual Studio shell, providing a tool window for chat interaction and seamless file operations within the IDE.

**Trust Model**: The user is a developer who intentionally configures all behavior — user-authored content is not untrusted input. The LLM is a local, user-controlled service; all file operations require explicit user confirmation (diff preview gate) unless the file is known to the current task.

## Architecture

### Entry Points
- **`DevMindPackage.cs`** — VSIX package entry point. Registers the tool window, options page, and menu commands with the Visual Studio shell via `ToolkitPackage`. Initialization happens in `InitializeAsync()`.
- **`DevMindCommand.cs`** — Command handler registered under `View > Other Windows > DevMind` with shortcut `Ctrl+Alt+D`. Shows the tool window.
- **`DevMindToolWindow.cs`** — Tool window pane that creates the `DevMindToolWindowControl` and initializes the `LlmClient` with configured endpoint settings.

### Core Components

#### UI Layer
- **`DevMindToolWindowControl.xaml.cs`** — Main WPF control hosting the chat interface. Handles user input, LLM response streaming, output rendering, and coordinates the agentic loop. Contains:
  - `SendToLlm()` — Main entry point for user messages
  - `ProcessBatchInputAsync()` — Handles batched multi-directive inputs
  - `SaveGeneratedFileAsync()` — Writes FILE: blocks to disk
  - `BuildToolUsePrompt()` — Constructs the system prompt (tool catalog, behavioral rules, build config)
  - `FindMSBuildPath()` — Discovers MSBuild.exe at runtime

- **`DevMindToolWindowControl.AgenticHost.cs`** — Partial class implementing `IAgenticHost`. Provides:
  - File write guards (`IsFileKnownToTask()`, `ConfirmUnreadFileWriteAsync()`)
  - TRX test result parsing (`ParseTrxSummary()`)
  - File not found message builder (`BuildFileNotFoundMessageAsync()`)
  - `DiffHelper` — Unified diff generation for patch previews

- **`DevMindToolWindowControl.Context.cs`** — Partial class handling context loading:
  - `LoadDevMindContextAsync()` — Discovers and loads `DevMind.md` or `.agent.md` files
  - `GetEditorContextAsync()` — Captures selected text and active file from VS editor
  - `ApplyReadCommandAsync()` — Handles `READ:` directives
  - `ExtractCSharpOutline()` — Generates class/method outlines for large files

- **`DevMindToolWindowControl.Patch.cs`** — Partial class handling PATCH operations:
  - `ResolvePatchAsync()` — Resolves PATCH blocks without applying
  - `ApplyResolvedPatchAsync()` — Applies a resolved patch after diff preview confirmation
  - `ApplyPatchAsync()` — Full patch application with fuzzy matching fallback
  - `FindFuzzyMatch()` — Levenshtein-based fuzzy matching for near-misses
  - `BuildPatchDiffView()` — Generates ±3 line context diff for preview cards

- **`DevMindToolWindowControl.Shell.cs`** — Partial class for shell command execution:
  - `RunShellCommandCaptureAsync()` — Executes PowerShell commands and captures output
  - `SanitizeShellCommand()` — Collapses newlines in quoted strings
  - `ParseShellDirectives()` — Extracts SHELL: commands from responses

- **`DiffPreviewCard.xaml.cs`** — WPF control for inline diff preview. Shows ±3 lines of context, awaits user Apply/Skip decision via `TaskCompletionSource<bool>`.
- **`DiffBatchBar.xaml.cs`** — Batch control bar for multiple diff cards (Apply All / Skip All).

#### LLM Client
- **`LlmClient.cs`** — HTTP client for OpenAI-compatible `/v1/chat/completions` endpoints. Key features:
  - `SendMessageAsync()` — Streams LLM responses with token callbacks
  - `DetectContextSizeAsync()` — Auto-detects context window from server endpoints
  - `MicroCompactToolResultsAsync()` — Trims stale tool results with semantic summarization
  - `BrainwashContextAsync()` — Full context replacement when compaction thrashes
  - `EvictStaleContext()` — Age-based message eviction
  - `ContextBudget` — Token budget tracking across buckets (system, protected, working)
  - `NearlineCache` — RAM cache for trimmed content (instant recall)

#### Agentic Loop
- **`AgenticExecutor.cs`** — Executes `AgenticAction` by calling `IAgenticHost` methods. Contains:
  - `ExecuteAsync()` — Main execution loop, iterates through response blocks
  - `ExecuteBlocksAsync()` — Processes individual blocks (PATCH, FILE, SHELL, READ, etc.)
  - `ExecuteBatchPatchesAsync()` — Resolves all patches, shows diff preview cards, awaits user decisions
  - `ExecuteLoadAndResubmitAsync()` — Loads files and resubmits original prompt

- **`AgenticActionResolver.cs`** — Pure static resolver that evaluates `ResponseOutcome` and `ExecutionResult` to decide the next action (`ApplyAndBuild`, `RunShell`, `CreateFile`, `LoadAndResubmit`, `ContinueAgentic`, `RetryWithCorrection`, `Stop`, `AskUser`).

- **`ResponseParser.cs`** — State machine parser for LLM responses. Recognizes blocks:
  - `FILE:` / `END_FILE` — File creation
  - `PATCH` / `END_PATCH` — File modifications with FIND/REPLACE pairs
  - `SHELL:` — Shell command execution
  - `READ` / `READ!` — File read requests (with optional line ranges)
  - `GREP:` / `FIND:` — Text search in files
  - `DELETE` / `RENAME` / `DIFF` / `TEST` — File operations
  - `SCRATCHPAD:` / `END_SCRATCHPAD` — State tracking
  - `DONE` / `task_done` — Task completion
  - `RECALL_MEMORY` / `SAVE_MEMORY` / `LIST_MEMORY` — Cross-session memory

- **`ResponseClassifier.cs`** — Wraps `ResponseParser.Parse()` to produce `ResponseOutcome` with pre-computed action flags.

- **`AgenticAction.cs`** — Data class describing the next action in the agentic loop.

- **`ExecutionResult.cs`** — Captures what happened during execution (patches applied, shell exit code, files created, errors).

#### Tool Calling
- **`ToolCallMapper.cs`** — Converts `List<ToolCallResult>` (from LLM tool calls) into `List<ResponseBlock>` for the executor.
- **`ToolCallResult.cs`** — Unified representation of a single tool call (ID, name, arguments, thinking text).
- **`ToolRegistry.cs`** — Builds the OpenAI-compatible `tools` JArray for structured tool calling. Defines 15 tools: `read_file`, `patch_file`, `create_file`, `run_shell`, `grep_file`, `find_in_files`, `delete_file`, `rename_file`, `diff_file`, `run_tests`, `recall_memory`, `save_memory`, `list_memory_topics`, `scratchpad`, `task_done`.

#### Configuration & Profiles
- **`DevMindOptionsPage.cs`** — Options page under `Tools > Options > DevMind`. Contains:
  - `DevMindOptions` — Settings model (endpoint URL, API key, model name, context eviction, directive mode, etc.)
  - `LlmServerType` — Enum for server type (LlamaServer, LMStudio, Ollama, Custom)
  - `ContextEvictionMode` — Aggressiveness of context compaction
  - `DirectiveMode` — TextDirective vs ToolCalling communication
  - Profile management (save, load, delete, rename profiles)

- **`ProfileManager.cs`** — Manages named connection profiles stored in `profiles.json`. Provides CRUD operations and profile switching.

#### Memory System
- **`MemoryManager.cs`** — Cross-session memory via `.devmind/` folder:
  - `MEMORY.md` — Index of topics
  - `.devmind/<topic>.md` — Individual topic files
  - Operations: `LoadIndex()`, `LoadTopic()`, `SaveTopic()`, `ListTopics()`, `DeleteTopic()`

#### Caching
- **`FileContentCache.cs`** — In-memory cache for recently read files. Supports line-range retrieval.
- **`NearlineCache.cs`** — RAM cache for content trimmed from context by `MicroCompactToolResults`. Allows instant recall.

#### Training Data Capture
- **`TrainingLogger.cs`** — Captures fine-tuning training data as JSONL. Logs each turn with:
  - System prompt, user message, assistant response
  - Tool calls and tool results
  - Metrics (tokens, context %, iteration count)
  - Outcome classification (success, partial, error, etc.)

#### Utilities
- **`OutputColor.cs`** — Enum for output panel text colors (Normal, Dim, Input, Error, Success, Thinking, Warning).
- **`PackageIds.cs`** — Package GUID and command ID constants.
- **`PatchConfidence.cs`** — Enum (`Exact`, `Fuzzy`) and `PatchResolveResult` class for patch resolution results.
- **`StringValidator.cs`** — Simple string validation utility.
- **`ResponseOutcome.cs`** — Wrapper around parsed blocks with boolean flags for action types.

### Data Flow
1. User types message in tool window → `SendToLlm()` constructs system prompt + user message
2. `LlmClient.SendMessageAsync()` streams response tokens
3. Response is parsed by `ResponseParser` into `ResponseBlock`s
4. `ResponseClassifier` produces `ResponseOutcome` with action flags
5. `AgenticActionResolver` decides next action based on outcome + previous `ExecutionResult`
6. `AgenticExecutor.ExecuteAsync()` performs actions via `IAgenticHost` methods
7. Results are fed back to LLM for next iteration (up to `AgenticLoopMaxDepth` times)
8. `LlmClient` manages context budget, evicts stale messages, compacts tool results

### Build Pipeline
- **`DevMind.csproj`** — SDK-style project with custom targets:
  - `IncrementBuildCounter` (BeforeBuild): Reads `build.counter`, increments, stamps version into `AssemblyInfo.cs` and VSIX manifests
  - `StampVsixManifests` (BeforeCreateVsixContainer): Re-applies version to manifests before packaging
  - Version format: `7.0.{build_counter}.0`

## Key Patterns

### Error Handling
- **Try-catch with logging**: Most async methods wrap operations in try-catch, appending errors to output panel via `AppendOutput(error, OutputColor.Error)`.
- **Graceful degradation**: LLM connection failures show "Disconnected" status; missing files produce informative messages.
- **Build command discovery**: `FindMSBuildPath()` checks `VSINSTALLDIR`, then `vswhere.exe`, then PATH.

### Configuration Loading
- **Community.VisualStudio.Toolkit**: Uses `BaseOptionModel<DevMindOptions>` for settings persistence.
- **Profile-based**: `ProfileManager` loads `profiles.json` from extension directory, applies settings to `DevMindOptions`.
- **Live settings**: `DevMindOptions.GetLiveInstanceAsync()` retrieves current settings.

### COM Interop
- **Visual Studio SDK**: Uses `Microsoft.VisualStudio.Shell` for package registration, tool windows, and command handling.
- **EnvDTE**: Accesses active document, selection, and solution explorer via `DTE` object.

### External Tool Usage
- **LLM Server**: HTTP client communicates with OpenAI-compatible endpoints (`/v1/chat/completions`, `/v1/models`, `/props`, `/api/v0/models`).
- **PowerShell**: Shell commands execute via `powershell.exe -NoProfile -Command`.
- **MSBuild**: Build commands invoke `MSBuild.exe /t:Build /p:Configuration=Release`.
- **vswhere**: Used to discover Visual Studio installation path.

### Threading Model
- **Async/await**: All I/O operations are async (file reads, HTTP requests, shell commands).
- **UI thread**: WPF control updates happen on UI thread; background work uses `async Task`.
- **CancellationToken**: Agentic loop supports cancellation via `_cts.Token`.

### Validation
- **Patch matching**: Whitespace-normalized exact match first, then fuzzy Levenshtein match (≥85% similarity).
- **File write guard**: Confirms writes to files not mentioned in the current task.
- **StringValidator**: Simple null/whitespace validation.

### Context Management
- **Tiered eviction**: System prompt (pinned), protected turns (recent), working history (evicted first).
- **MicroCompact**: Fires at 85% working budget, trims tool results with semantic summarization.
- **Brainwash**: Full context replacement when compaction thrashes (multiple compactions with minimal reclamation).
- **Nearline cache**: Trimmed content stays in RAM for instant recall.

## File Summary
| File | Lines | Purpose |
|------|-------|---------|
| `AgenticAction.cs` | 63 | Defines ActionType enum and AgenticAction class for agentic loop decisions |
| `AgenticActionResolver.cs` | 105 | Static resolver that decides next action from ResponseOutcome and ExecutionResult |
| `AgenticExecutor.cs` | 761 | Executes AgenticAction by calling IAgenticHost methods; handles batch patches |
| `DevMindCommand.cs` | 21 | Command handler for showing the DevMind tool window (Ctrl+Alt+D) |
| `DevMindOptionsPage.cs` | 544 | Options page with DevMindOptions model, enums, and profile management |
| `DevMindPackage.cs` | 39 | VSIX package entry point; registers tool window, options, commands |
| `DevMindToolWindow.cs` | 52 | Tool window pane that creates DevMindToolWindowControl and LlmClient |
| `DevMindToolWindowControl.AgenticHost.cs` | 1380 | IAgenticHost implementation: file write guards, TRX parsing, diff generation |
| `DevMindToolWindowControl.Context.cs` | 955 | Context loading: DevMind.md, .agent.md, editor selection, READ commands |
| `DevMindToolWindowControl.Patch.cs` | 1074 | PATCH handling: resolve, apply, fuzzy matching, diff preview |
| `DevMindToolWindowControl.Shell.cs` | 364 | Shell command execution: PowerShell, sanitization, directive parsing |
| `DevMindToolWindowControl.xaml.cs` | 2396 | Main WPF control: chat UI, LLM streaming, agentic loop coordination |
| `DiffBatchBar.xaml.cs` | 52 | Batch control bar for multiple diff preview cards (Apply All / Skip All) |
| `DiffPreviewCard.xaml.cs` | 244 | Inline diff preview card with Apply/Skip buttons and TaskCompletionSource |
| `ExecutionResult.cs` | 58 | Captures execution outcomes: patches applied, shell exit code, files created |
| `FileContentCache.cs` | 64 | In-memory cache for recently read files with line-range retrieval |
| `IAgenticHost.cs` | 173 | Interface abstracting side effects from agentic decision logic |
| `LlmClient.cs` | 3165 | HTTP client for LLM: streaming, context detection, compaction, budget tracking |
| `MemoryManager.cs` | 250 | Cross-session memory: MEMORY.md index and .devmind/<topic>.md files |
| `NearlineCache.cs` | 95 | RAM cache for trimmed content with instant recall |
| `OutputColor.cs` | 21 | Enum for output panel text colors |
| `PackageIds.cs` | 21 | Package GUID and command ID constants |
| `PatchConfidence.cs` | 38 | PatchConfidence enum and PatchResolveResult class |
| `ProfileManager.cs` | 388 | Profile CRUD operations and profile switching |
| `ResponseClassifier.cs` | 42 | Wraps ResponseParser to produce ResponseOutcome with action flags |
| `ResponseOutcome.cs` | 138 | Wrapper around parsed blocks with boolean flags for action types |
| `ResponseParser.cs` | 993 | State machine parser for LLM responses (FILE, PATCH, SHELL, READ, etc.) |
| `StringValidator.cs` | 6 | Simple string validation utility |
| `ToolCallMapper.cs` | 225 | Maps ToolCallResult list to ResponseBlock list |
| `ToolCallResult.cs` | 27 | Unified representation of a single tool call |
| `ToolRegistry.cs` | 230 | Builds OpenAI-compatible tools array for structured tool calling |
| `TrainingLogger.cs` | 345 | Captures fine-tuning training data as JSONL |
| `Properties\AssemblyInfo.cs` | 26 | Assembly attributes: version, company, product info |

## Known Issues
None identified. (No TODO, HACK, or FIXME comments found in source code.)

## Dependencies

### NuGet Packages
- `Microsoft.VisualStudio.SDK` v17.0.32112.339 — Visual Studio extension SDK
- `Microsoft.VSSDK.BuildTools` v17.14.2120 — VSIX build tools (build/analyzers only)
- `Community.VisualStudio.Toolkit.17` v17.0.549 — Community toolkit for VS extensions
- `Newtonsoft.Json` v13.0.1 — JSON serialization
- `Microsoft.Extensions.Http` v3.1.8 — HTTP client factory and pooling

### External Tools (Runtime)
- **PowerShell** — Shell command execution
- **MSBuild.exe** — Build commands (discovered via VSINSTALLDIR, vswhere.exe, or PATH)
- **vswhere.exe** — Visual Studio installation discovery
- **LLM Server** — OpenAI-compatible HTTP endpoint (LM Studio, Ollama, llama.cpp, or custom)

### Core Function
The project uses no third-party SDKs for its core function (LLM communication) beyond standard `System.Net.Http`. All agentic logic, parsing, and execution is custom-built.

## Coding Standards
- **Language version**: C# 8.0 — no newer syntax (`.NET Framework 4.8` constraint).
- **Naming**: 
  - PascalCase for public types and members
  - `_camelCase` for private fields
  - `I` prefix for interfaces
  - Static classes for utility functions
- **Error handling**: Try-catch with `AppendOutput(error, OutputColor.Error)`; graceful degradation for missing resources.
- **Thread model**: Async/await for all I/O; UI updates on UI thread; `CancellationToken` for cancellation support.
- **Build**: 
  - Command: `msbuild DevMind.csproj /p:Configuration=Release`
  - Auto-incrementing build counter in `build.counter`
  - Version stamped into `AssemblyInfo.cs` and VSIX manifests before each build
- **File operations**: 
  - Full read for files <1000 lines; outline-first for larger files
  - Whitespace-normalized patch matching with fuzzy fallback
  - Encoding preservation (BOM detection) for file writes
- **Configuration**: Profile-based settings via `ProfileManager`; live settings via `DevMindOptions.GetLiveInstanceAsync()`.
- **Documentation**: XML doc comments on public APIs; inline comments for complex logic.

## Efficiency Guidelines
- READ files in full unless they exceed 1000 lines.
- Use parallel tool calls to read multiple files in one turn when possible.
- Write findings incrementally with append_file — do not accumulate large outputs in context.
- Do not re-read output files to verify — trust the tool result confirmation.
- Move to the next step immediately after each action completes.
- Prefer creating a file once with full content over multiple small appends when the content is ready.