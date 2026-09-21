// File: PatchAppliedMessageTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Both agentic hosts used to tell the model, after every applied patch:
//
//     [PATCH] Applied to <path> (undo depth: N)
//
// There is no undo. Outside _archive/, every use of a patch backup path is
// create (PatchEngine), push, evict-oldest, or delete (DrainPatchBackups) —
// nothing restores a file from one, and no /undo command or UndoLastPatch
// exists in Core, TUI, Cli or McpServer. The counter itself is honest as an
// internal metric (GetPatchBackupCount / PatchBackupCount, exercised by
// PatchBackupDrainTests); the CLAIM made to the model was not.
//
// These tests pin the fix from both sides, because "the fragment is gone" on
// its own would also pass if the whole line vanished:
//
//   * Behaviourally, through a real patch on the Core host: the line still
//     names the patched file, and no longer advertises a depth.
//   * Structurally, on both hosts' sources: the message still interpolates
//     resolved.FullPath AND still carries the "[two-way fallback]" suffix on
//     the same emit, and no production source anywhere claims an undo depth.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class PatchAppliedMessageTests : IDisposable
    {
        private readonly string _dir;

        public PatchAppliedMessageTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_patchmsg_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        // ── Behaviour: the emitted line ─────────────────────────────────────────

        [Fact]
        public async Task PatchApplied_NamesTheFile_AndAdvertisesNoUndoDepth()
        {
            var captured = new StringBuilder();
            var host = new BufferedAgenticHost(_dir, outputSink: (text, _) => captured.Append(text));

            string target  = Path.Combine(_dir, $"patchmsg_{Guid.NewGuid():N}.txt");
            string content = "line one\nline two\nline three\n";
            File.WriteAllText(target, content);

            var reporter = new StringBuilder();
            var resolved = PatchEngine.ResolvePairs(
                new List<(string, string)> { ("line two", "MARKER") },
                target, Path.GetFileName(target), content, Encoding.UTF8,
                (text, _) => reporter.Append(text));
            Assert.True(resolved != null, $"patch did not resolve. Reporter said: {reporter}");

            var (fullPath, failureReason) = await ((IAgenticHost)host).ApplyResolvedPatchAsync(resolved);
            Assert.Null(failureReason);
            Assert.Equal(target, fullPath);

            string output = captured.ToString();

            // The rest of the line survives — this is NOT just "the fragment is gone".
            Assert.Contains($"[PATCH] Applied to {target}", output, StringComparison.Ordinal);

            // The false claim is gone.
            Assert.DoesNotContain("undo depth", output, StringComparison.OrdinalIgnoreCase);

            // ...and the counter it was derived from is still kept, honestly, internally.
            Assert.Equal(1, host.PatchBackupCount);

            host.DrainPatchBackups();
        }

        // ── Structure: both hosts, and the repo at large ────────────────────────

        private static string RepoRoot()
        {
            // DevMind.Core.Tests/bin/<cfg>/<tfm>/ → up 3 = DevMind.Core.Tests → up 1 = repo root.
            string dir = AppContext.BaseDirectory;
            for (int up = 0; up < 4; up++)
            {
                var d = new DirectoryInfo(dir);
                if (d.Parent == null) break;
                dir = d.Parent.FullName;
            }
            return dir;
        }

        [Theory]
        [InlineData("DevMind.Core/BufferedAgenticHost.cs", "AppendOutput")]
        [InlineData("DevMind.TUI/TuiAgenticHost.cs", "AppendOutputLocal")]
        public void BothHosts_KeepTheWholeLineExceptTheUndoClaim(string relativePath, string emitter)
        {
            string path = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"source not found: {path} — RepoRoot() resolved wrong, the guard is vacuous.");

            string[] lines = File.ReadAllLines(path);
            string? emit = lines.FirstOrDefault(l => l.Contains("[PATCH] Applied to", StringComparison.Ordinal));

            Assert.True(emit != null, $"{relativePath} no longer emits a '[PATCH] Applied to' line at all.");
            Assert.Contains(emitter + "(", emit!, StringComparison.Ordinal);
            Assert.Contains("{resolved.FullPath}", emit!, StringComparison.Ordinal);
            Assert.Contains("[two-way fallback]", emit!, StringComparison.Ordinal);
            Assert.DoesNotContain("undo depth", emit!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NoProductionSourceAdvertisesAnUndoDepth()
        {
            string root = RepoRoot();
            var offenders = new List<string>();
            int scanned = 0;

            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = file.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
                bool skip = false;
                foreach (string bad in new[] { "/_archive/", "/bin/", "/obj/", "/dist/", "/publish/",
                                               "/tools/", "/.vs/", "/.devmind/", "/CodeReviewBenchmarkWithBugs/",
                                               ".Core.Tests/", ".Cli.Tests/", ".McpServer.Tests/", ".TUI.Tests/" })
                    if (rel.Contains(bad, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                if (skip) continue;

                scanned++;
                if (File.ReadAllText(file).Contains("undo depth", StringComparison.OrdinalIgnoreCase))
                    offenders.Add(rel);
            }

            Assert.True(scanned > 0, "Scanned zero source files — RepoRoot() resolved wrong; the guard is vacuous.");
            Assert.True(offenders.Count == 0,
                "Production source advertises an undo depth, but nothing restores a file from a patch " +
                "backup. Either remove the claim or ship a real restore.\nOffenders: " +
                string.Join(", ", offenders));
        }
    }
}
