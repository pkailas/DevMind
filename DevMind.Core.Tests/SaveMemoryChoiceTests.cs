// File: SaveMemoryChoiceTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// save_memory asks the host whether and where to save (IAgenticHost.ConfirmMemorySaveAsync)
// before anything is written. These pin the executor's routing of each answer to the right
// layer, the tool result the model reads for each, and that a host which does not override
// the question (headless) keeps writing the repo layer exactly as before.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class SaveMemoryChoiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _globalDir;

        public SaveMemoryChoiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_savemem_{Guid.NewGuid():N}");
            _globalDir = Path.Combine(_dir, "_global", "memory");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static AgenticAction ApplyAndBuild()
            => new AgenticAction { Type = ActionType.ApplyAndBuild };

        private static List<ResponseBlock> SaveBlock(string topic = "Sap Spec") => new()
        {
            new ResponseBlock
            {
                Type = BlockType.SaveMemory,
                MemoryTopic = topic,
                MemoryContent = "the spec",
                MemoryDescription = "SAP spec notes",
            },
        };

        /// <summary>Runs one save_memory through the executor; returns the model's tool result.</summary>
        private static async Task<string> RunSaveAsync(IAgenticHost host, List<ResponseBlock> blocks)
        {
            var exec = new AgenticExecutor(host, new FakeLlmOptions());
            var result = await exec.ExecuteAsync(ApplyAndBuild(), new ResponseOutcome(blocks));
            return LoopHelpers.BuildToolResultContent(
                new ToolCallResult { Name = "save_memory", Id = "call_1" }, result, blocks);
        }

        private (FakeHost host, MemoryManager mm) Fake(MemorySaveChoice answer)
        {
            var mm = new MemoryManager(_dir, _globalDir);
            return (new FakeHost(_dir) { Memory = mm, MemorySaveAnswer = answer }, mm);
        }

        [Fact]
        public async Task ChoiceRepo_WritesTheRepoLayer_AndSaysSo()
        {
            var (host, mm) = Fake(MemorySaveChoice.Repo);

            string toolResult = await RunSaveAsync(host, SaveBlock());

            Assert.Equal(new[] { "Sap Spec" }, host.MemorySaveConfirms);
            Assert.Equal(1, host.SaveMemoryCalls);
            Assert.Equal(0, host.SaveGlobalMemoryCalls);
            Assert.Equal("the spec", mm.LoadTopic("sap-spec"));
            Assert.Empty(mm.ListGlobalTopics());
            Assert.Equal(MemoryManager.SavedResult("sap-spec", "folder", mm.TopicPath("sap-spec")), toolResult);
        }

        [Fact]
        public async Task ChoiceGlobal_WritesTheGlobalLayer_RecallableAsGlobalSlug()
        {
            var (host, mm) = Fake(MemorySaveChoice.Global);

            string toolResult = await RunSaveAsync(host, SaveBlock());

            Assert.Equal(0, host.SaveMemoryCalls);
            Assert.Equal(1, host.SaveGlobalMemoryCalls);
            Assert.Null(mm.LoadTopic("sap-spec"));
            Assert.False(File.Exists(Path.Combine(_dir, "MEMORY.md")), "a global save must not touch the repo index");
            Assert.True(File.Exists(Path.Combine(_globalDir, "sap-spec.md")));
            Assert.Contains("global:sap-spec", mm.ListTopicsForRecall());
            Assert.Equal("the spec", mm.RecallTopic("global:sap-spec")?.Content);
            Assert.Equal(MemoryManager.SavedResult("sap-spec", "global", Path.Combine(_globalDir, "sap-spec.md")), toolResult);
        }

        [Fact]
        public async Task Declined_WritesNothing_AndTellsTheModelItWasNotSaved()
        {
            var (host, mm) = Fake(MemorySaveChoice.Declined);

            string toolResult = await RunSaveAsync(host, SaveBlock());

            Assert.Single(host.MemorySaveConfirms);
            Assert.Equal(0, host.SaveMemoryCalls);
            Assert.Equal(0, host.SaveGlobalMemoryCalls);
            Assert.Empty(mm.ListTopics());
            Assert.Empty(mm.ListGlobalTopics());
            Assert.Equal("Not saved — the user declined. Do not say it was saved.", toolResult);
        }

        [Fact]
        public async Task DefaultHost_NoOverride_StillWritesTheRepoLayer()
        {
            // BufferedAgenticHost (headless jobs) does not override ConfirmMemorySaveAsync:
            // the interface default answers Repo, so no prompt and the long-standing write.
            var host = new BufferedAgenticHost(_dir);
            Assert.Equal(MemorySaveChoice.Repo,
                await ((IAgenticHost)host).ConfirmMemorySaveAsync("x", "y", 1));

            string toolResult = await RunSaveAsync(host, SaveBlock());

            Assert.Equal("the spec", new MemoryManager(_dir, _globalDir).LoadTopic("sap-spec"));
            Assert.Contains("[sap-spec]", File.ReadAllText(Path.Combine(_dir, "MEMORY.md")));
            Assert.DoesNotContain("Not saved", toolResult);
        }

        [Fact]
        public void SaveGlobalTopic_NoGlobalDirectory_ReturnsFalse_WritesNothing()
        {
            var mm = new MemoryManager(_dir, globalMemoryDir: null!);
            Assert.False(mm.SaveGlobalTopic("t", "c"));
            Assert.Empty(mm.ListGlobalTopics());
        }

        [Fact]
        public void SaveMemoryGuidance_IsInTheToolDescription_AndTheCatalogBullet()
        {
            string description = ToolRegistry.BuildToolsArray()
                .Select(t => t["function"])
                .Where(f => (string?)f?["name"] == "save_memory")
                .Select(f => (string?)f?["description"])
                .Single()!;
            Assert.Contains("Only save when the user asks you to or agrees to it", description);
            Assert.Contains("this folder or global (all folders)", description);
            Assert.Contains("Never claim something was saved unless this tool's result says it was saved", description);

            string prompt = LoopHelpers.BuildToolUsePrompt(buildCommand: "", projectNamespace: "",
                workingDirectory: @"C:\work\repo");
            string bullet = prompt.Split('\n').Single(l => l.Contains("`save_memory`"));
            Assert.Contains("Save only when the user asks or agrees", bullet);
            Assert.Contains("chooses this folder or global", bullet);
            Assert.Contains("never claim something was saved unless the tool result says so", bullet);
        }
    }
}
