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
  Checked: both DevMind.McpServer processes (dist\mcp) started 16:25:30, i.e. after the deploy - so the new code IS loaded. Remaining
  suspects: the text the classifier receives, and the "- INCOMPLETE:" bullet prefix (job-1674's lines all start with "- ").
  **Root cause (2026-09-24):** the classifier WAS reading the right text - `AgentJob.SelfReportedIncomplete` runs
  `SelfReportedIncompleteDetector.Detect(Result?.Answer)`, the same `answer` devmind_task_result and the sidecar return. The miss was
  the marker match: v1.0 did `line.Trim().StartsWith("INCOMPLETE:")`, and job-1674 wrote a `**INCOMPLETE:**` header followed by
  five `- INCOMPLETE: ...` bullets; none of its lines hit the phrase list either ("unexecuted", "not observed", "not yet executed").
  v1.1 matches the marker after indent, `>`, `#`, list bullets (`-` `*` `+` `1.` `1)`) and emphasis (`**` `__` `*` `_`); a bare
  marker header quotes the line under it. job-1674's real answer is pinned as a test fixture (DevMind.McpServer.Tests/Fixtures).
- Also job-1674: a shell write re-saved a test file as UTF-16 (NUL bytes) - PowerShell 5.1 Out-File/Set-Content default; one more
  reason to block file content through run_shell (H-07).
- **Symptom:** the agent's final message lists brief steps under "NOT DONE / caller must finish" (or says a fix is "not verified"), yet the job ends `state: done`, `incomplete_reasons: null`.
- **Why it matters:** the driver has to read every final message to catch it; `done` is supposed to mean trustworthy-as-is.
- **Proposed fix:** scan the final answer for explicit incompleteness markers ("NOT DONE", "not verified", "caller must", "did not run", "hit the iteration cap") and set `stopped_incomplete` with reason `self_reported_incomplete`, keeping the text.
- **Status:** fixed, pending deploy - commit "H-01: detect bulleted INCOMPLETE markers; classify the returned final answer" (follow-up to b1df22d; the fix and this line are the same commit, so it cannot name its own hash). Verify on the next job that ends with an INCOMPLETE list.

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
- **Binary in output (2026-09-25, job-1694):** a PowerShell PDF dump put several KB of raw JPEG bytes (NUL runs, control chars,
  "JFIF", `├┐├` mojibake) into the context. **Fixed** - commit "harness: collapse binary runs in shell output":
  `BinaryOutputMask.Collapse` in AgenticExecutor.WithBuildHints (run_shell / run_build / run_tests tool results) replaces a region
  of >=16 control chars (bridging printable gaps of <=8, control chars >=50% of it) with "[... N bytes of binary data ...]". Text,
  non-ASCII letters, box-drawing and ANSI-coloured output are untouched. The live transcript still shows the raw bytes (the host
  streams it before the executor sees the output). Pending deploy.
- **Status:** open (binary part fixed)

### H-08 - `run_tests` has no `--blame-hang` option
- **First seen:** 2026-09-23 - job-1652 ("run_tests tool has no arg for them")
- **Symptom:** briefs require `--blame-hang --blame-hang-timeout 45s --blame-hang-dump-type none` (testhost hangs seen Sep 18); the tool can't pass them, so agents skip the guard or fall back to shell.
- **Proposed fix:** always add the blame-hang flags in `run_tests` (configurable timeout), or accept extra args.
- **Status:** fixed, pending deploy - commit "H-11: run_tests builds before testing (+ blame-hang)": every run_tests (headless, TUI and
  the MCP tool) now carries `--blame-hang --blame-hang-timeout 45s --blame-hang-dump-type none` (DotnetTestCommand.BlameHangArgs; fixed
  45s, not configurable yet).

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
- **Status:** fixed, pending deploy - commit "H-11: run_tests builds before testing (+ blame-hang)". `--no-build` dropped: the one
  command line now comes from `DotnetTestCommand` (DevMind.Core) for BufferedAgenticHost, TuiAgenticHost and the MCP run_tests tool, so
  `dotnet test` incrementally builds the test project and its references first; a build failure is the tool result. Tool descriptions
  say so. Smoke-run of the exact line on DevMind.Cli.Tests: build + 20/20 with the Blame collector active.

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

### H-19 - xUnit message argument (`Assert.Equal(a, b, "msg")`) written again and again
- **First seen:** Sep 3; recurring - jobs 1660 (`Assert.Contains` 3-arg), 1668 (5 call sites), 1683 and others (see Model-behaviour
  notes). xUnit v2/v3 have no message overload on these asserts; the string binds to a comparer/other overload -> CS1503 / CS1929,
  and agents spend iterations on it or misdiagnose a "stale build".
- **Fix (harness, generic, table-driven - `DevMind.Core/WriteLint.cs`):**
  - *Write-time lint:* after a successful create_file / append_file / patch_file (`AgenticExecutor` -> `ExecutionResult.LintNotes`,
    appended in `LoopHelpers.BuildToolResultContent`), files named `*Test*.cs` or containing `using Xunit;` are scanned on the CHANGED
    lines only; Assert.Equal/NotEqual/Same/NotSame/Contains/DoesNotContain/StartsWith/EndsWith/Matches/DoesNotMatch (3+ args) and
    Empty/NotEmpty (2+ args) whose last argument is a single string literal add ONE `[LINT] xUnit: ...` line per file. Never blocks.
    Calls inside strings/comments are ignored. Scan of 13,833 existing .cs files under source\repos: 0 hits.
  - *Build hint:* run_shell / run_build / run_tests output gets one `[HINT] CS1503/CS1929 on an Assert.* call ...` line when such an
    error's source line contains `Assert.` (`BuildErrorHints`, rows are {Codes, LinePredicate, Message}; Razor hints not added yet).
  - Not covered: the caller-facing `build_verification.output_tail` in devmind_task_result (agent never reads it).
- **Status:** fixed, pending deploy - commit "harness: xUnit message-argument lint on test-file writes + CS1503/CS1929 build hint"
  (the fix and this line are the same commit, so it cannot name its own hash)

### H-20 - "Run/exec command succeeded - treating as task complete" still ends real jobs (implicit-done fallback)
- **Seen 2026-09-25, job-1693 (VLink PDF composer):** after ~20 read/grep iterations the agent ran one PowerShell script (a reflection
  dump of SkiaSharp's API - research, not the deliverable). The harness printed "[AGENTIC] Run/exec command succeeded - treating as task
  complete." and ended the job after 112 s: state `done`, incomplete_reasons null, NO final answer, nothing written. H-01 could not help
  (no answer text to classify). Restarted as job-1694.
- **Code:** DevMind.Core/LoopDriver.cs ~L284-308. The August fix narrowed the fallback to "the turn so far was pure shell (no file
  mutations) and no run/exec already succeeded" - which is exactly the situation of any job's FIRST shell command during research.
- **Proposed fix:** disable the implicit-done fallback for headless/MCP-delegated jobs (they always end with task_done; the fallback was
  for chat models that never call it), or require an explicit opt-in. At minimum a job must never end `done` without a final answer -
  classify that as stopped_incomplete: no_final_answer.
- **Actual trigger:** the fallback matched any command *containing* `dotnet run`/`dotnet exec`. job-1693's script wrote a probe
  Program.cs with `Set-Content` (run_shell, so no mutation was recorded) and ran `dotnet run --project $dir` - first run, "pure shell".
- **Fix:** the implicit-done fallback is removed for every host, not just headless. Nothing depended on it except
  `LoopDriverRunExecTests` (which pinned the rescue): headless jobs end on task_done, and an interactive model that never calls it
  still ends through the prose-finish re-prompt (one extra iteration). `LoopHelpers.IsRunOrExecCommand` and the
  `HadFileMutationThisTurn` / `RunExecSucceededThisTurn` gate flags went with it. Safety net in `AgentJob`: a `done` job whose answer
  is empty after stripping harness status lines (`[CONTEXT]`/`[TOOL_USE]`/`[LLM]`/`[AGENTIC]`) is `stopped_incomplete` with reason
  `no_final_answer` - job-1693's verbatim answer is a test case.
- **Status:** fixed, pending deploy - commit "H-20: no implicit done for headless jobs; empty answer is incomplete". Verify on the
  next job that runs a `dotnet run` probe mid-research.

### H-21 - run_shell rewrites `&&` inside quoted content and here-strings
- **First seen:** 2026-09-26 - job-1697 (VLink.PSCPConnector); also hit the driver the same day.
- **Symptom:** C# passed to `Add-Type` through a PowerShell here-string came back as `wpid == pid; IsWindowVisible(h)` - the `&&`
  had been replaced by `;`, so the type failed to compile. Cost the agent several iterations before it found the cause.
- **Related:** H-15 (run_shell rewriting unquoted paths) - same family: the cmd->PowerShell translation edits command text it
  does not understand.
- **Proposed fix:** the cmd->PowerShell chaining translation must skip quoted strings and here-strings (`@'...'@`, `@"..."@`), or be
  removed.
- **Fix:** `ShellRunner.TranslateChainOperators` rewrites ` && ` only in statement-level code; quoted strings, here-strings and
  comments pass through verbatim. The rewrite is kept (not removed): run_shell runs Windows PowerShell 5.1, where `&&` is a parse
  error. The MCP run_shell description now says so.
- **Status:** fixed in 739af47, pending deploy

### H-22 - write_file/create_file emit a UTF-8 BOM on new files
- **First seen:** 2026-09-26 - driver session on the PSCP connector jobs (jobs 1697-1702).
- **Symptom:** a commit-message file written by write_file started with U+FEFF, which ended up at the start of the git subject.
- **Related:** the "Encoding drift on edit" fix (commit "harness: patch/append/overwrite preserve BOM and line endings") made EXISTING
  files keep their BOM, but left new-file behaviour unchanged - and the MCP write_file adds a BOM to every non-script new file.
- **Proposed fix:** write new files with `UTF8Encoding(false)`; keep a BOM only when the existing file had one.
- **Status:** open

### H-23 - devmind_task_status `wait_seconds=60` always fails over the remote-devices bridge
- **First seen:** 2026-09-26 - driving jobs 1697-1702 over the Claude remote-devices bridge.
- **Symptom:** every `devmind_task_status` call with `wait_seconds=60` fails with "Device 'beast' did not respond within 60s" - the
  bridge's own timeout is 60 s, so a full-length wait plus overhead always loses the race. `wait_seconds=55` works.
- **Proposed fix:** clamp `wait_seconds` to 55 in the tool (and say so in its description).
- **Status:** open

### H-24 - Child processes started by run_shell die when the call returns
- **First seen:** 2026-09-23/24 (fakesap, see Model-behaviour notes); **again 2026-09-26** - jobs 1697, 1698.
- **Symptom:** a two-call "Start-Process the dialog, then check it" always reports NOT RUNNING - the job object kills the child when
  the first run_shell returns. Both the driver and the agent lost iterations on it; nothing in the tool description warns about it.
- **Proposed fix:** a `detach: true` option on run_shell that starts the child outside the job object; either way, one sentence in
  the tool description stating that children are killed when the call returns.
- **Status:** open

### H-25 - Agent attributes its own compile errors to the toolchain
- **First seen:** 2026-09-26 - job-1699
- **Symptom:** the agent twice declared "AutoScaleMode won't resolve in the probe's net48 compile context - a recurring quirk", when
  it had typed the variable as `Control` instead of `ContainerControl`; it then abandoned a line of inquiry because of the phantom
  quirk.
- **Proposed fix:** (harness) on a repeated CS1061/CS0117 naming the same member, inject a nudge "check the declared type of the
  receiver"; (prompt) a claim of a toolchain quirk must come with a minimal repro or be retracted.
- **Status:** open

### H-26 - Rabbit hole on work the brief marked optional
- **First seen:** 2026-09-26 - job-1698
- **Symptom:** ~30 iterations spent trying to make `InternalsVisibleTo` expose private Designer fields for a test the brief marked
  optional, editing csproj/AssemblyInfo it later reverted; it took an override steer to get it back on the required work.
- **Related:** P-01 (no-write-streak nudge).
- **Proposed fix:** spend guard - when the brief marks an item optional and the agent has spent N iterations on it (or the same
  compiler error repeats twice), inject "optional - drop it and continue".
- **Status:** open

### H-27 - run_shell expands %VAR% inside quoted content and comments
- **First seen:** 2026-09-26 - found while fixing H-21 (same defect class, the next lines of `ShellRunner.ExecuteAsync`).
- **Symptom:** the cmd-style `%VAR%` expansion ran `Environment.ExpandEnvironmentVariables` over the whole command, so `%NAME%` inside
  a string, here-string or comment was replaced whenever NAME was a real environment variable (a `%TEMP%` in a single-quoted
  literal, a C# format string in an Add-Type here-string).
- **Fix:** a shared tokenizer, `ShellRunner.TokenizeShellSpans`, splits the command into code, `'...'`, `"..."`, here-string and
  comment spans, and each rewrite pass picks the kinds it applies to. `&&` is rewritten in code only. `%VAR%` is sugar for
  `$env:VAR`, so it follows PowerShell interpolation rules: expanded at statement level and inside `"..."` (the existing
  `"%USERNAME%"` guardrail test still pins this), left alone inside `'...'`, here-strings and comments - single quotes give a
  literal `%NAME%`. The MCP run_shell description says so.
- **Status:** fixed, pending deploy - commit "fix(shell): expand %VAR% only outside quoted content; share the shell tokenizer" (the
  fix and this line are the same commit, so it cannot name its own hash)

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
- **H-01 verified live after c032a9b (2026-09-24):** job-1675 ended `stopped_incomplete: needs_input`, job-1676 ended
  `stopped_incomplete: self_reported_incomplete` with the first INCOMPLETE line as the reason. Working as intended.
- **H-01 false positive (2026-09-24, job-1679):** a finished job ended with the line "INCOMPLETE: none." (the brief said "anything
  unfinished on a line starting INCOMPLETE:") and was classified stopped_incomplete / self_reported_incomplete with reason
  "INCOMPLETE: none.". Detector fix: treat "INCOMPLETE: none|nothing|n/a|-" (optionally with punctuation) as NOT incomplete. Brief
  fix (driver side, applied from job-1680 on): "only if something is unfinished, add a line starting INCOMPLETE: - otherwise omit it".
  **Detector fixed** - commit "H-01: 'INCOMPLETE: none' is not incomplete" (SelfReportedIncompleteDetector v1.2): a marker whose
  text is only none / nothing / n/a / na / - / (em/en) dash, case-insensitive, optionally emphasised with trailing punctuation, is
  NOT incomplete; "INCOMPLETE: none of the tests ran" still is. An empty marker counts only as a header over a list (first item
  quoted, unless that item is itself "none"); an empty marker with no list under it declares nothing. Pending deploy.
- **H-01 false positive #2 (2026-09-25, job-1686, after deploy of 90d5365):** a fully finished job (tests 473/75 green, clean build)
  ended stopped_incomplete / self_reported_incomplete because its summary contained the markdown HEADER "## Caller must know" (a
  notes section) - the backup phrase list matches "caller must" anywhere. Suggested fix: apply the phrase list only to lines that are
  not markdown headers, and require the phrase to be followed by an action ("caller must finish/run/fix/...") or drop "caller must" from
  the list now that the INCOMPLETE: convention exists and is used consistently.
  **Fixed** - commit "H-01: headers and 'caller must' no longer trigger self_reported_incomplete" (SelfReportedIncompleteDetector
  v1.3): markdown ATX header lines (# to ######) are skipped by the phrase list (the INCOMPLETE: marker still counts in a header), and
  "caller must" is dropped from the phrase list ("INCOMPLETE: caller must run the migration" still fires via the marker). job-1686's
  verbatim answer is a fixture and now ends `done`. Pending deploy.
- **H-17 recurrence (2026-09-25, job-1690):** the agent's first patch to AdminUiPagesTests.cs corrupted a PRE-EXISTING line (164) -
  "double-quote mangled" in its own words - CS1010 "Newline in constant". It fixed it itself within 2 iterations. Evidence that the
  quote corruption also hits lines adjacent to the edit, not only the new content.
- **RAG consulted but not applied (job-1690):** the Razor em-dash-as-&#x2014; rule is in Pitfalls_DotNet_Config_Tests, yet the agent
  spent ~8 iterations writing debug dumps before a steer pointed at it. Retrieval happens at the start of a job; the knowledge is not
  recalled at the moment a matching symptom (non-ASCII + Contains failure) appears. Idea: a build/test-output hint (like H-19 layer 2)
  for "Assert.Contains failure where the expected string has non-ASCII chars" -> "Razor HTML-encodes non-ASCII; decode first".
- **Tests that check the shape but not the substance (2026-09-25, job-1692):** the agent hand-built EXIF orientation matrices with
  `new SKMatrix { ... }` object initializers - every unset field stayed 0 (including Persp2, which must be 1) and several mappings were
  simply wrong; orientation 6 (the normal "phone held upright" case) produced a solid-black image. Its test only asserted the swapped
  width/height and the absence of EXIF, so it passed. Driver added a pixel test for all 8 orientations (red/green corner markers) and
  confirmed it fails on the old matrix. Brief-level rule worth adding for image/geometry work: "assert on pixel content, not only
  dimensions". Also: the job used learn_search for SkiaSharp APIs (good) and verified signatures against the compiler when the online
  docs did not match SkiaSharp 3.119.4.
- **list_files glob `**/config/**` returned 200 files (job-1692)** - bin/obj noise (H-18), agent fell back to the shell.
- **Swift-variant scorecard (2026-09-25, jobs 1683-1690):** 8 jobs, typical 4-10 min, 25-65 iterations; red-first in 7/8; driver fixes
  in 5/8 (test arithmetic, unbounded query, a duplicate confirm handler, a process-wide WAL guard, Serilog reflection hack); zero
  "stale build" spirals.
- **xUnit lint (H-19) first live job (job-1686):** 0 [LINT] and 0 [HINT] lines - the model did not make the mistake this time, so no
  evidence yet either way.
- **API misknowledge -> dangerous proposals** (job-1675): the agent decided .NET 10 has no SAN builder (it searched for
  `X509SubjectAlternativeNameBuilder`; the type is `SubjectAlternativeNameBuilder`), hand-built ASN.1 with non-existent types, then
  offered to DROP the SANs. It spent many iterations on scratch console projects and PE-string scans instead of `hover` /
  `go_to_definition` on the real project. Now in Pitfalls_DotNet_Config_Tests.md (RAG 2727).
- **Security regression introduced to make a test pass** (job-1676): `[IgnoreAntiforgeryToken]` on the whole Bindings PageModel
  "for the GET download" - disables CSRF on every POST handler. Caught in driver review. Worth a brief-level rule: never weaken
  auth/antiforgery/validation to make a test pass; stop and ask.
- **Encoding drift on edit** (job-1676): the agent's edits stripped the UTF-8 BOM from two installer .ps1 files that must keep it
  (PowerShell 5.1). Patch/write tools should preserve an existing BOM.
  **Fixed** - commit "harness: patch/append/overwrite preserve BOM and line endings". Cause: `PatchEngine.ReadFilePreservingEncoding`
  deliberately dropped the BOM for .ps1/.cmd/.bat/.sh; host create_file-overwrite/append wrote BOM-less UTF-8 regardless; the MCP
  write_file/append_file forced BOM-less for scripts and ADDED a BOM to every other file. Now an EXISTING file keeps its BOM (any
  extension) and its dominant line ending (`TextFileFormat`, DevMind.Core) on patch_file, create_file/write_file overwrite, append_file
  and /resolve accept_proposed, in BufferedAgenticHost, TuiAgenticHost and the MCP tools. New files: unchanged per-tool behaviour.
  Pending deploy.
- **Test-helper bugs mistaken for product bugs** (2026-09-24, job-1670): a non-verbatim interpolated string with doubled quotes
  (`$"title=\"\"{x}\"\""`) produced a regex that could never match; the failure message itself printed the correct element. Agents should
  read their own assertion's pattern when the "actual" in the message looks right.
- **Razor tag-helper registration rules unknown to the model** (job-1670): bare class name in @addTagHelper, missing HtmlTargetElement,
  `data-*` attribute binding - now in Razor_PITFALLS.md (RAG, doc_filter "Razor"). The agent did not query the RAG library during the job
  even though the brief pointed at the Razor docs; consider nudging `library_query` when a build/runtime question matches an ingested topic.
- **Vacuous tests:** job-1659's page tests passed because the fake server was never used (no listeners -> no comparison). Briefs should require a test to fail before the fix.
- **Razor-only `<text>` asserted in rendered HTML** (job-1652). Now in `Razor_PITFALLS.md` (RAG id 2700).
- **Evidence-free conclusion (2026-09-26, job-1699):** the agent concluded the defect was "a 125%+ DPI interaction" and self-reported
  incomplete; the machine was at 96 DPI and the driver had to supply the diagnosis. Note only - handled brief-side ("state assumptions
  as claims to verify"). Status: noted.
- **What worked (2026-09-26, PSCP connector jobs 1697-1702):** override steers were consumed at the next iteration boundary every
  time; continue chains kept full context across four hops; build/test reporting was honest throughout; the
  reflection-into-private-fields on-screen probe was an effective verification instrument once the driver directed the agent to it.
