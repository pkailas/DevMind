# DevMind MCP Server — Developer's Guide

`DevMind.McpServer` is a stdio JSON-RPC MCP server that fronts the DevMind
engine (`DevMind.Core`). It lives in the DevMind solution and references Core
one-way; nothing in Core knows MCP exists.

---

## Project layout

| File | Role |
|---|---|
| `Program.cs` | Host startup: stdio transport, tool registration, logging (with env redaction). |
| `McpServices.cs` | Shared service container for the tool classes (working directory, file cache, shell runner, LSP router, …). |
| `DevMindTools.cs` | The granular tool surface (~40 tools): files, search, LSP, shell, build/test, memory, library/RAG, db, clipboard, network. |
| `AgentTaskTools.cs` | The headless-agent job tools: `devmind_task_start` / `status` / `result` / `list` / `continue` / `cancel`. |
| `AgentJobManager.cs` | The job queue and lifecycle: one-at-a-time execution, state tracking, transcript persistence, result retention. |

Tools are declared with `[McpServerTool(Name = "...")]` +
`[Description(...)]` attributes; descriptions are part of the product surface —
they are what the calling model reads, so treat them like UX copy and keep
sizing guidance (depth estimates, defaults, failure modes) in them.

## The cardinal rule: stdout is the wire

In an MCP process, **stdout carries the JSON-RPC protocol**. Any stray
`Console.Write` corrupts the stream and kills the session. This constraint
drove the core refactor that enables headless operation:

- `ConsoleAgenticHost` (originally sealed in `DevMind.Cli`, writing to
  `Console`) was promoted into `DevMind.Core` as **`BufferedAgenticHost`**,
  with all output routed through an `Action<string, OutputColor>` sink.
- `DevMind.Cli` keeps a thin wrapper whose sink is `Console.Write` — zero
  behavior change for the CLI.
- The MCP server's sink writes to buffers/transcripts, never the console.

`BufferedAgenticHost` also records the **action journal**: every file
create/patch/append/delete/rename (with paths), every shell command and exit
code, and build/test invocations with outcomes. That journal is the audit trail
returned to callers in `devmind_task_result`.

## HeadlessAgent

The engine-side entry point for autonomous runs:

```
HeadlessAgentResult RunAsync(prompt, ILlmOptions options, workingDir,
                             Action<string> progress, CancellationToken ct)
```

It wires `LlmClient` + `BufferedAgenticHost` + no-op `ILoopCallbacks` +
`LoopDriver`/`LoopState` — the same plumbing `DevMind.Cli.Main` uses — seeds
the prompt, and iterates `ProcessIterationAsync` until natural completion,
depth cap, timeout, or cancellation.

`HeadlessAgentResult` carries: `Answer` (final model text), `Actions[]` (the
journal), `Iterations`, `ElapsedSeconds`, `HitDepthCap`, `Cancelled`, and
`TranscriptPath` (full transcript written beside the server logs for
post-mortem).

The headless system-prompt addendum enforces: never `git commit` unless the
task granted `allow_commit`; operate only within the working directory; no
interactive questions — decide, proceed, and note assumptions in the answer.

## The job layer

An agentic turn runs 1–15 minutes; MCP clients time out long tool calls. Hence
the job pattern in `AgentTaskTools` / `AgentJobManager`:

- `devmind_task_start` validates the prompt and absolute `working_dir`,
  **health-probes the model server** (fail fast beats a queued job dying
  minutes later), clamps `max_depth` (default 40, 1–100) and `timeout_minutes`
  (default 30, 1–240), and enqueues. Returns `job_id` + queue position.
- Jobs execute **strictly one at a time** — single GPU; a queue beats KV-cache
  thrash. The queue is in-process; a server restart loses it (acceptable — the
  client re-submits). `devmind_task_result` falls back to the on-disk
  transcript when the job is no longer in memory.
- A `_active.json` marker is written while a job runs; on status queries,
  `CheckStaleActiveMarker` detects jobs that died mid-run (server killed) and
  reports them honestly instead of showing them running forever.
- Display state maps to caller trust: `done` only when the work is actually
  trustworthy — a depth-capped run or failed build verification surfaces as
  `stopped_incomplete`.
- Post-run verification: the job runner itself builds the `working_dir`
  (`verify_build`, default on) and optionally runs `dotnet test`
  (`verify_tests`) and attaches the outcomes to the result — so callers don't
  have to re-verify.
- `devmind_task_continue` resumes a finished job's conversation with full
  context, returning a new `job_id` chained to it. Conversations expire after
  ~60 minutes idle.

## Granular tool conventions (DevMindTools.cs)

- **Path safety** — `ResolveFilePath` + `PathContainmentCheck` keep every
  resolved path inside the session working directory before any read/write.
- **Outline-first reads** — files over the size threshold return a declaration
  outline with line numbers; callers follow up with ranged reads. This is a
  deliberate context-budget feature; don't bypass it casually.
- **Output caps** — `CapShellOutput` truncates shell output at 1000 lines /
  50 KB, whichever comes first.
- **Background shell** — long commands run detached (`StartBackgroundShell`,
  `ShellJobStatus` polling with a bounded tail buffer) to dodge client
  timeouts.
- **Build detection** — `DetectBuildCommand` auto-detects per working
  directory; `DEVMIND_BUILD_COMMAND` overrides.
- **git via read_file** — `read_file` special-cases `git log` / `git diff …`
  and delegates to `ShellRunner` (`ReadGitAsync`).
- **BOM handling** — `IsScriptFileExtension` suppresses UTF-8 BOMs for script
  types where a BOM breaks the interpreter.

When adding a tool: put real behavior in `DevMind.Core` if any UI skin could
ever want it; keep the MCP method a thin attribute-decorated wrapper; write the
description for the calling model (what it does, when to use it, limits and
defaults); and never write to stdout.

## Deployment

`run-deploy.ps1` publishes the server self-contained/single-file into
`dist\mcp\` (its own folder — the TUI's single-file publish would clobber
shared DLL names in `dist\`). Registration targets that exe:

```powershell
claude mcp add --scope user devmind -- <repo>\dist\mcp\DevMind.McpServer.exe
```

## Testing

- **Core**: FakeSseServer-driven `HeadlessAgent` tests — scripted
  multi-iteration SSE (tool call → tool result → final answer) asserting the
  loop terminates, the journal records actions, and nothing writes to Console.
- **MCP layer**: direct stdio JSON-RPC smoke tests (initialize → tools/list →
  task_start/status/result against the fake), plus a live end-to-end with the
  real model on a scratch directory.

## Deferred (v1.1 candidates)

Sessions/follow-up beyond `devmind_task_continue`'s current model, `devmind_digest(pdf)` and
`devmind_library(question)` as MCP tools, MCP progress notifications instead of
poll-only status, and multi-job parallelism if a second GPU appears.

---

*DevMind is a product of iOnline Consulting LLC.*
