// File: HeadlessGuardrailTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the delegation guardrails added after the job-11 postmortem:
//   * SearchPattern '|' OR-alternation (false "no matches" from grep muscle-memory)
//   * headless shell blocklist (git-object restores destroyed uncommitted work;
//     taskkill hit the operator's running API)
//   * patch staleness guard (overlapping patches against a stale view corrupt files)
//   * HeadlessAgent answer sanitization (leaked <tool_call>/</think> tokens)
//   * ShellRunner %VAR% expansion under PowerShell (literal %TEMP% directories)

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class SearchPatternTests
    {
        [Theory]
        [InlineData("PdfGenerated|Exported", "public bool Exported { get; set; }", true)]
        [InlineData("PdfGenerated|Exported", "public bool PdfGenerated { get; set; }", true)]
        [InlineData("PdfGenerated|Exported", "nothing relevant here", false)]
        [InlineData("class ConnectorConfiguration", "public class ConnectorConfiguration", true)]
        [InlineData("ADDSCOPED|addsingleton", "services.AddSingleton<IThing, Thing>();", true)]
        [InlineData("a|b|c", "only C here", true)]
        [InlineData("|", "anything", false)]
        [InlineData("", "anything", false)]
        public void BuildMatcher_AlternationAndCase(string pattern, string line, bool expected)
        {
            Assert.Equal(expected, SearchPattern.BuildMatcher(pattern)(line));
        }
    }

    public class HeadlessGuardrailTests : IDisposable
    {
        private readonly string _dir;

        public HeadlessGuardrailTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_guard_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        // ── Shell blocklist ──────────────────────────────────────────────────

        [Theory]
        [InlineData("git restore src/File.cs", true)]
        [InlineData("git checkout -- src/File.cs", true)]
        [InlineData("git checkout HEAD~1 -- file", true)]
        [InlineData("git reset --hard HEAD~1", true)]
        [InlineData("git show HEAD~1:file | Set-Content file", true)]
        [InlineData("git show HEAD~1:file > file", true)]
        [InlineData("taskkill /F /PID 1234", true)]
        [InlineData("Stop-Process -Id 1234", true)]
        [InlineData("git show HEAD~1:file", false)]        // inspection without overwrite is fine
        [InlineData("git status", false)]
        [InlineData("git log --oneline -5", false)]
        [InlineData("dotnet build MySolution.slnx", false)]
        public void IsBlockedHeadlessCommand_Classifies(string command, bool blocked)
        {
            Assert.Equal(blocked, BufferedAgenticHost.IsBlockedHeadlessCommand(command, out _));
        }

        [Fact]
        public async Task RestrictedHost_BlocksGitRestore_WithModelVisibleGuidance()
        {
            var host = new BufferedAgenticHost(_dir) { RestrictWritesToWorkingDirectory = true };
            var (exitCode, output) = await ((IAgenticHost)host).RunShellAsync("git restore something.cs");

            Assert.Equal(1, exitCode);
            Assert.Contains("[BLOCKED]", output);
            Assert.Contains("READ", output);
            Assert.Contains(host.GetActions(), a => a.Kind == "blocked");
        }

        [Fact]
        public async Task UnrestrictedHost_DoesNotBlockGitCommands()
        {
            // Interactive skins keep full git freedom — guard is headless-only.
            var host = new BufferedAgenticHost(_dir);
            var (_, output) = await ((IAgenticHost)host).RunShellAsync("git status");
            Assert.DoesNotContain("[BLOCKED]", output);
        }

        // ── Patch staleness guard ─────────────────────────────────────────────

        [Fact]
        public async Task FourthPatchWithoutReread_Throws_ReadResets()
        {
            var host = new BufferedAgenticHost(_dir) { RestrictWritesToWorkingDirectory = true };
            IAgenticHost agenticHost = host;

            string file = Path.Combine(_dir, "victim.txt");
            File.WriteAllText(file, "alpha\nbravo\ncharlie\ndelta\n");
            await agenticHost.LoadFileContentAsync("victim.txt", forceFullRead: true);

            async Task PatchAsync(string find, string replace)
            {
                string patch = $"PATCH victim.txt\nFIND:\n{find}\nREPLACE:\n{replace}\nEND_PATCH";
                var resolved = await agenticHost.ResolvePatchAsync(patch, fromToolCall: true);
                Assert.NotNull(resolved);
                Assert.NotNull(await agenticHost.ApplyResolvedPatchAsync(resolved));
            }

            await PatchAsync("alpha", "ALPHA");
            await PatchAsync("bravo", "BRAVO");
            await PatchAsync("charlie", "CHARLIE");

            // Patch #4 without a re-read → guard throws with model-visible guidance.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                agenticHost.ResolvePatchAsync(
                    "PATCH victim.txt\nFIND:\ndelta\nREPLACE:\nDELTA\nEND_PATCH", fromToolCall: true));
            Assert.Contains("PATCH GUARD", ex.Message);
            Assert.Contains("READ", ex.Message);

            // Re-reading resets the counter — patching works again.
            await agenticHost.LoadFileContentAsync("victim.txt", forceFullRead: true);
            await PatchAsync("delta", "DELTA");
            Assert.Contains("DELTA", File.ReadAllText(file));
        }

        // ── Stale-cache invalidation (out-of-band writes) ─────────────────────

        [Fact]
        public void InvalidateIfStale_EvictsOnNewerDisk_KeepsFresh_EvictsMissing()
        {
            var cache = new FileContentCache();
            string path = Path.Combine(_dir, "watched.txt");
            File.WriteAllText(path, "v1");

            cache.Store("watched.txt", "v1");
            cache.InvalidateIfStale("watched.txt", path);
            Assert.True(cache.Contains("watched.txt"));          // fresh — kept

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            cache.InvalidateIfStale("watched.txt", path);
            Assert.False(cache.Contains("watched.txt"));         // disk newer — evicted

            cache.Store("watched.txt", "v2");
            File.Delete(path);
            cache.InvalidateIfStale("watched.txt", path);
            Assert.False(cache.Contains("watched.txt"));         // file gone — evicted
        }

        [Fact]
        public async Task Grep_SeesOutOfBandWrite_NotStaleCache()
        {
            // Regression: read/grep served session-cached content after a task agent
            // rewrote the file on disk — false "no matches" for text that existed.
            var host = new BufferedAgenticHost(_dir);
            IAgenticHost agenticHost = host;

            string file = Path.Combine(_dir, "shared.txt");
            File.WriteAllText(file, "original content");
            Assert.Contains("original", await agenticHost.GrepFileAsync("original", "shared.txt", null, null));

            // Out-of-band rewrite (as a task agent or git would do).
            File.WriteAllText(file, "replaced by another agent");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));

            string result = await agenticHost.GrepFileAsync("replaced by another agent", "shared.txt", null, null);
            Assert.Contains("replaced by another agent", result);
            Assert.DoesNotContain("no matches", result);
        }

        [Fact]
        public async Task Grep_SameFileNameInDifferentDirs_DoesNotServeWrongFile()
        {
            // Regression (job-8 postmortem): the file cache was keyed by BARE file name,
            // so once any Program.cs was cached (e.g. by an earlier **/*.cs FIND scan),
            // grepping a DIFFERENT Program.cs served the first one's content — a false
            // "0 matches" for a 7-alternative pattern whose hits sat past line 300.
            var host = new BufferedAgenticHost(_dir);
            IAgenticHost agenticHost = host;

            string harnessFile = Path.Combine(_dir, "harness", "Program.cs");
            string apiFile     = Path.Combine(_dir, "api", "Program.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(harnessFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(apiFile)!);

            File.WriteAllText(harnessFile, "var files = Directory.EnumerateFiles(dir);\n");

            // 420-line file whose interesting content sits past line 300.
            File.WriteAllLines(apiFile, Enumerable.Range(1, 420).Select(i => i switch
            {
                203 => "// still enumerates IEnumerable<IPipelineStep>",
                348 => "options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;",
                350 => "// naming policy stays default, not camelCase",
                351 => "opts.Converters.Add(new JsonStringEnumConverter());",
                _   => $"var filler{i} = {i};",
            }));

            // Cache the same-named harness file first — the collision trigger.
            await agenticHost.GrepFileAsync("EnumerateFiles", harnessFile, null, null);

            const string sevenAlt = "JsonStringEnumConverter|JsonSerializerOptions|AddJsonOptions|"
                + "WriteAsString|EnumNamingPolicy|camelCase|PropertyNameCaseInsensitive";
            string r1 = await agenticHost.GrepFileAsync(sevenAlt, apiFile, null, null);
            Assert.Contains("(3 matches)", r1);
            Assert.Contains("348:", r1);
            Assert.Contains("350:", r1);
            Assert.Contains("351:", r1);

            string r2 = await agenticHost.GrepFileAsync("enum|Enum", apiFile, null, null);
            Assert.Contains("(2 matches)", r2);
            Assert.Contains("203:", r2);
            Assert.Contains("351:", r2);
        }

        [Fact]
        public async Task Grep_TranscriptLine_LogsScanWindowAndFileSize()
        {
            // The transcript summary must carry the full call parameters — a bare
            // "0 matches" line made the job-8 false negative undiagnosable from logs.
            var transcript = new StringBuilder();
            var host = new BufferedAgenticHost(_dir, outputSink: (s, _) => transcript.Append(s));
            IAgenticHost agenticHost = host;

            string file = Path.Combine(_dir, "windowed.txt");
            File.WriteAllText(file, string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}")));

            await agenticHost.GrepFileAsync("line 5", "windowed.txt", 2, 8);
            Assert.Contains("[lines 2-8 of 10, requested start_line=2 end_line=8]", transcript.ToString());

            await agenticHost.GrepFileAsync("nope", "windowed.txt", null, null);
            Assert.Contains("[GREP] no matches for \"nope\" in windowed.txt [lines 1-10 of 10]", transcript.ToString());
        }

        // ── Answer sanitization ───────────────────────────────────────────────

        [Theory]
        [InlineData("Done.</think> All files updated.", "Done. All files updated.")]
        [InlineData("Summary<tool_call><function=task_done>{\"summary\":\"x\"}</function></tool_call>", "Summary")]
        [InlineData("Fixed the bug.<tool_call><function=task_done>truncated...", "Fixed the bug.")]
        [InlineData("plain answer", "plain answer")]
        public void SanitizeAnswer_StripsControlTokens(string raw, string expected)
        {
            Assert.Equal(expected, HeadlessAgent.SanitizeAnswer(raw));
        }

        // ── %VAR% expansion under PowerShell ──────────────────────────────────

        [Fact]
        public async Task ShellRunner_ExpandsCmdStyleEnvVars_UnderPowerShell()
        {
            var runner = new ShellRunner(_dir);
            var (output, exitCode) = await runner.ExecuteAsync("Write-Output \"%USERNAME%\"");

            Assert.Equal(0, exitCode);
            Assert.Contains(Environment.UserName, output);
            Assert.DoesNotContain("%USERNAME%", output);
        }

        // ── Knowledge access: search_memory / query_library ──────────────────

        [Fact]
        public async Task SearchMemory_FindsAcrossTopics_WithAlternation()
        {
            var host = new BufferedAgenticHost(_dir);
            IAgenticHost agenticHost = host;

            await agenticHost.SaveMemoryAsync("parsely-frontend",
                "Enums serialize PascalCase (JsonStringEnumConverter, no naming policy).\nDTO types import from types/index.ts.",
                "Parsely frontend conventions");
            await agenticHost.SaveMemoryAsync("build-quirks",
                "Use npm run build; tsc -b runs first.",
                "Build notes");

            string hits = await agenticHost.SearchMemoryAsync("PascalCase|naming policy");
            Assert.Contains("parsely-frontend:1:", hits);
            Assert.DoesNotContain("build-quirks", hits);

            string none = await agenticHost.SearchMemoryAsync("kubernetes");
            Assert.Contains("no matches", none);
        }

        [Fact]
        public async Task SearchMemory_NoTopics_ReturnsGuidance()
        {
            var host = new BufferedAgenticHost(_dir);
            string result = await ((IAgenticHost)host).SearchMemoryAsync("anything");
            Assert.Contains("No memory topics found", result);
        }

        [Fact]
        public async Task QueryLibrary_NotConfigured_ReturnsClearMessage_NeverThrows()
        {
            string result = await DocumentLibrarian.QueryAsTextAsync(
                "http://127.0.0.1:9", connectionString: "", "react hooks rules", 6, CancellationToken.None);
            Assert.Contains("not configured", result);
        }

        // ── Sandbox write restriction ──────────────────────────────────────────

        [Fact]
        public async Task RestrictedHost_AllowsWriteUnderDevMindTempDir()
        {
            var host = new BufferedAgenticHost(_dir) { RestrictWritesToWorkingDirectory = true };

            // Path under <temp>\devmind — should be allowed
            string devmindTempDir = Path.Combine(Path.GetTempPath(), "devmind");
            Directory.CreateDirectory(devmindTempDir);
            string testFile = Path.Combine(devmindTempDir, $"sandbox_test_{Guid.NewGuid():N}.txt");

            try
            {
                var result = await ((IAgenticHost)host).SaveFileAsync(testFile, "hello", false);

                // Should succeed — not null means the write was allowed
                Assert.NotNull(result);
                Assert.True(File.Exists(testFile));

                // Should NOT have a "blocked" action
                Assert.DoesNotContain(host.GetActions(), a => a.Kind == "blocked");
            }
            finally
            {
                // Cleanup
                if (File.Exists(testFile))
                    File.Delete(testFile);
            }
        }

        [Fact]
        public async Task RestrictedHost_BlocksWriteOutsideBothRoots()
        {
            var host = new BufferedAgenticHost(_dir) { RestrictWritesToWorkingDirectory = true };

            // (1) Path directly under <temp> but NOT under <temp>\devmind — should be blocked
            string tempSiblingFile = Path.Combine(Path.GetTempPath(), $"blocked_test_{Guid.NewGuid():N}.txt");

            try
            {
                var result = await ((IAgenticHost)host).SaveFileAsync(tempSiblingFile, "hello", false);

                Assert.Null(result);
                Assert.Contains(host.GetActions(), a => a.Kind == "blocked");
            }
            finally
            {
                if (File.Exists(tempSiblingFile))
                    File.Delete(tempSiblingFile);
            }
        }

        [Fact]
        public async Task RestrictedHost_BlocksWriteToSiblingOfWorkingDir()
        {
            var host = new BufferedAgenticHost(_dir) { RestrictWritesToWorkingDirectory = true };

            // Sibling directory of the working directory — should be blocked
            string siblingDir = Path.Combine(Path.GetDirectoryName(_dir)!, $"sibling_{Guid.NewGuid():N}");
            Directory.CreateDirectory(siblingDir);
            string siblingFile = Path.Combine(siblingDir, "test.txt");

            try
            {
                var result = await ((IAgenticHost)host).SaveFileAsync(siblingFile, "hello", false);

                Assert.Null(result);
                Assert.Contains(host.GetActions(), a => a.Kind == "blocked");
            }
            finally
            {
                if (File.Exists(siblingFile))
                    File.Delete(siblingFile);
                if (Directory.Exists(siblingDir))
                    Directory.Delete(siblingDir);
            }
        }

        // ── Headless addendum: frontend-quality rails ─────────────────────────

        [Fact]
        public void HeadlessAddendum_ContainsTsDiagnosticsAndMemoryRules()
        {
            Assert.Contains("get_diagnostics", HeadlessAgent.HeadlessAddendum);
            Assert.Contains(".ts or .tsx", HeadlessAgent.HeadlessAddendum);
            Assert.Contains("recall_memory", HeadlessAgent.HeadlessAddendum);
            Assert.Contains("query_library", HeadlessAgent.HeadlessAddendum);
        }
    }
}
