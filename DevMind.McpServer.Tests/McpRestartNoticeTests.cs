// File: McpRestartNoticeTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// job-1740: the job's comfy-mcp was killed mid-job, the next call relaunched it and
// succeeded, and nothing said so. These tests reproduce that with a real stdio MCP server —
// DevMind.McpServer.exe itself, built next to this assembly, started read-only (--root) — so
// the relaunch path runs for real: the process dies, the next call notices, relaunches, and
// the manager announces it exactly once. The headless wiring is the real one too:
// BufferedAgenticHost.RecordMcpNotice is what HeadlessSession subscribes.

using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class McpRestartNoticeTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcprestart_{Guid.NewGuid():N}");

        public McpRestartNoticeTests()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private McpServerConfig DevMindServer() => new McpServerConfig
        {
            Name = "dm",
            Command = Path.Combine(AppContext.BaseDirectory, "DevMind.McpServer.exe"),
            Args = new[] { "--root", _dir },
            CallTimeoutSeconds = 60,
        };

        private static JObject ListArgs() => new JObject { ["glob"] = "*.txt" };

        private static void Kill(int pid)
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            Assert.True(p.WaitForExit(10_000), $"server {pid} did not die");
        }

        [Fact]
        public async Task A_killed_server_is_relaunched_with_one_notice_and_journaled()
        {
            await using var m = new McpClientManager(new[] { DevMindServer() });
            var notices = new List<McpNotice>();
            m.Notice += n => { lock (notices) notices.Add(n); };

            // The headless wiring: the session subscribes its host, and the executor journals calls.
            var host = new BufferedAgenticHost(_dir);
            m.Notice += host.RecordMcpNotice;
            var exec = new AgenticExecutor(host, new ApproveAllOptions()) { McpTools = m };

            string first = await m.CallToolAsync("dm", "list_files", ListArgs(), CancellationToken.None);
            Assert.DoesNotContain("[MCP ERROR]", first);
            Assert.Contains("a.txt", first);
            Assert.Empty(notices);

            Kill(m.GetServerProcessId("dm") ?? throw new Xunit.Sdk.XunitException("no server PID"));

            var blocks = ToolCallMapper.Map(new List<ToolCallResult>
            {
                new ToolCallResult { Id = "c1", Name = "mcp__dm__list_files", RawArguments = ListArgs() },
            }, null!);
            var result = await exec.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild }, new ResponseOutcome(blocks));

            // Recovery still works…
            Assert.Empty(result.Errors);
            Assert.Contains("a.txt", result.ToolResultContents[McpToolName.ResultKey("c1")]);
            // …and is visible, once.
            var n = Assert.Single(notices);
            Assert.Equal(McpNoticeKind.Restarted, n.Kind);
            Assert.StartsWith("exit code", n.Reason);
            Assert.StartsWith("[MCP] dm restarted (previous process exited: exit code", n.Text);

            var actions = host.GetActions();
            Assert.Contains(actions, a => a.Kind == "mcp_restart" && a.Success && a.Detail.StartsWith("dm restarted"));
            Assert.Contains(actions, a => a.Kind == "mcp" && a.Success && a.Detail.StartsWith("dm.list_files {\"glob\":\"*.txt\"} → ok"));

            // A later call on the healthy relaunched server announces nothing more.
            await m.CallToolAsync("dm", "list_files", ListArgs(), CancellationToken.None);
            Assert.Single(notices);
        }

        [Fact]
        public async Task An_explicit_restart_is_announced()
        {
            await using var m = new McpClientManager(new[] { DevMindServer() });
            var notices = new List<McpNotice>();
            m.Notice += n => { lock (notices) notices.Add(n); };

            Assert.Null(await m.StartAsync("dm"));
            Assert.Empty(notices);

            Assert.Null(await m.RestartAsync("dm"));
            var n = Assert.Single(notices);
            Assert.Equal("[MCP] dm restarted (restart requested)", n.Text);
        }

        /// <summary>Auto approval, like a default headless job.</summary>
        private sealed class ApproveAllOptions : ILlmOptions
        {
            public string SystemPrompt => "test";
            public string ModelName => "test-model";
            public int RequestTimeoutMinutes => 1;
            public int FirstTokenTimeoutMinutes => 1;
            public bool ShowDebugOutput => false;
            public bool ShowContextBudget => false;
            public bool ShowLlmThinking => false;
            public ContextEvictionMode ContextEviction => ContextEvictionMode.Off;
            public int ManualContextSize => 32768;
            public LlmServerType ServerType => LlmServerType.LlamaServer;
            public string CustomContextEndpoint => null!;
            public int MicroCompactThreshold => 99;
            public int NearlineIngestThresholdChars => 8_000;
            public bool MicroCompactSummarize => false;
            public bool MicroCompactBrainwash => false;
            public bool AlwaysConfirmPatch => false;
            public ApprovalMode ApprovalMode => ApprovalMode.Auto;
            public int AgenticLoopMaxDepth => 25;
            public int AgenticContextLimitPercent => 0;
        }
    }
}
