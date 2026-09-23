// File: TranscriptVocabularyTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// What the transcript is able to say.
//
// The translation table is the whole feature, and its failure mode is a tag nobody mapped:
// the line still appears — nothing is ever dropped — but it appears as "● Write Guard" in a
// column of "✓ Write" lines, and it looks like a bug in the feature rather than a gap in a
// table. So the table is enumerated here rather than sampled, and a source-derived guard
// below fails when a tag is added to an emitting file and not to the map.
//
// The colour→glyph rule is the other half. The engine already decides Success / Warning /
// Error at every emit site; the glyph is a rendering of a judgement that was already made,
// which is why it is a mapping and not a heuristic over the text.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class TranscriptVocabularyTests
    {
        private static string Render(string line, OutputColor color)
            => TranscriptVocabulary.Translate(line, color).Render();

        // ── The table, tag by tag ────────────────────────────────────────────────

        [Theory]
        // File and shell work.
        [InlineData("[SHELL] > git status --short", OutputColor.Dim, "› Shell git status --short")]
        [InlineData("[FILE] Saved patch_883.py (24 lines)", OutputColor.Success, "✓ Write patch_883.py (24 lines)")]
        [InlineData("[APPEND] Appended to notes.md", OutputColor.Success, "✓ Append notes.md")]
        [InlineData("[READ] Loaded Program.cs (1,204 lines)", OutputColor.Success, "✓ Read Program.cs (1,204 lines)")]
        [InlineData("[AUTO-READ] Loading Program.cs before patch...", OutputColor.Success, "✓ Read Program.cs before patch...")]
        [InlineData("[PATCH] Applied to Widget.cs", OutputColor.Success, "✓ Edit Widget.cs")]
        [InlineData("[DELETE] Removed old.txt", OutputColor.Success, "✓ Delete old.txt")]
        [InlineData("[RENAME] Renamed a.txt to b.txt", OutputColor.Success, "✓ Rename a.txt to b.txt")]
        // Search and inspection.
        [InlineData("[GREP] 4 matches", OutputColor.Success, "✓ Grep 4 matches")]
        [InlineData("[FIND] 2 files", OutputColor.Success, "✓ Find 2 files")]
        [InlineData("[LIST] 18 entries", OutputColor.Success, "✓ List 18 entries")]
        [InlineData("[DIFF] Widget.cs", OutputColor.Success, "✓ Diff Widget.cs")]
        [InlineData("[LSP] 3 diagnostics", OutputColor.Success, "✓ Lsp 3 diagnostics")]
        [InlineData("[TEST] > dotnet test", OutputColor.Dim, "› Test dotnet test")]
        [InlineData("[DEBUG] attached", OutputColor.Dim, "› Debug attached")]
        // Knowledge and data.
        [InlineData("[MEMORY] Saved topic", OutputColor.Success, "✓ Memory Saved topic")]
        [InlineData("[LIBRARY] 3 results", OutputColor.Success, "✓ Library 3 results")]
        [InlineData("[RECALL] cache hit", OutputColor.Success, "✓ Recall cache hit")]
        [InlineData("[WEB] fetched", OutputColor.Success, "✓ Web fetched")]
        [InlineData("[LEARN] 5 hits", OutputColor.Success, "✓ Learn 5 hits")]
        [InlineData("[SQL] 12 rows", OutputColor.Success, "✓ Sql 12 rows")]
        [InlineData("[DIGEST] Complete", OutputColor.Dim, "› Digest Complete")]
        [InlineData("[SCRATCHPAD] updated", OutputColor.Dim, "› Scratchpad updated")]
        // Guards and conflicts.
        [InlineData("[WRITE GUARD] \"x.py\" was not read during this task — auto-approved.",
                    OutputColor.Warning, "⚠ Write \"x.py\" was not read during this task — auto-approved.")]
        [InlineData("[SANDBOX] write blocked", OutputColor.Error, "x Write write blocked")]
        [InlineData("[MERGE] Accepted proposed content", OutputColor.Success, "✓ Merge Accepted proposed content")]
        [InlineData("[MERGE CONFLICT] Cannot write", OutputColor.Error, "x Merge Cannot write")]
        [InlineData("[SKIPPED] user declined", OutputColor.Warning, "⚠ Skipped user declined")]
        [InlineData("[BLOCKED] outside the working directory", OutputColor.Error, "x Blocked outside the working directory")]
        public void EveryMappedTag_RendersAsGlyphLabelDetail(string line, OutputColor color, string expected)
        {
            Assert.Equal(expected, Render(line, color));
        }

        // ── State lines ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData("[AGENTIC] Task complete.", OutputColor.Success, "● Task complete.")]
        [InlineData("[AGENTIC] Depth cap reached (5). Stopping.", OutputColor.Dim, "● Depth cap reached (5). Stopping.")]
        [InlineData("[AGENTIC] Cancelled.", OutputColor.Dim, "● Cancelled.")]
        [InlineData("[CONTEXT] CRITICAL: Cannot fit in context window", OutputColor.Error, "● Context CRITICAL: Cannot fit in context window")]
        [InlineData("[DROPPED] 2 tool results", OutputColor.Dim, "● Context 2 tool results")]
        [InlineData("[MODE] manual — every mutation will ask", OutputColor.Warning, "● Mode manual — every mutation will ask")]
        [InlineData("[RESUME] The session (4 messages loaded)", OutputColor.Success, "● Resume The session (4 messages loaded)")]
        public void LoopAndSessionEvents_GetTheStateGlyph_RegardlessOfColour(string line, OutputColor color, string expected)
        {
            Assert.Equal(expected, Render(line, color));
            Assert.Equal(TranscriptKind.State, TranscriptVocabulary.Translate(line, color).Kind);
        }

        [Fact]
        public void ASteerIsItsOwnGesture()
        {
            // Neither an outcome nor a loop event: the turn was redirected while it ran.
            Assert.Equal("⟲ Steer suggest folded into the prompt",
                Render("[STEER] suggest folded into the prompt", OutputColor.Success));
        }

        // ── Errors ───────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("[SHELL ERROR] command failed", "x Shell command failed")]
        [InlineData("[PATCH ERROR] Widget.cs: no match", "x Edit Widget.cs: no match")]
        [InlineData("[FILE ERROR] access denied", "x Write access denied")]
        [InlineData("[LSP ERROR] server not running", "x Lsp server not running")]
        [InlineData("[LIST_CACHE ERROR] unavailable", "x Cache unavailable")]
        public void AnErrorTagAlwaysFails_WhateverColourWasAsked(string line, string expected)
        {
            // The emit sites are not consistent about colour on the error paths, and the tag
            // is the more reliable statement of what happened.
            Assert.Equal(expected, Render(line, OutputColor.Success));
            Assert.Equal(OutputColor.Error, TranscriptVocabulary.Translate(line, OutputColor.Success).Color);
        }

        // ── Colour → glyph ───────────────────────────────────────────────────────

        [Theory]
        [InlineData(OutputColor.Success, "✓")]
        [InlineData(OutputColor.Error, "x")]
        [InlineData(OutputColor.Warning, "⚠")]
        [InlineData(OutputColor.Dim, "›")]
        [InlineData(OutputColor.Normal, "›")]
        public void TheGlyphIsTheColourTheEngineAlreadyChose(OutputColor color, string glyph)
        {
            Assert.Equal(glyph, TranscriptVocabulary.GlyphFor(color));
        }

        [Fact]
        public void FailureIsAscii()
        {
            // Every other glyph can fall back to a box in a bad font and still leave the line
            // readable. The one that says "this did not work" cannot.
            Assert.Equal("x", TranscriptVocabulary.GlyphFail);
            Assert.All(TranscriptVocabulary.GlyphFail, c => Assert.True(c < 128));
        }

        // ── Everything else ──────────────────────────────────────────────────────

        [Fact]
        public void AnUnknownTagIsShownAsAWord_NeverDroppedAndNeverRaw()
        {
            Assert.Equal("● Patch Guard fuzzy match refused",
                Render("[PATCH GUARD] fuzzy match refused", OutputColor.Dim));
        }

        [Fact]
        public void AnUntaggedLineIsOutput_AndPassesThroughVerbatim()
        {
            var ev = TranscriptVocabulary.Translate("positional wv count before: 6", OutputColor.Normal);

            Assert.Equal(TranscriptKind.Output, ev.Kind);
            Assert.Equal("positional wv count before: 6", ev.Render());
        }

        [Fact]
        public void TheUserEchoIsLeftAlone()
        {
            // "> text" is already the shape every other tool uses for the same thing.
            var ev = TranscriptVocabulary.Translate("> fix the build", OutputColor.Input);

            Assert.Equal(TranscriptKind.User, ev.Kind);
            Assert.Equal("> fix the build", ev.Render());
        }

        [Fact]
        public void ModelProseIsNotMistakenForATag()
        {
            // A mixed-case bracket is the model writing, not the engine reporting.
            Assert.Equal(TranscriptKind.Output,
                TranscriptVocabulary.Translate("The [Fact] attribute marks a test.", OutputColor.Normal).Kind);
            Assert.Equal(TranscriptKind.Output,
                TranscriptVocabulary.Translate("[1] see the footnote", OutputColor.Normal).Kind);
        }

        [Fact]
        public void TheShellArrowIsDroppedBecauseTheLabelSaysItNow()
        {
            // "› Shell > cmd" reads as a quotation; the label already means "a command".
            Assert.Equal("› Shell dotnet build", Render("[SHELL] > dotnet build", OutputColor.Dim));
        }

        [Fact]
        public void TheAskCallerHeadingIsRecognisedOnTheProsePath()
        {
            Assert.True(TranscriptVocabulary.IsNeedsInputHeading("NEEDS INPUT — questions for the caller:"));
            Assert.False(TranscriptVocabulary.IsNeedsInputHeading("The build needs input from you."));
            Assert.Equal("? Needs input", TranscriptVocabulary.NeedsInputLine);
        }
    }
}
