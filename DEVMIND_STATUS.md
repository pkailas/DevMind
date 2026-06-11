# DevMind — Status & Architecture

_Last updated: 2026-06-14_

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
    `LlmClient`, context management, PatchEngine, LSP router, ShellRunner, MemoryManager,
    WebTools (SearXNG + fetcher), LspToolService (host-side LSP facade), BuildCommandResolver
    (auto-detect build: .vsixmanifest→MSBuild, package.json→npm/bun, .slnx/.sln/.csproj→dotnet).
    Boundary interfaces: `IAgenticHost` (side effects) + `ILoopCallbacks` (UI-state hooks).
    History store added 2026-06-09 (`IHistoryStore` + SqlServer/Sqlite/Null implementations).
    Scratchpad: real persistence via `LlmClient._taskScratchpad` + `Func<string>` delegate in
    `BuildCombinedSystemPrompt` (rebuilt every iteration, cleared on `ResetSession()`).
    **Added 2026-06-13:** `CommandRegistry` (tool registration system), `ToolResult`/`ToolError`
    types, `IFileSystem` abstraction for testable file operations.
    **Added 2026-06-14:** ShellRunner timeout (3-layer precedence: per-call > DEVMIND_SHELL_TIMEOUT env > 120s fallback).
    Web error mapping: surfaces real HTTP status + detail (removed EnsureSuccessStatusCode).
    Depth cap ceiling raised from 10 to 200 (`DepthCapMax` constant).
  - `DevMind.McpServer` — the existing C# MCP server (~27 tools). References Core one-way.
  - `DevMind.Cli` — console skin; reference implementation of the interfaces
    (`ConsoleAgenticHost`).
 - `DevMind.TUI` — Terminal.Gui v2 skin (the rebuild target). Dressed Phase 1-5: status bar
    (TuiStatusBar.cs), live token meter + tok/s, multi-line input (TuiInputBox.cs, Editor-as-input),
    polish. Published as self-contained exe. **2026-06-13:** Added TuiOptions.cs (CLI argument
    parsing), TuiLoopCallbacks.cs (loop state callbacks), TuiAgenticHost.cs (agentic host
    implementation), Program.cs (entry point).
  - `DevMind.Core.Tests` — xUnit test project (net10.0); **67 tests, 67/67 passing** covering
    all 10 public PatchEngine methods (fuzzy thresholds, CRLF/LF preservation, NormalizeWithMap
    offset mapping, reverse-order multi-block application).
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
- **Phase 3 — Port shell features** ✅ done:
  - ✅ Slash-command parsing + dispatch (`SlashCommand.cs`, 18 commands).
  - ✅ SQL history backend built (`IHistoryStore`, SqlServer/Sqlite/Null, factory, SessionId) —
    **NOT YET TESTED against a real DB** (needs `DEVMIND_HISTORY_*` env vars + connection
    string). Unblocks /history, /resume, /title stubs.
  - ✅ Remaining stubs: /compact, /t, /rules, /lsp, /dir, /output-lines, /training-delete-last.
  - ✅ `/copy` (clipboard working).
  - ✅ Config / env wiring into the TUI (`~/.devmind.env` with flat C# vars).
  - ✅ Depth cap ceiling raised from 10 to 200 (`DepthCapMax` constant).
  - ✅ 7 tools wired into TUI/Cli skin path: get_diagnostics, go_to_definition, find_references,
    hover, find_symbol, web_search, web_fetch — all runtime-verified.
  - ✅ BuildCommandResolver — auto-detects build command with 3-layer precedence.
  - ✅ Shell timeout: 3-layer precedence (per-call > DEVMIND_SHELL_TIMEOUT env > 120s fallback).
  - ✅ Scratchpad: real persistence (not theater), runtime-verified.
  - ✅ Web error mapping: surfaces real HTTP status + detail (removed EnsureSuccessStatusCode).
  - ✅ _archive added to ContextEngine._noisePathSegments — fixes build detector, makes _archive
    invisible to list_files/find_in_files/grep uniformly.
  - ✅ PatchEngine tests: 67 tests, 67/67 passing (DevMind.Core.Tests, xUnit, net10.0).
- **Phase 4 — Per-tool permission gating** ⬜: at the `TuiAgenticHost`/`IAgenticHost` boundary.
   **Added 2026-06-13:** `CommandRegistry` system in place — tools registered with names,
   descriptions, and parameter schemas. Foundation for per-tool gating.
  Hooks ALREADY EXIST in the interface (`ShowDiffPreviewAsync`, `ConfirmUnreadFileWriteAsync` —
  currently auto-approved). Per-method policy (ask/session/always), persisted config,
  Terminal.Gui v2 approval dialogs.
- **Phase 5 — Cutover** ✅ mostly done:
   **Added 2026-06-13:** `IFileSystem` abstraction added to Core — enables testable file
   operations and paves the way for sandboxed file access in Phase 4.
  - ✅ Published exe: `C:\Users\pkailas\bin\devmind\DevMind.TUI.exe` (self-contained single-file Release).
  - ✅ `devmind.cmd` on PATH (`C:\Users\pkailas\bin` added to user PATH).
  - ✅ Passes `--dir "%CD%"` so `devmind` from any folder targets that folder.
  - ✅ `~/.devmind.env` updated with flat C# vars: DEVMIND_ENDPOINT, DEVMIND_API_KEY,
    DEVMIND_SEARCH_URL, DEVMIND_FETCH_URL.
  - ✅ Runtime-verified: connects to Qwen3.6-27B-Q8_0, answers prompts.
  - ⬜ TS `DevMindShell` formally retired.

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
- **PASTE (Ctrl+V) not working** — WT injects bracketed paste (`ESC[200~`) but Editor 2.5.0
  has no OnPaste override — View.OnPaste returns false, payload silently dropped. Enum-drift
  also affects paste cluster (Editor's Paste=61, core's 61=Cut, 62=Paste). Fix attempted
  (Pasting event handler + Ctrl+V STA PowerShell fallback) but not working at runtime.
  Right-click context menu visibly corrupted (Cut where Paste should be, Paste absent).
  Next step: run with `DEVMIND_TUI_DIAG` set, paste trace to Fable for ground-truth-before-fix.
- **Uncommitted files (CRITICAL — lost once already):** Program.cs, TuiInputBox.cs,
  TuiStatusBar.cs, TuiLoopCallbacks.cs, TuiAgenticHost.cs, TuiOptions.cs, DEVMIND_STATUS.md.
  Commit the full TUI unit once paste is verified. Do not let this sit untracked again.

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
- **Shift+Enter is NOT distinguishable from Enter (2.4.4, verified empirically 2026-06-11):**
  a key-logger spike on `app.Keyboard.KeyDown` (run under both the default host and explicit
  Windows Terminal, keys injected via SendKeys) shows Shift+Enter arrives as plain `Enter`
  (0x0000000D — shift bit stripped somewhere in the host→driver chain), while **Ctrl+Enter
  arrives correctly distinct** (0x4000000D) and Alt+Enter never arrives at all (host consumes
  it, likely fullscreen toggle). Consequence: any "Shift+Enter = newline" UX must use
  **Ctrl+Enter** instead. Spike artifacts: `%TEMP%\tg-keyspike\` + `%TEMP%\tg-keyspike.log`.
- **Command-enum ORDINAL DRIFT between Terminal.Gui.Editor 2.5.0 and core 2.4.4 (verified
  2026-06-11, offline dispatch harness):** Editor 2.5.0 is compiled against a newer core whose
  `Command` enum reordered — Editor registers its newline handler under literal **43**, but core
  2.4.4's `Command.NewLine` is **44** (43 = `DeleteAll` there). Binding `Command.NewLine` to a key
  on the Editor dispatches into a missing handler ("not supported by this View" → NotBound →
  silently swallowed; `KeyBindings.TryGet` still returns the binding, so the failure is invisible
  until dispatch). `Command.Accept` (=1) agrees across versions and its handler lives in core's
  View, so Enter→Accept rebinds work. **Rule: when binding keys to Editor-implemented commands,
  recover the command id from the Editor's own stock binding** (e.g.
  `KeyBindings.TryGet(Key.Enter, out var b); b.Commands[0]`), never from this process's enum
  names. Repro/fix harness: `%TEMP%\tg-keyspike\BindTest\`.
  **SUPERSEDED 2026-06-11 (same day, fuller bisection): the drift direction was BACKWARDS.**
  Editor 2.5.x is not "newer" — **core 2.4.4 inserted `Command.Insert` at ordinal 38**, shifting
  every later member +1 (NewLine 43→44, SelectAll 41→42, Paste 61→62, …). The ENTIRE Editor 2.5.x
  line (2.5.0–2.5.2 checked) is compiled against the PRE-insertion enum (core ≤ 2.4.3) despite a
  nuspec claiming `>= 2.4.0`. Pairing Editor 2.5.x with core 2.4.4+ cross-wires EVERY editing
  key, not just Enter: Backspace dispatches Editor's SelectAll ("backspace highlights the row"),
  Delete deletes leftward, the bracketed-paste pipeline lands in a dead handler, and the
  right-click context menu renders core's names for Editor's ordinals ("Cut" where Paste belongs,
  no Paste item). All failures are silent. **Fix: pin core 2.4.3 + Editor 2.5.2** — the newest
  aligned pairing; 2.4.3 already has the full paste pipeline (`View.Pasting`/`Pasted`,
  `IApplication.Paste`). Never bump either package without re-running the BindTest harness. The
  stock-binding-recovery rule above remains correct under any pairing and stays in the code.
- **WT paste (Ctrl+V) — BROKEN (2026-06-14):** Windows Terminal binds Ctrl+V itself and
  injects the clipboard as a bracketed paste (`ESC[200~…201~`), shown with WT's own multi-line
  warning dialog when applicable. Editor 2.5.0 has no `OnPaste` override — `View.OnPaste` returns
  false, payload silently dropped. The enum-drift also affects the paste cluster (Editor's
  Paste=61, core's 61=Cut, 62=Paste). Fix attempted (Pasting event handler + Ctrl+V STA
  PowerShell fallback) but not working at runtime — diagnostic trace pending
  (`DEVMIND_TUI_DIAG`). Right-click context menu visibly corrupted (Cut where Paste should be,
  Paste absent). **Next step:** run with `DEVMIND_TUI_DIAG` set, paste trace to Fable for
  ground-truth-before-fix. A real Ctrl+V keystroke only occurs under conhost or a WT profile
  with the paste keybinding unbound.
- **TextView WordWrap (2.4.4)** re-wraps the ENTIRE document on every grapheme insert
  (`WrapModel()` unconditional) — O(n) per token, causes streaming sluggishness that grows with
  transcript length. Also has an upstream bug in the wrap-rebuild attribute copy (indexes source
  line by segment index instead of cumulative offset) that breaks colors at wrap points. **This
  is the reason for migrating to Editor.**

---

## 6. Editor Component Migration (2026-06-09) — VALIDATED

Migrated the output view from the `[Obsolete]` TextView to **`Terminal.Gui.Editor` 2.5.0**
(composes with pinned Terminal.Gui 2.4.3 per the version-alignment finding in §5; core pinned
to avoid ordinal drift). Branch `spike/tui-editor-output` (`00dc3fe`, -170 lines).
**Status:** merged to master. TUI is dressed (Phase 1-5) and published as self-contained exe.

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

**Status:** validated, merged to master. TUI is dressed (Phase 1-5) and published as self-contained exe.

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
- **Ctrl+C three-case behavior** (TuiInputBox): with selection → copy; while running → cancel;
  idle → armed → second Ctrl+C exits. Esc cancels-while-running, clears-while-idle, does NOT quit.
- **Paste into the terminal mangles** (PowerShell/Windows Terminal quirk) — caused a 404 (stray
  `[` appended to the endpoint) and a `[otnet` typo. Ctrl+V paste into TUI input box is currently
  broken (see §4). Type commands by hand or right-click paste; `/copy` solves the copy direction
  programmatically.
- **TUI is published** — run `devmind` from any folder (targets that folder via `--dir`).
  Close running `DevMind.TUI.exe` before rebuild/relaunch (locks binary).
- **Start DM with `/new`** before a task to avoid the narration-stall.
- **PowerShell env-var syntax:** `$env:VAR="value"` (not cmd's `set VAR=value`).

---

## 9. Launch Reference

**TS shell (current daily driver):** `dm` (via `dm.cmd`, runs `bun run src/index.tsx` from the
checked-out branch; sets DEVMIND_SSH_HOSTS, DEVMIND_HTTP_ENDPOINTS [qwen @ 10.0.0.15:8080,
fetcher, searxng, parsely], DEVMIND_DB_CONNECTIONS).

**C# TUI (the rebuild):**
```
devmind              (from any folder — targets that folder)
```
Self-contained exe at `C:\Users\pkailas\bin\devmind\DevMind.TUI.exe`.
Config in `~/.devmind.env`: DEVMIND_ENDPOINT, DEVMIND_API_KEY, DEVMIND_SEARCH_URL, DEVMIND_FETCH_URL.
For history: set `DEVMIND_HISTORY_ENABLED`, `DEVMIND_HISTORY_PROVIDER`,
`DEVMIND_HISTORY_CONNECTION_STRING`. For diagnostics: `DEVMIND_TUI_DIAG=<logpath>`.

**Also available (development):**
```
dotnet run --project DevMind.TUI -- --dir C:\Users\pkailas\source\repos\DevMind
```

---

## 10. Next Session — Suggested Starting Points

1. **Fix Ctrl+V paste** — run with `DEVMIND_TUI_DIAG` set, paste trace to Fable for
   ground-truth-before-fix. Enum-drift fix (Editor Paste=61 vs core 61=Cut, 62=Paste)
   may be the root cause.
2. **Commit uncommitted files** (CRITICAL — lost once already): Program.cs, TuiInputBox.cs,
   TuiStatusBar.cs, TuiLoopCallbacks.cs, TuiAgenticHost.cs, TuiOptions.cs, DEVMIND_STATUS.md.
3. **History backend** — connect to a real SQL Server (decide which: WIN-SQL002, local
   MSSQLSERVER01, or a dedicated DevMind DB).
4. **TUI polish** — mouse/wheel, progress indicator for long agentic actions, right-click
   context menu fix (Cut/Paste labels wrong).
5. **Deploy to production** — move `devmind` to the production machine, update cron/
   scheduled tasks if needed.
