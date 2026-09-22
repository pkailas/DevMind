// File: CliApprovalModeTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The CLI gets --mode and the devmind.json key but no runtime toggle: it has no command
// surface to flip one through, so the mode is chosen at launch and left alone.
//
// The precedence that matters is FromArgs' existing one — devmind.json in pass 2, flags in
// pass 3 — so a flag beats the file. Pinned here because approval mode is the setting where
// getting that backwards is worst: a config saying "manual" silently overriding an explicit
// --mode auto would leave someone confirming every write they did not ask to confirm.

using Xunit;

namespace DevMind.Cli.Tests
{
    public sealed class CliApprovalModeTests : IDisposable
    {
        private readonly string _dir;

        public CliApprovalModeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_climode_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private void WriteConfig(string json) => File.WriteAllText(Path.Combine(_dir, "devmind.json"), json);

        [Fact]
        public void TheDefaultIsAuto()
        {
            Assert.Equal(ApprovalMode.Auto, new CliOptions().ApprovalMode);
        }

        [Theory]
        [InlineData("manual", ApprovalMode.Manual)]
        [InlineData("auto", ApprovalMode.Auto)]
        public void TheArgumentSetsTheMode(string text, ApprovalMode expected)
        {
            var opts = CliOptions.FromArgs(new[] { "--dir", _dir, "--mode", text });

            Assert.Equal(expected, opts.ApprovalMode);
        }

        [Fact]
        public void TheConfigFileSetsTheMode()
        {
            WriteConfig("{ \"approvalMode\": \"manual\" }");

            Assert.Equal(ApprovalMode.Manual, CliOptions.FromArgs(new[] { "--dir", _dir }).ApprovalMode);
        }

        // Pass 3 beats pass 2: an explicit flag wins over the file, as every other setting does.
        [Fact]
        public void TheArgumentBeatsTheConfigFile()
        {
            WriteConfig("{ \"approvalMode\": \"manual\" }");

            var opts = CliOptions.FromArgs(new[] { "--dir", _dir, "--mode", "auto" });

            Assert.Equal(ApprovalMode.Auto, opts.ApprovalMode);
        }

        [Fact]
        public void AnUnrecognisedConfigValue_LeavesTheDefault()
        {
            WriteConfig("{ \"approvalMode\": \"yolo\" }");

            Assert.Equal(ApprovalMode.Auto, CliOptions.FromArgs(new[] { "--dir", _dir }).ApprovalMode);
        }

        // --always-confirm is untouched by any of this: it still forces the diff card for
        // patches on its own, independently of the mode.
        [Fact]
        public void AlwaysConfirmStillWorksOnItsOwn()
        {
            var opts = CliOptions.FromArgs(new[] { "--dir", _dir, "--always-confirm" });

            Assert.True(opts.AlwaysConfirmPatch);
            Assert.Equal(ApprovalMode.Auto, opts.ApprovalMode);
        }
    }
}
