# DevMind harness: H-45 (baseline on a broken build) + H-09 (force test verification on build-affecting changes) + close H-10

Repo: C:\Users\pkailas\source\repos\DevMind  (build: dotnet build "C:\Users\pkailas\source\repos\DevMind\DevMind.slnx" -t:Rebuild)
Read docs\harness-watchlist.md entries H-09, H-10 and H-45 first.

## Current code (verify before relying on it)
- DevMind.McpServer\AgentJobManager.cs ~L868: `if (job.VerifyTests && job.RunTestBaseline) job.BaselineTests = await RunTestSuiteAsync(job)`
  — runs inside the job, before the agent starts.
- ~L1007: post-run test verification only when `job.VerifyTests && terminalState == Done && HasFileChanges(result) && job.Build is not { Succeeded: false }`.
- ~L1018-1030: terminal state published only after verification (this is the H-10 fix; AgentJobVerificationRaceTests covers it).
- DevMind.McpServer\TestVerification.cs ~L130-210: builds the test_verification payload (baseline_total, delta,
  baseline_unavailable_reason, note). See TestCountDeltaTests for the existing reason strings.
- Line numbers shifted after b8c47aa / f4ee24d — search for the code, don't trust the numbers.

## H-45 — baseline measured on a broken build
Problem: when the tree does not compile at baseline time (e.g. a continuation of a job that left a test project broken),
`dotnet test` builds and runs only the projects that compile, so baseline_total is a partial count and delta is bogus
(job-1859: reported +747, real +17).
Fix: when the baseline run's build failed, the baseline is unavailable — baseline_total null, delta null,
baseline_unavailable_reason "baseline build failed" (plus a short first-error excerpt in the note), never a partial count.
Gray area — decide and justify: how to detect "build failed" reliably. Candidates: exit code plus compiler-error lines
(`error CS`, `error MSB`, `Build FAILED`) in the output; or running an explicit `dotnet build` before the baseline
`dotnet test` and using its exit code (check whether RunTestSuiteAsync already builds first since H-11). Prefer the one
that cannot mistake a failing TEST for a failing BUILD.

## H-09 — force test verification when a change can affect tests
Problem: a job with verify_tests off touched Directory.Build.props; a version assertion went red unnoticed for two commits.
Fix: after the agent's turn, if verify_tests was OFF but the job changed any build-affecting file — `Directory.Build.props`,
`Directory.Build.targets`, `Directory.Packages.props`, `*.csproj`, `*.props`, `*.targets`, `global.json`,
`appsettings*.json`, `*.slnx`/`*.sln` — run test verification anyway (same gate otherwise: Done, build not failed).
- Mark it in the payload: test_verification gets `forced_reason` (e.g. "build-affecting file changed: Directory.Build.props").
- There is no baseline in this case: baseline_unavailable_reason must say so (e.g. "verify_tests forced after the run; no before-run").
- Add one line to the tail: "[job] test verification: forced (build-affecting change: <file>)".
Gray area: where the changed-file list comes from (whatever HasFileChanges uses — reuse it, don't re-derive).
Gray area: whether the forced run should also count toward the self-report / H-31 green-tests logic. Keep that logic
unchanged unless a test shows it misbehaves; say what you found.

## H-10 — close it out
No code change expected. Confirm the invariant in AgentJobManager (terminal state published only after verification) and that
AgentJobVerificationRaceTests passes. If anything still lets a job publish Done before verification finishes, fix it and add a
test; otherwise update the watchlist entry to "fixed" citing the test.

## Tests (follow TestCountDeltaTests / AgentJobVerificationRaceTests patterns, TestRunnerOverride seam)
- H-45: baseline run whose output is a compile failure → baseline_total null, delta null, reason "baseline build failed".
- H-45: baseline with a genuinely failing TEST (build OK) → still a real baseline count (must not be mistaken for build failure).
- H-09: verify_tests off + changed Directory.Build.props → test_verification present with forced_reason; baseline reason set.
- H-09: verify_tests off + only .cs files changed → no test run (unchanged behaviour).
- H-09: verify_tests on → behaviour unchanged, no forced_reason.

## Done means
- Rebuild: 0 errors, 0 warnings. dotnet test with --blame-hang --blame-hang-timeout 45s --blame-hang-dump-type none: all green; report counts.
- Mutation check: remove the H-45 build-failed branch and confirm its test fails by name; restore.
- Update docs\harness-watchlist.md: H-09, H-10, H-45 → fixed (commit message reference, pending deploy).
- Update docs\cc-prompts\README.md: mark this prompt done with the commit hash.
- Commit. Do NOT deploy.
- Report: files changed, gray-area decisions, test counts.
