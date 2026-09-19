// File: ContextStrategyTests.cs  v1.0
//
// Covers the hybrid-attention context work: CacheReuseMonitor sampling/verdicts,
// ingest-time nearline capping in AddToolResultMessage, key-based nearline recall,
// and the DEVMIND_CONTEXT_STRATEGY override.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class CacheReuseMonitorTests
    {
        [Fact]
        public void Verdict_IsUnknown_UntilMinimumSamples()
        {
            var m = new CacheReuseMonitor();
            Assert.Equal(CacheReuseVerdict.Unknown, m.Verdict);

            m.RecordTurn(promptN: 100, cacheN: 9_900, historyMutated: false);
            m.RecordTurn(promptN: 100, cacheN: 9_900, historyMutated: false);
            Assert.Equal(CacheReuseVerdict.Unknown, m.Verdict); // only 2 samples

            m.RecordTurn(promptN: 100, cacheN: 9_900, historyMutated: false);
            Assert.Equal(CacheReuseVerdict.Reusing, m.Verdict);
        }

        [Fact]
        public void Verdict_NotReusing_WhenBackendRePrefillsEveryTurn()
        {
            var m = new CacheReuseMonitor();
            for (int i = 0; i < 3; i++)
                m.RecordTurn(promptN: 10_000, cacheN: 0, historyMutated: false);

            Assert.Equal(CacheReuseVerdict.NotReusing, m.Verdict);
        }

        [Fact]
        public void MutatedTurns_UpdateLastRatio_ButAreExcludedFromWindow()
        {
            var m = new CacheReuseMonitor();
            for (int i = 0; i < 5; i++)
                m.RecordTurn(promptN: 10_000, cacheN: 0, historyMutated: true);

            Assert.Equal(0, m.SampleCount);                    // rebuild turns prove nothing
            Assert.Equal(CacheReuseVerdict.Unknown, m.Verdict);
            Assert.Equal(0.0, m.LastReuseRatio);               // but the display ratio updates
            Assert.True(m.LastTurnWasRebuild);
        }

        [Fact]
        public void TinyPrompts_AreExcludedFromWindow()
        {
            var m = new CacheReuseMonitor();
            for (int i = 0; i < 5; i++)
                m.RecordTurn(promptN: 100, cacheN: 0, historyMutated: false); // total < 512

            Assert.Equal(0, m.SampleCount);
            Assert.Equal(CacheReuseVerdict.Unknown, m.Verdict);
        }

        [Fact]
        public void ZeroTotal_IsIgnoredEntirely()
        {
            var m = new CacheReuseMonitor();
            m.RecordTurn(promptN: 0, cacheN: 0, historyMutated: false);
            Assert.Equal(-1, m.LastReuseRatio); // no measurement yet
        }
    }

    public class NearlineIngestCapTests
    {
        [Fact]
        public void OversizedToolResult_IsFullyCached_AtIngest()
        {
            var client = new LlmClient(new FakeLlmOptions());
            string big = new string('x', 20_000);

            client.AddToolResultMessage("call_1", big);

            // Full content is in the cache under the tool-call key…
            Assert.Equal(big, client.NearlineCache.Retrieve("tool:call_1"));

            // …and a live handle maps back to that key for recall_cache.
            var pair = client.NearlineCache.Handles.Single();
            Assert.StartsWith("nl-", pair.Key);
            Assert.Equal("tool:call_1", pair.Value);
        }

        [Fact]
        public void RecallCacheResults_AreExemptFromIngestCapping()
        {
            // A recall_cache result re-entering history MUST arrive verbatim: capping it
            // would hand the model another excerpt with a fresh handle, forever hiding
            // the middle of any large content behind an infinite recall loop.
            var client = new LlmClient(new FakeLlmOptions());
            string big = new string('x', 20_000);

            client.AddToolResultMessage("call_2", big, toolName: "recall_cache");

            Assert.Equal(0, client.NearlineCache.Count); // no re-store, no new handle
        }

        [Fact]
        public void SmallToolResult_IsNotCached()
        {
            var client = new LlmClient(new FakeLlmOptions());
            client.AddToolResultMessage("call_1", new string('x', 2_000));

            Assert.Equal(0, client.NearlineCache.Count);
        }

        [Fact]
        public void RecallByKey_WorksWithoutHandle()
        {
            // After a brainwash the breadcrumb handles are gone; the synthetic prompt
            // advertises keys, so retrieval by key must work directly.
            var cache = new NearlineCache();
            cache.Store("read:foo.cs", "full file content", "[cached — foo.cs]");

            Assert.Equal("full file content", cache.Retrieve("read:foo.cs"));
        }
    }

    // Verifies the ingest choke point in AddToolResultMessage end to end: what actually
    // lands in conversation history (via the test-only LastToolResultContent accessor), the
    // durable spill file the full text is written to, and its sha256. The 8_000-char default
    // threshold comes from FakeLlmOptions.NearlineIngestThresholdChars.
    public class ToolResultIngestSpillTests
    {
        private static string Sha256Hex(string text)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        [Fact]
        public void UnderThreshold_PassesThroughUnchanged()
        {
            var client = new LlmClient(new FakeLlmOptions());
            string small = new string('a', 2_000); // well under the 8_000 default

            client.AddToolResultMessage("call_1", small);

            Assert.Equal(small, client.LastToolResultContent); // verbatim, no marker appended
            Assert.Equal(0, client.NearlineCache.Count);       // nothing cached, no handle
        }

        [Fact]
        public void OverThresholdByChars_IsTruncatedWithMarker()
        {
            var client = new LlmClient(new FakeLlmOptions());
            string big = new string('x', 20_000); // over the 8_000 default

            client.AddToolResultMessage("call_2", big);

            string stored = client.LastToolResultContent;
            Assert.NotNull(stored);
            Assert.True(stored.Length < big.Length);                  // truncated, not verbatim
            Assert.StartsWith(big.Substring(0, 4_000), stored);       // head preview kept
            Assert.EndsWith(big.Substring(big.Length - 2_000), stored); // tail preview kept
            Assert.Contains("chars omitted", stored);                 // truncation marker present
        }

        [Fact]
        public void ReadFile_IsExemptFromIngestCapping()
        {
            var client = new LlmClient(new FakeLlmOptions());
            string big = new string('y', 20_000); // over the default, but read_file is exempt

            client.AddToolResultMessage("call_3", big, toolName: "read_file");

            Assert.Equal(big, client.LastToolResultContent); // verbatim, not capped
            Assert.Equal(0, client.NearlineCache.Count);     // no cache entry, no handle, no spill
        }

        [Fact]
        public void OverThreshold_SpillsFileWithMatchingSha256()
        {
            var client = new LlmClient(new FakeLlmOptions());
            string big = "line1\n" + new string('z', 15_000) + "\nlast-line"; // over threshold, has newlines

            string? spillPath = null;
            try
            {
                client.AddToolResultMessage("call_4", big);

                string expectedHash = Sha256Hex(big);
                spillPath = Path.GetFullPath(Path.Combine(
                    BufferedAgenticHost.OutputDirectory, $"dm_toolout_{expectedHash}.txt"));

                Assert.True(File.Exists(spillPath));            // durable file was written
                Assert.Equal(big, File.ReadAllText(spillPath)); // holds the FULL original, not the excerpt

                string stored = client.LastToolResultContent;
                Assert.Contains(spillPath, stored);             // marker points the model at the file
                Assert.Contains(expectedHash, stored);          // marker carries the sha256 of the original
            }
            finally
            {
                if (spillPath != null && File.Exists(spillPath)) File.Delete(spillPath);
            }
        }
    }

    public class ContextStrategyOverrideTests
    {
        [Fact]
        public void EnvOverride_ForcesHybridStrategy()
        {
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_CONTEXT_STRATEGY");
            Environment.SetEnvironmentVariable("DEVMIND_CONTEXT_STRATEGY", "hybrid");
            try
            {
                var client = new LlmClient(new FakeLlmOptions());
                Assert.Equal(ContextStrategy.Hybrid, client.EffectiveContextStrategy);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_CONTEXT_STRATEGY", prior);
            }
        }

        [Fact]
        public void NoOverride_NoSamples_NoHint_DefaultsToTransformer()
        {
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_CONTEXT_STRATEGY");
            Environment.SetEnvironmentVariable("DEVMIND_CONTEXT_STRATEGY", null);
            try
            {
                var client = new LlmClient(new FakeLlmOptions());
                Assert.Equal(ContextStrategy.Transformer, client.EffectiveContextStrategy);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_CONTEXT_STRATEGY", prior);
            }
        }
    }
}
