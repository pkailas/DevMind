// File: StaleRecallTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for the stale-recall banner (BufferedAgenticHost.BuildStaleRecallNote).
//
// Field evidence (job-1387, Sep 2026): the agent recalled a nearline-cached [READ:Main.vb]
// block that predated two of its own patches, built FIND text from it, and when the patch
// failed to resolve it fell back to a PowerShell line-index rewrite (which also stripped
// the file's UTF-8 BOM). The fix (a) tags recalled read blocks of edited files with a
// [STALE RECALL] banner and (b) makes the subsequent patch-resolve failure name the cause.
//
// These tests cover the pure helper that decides WHICH file a recalled block refers to,
// plus the NearlineCache accessor the mtime check depends on.

using System;
using Xunit;

namespace DevMind.Core.Tests
{
    public class StaleRecallTests
    {
        [Fact]
        public void ExtractReadBlockFileName_FullRead_ReturnsBareName()
        {
            string content = "[READ:Main.vb]\nThe following files have been loaded for context:\n\nMain.vb\n```\n...";
            Assert.Equal("Main.vb", BufferedAgenticHost.ExtractReadBlockFileName(content));
        }

        [Fact]
        public void ExtractReadBlockFileName_RangeRead_StripsRange()
        {
            string content = "[READ:DesktopLink.vb:447-524] (lines 447-524 of 965 total)\n```\n447: ...";
            Assert.Equal("DesktopLink.vb", BufferedAgenticHost.ExtractReadBlockFileName(content));
        }

        [Fact]
        public void ExtractReadBlockFileName_PathInTag_ReturnsBareName()
        {
            string content = "[READ:src/Services/Foo.cs]\n...";
            Assert.Equal("Foo.cs", BufferedAgenticHost.ExtractReadBlockFileName(content));
        }

        [Fact]
        public void ExtractReadBlockFileName_NotAReadBlock_ReturnsNull()
        {
            Assert.Null(BufferedAgenticHost.ExtractReadBlockFileName("Build succeeded.\n    0 Warning(s)"));
            Assert.Null(BufferedAgenticHost.ExtractReadBlockFileName(""));
            Assert.Null(BufferedAgenticHost.ExtractReadBlockFileName(null));
        }

        [Fact]
        public void ExtractReadBlockFileName_IncidentalMentionDeepInContent_ReturnsNull()
        {
            // A [READ: tag buried in a shell transcript is not a read block.
            string content = new string('x', 200) + "\n[READ:Other.cs] something";
            Assert.Null(BufferedAgenticHost.ExtractReadBlockFileName(content));
        }

        [Fact]
        public void StripStatusLines_RemovesHarnessNoise_KeepsProse()
        {
            // The final turn of a thrash-stopped job (job-1384) was exactly this shape.
            string noise = "[CONTEXT] ~60,757 / 262,144 (~23%) [estimated]\n\n[TOOL_USE] Processing tool call(s)...";
            Assert.Equal("", HeadlessAgent.StripStatusLines(noise));

            string mixed = "[AGENTIC] Iteration 40/70\nMain.vb is clean. Now DesktopLink.vb:\n[LLM] reasoning… 30s\n  [CONTEXT] ~1 / 2\nSecond line.";
            Assert.Equal("Main.vb is clean. Now DesktopLink.vb:\nSecond line.", HeadlessAgent.StripStatusLines(mixed));

            Assert.Equal("", HeadlessAgent.StripStatusLines(null));
        }

        [Fact]
        public void NearlineCache_GetCachedAtUtc_KnownKeyReturnsStoreTime_UnknownReturnsNull()
        {
            var cache = new NearlineCache();
            DateTime before = DateTime.UtcNow.AddSeconds(-1);
            cache.Store("tool:1", "[READ:A.cs]\ncontent", "[cached]");
            DateTime? at = cache.GetCachedAtUtc("tool:1");
            Assert.NotNull(at);
            Assert.True(at.Value >= before && at.Value <= DateTime.UtcNow.AddSeconds(1));
            Assert.Null(cache.GetCachedAtUtc("tool:nope"));
            cache.Clear();
        }
    }
}
