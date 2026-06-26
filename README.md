# DevMind

Local, LLM-powered agentic coding assistant (.NET 10). See `CLAUDE.md` and
`DEVMIND_DEV.md` for architecture and developer documentation.

## Prerequisites

- **.NET 10 SDK**
- **C# language server (Roslyn LS)** — powers the LSP tools; resolved from your
  dotnet global tools. See `CLAUDE.md` for the `DEVMIND_LSP_SERVER*` settings.

### Debugging (`/debug`) — netcoredbg

The `/debug` commands speak the Debug Adapter Protocol (DAP) to
[**netcoredbg**](https://github.com/Samsung/netcoredbg), run as a separate
adapter process over stdio. (The Roslyn language server is LSP-only and exposes
no DAP endpoint, so a dedicated debug adapter is required.)

1. Download the latest `netcoredbg-win64.zip` from the
   [netcoredbg releases](https://github.com/Samsung/netcoredbg/releases).
2. Extract it so the executable lives at
   `%LOCALAPPDATA%\netcoredbg\netcoredbg\netcoredbg.exe` — the default location
   DevMind probes. **Or** extract anywhere and point `DEVMIND_DAP_PATH` at the
   full path to `netcoredbg.exe`.

DevMind resolves the adapter in this order: `DEVMIND_DAP_PATH` →
`%LOCALAPPDATA%\netcoredbg\...` → `PATH`. Without netcoredbg installed, `/debug`
cannot start a session and reports the adapter could not be launched.

Once installed, debugging supports both launch and attach:

```
/debug launch <project-path>      build and run under the debugger
/debug attach <pid|process-name>  attach to a running process
/debug break <file> <line>        set a breakpoint (break clear ... to remove)
/debug continue | step | stepin | stepout
/debug inspect <var> | stack | eval <expr>
/debug detach | stop
```
