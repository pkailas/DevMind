// File: SelfReportStrengthTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-31: the detector says HOW an answer admitted unfinished work — an explicit INCOMPLETE:
// marker (Strong) or a phrase-list hit (Weak) — so the job can weigh a weak hit against the
// harness's own verification. A marker anywhere outranks a phrase line above it.

using Xunit;

namespace DevMind.Core.Tests
{
    public class SelfReportStrengthTests
    {
        private static SelfReportedIncomplete Detect(string answer) => SelfReportedIncompleteDetector.Detect(answer);

        [Theory]
        [InlineData("Done.\nINCOMPLETE: tests not run", "INCOMPLETE: tests not run")]
        [InlineData("Done.\n- INCOMPLETE: the migration", "- INCOMPLETE: the migration")]
        [InlineData("**INCOMPLETE:**\n- wire the TUI hook", "- wire the TUI hook")]
        public void Marker_IsStrong(string answer, string line)
        {
            var r = Detect(answer);
            Assert.Equal(SelfReportStrength.Strong, r.Strength);
            Assert.True(r.Detected);
            Assert.Equal(line, r.Line);
        }

        [Theory]
        [InlineData("The core defect is NOT fixed.")]
        [InlineData("The full solution suite was not run by me — the harness verifies it.")]
        [InlineData("- Did not run the TUI; did not commit.")]
        public void PhraseOnly_IsWeak(string answer)
        {
            var r = Detect("Patched it.\n" + answer);
            Assert.Equal(SelfReportStrength.Weak, r.Strength);
            Assert.True(r.Detected);
            Assert.Equal(answer, r.Line);
        }

        [Theory]
        [InlineData("All done.\nINCOMPLETE: none")]
        [InlineData("All done.\nINCOMPLETE: n/a.")]
        [InlineData("Added the handler; build 0/0; suite green.")]
        [InlineData("")]
        public void NothingUnfinished_IsNone(string answer)
        {
            var r = Detect(answer);
            Assert.Equal(SelfReportStrength.None, r.Strength);
            Assert.False(r.Detected);
        }

        [Fact]
        public void APhraseLineAboveAMarker_ReportsTheMarker()
        {
            var r = Detect("- Did not run the TUI.\nINCOMPLETE: the migration is not written");
            Assert.Equal(SelfReportStrength.Strong, r.Strength);
            Assert.Equal("INCOMPLETE: the migration is not written", r.Line);
        }

        [Fact]
        public void TwoPhraseLines_ReportTheFirst()
        {
            var r = Detect("Did not run the TUI.\nThe bug is not fixed.");
            Assert.Equal(SelfReportStrength.Weak, r.Strength);
            Assert.Equal("Did not run the TUI.", r.Line);
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
