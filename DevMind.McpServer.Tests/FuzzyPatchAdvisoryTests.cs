// File: FuzzyPatchAdvisoryTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tool-level test for requirement 3 of the fuzzy-patch-safety change: the fuzzy-match
// advisory in patch_file must be LOAD-BEARING. The old behavior returned "fuzzy match —
// verify with diff_file", a hint the agent could ignore and then report success without
// ever looking. The new behavior emits the actual resulting unified diff in the tool
// result for fuzzy matches, so a silent line-merge or re-indent is visible in-band, with
// no second call. An exact match must NOT get the fuzzy advisory.

using DevMind;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public class FuzzyPatchAdvisoryTests : IDisposable
    {
        private readonly string _dir;
        private readonly McpServices _svc;
        private readonly DevMindTools _tools;

        public FuzzyPatchAdvisoryTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcp_fz_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _svc = new McpServices(_dir);
            _tools = new DevMindTools(_svc);
        }

        public void Dispose()
        {
            _svc.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public async Task PatchFile_FuzzyMatch_ReturnsResultingDiffNotJustHint()
        {
            string content =
                "namespace DevMind\n" +
                "{\n" +
                "    internal sealed class BuildVerification\n" +
                "    {\n" +
                "        public required string Command { get; init; }\n" +
                "        public int ExitCode { get; init; }\n" +
                "        /// <summary>Last ~2 KB of build output</summary>\n" +
                "        public required string OutputTail { get; init; }\n" +
                "        public bool Succeeded => ExitCode == 0;\n" +
                "    }\n" +
                "}\n";
            string path = Path.Combine(_dir, "target.cs");
            File.WriteAllText(path, content);

            // One-char typo ("Ouptut") forces the fuzzy path (the normalized exact match misses).
            string result = await _tools.PatchFile(path,
                find: "        public required string OuptutTail { get; init; }",
                replace: "        public required string OutputTail { get; set; }");

            // Load-bearing: the fuzzy badge is explicit and the resulting diff is in-band.
            Assert.StartsWith("patch_file: APPLIED VIA FUZZY MATCH", result);
            Assert.Contains("NOT an exact match", result);
            // The unified diff is present, showing both the removed and added lines.
            Assert.Contains("+++ target.cs (current)", result);
            Assert.Contains("public required string OutputTail { get; init; }", result);
            Assert.Contains("public required string OutputTail { get; set; }", result);
            // The old hint-only advisory ("verify with diff_file") is gone.
            Assert.DoesNotContain("verify with diff_file", result);
        }

        [Fact]
        public async Task PatchFile_ExactMatch_NoFuzzyBadgeOrDiff()
        {
            // An exact match must return the plain applied line — no fuzzy badge, no diff.
            string content =
                "namespace DevMind\n" +
                "{\n" +
                "    internal sealed class BuildVerification\n" +
                "    {\n" +
                "        public int ExitCode { get; init; }\n" +
                "    }\n" +
                "}\n";
            string path = Path.Combine(_dir, "target2.cs");
            File.WriteAllText(path, content);

            string result = await _tools.PatchFile(path,
                find: "        public int ExitCode { get; init; }",
                replace: "        public int ExitCode { get; set; }");

            Assert.StartsWith("patch_file: applied to ", result);
            Assert.DoesNotContain("APPLIED VIA FUZZY MATCH", result);
            Assert.DoesNotContain("+++ ", result);
        }
    }
}
