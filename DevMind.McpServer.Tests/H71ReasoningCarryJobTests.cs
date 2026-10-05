// File: H71ReasoningCarryJobTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-71, the McpServer side: carry_cutoff_reasoning (default true, inherited by a continuation
// unless it sets its own), and think_budget_cutoffs / reasoning_carry_notes /
// reasoning_carry_escalations in the live result and the result sidecar — and a sidecar
// written before those fields existed still serves. The loop behaviour itself is pinned in
// DevMind.Core.Tests\ReasoningCutoffCarryTests.cs.

using System.Text.Json;
using Xunit;

namespace DevMind.McpServer.Tests
{
    [Collection(ProcessEnvironmentCollection.Name)]
    public sealed class H71ReasoningCarryJobTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _work;
        private readonly string _tasksDir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;
        private readonly string? _priorTasksDir;

        public H71ReasoningCarryJobTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h71job_{Guid.NewGuid():N}");
            _work = Path.Combine(_dir, "work");
            _tasksDir = Path.Combine(_dir, "tasks");
            Directory.CreateDirectory(_work);
            Directory.CreateDirectory(_tasksDir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            _priorTasksDir = Environment.GetEnvironmentVariable("DEVMIND_TASKS_DIR");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _tasksDir);
        }

        public void Dispose()
        {
            // Restore, never clear (see ResultSidecarTokenUsageTests).
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _priorTasksDir);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static async Task WaitForEnd(AgentJob job, int timeoutMs = 60_000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(50);
            Assert.Equal(AgentJobState.Done, job.State);
        }

        [Fact]
        public void Sidecar_PersistsTheCarryCounts()
        {
            var job = new AgentJob
            {
                Id = "job-h71counts",
                Prompt = "p",
                WorkingDirectory = @"C:\temp\hermetic",
                State = AgentJobState.Done,
                Think = true,
                ReasoningEffort = "medium",
                Result = new HeadlessAgentResult
                {
                    Answer = "done",
                    ThinkBudgetCutoffs = 7,
                    ReasoningCarryNotes = 5,
                    ReasoningCarryEscalations = 2,
                },
            };
            AgentJobManager.WriteResultSidecar(job);

            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(AgentJobManager.TranscriptDir, "job-h71counts.result.json")));
            Assert.Equal(7, doc.RootElement.GetProperty("think_budget_cutoffs").GetInt32());
            Assert.Equal(5, doc.RootElement.GetProperty("reasoning_carry_notes").GetInt32());
            Assert.Equal(2, doc.RootElement.GetProperty("reasoning_carry_escalations").GetInt32());
        }

        [Fact]
        public async Task OldSidecarWithoutTheFields_StillServes()
        {
            Directory.CreateDirectory(AgentJobManager.TranscriptDir);
            File.WriteAllText(Path.Combine(AgentJobManager.TranscriptDir, "job-h71old.result.json"),
                "{\"job_id\":\"job-h71old\",\"state\":\"done\",\"answer\":\"old answer\",\"actions\":[]," +
                "\"iterations\":3,\"auto_think_escalations\":0,\"auto_think_iterations\":0}");

            using var mgr = new AgentJobManager();
            using var doc = JsonDocument.Parse(await new AgentTaskTools(mgr).TaskResult("job-h71old", CancellationToken.None));
            var result = doc.RootElement.GetProperty("result");
            Assert.Equal("old answer", result.GetProperty("answer").GetString());
            Assert.False(result.TryGetProperty("think_budget_cutoffs", out _));
        }

        [Fact]
        public async Task LiveResult_ReportsTheCounts_AndContinuationsInheritTheSwitch()
        {
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new AgentJobManager();
            var tools = new AgentTaskTools(mgr);

            // Default on.
            var p1 = mgr.Start("p", _work, 5, 30, allowCommit: false, verifyBuild: false, think: true);
            Assert.True(p1.CarryCutoffReasoning);
            await WaitForEnd(p1);

            using (var doc = JsonDocument.Parse(await tools.TaskResult(p1.Id, CancellationToken.None)))
            {
                Assert.Equal(0, doc.RootElement.GetProperty("think_budget_cutoffs").GetInt32());
                Assert.Equal(0, doc.RootElement.GetProperty("reasoning_carry_notes").GetInt32());
                Assert.Equal(0, doc.RootElement.GetProperty("reasoning_carry_escalations").GetInt32());
            }

            // Off on the start: the continuation inherits it; an explicit value overrides it.
            var p2 = mgr.Start("p", _work, 5, 30, allowCommit: false, verifyBuild: false, think: true,
                carryCutoffReasoning: false);
            Assert.False(p2.CarryCutoffReasoning);
            await WaitForEnd(p2);

            var c1 = mgr.Continue(p2.Id, "continue", 5, 30, verifyBuild: false, out string err1);
            Assert.Null(err1);
            Assert.False(c1!.CarryCutoffReasoning);
            await WaitForEnd(c1);

            var c2 = mgr.Continue(c1.Id, "continue", 5, 30, verifyBuild: false, out string err2,
                carryCutoffReasoning: true);
            Assert.Null(err2);
            Assert.True(c2!.CarryCutoffReasoning);
            await WaitForEnd(c2);
        }
    }
}
