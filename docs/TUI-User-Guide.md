# DevMind TUI — User Guide

DevMind is a terminal-based, agentic coding assistant that runs against a
**local** model server. You give it a task in plain English; it reads files,
searches, patches code, runs shell/build/test commands, and iterates until the
task is done — all inside a working directory you choose, with nothing leaving
your machine.

---

## Starting DevMind

```powershell
devmind --dir C:\path\to\your\repo
dm --dir C:\path\to\your\repo          # short alias
```

Common startup options:

| Option | Meaning |
|---|---|
| `--dir <path>` | Working directory the agent operates in |
| `--endpoint <url>` | Model server URL (default `http://127.0.0.1:1234/v1`) |
| `--api-key <key>` | API key, if the endpoint needs one |
| `--model <name>` | Model name to request |
| `--system-prompt <text>` | Override the base system prompt |
| `--build-command <cmd>` | Explicit build command for the TEST/build tools (otherwise auto-detected) |
| `--max-depth <n>` | Agentic iteration cap for a single task |
| `--context-limit <pct>` | Context-window % at which the loop pauses to ask before continuing |
| `--context-size <n>` | Manually declare the model's context size |
| `--timeout <min>` | Request timeout in minutes |
| `--thinking` / `--no-thinking` | Show or hide the model's reasoning stream |

Your working directory, behavioral rules, depth cap, and context limit persist
between sessions in `%APPDATA%\devmind\devmind.json`.

---

## Everyday use

Type what you want done and press Enter:

```
Add null-checking to the constructor of OrderService and rebuild.
```

DevMind streams the model's response and **executes what the model decides to
do** — reading files, applying patches, running the build — showing each action
as it happens. The loop continues until the model signals it's done or hits the
depth cap.

Things to know:

- **The agent acts inside the working directory.** Change it with `/dir`.
- **Patches are applied with an undo trail** — the patch engine validates edits
  before applying and keeps whitespace/line-ending fidelity.
- **If the context window fills up**, DevMind pauses at your configured
  percentage (default 78%) and asks before continuing.

---

## Slash commands

Session control:

| Command | Description |
|---|---|
| `/new` | Start a new session (clears conversation, resets state) |
| `/restart` | Alias for `/new` |
| `/clear` | Clear screen and reset conversation |
| `/cls` | Clear the screen **only** — keeps conversation, context, and session |
| `/help` | Show the command list |

Thinking (reasoning) display:

| Command | Description |
|---|---|
| `/think on\|off` | Toggle thinking display for the session |
| `/reasoning on\|off` | Alias for `/think` |
| `/t <message>` | One-shot: send this message with thinking ON, without changing the default |

Configuration (persisted):

| Command | Description |
|---|---|
| `/dir [path\|-b]` | Show or change working directory (`-b` opens a folder picker) |
| `/rules [text\|clear]` | Show, set, or clear behavioral rules injected into the system prompt |
| `/depth-cap [N]` | Show or set the agentic depth cap (1–200) |
| `/context-limit [1-99\|off]` | Show or set the context-window pause threshold |
| `/training-log [on\|off\|folder <path>]` | Show or toggle training-turn capture, or retarget its folder |
| `/system_prompt` | Display the assembled system prompt |
| `/cache` | Show nearline cache stats |

History:

| Command | Description |
|---|---|
| `/history` | List past sessions |
| `/resume <n>` | Resume a past session by number from the `/history` listing |
| `/title <text>` | Set the current session's title |

Documents and images (requires a vision model + mmproj):

| Command | Description |
|---|---|
| `/image <path> [page\|first-last\|all\|p=N]` | Attach an image — or rasterized PDF pages — to your next message; `p=N` chunks a document N pages at a time |
| `/digest <path-to-pdf> [p=N]` | Chunk-summarize an entire PDF on a side conversation, then inject the digest into this session |
| `/library [add\|replace\|list\|remove\|<question>]` | RAG over ingested documents (SQL Server 2025 vector store): add PDFs or .md/.txt/.docx files, or ask a question against the whole library |

Patching:

| Command | Description |
|---|---|
| `/resolve accept_proposed\|accept_current\|cancel` | Resolve a pending three-way-merge conflict from a patch |

Debugging (requires netcoredbg — see the Installation Guide):

```
/debug launch <project-path>      build and run under the debugger
/debug attach <pid|process-name>  attach to a running process
/debug break <file> <line>        set a breakpoint (break clear ... to remove)
/debug continue | step | stepin | stepout
/debug inspect <var> | stack | eval <expr>
/debug detach | stop
```

Not yet implemented in the TUI (registered, but currently no-ops):
`/compact`, `/lsp`, `/output-lines`, `/training-delete-last`.

---

## How the model does things (directives)

You don't type these — the **model** emits them, and DevMind executes them.
They're listed here so you can read the transcript:

| Directive | Action |
|---|---|
| `FILE: name` … `END_FILE` | Create a new file |
| `PATCH name` + `FIND:`/`REPLACE:` … `END_PATCH` | Edit an existing file |
| `SHELL: command` | Run a shell command |
| `READ file[:start-end]` | Load file contents into context |
| `GREP: "pattern" file` | Search within one file |
| `FIND: "pattern" *.cs` | Search across files by glob |
| `DELETE file` / `RENAME old new` | Remove or move a file |
| `DIFF file` | Show what changed this session |
| `TEST Project.csproj` | Run tests |
| `DONE` | Explicit task completion — stops the loop |

---

## Useful environment variables

| Variable | Purpose |
|---|---|
| `DEVMIND_ENDPOINT`, `DEVMIND_API_KEY` | Model server connection |
| `DEVMIND_SERVER_TYPE` | Force backend type (`vllm`/`llama`/`lmstudio`/`custom`) |
| `DEVMIND_BUILD_COMMAND` | Explicit build command for the build tool |
| `DEVMIND_CONTEXT_STRATEGY` | Context-management policy (`transformer`/`hybrid`/`auto`) |
| `DEVMIND_HISTORY_*` | Session-history store configuration (SqlServer/Sqlite/Null) |
| `DEVMIND_SEARCH_URL` / `DEVMIND_FETCH_URL` | Self-hosted SearXNG / fetcher endpoints for the web tools |
| `DEVMIND_LEARN_URL` | Microsoft Learn MCP endpoint for the learn_* doc tools (default: Microsoft-hosted, no setup) |
| `DEVMIND_TUI_VERBOSE` | Show the full tool/loop firehose in the transcript |

Config resolution order: environment variables →
`%APPDATA%\devmind\devmind.json` → `~/.devmind.env` → defaults.

---

## Tips

- Write task briefs the way you'd brief a junior developer: the goal, the
  relevant files, and how to verify success.
- Keep `/depth-cap` modest for small tasks; raise it for cross-cutting work.
- Use `/rules` for standing instructions ("never touch the _archive folder",
  "always run tests after a change") — they persist across sessions.
- If a fuzzy patch match or merge conflict comes up, DevMind pauses and asks —
  nothing is applied behind your back.

---

*DevMind is a product of iOnline Consulting LLC.*
