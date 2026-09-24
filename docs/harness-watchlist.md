# DevMind Harness Watchlist

Harness issues observed while driving delegated jobs (`devmind_task_*`) and the TUI.
One entry per issue: date first seen, job id(s), symptom, evidence, proposed fix, status.
Model-behaviour notes are kept separate from harness defects (see the last section).

Status values: **open**, **parked** (acknowledged, not scheduled), **fixed** (commit), **wontfix**.

---

## Open

### H-01 - "Task complete" reported with unfinished brief steps
- **First seen:** 2026-09-23 - job-1652, job-1654 (VLink.Warehouses, docs repo). **Again 2026-09-24:** job-1667 (grep for leftover debug
  code reported a match, job still `done`), job-1668 (final message: "no test run was performed ... red run NOT demonstrated ... resume by
  fixing the 7 CS1503" - test project did not compile - yet `state: done`, `incomplete_reasons: null`). Jobs were run with
  `verify_build: false` (the TestHarness exe is locked by a user-run fakesap), so the harness had no build signal of its own either.
- **Symptom:** the agent's final message lists brief steps under "NOT DONE / caller must finish" (or says a fix is "not verified"), yet the job ends `state: done`, `incomplete_reasons: null`.
- **Why it matters:** the driver has to read every final message to catch it; `done` is supposed to mean trustworthy-as-is.
- **Proposed fix:** scan the final answer for explicit incompleteness markers ("NOT DONE", "not verified", "caller must", "did not run", "hit the iteration cap") and set `stopped_incomplete` with reason `self_reported_incomplete`, keeping the text.
- **Status:** open

### H-02 - `devmind_task_continue` times out without starting a job
- **First seen:** 2026-09-23 - job-1643 (two attempts, ~4 min each)
- **Symptom:** the MCP call never returns; afterwards `devmind_task_list` shows the parent with `continued_by: null` (nothing was queued). `devmind_task_list` itself responds normally during the stall, so the server is up.
- **Suspect:** the continuation path blocks before enqueueing - possibly the `verify_tests` + `test_baseline: before-run` default running the full suite synchronously inside the call while the parent's tree did not compile.
- **Proposed fix:** enqueue first and return the new job id immediately; run any baseline inside the job. Add `mcp.tool.begin/end` trace events around continue.
- **Workaround:** start a fresh task with a continuation brief.
- **Status:** open

### H-03 - Server restart mid-job makes the job non-continuable
- **First seen:** 2026-09-23 - job-1653
- **Symptom:** result came back "recovered from result sidecar ... NOT continuable across restarts". The restart happened while the job was running.
- **Proposed fix:** persist enough conversation state to resume, or at minimum mark the result `stopped_incomplete: server_restart` with the last iteration number so the driver knows why.
- **Status:** open

### H-04 - Patch anchor lands inside the wrong member / duplicates a method
- **First seen:** 2026-09-23 - job-1652 (AdminUiPagesTests.cs)
- **Symptom:** an insert intended between two test methods landed inside `DetailPage_ContainsMetadataAndImage`, leaving it unterminated and later producing a duplicate copy; ~6 iterations to untangle.
- **Related:** Sep 1 ambiguous-match thrash on near-duplicate bodies.
- **Proposed fix:** after applying a patch to a .cs file, run a syntax check (Roslyn parse) and reject/undo the patch if it introduced parse errors, reporting the location.
- **Status:** open

### H-05 - `[two-way fallback]` label on every clean patch
- **First seen:** 2026-09-01 (item b), still present 2026-09-23 on every patch
- **Symptom:** `ThreeWayMergeCheck` sets `UsedFallback` when base == current (the normal case); the transcript prints `[two-way fallback]` for clean patches, which trains the reader to ignore it.
- **Proposed fix:** only label when a genuine divergence forced the fallback.
- **Status:** open (semantics decision pending since Sep 1)

### H-06 - Lessons don't carry between jobs or repos
- **First seen:** 2026-09-23 - job-1651 learned the Razor `\"`-in-`@()` RZ1000 trap; job-1652 re-discovered it from scratch the next job.
- **Proposed fix:** (a) a global "known traps" section the harness loads (the new `prompts/system-prompt.md` evidence rule is a start); (b) make repo memory topics visible to later jobs in the same repo by default; (c) reference docs in the RAG library (Razor docs added 2026-09-23, `doc_filter "Razor"`).
- **Status:** partially addressed (RAG + system-prompt rule); harness-side carry-over open

### H-07 - Shell output hard to consume
- **First seen:** 2026-09-23 - jobs 1652, 1653, 1655
- **Symptoms:**
  - agents redirect output to `%TEMP%` files and `read_file` them back (4 round trips for one byte check);
  - PowerShell `2>file` writes UTF-16, which cost several iterations of decode confusion;
  - `cmd /c "... > log 2>&1"` inside the wrapper wrote only "The system cannot find the path specified." with exit 0;
  - CLIXML error blocks still appear for stderr from some commands.
- **Proposed fix:** return stdout+stderr inline (capped) as UTF-8 text; set `$OutputEncoding`/`[Console]::OutputEncoding` to UTF-8 in the wrapper; document that redirect-to-file is unnecessary.
- **Status:** open

### H-08 - `run_tests` has no `--blame-hang` option
- **First seen:** 2026-09-23 - job-1652 ("run_tests tool has no arg for them")
- **Symptom:** briefs require `--blame-hang --blame-hang-timeout 45s --blame-hang-dump-type none` (testhost hangs seen Sep 18); the tool can't pass them, so agents skip the guard or fall back to shell.
- **Proposed fix:** always add the blame-hang flags in `run_tests` (configurable timeout), or accept extra args.
- **Status:** open

### H-09 - Test verification not forced when a change can affect tests
- **First seen:** 2026-09-23 - job-1658 (VersionMajorMinor bump in Directory.Build.props)
- **Symptom:** job ran with `verify_tests` off; a hard-coded `"1.0"` assertion in `VersionSchemeTests` went red and stayed unnoticed for two commits.
- **Proposed fix:** when the job touched build-affecting files (`Directory.Build.props`, `*.csproj`, `appsettings*.json`) turn test verification on regardless of the flag.
- **Status:** open

### H-10 - Harness test/build verification can be ambiguous
- **First seen:** 2026-09-23 - job-1652 result text ended with "build verification: running... test verification: running..." while state was already `done`.
- **Proposed fix:** don't flip to `done` until verification has finished; include the verification outcome in the same payload.
- **Status:** open

---

## Parked

### P-01 - No-write-streak nudge
- Count consecutive non-mutating iterations; at ~12 inject "state your hypothesis and the one command that falsifies it", at ~25 stop as `stopped_incomplete: research_loop`. Would have fired in job-1643 and job-1647 (2026-09-23).
- A prompt-level version was added to `prompts/system-prompt.md` on 2026-09-23 (evidence-before-theory rule).

### P-02 - Read-only dispatcher lane / auto-background long shells
- From the Sep 1 review; Paul chose to leave these.

---

## Model-behaviour notes (not harness defects)

- **Theory before evidence when a test fails** (2026-09-23): job-1643 read xUnit's `···` display truncation as "the page is truncated"; job-1647 blamed "override not applied" instead of comparing test data to the production cutoff; job-1659 blamed a "stale build" for its own first-colon parser bug. Mitigation: evidence rule in `prompts/system-prompt.md`; brief it explicitly.
- **xUnit v2 3-argument `Assert.Equal/NotEqual(a, b, "message")`** (2026-09-24, job-1668, 5 call sites; also job-1660 `Assert.Contains`
  3-arg): no such overload exists, it binds to the comparer overloads -> CS1503. Known since Sep 3; still recurring. Candidate for the
  system-prompt known-traps list: "xUnit v2 asserts take no message argument (except Assert.True/False); put the message in a comment or
  use Assert.True(cond, msg)".
- **Harness can't build when a test tool is running** (2026-09-24): with fakesap started from a user terminal, the TestHarness exe is
  locked, so full-solution builds fail and jobs must run with verify_build:false. Idea: let build_verification target a project list
  (e.g. the test projects) instead of the whole solution.
- **Stale-build theory via UTF-8 DLL scan, again** (2026-09-24, job-1667): after its own assertion expected `selected>` instead of Razor's
  `selected="selected"`, the agent grepped the test DLL as UTF-8 for its string literals, found none, and concluded the build was stale.
  String literals live in the #US heap as UTF-16LE, so the scan is blind by construction (known since 2026-09-21). An override steer
  ("dump the HTML slice, fix the regex") resolved it in 3 iterations. The evidence rule in prompts/system-prompt.md was committed but not
  yet deployed; consider adding "DLL string literals are UTF-16 - never infer a stale build from a UTF-8 grep" to it.
- **Also seen (2026-09-23/24):** `run_shell` cannot start long-lived processes (fakesap died when the command returned - job object kills
  children); `devmind_task_start` with verify_build hits a locked TestHarness exe when a user runs fakesap - brief jobs to build the test
  projects directly.
- **Vacuous tests:** job-1659's page tests passed because the fake server was never used (no listeners -> no comparison). Briefs should require a test to fail before the fix.
- **Razor-only `<text>` asserted in rendered HTML** (job-1652). Now in `Razor_PITFALLS.md` (RAG id 2700).
