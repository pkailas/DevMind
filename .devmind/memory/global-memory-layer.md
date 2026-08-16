Global (machine-level) memory layer, approved design A-G, implemented 2026-08.

Architecture:
- %APPDATA%\devmind\memory\ (per-operating-user; NOT %ProgramData%) via DevMindPaths.cs (DevMind.Core) — single source of truth; TuiConfig.ConfigPath now derives from DevMindPaths.GlobalDir so paths can't drift.
- MemoryManager (v8): dir-parameterized private reads (LoadTopicIn/ListTopicsIn/SearchTopicsIn) shared by repo (.devmind/memory) + global layer. New public surface: ListGlobalTopics, LoadGlobalTopic, RecallTopic (returns RecallResult{Content, CollisionNote}), ListTopicsForRecall (repo first, then "global:<slug>"), SearchTopicsAllLayers, LoadGlobalStandingContext, BuildRepoStandingContext.
- LoadStandingContext: two-part budget. Global layer trims against its OWN cap (const GlobalStandingContextMaxBytes = 8*1024, documented) before a byte can reduce the repo cap. Global-first then repo, labelled sections ("## GLOBAL (machine-level) conventions" / "## REPO conventions" — repo header only when global present). When NO global standing context exists, output is byte-identical to legacy (StandingContextTests rely on this; exact "[omitted — standing-context budget exhausted..." string preserved).
- recall_memory: repo-default; "global:<slug>" prefix = explicit global; collision (same slug both layers) => requested layer's content + visible CollisionNote, never first-match.
- save_memory stays repo-only (global is read-only at the tool surface — deliberate capability decision).
- Tool surfaces patched in 4 places: DevMindTools.cs (MCP), BufferedAgenticHost.cs (Core, in-process CLI path), TuiAgenticHost.cs (TUI), plus HeadlessAgent.BuildSystemPrompt automatically gets global standing context via LoadStandingContext. McpServices got an internal (wd, roots, memoryManager) ctor as the test seam (InternalsVisibleTo already existed).
- Missing/unreadable global dir never throws — every global read degrades to empty/null.

Tests: DevMind.Core.Tests\GlobalMemoryLayerTests.cs (13) + DevMind.McpServer.Tests\GlobalMemoryToolTests.cs (8), all via the (repo, globalDir) ctor seam — hermetic, never touch real %APPDATA%.

Pitfalls hit during implementation:
- run_tests with its own incremental build can run a STALE test DLL (reported identical failure with line numbers not in current source). Force run_build first, or use --no-build after a fresh run_build.
- DevMind.Core is <Nullable>disable</Nullable> — don't use ? annotations in Core source (CS8632 warnings); test projects are nullable-enabled.
- grep_file rejects patterns with regex metacharacters (| only); "using System.Text;" matched nothing because of the dots.
- patch_file batch 'edits' can silently mangle one edit when a find's context overlaps another — verify the post-patch view for each hunk.