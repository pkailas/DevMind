// File: GlobalMemoryToolTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the MCP tool surface (DevMindTools list_memory_topics / recall_memory /
// search_memory) composing with the machine-level (global) memory layer. Uses the
// internal McpServices(workingDirectory, roots, memoryManager) seam to root the
// memory layer at temp dirs — the real %APPDATA% is never touched.

using DevMind;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public class GlobalMemoryToolTests : IDisposable
    {
        private readonly string _repo;
        private readonly string _global;
        private readonly McpServices _svc;
        private readonly DevMindTools _tools;

        public GlobalMemoryToolTests()
        {
            string baseDir = Path.Combine(Path.GetTempPath(), $"devmind_mcp_global_{Guid.NewGuid():N}");
            _repo = Path.Combine(baseDir, "repo");
            _global = Path.Combine(baseDir, "global");
            Directory.CreateDirectory(_repo);
            // _global deliberately not created — per-test opt-in.

            // Internal test seam: root the memory layer at temp dirs (global dir
            // absent => the "no global dir" state this machine currently has).
            _svc = new McpServices(_repo, additionalWriteRoots: null,
                memoryManager: new MemoryManager(_repo, _global));
            _tools = new DevMindTools(_svc);
        }

        public void Dispose() => _svc.Dispose();

        private void CreateGlobal(string topic, string content)
        {
            Directory.CreateDirectory(_global);
            File.WriteAllText(Path.Combine(_global, topic + ".md"), content);
        }

        [Fact]
        public async Task ListMemoryTopics_NoGlobalDir_IsRepoOnly()
        {
            _svc.Memory.SaveTopic("alpha-topic", "repo alpha", "a");

            string result = await _tools.ListMemoryTopics();

            Assert.Contains("[alpha-topic]", result);
            Assert.DoesNotContain("Global (machine-level)", result);
            Assert.DoesNotContain("global:", result);
        }

        [Fact]
        public async Task ListMemoryTopics_GlobalPresent_IsLabelledAndRecallable()
        {
            _svc.Memory.SaveTopic("alpha-topic", "repo alpha", "a");
            CreateGlobal("gamma-topic", "global gamma");

            string result = await _tools.ListMemoryTopics();

            Assert.Contains("[alpha-topic]", result);
            Assert.Contains("Global (machine-level)", result);
            Assert.Contains("[global:gamma-topic]", result);
            // Repo topics listed before the global section.
            Assert.True(
                result.IndexOf("[alpha-topic]", StringComparison.Ordinal)
                    < result.IndexOf("[global:gamma-topic]", StringComparison.Ordinal),
                "repo topics must precede the global section");
        }

        [Fact]
        public async Task RecallMemory_RepoDefault_ReturnsRepoContent()
        {
            _svc.Memory.SaveTopic("auth-system", "REPO AUTH", "a");
            CreateGlobal("auth-system", "GLOBAL AUTH");

            string result = await _tools.RecallMemory("auth-system");

            Assert.Contains("REPO AUTH", result);
            Assert.DoesNotContain("GLOBAL AUTH", result);
            // The collision is surfaced, never silently resolved.
            Assert.Contains("global:auth-system", result);
        }

        [Fact]
        public async Task RecallMemory_GlobalPrefix_ReturnsGlobalContent()
        {
            _svc.Memory.SaveTopic("auth-system", "REPO AUTH", "a");
            CreateGlobal("auth-system", "GLOBAL AUTH");

            string result = await _tools.RecallMemory("global:auth-system");

            Assert.Contains("GLOBAL AUTH", result);
            Assert.DoesNotContain("REPO AUTH", result);
            Assert.Contains("auth-system", result); // collision note names the repo slug
        }

        [Fact]
        public async Task RecallMemory_GlobalFallback_WhenRepoHasNoTopic()
        {
            // No repo memory dir at all (Parsely / VLink case) — a plain slug still
            // reaches the machine-level layer.
            CreateGlobal("build-quirks", "GLOBAL QUIRKS");

            string result = await _tools.RecallMemory("build-quirks");

            Assert.Contains("GLOBAL QUIRKS", result);
        }

        [Fact]
        public async Task RecallMemory_NotFound_ListsGlobalTopicsTagged()
        {
            CreateGlobal("gamma-topic", "global gamma");

            string result = await _tools.RecallMemory("does-not-exist");

            Assert.Contains("not found", result);
            Assert.Contains("global:gamma-topic", result);
        }

        [Fact]
        public async Task SearchMemory_BothLayers_TagsGlobalHits()
        {
            _svc.Memory.SaveTopic("alpha-topic", "needle in alpha", "a");
            CreateGlobal("gamma-topic", "needle in gamma");

            string result = await _tools.SearchMemory("needle");

            Assert.Contains("[alpha-topic]", result);
            Assert.Contains("[global:gamma-topic]", result);
            Assert.True(
                result.IndexOf("[alpha-topic]", StringComparison.Ordinal)
                    < result.IndexOf("[global:gamma-topic]", StringComparison.Ordinal),
                "repo hits must precede global hits");
        }

        [Fact]
        public async Task SearchMemory_NoGlobalDir_IsRepoOnly()
        {
            _svc.Memory.SaveTopic("alpha-topic", "needle in alpha", "a");

            string result = await _tools.SearchMemory("needle");

            Assert.Contains("[alpha-topic]", result);
            Assert.DoesNotContain("global:", result);
        }
    }
}
