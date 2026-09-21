// File: LoopDriverRunExecTests.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Covers the run/exec implicit-DONE fallback in LoopDriver:
//   * preserved: a successful `dotnet run` as the ONLY shell work of a pure-shell
//     turn still terminates the loop (rescues "build and run this" tasks that
//     never emit task_done).
//   * fixed bug: a successful `dotnet run` AFTER file work (scaffold → run) or
//     after a prior successful run/exec is an intermediate step — the loop must
//     re-trigger, not truncate the task (e.g. "run this and report on it").
//   * IsRunOrExecCommand no longer classifies bare "*.exe" commands.
//   * explicit task_done still wins (checked before the fallback).

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static DevMind.Core.Tests.LoopDriverTest;   // Tool(); Env/FakeHost live in LoopDriverTestEnv.cs

namespace DevMind.Core.Tests
{
    public sealed class LoopDriverRunExecTests
    {
        // ── IsRunOrExecCommand classification ────────────────────────────────────

        [Theory]
        [InlineData("dotnet run", true)]
        [InlineData("dotnet run --project src/App", true)]
        [InlineData("dotnet exec app.dll", true)]
        [InlineData("dotnet run; echo done", true)]
        [InlineData("dotnet build", false)]
        [InlineData("dotnet test", false)]
        [InlineData(@"C:\temp\app\bin\app.exe", false)]          // bare .exe — dropped on purpose
        [InlineData(@"C:\temp\app.exe --check", false)]          // bare .exe with args
        [InlineData(@"MSBuild.exe My.csproj", false)]
        [InlineData("", false)]
        [InlineData("git status", false)]
        public void IsRunOrExecCommand_Classifies(string command, bool expected)
        {
            Assert.Equal(expected, LoopHelpers.IsRunOrExecCommand(command));
        }

        [Fact]
        public void LoopState_ResetForUserTurn_ClearsRunExecGateFlags()
        {
            var state = new LoopState
            {
                HadFileMutationThisTurn  = true,
                RunExecSucceededThisTurn = true,
            };

            state.ResetForUserTurn();

            Assert.False(state.HadFileMutationThisTurn);
            Assert.False(state.RunExecSucceededThisTurn);
        }

        // ── LoopDriver fallback behaviour ─────────────────────────────────────────

        [Fact]
        public async Task RunExec_PureShellTurn_FirstRun_Terminates()
        {
            using var env = new Env();
            // "Build and run this": no file tools, no task_done — the run is the deliverable.
            var turn1 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet build\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn1.Kind); // build ≠ run/exec — loop continues

            var turn2 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run\"}"));
            Assert.Equal(LoopIterationKind.Terminal, turn2.Kind);
            Assert.Contains("Run/exec command succeeded", env.Host.Output);
        }

        [Fact]
        public async Task RunExec_AfterScaffolding_Files_DoesNotTerminate()
        {
            using var env = new Env();
            // The observed bug: task is "produce a written analysis". The model scaffolds
            // a throwaway app (file work) and runs it to probe runtime behaviour, then must
            // still write the analysis. The successful run must NOT end the task.
            var turn1 = await env.RunToolTurn(Tool("create_file",
                "{\"filename\":\"probe.cs\",\"content\":\"using System;\\nstatic class P{static void Main(){System.Console.WriteLine(1);}}\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn1.Kind);
            Assert.True(env.State.HadFileMutationThisTurn);

            // Before the fix, this iteration returned Terminal ("treating as task complete").
            var turn2 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn2.Kind);
            Assert.DoesNotContain("treating as task complete", env.Host.Output);
            Assert.True(env.State.RunExecSucceededThisTurn);
        }

        [Fact]
        public async Task RunExec_SecondSuccessfulRunSameTurn_DoesNotTerminate()
        {
            using var env = new Env();
            // A successful run that was gated by prior file work sets the
            // RunExecSucceededThisTurn flag; a LATER run/exec in the same turn is
            // always an intermediate compare-and-check step, never the deliverable.
            await env.RunToolTurn(Tool("create_file",
                "{\"filename\":\"probe.cs\",\"content\":\"x\"}"));

            var run1 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, run1.Kind);
            Assert.True(env.State.RunExecSucceededThisTurn);

            var run2 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run --variant two\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, run2.Kind);
            Assert.DoesNotContain("treating as task complete", env.Host.Output);
        }

        [Fact]
        public async Task RunExec_Failing_DoesNotTerminate()
        {
            using var env = new Env();
            env.Host.ShellResults["dotnet run"] = (exitCode: 1, output: "Unhandled exception");

            var turn = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run\"}"));

            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);
            Assert.False(env.State.RunExecSucceededThisTurn);
        }

        [Fact]
        public async Task ExplicitTaskDone_TakesPrecedenceOverRunExecFallback()
        {
            using var env = new Env();
            // Model packs a final shell run together with task_done: the explicit
            // DONE check must win (order preserved — the fallback stays a fallback).
            var turn = await env.RunToolTurn(
                Tool("run_shell", "{\"command\":\"dotnet run\"}"),
                Tool("task_done", "{\"summary\":\"done\"}"));

            Assert.Equal(LoopIterationKind.Terminal, turn.Kind);
            Assert.Contains("Task complete.", env.Host.Output);
        }

        // ── Prose-finish re-prompt: task_done vs ask_caller ─────────────────────────
        // The signalling gap: a model that ends in prose with questions buried in it,
        // instead of calling ask_caller, terminates as "done" and the caller never learns
        // input is wanted. The structural hook is the no-tool-call terminal gate. These
        // tests pin that the one-shot re-prompt (a) fires for a long prose ending and
        // (b) presents BOTH terminal tools — task_done AND ask_caller — so the model
        // makes the done-vs-needs_input decision at the exact point where it stops.

        [Fact]
        public async Task ProseFinish_NoToolCall_FiresOneShotRePromptWithBothTerminalTools()
        {
            using var env = new Env();
            // A prose ending with no tool call (the "2 of 4 runs" failure mode — the
            // model writes numbered questions in the final text and stops).
            string prose = "I need to clarify a couple of things before proceeding: " +
                           "1. Should the endpoint use SQL or an ORM? 2. What auth is expected?";

            var turn = await env.RunProseTurn(prose);

            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);
            Assert.True(env.State.PromptedForTaskDone);          // one-shot consumed
            Assert.True(env.State.ShellLoopPending);
            // The re-prompt must name BOTH terminal tools so the decision is explicit.
            Assert.Contains("task_done", turn.NextContextualMessage);
            Assert.Contains("ask_caller", turn.NextContextualMessage);
            Assert.Equal(SyntheticPrompts.ProseFinish, turn.NextContextualMessage);
        }

        [Fact]
        public async Task ProseFinish_ShortQuestionOnlyAlso_FiresRePrompt()
        {
            using var env = new Env();
            // Regression for the loosened gate: a SHORT question-only ending (fewer
            // than the old 40-char threshold) must still reach the decision prompt,
            // not terminate as "done" with the question buried in the answer.
            var turn = await env.RunProseTurn("Should I use SQL or ORM?");

            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);
            Assert.True(env.State.PromptedForTaskDone);
            Assert.Contains("ask_caller", turn.NextContextualMessage);
            Assert.Contains("task_done", turn.NextContextualMessage);
        }

        [Fact]
        public async Task ProseFinish_RePromptIsOneShot_SecondProseTerminates()
        {
            using var env = new Env();
            var first = await env.RunProseTurn("I need to clarify: which database should I target here?");
            Assert.Equal(LoopIterationKind.ShouldReTrigger, first.Kind);

            // The model STILL answers in prose (ignores the re-prompt) — the loop must
            // not loop forever: PromptedForTaskDone is already set, so it accepts
            // prose-finish and terminates (reason null → caller sees "done").
            var second = await env.RunProseTurn("I'll make a reasonable assumption and note it.");
            Assert.Equal(LoopIterationKind.Terminal, second.Kind);
            Assert.Null(second.TerminalReason);
        }
    }
}
