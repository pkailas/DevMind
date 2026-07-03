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
    }
}
