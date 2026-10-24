// File: LoopDriverApprovalReasonTests.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The approval dialog's "Why (thinking):" fallback, end to end through a real LoopDriver:
// the response's reasoning (ILlmClient.LastReasoning â€” the same value the training log
// writes as reasoning_content) must actually reach the executor, not just the static
// helper the AgenticExecutor tests cover.

using System.Threading.Tasks;
using Xunit;
using static DevMind.Core.Tests.LoopDriverTest;   // Tool(); Env/FakeHost live in LoopDriverTestEnv.cs

namespace DevMind.Core.Tests
{
    public sealed class LoopDriverApprovalReasonTests
    {
        [Fact]
        public async Task ToolTurn_NoProse_ReasoningReachesTheApprovalDialog()
        {
            using var env = new Env();
            env.Options.ApprovalMode = ApprovalMode.Manual;
            env.Host.ConfirmAnswer = false;

            await env.RunToolTurnWith("", "Look around first.\n\nRun the tests to see what fails.",
                Tool("run_shell", "{\"command\":\"echo hi\"}"));

            string detail = Assert.Single(env.Host.ConfirmActionDetails);
            Assert.Equal("Tool: run_shell\nWhy (thinking): Run the tests to see what fails.\necho hi", detail);
        }

        [Fact]
        public async Task ToolTurn_WithProse_ProseBeatsReasoning()
        {
            using var env = new Env();
            env.Options.ApprovalMode = ApprovalMode.Manual;
            env.Host.ConfirmAnswer = false;

            await env.RunToolTurnWith("Running the tests.\n", "Hidden deliberation.",
                Tool("run_shell", "{\"command\":\"echo hi\"}"));

            string detail = Assert.Single(env.Host.ConfirmActionDetails);
            Assert.Equal("Tool: run_shell\nWhy: Running the tests.\necho hi", detail);
        }
    }
}
