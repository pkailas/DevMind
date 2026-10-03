// File: PromptProfileLaunchFlagTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// --prompt <name>: the launch-time route to the same session state /prompt writes.
//
// Two halves, and the second is the one that matters:
//   * TuiOptions.FromArgs records the requested name (and maps "default" to none —
//     the same reserved word /prompt default uses).
//   * Program.ResolvePromptProfile turns that request into a usable profile or
//     null, and EVERY rejection is reported through the warn callback. A launch
//     flag that quietly does nothing is the failure mode these pin out: the
//     operator must learn the value was bad AND see the prompts that do exist.
//
// ResolvePromptProfile takes the profiles directory as a parameter, so these are
// hermetic without touching the environment at all.

using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace DevMind.TUI.Tests
{
    public sealed class PromptProfileLaunchFlagTests : IDisposable
    {
        private readonly string _dir; // stands in for %APPDATA%\devmind\prompts

        public PromptProfileLaunchFlagTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_prompt_flag_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private void Write(string name, string content = "PROFILE TEXT")
            => File.WriteAllText(Path.Combine(_dir, name + ".md"), content);

        // ── Argument parsing ──────────────────────────────────────────────────

        [Fact]
        public void FromArgs_RecordsTheRequestedProfile()
        {
            var opts = TuiOptions.FromArgs(new[] { "--prompt", "rigorous" });

            Assert.Equal("rigorous", opts.ActivePromptProfile);
        }

        [Fact]
        public void FromArgs_DefaultMeansNoProfile()
        {
            Assert.Null(TuiOptions.FromArgs(new[] { "--prompt", "default" }).ActivePromptProfile);
            Assert.Null(TuiOptions.FromArgs(new[] { "--prompt", "DEFAULT" }).ActivePromptProfile);
        }

        [Fact]
        public void WithoutTheFlag_NothingIsActive()
        {
            Assert.Null(new TuiOptions().ActivePromptProfile);
            Assert.Null(TuiOptions.FromArgs(Array.Empty<string>()).ActivePromptProfile);
        }

        // ── Resolution: honoured or warned, never silent ──────────────────────

        [Fact]
        public void ARealProfile_ResolvesToItsCanonicalSpelling()
        {
            Write("Rigorous-H3");

            var warnings = new List<string>();
            string resolved = Program.ResolvePromptProfile("rigorous-h3", _dir, warnings.Add);

            Assert.Equal("Rigorous-H3", resolved);
            Assert.Empty(warnings);
        }

        [Fact]
        public void NoRequestedProfile_ResolvesToNothingSilently()
        {
            var warnings = new List<string>();

            Assert.Null(Program.ResolvePromptProfile(null, _dir, warnings.Add));
            Assert.Empty(warnings); // nothing was asked for — nothing to report
        }

        // The three ways the flag can go wrong. Each must (a) fall back to the
        // default chain (null) and (b) warn, naming the bad value. A silent no-op
        // here would leave the operator believing the session runs on a persona it
        // does not.
        [Fact]
        public void AnInvalidName_FallsBack_AndNamesTheRule()
        {
            Write("rigorous");
            var warnings = new List<string>();

            Assert.Null(Program.ResolvePromptProfile("../evil", _dir, warnings.Add));

            string warning = Assert.Single(warnings);
            Assert.Contains("../evil", warning, StringComparison.Ordinal);
            Assert.Contains("default chain", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rigorous", warning, StringComparison.Ordinal); // what DOES exist
        }

        [Fact]
        public void AnUnknownName_FallsBack_AndListsTheSavedPrompts()
        {
            Write("rigorous");
            Write("terse");
            var warnings = new List<string>();

            Assert.Null(Program.ResolvePromptProfile("rigourous", _dir, warnings.Add)); // typo

            string warning = Assert.Single(warnings);
            Assert.Contains("rigourous", warning, StringComparison.Ordinal);
            Assert.Contains("not found", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rigorous", warning, StringComparison.Ordinal);
            Assert.Contains("terse", warning, StringComparison.Ordinal);
        }

        [Fact]
        public void AnEmptyProfileFile_FallsBack_AndNamesTheFile()
        {
            Write("blank", "  \n ");
            var warnings = new List<string>();

            Assert.Null(Program.ResolvePromptProfile("blank", _dir, warnings.Add));

            string warning = Assert.Single(warnings);
            Assert.Contains("blank", warning, StringComparison.Ordinal);
            Assert.Contains("empty", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.Combine(_dir, "blank.md"), warning, StringComparison.Ordinal);
        }

        [Fact]
        public void NoPromptsFolderAtAll_FallsBack_AndSaysNoneAreSaved()
        {
            string missing = Path.Combine(_dir, "no-such-folder");
            var warnings = new List<string>();

            Assert.Null(Program.ResolvePromptProfile("anything", missing, warnings.Add));

            string warning = Assert.Single(warnings);
            Assert.Contains("none saved", warning, StringComparison.Ordinal);
        }
    }
}
