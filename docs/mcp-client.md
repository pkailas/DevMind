# MCP client (external MCP servers)

DevMind can act as an MCP **client**: launch external MCP servers over stdio, list their
tools and call them. Motivating use: the ComfyUI server (`comfy-mcp`) so DM agents can drive
ComfyUI.

Status (2026-09-29): **parts 1–3 of 4 done.**
- Part 1: config and client manager.
- Part 2: the agent loop can advertise and run the tools of servers that are already started.
- Part 3: the TUI and headless jobs create the manager and start servers, so MCP tools
  actually reach the model.

**The CLI host (`DevMind.Cli`) is out of scope.** It never creates a manager, so it has no MCP
tools.

## Config

An `mcpServers` object in the global config `%APPDATA%\devmind\devmind.json`
(`DevMindPaths.GlobalDir`; `DEVMIND_GLOBAL_DIR` overrides). The shape mirrors
`claude_desktop_config.json`, so an entry can be pasted across:

```json
"mcpServers": {
  "comfy": {
    "command": "C:\\Users\\pkailas\\AppData\\Roaming\\Python\\Python314\\Scripts\\comfy-mcp.exe",
    "args": [],
    "env": { "COMFY_BIN": "C:\\Users\\pkailas\\AppData\\Roaming\\Python\\Python314\\Scripts\\comfy.exe" },
    "tools": ["server_info", "validate_workflow", "run_workflow"],
    "callTimeoutSeconds": 900,
    "autoStart": true
  }
}
```

| Field | Required | Meaning |
|-------|----------|---------|
| key (`comfy`) | yes | Server name: `[a-z0-9_-]+`, no `__`, no trailing `_` (it becomes the middle of `mcp__<server>__<tool>`). |
| `command` | yes | Executable. `.exe`/`.com` run directly; anything else (`npx`, a `.cmd` shim) runs via `cmd.exe /c`. |
| `args` | no | Array of strings. |
| `env` | no | Overrides applied on top of the host's **full** environment. A `null` value removes a variable. |
| `tools` | no | Allowlist. Absent = every tool the server lists; `[]` = none (warns). |
| `callTimeoutSeconds` | no | Per-call timeout, positive integer, default 120. A bad value warns and uses the default. |
| `autoStart` | no | TUI only: start this server in the background at session start. Default `true`; a non-boolean warns and uses `true`. Headless jobs ignore it and start exactly the servers the job names. |
| `type` | no | Only `"stdio"` is accepted; a pasted HTTP/SSE entry is skipped with a warning. |

- No block, or an empty one, means the feature is off. Nothing else changes.
- A bad entry (invalid name, missing command, wrongly typed field) is skipped with a warning.
  The other entries still load.
- The file must be strict JSON. Comments make the whole of `devmind.json` unreadable, as they
  do for every other setting.
- `TuiConfig` keeps the block verbatim (`TuiConfig.McpServers`, a raw `JsonObject`), so a
  TUI save (`/mode`, `/rules`, …) round-trips it unchanged. **A TUI built before this change
  deletes the block on its next save**, so redeploy before relying on it (watchlist H-39).
- Parsing starts no process and probes no path. A slow load would blow Claude Desktop's 60 s
  MCP-initialize limit, as the Sep 9 UNC probe in WriteRootPolicy did.

## Client manager (`DevMind.Core/McpClientManager.cs`)

- SDK: `ModelContextProtocol` 2.2.0, the same version DevMind.McpServer uses. The client
  types come from `ModelContextProtocol.Core`: `McpClient.CreateAsync(IClientTransport,
  McpClientOptions, ILoggerFactory, CancellationToken)`, `McpClient.ListToolsAsync(RequestOptions,
  CancellationToken)` → `IList<McpClientTool>` (`.ProtocolTool.InputSchema` is a `JsonElement`),
  and `McpClient.CallToolAsync(string, IReadOnlyDictionary<string, object?>, IProgress<…>,
  RequestOptions, CancellationToken)` → `CallToolResult`.
- **DevMind starts the process itself**, then hands its stdin/stdout to the SDK's
  `StreamClientTransport`. `StdioClientTransport` never exposes its `Process`, which rules out
  job-object containment. Owning the process gives three things:
  - A `WindowsJobObject` kill-on-close job. The server and everything it spawns die with the
    host. For example, `comfy-mcp.exe` is a pip launcher stub whose `python.exe` child does the
    real work.
  - The full user environment. ShellRunner's stripped child environment is deliberately not
    reused.
  - A 30-line stderr tail for error messages.
- Servers start lazily on first `ListToolsAsync` / `CallToolAsync`, or on an explicit `StartAsync`.
- `CallToolAsync` never throws into the agent loop, apart from the caller's own cancellation.
  - Every failure comes back as a `[MCP ERROR] server '<name>' …` string that includes the
    stderr tail.
  - A server that failed to start or died gets **one** automatic restart attempt on the next
    call.
  - A second consecutive failure stops relaunching until a call succeeds again.
  - A timed-out call leaves the session up, because a slow tool is not a dead server.
- Result → text for the text-only worker model:
  - Text blocks are joined with newlines.
  - Image, audio and blob resources are written to `%TEMP%\devmind\mcp\<server>\<guid>.<ext>`
    and returned as `[image saved: <path>]`.
  - Structured content is rendered as pretty JSON when no text block already carries it.
  - `isError` results are prefixed `[MCP TOOL ERROR]`.
- `DisposeAsync` disposes the client, which closes stdin (graceful EOF). It then terminates the
  job and waits until the job's process list is empty.

## In the agent loop (part 2)

### Injection

Collaborators are property-injected, following the existing `LoopDriver.Liveness` /
`LlmClient.StreamDataReceived` pattern. A host sets the same manager in two places:

- `LlmClient.McpClients` (an `IMcpClientManager`) advertises the tools.
- `LoopDriver.McpTools` (an `IMcpToolInvoker`) runs them. LoopDriver hands it to each
  iteration's `AgenticExecutor.McpTools`.

Both default to null, which means the feature is off. MCP calls are not `IAgenticHost` methods,
because they are host-agnostic.

### Exposure

- `McpClientManager.GetExposedTools()` returns a cached snapshot of OpenAI function objects for
  **started servers only**, with each server's allowlist applied. It does no I/O, because it
  runs on every request build.
- The snapshot is refilled when a server starts, restarts or is re-listed. It is emptied when
  the server dies or the manager is disposed.
- The tool name is `mcp__<server>__<tool>`. The description is prefixed `[<server> MCP]`.
  `parameters` is the server's input schema; an empty schema becomes
  `{"type":"object","properties":{}}`.
- A qualified name longer than 64 characters, or containing characters outside
  `[A-Za-z0-9_-]`, cannot be called through the chat API. That tool is skipped, and a warning
  is written once to `DevMindLog`.
- `LlmClient` sends `ToolRegistry.BuildToolsArray(extra)`: the static catalogue with the MCP
  tools appended as deep clones. The no-argument `BuildToolsArray()` still returns only the
  static set. `ToolCatalogueRegistryParityTests` and `ToolCount` rely on that, and dynamic
  tools are outside the parity test's scope.

### Arguments

- `ToolCallResult.Arguments` flattens values through `JToken.ToString()`, so `true` becomes
  `"True"`, `2` becomes `"2"` and `null` becomes `""`. MCP servers validate against their own
  schemas, so that flattened form is not usable for them.
- The parser therefore also keeps `ToolCallResult.RawArguments`, a typed `JObject`. It is taken
  after `ToolArgumentRepair`, which fixes JSON syntax only and knows nothing about schemas, so it
  is safe for any tool.
- `ToolCallMapper` maps any name that parses as `mcp__…` to `BlockType.McpCall` and forwards
  `RawArguments`. A malformed `mcp__` name falls through to the existing unknown-tool text.

### Results

- **Keying.** Every other tool files its result in `ExecutionResult.ToolResultContents` under
  an argument value (filename, query, url) or a literal (`"run_sql"`). `LoopHelpers` rebuilds
  the same key from the tool call's arguments. MCP results are keyed by tool-call id instead
  (`McpToolName.ResultKey(id)` = `mcp:<id>`), so two identical calls in one turn, such as
  `run_workflow` twice, each keep their own result. The block carries `ToolCallId` so the
  executor can use that key.
- **Call-id guard (part 3).** A call whose id is missing, or repeats an earlier call's in the
  same turn, gets a unique `ToolCallResult.FallbackId` from `ToolCallMapper.Map`. The first
  holder of an id keeps it. The block and the tool-message lookup both use `ResultId`, which
  is the fallback when set and the id otherwise. The tool message still carries the model's
  own id as its `tool_call_id`, because that has to match the assistant message already in
  history.
- **Size.** Oversize handling is left to the existing ingest cap in
  `LlmClient.AddToolResultMessage`. Above 8,000 chars, a result becomes a head/tail excerpt
  plus a `recall_cache` handle, and the full text is spilled to disk.
- web_fetch's own 8,000-char hard cut was not copied, because it would throw away text the
  ingest cap keeps recoverable. The executor adds only a 200,000-char safety ceiling, with a
  `[MCP: result truncated …]` note.
- **Errors.** Error text (`[MCP ERROR]`, or `[MCP TOOL ERROR]` for a tool's own error result)
  goes to `result.Errors` and is also what the model receives. The loop never gets an exception.
- **Transcript.** The transcript shows `[MCP] <server>.<tool> running…` before the call. After
  it, it shows `done (N chars)` or the first line of the error. In the TUI the tag renders as
  "Mcp" (`TranscriptVocabulary`, alongside Lsp and Sql).

### Approval

MCP calls use the **same approval class as `run_shell`**: they go through
`ApproveMutationAsync(MutationKind.Shell, "Run MCP tool <server>.<tool> <args>")`.

| Mode | Behaviour |
|------|-----------|
| Auto | Runs without asking. |
| Manual | Asks first; a decline reaches the model as a refusal. |
| Plan | Refused without asking. |
| Headless | Follows the job's mode, exactly as `run_shell` does. |

- An external tool can do anything: render (`run_workflow`), install (`install_node`),
  download (`download_model`). DevMind cannot tell which calls are harmless.
- MCP's `readOnlyHint` annotation is self-declared by the server, so it is not trusted here.
- A separate `MutationKind` would have the same three decisions. It can be split out later if
  MCP needs a different policy from the shell.

### Bookkeeping

- `ResponseOutcome.HasMcpCalls` counts an MCP call as a directive and a mutation.
- The training log records `{"type":"mcp","server":…,"tool":…}` for the call and
  `type:"mcp"` for its result.

## Hosts (part 3)

All hosts depend on `IMcpClientManager`, which bundles the invoker, `GetExposedTools`,
`GetStatuses`, `StartAsync`, `RestartAsync` and `CallCount`. `McpClientManager` implements it,
and tests substitute fakes.

### System prompt

When at least one MCP tool is exposed, both prompt builders add one line:
"External tools prefixed mcp__<server>__ come from MCP servers; their results may be long —
prefer narrow queries." The builders are the TUI's `BuildCombinedSystemPrompt` and headless
`HeadlessSession.BuildSystemPrompt`, and both add the line via `McpPrompt.Note`. The prompt is
rebuilt every turn, so the line appears once a server is ready and disappears when none is
running. With no MCP tools exposed, the prompt contains nothing about MCP.

### TUI

- At session start the TUI creates **one** `McpClientManager` from config for the whole
  process. It sets it on `LlmClient.McpClients` and `LoopDriver.McpTools`.
- Config warnings about skipped entries are printed once, under the banner.
- **Servers with `autoStart` start in the background** when the window initializes
  (`window.Initialized`), each on the thread pool. Startup never waits for them, so a slow or
  dead server cannot delay the TUI.
- Each server reports exactly once, marshalled to the UI thread:
  - `[MCP] comfy ready (39 tools)`
  - `[MCP] comfy failed to start: <reason>`
  
  `<reason>` is the cause only. The stderr tail is in `DevMindLog`.
- Tools reach the model on the first request after a server is ready.
- `/mcp` lists every configured server:

  ```
  MCP servers:
    comfy    ready     39 tools   all tools
    blender  failed    -          allowlist  — file not found
    manual   stopped   -          all tools  (manual start)
  ```

  - States are stopped, starting, ready and failed.
  - The allowlist column shows "all tools" or "allowlist".
  - A server with `autoStart: false` is marked "(manual start)".
- `/mcp restart <name>` stops the server, clears its automatic-restart budget, starts it
  again, and reports ready or failed. An unknown name lists the configured ones.
- The manager is disposed on exit, bounded to 15 s. The job objects already guarantee the
  servers die with the process; disposing is for clean logs.

### Headless (`devmind_task_start` → `mcp_servers`)

- **Parameter.** `mcp_servers` is an optional array of server names from `devmind.json`.
  - The names are resolved when `devmind_task_start` is called, not when the job runs. An
    unknown name rejects the start with the configured names, for example:
    `mcp_servers: unknown server 'comfyui' — configured: comfy, blender.`
  - Absent or empty means no manager is created and nothing changes.
  - `autoStart` is ignored.
- **Start.** Each job gets its own manager (`AgentJobManager.McpManagerFactory` is the test
  seam). Before the first LLM request it starts the listed servers **in parallel** and waits
  for all of them, with a **60 s limit per server** (`McpStartTimeout`). A server that fails or
  times out is recorded, and the job runs on without it. The transcript tail shows
  `[job] mcp: comfy started (N tools)` or `… failed to start: <reason> — continuing without it`.
- **Result.** `devmind_task_result` and the on-disk result sidecar gain an `mcp` section, which
  is omitted when no servers were requested:

  ```json
  "mcp": { "requested": ["comfy"], "started": ["comfy"], "failed": [], "calls": 4 }
  ```

  `failed` holds `{ "server", "reason" }` entries. `calls` counts every MCP tool call the agent
  made, including failed ones.
- **Terminal cleanup.** The job's manager is disposed in the worker's `finally` in
  `AgentJobManager.WorkerLoopAsync`. That block already runs for every terminal state: done,
  failed, cancelled and stopped_incomplete (Done with `IsIncomplete`). The session is detached
  first (`HeadlessSession.SetMcpClients(null)`), so the retained conversation never points at
  a disposed manager.
- A job cancelled while still queued never reaches the worker, so it never had a manager.
- **Continuations.** A continuation copies the parent's `McpServers`, the same way it inherits
  `no_execute`. The parent's manager was disposed when the parent ended, so the continuation's
  worker creates a fresh manager, starts the servers again and attaches it to the transferred
  session.

## Tests

- `DevMind.Core.Tests/McpClientTests.cs`: config parsing, name round-trip, result formatting,
  the failure contract (missing executable, a crash at startup with its stderr, the restart
  budget), and the TuiConfig round-trip.
- Live smoke test (`McpClientSmokeTests`), skipped unless `DEVMIND_MCP_SMOKE=1`. It uses the
  real `comfy` entry with the allowlist ignored. It checks that `which` is listed and that
  calling it returns `workspace_path`, then checks that the server PID is gone after dispose.
  Since part 2 it also checks that `GetExposedTools()` contains `mcp__comfy__which` with a
  parameters object.
- `DevMind.Core.Tests/McpAgentLoopTests.cs` (part 2):
  - Exposure: the no-argument array is unchanged, extras are cloned, the 64-character and
    character-set skip works, only started servers are exposed, and the allowlist holds.
  - End to end over the fake SSE server: the tools are on the wire and the arguments come back
    typed.
  - Mapping: `mcp__` names map correctly, malformed ones fall through, other names are
    unchanged.
  - Executor, through a fake `IMcpToolInvoker`: call-id keying, two identical calls not
    colliding, the error and throw paths, a missing invoker, the size ceiling, and
    Manual/Plan approval.
- `DevMind.Core.Tests/McpHostTests.cs` (part 3):
  - `autoStart` default, explicit and bad values.
  - The call-id fallback for null, empty and repeated ids.
  - `McpPrompt`, checked on the wire through a real `HeadlessSession`: the line and tools with
    a server, none without.
  - Status, restart and `CallCount` on the real manager, and `ShortReason`.
- `DevMind.McpServer.Tests/McpJobTests.cs` (part 3):
  - `mcp_servers` resolution, including the unknown-name error text.
  - No manager when absent.
  - A failing and a hanging server recorded while the job completes.
  - A continuation inheriting the list with a fresh manager.
  - Disposal on done, failed, cancelled and stopped_incomplete.
  - The payload shape.
  - A live headless smoke test (`McpJobSmokeTests`, `DEVMIND_MCP_SMOKE=1`) that starts a
    job-scoped comfy manager, checks the tools are exposed, and checks the process is gone
    after dispose.
- `DevMind.TUI.Tests/McpCommandTests.cs` (part 3):
  - `/mcp` formatting, restart, unknown name and usage.
  - `/mcp` listed in `/help`.
  - Autostart returning before a start that never completes.
  - Each server reporting once.
