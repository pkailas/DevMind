# MCP client (external MCP servers)

DevMind can act as an MCP **client**: launch external MCP servers over stdio, list their
tools and call them. Motivating use: the ComfyUI server (`comfy-mcp`) so DM agents can drive
ComfyUI.

Status (2026-09-29): **part 1 of 4 — config + client manager only.** `McpClientManager`
exists in DevMind.Core but nothing in the agent loop uses it yet (part 2 wires the tools into
the catalogue as `mcp__<server>__<tool>`).

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
    "callTimeoutSeconds": 900
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

## Tests

- `DevMind.Core.Tests/McpClientTests.cs`: config parsing, name round-trip, result formatting,
  the failure contract (missing executable, a crash at startup with its stderr, the restart
  budget), and the TuiConfig round-trip.
- Live smoke test (`McpClientSmokeTests`), skipped unless `DEVMIND_MCP_SMOKE=1`. It uses the
  real `comfy` entry with the allowlist ignored. It checks that `which` is listed and that
  calling it returns `workspace_path`, then checks that the server PID is gone after dispose.
