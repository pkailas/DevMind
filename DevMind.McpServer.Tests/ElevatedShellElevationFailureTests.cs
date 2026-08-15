// File: ElevatedShellElevationFailureTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for elevated_shell's handling of the Win32Exception raised when
// Process.Start fails to spawn the elevated ("runas") powershell. The old catch
// asserted "UAC prompt was declined (or elevation is blocked by policy)" for EVERY
// Win32Exception — but that exception also covers "no interactive desktop / no
// session to prompt in", which is the NORMAL case when DevMind runs headless as an
// MCP server. The agent would then tell the user to approve a prompt nobody ever saw.
//
// The fix distinguishes the ONE cause it can actually confirm — ERROR_CANCELLED
// (1223), the genuine user-declined-at-the-UAC case — from everything else, where it
// reports the actual error code and says the cause is undetermined rather than
// asserting user intent.

using System.ComponentModel;
using Xunit;

namespace DevMind.McpServer.Tests;

public sealed class ElevatedShellElevationFailureTests
{
    // ── ERROR_CANCELLED (1223): the one case where we KNOW the user declined ──
    // This is the genuine UAC-declined path. The message MAY assert the user
    // declined — and it must include the code so a caller can confirm the cause.
    [Fact]
    public void ErrorCode1223_ClaimsUserDeclined()
    {
        var ex = new Win32Exception(1223, "The operation was canceled.");
        string msg = DevMindTools.DescribeElevationFailure(ex);

        Assert.Contains("declined", msg, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1223", msg);
    }

    // ── Any OTHER code: we do NOT know the user declined ─────────────────────
    // These are the headless / policy-blocked / no-desktop cases. The message
    // must NOT claim the user declined a prompt (there may have been no prompt),
    // and must surface the actual code + say the cause is undetermined.
    [Theory]
    [InlineData(5, "Access is denied.")]            // ERROR_ACCESS_DENIED
    [InlineData(740, "The operation requires elevation.")]  // ERROR_ELEVATION_REQUIRED
    [InlineData(741, "Access denied by UAC policy.")]      // ERROR_ELEVATION_PROMPT
    [InlineData(1054, "No interactive desktop available.")] // ERROR_NO_INTERACTIVE desktop-ish
    public void NonCancelledCode_DoesNotClaimUserDeclined(int code, string osMessage)
    {
        var ex = new Win32Exception(code, osMessage);
        string msg = DevMindTools.DescribeElevationFailure(ex);

        // The central regression: must NOT assert the user declined.
        Assert.DoesNotContain("declined", msg, System.StringComparison.OrdinalIgnoreCase);
        // Must report the actual error code so a caller can investigate.
        Assert.Contains(code.ToString(), msg);
        // Must say the cause is undetermined / not assume a prompt was shown.
        Assert.Contains("could not be determined", msg, System.StringComparison.OrdinalIgnoreCase);
    }

    // ── 1223 vs. a non-1223 code must produce DIFFERENT messages ─────────────
    // This is the whole point of the fix: the two causes are now distinguishable,
    // so an agent can tell "user said no" from "no one was asked".
    [Fact]
    public void DeclinedVsHeadless_ProduceDistinguishableMessages()
    {
        string declined = DevMindTools.DescribeElevationFailure(
            new Win32Exception(1223, "The operation was canceled."));
        string headless = DevMindTools.DescribeElevationFailure(
            new Win32Exception(740, "The operation requires elevation."));

        Assert.NotEqual(declined, headless);
        Assert.Contains("declined", declined, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("declined", headless, System.StringComparison.OrdinalIgnoreCase);
    }
}
