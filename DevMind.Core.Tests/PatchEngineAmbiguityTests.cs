// File: PatchEngineAmbiguityTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the actionable ambiguous-FIND diagnostic in PatchEngine.ResolvePairs.
// The old error was a two-line "matched at line X and line Y" that gave the
// agent no way to fix the patch. The new error must:
//   1. Show the surrounding context of BOTH match sites with whitespace made
//      visible (spaces → '·', tabs → '→').
//   2. Explain that normalization collapses ALL whitespace runs (including
//      leading indentation) to a single space, so an EXACT ambiguous match
//      means the two regions differ only in whitespace.
//   3. State that in-block context cannot disambiguate and direct the agent
//      to anchor on lines ABOVE or BELOW the block.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests;

public sealed class AmbiguousFindDiagnosticsTests
{
    // ── WhitespaceOnlyDiff_ShowsBothContexts ──────────────────────────────────
    // "RunTask();" appears at line 6 (12-space indent) and line 12 (8-space
    // indent) in AmbiguousIndentSource. After normalization both sites are
    // identical, so the FIND is ambiguous. The diagnostic must show context
    // around BOTH sites with the whitespace made visible.

    [Fact]
    public void WhitespaceOnlyDiff_ShowsBothContexts()
    {
        var messages = new List<string>();

        var patch =
            "PATCH worker.cs\n" +
            "FIND:\n" +
            "RunTask();\n" +
            "REPLACE:\n" +
            "RunTask2();\n";

        var result = PatchEngine.ResolvePatch(
            patch, "worker.cs", "worker.cs",
            PatchFixtures.AmbiguousIndentSource, Encoding.UTF8, fromToolCall: false,
            (msg, _) => messages.Add(msg));

        Assert.Null(result);
        string msg = messages.Single(m => m.Contains("Ambiguous FIND"));

        // Both match sites are reported by line number.
        Assert.Contains("line 6", msg);
        Assert.Contains("line 12", msg);

        // Surrounding context of EACH match is included (the only non-whitespace
        // difference between the two sites).
        Assert.Contains("SetupAlpha();", msg);
        Assert.Contains("TeardownAlpha();", msg);
        Assert.Contains("SetupBeta();", msg);
        Assert.Contains("TeardownBeta();", msg);

        // Whitespace is made visible: the 12-space indent of line 6 renders as
        // twelve '·' before "RunTask();", and the 8-space indent of line 12
        // renders as eight. Both runs must appear in the message.
        Assert.Contains("············RunTask();", msg);   // 12 ·
        Assert.Contains("········RunTask();", msg);       // 8 ·

        // The diagnostic explains the normalization and states that in-block
        // context cannot disambiguate — the agent must anchor above/below.
        Assert.Contains("WITHIN the block", msg);
        Assert.Contains("ABOVE or BELOW", msg);
    }

    // ── SameText_SameIndent_StillShowsBothContexts ────────────────────────────
    // "return 30;" appears at line 3 (inside GetTimeout) and line 4 (inside
    // GetRetries) in DuplicateSource — identical text AND identical indentation,
    // so the diagnostic cannot point to any whitespace difference. It must
    // still show context around both sites (GetTimeout vs GetRetries) and
    // still give the above/below anchoring advice.

    [Fact]
    public void SameText_SameIndent_StillShowsBothContexts()
    {
        var messages = new List<string>();

        var patch =
            "PATCH config.cs\n" +
            "FIND:\n" +
            "return 30;\n" +
            "REPLACE:\n" +
            "return 60;\n";

        var result = PatchEngine.ResolvePatch(
            patch, "config.cs", "config.cs",
            PatchFixtures.DuplicateSource, Encoding.UTF8, fromToolCall: false,
            (msg, _) => messages.Add(msg));

        Assert.Null(result);
        string msg = messages.Single(m => m.Contains("Ambiguous FIND"));

        // Both distinct anchor methods appear in the context so the agent knows
        // which surrounding line to pull into the FIND.
        Assert.Contains("GetTimeout", msg);
        Assert.Contains("GetRetries", msg);

        // The actionable advice is still present.
        Assert.Contains("ABOVE or BELOW", msg);
    }

    // ── Normalization_CollapsesAllWhitespaceToSingleSpace ─────────────────────
    // Direct unit test on the normalization itself: the finding the diagnostic
    // is built on. Every whitespace run — leading indentation, inter-token
    // spacing, and newlines — collapses to exactly one space. Therefore two
    // regions that differ only in leading indentation normalize identically,
    // which is why an EXACT ambiguous match can only be broken with context
    // ABOVE or BELOW the block.

    [Fact]
    public void Normalization_CollapsesAllWhitespaceToSingleSpace()
    {
        (string a, _) = PatchEngine.NormalizeWithMap("        RunTask();\n");
        (string b, _) = PatchEngine.NormalizeWithMap("    RunTask();\n");

        // Different indentation → identical normalized text (leading and
        // trailing whitespace each collapse to a single space).
        Assert.Equal(a, b);
        Assert.Equal(" RunTask(); ", a);

        // Multi-space and multi-newline runs each collapse to a single space.
        (string c, _) = PatchEngine.NormalizeWithMap("a    b\n\n\n   c");
        Assert.Equal("a b c", c);
    }
}
