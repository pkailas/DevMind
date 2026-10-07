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
| `AgentJobManager.cs` | The job queue and lifecycle: one-at-a-time execution (machine-wide, via `MachineJobSlot`), state tracking, transcript persistence, result retention. |
| `MachineJobSlot.cs` | H-74: the machine-wide job slot (an exclusive handle on `%LOCALAPPDATA%\devmind\job-slot.lock`) and the ticket files that order waiters FIFO across server processes. |
| `JobStateFiles.cs` | H-74: per-job `<job_id>.state.json` in the tasks folder, so every server process can see every job. |

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
  (default 10, 1–240; a STALL window since H-37 — the job runner's watchdog cancels the
  agent turn only after that long with no progress, never on wall-clock time), and enqueues. Returns `job_id` + queue position.
- Jobs execute **strictly one at a time across the machine** — single GPU; a
  queue beats KV-cache thrash. Each server process keeps its own in-process queue
  (a server restart loses it — acceptable, the client re-submits), and the job at
  its head must also take the **machine-wide job slot** before it starts (H-74):
  Claude Desktop runs several McpServer processes, one per conversation, and they
  all share the one model behind `DEVMIND_ENDPOINT`.
  - The slot is an exclusive handle on `%LOCALAPPDATA%\devmind\job-slot.lock`
    (`DEVMIND_JOB_SLOT_LOCK` overrides the path): opened ReadWrite with
    `FileShare.Read`, so any second ReadWrite open fails with a sharing violation.
    The holder writes an owner record (pid, process start, job id, prompt snippet,
    working dir, acquired time) into the file; release clears it and closes the
    handle. A killed or crashed server's handle is closed by the OS, so the slot
    can never stay stuck. Not a named Mutex (thread-affine; a job spans async
    continuations) and not a named Semaphore (survives its holder's death).
  - The slot is held for the job's whole lifetime: test baseline, agent turn, and
    build/test verification. The result sidecar and state file are written before
    it is released.
  - A waiting job stays `queued`; it polls every 2 s, and `devmind_task_status`
    shows `queue_position` plus `waiting_for` {reason, job_id, pid, since,
    other_process, ahead}. The stall watchdog only runs during the agent turn, so
    waiting never counts against `timeout_minutes`. `devmind_task_cancel` ends the
    wait without the slot ever being taken.
  - Fairness: FIFO by queue time across processes. The waiter at the head of each
    process's queue holds a ticket file in `job-slot.lock.tickets\`
    (`<queued ticks>-<pid>-<job id>.ticket`, opened `DeleteOnClose`, so a dead
    waiter's ticket disappears with it); only the waiter whose ticket sorts first
    may take the slot. Without it, the process that just released the slot would
    always win (its next job tries at once; the others are mid-poll).
  - A lock path that cannot be used at all (permissions, bad path) degrades to
    "no machine slot" with a stderr line and a transcript note, rather than parking
    every job forever.
  - Only headless jobs are gated. The TUI and direct tool calls (`read_file`,
    `run_shell`, …) are not.
- Every job writes `{TasksDir}\<job_id>.state.json` (pid + process start, display
  state, queued/started/ended, prompt snippet, working dir, transcript, waiting_for)
  on each state change. `devmind_task_list` merges in other processes' jobs from
  these files (`other_process: true`, `owner_pid`; a queued/running job whose
  server is gone shows as `orphaned` for 24 h). `devmind_task_status` and
  `devmind_task_result` on another process's job id answer read-only from its state
  file / result sidecar instead of "Unknown job_id"; steer, cancel and continue stay
  with the owning conversation. `devmind_task_result` falls back to the on-disk
  transcript when the job is no longer in memory.
- Job ids are allocated machine-wide: a read-increment-write of
  `{TasksDir}\_jobcounter.txt` under an exclusive (`FileShare.None`) handle, retried
  for ~1 s. Only if the counter cannot be used at all does a server fall back to an
  in-memory counter, and then the id carries its pid (`job-<n>p<pid>`) so it still
  cannot collide with another process's id.
- A per-job `_active_<job_id>.json` marker is written while the job runs and
  removed when it ends; each server writes and clears only its own jobs' markers
  (several servers share the tasks folder). On startup, and on a status/result
  query for an unknown job, markers whose process is gone (pid dead, or reused —
  the marker records the process start time) are swept: the job gets a
  `stopped_incomplete` / `server_restart` result sidecar if it has none, so it is
  reported as having died with its server instead of showing as running forever.
  A legacy `_active.json` from an older server is read, never written.
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
