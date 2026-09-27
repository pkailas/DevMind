// File: VerificationRebuildTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-32 / H-13, end to end through the job manager and devmind_task_result: a working directory
// whose resolved build command is a plain `dotnet build <sln>` is verified with -t:Rebuild and
// reports warning_count_verified = true with the parsed count; DEVMIND_VERIFY_REBUILD=0 restores
// the incremental build and the unverified label. Only the process run is faked
// (BuildExecOverride) — resolution, the rewrite and the parse are the production code, and the
// seam records the exact command the harness would have executed.

using System.Diagnostics;
using System.Text.Json;
using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class VerificationRebuildTests : IDisposable
    {
        private const string RebuildOutputOneWarning =
            "C:\\src\\W\\Program.cs(1,5): warning CS0168: The variable 'x' is declared but never used [C:\\src\\W\\W.csproj]\r\n" +
            "  W -> C:\\src\\W\\bin\\Debug\\net10.0\\W.dll\r\n\r\nBuild succeeded.\r\n\r\n" +
            "C:\\src\\W\\Program.cs(1,5): warning CS0168: The variable 'x' is declared but never used [C:\\src\\W\\W.csproj]\r\n" +
            "    1 Warning(s)\r\n    0 Error(s)\r\n\r\nTime Elapsed 00:00:00.69\r\n";

        private readonly string _dir;
        private readonly Dictionary<string, string?> _priorEnv = new();

        public VerificationRebuildTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h32_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            // A solution file makes BuildCommandResolver return `dotnet build "<dir>\W.slnx"`.
            File.WriteAllText(Path.Combine(_dir, "W.slnx"), "<Solution>\n</Solution>\n");
            foreach (string name in new[] { "DEVMIND_ENDPOINT", "DEVMIND_SERVER_TYPE", "DEVMIND_BUILD_COMMAND", VerificationBuild.RebuildEnvVar })
                _priorEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            Environment.SetEnvironmentVariable("DEVMIND_BUILD_COMMAND", null);
            Environment.SetEnvironmentVariable(VerificationBuild.RebuildEnvVar, null);
        }

        public void Dispose()
        {
            foreach (var kv in _priorEnv) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private async Task<(string command, JsonElement bv)> RunJobAsync()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = new AgentJobManager();
            string? ran = null;
            mgr.BuildExecOverride = (command, _) =>
            {
                ran = command;
                return Task.FromResult((RebuildOutputOneWarning, 0));
            };

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: true, verifyTests: false);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 30_000 && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(20);
            Assert.Equal(AgentJobState.Done, job.State);
            Assert.NotNull(ran);

            string json = await new AgentTaskTools(mgr).TaskResult(job.Id, CancellationToken.None);
            var doc = JsonDocument.Parse(json);
            return (ran!, doc.RootElement.GetProperty("build_verification").Clone());
        }

        [Fact]
        public async Task PlainDotnetBuild_IsVerifiedAsAFullRebuild()
        {
            var (command, bv) = await RunJobAsync();

            Assert.StartsWith("dotnet build \"", command);
            Assert.EndsWith("W.slnx\" -t:Rebuild", command);
            Assert.Equal(command, bv.GetProperty("command").GetString());
            Assert.True(bv.GetProperty("warning_count_verified").GetBoolean());
            Assert.Equal(1, bv.GetProperty("warning_count").GetInt32());
            Assert.Contains("[verification] Full rebuild: warning count is verified - 1 warning(s).",
                bv.GetProperty("output_tail").GetString());
        }

        [Fact]
        public async Task EnvVarOff_IsIncremental_AndUnverified()
        {
            Environment.SetEnvironmentVariable(VerificationBuild.RebuildEnvVar, "0");

            var (command, bv) = await RunJobAsync();

            Assert.DoesNotContain("Rebuild", command);
            Assert.False(bv.GetProperty("warning_count_verified").GetBoolean());
            Assert.Equal(JsonValueKind.Null, bv.GetProperty("warning_count").ValueKind);
            Assert.Contains("Warning count above is NOT verified", bv.GetProperty("output_tail").GetString());
        }
    }
}
