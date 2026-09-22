// File: TuiApprovalModeToggleTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// THE BINDING ITSELF IS NOT TESTED HERE, AND CANNOT BE. A keypress arriving at
// app.Keyboard.KeyDown needs a running Terminal.Gui application, a driver, and a real
// terminal delivering a real key — and the genuinely uncertain part is what Windows
// Terminal and conhost actually send for Shift+Tab, which no unit test can answer. The live
// check is the verification for that, and the brief says so.
//
// What IS testable is everything behind the key: the toggle alternates, each change is
// applied to the live options AND persisted, and the text the operator sees names the mode.
// Those are the parts that would silently rot — a key that flips the session but forgets to
// save, or a flash that says "mode changed" without saying to what, both look fine until
// someone relies on them.
//
// ApprovalModeControl exists to make that boundary as small as possible: after this file,
// the untested surface is one `if` on a key code and two calls.

using Xunit;

namespace DevMind.TUI.Tests
{
    public sealed class TuiApprovalModeToggleTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorGlobalDir;

        // DEVMIND_GLOBAL_DIR redirects TuiConfig's path at a temp directory, so Save() never
        // touches the operator's real devmind.json.
        public TuiApprovalModeToggleTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_toggle_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorGlobalDir = Environment.GetEnvironmentVariable("DEVMIND_GLOBAL_DIR");
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _dir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _priorGlobalDir);
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string ConfigPath => Path.Combine(_dir, "devmind.json");

        // ── The alternation ────────────────────────────────────────────────────

        [Fact]
        public void TheToggleAlternates()
        {
            Assert.Equal(ApprovalMode.Manual, ApprovalModeControl.Next(ApprovalMode.Auto));
            Assert.Equal(ApprovalMode.Auto, ApprovalModeControl.Next(ApprovalMode.Manual));
        }

        [Fact]
        public void TwoPressesReturnToWhereItStarted()
        {
            var mode = ApprovalMode.Auto;

            mode = ApprovalModeControl.Next(mode);
            mode = ApprovalModeControl.Next(mode);

            Assert.Equal(ApprovalMode.Auto, mode);
        }

        // ── Apply: the live value AND the persisted one ───────────────────────

        // Both halves, in one test, because either alone is a bug that looks like it works.
        // Options only: the mode is gone next launch. Config only: the executor keeps reading
        // the old value and nothing changes this session.
        [Fact]
        public void ApplyingAMode_SetsTheLiveOptions_AndPersistsIt()
        {
            var options = new TuiOptions();
            var config = new TuiConfig();

            var returned = ApprovalModeControl.Apply(ApprovalMode.Manual, options, config);

            Assert.Equal(ApprovalMode.Manual, returned);
            Assert.Equal(ApprovalMode.Manual, options.ApprovalMode);
            Assert.Equal(ApprovalMode.Manual, config.ApprovalMode);

            Assert.Equal(ApprovalMode.Manual, TuiConfig.LoadFrom(ConfigPath).ApprovalMode);
        }

        [Fact]
        public void EveryFlipIsPersisted_NotJustTheFirst()
        {
            var options = new TuiOptions();
            var config = new TuiConfig();

            ApprovalModeControl.Apply(ApprovalMode.Manual, options, config);
            Assert.Equal(ApprovalMode.Manual, TuiConfig.LoadFrom(ConfigPath).ApprovalMode);

            ApprovalModeControl.Apply(ApprovalMode.Auto, options, config);
            Assert.Equal(ApprovalMode.Auto, TuiConfig.LoadFrom(ConfigPath).ApprovalMode);
            Assert.Equal(ApprovalMode.Auto, options.ApprovalMode);
        }

        // The full round trip a keypress performs: read the live mode, flip it, apply it.
        [Fact]
        public void TogglingThroughApply_RoundTripsTheLiveOptions()
        {
            var options = new TuiOptions();
            var config = new TuiConfig();

            ApprovalModeControl.Apply(ApprovalModeControl.Next(options.ApprovalMode), options, config);
            Assert.Equal(ApprovalMode.Manual, options.ApprovalMode);

            ApprovalModeControl.Apply(ApprovalModeControl.Next(options.ApprovalMode), options, config);
            Assert.Equal(ApprovalMode.Auto, options.ApprovalMode);

            Assert.Equal(ApprovalMode.Auto, TuiConfig.LoadFrom(ConfigPath).ApprovalMode);
        }

        // Null-tolerant: a caller with no config (or no options) still gets the other half
        // applied rather than a NullReferenceException on a keypress.
        [Fact]
        public void ApplyToleratesAMissingOptionsOrConfig()
        {
            Assert.Equal(ApprovalMode.Manual,
                ApprovalModeControl.Apply(ApprovalMode.Manual, null!, null!));

            var options = new TuiOptions();
            ApprovalModeControl.Apply(ApprovalMode.Manual, options, null!);
            Assert.Equal(ApprovalMode.Manual, options.ApprovalMode);
        }

        // ── The text the operator sees ─────────────────────────────────────────

        // An accidental Shift+Tab has to be noticeable, and "mode changed" is not — the line
        // has to name which mode, or the record is useless the moment you scroll back to it.
        [Theory]
        [InlineData(ApprovalMode.Manual, "manual")]
        [InlineData(ApprovalMode.Auto, "auto")]
        public void TheTranscriptLineNamesTheMode(ApprovalMode mode, string expected)
        {
            string line = ApprovalModeControl.TranscriptLine(mode);

            Assert.Contains("[MODE]", line, StringComparison.Ordinal);
            Assert.Contains(expected, line, StringComparison.Ordinal);
            Assert.EndsWith("\n", line, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(ApprovalMode.Manual, "manual")]
        [InlineData(ApprovalMode.Auto, "auto")]
        public void TheStatusFlashNamesTheMode(ApprovalMode mode, string expected)
        {
            string flash = ApprovalModeControl.StatusFlash(mode);

            Assert.Contains(expected, flash, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", flash, StringComparison.Ordinal);   // one status line
        }

        // The two texts must not merely contain the word — they must distinguish the modes
        // from each other, which a template bug ("[MODE] {0}" with the wrong argument) would
        // break while still passing a "contains the word" check on one of them.
        [Fact]
        public void TheTwoModesReadDifferently()
        {
            Assert.NotEqual(
                ApprovalModeControl.TranscriptLine(ApprovalMode.Auto),
                ApprovalModeControl.TranscriptLine(ApprovalMode.Manual));

            Assert.NotEqual(
                ApprovalModeControl.StatusFlash(ApprovalMode.Auto),
                ApprovalModeControl.StatusFlash(ApprovalMode.Manual));
        }

        // Manual's line says what it DOES, because the consequence is the point: someone who
        // hit the key by accident needs to know the next write will stop and wait.
        [Fact]
        public void ManualSaysThatMutationsWillAsk()
        {
            Assert.Contains("ask", ApprovalModeControl.TranscriptLine(ApprovalMode.Manual),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
