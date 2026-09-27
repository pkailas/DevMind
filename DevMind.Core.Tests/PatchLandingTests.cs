// File: PatchLandingTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-34: patch_file reported "[PATCH] Applied" for an edit that never reached the file.
// job-1719 iteration 45 sent two patch_file calls to DevMind.TUI/Program.cs in ONE iteration;
// both reported "Applied ... [two-way fallback]", and the first edit (a new PlanModePromptNote
// member) was not in the file afterwards.
//
// Root cause: AgenticExecutor resolves every patch of an iteration first (Phase 1) and applies
// them after (Phase 2), and PatchEngine.ApplyPatch spliced each edit into the file content
// captured at RESOLVE time and wrote that over the whole file. The second patch to the same
// file therefore wrote original+edit2, erasing edit1. The first apply had refreshed the cache,
// so base == current and the merge gate took its two-way fallback: "[two-way fallback]" on both.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class PatchLandingTests : IDisposable
    {
        private readonly string _dir;

        public PatchLandingTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_patchland_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private const string Original =
            "internal static class Program\n" +
            "{\n" +
            "    static string BuildCombinedSystemPrompt(string basePrompt)\n" +
            "    {\n" +
            "        return basePrompt;\n" +
            "    }\n" +
            "\n" +
            "    static void Main() { }\n" +
            "}\n";

        private string Write(string name, string text)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, text);
            return path;
        }

        private static ToolCallResult PatchCall(string file, string find, string replace) => new()
        {
            Name = "patch_file",
            Arguments = new Dictionary<string, string> { ["filename"] = file, ["find"] = find, ["replace"] = replace },
        };

        private async Task<ExecutionResult> RunIterationAsync(IAgenticHost host, params ToolCallResult[] calls)
        {
            var blocks = ToolCallMapper.Map(calls.ToList(), buildCommand: "dotnet build");
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);
            return await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild }, new ResponseOutcome(blocks));
        }

        // ── The root cause, as job-1719 hit it ──

        [Fact]
        public async Task TwoPatchesToTheSameFile_InOneIteration_BothLand()
        {
            string path = Write("Program.cs", Original);
            var transcript = new System.Text.StringBuilder();
            IAgenticHost host = new BufferedAgenticHost(_dir, outputSink: (t, _) => transcript.Append(t));

            var result = await RunIterationAsync(host,
                // edit 1: a new member (the PlanModePromptNote shape)
                PatchCall(path,
                    "    static void Main() { }",
                    "    internal static string PlanModePromptNote(bool plan) => plan ? \"PLAN\" : \"\";\n\n    static void Main() { }"),
                // edit 2: wire it in, elsewhere in the same file
                PatchCall(path,
                    "        return basePrompt;",
                    "        return basePrompt + PlanModePromptNote(true);"));

            string onDisk = File.ReadAllText(path);
            Assert.DoesNotContain("PATCH-FAILED", transcript.ToString());
            Assert.Contains("internal static string PlanModePromptNote(bool plan)", onDisk);   // edit 1
            Assert.Contains("return basePrompt + PlanModePromptNote(true);", onDisk);         // edit 2
            Assert.Equal(2, result.PatchesApplied);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public async Task SecondPatchWhoseFindTheFirstReplaced_FailsLoudly_FirstStays()
        {
            // Rebase: patch 2 was resolved against the original, but patch 1 changed that text.
            string path = Write("Program.cs", Original);
            IAgenticHost host = new BufferedAgenticHost(_dir);

            var result = await RunIterationAsync(host,
                PatchCall(path, "        return basePrompt;", "        return basePrompt.Trim();"),
                PatchCall(path, "        return basePrompt;", "        return basePrompt + \"!\";"));

            string onDisk = File.ReadAllText(path);
            Assert.Contains("return basePrompt.Trim();", onDisk);
            Assert.Equal(1, result.PatchesApplied);
            string error = Assert.Single(result.Errors);
            Assert.StartsWith("[PATCH-FAILED:Program.cs] Apply failed: Program.cs changed after this patch was resolved", error);
            Assert.EndsWith("File was NOT modified.", error);
        }

        // ── The post-write guard: a forced non-landing edit is never "Applied" ──

        private sealed class WriteHook : IDisposable
        {
            public WriteHook(string path, Func<string, string> rewrite)
                => PatchEngine.WriteHookForTest = (p, text) =>
                    string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ? rewrite(text) : text;
            public void Dispose() => PatchEngine.WriteHookForTest = null;
        }

        [Fact]
        public async Task AgentHost_ForcedNonLanding_ReportsPatchFailed_NotApplied_AndRestores()
        {
            string path = Write("Program.cs", Original);
            var transcript = new System.Text.StringBuilder();
            IAgenticHost host = new BufferedAgenticHost(_dir, outputSink: (t, _) => transcript.Append(t));

            ExecutionResult result;
            using (new WriteHook(path, _ => Original))   // the write "succeeds" but the edit is not in it
                result = await RunIterationAsync(host,
                    PatchCall(path, "        return basePrompt;", "        return basePrompt.Trim();"));

            Assert.Equal(0, result.PatchesApplied);
            Assert.DoesNotContain("[PATCH] Applied", transcript.ToString());
            string error = Assert.Single(result.Errors);
            Assert.StartsWith("[PATCH-FAILED:Program.cs] Apply failed: edit 1 did not land — its REPLACE text is not at the edited position", error);
            Assert.Contains("restored to its content before this patch", error);
            Assert.Equal(Original, File.ReadAllText(path));
        }

        // ── PatchEngine directly: the path all three callers share (agent host, TUI host at
        //    TuiAgenticHost.ApplyResolvedPatchAsync, MCP patch_file). TuiAgenticHost itself needs a
        //    live Terminal.Gui view and is not constructed in tests. ──

        private PatchResolveResult Resolve(string path, params (string find, string replace)[] pairs)
        {
            var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(path);
            var resolved = PatchEngine.ResolvePairs(pairs.ToList(), path, Path.GetFileName(path), content, encoding, (_, _) => { });
            Assert.NotNull(resolved);
            return resolved;
        }

        private string Backups => Path.Combine(_dir, "bak");

        [Fact]
        public void Batch_OneEditDoesNotLand_WholeBatchRolledBack_AndLandedEditsNamed()
        {
            string path = Write("A.cs", "alpha();\nbeta();\ngamma();\n");
            var resolved = Resolve(path, ("alpha();", "ALPHA();"), ("beta();", "BETA();"), ("gamma();", "GAMMA();"));

            PatchApplyResult r;
            using (new WriteHook(path, text => text.Replace("BETA();", "beta();")))   // edit 2 lost
                r = PatchEngine.ApplyPatch(resolved, Backups);

            Assert.False(r.Success);
            Assert.True(r.NotLanded);
            Assert.StartsWith("edit 2 did not land — its REPLACE text is not at the edited position after the write.", r.Error);
            Assert.Contains("(edits 1, 3 had landed and were rolled back)", r.Error);
            Assert.Equal("alpha();\nbeta();\ngamma();\n", File.ReadAllText(path));
        }

        [Fact]
        public void Batch_AllEditsLand_Succeeds()
        {
            string path = Write("A.cs", "alpha();\nbeta();\ngamma();\n");
            var r = PatchEngine.ApplyPatch(Resolve(path, ("alpha();", "ALPHA();"), ("gamma();", "GAMMA();")), Backups);

            Assert.True(r.Success, r.Error);
            Assert.Equal("ALPHA();\nbeta();\nGAMMA();\n", File.ReadAllText(path));
        }

        [Fact]
        public void PureDeletion_Lands_AndAForcedMissIsCaught()
        {
            string path = Write("A.cs", "keep();\n// TODO remove\nkeep2();\n");
            var ok = PatchEngine.ApplyPatch(Resolve(path, ("// TODO remove\n", "")), Backups);
            Assert.True(ok.Success, ok.Error);
            // (The matcher trims FIND's surrounding whitespace, so the line's break stays.)
            Assert.Equal("keep();\n\nkeep2();\n", File.ReadAllText(path));

            string path2 = Write("B.cs", "keep();\n// TODO remove\nkeep2();\n");
            var resolved = Resolve(path2, ("// TODO remove\n", ""));
            PatchApplyResult miss;
            using (new WriteHook(path2, _ => "keep();\n// TODO remove\nkeep2();\n"))
                miss = PatchEngine.ApplyPatch(resolved, Backups);
            Assert.False(miss.Success);
            Assert.StartsWith("edit 1 did not land — the text it deletes is still in the file.", miss.Error);
        }

        [Fact]
        public void ReplaceTextThatAlreadyExistedElsewhere_IsCheckedAtTheEditedPosition()
        {
            // "Log();" is already in the file, so "present after the write" would prove nothing.
            string original = "Log();\nOld();\nOther();\n";
            string path = Write("A.cs", original);

            var ok = PatchEngine.ApplyPatch(Resolve(path, ("Old();", "Log();")), Backups);
            Assert.True(ok.Success, ok.Error);
            Assert.Equal("Log();\nLog();\nOther();\n", File.ReadAllText(path));

            string path2 = Write("B.cs", original);
            var resolved = Resolve(path2, ("Old();", "Log();"));
            PatchApplyResult miss;
            using (new WriteHook(path2, _ => original))   // "Log();" still present — at line 1 only
                miss = PatchEngine.ApplyPatch(resolved, Backups);
            Assert.False(miss.Success);
            Assert.StartsWith("edit 1 did not land", miss.Error);
            Assert.Equal(original, File.ReadAllText(path2));
        }

        [Fact]
        public void CrlfFile_EditLands_NoFalseAlarm()
        {
            string path = Write("A.cs", "alpha();\r\nbeta();\r\n");
            var r = PatchEngine.ApplyPatch(Resolve(path, ("beta();", "BETA();\nmore();")), Backups);
            Assert.True(r.Success, r.Error);
            Assert.Contains("BETA();", File.ReadAllText(path));
        }
    }
}
