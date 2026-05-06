---
doc_type: design_doc
project: DevMind
stage: 11
title: Stage 11 Ink Shell Discovery
verified_date: "2026-05-06"
last_updated: "2026-05-06"
revalidate_after: "2026-11-06"
tech_versions:
  bun: "1.3.13"
  ink: "7.0.2"
  node: "24.15.0"
  mcp_sdk_typescript: "1.29.0"
  openai_npm: "unknown"   # not yet installed; must verify SSE streaming in Phase A
status: draft
rag_ready: true
---

# Stage 11 — Ink Shell Discovery

**Purpose**: Design document for the DevMind Ink shell. Records architectural decisions, rationale, and the phase breakdown. Not a tutorial — assumes the reader has read `docs/Stage11-Tech-Reference.md`.  
**Date**: 2026-05-06  
**Stage 10 baseline**: MCP server complete, tagged v8.0. 16 tools over stdio, validated via MCP Inspector.

---

## 1. Recommended Architecture

**One-line summary**: The shell is the brain. McpServer is the hands. llama-server is the voice.

```
┌─────────────────────────────────────────────────────────────┐
│                    devmind-shell (Bun / Ink)                 │
│                                                             │
│  ┌─────────────┐    ┌────────────────┐   ┌──────────────┐  │
│  │  Ink render │    │  Agentic loop  │   │  LLM client  │  │
│  │             │◄───│                │──►│  (openai npm)│  │
│  │ <Static>    │    │ - history      │   └──────┬───────┘  │
│  │ <ActiveTurn>│    │ - system prompt│          │ SSE      │
│  │ <StatusBar> │    │ - tool dispatch│          │ stream   │
│  │ <InputBox>  │    │ - ctx budget   │          ▼          │
│  └─────────────┘    └───────┬────────┘   llama-server      │
│                             │            10.0.0.15:8080/v1  │
│                             │ MCP stdio                     │
└─────────────────────────────┼─────────────────────────────-┘
                              │
                    ┌─────────▼──────────┐
                    │  DevMind.McpServer  │
                    │  (C# .NET, stdio)   │
                    │                    │
                    │  read_file         │
                    │  list_files        │
                    │  grep_file         │
                    │  find_in_files     │
                    │  patch_file        │
                    │  create_file       │
                    │  append_file       │
                    │  delete_file       │
                    │  rename_file       │
                    │  diff_file         │
                    │  recall_memory     │
                    │  list_memory_topics│
                    │  save_memory       │
                    │  run_shell         │
                    │  run_build         │
                    │  run_tests         │
                    └────────────────────┘
```

**Data flow for a single agentic turn:**

```
User hits Enter
  │
  ▼
Shell assembles request:
  system prompt (directives + cwd + tool list + DevMind.md context)
  + conversation history
  + new user message
  │
  ▼
openai.chat.completions.create({ stream: true })  →  llama-server
  │
  ├── token deltas  →  <ActiveTurn> re-renders on each token (Ink diffing renderer)
  │
  └── tool_call delta  →  shell detects, pauses token display, shows [tool: name]
        │
        ▼
      await mcpClient.callTool(name, args)  →  McpServer.exe
        │
        ▼
      tool result injected into conversation as tool_result message
        │
        ▼
      next LLM request (loop continues)
        │
        ▼  (when model stops requesting tools or emits task_done)
      completed turn moves to <Static>
      <InputBox> restored
```

---

## 2. Repo Structure

**Recommendation: Sibling repo on the Synology NAS Git host.**

Create `devmind-shell` as a separate repository adjacent to `DevMind` on the same Synology host. Do not monorepo.

**Reasoning:**

The toolchains are entirely different. DevMind is C# / .NET Framework / VSSDK / MSBuild. The shell is TypeScript / Bun / React. A monorepo would need cross-language tooling that helps neither side — no shared `package.json`, no shared `.sln`, no shared build script. The repositories share nothing at the build level.

**Release coupling** is managed by a configured path, not a build dependency. The shell finds `DevMind.McpServer.exe` at runtime via a resolution chain (see §7). The McpServer does not need to version-bump when the shell changes, and vice versa — they communicate over the stable MCP protocol. When the MCP server adds or removes tools, `client.listTools()` surfaces the change dynamically; the shell requires no recompilation.

**Future multi-frontend scenario** (Stage 12+ web UI, VSIX integration): a sibling repo makes each frontend independently deployable. A monorepo would create a pressure to coordinate deployments across the C# VSIX build pipeline and the TypeScript shell build pipeline — two unrelated CI systems.

**If the McpServer binary path needs to be co-installed with the shell**, that's a packaging concern, not a repo-structure concern. A release step (GitHub Actions or a PowerShell script) can copy `DevMind.McpServer.exe` into the shell's distribution package. This is simpler than a monorepo.

---

## 3. LLM Client Architecture

This is the central decision. Both options are analyzed below; the recommendation is at the end.

### Option A: Shell owns the agentic loop

The shell talks to llama-server directly for completions and to McpServer for tool execution. The shell owns conversation history, the system prompt, and all loop logic.

```
Shell  →  llama-server (openai npm, streaming)
Shell  →  McpServer (MCP client, tools only)
```

**Advantages:**

1. **Zero new interfaces on McpServer.** McpServer remains exactly what Stage 10 built: a passive stdio tool server. It answers tool calls. It knows nothing about LLM sessions. No new transport, no new protocol, no C# modifications required for Stage 11.

2. **Streaming renders on the fastest possible path.** Token deltas arrive at the shell directly from llama-server via SSE. The shell feeds them to Ink's render loop immediately. There is no intermediate process hop. The "first token latency" the user perceives is purely llama-server latency — nothing added by the shell architecture.

3. **System prompt is assembled where context lives.** The shell knows: the cwd, the tool list (from `client.listTools()`), the user's DevMind.md equivalent, the conversation history. Assembling the system prompt in the shell is natural. Assembling it in McpServer would require the shell to forward all that context to McpServer every turn, which is more work than just building the prompt directly.

4. **Conversation history is naturally owned by the client.** A session is a shell instance. History lives in the shell process. When the shell exits, the session ends. This matches user expectation. If McpServer owned history, sessions would outlive shell instances, creating stale state that must be explicitly cleared.

5. **Future frontend flexibility is preserved the right way.** A future web UI (Stage 12 Phase E) would be a new frontend that also owns its own agentic loop (or shares a TypeScript loop module). The "extract the loop to a shared component" refactor is straightforward in TypeScript — move the loop into an npm package. Under Option B, the loop would live in C# inside McpServer, making a TypeScript web UI impossible without duplicating it anyway.

6. **Aligns with how all production MCP clients work.** Claude Desktop, Cursor, Zed — all follow the Option A pattern. The model is the orchestrator; MCP servers are tool providers; the client (host) owns the loop. This is the intended MCP architecture. Source: MCP specification design rationale.

**Disadvantages:**

1. The agentic loop logic is duplicated: once in DevMind.Core (`LoopDriver.cs`) for the WPF VSIX, once in TypeScript for the shell. These are parallel implementations, not shared code. If the loop logic becomes complex, keeping them in sync is a maintenance burden.

2. If a third frontend is ever needed (e.g., a VS Code extension), it would need to implement the loop a third time — or the TypeScript loop module gets extracted.

**Mitigation for disadvantage 1**: The loop logic is not deeply complex for Stage 11 — it's: stream → detect tool calls → dispatch sequentially → inject results → re-stream. The WPF VSIX's loop has accumulated complexity from years of edge cases (depth caps, consecutive error guards, scratchpad, block-by-block mode). The TypeScript shell starts clean. Accept the duplication for now; extract when the third frontend materializes.

### Option B: McpServer owns the agentic loop

McpServer proxies LLM calls and returns a stream to the shell. The shell is a terminal renderer that subscribes to a McpServer-controlled stream.

**This option is not viable for Stage 11. The reasons:**

1. **McpServer has no outbound channel to the shell.** McpServer is a stdio MCP server. Its only output mechanism is MCP responses to tool calls. For the shell to subscribe to a stream from McpServer, McpServer would need a new outbound transport — HTTP+SSE, WebSocket, or a second stdio channel. This is a Major Stage 10 retrofit that Stage 11 is not budgeted for.

2. **It inverts the intended MCP architecture.** MCP is designed so the host (client) orchestrates and servers provide tools. Putting the agentic loop in the server breaks this model and makes McpServer non-interoperable with other MCP hosts (Claude Desktop, MCP Inspector, Cursor) — they would all need the loop to live in the server, which they don't.

3. **Streaming would require a second protocol hop.** Every token delta would travel: llama-server → McpServer → shell. The extra hop adds latency and complexity for no benefit, since the shell is the only consumer of this stream.

4. **Adding a C# LLM client to McpServer means maintaining two LLM clients** (DevMind.Core's `LlmClient.cs` and a new one in McpServer) in the same project, in the same language. This is strictly worse than Option A's TypeScript-only loop.

**Recommendation: Option A — Shell owns the agentic loop.** This is not a close call.

The one concession to Option B's theoretical future-frontend appeal: the shell's agentic loop should be written as a standalone TypeScript module (`src/loop/AgenticLoop.ts` or similar) with clear input/output contracts, so it can be extracted into an npm package if a second TypeScript frontend is ever built. This is good engineering regardless of the architecture choice.

---

## 4. Runtime

**Recommendation: Bun 1.3.13.**

Evidence from tech reference:
- Bun spike on Beast (Windows x64) passed all five criteria: Ink installs clean, `<Static>` + live render works, `child_process.spawn` with stdio pipe works, MCP SDK Client and StdioClientTransport both import and instantiate. Source: `docs/Stage11-Tech-Reference.md` §Bun+Ink Spike.
- The previously-documented blocking issue (#2034, `yoga-layout-prebuilt`) is moot: Ink 7.x migrated to `yoga-layout`. Source: tech reference §1.

**One unverified gap remains**: `openai` npm package SSE streaming on Bun. Node's fetch and Bun's fetch differ in subtle ways. This must be the **first test in Phase A** — before any other LLM wiring. If it fails, fall back to Node 20+ with no other code changes (the project structure is identical).

Node 20+ is the safety net, not the default. Given the spike evidence, Bun is the right starting point. Bun's cold-start advantage matters for a developer tool that users run interactively dozens of times per day.

**Revalidation note** (from tech reference §Revalidation Policy): spike results are revalidated every 3 months. The Phase A SSE verification constitutes a fresh spike. Pin the Bun version in the project (`package.json` → `"bun": "1.3.13"` in `engines` field or `bunfig.toml`).

---

## 5. UX Pattern

**Recommendation: Hybrid — adopt CC patterns where they're genuinely right, deviate for DevMind's tool-visibility needs.**

### What to copy from Claude Code

1. **`<Static>` for append-only history.** Past turns are flushed to the terminal via `<Static>`. They're not re-rendered. The terminal scrollback preserves them. This is the correct pattern — verified in the spike. Source: tech reference §1.

2. **Inline rendering (no alternate screen).** No `useAlternateScreen: true`. Conversation history lives in the terminal scrollback, accessible via normal scrolling. This is the right UX for a chat shell. Alternate screen is appropriate for full-screen TUIs (vim, htop) but wrong for conversations. Source: tech reference §1, CC observable behavior.

3. **Floating status row.** The last component rendered in the Ink tree is a status bar. Because Ink renders from top to bottom and the cursor ends at the last line, the status bar appears at the bottom of the visible terminal. It updates in place — each re-render replaces only the status bar line, not the history above it. Source: tech reference §4 (opencode observation), CC observable behavior.

4. **Multiline input.** Shift+Enter inserts a newline; Enter submits. Source: CC behavior, HIGH confidence from usage.

### Where to deviate for DevMind

1. **Tool calls and results are surfaced inline in the conversation, not hidden.**  
   When the model emits a tool call, the shell renders a tool-call indicator inline in the active turn area:
   ```
   [→ read_file "Program.cs"]
   [← 1,247 lines returned]
   ```
   This is a deviation from CC, which minimizes tool call display. DevMind's target user is a developer who wants to see what's happening. Hiding tool calls creates "magic box" anxiety.

2. **Streaming tool output (run_shell, run_build, run_tests) gets its own live region.**  
   These tools stream output lines. The shell renders a live `<ToolOutputRegion>` component that appends lines as they arrive (using a useState buffer), then collapses to a summary line when the tool completes:
   ```
   [→ run_build "DevMind.csproj"]
     Building DevMind...
     Compiling 34 files...
     ✓ Build succeeded in 12.3s
   [← exit=0  34 warnings, 0 errors]
   ```
   The collapsed summary line moves into `<Static>` with the turn when the turn completes.

3. **Status bar shows the current tool, not just "thinking".**  
   ```
   ■ Generating... (342 tokens)          [ESC to stop]
   ■ Running: read_file "AgenticLoop.ts" [ESC to stop]
   ■ Running: run_build "DevMind.csproj" [ESC to stop]
   ○ Ready                               [Enter to send]
   ```

### Component tree sketch

```
<App>
  <Static items={completedTurns}>
    <TurnView>           — completed user+assistant turn pair, immutable once added
      <UserMessage>
      <AssistantMessage>
        [<ToolCall> ...]  — inline tool call records
    </TurnView>
  </Static>

  <ActiveTurn>           — live region, re-renders on tokens and tool calls
    <StreamingText>      — accumulated token text so far
    <ActiveToolCall>     — visible when a tool call is in flight
  </ActiveTurn>

  <InputBox>             — text input area
  <StatusBar>            — always last; shows state + token count + keybind hints
</App>
```

**Stop/Cancel UX — flagged as an open question.**  
The tech reference §Gaps item 8 flags Ink focus management during high-frequency streaming as unverified. Specifically: can `useInput()` capture Escape/Ctrl+C keystrokes reliably while SSE tokens are arriving at ~50–100ms intervals? If Bun's event loop serializes SSE reads and Ink renders on the same thread, the input handler may not fire promptly. This must be verified in Phase B. If it doesn't work reliably, the mitigation is to use `process.on('SIGINT', ...)` for Ctrl+C (which fires as a signal, not as an Ink input event) and implement a separate AbortController for Escape.

---

## 6. State Ownership

**Shell owns conversation history and context budget.** Follows directly from §3 Option A.

**Conversation history**: An array of `ChatCompletionMessageParam` objects (the openai npm package's type). The shell maintains this array across turns and serializes it into each LLM request.

**System prompt**: Assembled fresh at the start of each session (or when DevMind.md-equivalent changes). Contents: LLM directives, cwd, `client.listTools()` result formatted as tool descriptions, project context from DevMind.md (if present). Stored as the first message in the history array with role `"system"`.

**Context budget**: Simple rolling window for Stage 11. Estimated token count using a character-based approximation (~4 chars/token) plus the exact count from `stream_options.include_usage` in each response. When estimated history exceeds 80% of the context window (131K for Gemma 4), drop oldest non-system turns. Implement the 80% soft trim / 95% hard trim pattern from DevMind.Core's budget guard — but in TypeScript, not extracted from C#.

**What NOT to share with the WPF VSIX**: The WPF VSIX's `ContextBudget`, `LoopDriver`, and proactive eviction logic are C# and deeply integrated with `LlmClient.cs`. Extracting them to a shared component is not feasible across language boundaries. The TypeScript shell reimplements the same concepts independently. Accept the duplication. The concepts are simple enough that parallel implementations are maintainable.

**Tool list as system prompt content**: The shell calls `client.listTools()` after McpServer connects. The returned tool schemas are formatted into the system prompt as a tool reference section. This ensures the model knows what it can do. The openai npm package's tool-calling API also receives these as `tools` parameter — both paths should be populated.

---

## 7. MCP Client Wiring

### Spawn pattern: synchronous at startup

Spawn `DevMind.McpServer.exe` before rendering any UI. Rationale: the shell needs the tool list to build the system prompt. Without the tool list, the first LLM turn cannot include tool descriptions. Lazy spawn would require the first turn to use a tool-free system prompt, then retry — more complexity for no benefit.

Startup sequence:
```
1. Resolve McpServer binary path (see below)
2. spawn("DevMind.McpServer.exe", ["--dir", cwd.replace(/\\/g, '/')])
   shell: false, stdio: ['pipe', 'pipe', 'inherit']
3. new StdioClientTransport({ command, args }) — SDK wraps the process
4. await client.connect(transport) — MCP handshake
5. tools = await client.listTools() — get all 16 tools
6. Assemble system prompt including tool list
7. Render Ink UI (now ready for first user input)
```

Startup failure (McpServer not found, or exits immediately) must surface as a clear error before the Ink UI renders. Do not silently skip McpServer and operate tool-free.

### Path resolution

Priority order:
1. `DEVMIND_MCP_SERVER_PATH` environment variable (absolute path to the .exe)
2. Config file: `~/.config/devmind/shell.json` → `"mcpServerPath"` field  
   (or `.devmind/shell.json` in the cwd, for per-project overrides)
3. Adjacent build convention: `../DevMind.McpServer/bin/Release/net8.0/DevMind.McpServer.exe` relative to the shell script's location. Useful in dev when both repos are cloned siblings on the Beast.
4. `DevMind.McpServer.exe` on `PATH` (for production installs where the binary is on the system path)
5. Hard error: print the resolution chain that was tried and exit non-zero.

The adjacent build convention (step 3) assumes:
```
C:/source/repos/
  DevMind/          ← existing repo
  devmind-shell/    ← new repo
    src/
    dist/
```

This is consistent with both repos being cloned siblings on the Beast, which is the expected dev setup.

### Forward-slash path convention

All paths passed to McpServer as arguments must use forward slashes. This is a hard rule from Stage 10 discovery (the `\t` tab mangling bug). Source: tech reference §6.

```typescript
const cwdArg = process.cwd().replace(/\\/g, '/');
// → "C:/Users/pkailas/source/repos/MyProject"
// NOT "C:\Users\pkailas\source\repos\MyProject"
```

Apply `path.replace(/\\/g, '/')` to every absolute path before it becomes a subprocess argument, not just `--dir`.

### Stdio framing

The TypeScript MCP SDK handles JSON-RPC 2.0 framing internally. The shell does not deal with newline buffering, partial reads, or encoding. Source: tech reference §3 (HIGH confidence).

Critical: `DevMind.McpServer.exe` must not write non-JSON to stdout. All logging in the C# server must go to stderr (or a file). This is already enforced in Stage 10's implementation. Source: tech reference §3.

The spawn call uses `shell: false` (array args form). This bypasses cmd.exe and PowerShell, preventing shell-escaping issues with spaces in paths. Source: tech reference §6.

### Lifecycle and crash recovery

**Normal shutdown**: Shell exit (Ctrl+C, `process.on('SIGINT')`) calls `await client.close()`. The SDK closes the stdin pipe; McpServer detects EOF and exits. No `taskkill` needed.

**McpServer crash**: `StdioClientTransport` will throw an error on the next `callTool()` call (or possibly emit an error event). Source: tech reference §3, MEDIUM confidence — not empirically verified (Gaps §7 in tech reference). Shell catches this, displays a reconnect notice, and attempts one reconnect:

```
1. await client.close() — best-effort cleanup
2. Re-resolve binary path
3. Spawn new process → new StdioClientTransport → new Client
4. await client.connect()
5. If reconnect succeeds: resume with existing conversation history
6. If reconnect fails: surface error, disable tool dispatch, allow read-only LLM conversation
```

No automatic retry loop — repeated crashes indicate a deeper problem the user should diagnose.

**Sequential tool dispatch (the ForceYielding constraint)**: The C# server uses `ConfigureAwaitOptions.ForceYielding`, meaning protocol-order dispatch is not guaranteed under burst load. The shell must always `await client.callTool(...)` before issuing the next call. Never fire-and-forget. Source: tech reference §3. The agentic loop design enforces this: tool calls are dispatched one at a time from a sequential `for` loop over the model's tool call list.

---

## 8. Stage 11 Phase Breakdown

### Phase A: Project scaffold + first contact
*Analogous to Stage 10 Phase A (MCP server scaffold)*

**Scope:**
- Create `devmind-shell` repo on Synology NAS
- `bun init`, install `ink`, `react`, `@modelcontextprotocol/sdk`, `openai`
- Verify the one remaining gap: `openai` npm SSE streaming on Bun 1.3.13. This is the first thing built in Phase A — not scaffolding. If it fails, switch to Node 20+ immediately. Source: tech reference §Gaps item 6.
- Implement McpServer spawn + connect + `listTools()` — no LLM yet
- Minimal Ink app: `<Box>` showing "Connected to McpServer. Tools available: [list]" and exits
- Wire the forward-slash path conversion for `--dir`
- Establish project structure: `src/mcp/`, `src/llm/`, `src/loop/`, `src/ui/`
- `bunfig.toml` or `package.json` engines field pinning Bun 1.3.13

**Done when**: Shell spawns McpServer, connects, retrieves the tool list, displays it in an Ink component, and exits cleanly. No hanging processes. Forward-slash path convention in place from commit one.

### Phase B: LLM integration + single-turn streaming
*Analogous to Stage 10 Phase B (core tool implementations)*

**Scope:**
- Wire `openai` npm to llama-server (`new OpenAI({ baseURL: "http://10.0.0.15:8080/v1", apiKey: "..." })`)
- Single-turn streaming chat: user types → model streams → Ink renders tokens live
- `<Static>` + `<ActiveTurn>` + `<InputBox>` components
- No tool calls yet — model runs in text-only mode
- Status bar: "Generating... (N tokens)" and "Ready"
- Ctrl+C / Escape to stop generation (verify `useInput()` vs `process.on('SIGINT')` — see §9)
- System prompt: minimal (cwd + empty tool list placeholder)

**Done when**: Interactive single-turn streaming conversation renders correctly in a real terminal. Token count visible in status bar. Stop works.

### Phase C: Agentic loop + tool dispatch
*Analogous to Stage 10 Phase C (tool dispatch + mutation tools)*

**Scope:**
- Detect `tool_calls` in model response deltas
- Sequential tool dispatch: `for` loop over tool calls, `await callTool()` each
- Inject `tool_result` messages and re-prompt
- Detect `task_done` tool call (Gemma 4 pattern) and stop the loop
- `<ToolCallView>` inline indicator: `[→ tool_name "arg"]` / `[← result summary]`
- `<ToolOutputRegion>` for streaming `run_shell` / `run_build` / `run_tests` output
- System prompt: include full tool list from `client.listTools()` + formatted tool descriptions
- Multi-turn history: accumulate turns across the conversation
- McpServer crash recovery (one reconnect attempt)

**Done when**: Can ask the model to read a file, patch it, and run the build. The full read → patch → build agentic cycle works end-to-end. `run_build` streaming output is visible while it runs.

### Phase D: Production hardening
*Analogous to Stage 10 Phase D (streaming + hardening)*

**Scope:**
- Config file (`~/.config/devmind/shell.json`): base URL, API key, McpServer path, model name
- `DEVMIND_BASE_URL` / `DEVMIND_API_KEY` env var overrides (never hardcode the endpoint)
- Context budget: rolling window trim at 80%/95% of context window
- DevMind.md loading: find `DevMind.md` / `CLAUDE.md` / `AGENTS.md` in cwd, inject into system prompt
- Full McpServer path resolution chain (env var → config file → adjacent build convention → PATH)
- Graceful shutdown (SIGINT, SIGTERM → client.close() → process exit)
- Startup errors surface clearly before Ink renders
- Color theming: at minimum, a dark theme consistent with DevMind VSIX color palette

**Done when**: A developer can clone the repo, set `DEVMIND_MCP_SERVER_PATH`, and run `bun src/index.ts` against their own project. No hardcoded values. Config documented in README.

---

## 9. Open Questions and Lower-Confidence Recommendations

These items are below ~80% confidence. They must be resolved before committing to the implementations that depend on them.

### 9.1 Stop/Cancel UX during SSE streaming [MEDIUM confidence — needs Phase B verification]

**Question**: Can `useInput()` (Ink's keyboard handler) fire reliably while SSE tokens are arriving at 50–100ms intervals?

**The concern**: Bun's event loop is single-threaded. If `openai` npm's async iterable stream holds the event loop between yields, `useInput()` callbacks may not fire promptly. The Ink render loop itself depends on the same event loop.

**Why it matters**: The Stop button is critical UX. A user who can't cancel a runaway generation will close the terminal in frustration.

**Proposed resolution**: In Phase B, test this empirically. Stream a long generation (ask the model to write 500 lines of code) and try pressing Escape at various points. Measure the response lag. If lag exceeds ~300ms, implement the mitigation:
- Ctrl+C: handled via `process.on('SIGINT', ...)` — fires as a signal, not through the Ink input pipeline. This is reliable regardless of event loop saturation.
- Escape: if `useInput()` is laggy, fallback to polling: check an `AbortController` signal at each `for await` iteration in the SSE loop.

Source for concern: tech reference §Gaps item 8.

### 9.2 openai npm SSE streaming on Bun [MEDIUM confidence — Phase A first test]

**Question**: Does `client.chat.completions.create({ stream: true })` work correctly on Bun 1.3.13?

**The concern**: Bun's `fetch` implementation differs from Node's in subtle ways. The `openai` npm package uses `fetch` internally for SSE. A Bun-specific fetch behavior could cause dropped chunks, encoding issues, or connection errors.

**Why it matters**: If SSE streaming doesn't work on Bun, the entire runtime decision reverses.

**Proposed resolution**: This is the first test in Phase A. If it fails, switch to Node 20+ before any other code is written. The project structure is runtime-agnostic (same `package.json`, same TypeScript) so the switch has zero cost beyond updating the run command.

Source: tech reference §Gaps item 6.

### 9.3 McpServer crash error surface from StdioClientTransport [MEDIUM confidence]

**Question**: When `DevMind.McpServer.exe` crashes mid-session, does `StdioClientTransport` throw synchronously on the next `callTool()`, emit an error event, or silently drop?

**Why it matters**: The crash recovery flow in §7 assumes a catchable error. If the SDK silently drops, the shell would deadlock waiting for a tool result that never arrives.

**Proposed resolution**: Test in Phase C by deliberately killing the McpServer process mid-tool-call and observing what the TypeScript SDK does. Implement recovery based on what's observed, not what's assumed.

Source: tech reference §Gaps item 7.

### 9.4 Gemma 4 parallel tool calls [LOW confidence]

**Question**: Does Gemma 4 31B via ik_llama.cpp ever emit parallel tool calls (multiple `tool_calls` in a single delta, OpenAI's parallel function calling feature)?

**Why it matters**: The agentic loop design assumes sequential tool calls. If the model sometimes emits multiple tool calls simultaneously, and the shell naively dispatches them sequentially, the model may be confused by out-of-order results.

**Current assumption**: Gemma 4 does not emit parallel tool calls against ik_llama.cpp. Source: tech reference §5 (llama-server does not fully implement parallel tool calling) — LOW confidence, not empirically verified.

**Proposed resolution**: In Phase C, observe actual model behavior. If parallel tool calls appear, dispatch them sequentially (one at a time) and send all results back in one batch. This matches how Claude Desktop handles the case.

### 9.5 Static + high-frequency streaming isolation [LOW confidence — but low-risk]

**Question**: Does `<Static>` content get accidentally re-drawn when the `<ActiveTurn>` component below it re-renders at ~50–100ms intervals?

**Current assumption**: No, because `<Static>` is explicitly designed to flush once and not re-render. The spike verified this at 300ms intervals.

**Why the confidence is not higher**: The spike used 300ms intervals, not the 50–100ms interval typical of token streaming. Ink's rendering budget may behave differently at higher frequency.

**Proposed resolution**: Verify in Phase B with a synthetic high-frequency test before building the full component tree. If `<Static>` re-draws, the mitigation is to throttle Ink re-renders (batch tokens, update every ~50ms rather than every token).

---

## 10. Risks and Known Footguns

### 10.1 opencode #5674: baseURL not forwarded through provider-abstraction layers

**Risk**: Using any SDK that wraps `openai` npm (Vercel AI SDK, LangChain, ai-sdk, etc.) silently drops the `baseURL` and routes all requests to `https://api.openai.com`.

**Mitigation**: Use `openai` npm directly. `new OpenAI({ baseURL, apiKey })` is the only safe pattern for local LLM endpoints. Verify in Phase A that the baseURL is being used by inspecting the outbound request (set llama-server's verbose logging on and confirm the request arrives).

**Rule**: Never hardcode `https://api.openai.com` anywhere in the codebase. Search for it in every PR.

Source: tech reference §5. HIGH confidence.

### 10.2 Windows backslash-tab mangling in subprocess args

**Risk**: Passing `C:\temp` as a subprocess arg results in `C:` + `[TAB]` + `emp` in some arg-passing contexts.

**Mitigation**: Always apply `path.replace(/\\/g, '/')` to absolute paths before they become subprocess arguments. Use the array form of `spawn(cmd, args)` with `shell: false` to avoid shell interpretation.

**Rule**: Any function that takes a path and passes it to a subprocess must forward-slashify it. Make this a utility function (`toSubprocessPath(p: string): string`) called at every callsite. Not an ad-hoc replace.

Source: tech reference §6. HIGH confidence (empirically discovered in Stage 10).

### 10.3 MCP server ForceYielding / sequential-dispatch constraint

**Risk**: Calling `client.callTool()` multiple times without awaiting each one sends concurrent JSON-RPC requests to the C# server. The C# server's `ConfigureAwaitOptions.ForceYielding` dispatch does not guarantee protocol-order execution under concurrent load.

**Mitigation**: The agentic loop always `await`s each tool call before dispatching the next. Use a typed `executeToolsSequentially(calls: ToolCall[]): Promise<ToolResult[]>` helper that enforces this. Never call `Promise.all()` over a list of tool calls.

Source: tech reference §3. MEDIUM confidence (SDK design reasoning; not empirically tested by running concurrent calls).

### 10.4 OpenAI SSE streaming on Bun — unverified

**Risk**: Bun's fetch diverges from Node's in a way that breaks `openai` npm's SSE stream reading.

**Mitigation**: Phase A first test. If it fails, Node 20+ is the fallback with zero code changes.

Source: tech reference §Gaps item 6.

### 10.5 McpServer stdout contamination

**Risk**: Any non-JSON written to stdout by `DevMind.McpServer.exe` corrupts the JSON-RPC framing and causes the TypeScript SDK to throw a parse error.

**Mitigation**: Already enforced in Stage 10 — the C# server logs to stderr. Do not regress this. If adding diagnostics to McpServer in Stage 11, they must go to stderr.

Source: tech reference §3. HIGH confidence.

### 10.6 dliedke reference (dropped per prompt instructions)

The "dliedke Ink wrapping pattern" mentioned in earlier planning had no findable source (tech reference §4). It has been dropped from this document. Any future Stage 12 work on an Ink-wrapping VSIX pattern should be referenced as "an Ink-wrapping VSIX pattern (TBD reference)" until a concrete source is identified.
