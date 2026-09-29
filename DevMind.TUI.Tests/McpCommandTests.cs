// File: McpCommandTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The TUI side of the MCP client (part 3): /mcp's listing and restart, and the startup
// guarantee — autostart hands every server to the thread pool and returns at once, so a
// server that takes seconds (comfy-mcp) or never answers cannot delay the TUI.

using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.TUI.Tests
{
    /// <summary>A scripted IMcpClientManager for the TUI tests.</summary>
    internal sealed class FakeTuiMcpClients : IMcpClientManager
    {
        public List<McpServerStatus> Statuses { get; } = new();
        public Func<string, Task<string?>> Start { get; set; } = _ => Task.FromResult<string?>(null);
        public List<string> Restarted { get; } = new();

        public IReadOnlyList<string> ServerNames => Statuses.Select(s => s.Name).ToList();
        public IReadOnlyList<JObject> GetExposedTools() => Array.Empty<JObject>();
        public IReadOnlyList<McpServerStatus> GetStatuses() => Statuses;
        public Task<string?> StartAsync(string server, CancellationToken ct = default) => Start(server);

        public Task<string?> RestartAsync(string server, CancellationToken ct = default)
        {
            Restarted.Add(server);
            return Start(server);
        }

        public int CallCount => 0;
        public Task<string> CallToolAsync(string server, string tool, JObject? args, CancellationToken ct) => Task.FromResult("");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public class McpCommandTests
    {
        private static FakeTuiMcpClients Fake() => new FakeTuiMcpClients
        {
            Statuses =
            {
                new McpServerStatus { Name = "comfy", State = McpServerState.Ready, ToolCount = 39, AutoStart = true },
                new McpServerStatus { Name = "blender", State = McpServerState.Failed, HasAllowlist = true, AutoStart = true, Error = "file not found" },
                new McpServerStatus { Name = "slow", State = McpServerState.Starting, AutoStart = true },
                new McpServerStatus { Name = "manual", State = McpServerState.Stopped, AutoStart = false },
            },
        };

        [Fact]
        public void Status_listing_shows_state_tools_allowlist_and_reason()
        {
            string text = McpTui.FormatStatus(Fake().GetStatuses());
            string[] lines = text.Split('\n');

            Assert.Equal("MCP servers:", lines[0]);
            Assert.Equal("  comfy    ready     39 tools   all tools", lines[1]);
            Assert.Equal("  blender  failed    -          allowlist  — file not found", lines[2]);
            Assert.Equal("  slow     starting  -          all tools", lines[3]);
            Assert.Equal("  manual   stopped   -          all tools  (manual start)", lines[4]);
            Assert.Equal("/mcp restart <name> restarts one server.", lines[5]);
        }

        [Fact]
        public async Task Mcp_with_no_manager_says_how_to_configure()
        {
            var result = await SlashCommand.Dispatch("/mcp", new CommandContext());
            Assert.False(result.IsError);
            Assert.Equal(McpTui.NotConfigured, result.Message);
        }

        [Fact]
        public async Task Mcp_lists_through_the_dispatcher()
        {
            var result = await SlashCommand.Dispatch("/mcp", new CommandContext { McpClients = Fake() });
            Assert.False(result.IsError);
            Assert.StartsWith("MCP servers:", result.Message);
        }

        [Fact]
        public async Task Restart_reports_ready_or_failed()
        {
            var fake = Fake();
            var ok = await SlashCommand.Dispatch("/mcp restart comfy", new CommandContext { McpClients = fake });
            Assert.False(ok.IsError);
            Assert.Equal("[MCP] comfy ready (39 tools)", ok.Message);

            fake.Start = _ => Task.FromResult<string?>("[MCP ERROR] server 'blender' failed to start: file not found.\nstderr tail:\nx");
            var bad = await SlashCommand.Dispatch("/mcp restart blender", new CommandContext { McpClients = fake });
            Assert.True(bad.IsError);
            Assert.Equal("[MCP] blender failed to start: file not found.", bad.Message);
            Assert.Equal(new[] { "comfy", "blender" }, fake.Restarted);
        }

        [Fact]
        public async Task Restart_of_an_unknown_server_lists_the_configured_ones()
        {
            var fake = Fake();
            var result = await SlashCommand.Dispatch("/mcp restart nope", new CommandContext { McpClients = fake });
            Assert.True(result.IsError);
            Assert.Contains("no server named 'nope'", result.Message);
            Assert.Contains("comfy, blender, slow, manual", result.Message);
            Assert.Empty(fake.Restarted);
        }

        [Theory]
        [InlineData("/mcp restart")]
        [InlineData("/mcp stop comfy")]
        public async Task Anything_else_is_a_usage_error(string input)
        {
            var result = await SlashCommand.Dispatch(input, new CommandContext { McpClients = Fake() });
            Assert.True(result.IsError);
            Assert.StartsWith("usage: /mcp", result.Message);
        }

        [Fact]
        public void Mcp_is_listed_in_help()
        {
            var help = SlashCommand.Dispatch("/help", new CommandContext()).GetAwaiter().GetResult();
            Assert.Contains("/mcp", help.Message);
        }
    }

    public class McpAutoStartTests
    {
        [Fact]
        public async Task Startup_returns_before_a_server_that_never_finishes()
        {
            var never = new TaskCompletionSource<string?>();
            var fake = new FakeTuiMcpClients { Start = _ => never.Task };
            var reports = new List<string>();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Task all = McpTui.BeginAutoStart(fake, new[] { "comfy" }, (line, _) => { lock (reports) reports.Add(line); });
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 500, $"BeginAutoStart blocked for {sw.ElapsedMilliseconds} ms");
            await Task.Delay(100);
            Assert.False(all.IsCompleted);
            Assert.Empty(reports);

            never.SetResult(null);   // let the pool thread finish so nothing outlives the test
            await all.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task Each_server_reports_exactly_once()
        {
            var fake = new FakeTuiMcpClients
            {
                Statuses = { new McpServerStatus { Name = "comfy", State = McpServerState.Ready, ToolCount = 39 } },
                Start = name => Task.FromResult<string?>(name == "comfy" ? null : "[MCP ERROR] server 'dead' failed to start: gone.\nstderr tail:\nx"),
            };
            var reports = new List<(string line, OutputColor color)>();
            await McpTui.BeginAutoStart(fake, new[] { "comfy", "dead" }, (line, color) => { lock (reports) reports.Add((line, color)); })
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, reports.Count);
            Assert.Contains(("[MCP] comfy ready (39 tools)\n", OutputColor.Success), reports);
            Assert.Contains(("[MCP] dead failed to start: gone.\n", OutputColor.Error), reports);
        }

        [Fact]
        public void Only_autoStart_servers_start_on_their_own()
        {
            var names = McpTui.AutoStartNames(new[]
            {
                new McpServerConfig { Name = "a", Command = "a.exe" },
                new McpServerConfig { Name = "b", Command = "b.exe", AutoStart = false },
            });
            Assert.Equal(new[] { "a" }, names);
        }
    }
}
