// File: SelfReportedIncompleteDetectorTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Reading the agent's own verdict on its own work.
//
// The positive cases below are quotes. Every one of them is from a job that ended
// state=done with incomplete_reasons=null while saying, in the final message the caller was
// handed, that the work was not finished. Five of them in two days — which is why the list
// is blunt rather than clever: a false positive costs one extra look at an answer somebody
// was going to read anyway, and a false negative is what produced the five.
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

        [Theory]
        // job-1670 (LT-04)
        [InlineData("The core defect is NOT fixed — the caller must not report LT-04 as fixed.")]
        // job-1668
        [InlineData("Summary: no test run was performed, so the red run was NOT demonstrated.")]
        [InlineData("Resume by fixing the 7 CS1503 errors; the test project did not run.")]
        // job-1667 / job-1652 / job-1654
        [InlineData("NOT DONE — caller must finish the remaining steps.")]
        [InlineData("The fix is not verified; I could not finish the last step.")]
        [InlineData("Three tests are still failing after the change.")]
        [InlineData("I hit the iteration cap before the last file was patched.")]
        [InlineData("The integration suite was not run.")]
        [InlineData("Remaining work: wire the handler and add a test.")]
        public void AFinalAnswerThatSaysItIsUnfinished_IsDetected(string answer)
        {
            Assert.True(Fires(answer), $"missed: {answer}");
        }

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
            // No list under the header, but the next line reports unfinished work on its own.
            Assert.True(Fires("INCOMPLETE:\nThe core defect is NOT fixed."));
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
        public void AMarkerThatDeclaresNothingUnfinished_IsNotIncomplete(string line)
        {
            Assert.False(Fires("Patched the parser and rebuilt clean. Suite green.\n" + line), $"false positive: {line}");
        }

        [Theory]
        [InlineData("INCOMPLETE: none of the tests ran")]
        [InlineData("INCOMPLETE: nothing was verified")]
        [InlineData("INCOMPLETE: n/a for build, but the tests are red")]
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
        public void TheReportedLineIsTheOneThatSaidSo()
        {
            var result = SelfReportedIncompleteDetector.Detect(
                "Built cleanly.\nThe core defect is NOT fixed.\nMore prose after.");

            Assert.True(result.Detected);
            Assert.Equal("The core defect is NOT fixed.", result.Line);
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
        public void APhraseInsideAFencedBlockIsQuoted_NotClaimed()
        {
            // A pasted log or diff is evidence the agent is showing, not a statement about
            // its own work — and test output in particular is full of "did not run".
            string answer =
                "Everything is green now.\n" +
                "```\n" +
                "  Failed: SomeTest — the fix is not verified\n" +
                "  did not run: 0\n" +
                "```\n" +
                "Build 0 errors / 0 warnings.";

            Assert.False(Fires(answer));
        }

        [Fact]
        public void APhraseAfterAFenceClosesStillCounts()
        {
            // The fence toggles; it does not swallow the rest of the answer.
            string answer =
                "```\n" +
                "some log output\n" +
                "```\n" +
                "The core defect is NOT fixed.";

            Assert.True(Fires(answer));
        }

        [Fact]
        public void TildeFencesCountAsFencesToo()
        {
            string answer = "~~~\nthe caller must do something\n~~~\nAll green.";

            Assert.False(Fires(answer));
        }

        [Fact]
        public void MatchingIgnoresCase()
        {
            Assert.True(Fires("the core defect is not fixed"));
            Assert.True(Fires("THE CORE DEFECT IS NOT FIXED"));
        }

        [Fact]
        public void EveryDocumentedPhraseActuallyFires()
        {
            // The list is the documentation; a phrase in it that does not work would be a
            // promise this makes and does not keep.
            foreach (string phrase in SelfReportedIncompleteDetector.Phrases)
                Assert.True(Fires($"Note: {phrase} yet."), $"documented phrase never fires: {phrase}");
        }
    }
}
