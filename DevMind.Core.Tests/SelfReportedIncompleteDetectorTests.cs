// File: SelfReportedIncompleteDetectorTests.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Reading the agent's own verdict on its own work.
//
// v1.1 (H-64): the phrase list is gone, and so are the tests that only asserted it. An
// explicit INCOMPLETE: declaration is the one signal; wording like "not done" or "did not run"
// without it is not a declaration (job-2143).
//
// The negative cases matter just as much. A clean summary must stay clean, or "done" stops
// meaning anything in the other direction and the signal is worth nothing again.

using Xunit;

namespace DevMind.Core.Tests
{
    public class SelfReportedIncompleteDetectorTests
    {
        private static bool Fires(string? answer)
            => SelfReportedIncompleteDetector.Detect(answer!).Detected;

        // ── The evidence ─────────────────────────────────────────────────────────

        [Fact]
        public void TheExplicitConventionIsDetected()
        {
            // The convention the system prompt now asks for. It is a declaration rather than
            // a phrase that happened to appear, so it is matched on its own.
            Assert.True(Fires("Everything else is fine.\nINCOMPLETE: tests not run"));
            Assert.True(Fires("incomplete: the parser still rejects two inputs"));
        }

        // job-1674 ended `done` with five lines like the first one below: v1.0 only matched
        // a line that opened with the bare word, and agents write their lists in markdown.
        [Theory]
        [InlineData("- INCOMPLETE: `AdminInputWidthTests` re-run after the `ElementWith` fix is unexecuted")]
        [InlineData("  - INCOMPLETE: nested bullet")]
        [InlineData("* INCOMPLETE: star bullet")]
        [InlineData("+ INCOMPLETE: plus bullet")]
        [InlineData("1. INCOMPLETE: numbered")]
        [InlineData("12) INCOMPLETE: numbered with a paren")]
        [InlineData("**INCOMPLETE:** bold, colon inside")]
        [InlineData("**INCOMPLETE**: bold, colon outside")]
        [InlineData("- **INCOMPLETE:** bold in a bullet")]
        [InlineData("3. **INCOMPLETE:** bold in a numbered item")]
        [InlineData("__INCOMPLETE:__ underscore bold")]
        [InlineData("_INCOMPLETE:_ italic")]
        [InlineData("> INCOMPLETE: quoted block")]
        [InlineData("## INCOMPLETE: as a heading")]
        [InlineData("\tINCOMPLETE: tab-indented")]
        public void TheExplicitMarkerIsDetectedThroughMarkdownDecoration(string line)
        {
            var result = SelfReportedIncompleteDetector.Detect("Patched three files.\n" + line + "\nMore prose.");

            Assert.True(result.Detected, $"missed: {line}");
            Assert.Equal(line.Trim(), result.Line);
        }

        [Theory]
        // The marker must open the line's content; mid-sentence it is not a declaration.
        [InlineData("The first pass was incomplete: the second one fixed it. All green.")]
        [InlineData("- Replaced the INCOMPLETE: placeholder in the template.")]
        // No colon, no declaration.
        [InlineData("- INCOMPLETE handling of nulls was reworked; suite green.")]
        public void TheMarkerWordAloneIsNotADeclaration(string answer)
        {
            Assert.False(Fires(answer), $"false positive: {answer}");
        }

        [Fact]
        public void ABareMarkerHeaderQuotesTheLineUnderIt()
        {
            // "**INCOMPLETE:**" on its own line is a header over the list that says what is
            // unfinished; that list line is what a reason field should carry.
            var result = SelfReportedIncompleteDetector.Detect(
                "Done with the markup.\n\n**INCOMPLETE:**\n- Full suite not yet executed.\n");

            Assert.True(result.Detected);
            Assert.Equal("- Full suite not yet executed.", result.Line);
        }

        [Fact]
        public void ABareMarkerWithNoListUnderItDeclaresNothing()
        {
            // An empty marker is only a declaration when it heads a list of unfinished items.
            Assert.False(Fires("Done with the markup.\n**INCOMPLETE:**"));
            Assert.False(Fires("INCOMPLETE:\nAll tests pass, build 0 errors / 0 warnings."));
        }

        [Fact]
        public void ABareMarkerDoesNotSwallowTheLineAfterIt()
        {
            // No list under the header, but the next line declares unfinished work on its own.
            Assert.True(Fires("INCOMPLETE:\nINCOMPLETE: the core defect is not fixed."));
        }

        // job-1679 finished its work and wrote "INCOMPLETE: none." — the all-clear, not a
        // declaration of unfinished work.
        [Theory]
        [InlineData("INCOMPLETE: none.")]
        [InlineData("- **INCOMPLETE:** None")]
        [InlineData("INCOMPLETE: n/a")]
        [InlineData("INCOMPLETE: N/A.")]
        [InlineData("INCOMPLETE: na")]
        [InlineData("INCOMPLETE: nothing!")]
        [InlineData("INCOMPLETE: -")]
        [InlineData("INCOMPLETE: —")]
        [InlineData("INCOMPLETE: **none**")]
        [InlineData("1. INCOMPLETE: NONE;")]
        [InlineData("**INCOMPLETE:**\n- none")]
        // H-43: the none-word followed by an explanation (job-2104, job-1850, job-1861).
        [InlineData("INCOMPLETE: none. Only the TotalAgility page changed; other config pages untouched.")]
        [InlineData("- INCOMPLETE: none. The scheduled-task registration is explicitly left to the caller")]
        [InlineData("INCOMPLETE: none. (Full-suite run intentionally delegated to the harness per job rules.)")]
        [InlineData("INCOMPLETE: none (full suite delegated to the harness)")]
        [InlineData("INCOMPLETE: nothing — all items done")]
        [InlineData("INCOMPLETE: Nothing outstanding.")]
        [InlineData("INCOMPLETE: N/A; no follow-ups")]
        public void AMarkerThatDeclaresNothingUnfinished_IsNotIncomplete(string line)
        {
            Assert.False(Fires("Patched the parser and rebuilt clean. Suite green.\n" + line), $"false positive: {line}");
        }

        [Theory]
        [InlineData("INCOMPLETE: none of the tests ran")]
        [InlineData("INCOMPLETE: nothing was verified")]
        [InlineData("INCOMPLETE: n/a for build, but the tests are red")]
        [InlineData("INCOMPLETE: none, but the migration is not written")]
        [InlineData("INCOMPLETE: nothing compiles yet")]
        public void AMarkerThatOnlyStartsWithANoneWord_IsStillIncomplete(string line)
        {
            var result = SelfReportedIncompleteDetector.Detect("Patched the parser.\n" + line);

            Assert.True(result.Detected, $"missed: {line}");
            Assert.Equal(line, result.Line);
        }

        [Fact]
        public void ANoneMarkerDoesNotHideALaterDeclaration()
        {
            Assert.True(Fires("INCOMPLETE: none.\nINCOMPLETE: the migration was not applied"));
        }

        [Fact]
        public void AVeryLongLineIsTrimmedForTheReasonField()
        {
            string answer = "INCOMPLETE: " + new string('x', 500);

            var result = SelfReportedIncompleteDetector.Detect(answer);

            Assert.True(result.Detected);
            Assert.True(result.Line.Length <= SelfReportedIncompleteDetector.MaxLineLength,
                $"line is {result.Line.Length} chars");
            Assert.EndsWith("…", result.Line);
        }

        // ── H-64: only the INCOMPLETE: marker counts ─────────────────────────────

        // job-2143's final line, verbatim. The brief had said "Do not run the service"; the
        // detector cannot see the brief, so wording about skipped steps is not a declaration.
        private const string Job2143Line =
            "- No test projects exist, so per the brief I ran no tests and did not run the service. " +
            "The only verification is the 0/0 rebuild plus read-back of the changed file.";

        [Fact]
        public void Job2143_ForbiddenStepsDescribedInProse_IsNotIncomplete()
        {
            Assert.False(Fires("Removed the v1 mailbox-path code; rebuild 0 errors / 0 warnings.\n" + Job2143Line));
        }

        [Theory]
        [InlineData("The migration step is not done; the brief left it to the caller.")]
        [InlineData("Did not run the TUI; did not commit.")]
        [InlineData("The core defect is NOT fixed.")]
        public void UnfinishedWordingWithoutTheMarker_IsNotIncomplete(string line)
        {
            Assert.False(Fires("Patched the parser.\n" + line), $"false positive: {line}");
        }

        // ── What must stay clean ─────────────────────────────────────────────────

        [Theory]
        [InlineData("All tests pass, build 0 errors / 0 warnings. Suite 1398 green.")]
        [InlineData("Done. Added the handler, added two tests, full rebuild clean.")]
        [InlineData("The change is complete and verified against the live endpoint.")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void AnAnswerThatClaimsNothingUnfinished_IsLeftAlone(string? answer)
        {
            Assert.False(Fires(answer), $"false positive: {answer}");
        }

        [Fact]
        public void AMarkerInsideAFencedBlockIsQuoted_NotClaimed()
        {
            // A pasted log, diff or template is evidence the agent is showing, not a
            // statement about its own work.
            string answer =
                "Everything is green now.\n" +
                "```\n" +
                "+ INCOMPLETE: placeholder line in the report template\n" +
                "```\n" +
                "Build 0 errors / 0 warnings.";

            Assert.False(Fires(answer));
        }

        [Fact]
        public void AMarkerAfterAFenceClosesStillCounts()
        {
            // The fence toggles; it does not swallow the rest of the answer.
            string answer =
                "```\n" +
                "some log output\n" +
                "```\n" +
                "INCOMPLETE: the core defect is not fixed.";

            Assert.True(Fires(answer));
        }

        [Fact]
        public void TildeFencesCountAsFencesToo()
        {
            string answer = "~~~\nINCOMPLETE: quoted from the old log\n~~~\nAll green.";

            Assert.False(Fires(answer));
        }

        // ── v1.3: headers and "caller must" (job-1686) ───────────────────────────

        [Fact]
        public void Job1686_CallerMustKnowHeaderOverACleanAnswer_IsNotIncomplete()
        {
            // job-1686 finished (473 green, build clean) and ended stopped_incomplete only
            // because its notes section was titled "## Caller must know".
            string answer =
                "LT-21 fixed: the tracker is seeded from persisted history at startup.\n\n" +
                "## Test results (observed)\n" +
                "- Service.Tests: 473 passed / 0 failed / 1 skipped.\n\n" +
                "## Caller must know\n" +
                "- SeedAsync was added to the ITargetHealthTracker interface.\n" +
                "- No git commit/push performed, as instructed.";

            Assert.False(Fires(answer));
        }

        [Fact]
        public void TheMarkerInAHeader_StillCounts()
        {
            // The explicit marker is a declaration wherever it opens a line, headers included.
            Assert.True(Fires("All green.\n## INCOMPLETE: the migration was not applied"));
        }

        [Fact]
        public void CallerMust_BehindTheMarker_IsStillIncomplete()
        {
            var result = SelfReportedIncompleteDetector.Detect("Patched the schema.\nINCOMPLETE: caller must run the migration");

            Assert.True(result.Detected);
            Assert.Equal("INCOMPLETE: caller must run the migration", result.Line);
        }

        // ── Marker reporting (was SelfReportStrengthTests, H-31) ──────────────────

        [Theory]
        [InlineData("Done.\nINCOMPLETE: tests not run", "INCOMPLETE: tests not run")]
        [InlineData("Done.\n- INCOMPLETE: the migration", "- INCOMPLETE: the migration")]
        [InlineData("**INCOMPLETE:**\n- wire the TUI hook", "- wire the TUI hook")]
        public void Marker_IsStrong(string answer, string line)
        {
            var r = SelfReportedIncompleteDetector.Detect(answer);
            Assert.True(r.Detected);
            Assert.Equal(line, r.Line);
        }

        [Theory]
        [InlineData("All done.\nINCOMPLETE: none")]
        [InlineData("All done.\nINCOMPLETE: n/a.")]
        [InlineData("Added the handler; build 0/0; suite green.")]
        [InlineData("")]
        public void NothingUnfinished_IsNone(string answer)
        {
            var r = SelfReportedIncompleteDetector.Detect(answer);
            Assert.False(r.Detected);
        }

        [Fact]
        public void APhraseLineAboveAMarker_ReportsTheMarker()
        {
            var r = SelfReportedIncompleteDetector.Detect("- Did not run the TUI.\nINCOMPLETE: the migration is not written");
            Assert.True(r.Detected);
            Assert.Equal("INCOMPLETE: the migration is not written", r.Line);
        }

        [Fact]
        public void TheCompletionReportRule_IsInTheHeadlessAddendum()
        {
            string addendum = HeadlessAgent.BuildHeadlessAddendum(harnessVerifiesTests: false);
            Assert.Contains(HeadlessAgent.CompletionReportRule, addendum);
            Assert.Contains("INCOMPLETE:", HeadlessAgent.CompletionReportRule);
            Assert.Contains("NOT to do are not unfinished work", HeadlessAgent.CompletionReportRule);
        }
    }
}
