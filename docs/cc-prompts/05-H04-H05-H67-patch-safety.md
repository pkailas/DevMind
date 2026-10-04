# DevMind harness: H-04 (reject .cs edits that break the syntax) + H-67 (fuzzy patches on structured files) + H-05 (`[two-way fallback]` only on a real fallback)

Repo: C:\Users\pkailas\source\repos\DevMind  (build: dotnet build "C:\Users\pkailas\source\repos\DevMind\DevMind.slnx" -t:Rebuild)
Read docs\harness-watchlist.md H-04, H-05, H-67 (and H-34 for how patches are rebased and verified today) first.
Run only after prompt 04 has committed.

## Current code (verify before relying on it; line numbers are approximate — search for the code)
- DevMind.Core\ThreeWayMergeCheck.cs CheckAndMerge(baseText, proposedText, currentText):
  `usedFallback = string.IsNullOrEmpty(baseText) || normBase == normCurrent` → returns MergedText = proposed, UsedFallback = true.
  The DiffPlex catch block also returns UsedFallback = true.
  So UsedFallback is true in three different situations: (a) no base cache entry, (b) base == current — the NORMAL, clean case
  (nobody else changed the file), (c) DiffPlex threw.
- DevMind.Core\BufferedAgenticHost.cs: SaveFileAsync, AppendFileAsync and ApplyResolvedPatchAsync print " [two-way fallback]"
  whenever merge.UsedFallback, and call Trace.Event("merge_fallback", ...) with a message claiming "no base cache entry".
  PendingConflictState.UsedFallback is copied from it.
- DevMind.Core\PatchEngine.cs (static class) — patch resolution incl. fuzzy matching (the transcript shows "[Fuzzy ?]" with a
  percentage); H-34 made patches rebase onto the current file and verify after write.
- No project references Microsoft.CodeAnalysis today; DevMind talks to Roslyn LS out of process for LSP. LSP diagnostics have been
  seen stale after patches (watchlist), so do NOT use LSP for these checks.

## H-05 — label only a real fallback (Paul's decision: label only genuine divergence/fallback)
Replace the single bool with a reason, e.g. `MergeMode { ThreeWay, CleanNoDivergence, NoBase, DiffEngineFailed }` (name it as you
see fit; keep UsedFallback as a computed property if that keeps callers/tests simple, or migrate them — your call, say which).
- base present and == current → clean, NO label, no "merge_fallback" trace event.
- no base cache entry → label " [no base: overwrite check only]" and trace "merge_fallback" with the existing wording.
- DiffPlex threw → label " [merge engine failed: proposed text accepted]" and trace with the exception type.
Update every caller and the transcript vocabulary (TranscriptVocabularyCoverageTests enforces it). Keep trace event NAMES stable constants
(an old watchlist note found message text being used as the event name).

## H-04 — reject a .cs edit that introduces syntax errors
Problem (job-1652): an insert meant to go between two test methods landed inside one, leaving it unterminated; ~6 iterations to untangle.
Fix: for patch_file / write_file / create_file / append_file on a `.cs` file, after computing the final text and BEFORE writing:
- Parse the CURRENT on-disk text and the NEW text with `Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText` (parse only — no
  compilation, no references; LanguageVersion.Preview is fine).
- If the new text has syntax errors the current text does not (compare the multiset of (id, message) for Severity == Error — a file
  can legitimately be mid-edit already), reject the write: leave the file untouched, return a tool error naming the first NEW
  diagnostic (id, message, line:column) with the 3 lines around it from the proposed text, and the instruction
  "the edit broke the file's structure; re-read the region and patch again".
- Otherwise write as today. create_file compares against zero errors.
Package: add Microsoft.CodeAnalysis.CSharp to DevMind.Core with `dotnet add package` (let it pick the version; never hand-write one).
Confirm a `dotnet publish` of DevMind.McpServer and DevMind.TUI with the same settings run-deploy.ps1 uses still succeeds (do NOT
deploy) and report the published exe size before/after.
Gray area: the check lives in the shared host, so it applies to the TUI too — confirm that and that it's acceptable (a rejected edit is a
tool error the model sees). .cshtml/.razor are out of scope (different parser) — note it in the watchlist.

## H-67 — fuzzy patches on near-identical lines
Evidence: job-2143 — a `[Fuzzy ?]` 99% patch of a `[LoggerMessage]` attribute wrote `)]]` at two sites (CS1519). job-2146 — two patches
to MainWindow.xaml.cs left the newest `// vX.Y.Z` header line duplicated. Both on near-identical neighbouring lines.
Fix:
1. For `.cs`, the H-04 parse check above already rejects the `)]]` case — add a test replaying it.
2. On ANY fuzzy (non-exact) apply, before writing: if a line of the REPLACE text that is not in the FIND text now appears more often in
   the result than (count in the original file + count added by REPLACE), or the result contains the FIND's first/last line duplicated
   adjacently where the original did not, reject with an error that shows the fuzzy candidate region and asks for an exact FIND.
   Gray area: tune this so a normal fuzzy apply (whitespace drift) still succeeds — the tests below decide.
3. No fuzzy auto-apply for files with no parser check: `.xaml`, `.axaml`, `.csproj`, `.props`, `.targets`, `.slnx`, `.sln`, `.json`,
   `.xml`, `.resx`, `.config`. For those, a non-exact match returns an error showing the best candidate (with its similarity) and asks for
   an exact FIND — it does not write.
4. When an exact match exists, it always wins over any fuzzy candidate (watchlist Sep 2: an exact 100% match was rejected because a 99%
   runner-up sat one line away). Verify whether that is still true after H-34; fix it if so, and say what you found.

## Tests
- ThreeWayMergeCheck: the three modes come back correctly; base == current is clean with no label.
- Host output: a clean patch prints no label; a no-base save prints the new label; a forced DiffPlex failure prints its label.
- H-04: replay job-1652's shape — insert lands inside a method body leaving an unbalanced brace → rejected, file unchanged, error names
  the new diagnostic and line. A correct insert between methods → written. A file that already has a syntax error, patched elsewhere
  without adding errors → written. Same for write_file and create_file.
- H-67: replay job-2143 (stacked `[LoggerMessage(...)]` attributes, fuzzy patch producing `)]]`) → rejected. Replay job-2146 (stacked
  `// vX.Y.Z` comment lines, fuzzy patch duplicating the newest) → rejected. A fuzzy apply that only differs in indentation → written.
  A non-exact match on a .xaml/.csproj/.json file → error with the candidate, file unchanged. Exact match next to a 99% runner-up →
  the exact one is applied.
- Non-.cs files are never parsed. Existing patch/merge tests green.

## Done means
- Rebuild 0 errors / 0 warnings; dotnet test with --blame-hang --blame-hang-timeout 45s --blame-hang-dump-type none, all green; report counts.
- Mutation check: make the H-04 comparison always pass and confirm the job-1652 replay test fails by name; restore.
- Watchlist: H-04, H-05, H-67 → fixed, pending deploy.
- Update docs\cc-prompts\README.md: mark this prompt done (hash in a follow-up commit as before).
- Commit. Do NOT deploy.
- Report: files changed, gray-area decisions, test counts, published exe size before/after.
