# Stage 11 — Tech Reference

**Purpose**: Durable research artifact for the Ink shell build. Cite this instead of re-running discovery.  
**Last updated**: 2026-05-06 (research session 2026-05-05)  
**Audience**: Future Claude sessions and the user. Assumes familiarity with Ink, MCP, and local LLM tooling.

Confidence legend: **HIGH** = verified from primary source (doc URL, package.json, issue thread). **MEDIUM** = plausible from secondary source or consistent with multiple observations. **LOW** = inference; must be verified before acting on it.

---

## 1. Ink (React for terminals)

### Version and install

- **Version evaluated**: 7.0.2 (latest as of 2026-05-05)  
  Source: https://www.npmjs.com/package/ink — **HIGH**
- **Node requirement**: `"node": ">=20"` (engines field)  
  Source: npm page above — **HIGH**
- **Key peer deps**: React 18+, `yoga-layout-prebuilt` (native layout engine — this is the Bun footgun, see below)

### Rendering model

- Ink uses a **virtual terminal renderer** — it computes a diff between the previous frame and the new frame, then emits only the delta. This is what makes streaming-token UX viable: each new token triggers a re-render, but only the changed cells are written to the terminal. Not polling, not full redraws.  
  Source: Ink README, rendering section — **HIGH**
- **Alternate screen buffer** is opt-in via `render({ useAlternateScreen: true })`. When enabled, the shell renders in a separate terminal buffer (like vim/htop); the primary buffer (scrollback) is preserved and restored on exit. When disabled (the default), Ink renders inline in the scrollback buffer.  
  Source: Ink README — **HIGH**
- For a chat shell, **inline rendering (no alternate screen)** is likely the right choice — preserves conversation history in the terminal's scrollback. Claude Code appears to use a similar pattern. This decision affects how "sticky" the status row can be.  
  Confidence: **MEDIUM** (based on CC observation; no source confirming CC uses inline mode specifically)

### Component model — relevant to a chat shell

- Standard React component tree. `useInput()` hook for keyboard events. `useFocus()` / `useFocusManager()` for focus traversal between interactive elements.
- **`<Static>`** component: renders items that will never change (past conversation turns). Critically, items inside `<Static>` are not re-rendered on subsequent frames — they're flushed to the terminal and the cursor advances past them. This is the idiomatic pattern for append-only conversation history: past turns in `<Static>`, current streaming turn in a live component below it.  
  Source: Ink docs — **HIGH**
- **`<Box>`** and **`<Text>`** for layout. Flexbox model via yoga-layout. No scroll region primitive — scrolling is emulated either by windowing (only render the last N items) or by relying on the terminal's own scrollback.
- There is no built-in "sticky footer" primitive. A floating status row (à la Claude Code's "[■ Generating…]" bar) requires rendering it last in the component tree and using terminal cursor positioning tricks, OR relying on alternate-screen mode where you control the full display.

### Known limitations / gotchas

- **No native scrollable region**: Ink has no `<ScrollableBox>`. Large outputs require manual windowing. For a chat shell, the `<Static>` + live-tail pattern is the practical substitute.
- **`<Static>` is append-only and irreversible**: Once an item enters `<Static>`, it cannot be updated. This matches conversation history semantics but means you cannot retroactively correct a displayed turn.
- **Windows install speed**: `npx create-ink-app` has been reported to take 40–50 minutes and stall on Windows 10. Not necessarily a runtime issue, but worth using direct `npm install ink` rather than the scaffolding tool.  
  Source: GitHub issues — **MEDIUM** (reported by users, not reproduced on Beast)
- **React DevTools on Windows**: Ink may block if it tries to connect to React DevTools on Windows with an empty event loop. Only relevant in dev mode.  
  Source: GitHub issues — **MEDIUM**

### Bun compatibility

- **Bun + Ink has a known breakage**: Bun issue #2034 reports a compile error on a 3-line Ink program. The root cause is `yoga-layout-prebuilt`, a native module dependency that Ink requires for its flexbox layout engine.  
  Source: https://github.com/oven-sh/bun/issues/2034 — **HIGH**
- Whether this is fixed in current Bun versions was **not verified**. The issue was still open as of research date.
- **Bottom line**: Bun + Ink compatibility is unverified on the Beast. Treat as blocked until a 30-minute spike confirms otherwise. **Do not assume it works.**

---

## 2. Bun

### Version and context

- Bun version evaluated: **not installed on Beast as of 2026-05-05**. Research was secondary source only.  
  Confidence: **HIGH** for SDK claims, **MEDIUM/LOW** for any "works on Beast" claims — none were made.

### child_process / subprocess on Windows

- Bun supports `node:child_process` — `spawn()`, `stdio: 'pipe'` — for the first three file descriptors (stdin=0, stdout=1, stderr=2).  
  Source: https://bun.com/docs/runtime/child-process — **HIGH**
- **Extra file descriptors (fd > 2) are NOT implemented in Bun** (issue #4670). This matters only if you need IPC channels or extra stdio streams. For MCP over stdio (stdin/stdout only), this is not a problem.  
  Source: https://github.com/oven-sh/bun/issues/4670 — **HIGH**
- MCP server subprocess uses only stdin/stdout for JSON-RPC framing. `spawn()` with `stdio: ['pipe', 'pipe', 'inherit']` is sufficient and should work on Bun.  
  Confidence: **MEDIUM** (logic holds; not empirically verified on Windows)

### @modelcontextprotocol/sdk on Bun

- The TypeScript SDK README officially states: "The SDK runs on Node.js, Bun, and Deno."  
  Source: https://github.com/modelcontextprotocol/typescript-sdk — **HIGH**
- However, there is a CommonJS compatibility issue: `pkce-challenge` (an SDK dependency) is ESM-only and will fail when required from a CommonJS context (`ERR_REQUIRE_ESM`). This is a problem for CJS projects, not ESM projects.  
  Source: SDK issue #217 — **HIGH**
- Bun's native CJS/ESM interop may paper over this, but this was **not verified empirically**.

### Package manager behavior

- Bun ships its own package manager (`bun install`). It is fast (parallel downloads, binary lockfile `bun.lockb`). Compatible with `package.json` and can install npm packages.
- `bun.lockb` is binary and not human-readable. Team projects that also need Node support should keep a `package-lock.json` or `pnpm-lock.yaml` as fallback, or commit to Bun as the sole runtime.

### Verdict for Stage 11

Bun is **not recommended as the primary runtime for Stage 11** given the unverified Ink compatibility issue (#2034). Node 20+ LTS with npm or pnpm is the safe choice. Bun can be revisited after a dedicated spike.  
If the spike confirms Bun + Ink works on Windows, the runtime switch is low-cost — package.json and TypeScript config are identical; only the runner changes.

---

## 3. @modelcontextprotocol/sdk (TypeScript)

### Version

- **Version evaluated**: 1.11.0 (latest stable as of 2026-05-05; npm page showed 1.29.0 but this may be a stale cache artifact — treat as "approximately 1.x, check on install")  
  Source: https://www.npmjs.com/package/@modelcontextprotocol/sdk — **HIGH** for "1.x", **LOW** for exact minor

### Client API surface — stdio transport

The relevant classes for spawning a stdio MCP server and calling tools:

```typescript
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const transport = new StdioClientTransport({
  command: "DevMind.McpServer.exe",
  args: ["--dir", "C:/Users/pkailas/source/repos/MyProject"],  // forward slashes — see §6
});

const client = new Client({ name: "devmind-shell", version: "1.0.0" }, { capabilities: {} });
await client.connect(transport);

const result = await client.callTool({ name: "read_file", arguments: { path: "Program.cs" } });
```

- `StdioClientTransport` spawns the subprocess and wires stdin/stdout for JSON-RPC framing.  
  Source: MCP TypeScript SDK source + MCP transports docs — **HIGH**
- `client.callTool()` is the primary tool invocation API.
- `client.listTools()` returns all registered tools from the server — use this to enumerate what McpServer exposes.

### JSON-RPC framing over stdio

- The SDK handles JSON-RPC 2.0 framing (newline-delimited JSON) internally. You do not deal with buffering or partial reads.
- **Critical**: The MCP server MUST NOT write anything to stdout except JSON-RPC messages. Any debug logging on stdout will corrupt the framing. Log to stderr or a file.  
  Source: MCP transports doc: "Do not write logs to stdout on stdio servers" — **HIGH**
- The SDK uses newline-delimited JSON (one complete JSON object per line). No length-prefix framing.

### Request serialization — does the TypeScript client serialize calls?

- The C# server uses `ConfigureAwaitOptions.ForceYielding`, which means tool dispatch is not guaranteed in protocol-arrival order under burst/parallel load. Real clients (Claude Desktop, MCP Inspector) send one tool call, await the response, then send the next.
- The TypeScript SDK client does **not** appear to serialize requests automatically — it sends them as they come and correlates by `id`. If the shell calls `client.callTool()` concurrently without awaiting, requests can arrive at the server out of order relative to when the shell wanted them processed.
- **Safe pattern**: always `await client.callTool(...)` before issuing the next call. Do not fire-and-forget tool calls. This matches how all production MCP clients behave.  
  Confidence: **MEDIUM** (reasoning from SDK architecture; not verified by reading client source)

### Connection lifecycle

- `client.connect(transport)` establishes the subprocess and performs MCP handshake.
- The SDK does **not** implement automatic reconnect. If `DevMind.McpServer.exe` crashes, the transport enters an error state and you must create a new `Client` + `StdioClientTransport`.
- On `client.close()`, the subprocess receives a close signal (stdin closes) and should exit. McpServer's graceful shutdown is handled by the C# SDK's stdio server lifecycle.  
  Confidence: **MEDIUM** (consistent with SDK design; not verified by crashing the server in a test)

### ESM vs CJS

- The SDK is ESM-first. Use `"type": "module"` in `package.json` and `.js` imports in TypeScript output, or configure `tsconfig.json` with `"module": "NodeNext"`.
- Avoid CJS project structure — the `pkce-challenge` dep will fail.  
  Source: SDK issue #217 — **HIGH**

### Examples and bookmarks

- Official SDK repo with examples: https://github.com/modelcontextprotocol/typescript-sdk/tree/main/src/examples
- MCP Inspector source is a good reference for how a real client wires `StdioClientTransport`: https://github.com/modelcontextprotocol/inspector

---

## 4. Reference Implementations

### sst/opencode

**Architecture**:  
- Bun + TypeScript monorepo (Turbo build). 
- Client-server: a long-running backend process handles LLM inference, tool execution, session persistence, and MCP server connections. Multiple frontends (terminal TUI, desktop, web) connect to the backend over local HTTP/SSE.
- TUI frontend uses **Ink** for rendering. The floating status row is rendered as the last component in the Ink tree, kept "sticky" via Ink's render cycle.
- LLM integration via **Vercel AI SDK** (not direct fetch) — provides unified provider abstraction. This is how it supports 75+ providers.
- MCP tools are fully supported — backend registers MCP servers, enumerates tools, and routes model-requested tool calls.

**What's worth copying**:  
- The client-server split enabling multiple frontends. If DevMind shell later gains a web UI (Stage 12 Phase E HTTP+SSE path), this pattern pays off.
- The Ink `<Static>` + live-tail pattern for conversation history — past turns in `<Static>`, current streaming turn rendered live below.
- Color theming (Catppuccin, Dracula, etc.) handled as a configuration layer, not hardcoded in components.

**What's wrong with it (issue #5674)**:  
- Custom OpenAI-compatible provider options (baseURL, apiKey) configured in `opencode.json` are **not forwarded to the actual API client**. The Vercel AI SDK's OpenAI provider is constructed without the custom baseURL, so requests go to `https://api.openai.com` regardless.  
  Source: https://github.com/sst/opencode/issues/5674 — **HIGH**
- Impact: opencode v1.0.164 cannot talk to a local llama-server. The Vercel AI SDK wrapping adds a bug-introduction surface that raw fetch does not have.
- **Lesson**: Using any provider-abstraction SDK (Vercel AI SDK, LangChain) between you and the LLM adds a forwarding layer that can silently drop custom endpoint configuration. Prefer direct `fetch()` to the OpenAI-compatible endpoint for local LLM use.

### dliedke Ink wrapping pattern

- **Not found**. Searches for "dliedke" + Ink on GitHub and the web returned no relevant results. The reference may be to an internal/private repo, a forum post, or a misremembered attribution.  
- **Action required**: Ask the user for the original source before Stage 11 design work references this pattern.  
  Confidence: **N/A — gap**

### ruvnet/open-claude-code

**The failure mode**:  
- Claude Code (Anthropic's official CLI) has approximately **30 OAuth and admin endpoints** that use a hardcoded `BASE_API_URL = "https://api.anthropic.com"`. These endpoints ignore `ANTHROPIC_BASE_URL`.  
- Affected: `/api/oauth/*`, `/api/claude_code/*`, `/v1/sessions/*`, `/v1/mcp_servers`, event logging. Regular inference calls at `/v1/messages` DO respect `ANTHROPIC_BASE_URL`.  
- This breaks users trying to route Claude Code through custom credential gateways or proxy servers.  
  Source: https://github.com/anthropics/claude-code/issues/48011 — **HIGH**
- `open-claude-code` is a decompiled reconstruction of Claude Code (Anthropic accidentally shipped source maps in the npm package on 2026-03-31). It inherits the same hardcoded-endpoint architecture.
- There is no documented configuration workaround. The hardcoding is structural, not accidental.

**Relevance to Stage 11**: DevMind shell must accept `DEVMIND_BASE_URL` (or equivalent) from env or config and pass it faithfully to every HTTP call, including any OAuth or session management calls. Never hardcode an endpoint URL. Verify this from the first commit — retrofitting is painful.

### Claude Code (observable behavior)

_These are observations, not source-grounded claims. Marked LOW confidence unless noted._

- CC appears to use inline Ink rendering (no alternate screen), keeping conversation in the terminal scrollback — **LOW** (observation)
- CC's floating status row updates in place within the current render frame — the "■ Generating… (123 tokens)" line doesn't push prior output up — **MEDIUM** (reproducible observation, mechanism not confirmed)
- CC serializes all MCP tool calls (one at a time, awaits response before next) based on observable behavior in multi-tool responses — **MEDIUM**
- CC's input box supports multiline (Shift+Enter for newline, Enter to submit) — **HIGH** (verified by usage)

---

## 5. OpenAI-Compatible API Gotchas for Local llama-server

### The baseURL forwarding bug pattern

Any third-party SDK that wraps the OpenAI client (Vercel AI SDK, LangChain, etc.) introduces a layer where baseURL can be dropped. The pattern is:

1. User sets `baseURL: "http://10.0.0.15:8080/v1"` in config.
2. SDK constructs its OpenAI provider client, but the wrapping layer doesn't pass `baseURL` to the underlying `openai` npm package constructor.
3. All calls go to `https://api.openai.com` → fail with 401 or 404.

**Prevention**: Use `openai` npm package directly (`new OpenAI({ baseURL, apiKey })`), or raw `fetch()`. Do not use provider-abstraction SDKs for local LLM endpoints unless you verify baseURL forwarding in the SDK source before adopting it.

### ik_llama.cpp / llama-server OpenAI compatibility

Endpoint: `http://10.0.0.15:8080/v1`

**What works** (verified in Stage 10 McpServer and prior DevMind VSIX work):  
- `POST /v1/chat/completions` with SSE streaming (`stream: true`) — works
- `stream_options: { include_usage: true }` — works, usage stats returned in final SSE chunk
- `temperature`, `max_tokens`, `stop` parameters — work
- Tool call response format (OpenAI function-calling shape) — works with compatible models

**What may differ from real OpenAI** — not fully investigated:  
- `/v1/models` endpoint — present but may return partial data depending on llama-server version
- `/v1/embeddings` — not verified  
- Parallel tool calls in a single response — behavior may differ from OpenAI
- Token usage accounting in streaming — present but accuracy vs OpenAI not compared

**Active model**: Gemma 4 31B Dense Q8_0 at 131K context. Served by ik_llama.cpp's llama-server.  
- Model uses OpenAI tool-call format when tools are registered.
- Some models (Gemma 4) emit completion as a `task_done` tool call with a `summary` parameter rather than a text DONE directive — the DevMind WPF extension handles this via `ToolCallMapper`. The Ink shell will need the same mapping.

### Streaming response format

- SSE format: `data: {...}\n\n` lines, terminated by `data: [DONE]\n\n`
- Delta object shape: `choices[0].delta.content` (text tokens) or `choices[0].delta.tool_calls` (tool call increments)
- The `openai` npm package handles this transparently via `client.chat.completions.create({ stream: true })`
- Do not parse SSE manually — use the SDK's async iterable stream interface

---

## 6. Windows Path / Arg-Passing

### The `--dir` backslash-tab mangling (Stage 10 discovery)

When passing Windows absolute paths as CLI arguments to a subprocess (via `child_process.spawn` or equivalent), backslash sequences are interpreted as escape sequences in some contexts:

- `--dir C:\temp` → the `\t` in `\temp` gets interpreted as a tab character in certain arg-passing scenarios
- This was discovered in Stage 10 MCP Inspector testing when `--dir C:\Users\pkailas\source\repos\...` produced a mangled path

**Enforced convention**: Always use **forward-slash paths** in `--dir` arguments passed to `DevMind.McpServer.exe`.  
`--dir C:/Users/pkailas/source/repos/MyProject` ✓  
`--dir C:\Users\pkailas\source\repos\MyProject` ✗

The C# McpServer accepts forward-slash paths — `Path.GetFullPath()` normalizes them on Windows.

### Implementation requirement for the shell

- When the shell spawns `DevMind.McpServer.exe`, it must convert the working directory path to forward-slash form before passing it as an argument.
- `path.replace(/\\/g, '/')` is sufficient. Or use `path.posix`-style normalization.
- This applies to any path passed as a string argument to a subprocess, not just `--dir`.

### Other Windows arg-passing considerations

- **Spaces in paths**: Wrap in quotes or pass as separate array elements to `spawn()`. Using the array form of `spawn(cmd, args)` avoids shell interpretation of spaces. Do not use `spawn(cmd, ['--dir ' + path])` — pass `['--dir', path]`.
- **PowerShell vs cmd.exe**: `child_process.spawn` on Windows uses the Win32 `CreateProcess` API directly when `shell: false` (the default for array args). This bypasses both cmd.exe and PowerShell, avoiding shell-escaping issues entirely. Always pass `shell: false` (or equivalently, use the array args form).

---

## 7. SDK Tier Landscape

The MCP SDK ecosystem has three tiers as of 2026-05-05:

**Tier 1 (production-stable, first-party)**: `@modelcontextprotocol/sdk` (TypeScript/Node), `modelcontextprotocol` Python SDK, `ModelContextProtocol` .NET SDK. These are maintained by Anthropic/MCP team, have stable APIs, and are what Claude Desktop and MCP Inspector use. The TypeScript SDK is the reference implementation.

**Tier 2 (community, production-usable)**: Various language bindings (Go, Rust, Java) with active maintenance but no official Anthropic backing. API surface may lag the spec slightly on new features.

**Tier 3 (experimental/dead)**: Early community ports that predate the spec stabilizing. Often missing transport implementations or have breaking-change gaps.

**Why this matters for Stage 11**: The Ink shell must use Tier 1 (`@modelcontextprotocol/sdk`) to ensure wire-compatibility with DevMind.McpServer.exe (which uses the .NET SDK, also Tier 1). Tier 2 or 3 clients risk protocol version mismatches or missing capability negotiation.

TypeScript is the correct choice for the shell — it gives direct access to the Tier 1 reference implementation, has the largest ecosystem of Ink components and examples, and is the language opencode and MCP Inspector are written in.

---

## Gaps — Not Yet Investigated

Items that must be verified before committing to design choices:

1. **Bun + Ink on Windows (Beast)**: Issue #2034 may be fixed in current Bun. Needs a 30-minute spike: `bun add ink react`, write a 5-line Ink component, run it. If it works, Bun becomes viable. If it still breaks on `yoga-layout-prebuilt`, Node 20 it is.

2. **dliedke Ink wrapping pattern**: Source not found. Ask the user before referencing this pattern in the Stage 11 design doc.

3. **Ink `<Static>` + streaming token interaction**: Does `<Static>` interact cleanly with a live streaming component below it in the same render tree? Specifically: when the streaming component re-renders on each token, does `<Static>` content get re-drawn? Theory says no (that's the point of `<Static>`), but this needs empirical verification.

4. **ik_llama.cpp parallel tool calls**: Does Gemma 4 via llama-server ever request multiple tool calls in a single response (OpenAI's parallel function calling feature)? If yes, the shell's sequential-dispatch constraint becomes more complex to implement correctly.

5. **`@modelcontextprotocol/sdk` v2.0**: The npm page referenced an anticipated v2.0.0. If released before Stage 11 ships, there may be breaking API changes. Check version on install day and pin the lockfile.

6. **`openai` npm package streaming on Bun**: If Bun is used, verify that the `openai` npm package's SSE streaming (`client.chat.completions.create({ stream: true })`) works correctly on Bun. Node fetch vs Bun fetch differ in subtle ways that can affect SSE stream reading.

7. **McpServer crash / reconnect**: What does `StdioClientTransport` do when the subprocess dies mid-session? Does it throw on the next `callTool()` call, or does it silently drop? Affects error recovery design in the shell.

8. **Ink focus management with streaming**: Can `useInput()` / `useFocus()` reliably capture keyboard events (Escape to stop, Ctrl+C) while the main thread is busy processing SSE tokens? Or does long streaming block the Ink render loop? This determines whether the Stop/Cancel UX is viable without a worker thread.
