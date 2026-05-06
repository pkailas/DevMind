# RAG Frontmatter Backlog

**Purpose**: Tracks repo markdown documents that need YAML frontmatter + per-section freshness annotations before RAG ingestion. Each entry is a proposal only — do not apply without user review.

**Pattern reference**: See `docs/Stage11-Tech-Reference.md` for the frontmatter schema and annotation format.

**Created**: 2026-05-06  
**Do not include**: Google Drive documents (handled separately).

---

## Candidates

### `DevMind.md`

- **Path**: `DevMind.md`
- **Description**: Primary project context file — architecture overview, feature roadmap, directive syntax, settings reference, coding standards. The document most likely to be ingested and queried. Most important candidate.
- **Last git commit**: 2026-05-05 (`Stage 9 follow-up: post-v8.0 doc + version metadata sync`)
- **Proposed doc_type**: `tech_reference`
- **Proposed verified_date**: `"2026-05-05"` (last commit date; actual content spans many sessions — use git log per-section for finer resolution if needed)
- **Proposed revalidate_after**: `"2026-11-05"`
- **Version-sensitive sections needing per-section annotations**:
  - LLM Directives (FILE/PATCH/SHELL/READ syntax) — stable but tied to DevMind version
  - Settings table — changes with every release; needs `DevMind version: x.y.z` annotation
  - Agentic Pipeline section — tied to LoopDriver/LoopIterationResult design; version-sensitive
  - Context Budget / Context Architecture — v6.0.132+ features; date these explicitly
  - TrainingLogger — gated behind setting; note when feature was added
- **Notes**: The file is also used as a DevMind agent context file (the `AGENTS.md` discovery chain). Frontmatter must not break agent context loading — verify that DevMind's `DevMind.md` loader ignores YAML frontmatter blocks before adding.

---

### `DEVMIND_DEV.md`

- **Path**: `DEVMIND_DEV.md`
- **Description**: Developer reference — build instructions, VS extension setup, project structure, dev environment notes.
- **Last git commit**: 2026-03-10 (`v5.0 - ResponseDispatcher refactor`)
- **Proposed doc_type**: `runbook`
- **Proposed verified_date**: `"2026-03-10"` (last commit; likely stale relative to current v8.0 codebase)
- **Proposed revalidate_after**: `"2026-09-10"`
- **Version-sensitive sections needing per-section annotations**:
  - Build / publish instructions — may reference pre-Stage 10 project structure
  - VS SDK version requirements
- **Notes**: High staleness risk. Last committed at v5.0; codebase is now v8.0+ with McpServer, DevMind.Core, DevMind.Cli added. Recommend a content audit pass before frontmatter retrofit, or mark entire doc `verified_date: unknown` until audited.

---

### `DevMind_v5_ResponseDispatcher_DesignSpec.md`

- **Path**: `DevMind_v5_ResponseDispatcher_DesignSpec.md`
- **Description**: Design spec for the v5.0 ResponseDispatcher — post-hoc classification, ResponseParser, block types. Historical artifact documenting a completed refactor.
- **Last git commit**: 2026-03-10 (`v5.0 - ResponseDispatcher refactor`)
- **Proposed doc_type**: `design_doc`
- **Proposed verified_date**: `"2026-03-10"`
- **Proposed revalidate_after**: `"never"` — this is a historical design doc for a shipped feature; it should not be updated to reflect later changes. RAG ingestion should tag it as `status: historical`.
- **Version-sensitive sections**: All of them — but as a historical doc the right treatment is a single doc-level annotation: `status: historical, reflects DevMind v5.0 as of 2026-03-10`. Do not add per-section annotations that imply ongoing maintenance.
- **Notes**: Consider whether this should be ingested into RAG at all. A historical design doc answers "why was it built this way" questions, not "how does it work today" questions. Useful for architectural context; confusing if RAG surfaces it as current behavior.

---

### `DevMind_UserGuide.md`

- **Path**: `DevMind_UserGuide.md`
- **Description**: End-user guide — how to use DevMind, UI walkthrough, key features.
- **Last git commit**: 2026-03-15 (`v5.2 - Context budget, FileContentCache, batch input`)
- **Proposed doc_type**: `runbook`
- **Proposed verified_date**: `"2026-03-15"` (last commit; content likely stale vs current v8.0 UI and features)
- **Proposed revalidate_after**: `"2026-09-15"`
- **Version-sensitive sections needing per-section annotations**:
  - All UI descriptions — toolbar, buttons, key bindings
  - Directive syntax examples — must match current system prompt
  - Settings descriptions — settings table has changed significantly since v5.2
- **Notes**: High staleness risk (same reasoning as DEVMIND_DEV.md). The diff preview, profiles, and McpServer features added after v5.2 are absent. Recommend content audit before RAG ingestion.

---

### `CodeReview.md`

- **Path**: `CodeReview.md`
- **Description**: Unknown — no git commits tracked (empty git log result). File exists on disk but has no commit history in the current git log view.
- **Last git commit**: unknown (not in tracked history)
- **Proposed doc_type**: unknown — needs inspection
- **Proposed verified_date**: `unknown`
- **Proposed revalidate_after**: unknown until content is reviewed
- **Notes**: Inspect the file before proposing frontmatter. If it's a one-off code review artifact (not a living document), it may not belong in RAG at all.

---

### `DevMind_Builder.md`

- **Path**: `DevMind_Builder.md`
- **Description**: Unknown — no git commits tracked. File exists on disk but has no commit history in the current git log view.
- **Last git commit**: unknown
- **Proposed doc_type**: unknown — needs inspection
- **Proposed verified_date**: `unknown`
- **Proposed revalidate_after**: unknown
- **Notes**: Same as CodeReview.md — inspect before proposing schema.

---

### `DevMind_CodeReview.md`

- **Path**: `DevMind_CodeReview.md`
- **Description**: Unknown — no git commits tracked.
- **Last git commit**: unknown
- **Proposed doc_type**: unknown — needs inspection
- **Proposed verified_date**: `unknown`
- **Proposed revalidate_after**: unknown
- **Notes**: Same as above.

---

### `DevMind_Template.md`

- **Path**: `DevMind_Template.md`
- **Description**: Unknown — no git commits tracked.
- **Last git commit**: unknown
- **Proposed doc_type**: unknown — needs inspection
- **Proposed verified_date**: `unknown`
- **Proposed revalidate_after**: unknown
- **Notes**: Name suggests it may be a prompt template or agent profile template rather than reference documentation. If so, `doc_type: other` and likely low RAG value — inspect before deciding.

---

## Explicitly Excluded

- `CLAUDE.md` — Claude Code project instructions file. Not a reference document; loaded directly by Claude Code, not by RAG. Frontmatter would conflict with Claude Code's parser. Do not retrofit.
- Google Drive documents — handled separately per user instructions.
- `docs/Stage11-Tech-Reference.md` — **already retrofitted** (this commit).

---

## Recommended Retrofit Order

1. `DevMind.md` — highest RAG value, most frequently queried, most important to get right. Audit for content staleness first.
2. `DevMind_v5_ResponseDispatcher_DesignSpec.md` — add historical tag; low maintenance burden.
3. `DevMind_UserGuide.md` and `DEVMIND_DEV.md` — after content audits confirm what's stale.
4. `CodeReview.md`, `DevMind_Builder.md`, `DevMind_CodeReview.md`, `DevMind_Template.md` — inspect contents, then decide whether to retrofit or exclude from RAG entirely.
