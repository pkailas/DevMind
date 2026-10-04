# DevMind harness: nudge pack — H-46, H-47, H-50, H-51b — and close H-51a

Repo: C:\Users\pkailas\source\repos\DevMind  (build: dotnet build "C:\Users\pkailas\source\repos\DevMind\DevMind.slnx" -t:Rebuild)
Read docs\harness-watchlist.md H-46, H-47, H-50, H-51 first. Run only after prompt 03 has committed.

## Current code (verify before relying on it; line numbers are approximate — search for the code)
- DevMind.Core\HarnessNudges.cs: class HarnessNudges, ObserveIteration(agentText, toolOutput) → nudges (H-25 repeated
  compile error, H-26 optional work); one-shot per key via _errorFired / _optionalFired; ResetCompileErrors per turn.
- DevMind.Core\HeadlessAgent.cs AppendHarnessNudges: feeds HarnessNudgeEvidence.AgentText(assistantResponse,
  iter.ToolCalls) and HarnessNudgeEvidence.ToolOutput(iter.Result); folds each nudge via HarnessNudgeEvidence.Fold
  ("[HARNESS GUARD] ..."), journals via _host.RecordNudge, transcript "[GUARD] nudge injected ...".
- HeadlessAgent.cs ObserveTestRuns / RepeatedTestFailureGuard (H-58) already handles the same FAILING tests repeating — don't duplicate it.
- The new nudges need per-tool-call facts (tool name, arguments, success/failure, target path, test filter). ObserveIteration
  today only gets flattened text. Gray area: extend the evidence passed in (preferred: a small structured record per tool call
  built from iter.ToolCalls / iter.Result) rather than re-parsing flattened text. Keep the existing two nudges' behaviour identical.

## Nudges to add (all one-shot per job turn per key, same framing/journal/transcript as the existing ones, constants for thresholds)
1. H-46 build-output-as-evidence: while the job's latest test or build run is failing, a read_file / grep_file / find_in_files /
   list_files whose target is under a `bin\` or `obj\` segment, or is `*.deps.json` / `*.dll` / `*.pdb` / `*.runtimeconfig.json`
   → inject once: "Build output is never the evidence. Print the failing assertion and the actual value, and fix what that shows."
   Also count shell commands that read those paths (Get-Content / Select-String / Get-ChildItem on them) — gray area how far to go;
   path-segment matching on the command text is enough.
2. H-47 fetch-for-framework-source: 2 consecutive failed web_fetch / learn_fetch fetches (404, not found, empty) → inject once:
   "Stop fetching. Write the code (or your own class implementing the interface) and build — the compiler is the reference."
   Reset the counter on any successful fetch.
3. H-50 paging shell output: 3+ consecutive shell calls whose commands are the same after removing numeric arguments of
   -Skip / -First / -Last / -Head / -Tail / -Index / head -n / tail -n (and the numbers themselves) → inject once: "Use read_file with
   start_line/end_line, or write the output to a file once and read that."
4. H-51b same-run-same-result loop. Evidence (job-1866, sidecar %LOCALAPPDATA%\Temp\devmind\tasks\job-1866.result.json): 11
   `dotnet test` runs before an override steer and 10 commands after; NONE were byte-identical (redirect targets isoA.txt … isoK.txt;
   the later recount commands differ by one character). So the key must be semantic, not the raw string:
   - For dotnet test: key = test project/solution + --filter value (normalized), ignoring redirects (`> file`, `*> file`, `2>&1`,
     `| Out-File`, `| Tee-Object`) and env-var prefixes; outcome = passed/failed counts (TestRunOutcome.Parse already exists).
     3 runs in a row with the same key and the same outcome → inject once: "Same tests, same result, three times. State in one
     sentence what question you are still answering and the one different command that would answer it — or move on."
   - For other shell commands: normalize by stripping redirect targets, temp-file names, and digits; 3 in a row with the same
     normalized command AND identical (normalized) output → same nudge.
   Must not fire when H-58 already fires for the same failing tests (don't double-nudge).

## H-51a — close, no code expected
Investigation done: job-1866's journal shows the override steer consumed (action 72, success true) and obeyed (later commands
dropped ASPNETCORE_ENVIRONMENT=Staging); the agent then started a second loop. Not a steer-consumption defect. Update the
watchlist with this evidence.

## Tests (follow HarnessNudgesTests patterns)
- Each nudge: fires at its threshold, exactly once, with the right text; does NOT fire one below the threshold; resets where specified.
- H-46 fires only while a build/test is failing; reading bin\ on a green job does not fire.
- H-51b: replay job-1866's shape — the same --filter run with different redirect files (isoA.txt, isoB.txt, isoC.txt) and the
  same passing result → fires on the 3rd; a different filter or a changed outcome resets it.
- H-51b and H-58 do not both fire for the same repeated failing tests.
- Existing nudge tests unchanged and green.

## Done means
- Rebuild 0 errors / 0 warnings; dotnet test with --blame-hang --blame-hang-timeout 45s --blame-hang-dump-type none, all green; report counts.
- Mutation check: make the H-51b key include the redirect target again and confirm the job-1866 replay test fails by name; restore.
- Watchlist: H-46, H-47, H-50, H-51 → fixed (H-51a closed with evidence), pending deploy.
- Update docs\cc-prompts\README.md: mark this prompt done with the commit hash.
- Commit. Do NOT deploy.
- Report: files changed, gray-area decisions, test counts.
