# Claude Code prompts — harness batch (Oct 4 2026)

Prompts written by Claude (driver) for Paul to run in Claude Code. Run them **in order, one at a time** — each must
commit before the next starts (several touch the same files). Each prompt updates this table when it commits.

| # | File | Scope | Status |
|---|------|-------|--------|
| 01 | (run from chat, not saved) | Depth-cap auto-extend for converging jobs (H-65) | done — b8c47aa + f4ee24d |
| 02 | 02-H45-H09-H10-verification.md | H-45 baseline on broken build, H-09 forced test verification, close H-10 | done — 837abc0 |
| 03 | 03-H03-H02-active-markers.md | Per-job active markers, H-03 server-restart sidecar, close H-02 + tracing (+ H-66) | done — 2f54938 (ran before 02; deploy.ps1 + dist\dm-watch.ps1 now read every _active*.json) |
| 04 | 04-nudge-pack-H46-H47-H50-H51.md | Nudges H-46/H-47/H-50/H-51b, close H-51a | pending |
| 05 | (not written yet) | H-04 Roslyn parse check after .cs patches, H-05 `[two-way fallback]` label only on real divergence | pending |
| 06 | (not written yet) | H-07 shell output: UTF-8 wrapper encoding, inline stdout+stderr | pending |

Run next: 04.

Nothing in this batch is deployed. Deploy once after the batch (run-deploy.ps1), then live-check each item.
