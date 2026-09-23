// File: HeadlessTagsAreNotTranslatedTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The tags are a contract everywhere except the TUI.
//
// The TUI now translates [WRITE GUARD] into "⚠ Write …" at its own door, because it has a
// screen and a glyph is cheaper to read than a tag. Nothing else does. An operator reading a
// piped CLI session or a headless transcript_tail is matching on those tags, by eye or by
// grep, and a glyph would be both unreadable in a log file and a silent break of whatever
// they had written to watch for one.
//
// The structural guard in DevMind.Cli.Tests says the translator is not even reachable from
// the CLI assembly. This says the stronger thing that matters in practice: drive the headless
// host and watch the raw tag come out of its sink.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    public class HeadlessTagsAreNotTranslatedTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "devmind-tags-" + Guid.NewGuid().ToString("N"));

        public HeadlessTagsAreNotTranslatedTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        }

        // The guard is protected and fires from inside three write paths; reaching it through
        // one of them would be testing the write path, not the wording.
        private sealed class GuardProbe : BufferedAgenticHost
        {
            public GuardProbe(string dir, Action<string, OutputColor> sink) : base(dir, null, sink) { }

            public Task<bool> Guard(string fileName) => ConfirmUnreadFileWriteAsync(fileName);
        }

        [Fact]
        public async Task TheWriteGuardReachesAHeadlessSinkAsARawTag()
        {
            var written = new List<string>();
            var host = new GuardProbe(_dir, (text, _) => written.Add(text));

            await host.Guard("notes.txt");

            string all = string.Concat(written);

            Assert.Contains("[WRITE GUARD]", all, StringComparison.Ordinal);
            Assert.DoesNotContain("⚠", all, StringComparison.Ordinal);   // ⚠
            Assert.DoesNotContain("✓", all, StringComparison.Ordinal);   // ✓
        }

        [Fact]
        public async Task TheFileTagReachesAHeadlessSinkAsARawTag()
        {
            var written = new List<string>();
            var host = new BufferedAgenticHost(_dir, outputSink: (text, _) => written.Add(text));

            await ((IAgenticHost)host).SaveFileAsync("notes.txt", "hello", fromToolCall: true);

            string all = string.Concat(written);

            Assert.Contains("[FILE]", all, StringComparison.Ordinal);
            // "✓ Write notes.txt" is the TUI's sentence, and only the TUI's.
            Assert.DoesNotContain("Write notes.txt", all, StringComparison.Ordinal);
        }

        [Fact]
        public void TheTranslatorIsNotAvailableToTheEngineAtAll()
        {
            // It lives in DevMind.TUI on purpose. Core cannot call what it cannot see, so no
            // future edit inside the engine can translate a tag by accident.
            Assert.Null(typeof(BufferedAgenticHost).Assembly.GetType("DevMind.TranscriptVocabulary", false));
        }
    }
}
