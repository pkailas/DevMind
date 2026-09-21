// File: AgentTaskMaxDepthClampTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The devmind_task_* tools and the TUI's /depth-cap set the SAME engine option
// (AgenticLoopMaxDepth), so they must accept the same range. They did not: the
// MCP path clamped to 1-100 while /depth-cap (DevMind.TUI/SlashCommand.cs,
// DepthCapMax) and CLAUDE.md both say 1-200. A caller asking for 150 got 100
// and was told nothing.
//
// Pinned here:
//   * the ceiling is 200, and a value between the old and new ceilings survives
//     intact (the regression that actually bit);
//   * the floor is 1 and the default is still 40;
//   * an out-of-range value is still CLAMPED, never rejected — an existing
//     caller passing 500 must not start failing;
//   * ...but the clamp is no longer silent: it reports what it did, which is the
//     whole reason the mismatch went unnoticed for so long.

using Xunit;

namespace DevMind.McpServer.Tests
{
    public class AgentTaskMaxDepthClampTests
    {
        [Fact]
        public void Ceiling_MatchesTheTuiDepthCapCeiling()
        {
            Assert.Equal(200, AgentTaskTools.MaxDepthCeiling);
            Assert.Equal(1,   AgentTaskTools.MaxDepthFloor);
            Assert.Equal(40,  AgentTaskTools.MaxDepthDefault);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(40)]
        [InlineData(101)]   // above the OLD ceiling — silently truncated to 100 before the fix
        [InlineData(150)]
        [InlineData(200)]
        public void InRangeValues_PassThroughUnchanged_AndReportNothing(int requested)
        {
            int resolved = AgentTaskTools.ClampMaxDepth(requested, out string? notice);

            Assert.Equal(requested, resolved);
            Assert.Null(notice);
        }

        [Fact]
        public void OmittedValue_UsesTheDocumentedDefault()
        {
            int resolved = AgentTaskTools.ClampMaxDepth(null, out string? notice);

            Assert.Equal(40, resolved);
            Assert.Null(notice);
        }

        [Theory]
        [InlineData(500, 200)]
        [InlineData(201, 200)]
        [InlineData(0,     1)]
        [InlineData(-7,    1)]
        public void OutOfRangeValues_AreClampedNotRejected_AndTheClampIsReported(int requested, int expected)
        {
            int resolved = AgentTaskTools.ClampMaxDepth(requested, out string? notice);

            Assert.Equal(expected, resolved);
            Assert.NotNull(notice);
            Assert.Contains(requested.ToString(), notice!);
            Assert.Contains(expected.ToString(), notice!);
        }
    }
}
