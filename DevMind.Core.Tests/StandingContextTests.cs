// File: StandingContextTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for MemoryManager.LoadStandingContext (MCP complaint #6): repo-level
// convention topics must be injected IN FULL into headless briefs — delegated
// agents repeatedly tripped rules (warnings-as-errors, LoggerMessage, test naming)
// that sat un-recalled in .devmind/memory.
//
// Uses the MemoryManager(repoRoot, globalDir) test seam so the real
// %APPDATA%\devmind\memory is never read — a machine-level standing-conventions.md
// on the developer's machine must not leak into these repo-layer assertions.
// _global is deliberately NOT created: every test exercises the "no global dir"
// state, which is the byte-identical legacy output under test here.

using Xunit;

namespace DevMind.Core.Tests
{
    public class StandingContextTests : IDisposable
    {
        private readonly string _repo;
        private readonly string _global;

        public StandingContextTests()
        {
            string baseDir = Path.Combine(Path.GetTempPath(), $"devmind_stand_{Guid.NewGuid():N}");
            _repo = Path.Combine(baseDir, "repo");
            _global = Path.Combine(baseDir, "global");
            Directory.CreateDirectory(_repo);
            // NOTE: _global is deliberately NOT created — these tests assert the
            // repo-layer behaviour in isolation ("no global dir" state).
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

        [Fact]
        public void ConventionAndStandingTopicsAreInjectedOthersAreNot()
        {
            var memory = new MemoryManager(_repo, _global);
            memory.SaveTopic("parsely-backend-conventions", "warnings are errors; LoggerMessage pattern", "conventions");
            memory.SaveTopic("standing-deploy-rules", "republish all four services after schema changes", "deploy");
            memory.SaveTopic("random-debug-notes", "one-off investigation notes", "notes");

            string? standing = memory.LoadStandingContext();

            Assert.NotNull(standing);
            Assert.Contains("warnings are errors", standing);
            Assert.Contains("republish all four services", standing);
            Assert.DoesNotContain("one-off investigation notes", standing);
        }

        [Fact]
        public void NoStandingTopicsReturnsNull()
        {
            var memory = new MemoryManager(_repo, _global);
            memory.SaveTopic("scratch", "nothing standing here", "scratch");

            Assert.Null(memory.LoadStandingContext());
        }

        [Fact]
        public void BudgetExhaustionOmitsWithPointerInsteadOfTruncatingMidFile()
        {
            var memory = new MemoryManager(_repo, _global);
            memory.SaveTopic("a-conventions", new string('x', 2_000), "big");
            memory.SaveTopic("b-conventions", new string('y', 2_000), "big");

            string? standing = memory.LoadStandingContext(maxTotalBytes: 2_500);

            Assert.NotNull(standing);
            // One fits; the other is omitted with a recall pointer, never half-included.
            Assert.Contains("standing-context budget exhausted", standing);
        }
    }
}
