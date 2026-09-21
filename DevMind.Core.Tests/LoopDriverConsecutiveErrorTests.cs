// File: LoopDriverConsecutiveErrorTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The consecutive-error abort fires after ConsecutiveErrorAbortThreshold (5) failures of
// the SAME tool. It printed the tool name, the count, the last error, the command and an
// output snippet — and then closed with "Review the failure above and re-send with a
// corrected approach", which says nothing the caller did not already know.
//
// Everything needed to say something specific was already in hand. Five failures sharing
// one tool IS the finding: it separates "this tool is being called wrongly" from "five
// unrelated things went wrong", and that distinction is what decides whether the fix
// belongs in the brief or in the code.
//
// Both routes into the abort are exercised, because they are different findings:
//   * five failures, five DIFFERENT error shapes — the thrash guard never fires, so this
//     abort is the only thing that reports anything at all;
//   * five failures, one repeating error shape — the thrash guard has already nudged, and
//     the consecutive check still wins the race to terminate.

using Xunit;
using static DevMind.Core.Tests.LoopDriverTest;

namespace DevMind.Core.Tests
{
    public sealed class LoopDriverConsecutiveErrorTests
    {
        private const int Threshold = 5;   // LoopDriver.ConsecutiveErrorAbortThreshold

        private static ToolCallResult FailingShell(string command)
            => Tool("run_shell", "{\"command\":\"" + command + "\"}");

        /// <summary>Drives <paramref name="count"/> failing run_shell turns. Each command is
        /// distinct, and so is its error text unless <paramref name="sameError"/> is set —
        /// which is what decides whether the thrash guard's signature counter also climbs.</summary>
        private static async Task<LoopIterationResult> DriveFailuresAsync(Env env, int count, bool sameError)
        {
            LoopIterationResult last = null!;
            for (int i = 0; i < count; i++)
            {
                string cmd = $"dotnet build step{i}";
                string err = sameError
                    ? "error CS0103: The name 'Foo' does not exist in the current context"
                    : $"error CS010{i}: distinct failure number {i}";
                env.Host.ShellResults[cmd] = (1, err);
                last = await env.RunToolTurn(FailingShell(cmd));
            }
            return last;
        }

        [Fact]
        public async Task Abort_NamesThePattern_WhenFiveFailuresShareOneTool()
        {
            using var env = new Env();

            var turn = await DriveFailuresAsync(env, Threshold, sameError: false);

            Assert.Equal(LoopIterationKind.Terminal, turn.Kind);
            Assert.Equal("consecutive_errors", turn.TerminalReason);

            string output = env.Host.Output;

            // The finding: one tool, every time — not five unrelated failures.
            Assert.Contains("Pattern:", output, StringComparison.Ordinal);
            Assert.Contains($"every one of those {Threshold} failures was 'run_shell'", output, StringComparison.Ordinal);
            Assert.Contains("the tool is being called wrongly", output, StringComparison.Ordinal);

            // ...and what to do about that specific finding.
            Assert.Contains("Fix how it is called, or use a different tool.", output, StringComparison.Ordinal);

            // The generic line it replaced must be gone.
            Assert.DoesNotContain("re-send with a corrected approach", output, StringComparison.Ordinal);
        }

        // Asserting only that the old sentence is absent would pass if the whole abort
        // message vanished. The detail above the closing line is the reason the message is
        // worth printing at all, so it is pinned too.
        [Fact]
        public async Task Abort_KeepsTheDetailAboveTheClosingLine()
        {
            using var env = new Env();

            await DriveFailuresAsync(env, Threshold, sameError: false);

            string output = env.Host.Output;

            Assert.Contains($"[AGENTIC] Aborted: 'run_shell' failed {Threshold} consecutive times with no resolution.",
                output, StringComparison.Ordinal);
            Assert.Contains("Last error:", output, StringComparison.Ordinal);
            Assert.Contains("Last command: dotnet build step4", output, StringComparison.Ordinal);
            Assert.Contains("Output:", output, StringComparison.Ordinal);
            Assert.Contains("distinct failure number 4", output, StringComparison.Ordinal);
        }

        // Below the threshold the loop keeps going — the abort must not fire early, or the
        // "five failures" the message claims would be a lie.
        [Fact]
        public async Task BelowTheThreshold_TheLoopContinuesAndSaysNothingAboutAPattern()
        {
            using var env = new Env();

            var turn = await DriveFailuresAsync(env, Threshold - 1, sameError: false);

            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);
            Assert.DoesNotContain("Pattern:", env.Host.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("[AGENTIC] Aborted:", env.Host.Output, StringComparison.Ordinal);
        }

        // The overlapping case: ThrashNudgeThreshold (3) is lower than this threshold, so a
        // repeating signature has already drawn a research directive by turn 3. The
        // consecutive-error check sits ahead of the thrash abort in the loop, so it is still
        // the one that terminates — and it still names the tool, which the thrash message
        // (which names the failure SIGNATURE) does not.
        [Fact]
        public async Task RepeatingSignature_ConsecutiveAbortStillWins_AndStillNamesTheTool()
        {
            using var env = new Env();

            var turn = await DriveFailuresAsync(env, Threshold, sameError: true);

            Assert.Equal(LoopIterationKind.Terminal, turn.Kind);
            Assert.Equal("consecutive_errors", turn.TerminalReason);
            Assert.Contains($"every one of those {Threshold} failures was 'run_shell'",
                env.Host.Output, StringComparison.Ordinal);
        }
    }
}
