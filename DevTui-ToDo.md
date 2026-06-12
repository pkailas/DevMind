---
# DevMind TUI — Known Issues & Next Session TODO

## BLOCKERS (fix first, in order)

### 0. System prompt is bare default — CRITICAL
/system_prompt shows only: "You are a helpful coding assistant. Be concise and precise."
The full tool-use directive, tool catalog, behavioral rules, AGENTS.md context — all absent. Model narrates instead of calling tools. Root cause: BuildCombinedSystemPrompt not assembling correctly, likely broken by the Func<string> delegate change for scratchpad wiring. Fix before anything else — items 1-3 may be secondary symptoms.

### 1. No live token counter
Context meter not updating during turns — only shows total, not live per-iteration updates.

### 2. No tok/s chip
Post-turn token rate not displaying in status bar.

### 3. Timer not running
Thinking/generating timer does not start during turns — braille spinner absent, no elapsed time shown.
Root cause suspicion for 1-3: published exe may predate Phase 2/3 status bar work, OR BeginTurn/OnStreamToken/EndTurn hooks not wired correctly. Re-publish after fixing #0 and verify.

## UNCOMMITTED WORK — CRITICAL (lost once already)
Commit immediately once blockers resolved and runtime-verified:
- DevMind.TUI/Program.cs (Ctrl+C/Esc state machine, scratchpad Func<string> wiring)
- DevMind.TUI/TuiInputBox.cs (untracked — multi-line input, Ctrl+Enter enum-drift fix, paste)
- DevMind.TUI/TuiStatusBar.cs (untracked — status bar)
- DevMind.TUI/TuiLoopCallbacks.cs (BeginTurn/OnStreamToken/EndTurn hooks)
- DevMind.TUI/TuiAgenticHost.cs
- DevMind.TUI/TuiOptions.cs
- DEVMIND_STATUS.md (needs today's session update)

## DEFERRED (after blockers resolved)

### 4. Narration tendency
Likely resolves once system prompt (#0) is fixed.

### 5. Re-publish after fixes
dotnet publish "C:\Users\pkailas\source\repos\DevMind\DevMind.TUI\DevMind.TUI.csproj" -c Release -o "C:\Users\pkailas\bin\devmind" --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true

### 6. Paste (resolved June 11 2026)
Ctrl+V wired via View.Pasting + STA PowerShell Get-Clipboard fallback. Verified working.

### 7. Write guard — containment not enforced
ConfirmUnreadFileWriteAsync in TuiAgenticHost auto-approves all writes (SPIKE behavior). Model can write to any absolute path outside --dir. Fix: reject (or prompt to confirm) any write whose resolved path falls outside the working directory tree. ConsoleAgenticHost likely has the same gap.

## HARD-WON FACTS (pending commit to DEVMIND_STATUS.md §5)
- Shift+Enter indistinguishable from Enter at VT layer — Ctrl+Enter is the only durable newline binding
- Command-enum ordinal drift: Editor 2.5.0 ordinals differ from core 2.4.4. Always recover command ids from the Editor's own stock bindings, never from Command.* enum values when targeting Editor-implemented commands. Affects Enter (43=DeleteAll in core) and paste cluster (Editor Paste=61, core 61=Cut)
- Editor 2.5.0 has no OnPaste override — bracketed paste must be consumed via View.Pasting

## SESSION SUMMARY (June 11 2026)
Completed: 7 tools wired + runtime-verified, BuildCommandResolver, _archive noise fix, web error mapping, scratchpad (real), PatchEngine 67 tests, shell timeout, depth-cap ceiling, TUI dressed (Phases 1-5), devmind.cmd launcher, env-schema fix. Connecting to Qwen3.6-27B-Q8_0 at http://10.0.0.15:8080/v1 verified.
Blocking issue: system prompt bare default — TUI not functional for real work until resolved.
---
