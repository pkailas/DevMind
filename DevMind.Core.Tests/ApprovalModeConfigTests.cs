// File: ApprovalModeConfigTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The mode has to survive a restart, or turning it on is a per-session ritual nobody keeps
// up. It is stored as text ("auto" / "manual") rather than as the enum's ordinal for two
// reasons: a hand-edited devmind.json should read as a word, and renumbering ApprovalMode
// later must not silently reinterpret every file already on disk as a different mode.
//
// The pairing of ApprovalModeText and ApprovalMode is the part worth pinning — two
// representations of one value is how they drift apart.

using Xunit;

namespace DevMind.Core.Tests
{
    public class ApprovalModeConfigTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;
        private readonly string? _priorGlobalDir;

        // DEVMIND_GLOBAL_DIR is DevMindPaths' documented test seam: it redirects
        // TuiConfig.ConfigPath at a hermetic temp directory, so Save()/Load() never touch
        // the operator's real devmind.json.
        public ApprovalModeConfigTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_cfgmode_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "devmind.json");

            _priorGlobalDir = Environment.GetEnvironmentVariable("DEVMIND_GLOBAL_DIR");
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _dir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _priorGlobalDir);
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void TheDefaultIsAuto()
        {
            var config = new TuiConfig();

            Assert.Equal(ApprovalMode.Auto, config.ApprovalMode);
            Assert.Equal("auto", config.ApprovalModeText);
        }

        [Fact]
        public void SettingTheModeWritesTheCanonicalText()
        {
            var config = new TuiConfig { ApprovalMode = ApprovalMode.Manual };

            Assert.Equal("manual", config.ApprovalModeText);
            Assert.Equal(ApprovalMode.Manual, config.ApprovalMode);
        }

        [Fact]
        public void TheModeRoundTripsThroughDisk()
        {
            new TuiConfig { ApprovalMode = ApprovalMode.Manual }.Save();

            string json = File.ReadAllText(_path);
            Assert.Contains("\"approvalMode\"", json, StringComparison.Ordinal);
            Assert.Contains("manual", json, StringComparison.Ordinal);

            Assert.Equal(ApprovalMode.Manual, TuiConfig.LoadFrom(_path).ApprovalMode);
        }

        [Fact]
        public void AutoRoundTripsThroughDiskToo()
        {
            new TuiConfig { ApprovalMode = ApprovalMode.Auto }.Save();

            Assert.Equal(ApprovalMode.Auto, TuiConfig.LoadFrom(_path).ApprovalMode);
        }

        // A config written before this key existed, or edited by hand into nonsense, must
        // read back as the safe default rather than throwing on load.
        [Theory]
        [InlineData("{}")]
        [InlineData("{\"approvalMode\": \"\"}")]
        [InlineData("{\"approvalMode\": \"yolo\"}")]
        [InlineData("{\"approvalMode\": \"MANUAL-ish\"}")]
        public void AnAbsentOrUnrecognisedValue_ReadsAsAuto(string json)
        {
            File.WriteAllText(_path, json);

            Assert.Equal(ApprovalMode.Auto, TuiConfig.LoadFrom(_path).ApprovalMode);
        }

        // Case and padding are tolerated on the way in, and normalised on the way out.
        [Fact]
        public void AHandEditedValue_IsAcceptedAndNormalised()
        {
            File.WriteAllText(_path, "{\"approvalMode\": \"  Manual \"}");

            var loaded = TuiConfig.LoadFrom(_path);
            Assert.Equal(ApprovalMode.Manual, loaded.ApprovalMode);

            loaded.ApprovalMode = loaded.ApprovalMode;      // re-assign through the setter
            Assert.Equal("manual", loaded.ApprovalModeText);
        }
    }
}
