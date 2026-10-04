# DevMind harness: 05b — H-69 (three-way merge never merged) + MCP write-path syntax gate + satellite resource trim

Repo: C:\Users\pkailas\source\repos\DevMind  (build: dotnet build "C:\Users\pkailas\source\repos\DevMind\DevMind.slnx" -t:Rebuild)
Read docs\harness-watchlist.md H-69, H-04, H-05 first. Run only after prompt 06 has committed (both touch DevMindTools.cs).

## Decisions already made by Paul (implement these)
1. Fix the merge: DevMind.Core\ThreeWayMergeCheck.cs calls DiffPlex CreateMerge with chunker: null, which DiffPlex 1.9.0 rejects
   (ArgumentNullException) — verified against the DLL in 4938f74's investigation; LineChunker works. Pass the line chunker so clean,
   non-overlapping three-way merges actually merge.
2. Conflicts in a HEADLESS / delegated job must not enter the blocking pending-conflict state (nobody can run /resolve there).
   Instead: refuse THAT write only, file unchanged, and return a tool error to the model that shows each conflict block (base / its
   proposal / current disk text, trimmed to a few lines each, with line numbers) and says: "The file changed since you read it.
   Re-read it and redo your edit against the current content." No state persists — the next write is evaluated fresh.
   Record it in the journal and transcript (stable event name, e.g. merge_conflict_refused).
3. The interactive TUI keeps today's behaviour: conflict → pending-conflict state → /resolve.
4. MCP write_file / create_file (DevMindTools.cs, their own write path — per 4938f74's report) get the same H-04 C# syntax gate
   (CSharpSyntaxGate) as the hosts' save/append and PatchEngine.ApplyPatch. Same refusal shape as the host path.
5. Add <SatelliteResourceLanguages>en</SatelliteResourceLanguages> to Directory.Build.props (DevMind is English-only; Roslyn's
   translated resources added ~17.1 MB per exe). Re-run the same scratch publish 4938f74 used (run-deploy.ps1 settings, NOT into
   dist\) and report both exe sizes again (4938f74: TUI 122,307,417 bytes, McpServer 119,274,952 bytes).

## Claims to verify (from 4938f74's report — confirm in code before relying on them)
- Where the pending-conflict state is set (PendingConflictState; BufferedAgenticHost save/append/ApplyResolvedPatchAsync and the
  TUI host's own write code) and how "every later write is blocked until /resolve" is enforced.
- How a host knows it is headless vs interactive (find the existing distinction — e.g. the BufferedAgenticHost headless default in
  ConfirmContinueCoreAsync; don't invent a new flag if one exists).
- The test that pins today's "[merge engine failed: proposed text accepted]" behaviour — it must now be replaced by real-merge tests.

## Gray areas — decide, test, and say what you chose
- Base-cache freshness: with merging now real, a stale base could produce spurious conflicts. Check what the base is (the
  last-read cache entry?) and whether the agent's own successful writes update it; if they don't, every second edit to a file
  would conflict. Fix that if so.
- Whether the "[merge engine failed]" label path still has any reachable case after the chunker fix; keep it for genuine exceptions.

## Tests
- Clean three-way merge (disk changed in a different region than the agent's edit) → merged; both changes present; no label.
- Overlapping change, headless → refused, file unchanged, error contains the conflict blocks and the re-read instruction; the NEXT
  write after a re-read succeeds (no lingering blocked state).
- Overlapping change, TUI host → pending-conflict state as before.
- Agent writes the same file twice in a row with no outside change → no conflict (base-cache freshness).
- MCP write_file / create_file on .cs: a broken file is refused, a valid one written; non-.cs untouched by the gate.
- Existing merge/patch/WriteEcho tests green (update only the one that pinned the broken behaviour).

## Done means
- Rebuild 0 errors / 0 warnings; dotnet test with --blame-hang --blame-hang-timeout 45s --blame-hang-dump-type none, all green; report counts.
- Mutation check: put chunker: null back and confirm the clean-merge test fails by name; restore.
- Watchlist: H-69 fixed (pending deploy); note the MCP write-path gating under H-04.
- Update docs\cc-prompts\README.md: mark this prompt done (hash in a follow-up commit as before).
- Commit. Do NOT deploy.
- Report: files changed, gray-area decisions, test counts, exe sizes before/after the satellite trim.
