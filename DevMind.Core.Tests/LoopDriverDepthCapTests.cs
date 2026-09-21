// File: LoopDriverDepthCapTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// When the loop hit the depth cap with the build still failing it told the operator:
//
//     Type UNDO to revert all changes, or continue editing manually.
//     (N change(s) can be undone)
//
// There is no UNDO. No handler, no slash command, no UndoLastPatch, nothing that restores
// a file from a patch backup — the backup stack is only ever pushed to, evicted from and
// drained. Outside _archive/ the only other occurrence of the token in production source
// is a keyword string in ContextEngine's identifier list.
//
// It is also the worst moment in the run to say it: the loop has just abandoned a
// half-edited tree with a broken build, and the operator is told they can put it back.
// The same claim was removed from the two hosts' model-facing output earlier; this copy
// was out of that change's scope and survived — which is exactly why the repo-wide guard
// at the bottom of this file is worth more than the message assertions above it.
//
// "The undo line is gone" on its own would pass if the whole message vanished, so what
// must survive is pinned alongside it: the depth, the build-still-failing distinction, and
// the replacement fact.

using Xunit;
using static DevMind.Core.Tests.LoopDriverTest;

namespace DevMind.Core.Tests
{
    public sealed class LoopDriverDepthCapTests
    {
        private const int MaxDepth = 25;   // FakeLlmOptions.AgenticLoopMaxDepth

        private static ToolCallResult Shell(string command)
            => Tool("run_shell", "{\"command\":\"" + command + "\"}");

        /// <summary>Parks the loop one iteration short of nothing — the cap check runs
        /// BEFORE the depth increment, so seeding the state is what puts the next turn at
        /// the cap without driving 25 real iterations.</summary>
        private static async Task<(Env env, LoopIterationResult turn)> AtCapAsync(bool buildFailing)
        {
            var env = new Env();
            const string cmd = "dotnet build";
            env.Host.ShellResults[cmd] = buildFailing ? (1, "error CS0103: broken") : (0, "");
            env.State.AgenticDepth = MaxDepth;

            var turn = await env.RunToolTurn(Shell(cmd));
            return (env, turn);
        }

        // ── The failing-build branch ────────────────────────────────────────────

        [Fact]
        public async Task DepthCapWithFailingBuild_DoesNotOfferAnUndoThatDoesNotExist()
        {
            var (env, turn) = await AtCapAsync(buildFailing: true);
            using (env)
            {
                Assert.Equal(LoopIterationKind.Terminal, turn.Kind);
                Assert.Equal("depth_cap", turn.TerminalReason);

                string output = env.Host.Output;

                Assert.DoesNotContain("UNDO", output, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("can be undone", output, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("revert all changes", output, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task DepthCapWithFailingBuild_StillReportsTheDepthAndTheBrokenBuild()
        {
            var (env, turn) = await AtCapAsync(buildFailing: true);
            using (env)
            {
                string output = env.Host.Output;

                // The rest of the message survives — this is not "the line vanished".
                Assert.Contains($"[AGENTIC] Depth cap reached ({MaxDepth}) — build still failing.",
                    output, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task DepthCapWithFailingBuild_SaysWhatIsActuallyTrueAboutTheTree()
        {
            var (env, turn) = await AtCapAsync(buildFailing: true);
            using (env)
            {
                string output = env.Host.Output;

                Assert.Contains("on disk and have not been reverted", output, StringComparison.Ordinal);
                Assert.Contains("the working tree is mid-change", output, StringComparison.Ordinal);
                Assert.Contains("revert the changes yourself", output, StringComparison.Ordinal);

                // The working directory is frequently not a git repository, so the message
                // must not assume one.
                Assert.DoesNotContain("git ", output, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("stash", output, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── The clean branch stays distinct ─────────────────────────────────────

        // "The build is broken" is genuinely different information from a clean stop, and
        // it is the most actionable thing the operator can be told at this point. The two
        // branches therefore stay separate rather than collapsing into one message.
        [Fact]
        public async Task DepthCapWithCleanBuild_KeepsItsOwnQuieterMessage()
        {
            var (env, turn) = await AtCapAsync(buildFailing: false);
            using (env)
            {
                Assert.Equal(LoopIterationKind.Terminal, turn.Kind);
                Assert.Equal("depth_cap", turn.TerminalReason);

                string output = env.Host.Output;

                Assert.Contains($"[AGENTIC] Depth cap reached ({MaxDepth}). Stopping.", output, StringComparison.Ordinal);
                Assert.DoesNotContain("build still failing", output, StringComparison.Ordinal);
                Assert.DoesNotContain("UNDO", output, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── Repo-wide guard ─────────────────────────────────────────────────────

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

        /// <summary>
        /// Phrases that promise an undo. Deliberately phrase-based rather than token-based:
        /// the bare token "UNDO" legitimately appears in ContextEngine's keyword list, and a
        /// guard that tripped on it would be turned off rather than fixed.
        /// </summary>
        public static IEnumerable<object[]> UndoPromises() => new[]
        {
            new object[] { "type undo" },
            new object[] { "can be undone" },
            new object[] { "revert all changes" },
            new object[] { "undolastpatch" },
        };

        [Theory]
        [MemberData(nameof(UndoPromises))]
        public void NoProductionSourcePromisesAnUndo(string phrase)
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
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(phrase, StringComparison.OrdinalIgnoreCase))
                        offenders.Add($"{rel}:{i + 1}");
                }
            }

            Assert.True(scanned > 0, "Scanned zero source files — RepoRoot() resolved wrong; the guard is vacuous.");
            Assert.True(offenders.Count == 0,
                $"Production source promises an undo (\"{phrase}\"), but nothing in Core, TUI, Cli or " +
                "McpServer restores a file from a patch backup — there is no handler and no command. " +
                "Either remove the claim or ship a real restore. Offenders: " + string.Join(", ", offenders));
        }
    }
}
