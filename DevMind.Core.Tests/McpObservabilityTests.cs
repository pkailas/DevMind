// File: McpObservabilityTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Two gaps found in live verification after 2a5eb31:
//   * job-1739 made 3 MCP calls and its action journal was empty. Every MCP call that runs
//     is now an "mcp" journal entry — ok, error and timeout alike — written by the executor
//     next to the transcript's "[MCP] …" lines.
//   * job-1740's comfy-mcp was killed mid-job and relaunched silently. A relaunch, and the
//     point where relaunching stops, are now announced once each (McpNotice), and headless
//     sessions journal them as "mcp_restart".
// Plus: /mcp restart re-reads the server's devmind.json entry (ConfigReloader).
//
// The relaunch itself needs a live MCP server; those tests are in DevMind.McpServer.Tests
// (McpRestartNoticeTests), which can launch DevMind.McpServer.exe as one.

using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class McpJournalTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcpjournal_{Guid.NewGuid():N}");

        public McpJournalTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private sealed class ThrowingClients : IMcpToolInvoker
        {
            public Task<string> CallToolAsync(string server, string tool, JObject? args, CancellationToken ct) =>
                throw new InvalidOperationException("kaboom");
        }

        /// <summary>Runs the calls through the executor on a REAL headless host and returns its journal.</summary>
        private async Task<IReadOnlyList<HostAction>> RunAsync(IMcpToolInvoker clients, params (string id, string argsJson)[] calls)
        {
            var host = new BufferedAgenticHost(_dir);
            var exec = new AgenticExecutor(host, new FakeLlmOptions()) { McpTools = clients };
            var blocks = ToolCallMapper.Map(calls.Select(c => new ToolCallResult
            {
                Id = c.id, Name = "mcp__comfy__run_workflow", RawArguments = JObject.Parse(c.argsJson),
            }).ToList(), null!);
            await exec.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild }, new ResponseOutcome(blocks));
            return host.GetActions();
        }

        private static readonly Regex Shape = new(
            @"^comfy\.run_workflow (?<args>.*) → (?<outcome>ok|error|timeout), (?<chars>[\d,]+) chars, \d+\.\d s$");

        [Fact]
        public async Task A_successful_call_is_journaled_with_every_field()
        {
            var fake = new FakeMcpClients { Respond = (_, _) => new string('r', 1234) };
            var entry = Assert.Single(await RunAsync(fake, ("c1", """{"workflow":"a.json","seed":7}""")));

            Assert.Equal("mcp", entry.Kind);
            Assert.True(entry.Success);
            var m = Shape.Match(entry.Detail);
            Assert.True(m.Success, entry.Detail);
            Assert.Equal("""{"workflow":"a.json","seed":7}""", m.Groups["args"].Value);
            Assert.Equal("ok", m.Groups["outcome"].Value);
            Assert.Equal("1,234", m.Groups["chars"].Value);
        }

        [Fact]
        public async Task An_error_call_is_journaled_as_error()
        {
            var fake = new FakeMcpClients { Respond = (_, _) => "[MCP ERROR] server 'comfy' failed to start: gone.\nstderr tail:\nx" };
            var entry = Assert.Single(await RunAsync(fake, ("c1", "{}")));
            Assert.False(entry.Success);
            Assert.Equal("error", Shape.Match(entry.Detail).Groups["outcome"].Value);
        }

        [Fact]
        public async Task A_tool_error_result_is_journaled_as_error()
        {
            var fake = new FakeMcpClients { Respond = (_, _) => "[MCP TOOL ERROR] bad workflow" };
            var entry = Assert.Single(await RunAsync(fake, ("c1", "{}")));
            Assert.False(entry.Success);
            Assert.Equal("error", Shape.Match(entry.Detail).Groups["outcome"].Value);
        }

        [Fact]
        public async Task A_timed_out_call_is_journaled_as_timeout()
        {
            var fake = new FakeMcpClients
            {
                Respond = (tool, _) => $"[MCP ERROR] server 'comfy': tool '{tool}' {McpJournal.TimeoutPhrase} 900s (raise \"callTimeoutSeconds\" ...).",
            };
            var entry = Assert.Single(await RunAsync(fake, ("c1", "{}")));
            Assert.False(entry.Success);
            Assert.Equal("timeout", Shape.Match(entry.Detail).Groups["outcome"].Value);
        }

        [Fact]
        public async Task A_throwing_invoker_is_journaled_as_error()
        {
            var entry = Assert.Single(await RunAsync(new ThrowingClients(), ("c1", "{}")));
            Assert.False(entry.Success);
            Assert.Equal("error", Shape.Match(entry.Detail).Groups["outcome"].Value);
        }

        [Fact]
        public async Task Each_call_gets_its_own_entry()
        {
            var actions = await RunAsync(new FakeMcpClients(), ("c1", "{}"), ("c2", "{}"), ("c3", "{}"));
            Assert.Equal(3, actions.Count(a => a.Kind == "mcp"));   // job-1739: calls 3, actions []
        }

        [Fact]
        public void Long_arguments_are_cut_at_500_chars()
        {
            var args = new JObject { ["prompt"] = new string('p', 2000) };
            string detail = McpJournal.Detail("comfy", "run_workflow", args, McpCallOutcome.Ok, 10, TimeSpan.FromSeconds(1.25));

            string json = Shape.Match(detail).Groups["args"].Value;
            Assert.Equal(McpJournal.MaxArgsChars + 1, json.Length);
            Assert.EndsWith("…", json);
            Assert.StartsWith("{\"prompt\":\"ppp", json);
            Assert.EndsWith("→ ok, 10 chars, 1.3 s", detail);   // invariant culture, one decimal
        }

        [Theory]
        [InlineData("fine", McpCallOutcome.Ok)]
        [InlineData("[MCP TOOL ERROR] x", McpCallOutcome.Error)]
        [InlineData("[MCP ERROR] server 'a' failed to start: x", McpCallOutcome.Error)]
        [InlineData("[MCP ERROR] server 'a': tool 'b' timed out after 5s", McpCallOutcome.Timeout)]
        [InlineData("result text that mentions timed out after 5s", McpCallOutcome.Ok)]
        public void Classify(string text, McpCallOutcome expected) => Assert.Equal(expected, McpJournal.Classify(text));

        [Fact]
        public async Task A_host_without_a_journal_is_fine()
        {
            // The TUI host keeps no journal; the executor must simply skip it.
            var host = new FakeHost(_dir);
            var exec = new AgenticExecutor(host, new FakeLlmOptions()) { McpTools = new FakeMcpClients() };
            var blocks = ToolCallMapper.Map(new List<ToolCallResult>
            {
                new ToolCallResult { Id = "c1", Name = "mcp__comfy__which", RawArguments = new JObject() },
            }, null!);
            var result = await exec.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild }, new ResponseOutcome(blocks));
            Assert.Empty(result.Errors);
        }
    }

    public class McpNoticeTests
    {
        private static McpServerConfig Bogus(string name, string[]? tools = null) => new McpServerConfig
        {
            Name = name,
            Command = Path.Combine(Path.GetTempPath(), $"no_such_{Guid.NewGuid():N}.exe"),
            Tools = tools,
        };

        [Fact]
        public void Notice_text()
        {
            Assert.Equal("[MCP] comfy restarted (previous process exited: exit code 1)",
                new McpNotice { Server = "comfy", Kind = McpNoticeKind.Restarted, Reason = "exit code 1" }.Text);
            Assert.Equal("[MCP] comfy restarted (restart requested)",
                new McpNotice { Server = "comfy", Kind = McpNoticeKind.Restarted, Reason = McpNotice.RestartRequested }.Text);
            Assert.StartsWith("[MCP] comfy not restarted: failed twice in a row;",
                new McpNotice { Server = "comfy", Kind = McpNoticeKind.NotRestarted, Reason = "x" }.Text);
        }

        [Fact]
        public async Task The_second_consecutive_failure_announces_not_restarted_exactly_once()
        {
            await using var m = new McpClientManager(new[] { Bogus("ghost") });
            var notices = new List<McpNotice>();
            m.Notice += n => { lock (notices) notices.Add(n); };

            await m.CallToolAsync("ghost", "t", null);   // failure 1 — no notice
            Assert.Empty(notices);
            await m.CallToolAsync("ghost", "t", null);   // failure 2 — relaunching stops
            await m.CallToolAsync("ghost", "t", null);   // refused without relaunch
            await m.CallToolAsync("ghost", "t", null);

            var n = Assert.Single(notices);
            Assert.Equal(McpNoticeKind.NotRestarted, n.Kind);
            Assert.Equal("ghost", n.Server);
            Assert.False(string.IsNullOrEmpty(n.Reason));
        }

        [Fact]
        public async Task A_failed_first_start_is_not_announced_as_a_restart()
        {
            await using var m = new McpClientManager(new[] { Bogus("ghost") });
            var notices = new List<McpNotice>();
            m.Notice += notices.Add;
            await m.StartAsync("ghost");
            Assert.Empty(notices);
        }

        [Fact]
        public void A_headless_host_writes_the_notice_to_transcript_and_journal()
        {
            var output = new List<string>();
            var host = new BufferedAgenticHost(Path.GetTempPath(), outputSink: (t, _) => output.Add(t));

            host.RecordMcpNotice(new McpNotice { Server = "comfy", Kind = McpNoticeKind.Restarted, Reason = "exit code 1" });
            host.RecordMcpNotice(new McpNotice { Server = "comfy", Kind = McpNoticeKind.NotRestarted, Reason = "gone" });

            Assert.Contains("[MCP] comfy restarted (previous process exited: exit code 1)\n", output);
            var actions = host.GetActions();
            Assert.Equal(2, actions.Count);
            Assert.All(actions, a => Assert.Equal("mcp_restart", a.Kind));
            Assert.Equal("comfy restarted (previous process exited: exit code 1)", actions[0].Detail);
            Assert.True(actions[0].Success);
            Assert.False(actions[1].Success);
        }

        // ── /mcp restart re-reads devmind.json ───────────────────────────────

        [Fact]
        public async Task Restart_re_reads_the_entry_so_an_edited_allowlist_takes_effect()
        {
            await using var m = new McpClientManager(new[] { Bogus("comfy") });
            Assert.False(Assert.Single(m.GetStatuses()).HasAllowlist);

            string? requested = null;
            m.ConfigReloader = name => { requested = name; return Bogus(name, tools: new[] { "which" }); };
            await m.RestartAsync("comfy");   // the bogus exe fails to start; the config swap is what is under test

            Assert.Equal("comfy", requested);
            Assert.True(Assert.Single(m.GetStatuses()).HasAllowlist);
        }

        [Fact]
        public async Task Restart_of_a_server_removed_from_config_says_so_and_starts_nothing()
        {
            await using var m = new McpClientManager(new[] { Bogus("comfy") }) { ConfigReloader = _ => null };
            string? err = await m.RestartAsync("comfy");
            Assert.Contains("no longer in \"mcpServers\"", err);
            Assert.Equal(McpServerState.Stopped, Assert.Single(m.GetStatuses()).State);
        }

        [Fact]
        public async Task Without_a_reloader_restart_keeps_the_startup_config()
        {
            await using var m = new McpClientManager(new[] { Bogus("comfy", tools: new[] { "a" }) });
            await m.RestartAsync("comfy");
            Assert.True(Assert.Single(m.GetStatuses()).HasAllowlist);
        }
    }
}
