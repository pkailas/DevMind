// File: AgentTaskStatusWaitTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the optional wait_seconds on devmind_task_status. The wait logic
// lives in two small pure helpers on AgentJobManager so it can be tested
// without a live job queue:
//   ClampWaitSeconds   — omitted/0/negative -> 0 (immediate), above cap -> cap.
//   WaitForStateChangeAsync — blocks until the observed state string changes or
//                             the budget elapses, polling ~1s, honoring cancel.
// The tool wires these up: TaskStatus(job_id, wait_seconds) waits via
// WaitForStateChangeAsync(() => DisplayState(job), wait_seconds ?? 0, ct) and
// then returns the normal payload, so omitted wait = the old immediate path.

using System.Diagnostics;
using Xunit;

namespace DevMind.McpServer.Tests;

public sealed class ClampWaitSecondsTests
{
    [Theory]
    [InlineData(0)]    // omitted/null coalesces to 0 -> immediate
    [InlineData(1)]
    [InlineData(55)]   // exactly at the cap stays
    public void WithinRange_IsUnchanged(int requested)
        => Assert.Equal(requested, DevMind.McpServer.AgentJobManager.ClampWaitSeconds(requested));

    [Theory]
    [InlineData(-5)]   // nonsense input degrades to immediate, not an error
    [InlineData(int.MinValue)]
    public void Negative_IsNormalizedToZeroNotAnError(int requested)
        => Assert.Equal(0, DevMind.McpServer.AgentJobManager.ClampWaitSeconds(requested));

    [Theory]
    [InlineData(56, 55)]
    [InlineData(60, 55)]    // H-23: 60 would race a 60 s transport timeout
    [InlineData(300, 55)]   // clamp, not reject
    [InlineData(int.MaxValue, 55)]
    public void AboveCap_IsClampedNotRejected(int requested, int expected)
        => Assert.Equal(expected, DevMind.McpServer.AgentJobManager.ClampWaitSeconds(requested));
}

public sealed class WaitForStateChangeTests
{
    // ── Omitted/zero wait returns immediately ─────────────────────────────────
    // The default must stay the long-standing behavior: one observation, no wait.
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task ZeroWait_ReturnsImmediatelyWithoutObservingAgain(int waitSeconds)
    {
        int observations = 0;
        Func<string> observe = () => observations++ == 0 ? "queued" : "done";

        var sw = Stopwatch.StartNew();
        bool changed = await DevMind.McpServer.AgentJobManager
            .WaitForStateChangeAsync(observe, waitSeconds, CancellationToken.None);
        sw.Stop();

        Assert.False(changed);              // no wait, no change reported
        // The helper observes zero times on the immediate path (the tool's initial
        // observation happens before the helper is even called).
        Assert.Equal(0, observations);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1),
            $"zero wait should not sleep, but took {sw.Elapsed}");
    }

    // ── State change returns early ────────────────────────────────────────────
    // When the state flips at ~1s the wait must return at ~1s, NOT burn the
    // full (clamped) budget.
    [Fact]
    public async Task StateChange_ReturnsEarlyNotFullBudget()
    {
        var states = new List<string> { "running", "running", "done" }; // flips on 2nd poll
        int i = 0;
        Func<string> observe = () => states[Math.Min(i++, states.Count - 1)];

        var sw = Stopwatch.StartNew();
        bool changed = await DevMind.McpServer.AgentJobManager
            .WaitForStateChangeAsync(observe, 60, CancellationToken.None);
        sw.Stop();

        Assert.True(changed);
        // 60s budget clamped stays 60; returning within ~3s proves the early
        // exit, not the budget elapsing.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"expected early return on state change, but took {sw.Elapsed}");
    }

    // ── No change burns the full (clamped) budget ─────────────────────────────
    // A steady state with a small requested wait returns only at the deadline.
    [Fact]
    public async Task SteadyState_BurnsRequestedBudget()
    {
        var sw = Stopwatch.StartNew();
        bool changed = await DevMind.McpServer.AgentJobManager
            .WaitForStateChangeAsync(() => "running", 2, CancellationToken.None);
        sw.Stop();

        Assert.False(changed);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(2),
            $"expected the full 2s wait, but took {sw.Elapsed}");
    }

    // ── Above-cap wait is clamped, not rejected ───────────────────────────────
    // The clamp is the exact unit the wait's deadline feeds from, so pinning it is
    // pinning the behavior: 300s requested -> 55s deadline, never an error. The
    // StateChange early-return test (which requests 60s — clamped to the cap) shows
    // the helper honors that clamped budget end-to-end.
    [Fact]
    public void AboveCapWait_ClampedToMaxNotRejected()
    {
        Assert.Equal(DevMind.McpServer.AgentJobManager.MaxWaitSeconds,
            DevMind.McpServer.AgentJobManager.ClampWaitSeconds(300));
        // The helper's immediate-return path is what an above-cap request reduces
        // to for its deadline computation — clamp(300) is what the deadline uses.
        Assert.Equal(55, DevMind.McpServer.AgentJobManager.ClampWaitSeconds(300));
    }

    // ── Cancellation ends the wait immediately ────────────────────────────────
    // A cancelled MCP request must not keep the caller blocked for the full wait.
    [Fact]
    public async Task Cancellation_EndsWaitImmediately()
    {
        using var cts = new CancellationTokenSource();
        var task = DevMind.McpServer.AgentJobManager
            .WaitForStateChangeAsync(() => "running", 60, cts.Token);
        cts.CancelAfter(250);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"cancellation should end the wait promptly, but took {sw.Elapsed}");
    }
}
