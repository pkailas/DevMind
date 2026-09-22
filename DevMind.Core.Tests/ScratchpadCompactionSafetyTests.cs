// File: ScratchpadCompactionSafetyTests.cs  v2.0
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
// correct as those passes change), the scratchpad is removed before any pass runs and
// rebuilt immediately before the request is serialized. None of the passes ever sees one.
// That makes the safety property an ORDERING, so the ordering is what is pinned here —
// behaviourally where a test can drive it, and structurally where it cannot.
//
// The structural half was rewritten when the send stopped being linear. Context-overflow
// recovery compacts and re-serializes a SECOND time inside one send, so "exactly one call
// site each, in ascending line order" — the old encoding — now describes a file layout
// rather than the property. Worse, it would have forbidden the very call that keeps the
// property true on the new path: the recovery must remove the scratchpad again before it
// force-compacts, because by then one has already been inserted for the first attempt.
//
// So the guard now walks the call sites in file order as a state machine: a removal clears
// the flag, an insertion sets it, every compaction pass must run with it clear, and every
// request serialization must run with it set. That is the property itself, it holds for any
// number of attempts, and it still fails if a pass is ever moved to the wrong side.

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

        private enum Site { Remove, Insert, Pass, Serialize }

        /// <summary>
        /// Classifies a line of LlmClient.cs as one of the four events this ordering is made
        /// of, or null. Comments and method declarations are not call sites: the declarations
        /// are excluded by requiring the call's own punctuation rather than by matching
        /// signatures, and the expression-bodied overload forward is excluded explicitly —
        /// it is a signature adapter, not a compaction.
        /// </summary>
        private static Site? Classify(string line)
        {
            string t = line.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal)) return null;

            if (line.Contains("=> MicroCompactToolResultsAsync(", StringComparison.Ordinal)) return null;

            if (line.Contains("RemoveScratchpadMessage();", StringComparison.Ordinal)) return Site.Remove;
            if (line.Contains("InsertScratchpadMessage();", StringComparison.Ordinal)) return Site.Insert;

            if (line.Contains("EvictStaleContext();", StringComparison.Ordinal)) return Site.Pass;
            if (line.Contains("TrimOldestTurns(", StringComparison.Ordinal)) return Site.Pass;
            if (line.Contains("await MicroCompactToolResultsAsync()", StringComparison.Ordinal)) return Site.Pass;
            if (line.Contains("MicroCompactToolResultsAsync(force:", StringComparison.Ordinal)) return Site.Pass;

            if (line.Contains("BuildRequestJson(modelName,", StringComparison.Ordinal)) return Site.Serialize;

            return null;
        }

        [Fact]
        public void NoCompactionPassEverRunsWithAScratchpadInHistory_AndEverySendSerializesWithOne()
        {
            string path = Path.Combine(RepoRoot(), "DevMind.Core", "LlmClient.cs");
            Assert.True(File.Exists(path), $"source not found: {path} — RepoRoot() resolved wrong, the guard is vacuous.");

            string[] lines = File.ReadAllLines(path);

            var sites = new List<(int Line, Site Kind)>();
            for (int i = 0; i < lines.Length; i++)
            {
                Site? kind = Classify(lines[i]);
                if (kind.HasValue) sites.Add((i + 1, kind.Value));
            }

            // If the needles stop matching, every assertion below passes vacuously.
            int passes = sites.Count(s => s.Kind == Site.Pass);
            Assert.True(passes >= 4, $"found only {passes} compaction call sites — the guard has lost track of them");
            Assert.Contains(sites, s => s.Kind == Site.Remove);
            Assert.Contains(sites, s => s.Kind == Site.Insert);
            Assert.Contains(sites, s => s.Kind == Site.Serialize);

            // The send begins with no scratchpad in history: it is removed at the top, before
            // anything reads or rewrites the conversation.
            Assert.Equal(Site.Remove, sites[0].Kind);

            bool scratchpadPresent = false;
            foreach (var (line, kind) in sites)
            {
                switch (kind)
                {
                    case Site.Remove:
                        scratchpadPresent = false;
                        break;

                    case Site.Insert:
                        scratchpadPresent = true;
                        break;

                    case Site.Pass:
                        Assert.False(scratchpadPresent,
                            $"the compaction pass at line {line} runs while a scratchpad is in history — " +
                            "it can delete the live one or carry a stale one forward. Remove it first.");
                        break;

                    case Site.Serialize:
                        Assert.True(scratchpadPresent,
                            $"the request serialized at line {line} was built with no scratchpad inserted — " +
                            "the model loses its cross-turn state for this turn.");
                        break;
                }
            }
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
