# DevMind Headless Agent via MCP — Design Spec (v1)

**Date:** July 3, 2026
**Status:** Approved design, not yet built
**Goal:** Claude Code (or any MCP client) delegates whole coding tasks to DevMind, which executes them agentically on the local model (Qwen3.6-27B via llama-server) at zero API token cost, returning a structured result.

## Decisions (Paul, Jul 3 2026)

| Decision | Choice |
|---|---|
| Session model | **One-shot only (v1).** Each task is a fresh conversation. Follow-up/session support deferred to v1.1. |
| Autonomy | **Full auto within `working_dir`.** All tools auto-approved including `run_shell`. Depth cap + wall-clock timeout + action journal are the safety net. |
| Registration | **User scope** — `claude mcp add --scope user devmind -- <exe>`; the `working_dir` parameter decides where it acts per call. |

## Architecture

### 1. Core: output-sink host refactor (removes duplication, enables headless)

`ConsoleAgenticHost` (currently `sealed` in DevMind.Cli, writes to `Console`) is promoted to
DevMind.Core as **`BufferedAgenticHost`** with all output routed through an
`Action<string, OutputColor>` sink instead of `Console.Write`. Rationale:

- In an MCP process, **stdout is the JSON-RPC wire** — any `Console.Write` corrupts the protocol.
- `TuiAgenticHost`'s own header admits the file/shell/patch/memory logic is "cribbed verbatim"
  from ConsoleAgenticHost — this refactor kills that duplication instead of adding a third copy.
- `DevMind.Cli` keeps a thin wrapper whose sink is `Console.Write` — zero behavior change.
- (Optional follow-up, NOT v1: rebase TuiAgenticHost on it too.)

The host additionally records an **action journal**: every file create/patch/append/delete/rename
(with paths), every shell command + exit code, build/test invocations + outcomes. This is the
audit trail returned to the caller.

### 2. Core: `HeadlessAgent`

```
HeadlessAgentResult RunAsync(prompt, ILlmOptions options, workingDir,
                             Action<string> progress, CancellationToken ct)
```

Wires `LlmClient` + `BufferedAgenticHost` + no-op `ILoopCallbacks` + `LoopDriver`/`LoopState`
(exact plumbing DevMind.Cli.Main already uses), seeds the prompt, iterates
`ProcessIterationAsync` until natural completion or depth cap.

`HeadlessAgentResult`:
- `Answer` — final model text
- `Actions[]` — the action journal
- `Iterations`, `ElapsedSeconds`, `HitDepthCap`, `Cancelled`
- `TranscriptPath` — full transcript written beside the MCP server logs for post-mortem

Headless system-prompt addendum: never `git commit` unless the task grants `allow_commit: true`;
operate only within the working directory; no interactive questions (decide and proceed,
note assumptions in the final answer).

### 3. McpServer: job-based tools

An agentic turn runs 1–15 min; MCP clients time out long tool calls. Job pattern:

| Tool | Behavior |
|---|---|
| `devmind_task_start(prompt, working_dir, max_depth?, timeout_minutes?, allow_commit?)` | Health-probes llama-server (fail fast with a clear message if down), validates absolute `working_dir`, enqueues, returns `job_id` + queue position. |
| `devmind_task_status(job_id)` | `queued (position N)` / `running (iteration i, elapsed s)` / `done` / `failed` / `cancelled`, plus the tail (~2 KB) of the live transcript. |
| `devmind_task_result(job_id)` | The full `HeadlessAgentResult` as JSON. Results retained for the server process lifetime (bounded ring, e.g. last 20 jobs). |
| `devmind_task_cancel(job_id)` | Cancels the job's CTS. |

**Concurrency:** jobs execute strictly one-at-a-time (single GPU; a queue beats KV-cache thrash).
The queue is in-process; server restart loses it (acceptable — client re-submits).

**Defaults:** `max_depth` = DevMind's AgenticLoopMaxDepth default (25), `timeout_minutes` = 30
hard wall-clock kill, `allow_commit` = false.

### 4. Coordination rules (usage discipline, not code)

- Claude Code does not edit files under a `working_dir` with a running DM job; treat DM-changed
  files as externally modified and re-read after `done`.
- Claude Code reviews the action journal + diffs like a junior dev's PR before building on them.

### 5. Deploy & registration

- `run-deploy.ps1` gains a second publish step: self-contained `DevMind.McpServer` into `dist\mcp\`.
- Registration (once): `claude mcp add --scope user devmind -- C:\...\dist\mcp\DevMind.McpServer.exe`
  (`working_dir` comes per-call, so no `--dir` needed at registration).

### 6. Testing

- Core: FakeSseServer-driven `HeadlessAgent` tests — scripted multi-iteration SSE (tool call →
  tool result → final answer), asserting the loop terminates, the journal records actions, and
  nothing writes to Console.
- MCP layer: direct stdio JSON-RPC smoke (initialize → tools/list → task_start/status/result
  against the fake) + a live end-to-end with the real model on a scratch directory.

### v1.1 candidates (explicitly deferred)

- Sessions / follow-up turns (`session_id` + `devmind_task_followup`)
- `devmind_digest(pdf)` and `devmind_library(question)` as MCP tools
- MCP progress notifications instead of poll-only status
- Multi-job parallelism if a second GPU/server appears
