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
  job-1670 (LT-04): final message "The core defect is NOT fixed ... the caller must not report LT-04 as fixed ... full test suites NOT
  run" - still `state: done`. Five occurrences in two days; this is the highest-value harness fix on the list.
- **Fix committed b1df22d (Claude Code, 2026-09-24), deployed by Paul ~16:25 - but job-1674 (16:44-16:54) still ended `state: done`,
  `incomplete_reasons: null` with FIVE lines starting "INCOMPLETE:" in its final answer.** Either the running MCP server process was not
  restarted by deploy.ps1 (old AgentJobManager still loaded), or the classifier reads a different text than the final answer (e.g. the
  task_done summary argument vs the transcript), or its line-start match misses "- INCOMPLETE:" (markdown bullet prefix). Check in that
  order: the server's loaded assembly version/timestamp, the text the classifier receives, the bullet prefix.
  **Status: fix NOT effective yet.**
- Also job-1674: a shell write re-saved a test file as UTF-16 (NUL bytes) - PowerShell 5.1 Out-File/Set-Content default; one more
  reason to block file content through run_shell (H-07).
- **Symptom:** the agent's final message lists brief steps under "NOT DONE / caller must finish" (or says a fix is "not verified"), yet the job ends `state: done`, `incomplete_reasons: null`.
- **Why it matters:** the driver has to read every final message to catch it; `done` is supposed to mean trustworthy-as-is.
- **Proposed fix:** scan the final answer for explicit incompleteness markers ("NOT DONE", "not verified", "caller must", "did not run", "hit the iteration cap") and set `stopped_incomplete` with reason `self_reported_incomplete`, keeping the text.
- **Status:** fixed, pending deploy - commit "H-01: self-reported incomplete final answers end as stopped_incomplete (self_reported_incomplete)" (the fix and this line are the same commit, so it cannot name its own hash)

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

### H-11 - `run_tests` can run against stale referenced-project DLLs
- **First seen:** 2026-09-24 - job-1669 (LT-05)
- **Symptom:** after a fix in Core, a `run_tests` invocation on Core.Tests reproduced the pre-fix failure; the agent inspected the test
  bin and found the old Core.dll. An explicit `dotnet build <test project>` refreshed it and the tests passed. (Contrast with jobs 1659,
  1660, 1667 where "stale build" was a misdiagnosis - here the agent had evidence: old DLL size/timestamp in the test bin.)
- **Suspect:** `run_tests` invokes `dotnet test --no-build` (or builds only the test project incrementally without refreshing references).
- **Proposed fix:** `run_tests` builds the test project (not --no-build) or accepts a flag; report which DLL versions were loaded.
- **Earlier evidence (found 2026-09-24 in DevMind\.devmind\memory\stale-dll-incremental-test-run.md, 2026-09-21):** during a
  mutation test of the patch_file fix, run_tests reported 10/10 passing against a DLL older than the source; a forced
  `dotnet build -t:Rebuild` then showed the real result. So this is confirmed twice, not a one-off.
- **Confirmed at the source (2026-09-24, job-1672):** the transcript shows the tool running
  `dotnet test <proj> --no-build --verbosity normal --filter ...` - it never builds. Any change is only picked up if something else
  built first. Fix: drop `--no-build` (or build the test project first) inside run_tests.
- **Counter-example, same day (job-1673):** a flip-flopping page test was blamed on "stale binary" again, but the real cause was the
  test's own `html.Contains("0 MB")` matching Disk free values like "58,030 MB". Stale builds happen (above) AND get over-diagnosed.
- **Also seen in job-1672/1673:** `devmind_task_continue` worked immediately this time (H-02 did not reproduce); the agent used the new
  prompt rules (checked DLL timestamps before rebuilding, stopped with ask_caller on a genuine-looking contradiction).
- **Status:** open

### H-12 - Write roots don't include the docs repository
- **First seen:** 2026-09-24 - driver-side patch_file on H:\users\pkailas\docs\razor\Razor_PITFALLS.md refused (outside allowedWriteRoots);
  a one-paragraph doc update had to become a DM job.
- **Fix:** add H:\users\pkailas\docs to allowedWriteRoots in %APPDATA%\devmind\devmind.json + reload_write_roots (user action).
- **Status:** fixed 2026-09-24 (Paul added the root; reload_write_roots picked it up without a restart)

### H-13 - Incremental builds hide warnings (build verification can under-report)
- **Source:** DevMind\.devmind\memory\build-warning-incremental-gotcha.md (Aug 2026): an incremental `dotnet build` reported
  "0 Warning(s)" while a `--no-incremental` rebuild showed 45 pre-existing warnings (up-to-date projects are not recompiled).
- **Proposed fix:** build_verification uses a full rebuild (or at least reports that the count is incremental). Already listed in the
  parked tool audit ("build_verification -> .slnx detection + full-rebuild warning counts").
- **Status:** open

### H-14 - create_file reports success for a path outside the working directory but writes nothing
- **Source:** DevMind\.devmind\memory\tooling-path-gotchas.md: `create_file` to an absolute path outside the working dir (e.g. %TEMP%)
  printed "[File created]" and did not write the file. Silent false success.
- **Proposed fix:** refuse with the same "outside allowed write roots" error the MCP tools give, never report success.
- **Status:** open (re-verify - may have been fixed when write-root checks were unified)

### H-15 - run_shell mangles unquoted Windows paths
- **Source:** tooling-path-gotchas.md: `$x = C:\...` (unquoted) gets rewritten into a command invocation. Quote paths.
- **Proposed fix:** don't rewrite inside assignments; or document in the run_shell tool description.
- **Status:** open (re-verify)

### H-16 - patch_file: batch is all-or-nothing, very short FIND lines fail, and recall of a read handle is stale after a patch
- **Source:** Verbella.VLink.Desktop\.devmind\memory\patch-file-failures.md (2026-09-01):
  - a multi-edit batch fails entirely ("Resolve failed") if any one edit can't be resolved;
  - a one-word FIND (`End` in VB) failed although it matched byte-for-byte (grep + hex dump) - suspected ambiguity after whitespace
    normalisation; the agent fell back to rewriting the line by index from PowerShell;
  - `recall_cache` on an `nl-` handle returns the ORIGINAL read, not the post-patch file.
- **Proposed fix:** report which edit in the batch failed and why (not found / ambiguous with N matches); reject ambiguous single-token
  FINDs with a clear message; invalidate/refresh read handles after a patch. Related: H-04 (misplaced anchor), Sep 1 near-duplicate thrash.
- **Status:** open

### H-17 - patch_file/create_file corrupt quote-heavy content (non-deterministic)
- **Source:** DevMind\.devmind\memory\patch-file-quote-corruption.md: C# `'\''`, SQL `''''` and Python `\'` sequences sometimes land
  with a dropped backslash or a wrong quote count; the same patch sometimes lands correctly. Workaround used: quote-free fixer scripts
  (`chr(39)`) and repr()-based verification.
- **Proposed fix:** find where the tool layer unescapes/normalises content (JSON decode -> string -> file) and make it byte-exact; add a
  round-trip test with quote-heavy fixtures.
- **Status:** open (re-verify against current build)

### H-18 - Older findings from DevMindTestBed\harness-findings.md (Aug 14, QuantEval runs) - status unknown, re-verify
- **working_dir changes path resolution non-deterministically:** with working_dir = a subfolder, brief paths relative to the repo root
  were found in one run and "not found" (then asserted nonexistent) in others (jobs 469-471). Fix idea: resolve against the repo root or
  fail loudly with the resolved path.
- **A successful run/exec command was treated as task completion** (job-474: "[AGENTIC] Run/exec command succeeded - treating as task
  complete" after a scratch program ran; the required output was never produced). Likely fixed since (not seen in September jobs) -
  confirm the rule is gone.
- **list_files globs return bin/ and obj/** (job-473). Also in the parked tool audit.
- **Trailing questions end as `done`, not `needs_input`** (jobs 466, 468, 473) - no continuation handle. Same family as H-01.
- **Write guard auto-approves in headless mode** ("was not read during this task - auto-approved (headless)") - the guard is advisory
  only under MCP delegation. Decide whether that is intended.

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
- **Test-helper bugs mistaken for product bugs** (2026-09-24, job-1670): a non-verbatim interpolated string with doubled quotes
  (`$"title=\"\"{x}\"\""`) produced a regex that could never match; the failure message itself printed the correct element. Agents should
  read their own assertion's pattern when the "actual" in the message looks right.
- **Razor tag-helper registration rules unknown to the model** (job-1670): bare class name in @addTagHelper, missing HtmlTargetElement,
  `data-*` attribute binding - now in Razor_PITFALLS.md (RAG, doc_filter "Razor"). The agent did not query the RAG library during the job
  even though the brief pointed at the Razor docs; consider nudging `library_query` when a build/runtime question matches an ingested topic.
- **Vacuous tests:** job-1659's page tests passed because the fake server was never used (no listeners -> no comparison). Briefs should require a test to fail before the fix.
- **Razor-only `<text>` asserted in rendered HTML** (job-1652). Now in `Razor_PITFALLS.md` (RAG id 2700).
