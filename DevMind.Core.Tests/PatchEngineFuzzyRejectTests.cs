// File: PatchEngineFuzzyRejectTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the enriched "FIND text not found" patch error: the rejection must
// name WHICH check fired (below-threshold vs. ambiguity gap), name the nearest
// candidate's line number, and show that candidate's actual content so an agent
// can see how its FIND differs from what's really in the file.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests;

public sealed class PatchEngineFuzzyRejectTests
{
    // Multi-line file with a distinctive near-miss region on line 3, so the
    // nearest candidate's line is unambiguous to assert on.
    private static string NearMissSource =>
        "public class Sample\n" +   // 1
        "{\n" +                      // 2
        "    public int Answer() { return 42; }\n" + // 3
        "    public void DoNothing() { }\n" +        // 4
        "}\n";                        // 5

    private static (List<string> messages, object? result) RunPatch(string find, string replace, string content)
    {
        var messages = new List<string>();
        var patch =
            "PATCH sample.cs\n" +
            "FIND:\n" + find + "\n" +
            "REPLACE:\n" + replace + "\n";

        var result = PatchEngine.ResolvePatch(
            patch, "sample.cs", "sample.cs",
            content, Encoding.UTF8, fromToolCall: false,
            (msg, _) => messages.Add(msg));
        return (messages, result);
    }

    private static string Join(List<string> messages) => string.Join("", messages);

    // ── Below-threshold rejection ─────────────────────────────────────────────
    // FIND differs from the file's closest line by too much (sim < 0.85).
    // The message must name the reason, the nearest candidate's line (3), and
    // show that line's actual content.
    [Fact]
    public void BelowThreshold_MessageNamesReasonLineAndContent()
    {
        // 199-char find vs. 41-char line 3 → similarity far below 0.85.
        string find = "    public int Answer() { return 42; }" + new string('x', 158);

        var (messages, result) = RunPatch(find, "    public int Answer() { return 7; }", NearMissSource);

        Assert.Null(result);
        string all = Join(messages);

        Assert.Contains("FIND text not found", all);
        // Names the reason — the closest match was below the threshold, not ambiguous.
        Assert.Contains("below the 85% threshold", all);
        Assert.DoesNotContain("ambiguous", all);
        // Names the nearest candidate's line number.
        Assert.Contains("at line 3", all);
        // Shows the nearest candidate's actual content — with the context
        // renderer's visible-whitespace rendering (spaces -> '·').
        Assert.Contains("public·int·Answer()·{·return·42;·}", all);
    }

    // ── Ambiguity-gap rejection ───────────────────────────────────────────────
    // The FIND matches NEITHER line exactly (exact match would take over before
    // fuzzy scoring), but is close to both: line 3 scores 1 - 1/28 = 0.964
    // (one char off), line 4 scores 1 - 2/28 = 0.929 (two chars off), gap
    // 0.036 < 0.05 → ambiguous, not below-threshold.
    [Fact]
    public void AmbiguousGap_MessageNamesBothLinesAndGap()
    {
        string content =
            "public class Sample\n" +   // 1
            "{\n" +                      // 2
            "    public int A() { return 10; }\n" + // 3
            "    public int A() { return 11; }\n" + // 4 (one char off line 3)
            "}\n";                        // 5

        string find = "    public int A() { return 15; }";

        var (messages, result) = RunPatch(find, "    public int A() { return 20; }", content);

        Assert.Null(result);
        string all = Join(messages);

        Assert.Contains("FIND text not found", all);
        // Names the reason — two near-equal candidates, not a missing region.
        Assert.Contains("ambiguous", all);
        Assert.Contains("need a 5% gap", all);
        Assert.DoesNotContain("below the 85% threshold", all);
        // Names the best candidate's line AND the runner-up's line.
        Assert.Contains("line 3", all);
        Assert.Contains("line 4", all);
        // The ambiguity remedy is different from the below-threshold one:
        // extend the FIND, don't re-read the file.
        Assert.Contains("extend the FIND", all);
    }

    // ── Below-threshold remedy: re-read the file ──────────────────────────────
    // The two rejections need opposite responses; the below-threshold message
    // must point the agent at re-reading, not at extending the FIND.
    [Fact]
    public void BelowThreshold_MessageSuggestsReRead()
    {
        string find = "    public int Answer() { return 999999; }";
        var (messages, result) = RunPatch(find, "x", NearMissSource);

        Assert.Null(result);
        string all = Join(messages);
        Assert.Contains("READ the file", all);
        Assert.DoesNotContain("extend the FIND", all);
    }

    // ── Rejection message stays compact ───────────────────────────────────────
    // This lands in an agent's context on every failure — cap the context
    // window so a huge file can't bloat it.
    [Fact]
    public void RejectionMessage_IsCompact()
    {
        string find = "    public int Answer() { return 42; }" + new string('x', 158);
        var (messages, _) = RunPatch(find, "x", NearMissSource);

        string all = Join(messages);
        Assert.True(all.Length < 1500, $"rejection message too long: {all.Length} chars");
    }
}
