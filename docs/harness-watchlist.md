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
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-01: detect bulleted INCOMPLETE markers; classify the returned final answer" (follow-up to b1df22d; the fix and this line are the same commit, so it cannot name its own hash). Verify on the next job that ends with an INCOMPLETE list.

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
  streams it before the executor sees the output). Deployed 1.0.568 (2026-10-04).
- **Status:** open (binary part fixed)

### H-08 - `run_tests` has no `--blame-hang` option
- **First seen:** 2026-09-23 - job-1652 ("run_tests tool has no arg for them")
- **Symptom:** briefs require `--blame-hang --blame-hang-timeout 45s --blame-hang-dump-type none` (testhost hangs seen Sep 18); the tool can't pass them, so agents skip the guard or fall back to shell.
- **Proposed fix:** always add the blame-hang flags in `run_tests` (configurable timeout), or accept extra args.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-11: run_tests builds before testing (+ blame-hang)": every run_tests (headless, TUI and
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
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-11: run_tests builds before testing (+ blame-hang)". `--no-build` dropped: the one
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
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - with H-32: harness build verification of a plain `dotnet build` is now a `-t:Rebuild` with a
  verified warning count (reproduced 2026-09-27: a scratch .slnx with one CS0168 printed "1 Warning(s)", then "0 Warning(s)" on the
  next incremental build; -t:Rebuild and --no-incremental both re-emit it).

### H-14 - create_file reports success for a path outside the working directory but writes nothing
- **Source:** DevMind\.devmind\memory\tooling-path-gotchas.md: `create_file` to an absolute path outside the working dir (e.g. %TEMP%)
  printed "[File created]" and did not write the file. Silent false success.
- **Proposed fix:** refuse with the same "outside allowed write roots" error the MCP tools give, never report success.
- **Status:** open, watching - re-verified 2026-10-04 on 1.0.568: the MCP create_file refuses a %TEMP% path with a containment
  error. The agent-side (headless) create_file path, where this was first seen, was not re-tested.

### H-15 - run_shell mangles unquoted Windows paths
- **Source:** tooling-path-gotchas.md: `$x = C:\...` (unquoted) gets rewritten into a command invocation. Quote paths.
- **Proposed fix:** don't rewrite inside assignments; or document in the run_shell tool description.
- **Status:** wontfix (2026-10-04) - not a harness defect. Plain PowerShell does the same: an unquoted path on the right of `=`
  is parsed as a command (CommandNotFoundException). Follow-up: add "quote paths in assignments" to the run_shell tool description.

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
- **Status:** open, watching - re-verified 2026-10-04 on 1.0.568: MCP create_file + a 2-edit patch_file with `'\''`, `''''`,
  `\'`, `\"`, `\\` and backtick mixes landed byte-exact (no BOM added, LF kept). The agent path was not tested. On recurrence, pull
  the raw tool-call arguments from the trace to tell model emission from tool corruption.

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
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "harness: xUnit message-argument lint on test-file writes + CS1503/CS1929 build hint"
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
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-20: no implicit done for headless jobs; empty answer is incomplete". Verify on the
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
- **Status:** fixed in 739af47, deployed 1.0.568 (2026-10-04)

### H-22 - write_file/create_file emit a UTF-8 BOM on new files
- **First seen:** 2026-09-26 - driver session on the PSCP connector jobs (jobs 1697-1702).
- **Symptom:** a commit-message file written by write_file started with U+FEFF, which ended up at the start of the git subject.
- **Related:** the "Encoding drift on edit" fix (commit "harness: patch/append/overwrite preserve BOM and line endings") made EXISTING
  files keep their BOM, but left new-file behaviour unchanged - and the MCP write_file adds a BOM to every non-script new file.
- **Proposed fix:** write new files with `UTF8Encoding(false)`; keep a BOM only when the existing file had one.
- **Fix:** the MCP write_file/create_file and append_file new-file branches now write UTF-8 without BOM for every extension (the
  per-extension rule and `IsScriptFileExtension` are gone). Existing files already kept their BOM or lack of one via
  `TextFileFormat`; the Core/TUI hosts and PatchEngine already wrote new files BOM-less.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "fix(files): write UTF-8 without BOM unless the file already had one" (the fix and
  this line are the same commit, so it cannot name its own hash)

### H-23 - devmind_task_status `wait_seconds=60` always fails over the remote-devices bridge
- **First seen:** 2026-09-26 - driving jobs 1697-1702 over the Claude remote-devices bridge.
- **Symptom:** every `devmind_task_status` call with `wait_seconds=60` fails with "Device 'beast' did not respond within 60s" - the
  bridge's own timeout is 60 s, so a full-length wait plus overhead always loses the race. `wait_seconds=55` works.
- **Proposed fix:** clamp `wait_seconds` to 55 in the tool (and say so in its description).
- **Fix:** `AgentJobManager.MaxWaitSeconds` 60 -> 55; the tool and parameter descriptions say "clamped to 55 (leaves headroom
  under a 60 s transport timeout)".
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "fix(mcp): clamp task_status wait_seconds to 55 for 60 s transports" (the fix and
  this line are the same commit, so it cannot name its own hash)

### H-24 - Child processes started by run_shell die when the call returns
- **First seen:** 2026-09-23/24 (fakesap, see Model-behaviour notes); **again 2026-09-26** - jobs 1697, 1698.
- **Symptom:** a two-call "Start-Process the dialog, then check it" always reports NOT RUNNING - the job object kills the child when
  the first run_shell returns. Both the driver and the agent lost iterations on it; nothing in the tool description warns about it.
- **Proposed fix:** a `detach: true` option on run_shell that starts the child outside the job object; either way, one sentence in
  the tool description stating that children are killed when the call returns.
- **Fix:** MCP run_shell takes `detach` (default false; also honoured with background=true). `ShellRunner.ExecuteAsync(detach: true)`
  arms JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK on the per-command job: the shell itself stays in the job (timeout/cancel kill
  unchanged), the processes it starts are left out, so closing the job on return does not kill them. A timeout or cancel still
  taskkill-reaps the whole tree. Both run_shell descriptions (MCP and the agent's ToolRegistry) now state the lifetime rule.
- **Agent side:** delegated agents can set it too - commit "feat(agent): expose run_shell detach to delegated agents": ToolRegistry
  run_shell takes `detach` (boolean, default false) -> ToolCallMapper (`ResponseBlock.ShellDetach`) -> AgenticExecutor ->
  `IAgenticHost.RunShellAsync(command, timeoutSeconds, detach = false)` -> BufferedAgenticHost (headless jobs) / TuiAgenticHost ->
  `ShellRunner.ExecuteAsync(..., detach)`. Existing calls are unchanged (new trailing optional parameter).
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "feat(shell): run_shell detach option; document child-process lifetime" (the fix and
  this line are the same commit, so it cannot name its own hash)

### H-25 - Agent attributes its own compile errors to the toolchain
- **First seen:** 2026-09-26 - job-1699
- **Symptom:** the agent twice declared "AutoScaleMode won't resolve in the probe's net48 compile context - a recurring quirk", when
  it had typed the variable as `Control` instead of `ContainerControl`; it then abandoned a line of inquiry because of the phantom
  quirk.
- **Proposed fix:** (harness) on a repeated CS1061/CS0117 naming the same member, inject a nudge "check the declared type of the
  receiver"; (prompt) a claim of a toolchain quirk must come with a minimal repro or be retracted.
- **Fix:** (harness) `HarnessNudges` (DevMind.Core) counts compiler errors by code + member (`CS1061 'AutoScaleMode'`) in the
  shell/build/test output and tool errors of each headless iteration - once per iteration, since dotnet build prints each error
  twice. On the 3rd iteration carrying the same key, HeadlessSession appends "[HARNESS GUARD] The same compile error has repeated
  three times - stop and re-read the declaration you are calling; do not attribute it to the toolchain." to the next prompt. Once
  per key; counts reset per turn; journalled as kind "nudge" and shown as "[GUARD]" in the transcript. get_diagnostics output is
  not counted (LSP diagnostics carry no stable CS code). (prompt) `HeadlessAgent.NoToolchainQuirkRule` in the built-in headless
  rules: never blame a compiler/SDK/toolchain "quirk" without a minimal repro that excludes your own code; after two attempts,
  report the cause as unknown.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "feat(harness): optional-work spend guard and repeated-compile-error nudge; no-quirk rule" (the fix and this line are the same commit, so
  it cannot name its own hash)

### H-26 - Rabbit hole on work the brief marked optional
- **First seen:** 2026-09-26 - job-1698
- **Symptom:** ~30 iterations spent trying to make `InternalsVisibleTo` expose private Designer fields for a test the brief marked
  optional, editing csproj/AssemblyInfo it later reverted; it took an override steer to get it back on the required work.
- **Related:** P-01 (no-write-streak nudge).
- **Proposed fix:** spend guard - when the brief marks an item optional and the agent has spent N iterations on it (or the same
  compiler error repeats twice), inject "optional - drop it and continue".
- **Fix:** `HarnessNudges.FindOptionalItems` records each brief sentence (per line, then per sentence) marked "optional", "if
  quick", "nice to have" or "skip this if" ("optionally", "not optional" and "non-optional" do not count). Its keywords are its
  4+-letter non-stopwords that do not occur (up to a plural s) in the brief's other sentences. An iteration references the item
  when the agent's prose or tool-call arguments mention min(2, keyword count) of them; after 8 consecutive such iterations the
  next prompt gets "[HARNESS GUARD] This item was marked optional in the brief. Drop it and continue with the required work." with
  the sentence quoted. Once per item per session (continuation prompts are scanned too). The repeated-compile-error half is the
  H-25 nudge. New heuristic - the thrash guard matches failure signatures, not keywords, so there was nothing to reuse.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - same commit as H-25

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
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "fix(shell): expand %VAR% only outside quoted content; share the shell tokenizer" (the
  fix and this line are the same commit, so it cannot name its own hash)

### H-28 - Runaway shell call can't be interrupted by an override steer; one long timeout for every command
- **First seen:** 2026-09-27 - job-1713 (DevMind.TUI prompt history). The agent wrote a PowerShell loop to scan a NuGet XML doc and sat
  in that single run_shell call for ~4.5 min. An override steer was queued but could not land until the call ended; the driver
  cancelled the job at 304 s. Every agent shell call had the 300 s DEVMIND_SHELL_TIMEOUT that exists for solution builds.
- **Fix:** 7cd9be9 (headless: an accepted override that the next boundary would honour cancels the in-flight shell/build/test call,
  never the job), e00246d (300 s only for build/test/restore/install commands, 60 s DEVMIND_SHELL_TIMEOUT_SHORT otherwise; run_build
  always long; timeout output tells the model to re-run with timeout_seconds), 6c45b7c (same override cancel for the TUI, via the
  shared Core helper ShellCallInterrupt).
- **Status:** fixed, deployed 2026-09-27 - Paul ran the TUI manual check (override cancels, suggest does not, Esc unchanged).

### H-29 - Cancelling a shell call logs "Failed to reap process tree ... taskkill exited with code 255"
- **First seen:** 2026-09-27 - job-1713 cancel: `[SHELL] Failed to reap process tree (PID 103952): taskkill exited with code 255`.
  PID 103952 was already gone when checked a minute later, so nothing leaked this time.
- **Question:** why does taskkill report failure (process already exited? access? job object already closed it?) when ShellRunner is
  supposed to kill via a job object. A false "failed to reap" is noise; a real one leaks runaway processes.
- **Findings (2026-09-27, 18 instrumented runs: nested `powershell ... Start-Sleep 60`, 3 per route):** a false alarm. Every cancel
  ran TWO taskkills concurrently - a "best-effort early" kill registered on the cancel token and the reap - and the loser hit a tree
  already dying. No child survived in any run.

  | Route | taskkills | Exit codes seen (loser) | taskkill's words | False "Failed to reap" | Child survived |
  |---|---|---|---|---|---|
  | job cancel, detach=false | 2, racing | 128 | "not found" / "no running instance" / "operation not supported" | 0/3 | 0/3 |
  | job cancel, detach=true | 2, racing | 255, 128 | 255: children killed, root "no running instance"; 128 with children "Access is denied" | 0/3 | 0/3 |
  | timeout, either | 1 | 0 | all SUCCESS | 0/6 | 0/6 |
  | interrupt, detach=false | 2, racing | 128 | "no running instance" / "operation not supported" | 0/3 | 0/3 |
  | interrupt, detach=true (/F, no /T) | 2, racing | 1 | "Access is denied" / "no running instance" | 1/3 | 0/3 |

  The old classifier accepted only 0 and 128, so 255 (job-1713) and 1 (reproduced) became "Failed to reap". The job object was
  meant to be the authoritative kill but only ran implicitly (handle close) after the taskkill verdict.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-29: one reap per call, job object first, verdict by what survived". The racing early
  kill is gone (the cancel wakes the reap immediately anyway). ReapCall: TerminateJobObject is the primary kill; taskkill runs only
  without a job (degraded path), for a detach call's full reap (its children broke away from the job), and as a second attempt on
  survivors. The verdict is liveness - the job's live PID list plus the root process (without a job: the PIDs taskkill named that are
  still running and started after the call) - polled up to 2 s. ClassifyReapResult(exit, stderr, survivors) is pure: nothing alive =
  success whatever taskkill said; anything alive = "still alive after the reap: PID ..." with taskkill's first stderr line.

### H-30 - New files created by DM tools are LF in a CRLF repo
- **First seen:** 2026-09-27 - job-1714 created DevMind.TUI/PromptHistory.cs and DevMind.TUI.Tests/PromptHistoryTests.cs; `git add`
  warned "LF will be replaced by CRLF". Existing files keep their line endings (H-22 / TextFileFormat); only the new-file branch is
  affected.
- **Second hit:** job-1715 - SlashCompletion.cs, SlashCompletionPopup.cs, SlashCompletionTests.cs.
- **Proposed fix:** for a new file, pick the dominant line ending of sibling files in the same folder (or .gitattributes /
  core.autocrlf), falling back to CRLF on Windows.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-30: new files take the repo's line ending". Rule (NewFileLineEnding, via
  TextFileFormat.WriteNew on every new-file path): .gitattributes eol= for the path (-text/binary = as given) > .sh is LF > dominant
  ending of up to 10 neighbouring files (same extension, then any, in the folder, then its parent) > CRLF on Windows; the content is
  normalised fully. core.autocrlf and $GIT_DIR/info/attributes are not read. No BOM on new files, as before.

### H-31 - self_reported_incomplete fires on a finished job ("not run by me - the harness verifies it")
- **First seen:** 2026-09-27 - job-1714 ended `stopped_incomplete`, reason `self_reported_incomplete`, quoting the line "The full
  solution suite was not run by me - the harness verifies it." The work was complete; the harness's own test verification then ran
  1763/1763 green. Same detector as H-01, the opposite failure: a phrase match on a sentence that delegates verification rather than
  admitting a gap.
- **Second hit:** job-1715 - "- Did not run the TUI; did not commit." ("did not run"); the brief said "Don't run the TUI. Don't
  commit." Harness build + tests green (1849/1849).
- **Proposed fix:** don't flag when harness verification ran and passed for the same claim, or narrow the phrase list so "not run by
  me" followed by "harness verifies" doesn't count.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit 3f4af9b, tightened by "H-31: only a green harness TEST run outweighs a phrase-only
  self-report". Rule: an INCOMPLETE: marker always makes the job stopped_incomplete; a phrase-only hit is outweighed only by a green
  harness TEST run (verify_tests on, harness build and test verification both green) - then the job is `done` and the line is returned
  as `self_report_note`. A green build alone does not outweigh it ("The core defect is NOT fixed" still compiles). The phrase list is
  unchanged, so H-01's unverified jobs are still caught; delegated agents are now told that brief-forbidden steps are not INCOMPLETE
  items.

### H-32 - Agent runs an incremental build when the brief says `-t:Rebuild` (H-13 recurrence)
- **First seen:** 2026-09-27 - job-1712 and job-1714 both ran plain `dotnet build DevMind.slnx` despite an explicit `-t:Rebuild`
  instruction, then reported "0 warnings". The driver re-ran with -t:Rebuild both times (0/0, so no harm this time).
- **Proposed fix:** have build_verification itself run -t:Rebuild (or a clean build) when warning counts matter, so the agent's
  choice doesn't decide whether warnings are visible. See H-13.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-32: harness build verification runs a full rebuild". Rule (VerificationBuild): when the
  resolved build command is a single plain `dotnet build`, the harness verifies with `-t:Rebuild` and reports warning_count_verified =
  true plus warning_count parsed from that run; a command that is already a rebuild (-t:Rebuild / --no-incremental) runs unchanged and
  counts as verified; DEVMIND_BUILD_COMMAND overrides, other build systems, custom targets and composite commands run unchanged and
  stay unverified. DEVMIND_VERIFY_REBUILD=0 restores the incremental build. The agent's own choice of build no longer matters.
  Follow-up: verified warnings now fail the job - stopped_incomplete with `build_warnings`, the count and up to 5 warning lines
  (an unverified count never does).

### H-33 - TUI override-steer hook in Program.cs has no test
- **First seen:** 2026-09-27 - 6c45b7c. With the Program.cs call to Steer.InterruptForOverride disabled, all 471 TUI tests still
  pass; only the shared helper and decision function are tested. TuiAgenticHost can't be constructed without a live Terminal.Gui view.
- **Proposed fix:** move the steer-handler wiring behind a small testable seam (a function taking the mailbox result, depth and a
  cancel delegate), or accept the manual check as the coverage and say so here.
- **Status:** open (manual check passed 2026-09-27)

### H-34 - patch_file reports "Applied" but the edit is not in the file
- **First seen:** 2026-09-27 - job-1719 (plan mode), iteration 46: two `[PATCH] Applied to ...DevMind.TUI/Program.cs [two-way fallback]`
  lines, yet a later grep found none of the new text (`PlanModePromptNote`) and a read showed the original code. The agent's own
  diagnosis: the first edit's find text didn't match. The tool still reported success. Found only because the build then failed on
  the missing symbol.
- **Why it matters:** a silent non-landing patch is invisible when nothing downstream references the new code (comments, a changed
  constant, a removed line). Same family as the Sep 17 note "a patch_file silently didn't land on the auth-handler ctor line".
- **Proposed fix:** after applying, verify each edit's replace text is present in the written file (or re-read and diff); if not,
  report `[PATCH-FAILED: edit N not found after write]` instead of Applied. Check the two-way-fallback path specifically.
- **Root cause:** the iteration had TWO patch_file calls to Program.cs (iteration 45's output: one AUTO-READ, two Applied lines).
  AgenticExecutor resolves all of an iteration's patches before applying any, and PatchEngine.ApplyPatch spliced each edit into the
  content captured at RESOLVE time and wrote the whole file - so the second patch wrote original+edit2 over original+edit1, erasing
  edit 1. The first apply had refreshed the cache, so base == current and the merge gate took its two-way fallback: hence
  `[two-way fallback]` on both lines. Reproduced exactly (PatchLandingTests.TwoPatchesToTheSameFile_InOneIteration_BothLand failed on
  the old code with the same transcript and applied=2, errors=0). The agent's "find text didn't match" diagnosis was a guess.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-34: patches rebase onto the current file and are verified after the write". In
  PatchEngine.ApplyPatch (shared by the agent host, the TUI host and MCP patch_file): if the file changed since the patch was resolved,
  its FIND/REPLACE pairs are re-resolved against the current content (exact only; otherwise PATCH-FAILED, nothing written). After the
  write the file is re-read and each edit's REPLACE must be at the position it targeted (a pure deletion must lower the deleted text's
  count); otherwise `[PATCH-FAILED: ...] edit N did not land - <reason>`, the atomic batch is restored, and the edits that had landed
  are named. - Task-completion report shown twice in the TUI (prose + task_done summary)
- **First seen:** long-standing (user: "pretty much always"); confirmed 2026-09-27 in a plan-mode run.
- **Fix:** ac4965f - when the terminal iteration already drew >= 40 visible prose chars, task_done's summary collapses to one dim
  line and is parked for Ctrl+O / /expand; ask_caller never collapses; history unchanged.
- **Status:** fixed, deployed 1.0.568 (2026-10-04); live check pending

### H-36 - TUI intermittently drops the first character of a prompt
- **Seen:** 2026-09-27 twice. ~15:47 local: "create a file hello.txt containing hi" was echoed and SAVED to history as
  "reate a file..." (SQL Server row 74890). ~18:30: user reported "create nothing, just reply..." lost its 'c' (first prompt after
  a fresh TUI start + Shift+Tab).
- **Ruled out (18:33-18:37, DEVMIND_TUI_DIAG on):** the key parser / app-level handler (the 'c' KeyDown reached the input box);
  Shift+Tab followed by typing (echo "create test" intact); a Windows Terminal paste into the empty box (echo intact); the history
  save path (no char stripping; stores inputBox.Text.Trim()).
- **Hunch, unproven:** a timing race right after TUI start or a mode switch (both occurrences were the first prompt in that state).
- **Next time:** note exactly what preceded it (fresh start, mode switch, paste, fast typing, popup open) and add a send-path trace
  (input box text at Accepting vs what is echoed/saved).
- **Status:** open, not reproducible on demand

### H-37 - Job timeout should fire only on a stall, not on wall-clock time
- **First seen:** 2026-09-28 - job-1726 (rewind git file checkpoints) was cancelled at the 30-minute wall-clock limit while still
  making progress (iteration 122 of 200, 17/20 new tests passing). Iterations run slower when BEAST's GPU is shared with other
  work, so the same job takes longer in wall-clock time.
- **Decision (Paul):** a job that is progressing is already bounded by max_depth; the timeout exists to catch hangs. Replace the
  wall-clock kill with a STALL timeout: kill only when no iteration has completed (and no tool call has returned) for N minutes.
  No absolute wall-clock cap.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-37: delegated jobs time out on a stall, not on wall-clock time". The
  wall-clock CancelAfter is gone. A watchdog runs for the agent turn only and cancels when the job's JobLiveness record
  shows no progress for timeout_minutes (the name is kept; it is now the stall window, default 10, range 1-240).
  Progress events: every SSE data chunk the model server streams (text, think and tool-call argument deltas; not
  DevMind's own [CONTEXT] status lines), each executed tool block returning, each shell/test command starting and
  returning, each line a running shell command streams, and each completed iteration. A shell or test command holds the
  clock while it runs, until its own timeout plus one window has passed. The test baseline and the build/test
  verification run outside the watchdog on their own timeouts. A stalled job is Cancelled with error "stalled: no
  progress for N min (last progress: <what> at <time> UTC)". Tests: JobLivenessTests, StallTimeoutJobTests.

### H-38 - /rewind files restores LF working files as CRLF under core.autocrlf=true
- **First seen:** 2026-09-28 - found by job-1727 while fixing the file-checkpoint tests. BEAST's system gitconfig sets
  core.autocrlf=true. Capture uses `git add -A` into a temp index, which normalises line endings; restore uses
  `git cat-file --filters`, which applies checkout conversion. A working file that was LF is therefore captured as LF and restored
  as CRLF: git sees no change, but the bytes differ. The DevMind working tree has many LF files.
- **Options:** accept (git treats the content as identical; the next commit normalises anyway), or capture raw working-tree bytes
  (hash each file without filters) so restore is truly byte-for-byte.
- **Decision:** judge from real /rewind files runs before changing anything.
- **Status:** open, watching

### H-39 - A deployed TUI older than the MCP client change deletes `mcpServers` from devmind.json
- **First seen:** 2026-09-29, while adding MCP client part 1 (see docs/mcp-client.md). `TuiConfig.Save()` re-serializes
  only the properties it models. Before this change it had no `mcpServers`, so any TUI save (`/mode`, `/rules`,
  `/depth-cap`, ...) silently dropped a hand-added block. The fix is `TuiConfig.McpServers`, a raw `JsonObject` kept verbatim
  and pinned by `McpServerConfigTests.TuiConfig_save_round_trips_the_block_verbatim`. It only helps once deployed.
- **Watch:** after deploy, confirm the `comfy` entry survives a `/mode` toggle. The same trap applies to ANY future key
  added to devmind.json by hand without a TuiConfig property.
- **Status:** fixed, deployed 1.0.568 (2026-10-04)

### H-40 - Claude Code's LSP index stays stale after a package restore and after out-of-editor edits
- **First seen:** 2026-09-29, during MCP client parts 1 and 2. `dotnet add package ModelContextProtocol` restored
  2.2.0 into DevMind.Core, and `dotnet build` resolved it at once. The LSP diagnostics kept reporting
  `ModelContextProtocol` / `McpClient` / `CallToolResult` as CS0246 "could not be found" for the whole of part 1 and
  on into the next session. In part 2, members added to project files by a script (`BlockType.McpCall`,
  `ResponseBlock.McpServer`, `ToolCallResult.RawArguments`) also stayed "missing" in the LSP diagnostics of
  dependent files, even after a clean build.
- **Why it matters:** the brief says to confirm SDK APIs with LSP hover / go-to-definition. With a stale index that
  check is impossible, and the diagnostics look like real compile errors. An agent that trusts them would "fix"
  code that compiles.
- **Workaround used:** check SDK APIs by reflection over the restored DLL plus its XML docs, and treat
  `dotnet build` as the truth over LSP diagnostics.
- **Proposed fix:** reload the workspace (or restart the language server) after a restore or a
  `*.csproj` change, and when files change outside the editor.
- **Status:** open

### H-41 - MCP calls missing from the action journal
- **First seen:** 2026-09-29, live check after deploying 2a5eb31. job-1739's result had `"mcp": {"calls": 3}` but
  `"actions": []`. Shell commands and file edits are journaled; calls to external systems were not, so a reviewer
  could not see what the agent did to them.
- **Cause:** the journal is written inside `BufferedAgenticHost`'s own operations (`run_shell` in `RunShellAsync`),
  and MCP calls run in the executor, so no journal call existed on their path.
- **Fix:** the executor's `McpCall` case journals every call that runs — ok, error or timeout alike — as kind `mcp`.
  It writes through an `IActionJournal` capability that `BufferedAgenticHost` implements. The detail is
  `server.tool {args≤500} → outcome, N chars, S.s s` (see docs/mcp-client.md).
- **Status:** fixed, deployed 1.0.568 (2026-10-04). Verify: a job with mcp_servers shows one `mcp` action per call.

### H-42 - A crashed MCP server was relaunched silently
- **First seen:** 2026-09-29, job-1740: the job's comfy-mcp (PID 49700) was killed mid-job. The next `mcp__comfy__which`
  succeeded after the automatic relaunch, and neither the transcript nor the journal said so.
- **Fix:**
  - `McpClientManager` raises a `Notice` once per relaunch: `[MCP] comfy restarted (previous process exited: exit
    code N)`, or `(restart requested)` for `/mcp restart`.
  - It raises one more when two failures in a row stop automatic relaunching (`not restarted: failed twice in a
    row…`), once, not per call.
  - The TUI prints notices to the transcript. Headless sessions write them to the job transcript and journal them as
    `mcp_restart`.
- **Status:** fixed, deployed 1.0.568 (2026-10-04). Verify: kill a job's MCP server mid-job and look for the line and the
  `mcp_restart` action.

### H-43 - "INCOMPLETE: none" still trips self_reported_incomplete when followed by text or a bullet
- **First seen:** 2026-10-01 (Strata / Qwen3.8-Flash-Next backend), job-1850 (`- INCOMPLETE: none. The scheduled-task
  registration is explicitly left to the caller`) and job-1861 (`INCOMPLETE: none. (Full-suite run intentionally delegated to the
  harness per job rules.)`). Both jobs were finished and clean; both ended `stopped_incomplete / self_reported_incomplete`.
  job-1863 (`INCOMPLETE: none.` alone) correctly ended `done`.
- **Cause:** detector v1.3 treats the marker as "none" only when the WHOLE marker text is none/nothing/n-a; an explanatory sentence
  after it, or a leading list bullet, makes it count.
- **Fix:** a marker whose text STARTS with none / nothing / n/a / na / -, optionally after a list bullet (`-`, `*`, `1.`), and
  followed by punctuation or a sentence is NOT incomplete. "INCOMPLETE: none of the tests ran" must still fire (word-boundary on
  "none" followed by "of").
- **Prompt side (done 2026-10-01):** system-prompt.md now says to write the INCOMPLETE word only when something is unfinished.
- **Reopened 2026-10-02:** job-2104 (`INCOMPLETE: none. Only the TotalAgility page changed; ...`) tripped the same way, and
  job-2118 ended `stopped_incomplete` on `INCOMPLETE: full solution test suite (1308 + 11 new) not run by me — harness verifies
  it. Nothing else outstanding.` with build verification clean (0 warnings) and test verification green (1319, +11),
  self_report_note null. Its status already read stopped_incomplete while the tail's last line was `[job] test verification:
  running...`.
- **Cause (job-2118):** NOT an ordering bug. The worker already holds the job in Running through build and test verification
  and publishes Done only after both settle; IsIncomplete / IncompleteReasons / SelfReportNote all require Done. Two real causes:
  (1) H-31's gate let a green harness test run excuse only a Weak (phrase-list) hit; an explicit INCOMPLETE: marker (Strong)
  stopped the job whatever the harness measured. (2) Nothing was appended to the tail after verification finished, so a
  finished job's last tail line was still "test verification: running..." — which read as a verdict issued mid-verification.
- **Fix (done 2026-10-02):**
  - AgentJob.SelfReportMakesIncomplete: ANY self-report (marker or phrase) stops the job unless HarnessTestVerified (verify_tests
    on, build verification passed, test verification passed). When it is excused, the line is quoted in self_report_note.
    verify_tests off / failed / requested-but-not-run, or a green build alone → stopped_incomplete as before.
  - SelfReportedIncompleteDetector v1.5: the none-word (none / nothing / n/a / na / dash), optionally "outstanding" /
    "remaining" / "left" / "pending", followed by end of line, a sentence after `.` `!` `;`, or a parenthesis or dash, is the
    all-clear. A word straight after it ("none of the tests ran", "n/a for build, but ...", "nothing compiles yet") or a comma
    ("none, but ...") still fires.
  - The worker appends `[job] build verification: passed|failed|skipped` and `[job] test verification: passed|failed (N tests)`
    after each run, so the tail no longer ends on "running..." once the verdict is out.
  - SelfReportedIncomplete is no longer cached while Result is null (a read before the answer existed would have pinned None).
  - Tests: SelfReportVerificationGateTests (marker x verification table, job-2118 verbatim green / failed / off / not run,
    none-markers incl. job-2104 → done with no note, status during a held test run is `running` with no reasons or note, then
    `done` + note); SelfReportedIncompleteDetectorTests (trailing-explanation none-markers, comma and word negatives).
- **Trade-off:** an `INCOMPLETE: <real gap>` line on a job whose harness tests are green now ends `done` — the gap is in
  self_report_note, not incomplete_reasons. A driver must read self_report_note.
- **Status:** fixed (2026-10-02).

### H-44 - DevMind's own test suite writes fake jobs into the live tasks folder
- **First seen:** 2026-10-01: result sidecars job-1769..job-1848 were created in 4 minutes (11:28-11:32 UTC) while job-1768's
  test verification ran the McpServer tests. Their working_dir is a temp fixture folder (`%TEMP%\devmind_testcount_job_...`,
  `devmind_stall_job_...`, `devmind_h31_...`). They consume real job numbers and pollute anything that reads the tasks folder
  (dm-watch, dm-daily - which now filters working_dir under %TEMP% as a workaround).
- **Again 2026-10-01 ~11:00-11:40:** job ids jumped from job-1866 to job-1954; all 83 result files in between have a
  working_dir under %TEMP% (test fixtures) - a DevMind test run in another session.
- **Fix:** every test that constructs an AgentJobManager / HeadlessSession must use a per-test tasks dir AND a per-test job-id
  counter. TestTasksDirInitializer evidently does not cover the harness's `dotnet test` run; check whether it is a module
  initializer in every test assembly that creates jobs, and whether the job-id counter file is shared.
- **Recurrence 2026-10-02 (found via a test failure):** `TranscriptDirOverrideTests.StubJobs_WroteArtifactsToPrivateDir_NotTheGlobalFolder`
  failed in full McpServer runs ("DEVMIND_TASKS_DIR should be set ...") but passed alone - on 64e86d5 and f6e977a alike. The test run
  at 13:28 wrote job-2094..job-2102 into the live folder and moved the live `_jobcounter.txt` to 2102.
- **Root cause:** not the initializer. TestTasksDirInitializer is a [ModuleInitializer] and works. `ResultSidecarTokenUsageTests`
  (added in 6b9b917, 2026-10-01 07:34 - the start of the leak) set DEVMIND_TASKS_DIR to its own dir and, in Dispose, set it to
  **null** instead of restoring the prior value. Every test that ran after it in the same process fell back to the production
  default `%TEMP%\devmind\tasks`, so all its stub jobs, transcripts and job ids went to the live folder. The job-id counter is
  per tasks dir, so it was hit too.
- **Fix:** `ResultSidecarTokenUsageTests` captures the prior value and restores it. Every other DEVMIND_* env mutation in
  Core.Tests and McpServer.Tests already restores (checked by grep). New `[Collection("ProcessEnvironment")]`
  (`ProcessEnvironmentCollection`, DisableParallelization = true) in both assemblies, on every class that repoints
  DEVMIND_TASKS_DIR or DEVMIND_GLOBAL_DIR. Both assemblies already turn off parallel collections in xunit.runner.json, so this
  is a second safeguard. Verified: 3 full McpServer runs in a row, 0 failures (StubJobs passes inside the full run), and the live
  folder listing and `_jobcounter.txt` are byte-identical before and after.
- **Leftovers (not deleted):** 276 stub-job result sidecars plus their 276 transcripts in `%LOCALAPPDATA%\Temp\devmind\tasks`,
  job-1769..job-2102 (159 dated 2026-10-01, 117 dated 2026-10-02). They are identified by a working_dir under `%TEMP%\devmind_*`
  (testcount_job, mcpjob, noexec_job, stall_job, steer_guard, showthinking_job, effort_job, verifyrace_job, jobend_bak, h31,
  h32). The 330 other sidecars are real jobs. Safe to remove by that working_dir filter whenever convenient.
- **Status:** fixed - commit "H-44: ResultSidecarTokenUsageTests restores DEVMIND_TASKS_DIR; env-seam tests in one serial
  collection". Test code only; nothing to deploy.

### H-45 - Test baseline measured on a broken build gives a misleading delta
- **First seen:** 2026-10-01, job-1859 (continuation of job-1858): the job-1858 tree had a test file that did not compile, so
  the continuation's baseline run measured only the Core tests (101) and the result reported `delta +747`. The real change was +17.
- **Fix:** if the baseline build fails, set `baseline_unavailable_reason: "baseline build failed"` and `delta: null` instead of
  reporting a partial count.
- **Status:** open.

### H-46 - Nudge when the agent reads build output to explain a failure
- **First seen:** 2026-10-01, job-1859: twice spent stretches grepping `*.deps.json` and listing bin/ while the cause was a
  one-line product bug in the page's comparison; an override steer was needed. Same family as the UTF-8 DLL scans (Sep 21-24).
- **Fix:** when a read/grep/list target is under `bin\`, `obj\`, or is `*.deps.json` / `*.dll` / `*.pdb` during a job with a
  failing test, inject once: "Build output is never the evidence. Print the failing assertion and the actual value, fix what that
  shows." (H-19-style hint.) Prompt rule added 2026-10-01; this is the harness backstop.
- **Status:** open.

### H-47 - Nudge after repeated failed web fetches for framework source
- **First seen:** 2026-10-01, job-1858: "burned many iterations trying to confirm ServerAddressesFeature's constructor/namespace
  from GitHub source (repeated 404s) instead of just writing the fake and letting the compiler answer" (agent's own words). The
  job ended with its tests unwritten.
- **Fix:** after 2 consecutive web_fetch / learn_fetch failures (404 or not-found) in a job, inject: "Stop fetching. Write the
  code (or your own class implementing the interface) and build - the compiler is the reference." Prompt rule added 2026-10-01.
- **Status:** open.

### H-48 - Block mutating git commands in delegated jobs
- **First seen:** 2026-10-01, job-1862: `git stash` -> run the suite on the base -> `git stash pop` inside the working tree to
  compare before/after. It restored cleanly this time; a failed pop would lose the job's uncommitted work (Paul owns commits).
  Seen before on 2026-09-18.
- **Fix:** the shell guard (which already blocks Stop-Process) refuses `git stash|checkout|switch|reset|clean|restore|add|commit|
  rebase|merge` in delegated jobs unless allow_commit is set, with a message: "read-only git only (status, diff, log, show)".
  Prompt rule added 2026-10-01.
- **Status:** fixed, pending deploy - commit "H-48: shell guard blocks mutating git in delegated jobs". Rule: `GitWriteGuard.Classify`
  (one git lexer, now with an `allowCommit` flag threaded through `IsBlockedHeadlessCommand` and `BufferedAgenticHost.AllowCommit`,
  set from the job's allow_commit) refuses git subcommands that change the repo, index, refs or working tree: stash (not `stash
  list|show`), checkout, switch, reset, clean, restore, add, rm, mv, commit, rebase, merge, cherry-pick, revert, am, apply, pull,
  push, fetch, gc, prune, update-ref, worktree (not `worktree list`), tag/branch create-delete-rename forms, config writes, remote
  add/remove/set-url etc., plus submodule, bisect, notes, update-index, filter-branch, symbolic-ref, sparse-checkout, init, clone,
  read-tree, checkout-index and replace. Reads pass: status, diff, log, show (H-59 write rule unchanged), blame, ls-files, ls-tree,
  rev-parse, rev-list, cat-file, describe, shortlog, grep, bare/`--list`/`-a`/`-v` branch, bare/`-l` tag, `remote -v`, `config
  --get*`/`--list`. With allow_commit only add and commit are let through. The subcommand is found past `-C <dir>`, `-c k=v`,
  `--no-pager`, `--git-dir`/`--work-tree`, for `git`, `git.exe` or a quoted path to git.exe, and inside `cmd /c "..."` /
  `powershell -Command "..."` (the wrapped line is re-classified, so a redirect of `git show` inside one is now caught as well).
  Reason: "read-only git only in delegated jobs (status, diff, log, show, blame, ls-files...); Paul owns commits"; the
  [BLOCKED] text points the agent at `git diff` / `git show` for base comparisons. Restore/write cases keep RestoreReason /
  WriteReason. `git checkout main` and `git checkout -b feature`, previously allowed under H-59, are now blocked.
  Tests (HeadlessGuardrailTests): `MutatingGit_IsBlocked` (one case per subcommand), `MutatingGit_IsBlocked_PastGlobalOptionsAndWrappers`
  (incl. `git -C sub stash`, `cmd /c "git stash"`, job-1862), `ReadOnlyGit_IsAllowed` (incl. `git --no-pager log`, `git branch`,
  `git config --get user.name`), `MutatingGit_ReasonNamesReadOnlyGitAndOwner`, `AddAndCommit_BlockedWithoutAllowCommit_AllowedWithIt`,
  `AllowCommit_StillBlocksEverythingButAddAndCommit` (incl. `git push`), `RestrictedHost_BlocksGitStash`; GitWriteGuardTests:
  `BranchCheckout_IsBlockedAsMutation`, H-59 `git show` cases unchanged.

### H-49 - think:true silently ran at reasoning effort xhigh
- **First seen:** 2026-10-01, after the switch to Strata (Qwen3.8-Flash-Next). Its chat template (and the Qwen3.8-27B one)
  reads `chat_template_kwargs.reasoning_effort` when thinking is on, and treats a missing value as `xhigh`, which adds a
  "think carefully" system line. DevMind sent only `{enable_thinking}`, so every think:true job ran at xhigh.
- **Fix:** with thinking on, the request now sends `reasoning_effort` (default `medium`, which adds no instruction).
  devmind_task_start takes `reasoning_effort` (low|medium|high|xhigh). Supplying it implies think, and continuations
  inherit it. The start response and devmind_task_result echo it (null when thinking is off). devmind.json
  `reasoningEffort` sets the default for MCP jobs and the TUI. With thinking off, the request is unchanged:
  `{enable_thinking:false}`.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) (commit "Default reasoning_effort to medium when thinking is on; add reasoning_effort
  to devmind_task_start"). Verify: a think:true job's request carries `"reasoning_effort":"medium"`.

### H-50 - Nudge when the agent pages shell output instead of reading the file
- **First seen:** 2026-10-01, job-1954: read an old file version as `git show HEAD:<file> | Select-String ... | Select-Object
  -Skip 13`, then `-Skip 16`, `-Skip 19`, `-Skip 22` - one shell call per few lines. Harmless but slow and context-heavy.
- **Fix:** when 3+ consecutive shell calls differ only in a -Skip/-First/head/tail offset over the same source, inject once:
  "Use read_file (start_line/end_line), or `git show <rev>:<path>` redirected to a file and read that."
- **Status:** open.

### H-51 - Override steers are not consumed while the agent repeats one shell command
- **First seen:** 2026-10-01, job-1866: an override steer was queued at iteration 78 while the agent re-ran the same single test;
  it kept re-running the same isolation test (isoF..isoK, 6+ identical commands) through iteration 89 with no sign of the steer,
  and had to be cancelled. On other jobs today overrides were folded in at the next boundary.
- **Fix:** (a) check whether the steer was actually consumed (journal disposition) and why not; (b) a repeat-command guard:
  the same shell command 3x in a row with identical output -> inject "same command, same result; state your hypothesis and the
  one different command that tests it" (the prompt already says this; the model ignored it under a loop).
- **Status:** open.

### H-52 - A narration-only reply ends a delegated job as "done"
- **First seen:** 2026-10-02, VLink.Warehouses: job-1974 ended `state: done` after 85 iterations. Its whole answer was one line of
  narration ("Let me confirm `MailConnectionSettingsJson` API ... then check the `SmtpSecurityMode` enum values.") - no task_done,
  no report. Its continuation job-1975 ended `done` after 2 s the same way ("Let me read the Notifications CreateConnection tail ...").
- **Root cause:** on a no-tool-call reply, `LoopDriver` has two guards - the narration-stall retry (`NarrationRetryUsed`) and the
  prose-finish re-prompt (`PromptedForTaskDone`) - each one-shot, reset only in `LoopState.ResetForUserTurn()`. `HeadlessSession`
  calls that once per job, so a delegated job got ONE of each for its whole life: an early stall spent them, and the next
  narration-only reply hit "accepting prose-finish" -> terminal. The headless result then had no harness reason and a non-empty
  answer, so neither H-20's `no_final_answer` nor H-01's self-report classifier fired, and the job read as `done`.
- **Fix:** (1) the guards are per stall: any iteration with a tool call resets both, plus a new
  `LoopState.ConsecutiveNoToolCallResponses`; 3 consecutive no-tool-call responses
  (`LoopDriver.MaxConsecutiveNoToolCallResponses`) always terminate. Applies to the TUI too; its prose answer is still a valid end.
  (2) Headless only: a terminal iteration with no task_done, no ask_caller and no harness reason sets
  `HeadlessAgentResult.EndedWithoutTaskDone`; the answer keeps the last prose under
  `[INCOMPLETE: ended without task_done — last message was narration]` (no prose -> answer stays empty and `no_final_answer` also
  fires). `AgentJob.IsIncomplete` / `IncompleteReasons()` extend H-20's path: `stopped_incomplete` with `ended_without_task_done`.
  Tests: `NarrationStallPerStallTests` (job-1974/1975 narration verbatim); removing the reset fails
  `SecondNarrationStall_AfterAToolCall_GetsItsOwnRePrompt_AndReachesTaskDone`.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-52: stall guards per stall; a job ending without task_done is stopped_incomplete".

### H-53 - Missing or misnamed tool arguments surface as raw null exceptions
- **First seen:** 2026-10-02, VLink.Warehouses jobs 1971-1977: `[READ ERROR] : Value cannot be null. (Parameter 'key')` and
  `[GREP ERROR] : ...` many times (empty file name), and `[FILE ERROR] : Value cannot be null. (Parameter 'path2')` from a
  create_file with the path under the wrong key. The model guessed its way to the fix.
- **Root cause:** `ToolCallMapper.GetArg` returns null for a missing key and `MapSingle` built blocks with a null FileName
  without telling anyone. The executor then indexed `ToolResultContents[null]` (read/grep/diff), and the host did
  `Path.Combine(dir, null)` (create_file), and the raw exception message became the only feedback.
- **Fix:** `ToolCallMapper.ValidateArguments` checks the required arguments of read_file, grep_file, find_in_files, create_file,
  append_file, patch_file, delete_file, rename_file, diff_file, list_files and run_shell before anything runs. The required keys come
  from `ToolRegistry`'s schemas (every parameter not marked `[optional]`), not a hand-written list. (`write_file` is an MCP-server
  tool, not in the agent catalogue.) A missing or blank one (blank `content` is allowed: an empty file is legitimate) means the call is
  not executed. Its tool result is `[TOOL ERROR] read_file: missing required argument 'filename'. Received: path, start_line.
  Expected: filename (required), start_line, end_line, force_full.` (`ToolCallResult.ArgumentError`, returned first by
  `LoopHelpers.BuildToolResultContent`). An unknown tool name now gets `[TOOL ERROR] Unknown tool call: x` as its result instead of
  `[Executed]`. Second safeguard: `AgenticExecutor` (read/grep/diff/create/append/delete) and `BufferedAgenticHost` (save/append/read)
  report "no file name was provided" instead of the exception.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-53/H-54/H-55: tool argument errors are reported, patch edits validated, per-call
  read results". Tests: `ToolArgumentValidationTests`.

### H-54 - patch_file edits with the wrong keys are skipped silently
- **First seen:** 2026-10-02, job-1973: `[PATCH] Block 1: FIND is empty after fence stripping — skipping.` repeatedly - the model
  sent edits keyed `old_text`/`new_text`, and later wrote "use find/replace keys (not new_text/old_text)" in its own notes.
- **Root cause:** `ToolCallMapper` skipped every edit item without `find` (`if (string.IsNullOrEmpty(f)) continue;`). With no
  usable pairs it fell back to the top-level `find` (also absent) and produced an empty FIND that `PatchEngine` skipped. A malformed
  `edits` JSON fell back the same way, and an item missing `replace` was silently turned into a deletion.
- **Fix:** an `edits` array, when given, must be usable as a whole. A bad item rejects the call with e.g. `patch_file: edit 1 has
  no 'find' key (keys: old_text, new_text). Each edit must be {"find": ..., "replace": ...}.`, and likewise for a missing `replace`,
  an empty `find`, a non-object item, an empty array or non-JSON. There is no fallback to the top-level find/replace when `edits`
  was given, and no aliases. Without `edits`, `find` and `replace` are both required. `ToolCallMapperTests` pinned the old fallback;
  it now pins the error.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - same commit as H-53. (Known aliases are accepted since H-56; the error now fires only for
  genuinely unknown keys.)

### H-55 - A grep and a read of the same file in one turn return the same result
- **Reported as:** "invalid regex is a silent no-match" - job-1973's grep_file `ConnectAsync|Calls.Add("connect` "returned only the
  [READ:] echo and no GREP section", and the agent concluded "grep_file is broken for patterns containing parentheses".
- **Actual cause (from the job-1973 transcript):** the grep worked - `[GREP] 5 matches for "ConnectAsync|Calls.Add("connect"` -
  and grep_file/find_in_files are not regex at all (`SearchPattern`: case-insensitive substring, `|` = OR), so an unbalanced paren
  cannot fail. The same turn also ran read_file on the same file. Read, grep and diff results are all filed under the file name in
  `ExecutionResult.ToolResultContents`, so the read overwrote the grep and both tool messages carried the read. The same collision
  hits two reads of different ranges of one file in one turn.
- **Fix:** read_file, grep_file, diff_file, find_in_files and list_files also file their result under the call's id
  (`ExecutionResult.ToolResultsByCallId`, keyed by `ToolCallResult.ResultId`), and `LoopHelpers` prefers it. The filename-keyed map
  is unchanged for its other readers (training log, MCP).
- **Finished (second commit):** three more ways a call could get another call's result in the same turn.
  - patch_file reported from turn-wide state (`PatchedPaths`, `Errors`) and filed its post-patch view under the file path, the key
    read_file used. Each patch call now gets a result built from only its own outcome (`ExecuteBatchPatchesAsync` keeps a per-call
    scope), filed under its call id, formatted by the same `LoopHelpers` code.
  - Patches ran after EVERY other call in the turn, so patch_file then read_file read the pre-patch text, and patch_file then
    run_build built unpatched code. Calls now run in call order. Consecutive patch calls still form one batch (one diff-preview
    card), applied before the next non-patch call.
  - create_file / append_file / delete_file / rename_file reported from turn-wide lists, so a failed create_file after a
    successful one was told "[File created: <the other file>]". Each now gets only what it added (`WriteCallScope`).
  - Also: plan mode's `[PATCH-REFUSED:...]` was missing from the patch result's error filter, so the model was told the find-text
    matched nothing.
  Tests: `PerCallToolResultTests` (patch then read, read then patch, two patches of one file, two create_files one of which fails).
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commits "H-53/H-54/H-55: ..." and "H-55: per-call results for patch and write tools; calls
  run in call order".

### H-56 - patch_file called with other editors' key names costs an iteration each time
- **First seen:** 2026-10-02, job-2108 (Qwen3.8 Flash): lost at least 4 iterations to H-54 errors from `old_text`/`new_text`, and
  narrated "I keep reaching for new_text/old_text by reflex". Also seen: `old_string`/`new_string`, `search`/`replace`. The H-54
  error was accurate, but the model repeated the mistake regardless.
- **Fix:** `ToolCallMapper.NormalisePatchAliases` runs before validation and renames `old_text` | `old_string` | `search` | `from`
  -> `find` and `new_text` | `new_string` | `to` -> `replace`, at the top level and in every `edits` item. When a canonical key and an
  alias (or two aliases) carry DIFFERENT text the call is rejected: `patch_file: edit 2 has both 'find' and 'old_text' with different
  text — it is ambiguous which to use. Send the text under 'find' only.` An identical duplicate is dropped. Any other key still gets
  the H-54 error. A call that was renamed journals one `tool_args` entry, count only, e.g. `patch_file: normalised alias
  old_text->find, new_text->replace (4 keys)` (`ToolCallResult.ArgumentNote` -> `ResponseBlock.ArgumentNote`, recorded by
  `AgenticExecutor` through `IActionJournal`; the TUI has no journal). Watch the count: if it stays high, the schema or prompt is
  fighting the model's training and the descriptions may need the aliases spelt out.
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-56: patch_file accepts common find/replace key aliases". Tests:
  `ToolArgumentValidationTests` (H-56 section, mutation-checked: disabling the rename fails all 15).

### H-57 - After one override steer the model re-acknowledges it every turn
- **First seen:** 2026-10-02, job-2108 (transcript `job-2108-20261002-154500.log`): an override steer folded at iteration 108
  ("Stop tuning the regex. Find the wrapper with string search instead..."); iterations 109-122 opened with "The caller's
  string-search approach...", "Following the caller's redirect..." (7 of those by iteration 122; 14 acknowledgements in all before the next steer) - the same steer re-acknowledged each turn.
- **Finding: the steer is NOT re-sent. The repetition is the model's own.** The transcript has one `[STEER] override folded`
  line. In code: `HeadlessAgent.DrainSteerIntoPrompt` takes the steer from the single-slot `SteerMailbox` (`Take` clears it)
  and `Steer.Apply` appends the framed block to THAT iteration's prompt, which `LlmClient.SendMessageAsync` adds to history
  as one user message. The next iteration's prompt is rebuilt from scratch (`iter.NextContextualMessage`, normally
  `SyntheticPrompts.Continue`), so later requests carry the steer only as that one history message, with a fresh
  "Continue with the task." as the newest user message. No system/suffix block holds it; the scratchpad never contained it; no
  compaction or brainwash ran in that window (brainwash re-anchors on the original task prompt, not on steers). Likely cause:
  the model copied its own previous replies, which all opened with the acknowledgement.
- **Fix (framing only, injection unchanged):** the override framing now ends "Acknowledge once, then continue; do not restate
  this instruction." Suggest framing is unchanged (job-2108's suggest steer at ~line 1420 was acknowledged once).
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-57: override steer asks for a single acknowledgement". Tests:
  `SteerInjectionTests.ConsumedOverride_IsInTheNextRequestOnce_AndNeverReSentAsANewMessage` (the steer appears exactly once in
  the request that consumes it, in the newest user message; the next request still has one copy, in history, and its newest
  user message is the re-trigger; mutation-checked - a mailbox that does not clear on Take sends two copies and fails it);
  `SteerDecisionTests.Frame_DiffersByMode_AndIsMarkedAsACallerSteer` pins the new text. Watch the next override: if the model
  still echoes it, the problem is self-imitation, and the next step would be a harness nudge rather than more prompt text.

### H-58 - The same tests fail run after run while the agent changes its theory
- **First seen:** 2026-10-02, job-2108: the layout tests (`AdminInputWidthTests.*_LongFields_*`,
  `TargetsEditFormLayoutTests.Get_Header_*`) failed in run after run while the agent re-tuned a regex; it only dumped the
  real HTML late, and once it looked the fix was quick. The H-52 stall guards do not fire: the agent is editing, so it is
  "progressing".
- **Fix (note):** `RepeatedTestFailureGuard` (DevMind.Core/RepeatedTestFailures.cs) reads each iteration's tool output
  (`run_tests` and `run_shell dotnet test` both land in `ExecutionResult.ShellOutput`). A run is a test-summary line (`Test Run
  Failed./Successful.`, `Failed!/Passed!  -`, MTP `Test run summary:`); the failing names are the `Failed <name> [<duration>]`
  lines (the duration keeps out "Failed to load prune package data" noise) and MTP `failed <name> (<duration>)`. The tracked
  set is the names that failed in EVERY run of the streak, so a full run followed by `--filter` re-runs of one of its failures
  still counts (job-2108 alternated both). At 3 runs, one note is folded into the next prompt as `[HARNESS GUARD] The same
  test(s) have failed 3 runs in a row: <names>. Before changing code again, print the actual value being asserted ...` and
  journaled as kind `harness_note`. Once per distinct set and once per streak. A passing run, or a failing run with no name in
  common with the streak, resets it; a run whose output names no test (quiet verbosity) and an iteration with no run change
  nothing. Delivery: the same fold-at-the-boundary path a steer uses (H-57: injected once, kept once in history), framed as the
  harness's voice like the H-25/H-26 nudges rather than as `[CALLER STEER]`, because the caller did not say it.
- **Fix (auto-think):** when the note fires on a job started with `think` off, `HeadlessSession` sets `ShowLlmThinking` on
  and `ReasoningEffort` "medium" on its options; `LlmClient` reads both per request, so the next request thinks. Journaled
  `harness_note: thinking auto-enabled (repeated failures: <names>)`. It goes off again, back to the job's own values, on a
  passing run or after 15 requests with it, whichever is first (`thinking auto-disabled (tests passed)` /
  `(15-iteration cap)`). Every turn starts from the job's own settings, so a continuation never inherits harness-enabled
  thinking. Not when the job was started with `think: true` (its own effort stands), nor with the new `devmind_task_start`
  flag `auto_think: false` (default true; continuations inherit it). `devmind_task_result` (and the result sidecar) report
  `auto_think_escalations` and `auto_think_iterations` (requests sent with harness-enabled thinking).
- **Status:** fixed, deployed 1.0.568 (2026-10-04) - commit "H-58: note and auto-think on repeated identical test failures". Tests:
  `RepeatedTestFailureTests` - parser (VSTest normal/minimal, MTP, build-only output), guard (3 runs -> one note; changed set
  and passing run reset; filtered re-run counts; same set never noted twice), escalation decisions, and three runs through
  the real headless loop (thinking on in exactly request 4 then off when the tests pass; on for exactly 15 requests under a
  permanent failure; no escalation with think on or auto_think off). Mutation-checked: disabling escalation, the cap or the
  pass de-escalation each fails unit and session tests. Watch: does the note change the next action (a dump / print of the
  actual value) and how often escalation fires per job.

### H-59 - Shell guard blocks read-only `git show HEAD:<path>`
- **First seen:** 2026-10-02. job-2115 (`$head = git show "HEAD:$f"` ... `Compare-Object $h $n | Where-Object SideIndicator -eq
  '=>'`) and job-2116 (`git show HEAD:.../Index.cshtml | Select-String -Pattern "p>|label|select|input"`) were both blocked
  with `[SHELL GUARD] Blocked (writes git object content over working-tree files)`. Neither wrote a file, and the briefs had
  told the agents to compare against HEAD with `git show`. job-2115 fell back to `git diff`.
- **Cause:** `BufferedAgenticHost.IsBlockedHeadlessCommand` (the job-11 rule) blocked any command containing `git show` AND
  `>` / `set-content` / `out-file` / `| sc ` ANYWHERE in its text. The `>` was the quoted `'=>'` in job-2115 and the regex
  `p>` in job-2116. The same substring approach also MISSED `git checkout <sha> -- <path>` (only `checkout --` and
  `checkout head` were matched).
- **Fix (done 2026-10-02):** new `GitWriteGuard` (DevMind.Core/GitWriteGuard.cs). It splits the command into statements
  (`;` newline `&&` `||`) and pipeline stages (`|`) outside quotes. A source is `git show` / `git cat-file`, or a variable
  assigned from one (taint follows reassignment: `$h = Toks $head` stays tainted, `$h = 'x'` clears it). From the source
  on, it blocks only a write whose target resolves inside the job's working dir (following any `cd` in the command): stdout
  redirects (`>`, `>>`, `1>`, `*>`, but not `2>`, `2>&1`, `>$null`, `>NUL`), Out-File / Set-Content / sc / Add-Content /
  ac / Tee-Object / tee (not `Tee-Object -Variable`), `[IO.File]::Write*/Append*`, and `git show --output`. `$env:X`,
  `%X%` and `~` are expanded. A target still holding a variable (`Set-Content $f`) counts as inside. Restore detection
  moved there too: `checkout` with `--`, `.`, rev+path, or a HEAD rev; `restore`; `reset --hard`; `clean`. The old
  restore substrings are kept as a backstop (they also catch `cmd /c "git checkout -- x"`). The `[BLOCKED]` message now
  says reading history to the console, a variable or a pipe is allowed.
- **Tests:** `GitWriteGuardTests`: job-2115 and job-2116 verbatim (cd target changed) are allowed, as are console / variable /
  Select-String / Measure / Compare-Object / `2>&1` / `>$null` / `Tee-Object -Variable`, and redirects or writers aimed at
  `$env:TEMP`, `%TEMP%` or another drive. Blocked: `> a.cs`, `>> a.cs`, `| Set-Content a.cs`, `| sc`, Out-File (both
  argument orders), Add-Content, Tee-Object -FilePath, job-11 verbatim, `Set-Content $f`, absolute in-tree path, `cd src;`
  relative, `git cat-file -p ... > a.cs`, `--output=a.cs`, tainted variable via Set-Content / Out-File / `[IO.File]::WriteAllText`,
  and a redirect inside a ForEach-Object script block. Restores: `checkout HEAD -- a.cs` (still blocked), `checkout <sha> --
  a.cs` (newly blocked), `checkout HEAD a.cs`, `restore --source`, `git -C <dir> restore`, `reset --hard`, `clean -fd`.
- **Limits:** lexing is approximate (no brace or here-string tracking). A write via a tool the guard does not know
  (`Copy-Item` of a file written to %TEMP%, `cmd /c` with a redirect inside quotes) is not caught. That is the same trust
  boundary as before: shell commands are not sandboxed.
- **Status:** fixed (2026-10-02), deployed 1.0.568 (2026-10-04).

### H-60 - list_files with "**/" in the directory part ignores the rest of the pattern
- **First seen:** 2026-10-02, job-2112 (iterations ~6-8, VLink.Warehouses). `list_files "**/ConfigPages/*.cs"` and
  `"tests/**/ConfigPages/*.cs"` both returned the 200-file cap; the agent noted "The glob is being ignored." The literal
  `tests/Verbella.VLink.Warehouses.Service.Tests/ConfigPages/*.cs` returned 15. Measured: the repo has 448 .cs files outside
  noise dirs, 24 of them directly under a ConfigPages folder.
- **Cause:** `FileReadTools.SplitGlob` splits off the last segment as the file pattern and treats the rest as a literal
  directory. `**/ConfigPages` never exists, so it silently fell back to the search root and enumerated `*.cs` recursively:
  every .cs in the tree. `find_in_files` shares the split and had the same bug.
- **Fix (done 2026-10-02):** `FileReadTools.WildcardDirGlob`. When the DIRECTORY part has a wildcard (`**`, `*` or `?`), the
  root is the literal prefix before the first wildcard segment, with no fallback (a missing prefix matches nothing). The
  walk is recursive, with noise dirs pruned as before, and each file's root-relative path must match the remaining segments:
  `**` = zero or more directories, `*`/`?` within one segment, case-insensitive. `recursive` does not apply to such a glob.
  Globs with no wildcard in the directory part (`*.cs`, `src/X/*.cs`) take the old path unchanged.
  Used by `list_files` and `find_in_files`.
- **Tests:** `ListFilesGlobTests` on a temp tree: `**/X/*.cs` (only files directly under any X), `a/**/X/*.cs`, `**/*.cs`
  (all, bin pruned), `a/*/X/*.cs` (one level), backslash + `./` prefix, missing prefix → no matches, `recursive: false`
  ignored, absolute prefix, unchanged `*.cs` / `a/X/*.cs`, and find_in_files with the same glob.
- **Status:** fixed (2026-10-02), deployed 1.0.568 (2026-10-04).

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
  quoted, unless that item is itself "none"); an empty marker with no list under it declares nothing. Deployed.
- **H-01 false positive #2 (2026-09-25, job-1686, after deploy of 90d5365):** a fully finished job (tests 473/75 green, clean build)
  ended stopped_incomplete / self_reported_incomplete because its summary contained the markdown HEADER "## Caller must know" (a
  notes section) - the backup phrase list matches "caller must" anywhere. Suggested fix: apply the phrase list only to lines that are
  not markdown headers, and require the phrase to be followed by an action ("caller must finish/run/fix/...") or drop "caller must" from
  the list now that the INCOMPLETE: convention exists and is used consistently.
  **Fixed** - commit "H-01: headers and 'caller must' no longer trigger self_reported_incomplete" (SelfReportedIncompleteDetector
  v1.3): markdown ATX header lines (# to ######) are skipped by the phrase list (the INCOMPLETE: marker still counts in a header), and
  "caller must" is dropped from the phrase list ("INCOMPLETE: caller must run the migration" still fires via the marker). job-1686's
  verbatim answer is a fixture and now ends `done`. Deployed.
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
  Deployed.
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

### H-61 - With thinking on, the agent reasons at length from memory instead of looking things up first
- **First seen:** 2026-10-03, jobs 2133 and 2137 (VLink.Warehouses LT-34 / LT-42, thinking on, effort medium).
- **Symptom:** job-2137 spent several long think blocks reasoning from memory about questions a lookup or a build would
  settle in seconds: whether `System.Diagnostics.EventLog` is in the shared framework, whether CA1416 fires on a guarded
  `new`, and how built-in DI handles optional constructor parameters. It made no `learn_*` / `library_query` calls in the
  run. It usually landed on the right answer ("EventLogChannel compiles without a package reference, so the type resolves"),
  but at a real cost in tokens and wall time. Job-2133 made one `[LEARN]` search for `TimeZoneInfo.CreateCustomTimeZone` and
  still assigned to read-only `TransitionTime` properties until the driver steered it. LSP use (`find_symbol`,
  `get_diagnostics`) was early and frequent; only documentation lookups were missing.
- **Proposed fix (Paul asked to pin, 2026-10-03):** a "look up before reasoning at length" nudge, complementary to the
  existing auto-think-on-repeated-failure guard. Options: (a) a standing rule in the headless system prompt ("when unsure about
  a framework API, analyzer rule or DI/runtime behaviour, settle it with LSP hover/diagnostics, a quick probe build,
  `learn_search` or `library_query` before reasoning past a couple of paragraphs"); (b) a HarnessNudges guard that fires when
  a think block exceeds N tokens with no tool call between consecutive iterations and names those tools; (c) both.
  Measure against the trace corpus: think tokens per iteration before vs after.
- **Status:** open (pinned).

### H-62 - "Optional item" guard misfires on the word "optional" describing a file, not the work
- **First seen:** 2026-10-04, job-2140 (TokenLedgerTray), iteration 9. The brief said "Settings: optional
  TokenLedgerTray.json next to the exe ... Missing file = defaults" (the FILE is optional at runtime; reading it is
  required work). The HarnessNudges optional-work guard injected "This item was marked optional in the brief. Drop it and
  continue with the required work." The agent correctly kept it (the required `--selftest` prints the resolved paths), so
  no damage this time, but a less careful run would have dropped required functionality.
- **Proposed fix:** only treat a brief item as optional when "optional" qualifies the task itself ("optional:", "(optional)",
  "if time permits", "nice to have" at the start of an item), not when it is an adjective on a noun inside the item; or have
  the nudge ask the agent to confirm instead of instructing it to drop the item.
- **Status:** fixed, pending deploy - commit "H-62: optional-work guard only fires on item-level qualifiers". Rule: a
  brief item (one SplitSentences sentence) is optional only when "optional" is its first word (after a "2." / "b)" /
  "Step N:" prefix, or as "Optional:" after a short label), or it carries "(optional)" / "[optional]", "if quick", "nice
  to have", "skip this if" or "if time permits"; "optional" as an adjective on a noun no longer counts, and the nudge now
  quotes the first 80 chars of the item. Tests (HarnessNudgesTests): `Detector_Job2140_OptionalSettingsFile_IsRequiredWork`,
  `Detector_ItemLevelQualifier_IsOptional`, `Detector_OptionalAsAdjectiveOrNegated_IsRequiredWork`,
  `SpendGuard_MessageQuotesTheItem`, `OptionalWorkMessage_QuotesAtMost80CharsOfTheItem`.

### H-63 - Foreground run_shell that starts a long-lived GUI child blocks the device bridge until the timeout
- **First seen:** 2026-10-04 (driver-side run_shell, not a delegated job). Running Install-TokenLedgerTray.ps1 in the
  foreground with `detach: true`: the script's last step starts the tray exe, which inherits the shell's stdout/stderr pipe,
  so the call never sees end-of-stream and sat until its 300 s timeout; every other device call (even trivial ones) timed
  out at the bridge's 60 s in the meantime, and the timeout killed the script after publish but before the Startup shortcut
  was written. Re-running with `background: true` finished in 43 s.
- **Proposed fix:** when `detach` is set, start children with redirected/null std handles (or close the pipe write ends in
  the parent after spawn) so a detached child cannot hold the call open; or auto-promote a detach run to background.
- **Status:** open.
