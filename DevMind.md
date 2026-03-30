# DevMind Project Context

## Project
- **Product**: DevMind
- **Company**: iOnline Consulting LLC
- **Platform**: Visual Studio 2022+ VSIX, .NET Framework 4.8, C#, WPF
- **Solution**: DevMind.slnx

## Context
This is a Visual Studio extension (VSIX) that provides a local LLM coding assistant inside the VS IDE tool window. It targets developers who want Claude Code-style assistance using a privately hosted model (LM Studio, Ollama, or any OpenAI-compatible endpoint) without sending code to external servers.

## Architecture
- **Entry point**: `DevMindPackage.cs` registers the tool window, options page, and commands.
- **Agent context file compatibility**: Supports DevMind.md (primary), AGENTS.md (GitHub Copilot), CLAUDE.md (Claude Code) with discovery chain fallback. Agent profiles in `.github/agents/*.agent.md` loadable via `/agents` and `/agent load <name>` commands.
- **UI**: `DevMindToolWindowControl.xaml/.cs` — single-stream WPF output (RichTextBox), input bar, toolbar, terminal strip. No third-party markdown renderers — plain `Run`/`Paragraph` appends only.
- **Partial classes**: `.AgenticHost.cs` (IAgenticHost bridge to VS/UI), `.Context.cs` (editor context, READ, file search), `.Patch.cs` (PATCH parsing, matching, UNDO, markdown fence stripping fix for FILE directive), `.Shell.cs` (shell execution).
- **Agentic pipeline**: `ResponseParser` → `ResponseClassifier` → `AgenticActionResolver` → `AgenticExecutor` → `IAgenticHost`. Only `AgenticExecutor` has side effects.
- **LLM client**: `LlmClient.cs` — SSE streaming to OpenAI-compatible `/v1/chat/completions`. Context budget auto-detected. Squeeze algorithm compresses history. Tiered context eviction (HOT/WARM/COLD/DROP) based on turn age.
- **Diff preview**: `DiffPreviewCard.xaml/.cs` + `DiffBatchBar.xaml/.cs` — inline diff cards in OutputBox for PATCH confirmation. Three-phase pipeline: resolve → auto-apply exact → preview fuzzy.

## Coding Standards
- **C# 8.0 only** — no C# 9+ syntax (no `or` patterns, records, init-only, top-level statements). This is .NET Framework net48.
- **File version headers**: Always bump `// File: FileName.cs  vX.Y` when making changes.
- **Copyright header**: `// Copyright (c) iOnline Consulting LLC. All rights reserved.`
- **No external NuGet dependencies** beyond VSSDK and Community Toolkit.
- **No `DispatcherTimer.Dispose()`** — it does not implement IDisposable.
- **No primary constructors** in WPF code-behind files.
- **Thread model**: All UI updates via `Dispatcher.BeginInvoke`. VSSDK007/VSTHRD001/VSTHRD110 pragmas for intentional fire-and-forget.
- **Build**: Use MSBuild with `/p:Configuration=Release`.

## Directives
The LLM communicates actions through these directives: `FILE:/END_FILE` (create files), `PATCH/END_PATCH` (edit files with FIND/REPLACE), `SHELL:` (run commands), `READ` (request file context), `GREP:` (single-file search), `FIND:` (cross-file search), `DELETE` (remove file), `RENAME` (rename file), `DIFF` (show changes), `TEST` (run tests), `SCRATCHPAD:/END_SCRATCHPAD` (state tracking), `DONE` (task completion).

## Key Files
| File | Purpose |
|------|---------|
| `DevMindToolWindowControl.xaml.cs` | Main UI + agentic loop orchestration |
| `DevMindToolWindowControl.AgenticHost.cs` | IAgenticHost — VS/file-system side effects |
| `AgenticActionResolver.cs` | Pure static: maps ResponseOutcome + ExecutionResult to AgenticAction |
| `AgenticExecutor.cs` | Executes actions via IAgenticHost; three-phase PATCH pipeline |
| `ResponseParser.cs` | Parses LLM responses into typed blocks |
| `LlmClient.cs` | HTTP SSE streaming, history management, squeeze algorithm |
| `DiffPreviewCard.xaml.cs` | Inline diff preview card with Apply/Skip |
| `PatchConfidence.cs` | PatchConfidence enum + PatchResolveResult for two-phase PATCH |
| `DevMindOptionsPage.cs` | VS Tools > Options settings |
