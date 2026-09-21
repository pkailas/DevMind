// File: ScratchpadCompactionSafetyTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Three history passes can reach a message between sends, and each is a way for the
// scratchpad to go wrong:
//
//   * MicroCompactToolResultsAsync CLEARS history outright and rebuilds it from the system
//     message plus one synthetic user turn — it would delete a live scratchpad.
//   * TrimOldestTurns forms removal groups over everything except the final message — with
//     the scratchpad second-to-last, it is inside that range.
//   * EvictStaleContext drops by turn age — a scratchpad left in place across turns would
//     eventually age out, or worse, linger as a stale copy.
//
// Rather than exempt the message from all three (three exemptions that must each stay
// correct as those passes change), the scratchpad is removed at the top of every send and
// rebuilt immediately before the request is serialized. None of the passes ever sees one.
// That makes the safety property an ORDERING, so the ordering is what is pinned here —
// behaviourally where a test can drive it, and structurally where it cannot.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class ScratchpadCompactionSafetyTests
    {
        // ── Structural: the ordering that makes the hazard impossible ───────────

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

        [Fact]
        public void TheScratchpadIsRemovedBeforeEveryCompactionPass_AndRebuiltAfterThemAll()
        {
            string path = Path.Combine(RepoRoot(), "DevMind.Core", "LlmClient.cs");
            Assert.True(File.Exists(path), $"source not found: {path} — RepoRoot() resolved wrong, the guard is vacuous.");

            string[] lines = File.ReadAllLines(path);

            int Only(string needle)
            {
                var hits = new List<int>();
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Contains(needle, StringComparison.Ordinal)
                        && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)
                        && !lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal))
                        hits.Add(i);
                Assert.True(hits.Count == 1, $"expected exactly one call site for \"{needle}\", found {hits.Count}");
                return hits[0];
            }

            int remove = Only("RemoveScratchpadMessage();");
            int insert = Only("InsertScratchpadMessage();");
            int request = Only("BuildRequestJson(modelName,");

            // Call sites inside the send, between the removal and the request. A method
            // DECLARATION elsewhere in the file is not a call site, so declarations are
            // excluded by the window rather than by pattern-matching their signatures.
            List<int> CallsInSend(string needle)
            {
                var hits = new List<int>();
                for (int i = remove; i <= request; i++)
                {
                    string t = lines[i].TrimStart();
                    if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (lines[i].Contains(needle, StringComparison.Ordinal)) hits.Add(i);
                }
                return hits;
            }

            var passes = new List<int>();
            passes.AddRange(CallsInSend("EvictStaleContext();"));
            passes.AddRange(CallsInSend("TrimOldestTurns("));
            passes.AddRange(CallsInSend("await MicroCompactToolResultsAsync()"));

            Assert.True(passes.Count >= 4,
                $"found only {passes.Count} compaction call sites — the guard has lost track of them");

            foreach (int pass in passes)
            {
                Assert.True(remove < pass,
                    $"a compaction pass at line {pass + 1} runs BEFORE the scratchpad is removed — it can delete or preserve one");
                Assert.True(insert > pass,
                    $"the scratchpad is inserted at line {insert + 1}, before the compaction pass at line {pass + 1} — that pass can reach it");
            }

            Assert.True(insert < request,
                "the scratchpad is inserted after the request is built, so it never reaches the wire");
        }

        // ── Behavioural: it survives turns of aggressive eviction, and stays current ──

        /// <summary>Aggressive eviction with a drop age of 5 turns, so messages really do age out.</summary>
        private sealed class EvictingOptions : ILlmOptions
        {
            public string SystemPrompt => "You are a test assistant.";
            public string ModelName => "test-model";
            public int RequestTimeoutMinutes => 1;
            public int FirstTokenTimeoutMinutes => 1;
            public bool ShowDebugOutput => false;
            public bool ShowContextBudget => false;
            public bool ShowLlmThinking => false;
            public ContextEvictionMode ContextEviction => ContextEvictionMode.Aggressive;
            public int ManualContextSize => 32768;
            public LlmServerType ServerType => LlmServerType.LlamaServer;
            public string CustomContextEndpoint => null!;
            public int MicroCompactThreshold => 99;
            public int NearlineIngestThresholdChars => 8_000;
            public bool MicroCompactSummarize => false;
            public bool MicroCompactBrainwash => false;
            public bool AlwaysConfirmPatch => false;
            public int AgenticLoopMaxDepth => 25;
            public int AgenticContextLimitPercent => 0;
        }

        [Fact]
        public async Task AcrossEnoughTurnsToEvict_TheScratchpadIsStillPresentAndCurrent()
        {
            using var server = new FakeSseServer();
            var client = new LlmClient(new EvictingOptions());

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try { client.Configure(server.BaseUrl, apiKey: null!); }
            finally { Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior); }

            // Well past the aggressive drop age of 5 turns.
            const int turns = 12;
            for (int t = 0; t < turns; t++)
            {
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.IncrementTurn();
                await client.SendMessageAsync(
                    $"turn {t}",
                    onToken: _ => { },
                    onComplete: () => done.TrySetResult(true),
                    onError: ex => done.TrySetException(ex),
                    combinedSystemPrompt: "You are a test assistant.",
                    taskScratchpad: $"state after turn {t}");

                Assert.True(await Task.WhenAny(done.Task, Task.Delay(15000)) == done.Task, "send timed out");
                await done.Task;
            }

            var messages = (Newtonsoft.Json.Linq.JArray)Newtonsoft.Json.Linq.JObject
                .Parse(server.RequestBodies[^1])["messages"]!;

            int found = 0;
            string current = "";
            foreach (var m in messages)
            {
                string content = (string?)m["content"] ?? "";
                if (content.Contains("--- CURRENT SCRATCHPAD ---", StringComparison.Ordinal))
                {
                    found++;
                    current = content;
                }
            }

            Assert.Equal(1, found);                                     // not deleted, not duplicated
            Assert.Contains($"state after turn {turns - 1}", current, StringComparison.Ordinal);
            Assert.DoesNotContain("state after turn 0", current, StringComparison.Ordinal);  // not stale
        }
    }
}
