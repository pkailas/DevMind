// File: ShellCallInterruptTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The pieces an override steer's shell interrupt is built from, shared by the headless
// host (BufferedAgenticHost) and the TUI host (TuiAgenticHost — which needs a live
// Terminal.Gui view and so is not constructed in tests; these pin what it delegates to):
//
//   * ShellCallInterrupt — a call interrupted mid-run (30s Start-Sleep) returns within
//     seconds, reports the steer reason, and leaves the turn token alone; a turn cancel
//     is NOT reported as a steer cancel; Cancel is a no-op with nothing in flight
//   * Steer.InterruptForOverride — the post-enqueue decision both the headless session
//     and the TUI's steer handler call: accepted override → cancel; suggest, refused
//     enqueue, or an override the next boundary would refuse → leave the call alone

using System.Diagnostics;
using Xunit;

namespace DevMind.Core.Tests
{
    [Collection("ShellRunnerJobObject")]
    public class ShellCallInterruptTests : IDisposable
    {
        private const string Marker = "interrupt-started.flag";
        private readonly string _dir;

        public ShellCallInterruptTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_interrupt_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static bool CanRunShell => OperatingSystem.IsWindows() && ShellRunner.IsPowerShellAvailable();

        private const string Sleep30 = "New-Item -ItemType File " + Marker + " | Out-Null; Start-Sleep 30";

        private async Task WaitForMarkerAsync()
        {
            string path = Path.Combine(_dir, Marker);
            var sw = Stopwatch.StartNew();
            while (!File.Exists(path) && sw.ElapsedMilliseconds < 20_000)
                await Task.Delay(20);
            Assert.True(File.Exists(path), "the shell call never started");
        }

        [Fact]
        public async Task Cancel_DuringARunningCall_EndsItInSeconds_WithTheSteerMessage_TurnUntouched()
        {
            if (!CanRunShell) return;

            var interrupt = new ShellCallInterrupt(ShellCallInterrupt.OperatorSteerMessage);
            using var turn = new CancellationTokenSource();
            var runner = new ShellRunner(_dir);
            var sw = Stopwatch.StartNew();

            string output, reason;
            using (var call = interrupt.Begin(turn.Token))
            {
                var run = runner.ExecuteAsync(Sleep30, turn.Token, timeoutSeconds: 60, interruptToken: call.Token);
                await WaitForMarkerAsync();
                Assert.True(interrupt.Cancel("change course"));
                (output, _) = await run;
                reason = call.End();
            }

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"call took {sw.Elapsed} — the 30s sleep was not cancelled");
            Assert.Equal("change course", reason);
            Assert.False(turn.IsCancellationRequested);
            string result = interrupt.ResultFor(output);
            Assert.StartsWith(ShellCallInterrupt.OperatorSteerMessage, result);
            Assert.DoesNotContain("[SHELL] Command cancelled.", result);
        }

        [Fact]
        public async Task TurnCancel_IsNotReportedAsASteerCancel()
        {
            if (!CanRunShell) return;

            var interrupt = new ShellCallInterrupt(ShellCallInterrupt.OperatorSteerMessage);
            using var turn = new CancellationTokenSource();
            var runner = new ShellRunner(_dir);

            using var call = interrupt.Begin(turn.Token);
            var run = runner.ExecuteAsync(Sleep30, turn.Token, timeoutSeconds: 60, interruptToken: call.Token);
            await WaitForMarkerAsync();
            turn.Cancel();
            var (output, exitCode) = await run;

            Assert.Null(call.End());
            Assert.Equal(-1, exitCode);
            Assert.Contains("[SHELL] Command cancelled.", output);
            Assert.False(interrupt.Cancel("too late"));
        }

        [Fact]
        public void Cancel_IsANoOp_WhenIdle_AndAfterTheCallEnded()
        {
            var interrupt = new ShellCallInterrupt(ShellCallInterrupt.DriverSteerMessage);
            Assert.False(interrupt.Cancel("nothing running"));

            var call = interrupt.Begin(CancellationToken.None);
            Assert.Null(call.End());
            Assert.False(interrupt.Cancel("already ended"));
            Assert.Null(call.End());   // idempotent
            call.Dispose();
        }

        [Fact]
        public void Cancel_SecondTime_ReportsNothingMoreToCancel()
        {
            var interrupt = new ShellCallInterrupt(ShellCallInterrupt.DriverSteerMessage);
            using var call = interrupt.Begin(CancellationToken.None);
            Assert.True(interrupt.Cancel("first"));
            Assert.True(call.Token.IsCancellationRequested);
            Assert.False(interrupt.Cancel("second"));
            Assert.Equal("first", call.End());
        }

        [Theory]
        [InlineData("[SHELL] Command cancelled.", "")]
        [InlineData("[SHELL] Command cancelled.\n(no output)", "")]
        [InlineData("[SHELL] Command cancelled.\nline one\nline two", "\nline one\nline two")]
        public void ResultFor_ReplacesTheGenericCancelLine(string output, string expectedTail)
        {
            var interrupt = new ShellCallInterrupt(ShellCallInterrupt.OperatorSteerMessage);
            Assert.Equal(ShellCallInterrupt.OperatorSteerMessage + expectedTail, interrupt.ResultFor(output));
        }

        // ── Steer.InterruptForOverride: the post-enqueue decision ──

        private static readonly SteerEnqueueResult Accepted = new() { Accepted = true };
        private static readonly SteerEnqueueResult Refused = new() { Accepted = false };

        [Theory]
        // mode, accepted, maxDepth, depth, expectCancel
        [InlineData(SteerMode.Override, true, 5, 0, true)]
        [InlineData(SteerMode.Override, true, 0, 99, true)]    // uncapped
        [InlineData(SteerMode.Override, true, 5, 3, true)]     // next boundary is depth 4 of 5
        [InlineData(SteerMode.Override, true, 5, 4, false)]    // next boundary is the last → refused
        [InlineData(SteerMode.Override, true, 1, 0, false)]
        [InlineData(SteerMode.Override, false, 5, 0, false)]   // enqueue refused
        [InlineData(SteerMode.Suggest, true, 5, 0, false)]     // a suggest never interrupts
        public void InterruptForOverride_Decision(SteerMode mode, bool accepted, int maxDepth, int depth, bool expectCancel)
        {
            string? cancelledWith = null;
            bool cancelled = Steer.InterruptForOverride(
                accepted ? Accepted : Refused, mode, "redirect", maxDepth, depth,
                reason => { cancelledWith = reason; return true; });

            Assert.Equal(expectCancel, cancelled);
            Assert.Equal(expectCancel ? "redirect" : null, cancelledWith);
        }

        [Fact]
        public void InterruptForOverride_ReportsFalse_WhenNothingWasInFlight()
            => Assert.False(Steer.InterruptForOverride(Accepted, SteerMode.Override, "redirect", 5, 0, _ => false));
    }
}
