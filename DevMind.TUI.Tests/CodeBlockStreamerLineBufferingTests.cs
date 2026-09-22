// File: CodeBlockStreamerLineBufferingTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Pins the per-COMPLETED-LINE prose buffering in DevMind.TUI/CodeBlockStreamer.cs.
// The whole point of the buffering: an inline marker pair (**bold**, `code`) can
// arrive split across two Feed calls (one per SSE token), and the prose callback
// must still receive the line as ONE complete unit so the downstream renderer can
// consume the markers. These tests are the regression the change exists for.
//
// Also pins the two non-negotiable invariants that must NOT change:
//   * Flush() releases a trailing partial line (no newline) — nothing is lost.
//   * Fenced code still routes to the CODE callback and never the prose callback,
//     so inline styling cannot leak into code blocks.

using System;
using System.Collections.Generic;
using DevMind;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class CodeBlockStreamerLineBufferingTests
    {
        private sealed class Recorder
        {
            public readonly List<string> Prose = new List<string>();
            public readonly List<(string text, string lang)> Code = new List<(string, string)>();

            public CodeBlockStreamer Streamer() =>
                new CodeBlockStreamer(
                    prose: t => Prose.Add(t),
                    code: (text, lang) => Code.Add((text, lang)));
        }

        // THE regression that motivates the job: a **bold** pair split across two
        // Feed calls ("**bo" then "ld**"). Without line buffering the prose callback
        // would fire twice with a marker fragment each — unstyleable. With buffering
        // it fires ONCE, with the complete line (incl. newline), which the renderer
        // then turns into a single Bold run.
        [Fact]
        public void BoldPairSplitAcrossTwoFeeds_EmitsOneCompleteLineStyleableLine()
        {
            var rec = new Recorder();
            var streamer = rec.Streamer();

            streamer.Feed("**bo");      // first SSE token: opening marker + partial word
            Assert.Empty(rec.Prose);     // nothing emitted yet — the line is incomplete

            streamer.Feed("ld**\n");     // closing marker + line end
            streamer.Flush();

            // Exactly one prose emission, and it is the WHOLE line including its newline.
            Assert.Single(rec.Prose);
            Assert.Equal("**bold**\n", rec.Prose[0]);

            // And that single emission is styleable: the renderer consumes both markers.
            var runs = MarkdownInlineRenderer.Render(rec.Prose[0].TrimEnd('\n'));
            Assert.Single(runs);
            Assert.Equal(InlineTextStyle.Bold, runs[0].Style);
            Assert.Equal("bold", runs[0].Text);
        }

        // Same guarantee for a heading whose marker and text arrive separately.
        [Fact]
        public void HeadingSplitAcrossTwoFeeds_EmitsOneCompleteLine()
        {
            var rec = new Recorder();
            var streamer = rec.Streamer();

            streamer.Feed("# ");
            Assert.Empty(rec.Prose);

            streamer.Feed("Title\n");
            streamer.Flush();

            Assert.Single(rec.Prose);
            Assert.Equal("# Title\n", rec.Prose[0]);

            var runs = MarkdownInlineRenderer.Render(rec.Prose[0].TrimEnd('\n'));
            Assert.Single(runs);
            Assert.Equal(InlineTextStyle.Heading, runs[0].Style);
            Assert.Equal("Title", runs[0].Text);
        }

        // A trailing partial line (no terminating newline) must be released by Flush().
        [Fact]
        public void Flush_ReleasesTrailingPartialLineWithoutNewline()
        {
            var rec = new Recorder();
            var streamer = rec.Streamer();

            streamer.Feed("still typing here");   // no newline
            Assert.Empty(rec.Prose);

            streamer.Flush();

            Assert.Single(rec.Prose);
            Assert.Equal("still typing here", rec.Prose[0]);   // no trailing newline
        }

        // A completed line followed by a partial tail: BOTH come out, in order,
        // nothing lost. The completed line keeps its newline; the tail does not.
        [Fact]
        public void Flush_CompletedLinePlusTrailingTail_BothReleasedInOrder()
        {
            var rec = new Recorder();
            var streamer = rec.Streamer();

            streamer.Feed("line one\nstill typing");
            streamer.Flush();

            Assert.Equal(2, rec.Prose.Count);
            Assert.Equal("line one\n", rec.Prose[0]);
            Assert.Equal("still typing", rec.Prose[1]);
        }

        // Fenced code must keep routing to the CODE callback (with its language) and
        // never reach the prose callback — so inline styling cannot leak into code.
        [Fact]
        public void FencedCode_RoutesToCodeCallbackOnly_NotProse()
        {
            var rec = new Recorder();
            var streamer = rec.Streamer();

            streamer.Feed("before\n```csharp\nint x = 1;\nafter\n```\nafter prose\n");
            streamer.Flush();

            // Code lines go to the code callback, with the language tag preserved.
            Assert.Equal(2, rec.Code.Count);
            Assert.Equal(("int x = 1;\n", "csharp"), rec.Code[0]);
            Assert.Equal(("after\n", "csharp"), rec.Code[1]);

            // No code content leaks into prose.
            foreach (var p in rec.Prose)
            {
                Assert.DoesNotContain("int x = 1;", p);
                Assert.DoesNotContain("after\n", p);
            }

            // The prose around the block is intact.
            Assert.Equal(2, rec.Prose.Count);
            Assert.Equal("before\n", rec.Prose[0]);
            Assert.Equal("after prose\n", rec.Prose[1]);
        }
    }
}
