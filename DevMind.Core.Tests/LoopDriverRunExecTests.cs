// File: LoopDriverRunExecTests.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-20: a successful run/exec is NEVER an implicit DONE.
//
// LoopDriver used to end the turn when a `dotnet run`/`dotnet exec` succeeded as the
// first run of a pure-shell turn (a rescue for chat models that never call task_done).
// These tests used to pin that rescue plus its two exemptions (run after file work,
// second run). The rescue itself was the bug: job-1693 (2026-09-25) ran ONE PowerShell
// research script — it wrote a probe Program.cs with Set-Content, which no mutation gate
// sees, and `dotnet run` it — after ~20 read/grep iterations, and the headless job ended
// `done` after 112 s with no answer and no work. The fallback is removed for every host:
// headless jobs always end on task_done, and an interactive model that never calls it
// still ends through the prose-finish re-prompt below. What is pinned now:
//   * every successful run/exec shape re-triggers the loop (the job-1693 script, the old
//     "build and run" pure-shell turn, run after scaffolding, a second run);
//   * a failing run re-triggers too;
//   * explicit task_done still ends the turn, even packed with a run.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static DevMind.Core.Tests.LoopDriverTest;   // Tool(); Env/FakeHost live in LoopDriverTestEnv.cs

namespace DevMind.Core.Tests
{
    public sealed class LoopDriverRunExecTests
    {
        // ── Successful run/exec never ends the turn ────────────────────────────────

        [Fact]
        public async Task Job1693_ResearchScriptWithDotnetRun_FirstShellCommand_Continues()
        {
            using var env = new Env();
            // job-1693's triggering command, shape-for-shape: a PowerShell script that writes
            // a probe project through Set-Content (no file tool, so no mutation is recorded)
            // and runs it. It is the first shell command of the turn and it succeeds.
            const string script =
                "$dir = Join-Path $env:TEMP 'skdump'\n" +
                "New-Item -ItemType Directory -Force $dir | Out-Null\n" +
                "$code = @\"\nusing System; class P { static void Main() { } }\n\"@\n" +
                "Set-Content -Path \"$dir\\Program.cs\" -Value $code -Encoding UTF8\n" +
                "dotnet run --project $dir 2>&1";
            var call = new ToolCallResult { Name = "run_shell", Id = "call_1" };
            call.Arguments["command"] = script;

            var turn = await env.RunToolTurn(call);

            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);
            Assert.DoesNotContain("treating as task complete", env.Host.Output);
            Assert.DoesNotContain("Task complete.", env.Host.Output);
        }

        [Fact]
        public async Task RunExec_PureShellTurn_FirstRun_Continues()
        {
            using var env = new Env();
            // The case the fallback was written for ("build and run this", no task_done).
            // It now continues: the model reports and calls task_done, or finishes in prose
            // and meets the prose-finish re-prompt — one extra iteration, never a silent end.
            var turn1 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet build\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn1.Kind);

            var turn2 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn2.Kind);
            Assert.DoesNotContain("treating as task complete", env.Host.Output);
        }

        [Fact]
        public async Task RunExec_AfterScaffolding_Files_Continues()
        {
            using var env = new Env();
            // Task is "produce a written analysis": the model scaffolds a throwaway app and
            // runs it to probe runtime behaviour, then must still write the analysis.
            var turn1 = await env.RunToolTurn(Tool("create_file",
                "{\"filename\":\"probe.cs\",\"content\":\"using System;\\nstatic class P{static void Main(){System.Console.WriteLine(1);}}\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn1.Kind);

            var turn2 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn2.Kind);
            Assert.DoesNotContain("treating as task complete", env.Host.Output);
        }

        [Fact]
        public async Task RunExec_SecondSuccessfulRunSameTurn_Continues()
        {
            using var env = new Env();
            var run1 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, run1.Kind);

            var run2 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run --variant two\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, run2.Kind);
            Assert.DoesNotContain("treating as task complete", env.Host.Output);
        }

        [Fact]
        public async Task RunExec_Failing_Continues()
        {
            using var env = new Env();
            env.Host.ShellResults["dotnet run"] = (exitCode: 1, output: "Unhandled exception");

            var turn = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run\"}"));

            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);
        }

        [Fact]
        public async Task ExplicitTaskDone_WithARun_StillEndsTheTurn()
        {
            using var env = new Env();
            // Model packs a final shell run together with task_done: task_done ends the turn.
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
