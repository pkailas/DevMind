# DevMind Project Context

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
- Current model: Qwen3.5 27B Q4_K_M on RTX PRO 4000 Blackwell (24GB GDDR7)
- Custom Jinja template (qwen35_fixed.jinja) to prevent KV cache invalidation
- Context: 32,768 tokens with quantized KV cache (K: q8_0, V: q4_0)
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

## Key Directives
- SCRATCHPAD terminated by END_SCRATCHPAD (not ---)
- PATCH separator --- between blocks stripped by AutoExecutePatchAsync
- ParsePatchBlocks uses context-aware --- detection to avoid stripping file content

## Testing
- DevMindTestBed (.NET 10 console app) at C:\Users\pkailas.KAILAS\source\repos\DevMindTestBed\
- TestBed.cs is the scratch file for PATCH and agentic loop testing
- Test with experimental VS instance (F5 from DevMind project)

## Build Command
Use msbuild, NOT dotnet build — VSIX projects don't support dotnet build:
msbuild "C:\Users\pkailas.KAILAS\source\repos\DevMind\DevMind.csproj" /p:DeployExtension=false /verbosity:minimal



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
