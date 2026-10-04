// File: MergeConflictTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-69: the three-way merge gate merges for real, and a headless job never enters the
// blocking pending-conflict state.
//
// Until now ThreeWayMergeCheck called DiffPlex with chunker: null, which DiffPlex 1.9.0 rejects,
// so every real divergence — the file changed on disk after the agent read it — took the agent's
// text unmerged and silently overwrote the other change. With the line chunker a change in a
// different region now merges, and an overlapping change is a conflict. A delegated job has nobody
// to run /resolve, so there the conflict refuses that one write — file unchanged, the conflict
// shown to the model, nothing kept — while an interactive host keeps the pending state.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class MergeConflictTests : IDisposable
    {
        private readonly string _dir;

        public MergeConflictTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_merge_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private const string Original = "alpha\nbravo\ncharlie\ndelta\necho\n";

        private (BufferedAgenticHost host, StringBuilder output) Host(bool headless)
        {
            var output = new StringBuilder();
            var host = new BufferedAgenticHost(_dir, outputSink: (t, _) => output.Append(t))
            {
                RestrictWritesToWorkingDirectory = headless,
            };
            return (host, output);
        }

        private string PathOf(string name) => Path.Combine(_dir, name);

        // ── The merge itself ─────────────────────────────────────────────────────

        [Fact]
        public void ACleanThreeWayMerge_Merges()
        {
            var r = ThreeWayMergeCheck.CheckAndMerge("a\nb\nc\n", "A\nb\nc\n", "a\nb\nC\n");

            Assert.Equal(MergeMode.ThreeWay, r.Mode);
            Assert.False(r.HasConflicts);
            Assert.False(r.UsedFallback);
            Assert.Equal("A\nb\nC\n", r.MergedText);
            Assert.Equal("", MergeReport.TranscriptLabel(r));
        }

        [Fact]
        public void AnOverlappingChange_IsAConflict_WithEachSidesLineNumbers()
        {
            var r = ThreeWayMergeCheck.CheckAndMerge("a\nb\nc\nd\n", "a\nB1\nc\nd\n", "x\na\nB2\nc\nd\n");

            Assert.True(r.HasConflicts);
            var c = Assert.Single(r.Conflicts);
            Assert.Equal("b", c.BaseText);
            Assert.Equal("B1", c.ProposedText);
            Assert.Equal("B2", c.CurrentText);
            Assert.Equal(2, c.BaseLine);
            Assert.Equal(2, c.ProposedLine);
            Assert.Equal(3, c.CurrentLine);                     // a line was inserted above it on disk
        }

        [Fact]
        public void TheDiffEngineFailureLabel_StaysForAGenuineException()
        {
            try
            {
                ThreeWayMergeCheck.DiffEngineHookForTest = () => throw new InvalidOperationException("boom");
                var r = ThreeWayMergeCheck.CheckAndMerge("a\nb\n", "A\nb\n", "a\nB\n");
                Assert.Equal(MergeMode.DiffEngineFailed, r.Mode);
            }
            finally { ThreeWayMergeCheck.DiffEngineHookForTest = null; }
        }

        // ── Headless: a clean merge lands both changes ───────────────────────────

        [Fact]
        public async Task Headless_ADiskChangeInAnotherRegion_IsMergedWithTheAgentsEdit()
        {
            var (host, output) = Host(headless: true);
            IAgenticHost h = host;
            Assert.NotNull(await h.SaveFileAsync("notes.txt", Original, fromToolCall: true));
            File.WriteAllText(PathOf("notes.txt"), Original.Replace("echo", "ECHO (outside)"));   // someone else
            output.Clear();

            Assert.NotNull(await h.SaveFileAsync("notes.txt", Original.Replace("alpha", "ALPHA (agent)"), fromToolCall: true));

            string merged = File.ReadAllText(PathOf("notes.txt")).Replace("\r\n", "\n");
            Assert.Equal("ALPHA (agent)\nbravo\ncharlie\ndelta\nECHO (outside)\n", merged);
            Assert.Contains("[FILE] Saved notes.txt", output.ToString());
            Assert.DoesNotContain("[merge engine failed", output.ToString());
            Assert.DoesNotContain("[no base", output.ToString());
        }

        // ── Headless: an overlapping change refuses that write only ──────────────

        [Fact]
        public async Task Headless_AnOverlappingSave_IsRefused_FileUnchanged_ThenTheNextWriteAfterAReReadLands()
        {
            var (host, output) = Host(headless: true);
            IAgenticHost h = host;
            Assert.NotNull(await h.SaveFileAsync("notes.txt", Original, fromToolCall: true));
            string onDisk = Original.Replace("charlie", "CHARLIE (outside)");
            File.WriteAllText(PathOf("notes.txt"), onDisk);

            var ex = await Assert.ThrowsAsync<MergeConflictRefusedException>(() =>
                h.SaveFileAsync("notes.txt", Original.Replace("charlie", "CHARLIE (agent)"), fromToolCall: true));

            Assert.StartsWith(MergeReport.ConflictRefusedMarker, ex.Message);
            Assert.Contains("Conflict 1:", ex.Message);
            Assert.Contains("what you read (base):", ex.Message);
            Assert.Contains("    3: charlie", ex.Message);
            Assert.Contains("    3: CHARLIE (agent)", ex.Message);
            Assert.Contains("    3: CHARLIE (outside)", ex.Message);
            Assert.EndsWith(MergeReport.ReReadInstruction, ex.Message);
            Assert.Equal(onDisk, File.ReadAllText(PathOf("notes.txt")));                       // unchanged
            Assert.Contains(host.GetActions(), a => a.Kind == MergeReport.ConflictRefusedEvent && !a.Success);
            Assert.Contains("[MERGE CONFLICT] Write to \"notes.txt\" refused", output.ToString());

            // No lingering state: re-read, redo the edit, and it lands.
            await h.LoadFileContentAsync(PathOf("notes.txt"), 0, 0, false);
            Assert.NotNull(await h.SaveFileAsync("notes.txt",
                onDisk.Replace("alpha", "ALPHA (agent, redone)"), fromToolCall: true));
            Assert.Contains("ALPHA (agent, redone)", File.ReadAllText(PathOf("notes.txt")));
            Assert.DoesNotContain("pending conflict", output.ToString());
        }

        [Fact]
        public async Task Headless_AnOverlappingPatch_IsRefusedWithTheConflict_FileUnchanged()
        {
            var (host, _) = Host(headless: true);
            IAgenticHost h = host;
            Assert.NotNull(await h.SaveFileAsync("notes.txt", Original, fromToolCall: true));
            string path = PathOf("notes.txt");

            var report = new StringBuilder();
            var resolved = PatchEngine.ResolvePairs(new List<(string, string)> { ("bravo", "BRAVO (agent)") },
                path, "notes.txt", File.ReadAllText(path), new UTF8Encoding(false), (t, _) => report.Append(t));
            Assert.True(resolved != null, report.ToString());
            string onDisk = Original.Replace("bravo", "BRAVO (outside)");
            File.WriteAllText(path, onDisk);                          // lands between resolve and apply

            var (written, error) = await h.ApplyResolvedPatchAsync(resolved!);

            Assert.Null(written);
            Assert.StartsWith(MergeReport.ConflictRefusedMarker, error);
            Assert.Contains("BRAVO (outside)", error);
            Assert.EndsWith(MergeReport.ReReadInstruction, error);
            Assert.Equal(onDisk, File.ReadAllText(path));
        }

        [Fact]
        public void TheModelReadsTheRefusal_AndThatTheFileDidNotChange()
        {
            var result = new ExecutionResult();
            result.Errors.Add(MergeReport.ConflictRefusal("notes.txt",
                ThreeWayMergeCheck.CheckAndMerge("a\nb\n", "a\nB1\n", "a\nB2\n")));
            var tc = new ToolCallResult { Name = "create_file", Arguments = new Dictionary<string, string> { ["filename"] = "notes.txt" } };

            string content = LoopHelpers.BuildToolResultContent(tc, result, null);

            Assert.Contains(MergeReport.ConflictRefusedMarker, content);
            Assert.Contains("The file on disk was NOT changed.", content);
        }

        // ── Base freshness ───────────────────────────────────────────────────────

        [Fact]
        public async Task TheAgentWritingTheSameFileTwice_NeverConflictsWithItself()
        {
            var (host, output) = Host(headless: true);
            IAgenticHost h = host;
            Assert.NotNull(await h.SaveFileAsync("notes.txt", Original, fromToolCall: true));
            Assert.NotNull(await h.SaveFileAsync("notes.txt", Original.Replace("bravo", "B2"), fromToolCall: true));
            Assert.NotNull(await h.SaveFileAsync("notes.txt", Original.Replace("bravo", "B3"), fromToolCall: true));
            Assert.NotNull(await h.AppendFileAsync("notes.txt", "foxtrot\n"));

            string path = PathOf("notes.txt");
            var report = new StringBuilder();
            var resolved = PatchEngine.ResolvePairs(new List<(string, string)> { ("B3", "B4") },
                path, "notes.txt", File.ReadAllText(path), new UTF8Encoding(false), (t, _) => report.Append(t));
            var (written, error) = await h.ApplyResolvedPatchAsync(resolved!);

            Assert.Null(error);
            Assert.Equal(path, written);
            Assert.Contains("B4", File.ReadAllText(path));
            Assert.DoesNotContain("CONFLICT", output.ToString());
            host.DrainPatchBackups();
        }

        // ── Interactive (the CLI host shares this class): today's pending state ──

        [Fact]
        public async Task Interactive_AnOverlappingSave_EntersThePendingConflictState_AsBefore()
        {
            var (host, output) = Host(headless: false);
            IAgenticHost h = host;
            Assert.NotNull(await h.SaveFileAsync("notes.txt", Original, fromToolCall: true));
            File.WriteAllText(PathOf("notes.txt"), Original.Replace("charlie", "CHARLIE (outside)"));

            Assert.Null(await h.SaveFileAsync("notes.txt", Original.Replace("charlie", "CHARLIE (agent)"), fromToolCall: true));
            Assert.Contains("blocked by merge conflict", output.ToString());
            Assert.Contains("/resolve accept_proposed", output.ToString());

            // ...and the next write waits for /resolve.
            Assert.Null(await h.SaveFileAsync("other.txt", "x\n", fromToolCall: true));
            Assert.Contains("pending conflict", output.ToString());
        }
    }
}
