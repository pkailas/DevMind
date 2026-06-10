# DevMind — Status & Architecture

_Last updated: 2026-06-09_

This is the living "where am I and why" reference for the DevMind rebuild. It captures current
state, the architectural decisions behind it, the phase roadmap, hard-won Terminal.Gui v2 facts,
and the model-assignment strategy. Update it as work progresses.

---

## 1. The Big Decision (why this rebuild exists)

DevMind has hit framework walls twice: first as a Visual Studio **VSIX** extension (trapped by
the extension model), then as a **Bun/Ink/TypeScript** terminal shell (trapped by Ink's render
model — scroll-snap bugs, blank-render deadlocks, whole-tree re-renders, single-event-loop input
lag, loss of `<Static>` performance in alt-screen).

**Root-cause insight:** the recurring failure wasn't any single framework — it was *coupling the
durable engine to a disposable UI framework*. When the UI framework hit its ceiling, the whole
tool hit the ceiling.

**The fix:** a **decoupled C# engine + swappable UI skins** architecture.
- The engine (agentic loop, context management, tools, MCP) lives in C#, UI-agnostic.
- The UI is a thin, swappable skin over that engine.
- When a skin's framework hits a wall, swap the skin — the engine never moves.

**Why C#** (over Go/Rust):
- The engine should be in the language Paul reshapes frictionlessly (native .NET) — important
  because DevMind's form keeps changing as needs change ("BRAIN is brain" — the tool must keep
  up with an unpredictable builder, in his own language, with minimal friction).
- Unifies with the **already-C# MCP server** — engine and server can live in one solution.
- Best SQL Server support (Paul's stack depends on it).
- **Cursor is on the horizon** — the terminal UI is explicitly transient, so optimizing the
  engine's durability (C#) matters more than optimizing the current skin's framework.
- **Go (Bubble Tea) remains the fallback** if C#/Terminal.Gui v2 proves not viable. The
  decoupled architecture means swapping to a Go skin would not throw away the engine.

**Viability verdict (2026-06-09):** C#/Terminal.Gui v2 is **viable**. The Editor-component
migration (see below) came back not just adequate but *faster than the TS shell ever streamed*.
Go stays as the unused fallback.

---

## 2. Repositories & Layout

- **C# solution (the future):** `C:\Users\pkailas\source\repos\DevMind`
  - `DevMind.Core` — the engine. UI-agnostic. Agentic loop (`LoopDriver`, `AgenticExecutor`),
    `LlmClient`, context management, PatchEngine, LSP router, ShellRunner, MemoryManager.
    Boundary interfaces: `IAgenticHost` (side effects) + `ILoopCallbacks` (UI-state hooks).
    History store added 2026-06-09 (`IHistoryStore` + SqlServer/Sqlite/Null implementations).
  - `DevMind.McpServer` — the existing C# MCP server (~27 tools). References Core one-way.
  - `DevMind.Cli` — console skin; reference implementation of the interfaces
    (`ConsoleAgenticHost`).
  - `DevMind.TUI` — **NEW** Terminal.Gui v2 skin (the rebuild target).
  - `_archive/` — retired projects (ShellHarness, ShellEncodingProbe, old VSIX).
  - Branch: `master`.

- **TS shell (current daily driver, dead tool walking):**
  `C:\Users\pkailas\source\repos\DevMindShell`
  - Reverted to normal-buffer version on branch `fix/compaction-flip-gates` with fast cosmetic
    timers restored (spinner 80ms, elapsed 100ms).
  - `feat/alt-screen-scroll` branch holds the abandoned alt-screen experiment — kept as
    reference (windowing logic, `/copy`, height-estimation, scroll math) for the C# rebuild.
  - Launched via `dm.cmd` → `bun run src/index.tsx` (runs from source; branch checkout = version
    switch; never published).
  - **Verdict:** failed to meet standards and never would (Ink's structural limits). Being
    replaced by `DevMind.TUI`.

---

## 3. Consolidation Roadmap (the rebuild plan)

- **Phase 0 — Cleanup** ✅ (`c00e626`): archived dead projects, cleared stale net8.0 artifacts,
  solution down to Core / Cli / McpServer. Clean build.
- **Phase 1 — TUI spike** ✅: `DevMind.TUI` (Terminal.Gui v2 2.4.4) implements `IAgenticHost` +
  `ILoopCallbacks`, cribbed from `ConsoleAgenticHost`. Proven: engine drives the TUI, real turns
  run, input/output/echo/status/counters work. Three v2 API traps found & fixed from decompiled
  source (see §5).
- **Phase 2 — Core TUI experience** ✅:
  - Streaming render fixed (`InsertionPoint`-reset bug removed — `8fa4950`).
  - Color (per-`OutputColor`), black background, word wrap.
  - Typing-lag fix (`MaximumIterationsPerSecond = 750`).
  - **Editor-component migration** ✅ (validated faster-than-ever; see §6).
- **Phase 3 — Port shell features** 🔶 in progress:
  - ✅ Slash-command parsing + dispatch (`SlashCommand.cs`, 18 commands; wired: /new, /clear,
    /restart, /think, /reasoning, /depth-cap, /system_prompt, /help).
  - ✅ SQL history backend built (`IHistoryStore`, SqlServer/Sqlite/Null, factory, SessionId) —
    **NOT YET TESTED against a real DB** (needs `DEVMIND_HISTORY_*` env vars + connection
    string). Unblocks /history, /resume, /title stubs.
  - ⬜ Remaining stubs: /compact, /t, /rules, /lsp, /dir, /output-lines, /training-delete-last.
  - ⬜ `/copy` (needs clipboard + UI — Opus track).
  - ⬜ Config / env wiring into the TUI (the `dm.cmd` vars).
- **Phase 4 — Per-tool permission gating** ⬜: at the `TuiAgenticHost`/`IAgenticHost` boundary.
  Hooks ALREADY EXIST in the interface (`ShowDiffPreviewAsync`, `ConfirmUnreadFileWriteAsync` —
  currently auto-approved). Per-method policy (ask/session/always), persisted config,
  Terminal.Gui v2 approval dialogs.
- **Phase 5 — Cutover** ⬜: new `dm` wrapper → `DevMind.TUI`; retire the TS `DevMindShell`;
  retire or keep `DevMind.Cli` as fallback.

---

## 4. Known Issues / Open Items

- **History backend untested** — built but never run against a real SQL Server. Needs env vars
  + a connection string (decide DB: WIN-SQL002, local MSSQLSERVER01, or a dedicated DevMind DB).
  Note: history uses its OWN config (`DEVMIND_HISTORY_*`), separate from `DEVMIND_DB_CONNECTIONS`.
- **No progress indicator for long agentic actions** (UX gap) — when DM does a lengthy tool
  action (e.g. writing a big file), the screen is static with no in-flight indication; only the
  Thinking spinner covers LLM calls. Worth adding tool-call-in-progress feedback.
- **404 root cause not fully confirmed** — one 404 was a paste-corrupted endpoint (`[` appended).
  A separate possible cause (system HTTP proxy) was mid-investigation when work shifted. If real
  turns 404 again, check the proxy; validate streaming via the diag self-test meanwhile.
- **`[Obsolete]` TextView** — superseded by Editor (now migrated). Old TextView path remains on
  `master` history / fallback only.
- **Layout polish** — current TUI layout is functional but spike-grade; banner had minor render
  glitches. A real layout pass is pending.
- **Mouse/wheel + copy-paste** — terminal quirks; `/copy` (programmatic clipboard) is the
  intended fix. Note: copy-paste *into* the terminal mangles input (host-terminal quirk; caused
  several "bugs" tonight that were actually paste corruption).

---

## 5. Terminal.Gui v2 — Hard-Won Verified Facts (2.4.4)

_All verified by decompiling the assembly, not guessed. These cost real debugging time — keep
this list current as more are found._

- **Thread marshalling:** Use the INSTANCE-based `IApplication.Invoke(Action)` via `View.App`
  (e.g. `_outputView.App.Invoke(...)`), NOT the deprecated static `Application.Invoke`. The
  static form can't resolve the instance from a background thread (created via
  `Application.Create()`), and throws.
- **ReadOnly + TextView:** `ReadOnly = true` BLOCKS programmatic `InsertText` (guard
  `if (_isReadOnly) return;` bails before mutating). For a programmatically-written log on
  TextView, the pattern was `ReadOnly = false` + `CanFocus = false` (writable but non-interactive).
  ALSO: `ReadOnly` does NOT clear `CanFocus` — a read-only TextView still steals focus on
  `app.Run` unless `CanFocus = false` is set and focus is re-asserted in `window.Initialized`.
- **TextView `InsertionPoint` setter** clamps X to column and Y to row — setting
  `Point(0, Lines)` as "auto-scroll" resets the cursor to column 0 each token, causing word
  reversal (`"projectThe"`). `InsertText` already auto-scrolls/positions; do NOT reset
  InsertionPoint after it.
- **`Application.Invoke` queue** is strict FIFO (monotonic unique keys via `NudgeToUniqueKey`,
  drained in key order) — token order IS preserved across Invokes from a background thread.
- **`MaximumIterationsPerSecond` defaults to 25** → ~40ms input-to-draw floor → perceptible
  per-keystroke lag. Set to **750** (~1.3ms) after `app.Init()`. Hard ceiling ~1000: below ~1ms
  the loop's delay guard degenerates into a CPU-pinning busy-spin. (Default 25 is too slow for
  interactive typing.)
- **Submit event:** `Accepting` (EventHandler<CommandEventArgs>) is the correct "user pressed
  Enter" event for a focused TextField (Enter → `Command.Accept` via `SetupKeyboard`). Set
  `e.Handled = true` to stop the command bubbling to the SuperView.
- **TextView WordWrap (2.4.4)** re-wraps the ENTIRE document on every grapheme insert
  (`WrapModel()` unconditional) — O(n) per token, causes streaming sluggishness that grows with
  transcript length. Also has an upstream bug in the wrap-rebuild attribute copy (indexes source
  line by segment index instead of cumulative offset) that breaks colors at wrap points. **This
  is the reason for migrating to Editor.**

---

## 6. Editor Component Migration (2026-06-09) — VALIDATED

Migrated the output view from the `[Obsolete]` TextView to **`Terminal.Gui.Editor` 2.5.0**
(composes with pinned Terminal.Gui 2.4.4; no core bump). Branch `spike/tui-editor-output`
(`00dc3fe`, -170 lines).

**Why it's better:**
- **Faster streaming** — rope insert is O(log n), line tree updates incrementally, visual lines
  are viewport-scoped, so appending a token does NOT re-wrap the whole transcript. Confirmed by
  Paul at runtime: "faster than I've ever seen it output text."
- **Deletes the fragile code** — ~120 lines of cell-stamping + `_wrapManager` reflection +
  `FixWrappedAttributes` + the upstream-wrap-bug workaround all removed.
- **Clean color model** — `IVisualLineTransformer` (`OffsetColorTransformer`) sets
  `element.Attribute` by document offset (per-grapheme elements = exact, zero bleed); survives
  wrap/resize automatically. No reflection.
- **Proper read-only** — `ReadOnly = true` + `Document.Insert` works (insert bypasses the
  command guard); drops the `ReadOnly=false`+`CanFocus=false` hack.

**Append path:** `Document.Insert(Document.TextLength, text)` → record `ColorSpan(start, len,
attr)` → `CaretOffset = Document.TextLength` (auto-scroll).

**Caveat:** Editor is pre-MVP / low adoption; the maintainer has an open "PLEASE TEST: Editor"
issue (#5155). It's actively maintained and the framework explicitly targets Claude-Code-style
agentic CLIs, so the risk profile is "early-adopter bugs in an actively-developed component aimed
at our use case," not abandonware. Validated for our path; watch for edge cases.

**Status:** validated, ready to merge `spike/tui-editor-output` → master.

---

## 7. Model-Assignment Strategy (which AI for which work)

- **DM (local Qwen3.6-27B MTP @ 10.0.0.15:8080):** logic-port / engine work where the answer is
  in available source (TS shell, engine interfaces) — e.g. slash commands, history backend. Good
  when its context is FRESH. **Failure mode: narrates-and-stalls** instead of acting; `/new`
  (fresh context) reliably recovers it. Capable of Reflection/decompile (proved on TTA) but lacks
  the DISCIPLINE to self-impose verify-before-coding on v2 work — needs tightly-gated prompts
  that force verification as a required, checkable step, not a guideline. Single MTP endpoint =
  no parallel agents.
- **Opus (CC):** v2 / framework-internal work requiring disciplined, every-time source
  verification (decompile-and-verify). Cracked every v2 trap tonight. Medium effort for
  research/assessment/contained fixes; High effort for complex multi-constraint implementations
  (e.g. the Editor migration). Worth the token cost for the frontier work.
- **Fable 5:** capable (found a real upstream bug, deep reflection) but **benched — too
  expensive**: burned ~30% of session in <45 min. Token-efficient in count but premium-priced;
  net cost didn't justify it for routine work. Reserve for genuinely huge autonomous jobs, if
  ever.

**Dividing line:** does the task require verifying the *framework's hidden behavior*? → Opus.
Is it porting/wiring against *known* code? → DM (fresh context).

---

## 8. Standing Gotchas / Rituals

- **Close the running `DevMind.TUI.exe` before rebuild/relaunch** — a running instance locks the
  binary (MSB3026/3027 build errors) AND a stale instance launched mid-implementation renders the
  OLD behavior (caused a false "colors broken — all white" alarm tonight).
- **Ctrl+C does NOT quit the TUI** — Esc quits (Terminal.Gui captures input; Ctrl+C is just a
  keystroke). To force-kill from outside: close the window or `taskkill` the PID.
- **Paste into the terminal mangles** (PowerShell/Windows Terminal quirk) — caused a 404 (stray
  `[` appended to the endpoint) and a `[otnet` typo tonight. Type commands by hand or right-click
  paste; the future `/copy` solves the copy direction programmatically.
- **Start DM with `/new`** before a task to avoid the narration-stall.
- **PowerShell env-var syntax:** `$env:VAR="value"` (not cmd's `set VAR=value`).

---

## 9. Launch Reference

**TS shell (current daily driver):** `dm` (via `dm.cmd`, runs `bun run src/index.tsx` from the
checked-out branch; sets DEVMIND_SSH_HOSTS, DEVMIND_HTTP_ENDPOINTS [qwen @ 10.0.0.15:8080,
fetcher, searxng, parsely], DEVMIND_DB_CONNECTIONS).

**C# TUI (the rebuild):**
```
dotnet run --project DevMind.TUI -- --dir C:\Users\pkailas\source\repos\DevMind --endpoint http://10.0.0.15:8080/v1
```
(Type the endpoint by hand to avoid paste corruption. For history: set `DEVMIND_HISTORY_ENABLED`,
`DEVMIND_HISTORY_PROVIDER`, `DEVMIND_HISTORY_CONNECTION_STRING`. For render diagnostics:
`DEVMIND_TUI_DIAG=<logpath>`.)

---

## 10. Next Session — Suggested Starting Points

1. Merge `spike/tui-editor-output` → master (Editor migration). _[may be done already]_
2. Test the SQL history backend against a real DB (decide which server; set env vars; run the
   /title → /history → /new → /resume cycle).
3. Continue Phase 3: config/env wiring; remaining slash-command stubs; `/copy` (Opus track).
4. Phase 4: permission gating at the IAgenticHost boundary.
5. Add tool-call-in-progress indicator (the UX gap).
6. Eventually: layout polish; Phase 5 cutover.
