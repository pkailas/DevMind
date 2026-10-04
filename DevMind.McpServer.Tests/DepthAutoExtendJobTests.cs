// File: DepthAutoExtendJobTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Depth-cap auto-extension at the job layer: the settings reach the job and its
// continuation, and the reporting fields (effective_max_depth, depth_extensions_used,
// depth_extensions) come from the right place — the finished turn's result, or the
// requested cap before the job has run. The loop behaviour itself is covered in
// DevMind.Core.Tests (HeadlessDepthAutoExtendTests, ConvergenceTrackerTests).

using System.Text.Json;
using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class DepthAutoExtendJobTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;

        public DepthAutoExtendJobTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_autoext_job_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static AgentJob FinishedJob(params DepthExtension[] extensions) => new()
        {
            Id = "job-autoext",
            Prompt = "p",
            WorkingDirectory = @"C:\temp\hermetic",
            MaxDepth = 40,
            State = AgentJobState.Done,
            Result = new HeadlessAgentResult
            {
                Answer = "done",
                EffectiveMaxDepth = 40 + 20 * extensions.Length,
                DepthExtensions = extensions,
            },
        };

        [Fact]
        public void AFinishedJob_ReportsTheEffectiveCapAndEachExtension()
        {
            var job = FinishedJob(
                new DepthExtension { AtDepth = 40, NewCap = 60, SignalSummary = "2 failure(s) resolved" },
                new DepthExtension { AtDepth = 60, NewCap = 80, SignalSummary = "1 failure(s) resolved" });

            Assert.Equal(80, job.EffectiveMaxDepth);
            Assert.Equal(2, job.DepthExtensionsUsed);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(job.DepthExtensionsPayload()));
            JsonElement first = doc.RootElement[0];
            Assert.Equal(40, first.GetProperty("at_depth").GetInt32());
            Assert.Equal(60, first.GetProperty("new_cap").GetInt32());
            Assert.Equal("2 failure(s) resolved", first.GetProperty("signal_summary").GetString());
            Assert.Equal(2, doc.RootElement.GetArrayLength());
        }

        [Fact]
        public void AQueuedJob_ReportsTheRequestedCap_AndNoExtensions()
        {
            var job = new AgentJob
            {
                Id = "job-autoext-q", Prompt = "p", WorkingDirectory = @"C:\temp\hermetic",
                MaxDepth = 40, State = AgentJobState.Queued,
            };

            Assert.Equal(40, job.EffectiveMaxDepth);
            Assert.Equal(0, job.DepthExtensionsUsed);
            Assert.Empty(job.DepthExtensionsPayload());
            Assert.True(job.AutoExtend);              // default on for delegated jobs
            Assert.Equal(2, job.MaxExtensions);
        }

        [Fact]
        public async Task TheSettings_ReachTheJob_AndAContinuationInheritsThem()
        {
            using var server = new FakeLlmServer();   // every turn ends with task_done
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new AgentJobManager();

            var parent = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: false,
                autoExtend: false, maxExtensions: 4);
            Assert.False(parent.AutoExtend);
            Assert.Equal(4, parent.MaxExtensions);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 20_000 && parent.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(50);
            Assert.Equal(AgentJobState.Done, parent.State);
            Assert.Equal(5, parent.EffectiveMaxDepth);
            Assert.Equal(0, parent.DepthExtensionsUsed);

            var child = mgr.Continue(parent.Id, "continue", 7, 30, verifyBuild: false, out string err);
            Assert.Null(err);
            Assert.NotNull(child);
            Assert.False(child!.AutoExtend);
            Assert.Equal(4, child.MaxExtensions);

            sw.Restart();
            while (sw.ElapsedMilliseconds < 20_000 && child.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(50);
            Assert.Equal(AgentJobState.Done, child.State);
            Assert.Equal(7, child.EffectiveMaxDepth);   // the continuation's own cap, not the parent's
        }
    }
}
