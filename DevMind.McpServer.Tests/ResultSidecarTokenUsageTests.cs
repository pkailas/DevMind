// File: ResultSidecarTokenUsageTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The persisted result sidecar ({id}.result.json, written by
// AgentJobManager.WriteResultSidecar on every job finish and read back by
// devmind_task_result after a server restart) must round-trip the per-job token
// usage fields: tokens_in_total, tokens_in_new, tokens_in_new_partial, tokens_out
// — including a NULL tokens_in_new (server reported no cached/new split), which
// must come back as JSON null, not 0 or a missing key.

using System.Text.Json;
using Xunit;

namespace DevMind.McpServer.Tests
{
    [Collection(ProcessEnvironmentCollection.Name)]
    public sealed class ResultSidecarTokenUsageTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorTasksDir;

        public ResultSidecarTokenUsageTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_sidecar_tokusage_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            // DEVMIND_TASKS_DIR is re-evaluated per access (AgentJobManager.TranscriptDir),
            // and this assembly's ModuleInitializer already pointed it at a per-run dir —
            // point it at THIS test's dir so the sidecar path is deterministic and isolated.
            _priorTasksDir = Environment.GetEnvironmentVariable("DEVMIND_TASKS_DIR");
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _dir);
        }

        public void Dispose()
        {
            // Restore, never clear (H-44 recurrence, 2026-10-02): clearing it dropped every
            // later test in the run back onto the LIVE %TEMP%\devmind\tasks folder.
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _priorTasksDir);
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static AgentJob JobWithUsage(HeadlessAgentResult result) => new()
        {
            Id = "job-tokusage",
            Prompt = "p",
            WorkingDirectory = @"C:\temp\hermetic",
            State = AgentJobState.Done,
            Result = result,
        };

        [Fact]
        public void Sidecar_RoundTripsFullSplit()
        {
            var job = JobWithUsage(new HeadlessAgentResult
            {
                Answer = "done",
                Iterations = 3,
                ElapsedSeconds = 12.5,
                TokensInTotal = 1660,
                TokensInNew = 160,
                TokensOut = 35,
            });

            AgentJobManager.WriteResultSidecar(job);

            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(AgentJobManager.TranscriptDir, "job-tokusage.result.json")));
            var root = doc.RootElement;
            Assert.Equal(1660, root.GetProperty("tokens_in_total").GetInt64());
            Assert.Equal(160, root.GetProperty("tokens_in_new").GetInt64());
            Assert.Equal(35, root.GetProperty("tokens_out").GetInt64());
            Assert.False(root.GetProperty("tokens_in_new_partial").GetBoolean());
            // Pre-existing fields untouched (additive-only contract).
            Assert.Equal(3, root.GetProperty("iterations").GetInt32());
        }

        [Fact]
        public void Sidecar_NullTokensInNew_SerializedAsJsonNull()
        {
            var job = JobWithUsage(new HeadlessAgentResult
            {
                Answer = "done",
                TokensInTotal = 700,
                TokensInNew = null,   // server gave no cached/new split
                TokensOut = 22,
            });

            AgentJobManager.WriteResultSidecar(job);

            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(AgentJobManager.TranscriptDir, "job-tokusage.result.json")));
            var root = doc.RootElement;
            Assert.Equal(700, root.GetProperty("tokens_in_total").GetInt64());
            // Must be an explicit JSON null — not 0, not absent. A missing key and a 0
            // would both read as "the server processed 0 new tokens", which is false.
            Assert.True(root.TryGetProperty("tokens_in_new", out var newEl));
            Assert.Equal(JsonValueKind.Null, newEl.ValueKind);
            Assert.Equal(22, root.GetProperty("tokens_out").GetInt64());
            Assert.False(root.GetProperty("tokens_in_new_partial").GetBoolean());
        }

        [Fact]
        public void Sidecar_PartialSplit_FlagTrue()
        {
            var job = JobWithUsage(new HeadlessAgentResult
            {
                Answer = "done",
                TokensInTotal = 700,
                TokensInNew = 100,
                TokensInNewPartial = true,
                TokensOut = 14,
            });

            AgentJobManager.WriteResultSidecar(job);

            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(AgentJobManager.TranscriptDir, "job-tokusage.result.json")));
            var root = doc.RootElement;
            Assert.Equal(700, root.GetProperty("tokens_in_total").GetInt64());
            Assert.Equal(100, root.GetProperty("tokens_in_new").GetInt64());
            Assert.True(root.GetProperty("tokens_in_new_partial").GetBoolean());
            Assert.Equal(14, root.GetProperty("tokens_out").GetInt64());
        }

        [Fact]
        public void Sidecar_NoUsageAtAll_AllThreeNull()
        {
            var job = JobWithUsage(new HeadlessAgentResult { Answer = "done" });

            AgentJobManager.WriteResultSidecar(job);

            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(AgentJobManager.TranscriptDir, "job-tokusage.result.json")));
            var root = doc.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("tokens_in_total").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("tokens_in_new").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("tokens_out").ValueKind);
            Assert.False(root.GetProperty("tokens_in_new_partial").GetBoolean());
        }
    }
}
