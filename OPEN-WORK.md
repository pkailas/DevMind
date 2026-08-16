# Open work — DevMind

Written 2026-08-15 after a day of harness fixes (commits d559491..81e626a).
Tests at time of writing: Core 410, McpServer 20. Deployed: 1.0.370 (one commit behind master).

---

## 1. Shell timeout does not reap runaway processes  — DONE (f2e59b4)

**CLOSED 2026-08-16.** The Job Object landed. `WindowsJobObject.cs` creates a
per-command job with `KILL_ON_JOB_CLOSE`, assigns the child right after
`Start()`, and closes the handle in the finally — an additional authoritative
kill on top of the taskkill path, which is untouched. Strictly degradable: any
failure (non-Windows, API error, no-breakaway parent job) traces
`mcp.shell.job.degraded` and runs exactly as before. Core 421, McpServer 20.

Containment is VERIFIED, not assumed — production self-check queries the
SPECIFIC job handle immediately after assignment and traces
`assigned_immediately`, plus a ground-truth test asserting membership via two
handles.

Accepted and documented trade: a grandchild spawned in the sub-millisecond
window between `Start()` and assignment is uncontained. Closing it needs
`CREATE_SUSPENDED` via manual `CreateProcess` — more native surface than the
containment itself.

**Worth remembering — four of the five bugs were in the MEASURING INSTRUMENT,
not the feature.** `IsProcessInJob` was declared with two parameters instead of
three, so it read the success-bool as the membership answer. Both a human and
the agent then produced confident wrong diagnoses from its output ("the
environment restricts job objects", "the test is querying a dead process"). The
lesson is the one already in §4, one level down: when a measurement disagrees
with expectations, suspect the measurement before the world. Also: the answer
was one `learn_search` away the whole time, and neither of us looked.

### Historical record (superseded, kept for context)

## 1b. Original diagnosis — PARTIALLY FIXED at b2ba5af

**UPDATE 2026-08-16 (commit b2ba5af, deployed 1.0.373).** Three of the four
recommendations are done:
- `MSBUILDDISABLENODEREUSE=1` is now set on every spawned shell. This should
  prevent the SPECIFIC runaway we hit, since the node-reuse pool was the likely
  survivor. Verified live: `cmd /c set` shows it in the child environment.
- taskkill's exit code and exceptions are classified instead of swallowed
  (`ClassifyReapResult`). Exit 0 and 128 = success; anything else reports
  `[SHELL] Failed to reap process tree (PID n): <reason>`.
- a process still alive after the 5s wait + 1s grace is now stated explicitly.
  `mcp.shell.exit` carries `reap_result`.

**STILL OPEN: the Job Object.** A genuinely detached child can still escape
`taskkill /F /T` — the two observability changes make that visible, they do not
prevent it. That fix needs `CreateJobObject` / `AssignProcessToJobObject` /
`SetInformationJobObject` with `KILL_ON_JOB_CLOSE`, which would be the FIRST
P/Invoke in this codebase (job-503 called it "small on net10.0" — that was wrong,
.NET has no managed Job Object API). Deferred deliberately: ShellRunner backs
`run_shell`, so the first native interop here wants its own careful change with a
redeploy check before relying on it.

Also still unexplored: `Process.Kill(entireProcessTree: true)` (managed, .NET 5+)
would at least replace the taskkill shell-out, though it has the same
re-parenting limitation.

**Severity: high.** This nearly took BEAST down twice — a `dotnet` test host
reached 64 GB and then 45 GB, leaving 1.5 GB free on a 128 GB machine.

**Diagnosed in job-503. My first hypothesis was WRONG and is recorded here so it
isn't repeated:** I assumed the timeout path never killed the process tree. It
does — `ShellRunner.cs:301-314` runs `taskkill /F /T /PID {proc.Id}` for both
`timedOut` and `cancelled`.

**Actual cause:** `ExecuteAsync` wraps every command in `powershell.exe`
(`ShellRunner.cs:91`), so `proc.Id` is the *shell's* PID. The runaway `dotnet`
had left that tree by the time taskkill walked it — almost certainly the MSBuild
build-server / node-reuse pool, which deliberately outlives the invoking command.
Supporting evidence: several `dotnet` processes shared a start time of 11:55:27,
consistent with a worker pool.

**Also found:** `ShellRunner.cs:303-313` swallows every taskkill exception with a
bare `catch { }` and never reads taskkill's exit code. A failed reap is currently
invisible to DevMind.

**Recommended fixes, in priority order (from job-503):**
1. Job Object with `KILL_ON_JOB_CLOSE` on every spawned process. .NET 9+ makes
   this small on net10.0 and removes the fragile taskkill entirely. Belongs in
   `ShellRunner.RunProcessAsync` — the gap is general, not dotnet-specific: any
   process that detaches or re-parents escapes the same way.
2. `MSBUILDDISABLENODEREUSE=1` / `-nodeReuse:false` for DevMind-spawned dotnet
   build/test. Low risk, additive to (1).
3. Verify the process actually died after taskkill; log/retry survivors.
4. Optional: memory watchdog on spawned children.

**Rejected:** "do nothing, node reuse is intended and the hung test was the
fault." Half right — but the reaping contract is silently broken, and a runaway
is still a DevMind child whose timeout-kill promised to reap it.

**Could not determine:** whether the survivor was the build-server pool or a
re-parented testhost. Same class either way. To settle on next occurrence:
capture the runaway's `ParentProcessId` at kill time, check whether
`dotnet build-server shutdown` reaps it, and capture taskkill's own exit code.

**Reproducer:** disable the `PatchEngine` malformed-input guards (8b04cb7) and
run the mutation test. NOTE the malformed input does not reliably throw when
unguarded — twice it consumed memory without bound instead. That non-termination
was never separately diagnosed.

---

## 1c. Global memory layer — DONE and LIVE (af6abdf, deployed 1.0.380)

Standing conventions used to be per-repo only, because MemoryManager is rooted
at the working directory. Tooling conventions ("check learn_search before an
unfamiliar P/Invoke", "use append_file to append") are not repo facts and should
bind everywhere.

`%APPDATA%\devmind\memory\` is now a second layer, composed global-first then
repo, each with its own budget (global 8 KB, repo keeps its 24 KB) so a long
global file can never starve a repo topic.

`standing-conventions.md` lives there now, NOT in this repo. Five rules, each
derived from a real field failure. Verified live in DevMindTestBed, which has no
`.devmind` directory at all: the task received all five rules labelled
`## GLOBAL (machine-level) conventions` and correctly reported no repo-level
topics.

Repo-level `.devmind/memory` remains the right home for genuine repo facts. The
four topics in this repo are per-repo incident notes and correctly stay here.

Global is read-only at the tool surface: `save_memory` still writes to the repo.
Letting a model author machine-wide standing rules for itself is a deliberate
capability to add later, not a side effect.

---

## 2. Cosmetic leftovers from the FilePathResolver review (job-487)

- **Double-resolve on the not-found path** — the failure path resolves twice.
- **`Candidates.Count == 1` message branch** — says "The directory portion of the
  hint did not match it" even when the hint has no directory portion.
- **Null guard** missing in the host-side `BuildFileNotFoundMessage` wrappers.

---

## 3. Known-and-deliberate — do NOT "fix" these

Each was investigated today and found correct as-is. Recorded so they don't get
re-raised as findings.

- **MCP write tools have no read-first guard** (job-504). The guard is a
  prompt-nudge by design, not an interlock. There is no human to prompt in the
  MCP path, `run_shell` bypasses any read-set gate anyway, and `WriteRoots` is
  the right control there. Recommendation was: do nothing.
- **TUI has no write sandbox** (job-505). Deliberate and documented at
  `BufferedAgenticHost.cs:140-145`. The TUI's whole posture is no-prompt —
  `ShowDiffPreviewAsync` auto-approves patches too.
- **Delete/rename bypass the read-first guard** (job-505). Sound: the guard's
  threat model is clobbering unseen content, which delete and rename don't do.
- **`LspToolService.cs:116` catch-all** (job-506). Passes `ex.Message` through
  verbatim, so every cause already yields a distinguishable message. Adding catch
  blocks would manufacture a distinction.
- **`TaskReadSet`'s `|| Count == 0` clause.** Commit 41afb6e's message shows the
  original guard also allowed writes to files "mentioned in the user's prompt"
  via a `_pendingResubmitPrompt` check that no longer exists. Whether Count==0 is
  the deliberate remainder or an artifact of the ba4cdbe rewrite is UNRESOLVED.
  Left as-is with a comment. Paul is the only one who can settle it.
- **`TuiAgenticHost` does not inherit `BufferedAgenticHost`** — it implements
  `IAgenticHost` directly. Anything describing its methods as "shadowing" the
  base is working from a false premise (mine, originally).

---

## 4. The pattern worth remembering

Nearly every fix today was the *harness* feeding the model something false or
withholding something it needed — not the model failing. Run C concluded a source
project didn't exist because the not-found message listed the working directory's
files as "Project files:". An agent stopped investigating because the prompt said
absolute paths were blocked when only writes are. Five FIND retries chased advice
that normalization made impossible.

The model's behaviour was correct given its inputs every time.

The principle that came out of the audit, and which should govern any new
diagnostic message:

> A prescription is something an LLM will execute; a bare failure is something it
> will investigate.

So: report the cause you actually determined. Prescribe an action only where that
action is correct for that cause. Where the cause is unknown, say so — a
confident half-truth is worse than a vague message, because the agent acts on it.
