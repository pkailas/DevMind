// File: StandingContextTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for MemoryManager.LoadStandingContext (MCP complaint #6): repo-level
// convention topics must be injected IN FULL into headless briefs — delegated
// agents repeatedly tripped rules (warnings-as-errors, LoggerMessage, test naming)
// that sat un-recalled in .devmind/memory.

using Xunit;

namespace DevMind.Core.Tests
{
    public class StandingContextTests : IDisposable
    {
        private readonly string _dir;

        public StandingContextTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_stand_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void ConventionAndStandingTopicsAreInjectedOthersAreNot()
        {
            var memory = new MemoryManager(_dir);
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
            var memory = new MemoryManager(_dir);
            memory.SaveTopic("scratch", "nothing standing here", "scratch");

            Assert.Null(memory.LoadStandingContext());
        }

        [Fact]
        public void BudgetExhaustionOmitsWithPointerInsteadOfTruncatingMidFile()
        {
            var memory = new MemoryManager(_dir);
            memory.SaveTopic("a-conventions", new string('x', 2_000), "big");
            memory.SaveTopic("b-conventions", new string('y', 2_000), "big");

            string? standing = memory.LoadStandingContext(maxTotalBytes: 2_500);

            Assert.NotNull(standing);
            // One fits; the other is omitted with a recall pointer, never half-included.
            Assert.Contains("standing-context budget exhausted", standing);
        }
    }
}
