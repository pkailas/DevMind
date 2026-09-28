// File: JobLivenessTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-37: a delegated job is cancelled on a STALL (no progress for the window), never on
// wall-clock time. These pin the liveness record the watchdog reads, on a manual clock so
// "ten minutes" costs nothing:
//   * a job that keeps ticking is never stalled however long it runs;
//   * silence for the window is a stall, and the reason names the last progress and its time;
//   * an in-flight shell/test command holds the clock for its own timeout plus one window
//     (and a command stuck past that is reported as such);
//   * the host ticks on every streamed shell output line.

using Xunit;

namespace DevMind.Core.Tests
{
    /// <summary>Hand-driven clock for stall tests (thread-safe).</summary>
    internal sealed class ManualClock : TimeProvider
    {
        private long _utcTicks;

        public ManualClock(DateTime startUtc) { _utcTicks = startUtc.Ticks; }

        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);
    }

    public sealed class JobLivenessTests
    {
        private static readonly DateTime Start = new DateTime(2026, 9, 28, 14, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

        [Fact]
        public void KeepsProgressing_PastManyWindows_NeverStalled()
        {
            var clock = new ManualClock(Start);
            var live = new JobLiveness(clock);

            // 100 iterations of 6 minutes each = 10 simulated hours, sixty windows.
            for (int i = 1; i <= 100; i++)
            {
                clock.Advance(TimeSpan.FromMinutes(6));
                Assert.False(live.IsStalled(Window, out _));
                live.Tick($"iteration {i} completed");
            }
            clock.Advance(TimeSpan.FromMinutes(9));
            Assert.False(live.IsStalled(Window, out string reason));
            Assert.Null(reason);
        }

        [Fact]
        public void Silence_ForTheWindow_IsAStall_NamingLastProgressAndTime()
        {
            var clock = new ManualClock(Start);
            var live = new JobLiveness(clock);
            clock.Advance(TimeSpan.FromMinutes(3));
            live.Tick("iteration 7 completed");

            clock.Advance(Window - TimeSpan.FromSeconds(1));
            Assert.False(live.IsStalled(Window, out _));

            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(live.IsStalled(Window, out string reason));
            Assert.Equal(
                "stalled: no progress for 10 min (last progress: iteration 7 completed at 2026-09-28 14:03:00 UTC)",
                reason);
        }

        [Fact]
        public void InFlightToolCall_HoldsTheClock_ForItsOwnTimeoutPlusOneWindow()
        {
            var clock = new ManualClock(Start);
            var live = new JobLiveness(clock);

            // A silent 15-minute test run with a 20-minute timeout of its own.
            using (live.BeginToolCall("test: dotnet test", TimeSpan.FromMinutes(20)))
            {
                clock.Advance(TimeSpan.FromMinutes(15));
                Assert.False(live.IsStalled(Window, out _));
            }
            Assert.Equal("tool call returned: test: dotnet test", live.LastProgress);

            // After it returned, the ordinary window applies again, from the return.
            clock.Advance(Window - TimeSpan.FromSeconds(1));
            Assert.False(live.IsStalled(Window, out _));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(live.IsStalled(Window, out _));
        }

        [Fact]
        public void InFlightToolCall_StuckPastItsOwnTimeoutAndAWindow_IsAStall()
        {
            var clock = new ManualClock(Start);
            var live = new JobLiveness(clock);

            using var call = live.BeginToolCall("shell: dotnet test", TimeSpan.FromMinutes(5));
            clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
            Assert.False(live.IsStalled(Window, out _));

            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(live.IsStalled(Window, out string reason));
            Assert.Equal(
                "stalled: no progress for 10 min (last progress: tool call started: shell: dotnet test at " +
                "2026-09-28 14:00:00 UTC; tool call 'shell: dotnet test' still running past its own 5 min timeout)",
                reason);
        }

        [Fact]
        public void EndingAToolCallTwice_TicksOnce_AndLeavesALaterCallInFlight()
        {
            var clock = new ManualClock(Start);
            var live = new JobLiveness(clock);

            var first = live.BeginToolCall("shell: a", TimeSpan.FromMinutes(1));
            first.Dispose();
            using var second = live.BeginToolCall("shell: b", TimeSpan.FromMinutes(30));
            first.Dispose(); // must not clear "b" or tick again

            Assert.Equal("tool call started: shell: b", live.LastProgress);
            clock.Advance(TimeSpan.FromMinutes(35));
            Assert.False(live.IsStalled(Window, out _));
        }

        [Theory]
        [InlineData(600, "10 min")]
        [InlineData(30, "30 s")]
        [InlineData(90, "90 s")]
        [InlineData(1.5, "1.5 s")]
        public void FormatSpan_WholeMinutesAsMinutes_ElseSeconds(double seconds, string expected)
            => Assert.Equal(expected, JobLiveness.FormatSpan(TimeSpan.FromSeconds(seconds)));

        // Every executed tool block reports back (LoopDriver turns it into a "tool call
        // returned" tick), so a batch of slow tools is alive between them, not only at the end.
        [Fact]
        public async Task Executor_ReportsEachExecutedToolBlock_ButNotProse()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"devmind_liveness_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var blocks = ToolCallMapper.Map(new List<ToolCallResult>
                {
                    new ToolCallResult { Name = "list_files", Arguments = new Dictionary<string, string>() },
                    new ToolCallResult { Name = "grep_file", Arguments = new Dictionary<string, string> { ["pattern"] = "x" } },
                }, buildCommand: "dotnet build");
                blocks.Insert(0, new ResponseBlock { Type = BlockType.Text, Content = "prose" });

                var executed = new List<BlockType>();
                var executor = new AgenticExecutor(new BufferedAgenticHost(dir), new FakeLlmOptions())
                {
                    BlockExecuted = executed.Add,
                };
                executor.SetCancellationToken(CancellationToken.None);
                await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild }, new ResponseOutcome(blocks));

                Assert.Equal(new[] { BlockType.ListFiles, BlockType.Grep }, executed);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        // The host ticks on every line a running shell command streams — not only when it
        // returns — so a long command that prints is visibly alive.
        [Fact]
        public async Task Host_StreamedShellOutputLines_TickLivenessWhileTheCommandRuns()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"devmind_liveness_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var clock = new ManualClock(Start);
                var live = new JobLiveness(clock);
                var host = new BufferedAgenticHost(dir) { Liveness = live };
                IAgenticHost h = host;

                const string command = "ping -n 3 127.0.0.1";
                var run = h.RunShellAsync(command, 30);

                bool sawLineTick = false;
                while (!run.IsCompleted)
                {
                    if (live.LastProgress == $"shell output: {command}") sawLineTick = true;
                    await Task.Delay(10);
                }
                await run;

                // (No assertion on the final value: Progress<T> posts line callbacks
                // asynchronously, so a late line may land after the return tick.)
                Assert.True(sawLineTick, "no 'shell output' tick was observed while the command ran");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
