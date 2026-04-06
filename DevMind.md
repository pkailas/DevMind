# DevMind Project Context  v2.0

## Project
- **Product**: DevMind
- **Company**: iOnline Consulting LLC
- **Platform**: Visual Studio 2022+ VSIX, .NET Framework 4.8, C#, WPF
- **Solution**: DevMind.slnx

## Context
This is a Visual Studio extension (VSIX) that provides a local LLM coding assistant inside the VS IDE tool window. It targets developers who want Claude Code-style assistance using a privately hosted model (LM Studio, ik_llama.cpp, or any OpenAI-compatible endpoint) without sending code to external servers.

## Architecture
- **Entry point**: `DevMindPackage.cs` registers the tool window, options page, and commands.
- **Agent context file compatibility**: Supports DevMind.md (primary), AGENTS.md (GitHub Copilot), CLAUDE.md (Claude Code) with discovery chain fallback. Agent profiles in `.github/agents/*.agent.md` loadable via `/agents` and `/agent load <n>` commands.
- **UI**: `DevMindToolWindowControl.xaml/.cs` — single-stream WPF output (RichTextBox), input bar, toolbar, terminal strip. No third-party markdown renderers — plain `Run`/`Paragraph` appends only.
- **Partial classes**: `.AgenticHost.cs` (IAgenticHost bridge to VS/UI), `.Context.cs` (editor context, READ, file search), `.Patch.cs` (PATCH parsing, matching, UNDO, markdown fence stripping fix for FILE directive), `.Shell.cs` (shell execution).
- **Agentic pipeline**: `ResponseParser` → `ResponseClassifier` → `AgenticActionResolver` → `AgenticExecutor` → `IAgenticHost`. Only `AgenticExecutor` has side effects.
- **Tool calling**: Supports both directive-based (text parsing) and structured `tool_use` mode (OpenAI function calling). ToolRegistry defines available tools, ToolCallMapper maps tool calls to typed blocks. Fence stripping is bypassed for tool_use content (FromToolCall flag).
- **LLM client**: `LlmClient.cs` — SSE streaming to OpenAI-compatible `/v1/chat/completions`. Parses server timings from SSE responses (n_past, n_ctx, predicted_n, predicted_ms) for real-time context tracking.
- **Settings profiles**: `ProfileManager.cs` — named connection profiles (endpoint, model, context settings). Stored in `%LOCALAPPDATA%\DevMind\profiles.json`. Toolbar dropdown for switching. Options page for CRUD. HttpClient recreated on profile switch.
- **Diff preview**: `DiffPreviewCard.xaml/.cs` + `DiffBatchBar.xaml/.cs` — inline diff cards in OutputBox for PATCH confirmation. Three-phase pipeline: resolve → auto-apply exact → preview fuzzy.

## Context Management (v7)
DevMind uses a three-tier context management system designed for hybrid recurrent models (Qwen 3.5 Mamba/SWA) where any prefix change forces full KV cache reprocessing.

- **Server timings**: Real n_past/n_ctx parsed from every SSE response. All budget decisions use actual server token counts, not estimates. Estimation is a first-turn fallback only.
- **Predictive compaction (MicroCompact)**: Tracks rolling average of n_past deltas across turns. Fires when projected next-turn context exceeds `ServerContextSize - (avgDelta * 3)`. Trims stale tool results, user READs, and old messages using watermark targeting. Stale messages are pre-tagged when superseded by newer reads of the same file.
- **Semantic summarizer**: During compaction, sends trimmed messages to the same LLM endpoint (with thinking disabled) to generate a structured summary (FILES READ, ACTIONS TAKEN, FINDINGS, CURRENT STATE, NEXT STEP). Summary replaces breadcrumbs. Previous summaries are stored in `_compactionSummaries` field and fed to subsequent summarization calls for cumulative context. NearlineCache file keys included in prompt for richer summaries. Gated behind `MicroCompactSummarize` setting.
- **NearlineCache**: Holds full content of trimmed file reads. If the model re-requests a file that was compacted, NearlineCache serves it instantly without disk I/O.
- **Append-only history**: Conversation history is immutable after append — no squeeze or retroactive compression. Compaction only removes complete messages, never modifies content in place.
- **Soft/hard trim disabled**: When MicroCompact is active, the old eviction system is bypassed entirely. MicroCompact is the sole context management strategy.

## Tools (tool_use mode)
| Tool | Description |
|------|-------------|
| `read_file` | Read file content (outline, full, or line range) |
| `create_file` | Create a new file |
| `patch_file` | Edit existing file with FIND/REPLACE |
| `append_file` | Append content to end of existing file (incremental writing) |
| `run_command` | Execute shell command |
| `grep_file` | Search within a single file |
| `find_files` | Search across project files |
| `save_memory` | Save key-value to cross-session memory |
| `recall_memory` | Recall value from cross-session memory |
| `task_done` | Signal task completion |

## Efficiency Guidelines
- READ files in full unless they exceed 1000 lines.
- Use parallel tool calls to read multiple files in one turn when possible.
- Write findings incrementally with append_file — do not accumulate large outputs in context.
- Do not re-read output files to verify — trust the tool result confirmation.
- Move to the next step immediately after each action completes.
- Prefer creating a file once with full content over multiple small appends when the content is ready.

## Coding Standards
- **C# 8.0 only** — no C# 9+ syntax (no `or` patterns, records, init-only, top-level statements). This is .NET Framework net48.
- **File version headers**: Always bump `// File: FileName.cs  vX.Y` when making changes.
- **Copyright header**: `// Copyright (c) iOnline Consulting LLC. All rights reserved.`
- **No external NuGet dependencies** beyond VSSDK and Community Toolkit.
- **No `DispatcherTimer.Dispose()`** — it does not implement IDisposable.
- **No primary constructors** in WPF code-behind files.
- **Thread model**: All UI updates via `Dispatcher.BeginInvoke`. VSSDK007/VSTHRD001/VSTHRD110 pragmas for intentional fire-and-forget.
- **Build**: Use MSBuild with `/p:Configuration=Release`.

## Directives (text mode)
The LLM communicates actions through these directives: `FILE:/END_FILE` (create files), `PATCH/END_PATCH` (edit files with FIND/REPLACE), `SHELL:` (run commands), `READ` (request file context), `GREP:` (single-file search), `FIND:` (cross-file search), `DELETE` (remove file), `RENAME` (rename file), `DIFF` (show changes), `TEST` (run tests), `SCRATCHPAD:/END_SCRATCHPAD` (state tracking), `DONE` (task completion).

**IMPORTANT: Always use the exact directive syntax shown in the examples below. Do not use markdown code fences for file edits — use PATCH with FIND:/REPLACE: pairs. Do not use alternative diff formats.**

### FILE: / END_FILE — Create New Files
```
FILE: Models/BenchmarkResult.cs
using System;

namespace DevMind.Models
{
    public class BenchmarkResult
    {
        public string PromptName { get; set; }
        public double TimeToFirstTokenMs { get; set; }
        public double TotalTimeMs { get; set; }
    }
}
END_FILE
```

### PATCH / END_PATCH — Edit Existing Files
```
PATCH DevMindToolWindowControl.xaml.cs
FIND:
        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            OutputTextBlock.Text = "";
        }
REPLACE:
        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            OutputTextBlock.Text = "";
            _messageHistory.Clear();
        }
END_PATCH
```

### READ — Request File Context
```
READ LlmClient.cs
READ DevMindToolWindowControl.xaml.cs:100-200
```

### SHELL: — Run Commands
```
SHELL: dotnet build --no-restore
```

## Key Files
| File | Purpose |
|------|---------|
| `DevMindToolWindowControl.xaml.cs` | Main UI + agentic loop orchestration |
| `DevMindToolWindowControl.AgenticHost.cs` | IAgenticHost — VS/file-system side effects |
| `AgenticActionResolver.cs` | Pure static: maps ResponseOutcome + ExecutionResult to AgenticAction |
| `AgenticExecutor.cs` | Executes actions via IAgenticHost; three-phase PATCH pipeline |
| `ResponseParser.cs` | Parses LLM responses into typed blocks |
| `ToolRegistry.cs` | Defines available tools for tool_use mode |
| `ToolCallMapper.cs` | Maps tool_call JSON to typed blocks |
| `LlmClient.cs` | HTTP SSE streaming, context management, server timings, summarizer |
| `ProfileManager.cs` | Named connection profiles — CRUD, save/load, apply to settings |
| `DevMindOptionsPage.cs` | VS Tools > Options settings, profile management actions |
| `DiffPreviewCard.xaml.cs` | Inline diff preview card with Apply/Skip |
| `PatchConfidence.cs` | PatchConfidence enum + PatchResolveResult for two-phase PATCH |
