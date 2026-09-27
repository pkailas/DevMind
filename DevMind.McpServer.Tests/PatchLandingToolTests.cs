// File: PatchLandingToolTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-34 on the MCP patch_file path: an edit that did not land after the write is reported as
// "patch_file: failed — edit N did not land …", never "applied", and the atomic batch is rolled
// back. PatchEngine.WriteHookForTest forces the non-landing write, keyed on this test's temp file.

using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class PatchLandingToolTests : IDisposable
    {
        private const string Original = "alpha();\nbeta();\ngamma();\n";
        private readonly string _dir;
        private readonly string _path;
        private readonly McpServices _svc;
        private readonly DevMindTools _tools;

        public PatchLandingToolTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcp_patchland_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "A.cs");
            File.WriteAllText(_path, Original);
            _svc = new McpServices(_dir);
            _tools = new DevMindTools(_svc);
        }

        public void Dispose()
        {
            PatchEngine.WriteHookForTest = null;
            _svc.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private void ForceWrite(Func<string, string> rewrite)
            => PatchEngine.WriteHookForTest = (p, text) =>
                string.Equals(Path.GetFullPath(p), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase) ? rewrite(text) : text;

        [Fact]
        public async Task SingleEdit_ForcedNonLanding_IsFailed_NotApplied()
        {
            ForceWrite(_ => Original);

            string r = await _tools.PatchFile(_path, find: "beta();", replace: "BETA();");

            Assert.StartsWith("patch_file: failed — edit 1 did not land", r);
            Assert.DoesNotContain("applied", r);
            Assert.Equal(Original, File.ReadAllText(_path));
        }

        [Fact]
        public async Task Batch_OneEditForcedNotToLand_WholeBatchRolledBack()
        {
            ForceWrite(text => text.Replace("GAMMA();", "gamma();"));

            string r = await _tools.PatchFile(_path, edits: new[]
            {
                new PatchEdit { Find = "alpha();", Replace = "ALPHA();" },
                new PatchEdit { Find = "gamma();", Replace = "GAMMA();" },
            });

            Assert.StartsWith("patch_file: failed — edit 2 did not land", r);
            Assert.Contains("(edit 1 had landed and was rolled back)", r);
            Assert.Equal(Original, File.ReadAllText(_path));
        }

        [Fact]
        public async Task NormalPatch_StillApplied()
        {
            string r = await _tools.PatchFile(_path, find: "beta();", replace: "BETA();");

            Assert.StartsWith("patch_file: applied to", r);
            Assert.Equal("alpha();\nBETA();\ngamma();\n", File.ReadAllText(_path));
        }
    }
}
