// File: GlobalMemoryLayerTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the machine-level (global) memory layer (%APPDATA%\devmind\memory)
// composing with the per-repo layer. Uses the MemoryManager(repoRoot, globalDir)
// test seam so the real %APPDATA% is never touched — these tests are hermetic
// and exercise the "no global dir" state exactly as it exists on this machine.

using Xunit;

namespace DevMind.Core.Tests
{
    public class GlobalMemoryLayerTests : IDisposable
    {
        private readonly string _repo;
        private readonly string _global;

        public GlobalMemoryLayerTests()
        {
            string baseDir = Path.Combine(Path.GetTempPath(), $"devmind_global_{Guid.NewGuid():N}");
            _repo = Path.Combine(baseDir, "repo");
            _global = Path.Combine(baseDir, "global");
            Directory.CreateDirectory(_repo);
            // NOTE: _global is deliberately NOT created — most tests exercise the
            // "no global dir" state. Individual tests create it as needed.
        }

        public void Dispose()
        {
            try
            {
                string baseDir = Path.GetDirectoryName(_repo)!;
                if (Directory.Exists(baseDir))
                    Directory.Delete(baseDir, recursive: true);
            }
            catch { }
        }

        private void CreateGlobal(string topic, string content)
        {
            Directory.CreateDirectory(_global);
            File.WriteAllText(Path.Combine(_global, topic + ".md"), content);
        }

        // ── LoadStandingContext composition ──────────────────────────────────

        [Fact]
        public void BothLayersPresent_AreLabelledGlobalFirstRepoLast()
        {
            var memory = new MemoryManager(_repo, _global);
            memory.SaveTopic("repo-conventions", "repo rule: warnings are errors", "repo");
            CreateGlobal("standing-tooling", "global rule: check the docs first");

            string standing = memory.LoadStandingContext();

            Assert.NotNull(standing);
            Assert.Contains("global rule: check the docs first", standing);
            Assert.Contains("repo rule: warnings are errors", standing);
            Assert.Contains("GLOBAL (machine-level)", standing);
            Assert.Contains("REPO conventions", standing);
            // Global section first, repo last (repo = more specific = wins on conflict).
            Assert.True(
                standing.IndexOf("global rule", StringComparison.Ordinal)
                    < standing.IndexOf("repo rule", StringComparison.Ordinal),
                "global standing context must precede repo standing context");
        }

        [Fact]
        public void GlobalOnlyWithNoRepoTopics_Loads()
        {
            // The Parsely / VLink case: the repo has NO .devmind/memory at all,
            // but the machine-level layer must still reach the agent.
            var memory = new MemoryManager(_repo, _global);
            CreateGlobal("standing-tooling", "global rule: check the docs first");

            string standing = memory.LoadStandingContext();

            Assert.NotNull(standing);
            Assert.Contains("global rule: check the docs first", standing);
            Assert.Contains("GLOBAL (machine-level)", standing);
            Assert.DoesNotContain("REPO conventions", standing);
        }

        [Fact]
        public void NoGlobalDir_ReturnsLegacyRepoOnlyShape()
        {
            // _global was never created: output must be byte-for-byte the legacy
            // repo-only result — no GLOBAL header, plain per-topic blocks.
            var memory = new MemoryManager(_repo, _global);
            memory.SaveTopic("repo-conventions", "repo rule: warnings are errors", "repo");

            string standing = memory.LoadStandingContext();

            Assert.Equal("\n## [repo-conventions]\nrepo rule: warnings are errors\n", standing);
            Assert.DoesNotContain("GLOBAL", standing);
        }

        [Fact]
        public void OversizedGlobal_TrimmedOnlyGlobal_RepoTopicRemainsComplete()
        {
            // The two-part budget invariant: the global layer over its 8 KB cap
            // trims itself (with its own marker) and the repo topic comes through
            // COMPLETE — assert the whole repo content, not merely that something
            // was omitted.
            var memory = new MemoryManager(_repo, _global);
            string repoContent = "repo rule A\nrepo rule B\nrepo rule C (complete)";
            memory.SaveTopic("repo-conventions", repoContent, "repo");
            CreateGlobal("standing-tooling", new string('g', 10_000));

            string standing = memory.LoadStandingContext();

            Assert.NotNull(standing);
            // Repo topic present in FULL, un-trimmed, un-omitted.
            Assert.Contains(repoContent, standing);
            Assert.DoesNotContain("[omitted — standing-context budget exhausted", standing);
            // The GLOBAL layer's own omission marker (its 8 KB cap, not the repo's).
            Assert.Contains("global standing-context budget exhausted", standing);
            Assert.Contains("recall_memory \"global:standing-tooling\"", standing);
        }

        [Fact]
        public void SlugCollision_BothVisibleNeitherDropped()
        {
            // Same standing slug in both layers: both blocks must appear; the
            // global one is tagged so the collision is visible.
            var memory = new MemoryManager(_repo, _global);
            memory.SaveTopic("standing-conventions", "repo says: build with dotnet build", "repo");
            CreateGlobal("standing-conventions", "global says: check the docs first");

            string standing = memory.LoadStandingContext();

            Assert.NotNull(standing);
            Assert.Contains("repo says: build with dotnet build", standing);
            Assert.Contains("global says: check the docs first", standing);
            Assert.Contains("also present in repo", standing);
            // No omission markers at all — nothing was dropped.
            Assert.DoesNotContain("[omitted", standing);
        }

        // ── RecallTopic: explicit scope, no first-match ──────────────────────

        [Fact]
        public void RecallTopic_RepoDefault_ReturnsRepoWithoutNote()
        {
            var memory = new MemoryManager(_repo, _global);
            memory.SaveTopic("auth-system", "repo auth notes", "repo");
            CreateGlobal("other-topic", "global other");

            var result = memory.RecallTopic("auth-system");

            Assert.NotNull(result);
            Assert.Equal("repo auth notes", result.Content);
            Assert.Null(result.CollisionNote);
        }

        [Fact]
        public void RecallTopic_RepoFallback_ReturnsGlobalWhenNoRepoTopic()
        {
            // The repo has no such slug: recall falls back to the machine-level layer.
            var memory = new MemoryManager(_repo, _global);
            memory.SaveTopic("unrelated", "repo unrelated", "repo");
            CreateGlobal("build-quirks", "global build notes");

            var result = memory.RecallTopic("build-quirks");

            Assert.NotNull(result);
            Assert.Equal("global build notes", result.Content);
            Assert.Null(result.CollisionNote);
        }

        [Fact]
        public void RecallTopic_Collision_ReturnsRequestedLayerWithVisibleNote()
        {
            var memory = new MemoryManager(_repo, _global);
            memory.SaveTopic("standing-conventions", "REPO VERSION", "repo");
            CreateGlobal("standing-conventions", "GLOBAL VERSION");

            // Repo-default: the repo content wins, and the collision is surfaced —
            // never silently first-matched to either layer.
            var repo = memory.RecallTopic("standing-conventions");
            Assert.NotNull(repo);
            Assert.Equal("REPO VERSION", repo.Content);
            Assert.NotNull(repo.CollisionNote);
            Assert.Contains("global:standing-conventions", repo.CollisionNote);

            // Explicit global: the other layer, with the inverse note.
            var global = memory.RecallTopic("global:standing-conventions");
            Assert.NotNull(global);
            Assert.Equal("GLOBAL VERSION", global.Content);
            Assert.NotNull(global.CollisionNote);
            Assert.Contains("standing-conventions", global.CollisionNote);
        }

        [Fact]
        public void RecallTopic_InNeitherLayer_ReturnsNull()
        {
            var memory = new MemoryManager(_repo, _global);
            CreateGlobal("only-global", "x");

            Assert.Null(memory.RecallTopic("does-not-exist"));
        }

        // ── Merged list / search: no global dir = byte-identical to legacy ───

        [Fact]
        public void NoGlobalDir_MergedListAndSearchAreByteIdenticalToLegacy()
        {
            var memory = new MemoryManager(_repo, _global);
            memory.SaveTopic("alpha-topic", "needle in alpha\nsecond line", "a");
            memory.SaveTopic("beta-topic", "needle in beta", "b");

            Assert.Equal(memory.ListTopics(), memory.ListTopicsForRecall());
            Assert.Equal(memory.SearchTopics("needle"), memory.SearchTopicsAllLayers("needle"));
            // And the legacy shape has no global-tagged hits.
            Assert.NotNull(memory.SearchTopicsAllLayers("needle"));
            Assert.DoesNotContain("global:", memory.SearchTopicsAllLayers("needle"));
        }

        [Fact]
        public void GlobalTopics_AppearTaggedInListAndSearch()
        {
            var memory = new MemoryManager(_repo, _global);
            memory.SaveTopic("alpha-topic", "needle in alpha", "a");
            CreateGlobal("gamma-topic", "needle in gamma");

            var list = memory.ListTopicsForRecall();
            Assert.Equal(new List<string> { "alpha-topic", "global:gamma-topic" }, list);

            string search = memory.SearchTopicsAllLayers("needle");
            Assert.Contains("alpha-topic:1:", search);
            Assert.Contains("global:gamma-topic:1:", search);
            // Repo hits come first (repo = specific context).
            Assert.True(
                search.IndexOf("alpha-topic", StringComparison.Ordinal)
                    < search.IndexOf("global:gamma-topic", StringComparison.Ordinal),
                "repo search hits must precede global search hits");
        }

        [Fact]
        public void MissingGlobalDir_ReadOnlySurfaceNeverThrows()
        {
            // _global was never created (and _global could be a bogus path):
            // every global read must degrade to empty/null, never throw.
            var memory = new MemoryManager(_repo, Path.Combine(_global, "does", "not", "exist"));
            memory.SaveTopic("repo-topic", "content", "r");

            Assert.Empty(memory.ListGlobalTopics());
            Assert.Null(memory.LoadGlobalTopic("repo-topic"));
            Assert.Null(memory.LoadGlobalStandingContext());
            Assert.Null(memory.RecallTopic("nope"));
            // Repo layer still fully functional.
            var res = memory.RecallTopic("repo-topic");
            Assert.NotNull(res);
            Assert.Equal("content", res.Content);
        }

        [Fact]
        public void GlobalStandingContext_IndependentOfRepoBudget()
        {
            // Passing a tiny REPO budget must not shrink the global layer's own
            // 8 KB cap — the budgets are independent by design. The repo topic is
            // sized to exceed the 64-byte repo budget (block = header + content) so it
            // is omitted with the legacy marker.
            var memory = new MemoryManager(_repo, _global);
            memory.SaveTopic("repo-conventions", new string('r', 80), "repo");
            CreateGlobal("standing-tooling", "rule one\nrule two\nrule three");

            string standing = memory.LoadStandingContext(maxTotalBytes: 64);

            // Repo layer over its 64-byte budget is omitted with the legacy marker…
            Assert.Contains("standing-context budget exhausted", standing);
            // …but the global layer loads in full from its own cap.
            Assert.Contains("rule three", standing);
        }
    }
}
