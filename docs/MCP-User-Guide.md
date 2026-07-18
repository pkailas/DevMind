# DevMind MCP Server — User Guide

The DevMind MCP server gives an MCP client (Claude Code, the Claude desktop
app, DevMX, …) two distinct capabilities:

1. **Granular tools** — direct file/search/shell/build/memory/LSP operations on
   your machine, one call at a time.
2. **Task delegation** — hand a *whole coding task* to DevMind's headless
   agent, which executes it autonomously on your **local** GPU model at zero
   API token cost, and returns a structured, auditable result.

The intended division of labor: use granular tools for quick lookups; delegate
mechanical, well-scoped coding work as tasks; keep design-judgment work in the
client.

---

## Task delegation (the headless agent)

### Starting a task

`devmind_task_start(prompt, working_dir, …)` enqueues a job and returns a
`job_id` immediately. Parameters:

| Parameter | Default | Meaning |
|---|---|---|
| `prompt` | *(required)* | The task brief: goal, relevant files, constraints, and how to verify success. Write it like a brief for a junior developer. |
| `working_dir` | *(required)* | **Absolute** path of the directory the agent operates in — its sandbox. |
| `max_depth` | 40 (1–100) | Max agentic iterations. Rough sizing: verify ~25, single-file feature ~40, cross-cutting ~60. |
| `timeout_minutes` | 30 (1–240) | Hard wall-clock kill. |
| `allow_commit` | false | Whether the agent may run `git commit`. Leave off — the caller owns version control. |
| `verify_build` | true | After the agent finishes, the job runner builds the working_dir itself and attaches `build_verification` to the result. |
| `verify_tests` | false | After a successful build verification, also run `dotnet test` and attach `test_verification`. |
| `think` | false | Enable model reasoning for this task. Leave off for briefed mechanical tasks — thinking runs unbounded on the local server and can add minutes per iteration. |

`devmind_task_start` health-probes the model server first and fails fast with a
clear message if it's down. Jobs run **one at a time** (single GPU — a queue
beats KV-cache thrash); additional jobs queue with a reported position.

### Following a task

- `devmind_task_status(job_id)` — `queued (position N)` / `running` / `done` /
  `failed` / `cancelled`, plus a tail of the live transcript. A job that hit
  its depth cap or failed build verification reports **`stopped_incomplete`**
  — meaning the work is not trustworthy as-is.
- `devmind_task_list()` — all jobs the server currently knows about.
- `devmind_task_cancel(job_id)` — cancel a queued or running job.

### Getting the result

`devmind_task_result(job_id)` returns the structured result: the agent's final
answer, the **action journal** (every file created/patched/deleted/renamed,
every shell command and exit code, build/test invocations and outcomes),
iteration/elapsed counts, and the `build_verification` /`test_verification`
blocks when enabled.

**Review the action journal and diffs like a junior developer's PR** before
building on the changes. Trust the attached `build_verification` rather than
re-running builds yourself.

### Continuing a task

`devmind_task_continue(job_id, prompt?)` resumes a *finished* task's
conversation — the agent keeps its full context, so a bare "continue" picks up
exactly where it stopped. Use it when a task hit its iteration cap, or to send
follow-ups ("now also fix the failing test"). It returns a **new** `job_id`;
a conversation is a chain, so always continue its **newest** job_id.
Conversations expire after ~60 minutes idle — after that, start a fresh task
with a continuation brief.

### Ground rules while a task runs

- Don't edit files under the `working_dir` while a job is running.
- Treat DevMind-changed files as externally modified: re-read them after the
  job reports `done`.

---

## Granular tools

These operate directly and return immediately. Highlights by category:

**Files** — `read_file` (large files return an outline first; also accepts
`git log` / `git diff` as the filename for git operations), `list_files`,
`create_file`, `write_file`, `append_file`, `patch_file` (batched find/replace
edits), `delete_file`, `rename_file`, `diff_file`, `open_file`.

**Search** — `grep_file` (one file), `find_in_files` (across files by glob;
case-insensitive substring with `|` alternatives — not full regex).

**Code navigation (LSP)** — `find_symbol`, `go_to_definition`,
`find_references`, `hover`, `get_diagnostics`.

**Shell & build** — `run_shell` (PowerShell; use `background=true` for anything
over ~45 s and poll `shell_job_status`), `elevated_shell`, `run_build`,
`run_tests`, `git_commit`.

**Memory** — `save_memory`, `recall_memory`, `search_memory`,
`list_memory_topics` — DevMind's persistent knowledge store for the codebase.

**Documents & data** — `library_add` / `library_list` / `library_query`
(vector-store RAG over ingested documents), `query_db`, `attach_image`.

**Microsoft Learn (built in — nothing to deploy)** — `learn_search`
(authoritative learn.microsoft.com docs search), `learn_fetch` (a specific
Learn page as clean markdown), `learn_code_search` (official code samples,
returned with language, source link, and fenced snippet). These call
Microsoft's hosted Learn MCP server directly (`https://learn.microsoft.com/api/mcp`,
override with `DEVMIND_LEARN_URL`) — no local service, no API key. The tool
descriptions steer the model to prefer them over `web_search` for .NET, C#,
Azure, and SQL Server API questions. The same three tools are in the agentic
tool catalog, so the TUI and delegated headless tasks use them too.

**Network & misc** — `web_search`, `web_fetch`, `http_request`, `ssh_exec`,
`clip_read` / `clip_write` (clipboard).

Notes that save you grief:

- File paths resolve against the session working directory, and writes are
  containment-checked — the tools won't reach outside it.
- Shell output is capped (~1000 lines / 50 KB); ask for less, or write output
  to a file and read it in slices.
- `run_shell` runs PowerShell: chain commands with `;`, not `&&`.

---

## Working headless from the Claude desktop app

Once registered with the desktop app (see the Installation Guide), the devmind
tools are available in your Claude sessions — including Cowork sessions running
in the cloud, which reach your machine through the app. A practical pattern:

1. You describe the change you want, from wherever you are.
2. Claude scopes it, then calls `devmind_task_start` with a junior-dev brief
   and an absolute `working_dir` under your repos.
3. Claude polls `devmind_task_status`, reviews the action journal in
   `devmind_task_result`, and reports back with the build verification.

Your GPU does the mechanical work; the API tokens go only to planning and
review. The desktop app must be running and online for the bridge to work.

---

*DevMind is a product of iOnline Consulting LLC.*
