# Claude Code prompts — harness batch (Oct 4 2026)

Prompts written by Claude (driver) for Paul to run in Claude Code. Run them **in order, one at a time** — each must
commit before the next starts (several touch the same files). Each prompt updates this table when it commits.

| # | File | Scope | Status |
|---|------|-------|--------|
| 01 | (run from chat, not saved) | Depth-cap auto-extend for converging jobs (H-65) | done — b8c47aa + f4ee24d |
| 02 | 02-H45-H09-H10-verification.md | H-45 baseline on broken build, H-09 forced test verification, close H-10 | done — 837abc0 |
| 03 | 03-H03-H02-active-markers.md | Per-job active markers, H-03 server-restart sidecar, close H-02 + tracing (+ H-66) | done — 2f54938 (ran before 02; deploy.ps1 + dist\dm-watch.ps1 now read every _active*.json) |
| 04 | 04-nudge-pack-H46-H47-H50-H51.md | Nudges H-46/H-47/H-50/H-51b, close H-51a | done — 5a75777 |
| 05 | 05-H04-H05-H67-patch-safety.md | H-04 Roslyn parse check on .cs edits, H-67 fuzzy patches on structured files, H-05 `[two-way fallback]` label only on a real fallback | done — 4938f74 (found H-69: the three-way merge never ran — DiffPlex null chunker; decided - see 05b) |
| 06 | 06-H07-shell-output.md | H-07 shell output: UTF-8 wrapper encoding, inline stdout+stderr | done — f753fda |
| 05b | 05b-H69-merge-conflicts.md | H-69 real three-way merge (LineChunker); headless conflict = refuse one write + re-read instruction; MCP write_file/create_file syntax gate; SatelliteResourceLanguages=en | done — 2a0673a |
| 07 | 07-H68-build-locks.md | H-68 build verification failing on user file locks → classify as environmental (build_verification_locked) | pending |

Run next: 07.

Nothing in this batch is deployed. Deploy once after the batch (run-deploy.ps1), then live-check each item.
