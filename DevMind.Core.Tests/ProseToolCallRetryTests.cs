// File: ProseToolCallRetryTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Some models write the tool call out as TEXT instead of emitting it on the tool-call
// channel — HeadlessAgent.SanitizeAnswer exists precisely to strip <tool_call> blocks out of
// a final answer, which is direct evidence the harness sees them. Such a turn arrives with
// no tool calls at all.
//
// The tempting fix is to parse the block and run it. That is the wrong trade. A missed
// recovery costs one iteration; a misread one executes something the model never asked for,
// and the shapes that would be misread — a call quoted inside an explanation, a half-written
// call the model then talked itself out of — are exactly the ones a text heuristic cannot
// tell from the real thing. So nothing here parses or executes the text.
//
// Instead the turn is routed to the existing narration retry, which re-issues it with
// tool_choice=required and makes the SERVER produce a structured call. Before this, such a
// turn fell through to the prose-finish re-prompt, which tells the model to call task_done
// or ask_caller — the wrong instruction for a model that was trying to call read_file, and
// one that ends the run early with the work unfinished.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class ProseToolCallRetryTests
    {
        [Theory]
        // The shapes SanitizeAnswer already strips, i.e. the ones actually observed.
        [InlineData("<tool_call><function=read_file>{\"filename\":\"a.cs\"}</function></tool_call>")]
        [InlineData("I'll check that.\n<tool_call>\n{\"name\":\"read_file\",\"arguments\":{}}\n</tool_call>")]
        [InlineData("<function=run_shell>{\"command\":\"dotnet build\"}</function>")]
        public async Task ProseEmittedToolCall_IsRetriedWithToolChoiceRequired(string prose)
        {
            using var env = new Env();

            var turn = await env.RunProseTurn(prose);

            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);

            // The retry that forces a STRUCTURED call, not the terminal-decision re-prompt.
            Assert.True(turn.ForceToolChoiceRequired,
                "a prose-written tool call must be retried with tool_choice=required");
            Assert.Equal(SyntheticPrompts.NarrationRetry, turn.NextContextualMessage);
            Assert.NotEqual(SyntheticPrompts.ProseFinish, turn.NextContextualMessage);

            Assert.True(env.State.NarrationRetryUsed);
        }

        // The recovery is a re-prompt, never an execution: whatever the text said, no tool
        // ran and no argument was read out of it.
        [Fact]
        public async Task ProseEmittedToolCall_IsNeverExecuted()
        {
            using var env = new Env();
            const string dangerous =
                "<tool_call><function=delete_file>{\"filename\":\"Program.cs\"}</function></tool_call>";

            var turn = await env.RunProseTurn(dangerous);

            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);
            Assert.True(turn.ForceToolChoiceRequired);

            // Nothing was mapped, nothing was run.
            Assert.Null(turn.ToolCalls);
            Assert.DoesNotContain("Deleted:", env.Host.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Program.cs", env.Host.Output, StringComparison.Ordinal);
        }

        // Ordinary prose with no call written in it must still take the terminal-decision
        // path — otherwise the new pattern would have swallowed the prose-finish behaviour.
        [Fact]
        public async Task OrdinaryProse_StillGetsTheTerminalDecisionRePrompt()
        {
            using var env = new Env();

            var turn = await env.RunProseTurn(
                "I have finished the refactor and everything looks consistent across the three files.");

            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);
            Assert.False(turn.ForceToolChoiceRequired);
            Assert.Equal(SyntheticPrompts.ProseFinish, turn.NextContextualMessage);
        }

        // One retry per user turn is the existing gate and must not change: a model that
        // keeps writing calls as prose has a problem no further prompting will fix, and the
        // loop must not spin on it.
        [Fact]
        public async Task TheRetryIsOneShot_ASecondProseCallFallsThroughToTheDecisionPrompt()
        {
            using var env = new Env();
            const string prose = "<tool_call><function=read_file>{\"filename\":\"a.cs\"}</function></tool_call>";

            var first = await env.RunProseTurn(prose);
            Assert.True(first.ForceToolChoiceRequired);

            var second = await env.RunProseTurn(prose);
            Assert.False(second.ForceToolChoiceRequired);
            Assert.Equal(SyntheticPrompts.ProseFinish, second.NextContextualMessage);
        }
    }
}
