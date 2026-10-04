# DevMind harness: H-68 — build verification that fails only on file locks is environmental, not the agent's

Repo: C:\Users\pkailas\source\repos\DevMind  (build: dotnet build "C:\Users\pkailas\source\repos\DevMind\DevMind.slnx" -t:Rebuild)
Read docs\harness-watchlist.md H-68 first. Run only after prompt 06 has committed.

## Problem (job-2152, VLink.PDFSanitizer config app)
The code was clean (the agent proved 0/0 with an alternate OutputPath), but the post-run verification Rebuild hit MSB3026/MSB3061
copy/delete locks held by Paul's running debug session (VLink.PDFSanitizerConfig.exe, devenv) and orphaned
Microsoft.UI.Xaml.Markup.Compiler processes. MSB3026 is a retry warning, so the job ended stopped_incomplete with "51 warnings"
blamed on the agent.

## Current code (verify before relying on it; line numbers are approximate — search for the code)
- DevMind.McpServer\AgentJobManager.cs: class BuildVerification (Command, ExitCode, OutputTail, Succeeded => ExitCode == 0,
  WarningCountVerified, WarningCount, WarningLines). VerifyBuildAsync builds it (a full -t:Rebuild since H-32) and parses warnings.
- AgentJob.IncompleteReasons(): "build_verification_failed" when Build.Succeeded is false; "build_warnings" + count + WarningLines
  when HasVerifiedBuildWarnings.

## Fix
1. Classify lock diagnostics. Lock codes: MSB3021 (unable to copy), MSB3026 (could not copy, retrying), MSB3027 (could not copy,
   exceeded retry count), MSB3061 (unable to delete). Parse them from the FULL build output (not only the 2 KB tail), and extract the
   locking process names from the message text ("The file is locked by: \"Name (pid)\"" — check the real message shape in an MSBuild
   output and in job-2152's sidecar %LOCALAPPDATA%\Temp\devmind\tasks\job-2152.result.json if it exists).
2. BuildVerification gains `LockedBy` (distinct process names with pids) and `LockWarningCount` / `LockErrorCount`.
3. Warnings: lock warnings (MSB3026 and any other lock code emitted as a warning) do NOT count toward WarningCount / build_warnings.
   Report them separately.
4. Errors: if the build failed AND every error is a lock error, the reason is `build_verification_locked` (not
   build_verification_failed), with one extra reason line naming the locking processes and "close them and re-run verification —
   this is the environment, not the code". If there are any non-lock errors, it stays build_verification_failed, and the lock lines
   are listed after the real errors.
5. If the build succeeded but there were lock warnings only, the job is not marked incomplete for them; the payload still shows
   LockedBy so the driver knows.
6. Gray area: whether to retry the verification build once after a short delay when the only failures are locks. Default: NO retry
   (a debug session will still hold the file); say if you find a reason otherwise.
7. Self-report / H-31 logic: a locked verification is NOT a green build — keep jobs from being treated as verified-green. Say how you
   wired that.

## Tests (follow the BuildRunnerOverride / VerificationRebuildTests patterns)
- Build output with only MSB3026 warnings (51 of them) and exit 0 → no build_warnings, LockedBy populated, job not incomplete for it.
- Exit 1 with only MSB3027/MSB3021 errors → build_verification_locked with process names; not build_verification_failed.
- Exit 1 with one CS error + lock errors → build_verification_failed; lock lines reported after the CS error.
- A real warning (CS8618) plus lock warnings → WarningCount counts only the CS8618.
- Process-name parsing against the real MSBuild message shape (use an actual captured line in the test).

## Done means
- Rebuild 0 errors / 0 warnings; dotnet test with --blame-hang --blame-hang-timeout 45s --blame-hang-dump-type none, all green; report counts.
- Mutation check: count lock warnings as normal warnings again and confirm the 51-warning test fails by name; restore.
- Watchlist: H-68 → fixed, pending deploy.
- Update docs\cc-prompts\README.md: mark this prompt done (hash in a follow-up commit as before).
- Commit. Do NOT deploy.
- Report: files changed, the real MSBuild lock-message shape you parsed, gray-area decisions, test counts.
