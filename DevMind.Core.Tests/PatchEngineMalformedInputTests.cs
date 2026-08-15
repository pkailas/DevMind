// File: PatchEngineMalformedInputTests.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for the PatchEngine.ParsePatchBlocks crash:
//
//     [PATCH] Error: length ('-57') must be a non-negative value. (Parameter 'length')
//
// Site: PatchEngine.cs line 150 (pre-fix):
//     input.Substring(findContentStart, replaceIdx - findContentStart)
//
// Root cause: findContentStart is the FIRST LINE of the FIND content (the line
// after the FIND: marker), while replaceIdx comes from
// input.IndexOf("REPLACE:", findIdx). If "REPLACE:" appears BEFORE that first
// content line — i.e. on the SAME line as the FIND: marker — the computed
// length goes negative and Substring throws ArgumentOutOfRangeException.
//
// The throw surfaced to the agent as a generic FIND-not-found / patch failure,
// which sent it into a retry loop on input that can never parse. The parser
// must reject the block with a message that names the actual defect instead.
//
// NOTE: this file intentionally contains the literal marker text in strings;
// build it via concatenation where needed so the file itself never presents a
// malformed PATCH block to tooling that parses it.

using System.Text;
using DevMind;
using Xunit;

namespace DevMind.Core.Tests;

public sealed class PatchEngineMalformedInputTests
{
    private const string FindMarker = "FIND";
    private const string ReplaceMarker = "REPLACE";

    private sealed class Reporter
    {
        public readonly List<string> Messages = new List<string>();
        public void Report(string message, OutputColor color) => Messages.Add(message);
        public Action<string, OutputColor> Handler => Report;
    }

    // ── The crash: both markers on one line → negative Substring length ─────

    [Fact]
    public void FindAndReplaceOnSameLine_ReportsClearError_DoesNotThrow()
    {
        // "FIND:oldREPLACE:new" — the REPLACE: marker sits on the same line as
        // the FIND: marker, before the newline that opens the FIND content.
        // Pre-fix this made replaceIdx < findContentStart and threw
        // ArgumentOutOfRangeException from Substring.
        string input = "PATCH victim.txt\n"
            + FindMarker + ":old" + ReplaceMarker + ":new\n"
            + "END_PATCH";

        var reporter = new Reporter();

        List<(string, string)> results = null!;
        var ex = Record.Exception(() => results = PatchEngine.ParsePatchBlocks(input, fromToolCall: true, reporter.Handler));

        Assert.Null(ex);                       // must NOT throw ArgumentOutOfRangeException
        Assert.Empty(results);                 // nothing parseable in a malformed block
        Assert.NotEmpty(reporter.Messages);
        Assert.Contains(reporter.Messages,
            m => m.Contains("Malformed") && m.Contains(ReplaceMarker + ":") && m.Contains("same line"));
    }

    [Fact]
    public void FindAndReplaceOnSameLine_ResolvePatch_ReturnsNullWithSpecificMessage()
    {
        // Drive the same malformed block through the production path.
        string input = "PATCH victim.txt\n"
            + FindMarker + ":old" + ReplaceMarker + ":new\n"
            + "END_PATCH";
        var reporter = new Reporter();

        var result = PatchEngine.ResolvePatch(input, "victim.txt", "victim.txt",
            "old\ntail\n", Encoding.UTF8, fromToolCall: true, reporter.Handler);

        Assert.Null(result);
        Assert.Contains(reporter.Messages,
            m => m.Contains("Malformed") && m.Contains("same line"));
    }

    [Fact]
    public void FindMarkerWithNoNewlineAfter_ReportsClearError_DoesNotThrow()
    {
        // FIND: marker is the last token in the input — no newline after it,
        // so findNl < 0: the marker line has no content line at all.
        string input = "PATCH victim.txt\n"
            + FindMarker + ":old" + ReplaceMarker + ":new";

        var reporter = new Reporter();

        List<(string, string)> results = null!;
        var ex = Record.Exception(() => results = PatchEngine.ParsePatchBlocks(input, fromToolCall: true, reporter.Handler));

        Assert.Null(ex);
        Assert.Empty(results);
        Assert.Contains(reporter.Messages,
            m => m.Contains("Malformed") && m.Contains("no newline after the " + FindMarker + ": marker"));
    }

    [Fact]
    public void WellFormedBlock_StillParses_AfterGuard()
    {
        // The guard must not reject ordinary input: FIND: alone on its line,
        // then content, then REPLACE: on its own line.
        string input = "PATCH victim.txt\n"
            + FindMarker + ":\nold\n"
            + ReplaceMarker + ":\nnew\n"
            + "END_PATCH";
        var reporter = new Reporter();

        var results = PatchEngine.ParsePatchBlocks(input, fromToolCall: true, reporter.Handler);

        Assert.Single(results);
        Assert.Equal(("old", "new"), results[0]);
        Assert.Empty(reporter.Messages);
    }

    [Fact]
    public void FindWithInlineContentBeforeNewline_IsContent_NotMarkerCollision()
    {
        // Sanity: content on the marker line that does NOT contain a marker is
        // ordinary text of the first content line (the marker search starts at
        // FIND:, and the content starts on the following line) — parsing must
        // behave exactly as before the fix.
        string input = "PATCH victim.txt\n"
            + FindMarker + ":\nsome old text\n"
            + ReplaceMarker + ":\nsome new text\n"
            + "END_PATCH";
        var reporter = new Reporter();

        var results = PatchEngine.ParsePatchBlocks(input, fromToolCall: true, reporter.Handler);

        Assert.Single(results);
        Assert.Equal(("some old text", "some new text"), results[0]);
        Assert.Empty(reporter.Messages);
    }

    // ── Design question: markers embedded in the REPLACE payload ─────────────
    //
    // The marker-based text PATCH format genuinely CANNOT express a literal
    // "FIND:" or "REPLACE:" inside a payload: the parser scans for the next
    // marker by plain substring search. A "FIND:" inside a REPLACE payload is
    // treated as the start of the next pair (truncating the payload); an
    // "END_PATCH" line is dropped by StripHallucinatedTerminators. This is a
    // known limitation, not a crash: it must not throw, and the structured
    // tool-call path (ResolvePairs) sidesteps it entirely by passing find/
    // replace verbatim. The tests below PIN that behavior so it cannot
    // regress into an exception.

    [Fact]
    public void ReplacePayloadContainingFindMarker_IsTreatedAsNextPair_DoesNotThrow()
    {
        // The agent intended one pair whose replacement contains "FIND:next".
        // The parser splits at that marker: the payload is truncated to "first"
        // and the "FIND:next" line starts a second (unpaired) block that is
        // discarded. Documented limitation — must not throw.
        string input = "PATCH victim.txt\n"
            + FindMarker + ":\nold\n"
            + ReplaceMarker + ":\nfirst\n"
            + FindMarker + ":next\n"
            + "END_PATCH";
        var reporter = new Reporter();

        List<(string, string)> results = null!;
        var ex = Record.Exception(() => results = PatchEngine.ParsePatchBlocks(input, fromToolCall: true, reporter.Handler));

        Assert.Null(ex);
        Assert.Single(results);
        Assert.Equal("first", results[0].Item2);   // truncated at the embedded marker
        Assert.Empty(reporter.Messages);
    }

    [Fact]
    public void ReplacePayloadContainingEndPatch_IsStripped_DoesNotThrow()
    {
        // A literal END_PATCH line inside the REPLACE payload is one of the
        // hallucinated terminators the parser strips. Documented behavior —
        // must not throw.
        string input = "PATCH victim.txt\n"
            + FindMarker + ":\nold\n"
            + ReplaceMarker + ":\nfirst line\n"
            + "END_PATCH\n"
            + "last line\n"
            + "END_PATCH";
        var reporter = new Reporter();

        List<(string, string)> results = null!;
        var ex = Record.Exception(() => results = PatchEngine.ParsePatchBlocks(input, fromToolCall: true, reporter.Handler));

        Assert.Null(ex);
        Assert.Single(results);
        Assert.Equal(("old", "first line\nlast line"), results[0]);
        Assert.Empty(reporter.Messages);
    }
}
