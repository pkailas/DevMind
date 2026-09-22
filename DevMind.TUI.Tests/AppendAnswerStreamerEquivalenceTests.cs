// File: AppendAnswerStreamerEquivalenceTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Pins the equivalence property TuiAgenticHost.AppendAnswer depends on: a
// model-authored answer block (task_done summary / ask_caller questions) is
// run through a CodeBlockStreamer in ONE Feed call plus a single Flush — the
// one-shot shape of the answer path — rather than per SSE token. For that to
// give the answer the same rendering as streamed prose, the whole-block feed
// must emit the SAME per-line prose sequence (and the same code sequence) as
// feeding the text character by character.
//
// These tests run at the CodeBlockStreamer level (TuiAgenticHost itself needs
// a live Terminal.Gui view) and pin exactly the property the fix rides on:
//   * one-shot Feed + Flush == character-by-character Feed + Flush, for a
//     multi-line markdown block with a single trailing newline;
//   * a fenced code section inside the block routes to the code callback, the
//     surrounding prose to the prose callback, and the ``` fence lines to
//     neither.

using System.Collections.Generic;
using System.Linq;
using DevMind;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class AppendAnswerStreamerEquivalenceTests
    {
        // Wraps one CodeBlockStreamer: Feed/Flush delegate to it; Prose/Code
        // record every callback emission in order.
        private sealed class Recorder
        {
            private readonly CodeBlockStreamer _streamer;
            public readonly List<string> Prose = new List<string>();
            public readonly List<(string text, string lang)> Code = new List<(string, string)>();

            public Recorder()
            {
                _streamer = new CodeBlockStreamer(
                    prose: t => Prose.Add(t),
                    code: (text, lang) => Code.Add((text, lang)));
            }

            public void Feed(string text) => _streamer.Feed(text);
            public void Flush() => _streamer.Flush();
        }

        // A realistic final answer: heading, bold, inline code, and a trailing
        // single "\n" (the executor's normalisation). One-shot Feed+Flush must
        // emit the identical per-line prose AND code sequences as feeding the
        // same text character by character.
        [Fact]
        public void OneShotFeedPlusFlush_MatchesCharacterByCharacter()
        {
            const string block =
                "## Summary\n" +
                "Fixed **four** files via `patch`.\n" +
                "```\n" +
                "dotnet build\n" +
                "```\n" +
                "All **green** now.\n";

            var oneShot = new Recorder();
            oneShot.Feed(block);   // the whole block in ONE call — the answer shape
            oneShot.Flush();

            var charByChar = new Recorder();
            foreach (char c in block) charByChar.Feed(c.ToString());
            charByChar.Flush();

            // Prose: same lines, same order, newlines included.
            Assert.Equal(charByChar.Prose, oneShot.Prose);

            // Code: same highlighted lines with the same (absent) language.
            Assert.Equal(charByChar.Code, oneShot.Code);

            // Sanity: the property is non-trivial — prose really was split into
            // per-line emissions and the code was routed out of the prose stream.
            Assert.Equal(new[] { "## Summary\n", "Fixed **four** files via `patch`.\n", "All **green** now.\n" },
                oneShot.Prose);
            Assert.Equal(new[] { ("dotnet build\n", "") }, oneShot.Code);
        }

        // An answer block containing a fenced code section: fenced lines go to
        // the code callback, surrounding prose to the prose callback, and the
        // ``` marker lines reach neither. Feeded one-shot — the answer shape.
        [Fact]
        public void FencedSection_RoutesCodeAndProseSeparately_FencesEmitNowhere()
        {
            var rec = new Recorder();
            rec.Feed("Intro line.\n```csharp\nint x = 1;\n```\nClosing line.\n");

            // Prose: only the two surrounding lines.
            Assert.Equal(2, rec.Prose.Count);
            Assert.Equal("Intro line.\n", rec.Prose[0]);
            Assert.Equal("Closing line.\n", rec.Prose[1]);

            // Code: the single fenced line, with its language tag.
            Assert.Single(rec.Code);
            Assert.Equal(("int x = 1;\n", "csharp"), rec.Code[0]);

            // The fence markers reach neither callback — not as prose text,
            // not as code text, not as a language tag.
            foreach (var p in rec.Prose)
            {
                Assert.DoesNotContain("```", p);
                Assert.DoesNotContain("csharp", p);
            }
            Assert.DoesNotContain("```", string.Concat(rec.Code.Select(c => c.text)));
            Assert.DoesNotContain("```", string.Concat(rec.Code.Select(c => c.lang)));
        }

        // A trailing partial line (no terminating newline) is released by
        // Flush in the one-shot shape too — so an answer whose last line
        // lacks the normalisation newline is not lost.
        [Fact]
        public void OneShotFeed_TrailingPartialLineReleasedByFlush()
        {
            var rec = new Recorder();
            rec.Feed("first line\nsecond line");
            rec.Flush();

            Assert.Equal(2, rec.Prose.Count);
            Assert.Equal("first line\n", rec.Prose[0]);
            Assert.Equal("second line", rec.Prose[1]);
        }
    }
}
