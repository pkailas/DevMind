// File: McpHostTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// MCP client, part 3, Core side: the autoStart setting, the call-id guard (results are keyed
// mcp:<callId>, so a missing or repeated id must not let two calls share a result), the
// system-prompt line that exists only while tools are exposed, and the manager's status /
// restart surface that /mcp and devmind_task_result read.

using System.Text.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    /// <summary>A scripted IMcpClientManager: fixed statuses and tools, counted calls.</summary>
    internal sealed class FakeMcpClients : IMcpClientManager
    {
        public List<JObject> Tools { get; } = new();
        public List<McpServerStatus> Statuses { get; } = new();
        public Func<string, JObject?, string> Respond { get; set; } = (tool, args) => $"ok:{tool}";
        public List<(string server, string tool, JObject? args)> Calls { get; } = new();
        public int Disposed;

        public IReadOnlyList<string> ServerNames => Statuses.Select(s => s.Name).ToList();
        public IReadOnlyList<JObject> GetExposedTools() => Tools;
        public IReadOnlyList<McpServerStatus> GetStatuses() => Statuses;
        public Task<string?> StartAsync(string server, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> RestartAsync(string server, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public int CallCount => Calls.Count;

        public Task<string> CallToolAsync(string server, string tool, JObject? args, CancellationToken ct)
        {
            Calls.Add((server, tool, args));
            return Task.FromResult(Respond(tool, args));
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposed);
            return ValueTask.CompletedTask;
        }

        public static JObject Tool(string server, string name) =>
            McpClientManager.BuildExposedTools(new[]
            {
                new McpToolInfo { Server = server, Name = name, QualifiedName = McpToolName.Build(server, name), Description = "d" },
            }, null)[0];
    }

    public class McpAutoStartConfigTests
    {
        private static (IReadOnlyList<McpServerConfig> servers, List<string> warnings) Parse(string json)
        {
            var warnings = new List<string>();
            using var doc = JsonDocument.Parse(json);
            return (McpServerConfig.ParseBlock(doc.RootElement, warnings.Add), warnings);
        }

        [Fact]
        public void AutoStart_defaults_to_true()
        {
            var (servers, warnings) = Parse("""{ "a": { "command": "a.exe" }, "b": { "command": "b.exe", "autoStart": null } }""");
            Assert.All(servers, s => Assert.True(s.AutoStart));
            Assert.Empty(warnings);
        }

        [Fact]
        public void AutoStart_explicit_values_are_honoured()
        {
            var (servers, _) = Parse("""{ "on": { "command": "a.exe", "autoStart": true }, "off": { "command": "b.exe", "autoStart": false } }""");
            Assert.True(servers.Single(s => s.Name == "on").AutoStart);
            Assert.False(servers.Single(s => s.Name == "off").AutoStart);
        }

        [Theory]
        [InlineData("\"false\"")]
        [InlineData("0")]
        public void A_non_boolean_autoStart_warns_and_keeps_the_server_on(string value)
        {
            var (servers, warnings) = Parse($$"""{ "a": { "command": "a.exe", "autoStart": {{value}} } }""");
            Assert.True(Assert.Single(servers).AutoStart);
            Assert.Contains(warnings, w => w.Contains("autoStart"));
        }
    }

    public class McpCallIdGuardTests
    {
        private static ToolCallResult Call(string? id, string name = "mcp__comfy__run_workflow") =>
            new ToolCallResult { Id = id!, Name = name, RawArguments = new JObject() };

        private static async Task<(ExecutionResult result, List<ResponseBlock> blocks)> RunAsync(List<ToolCallResult> calls, FakeMcpClients fake)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"devmind_callid_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var exec = new AgenticExecutor(new FakeHost(dir), new FakeLlmOptions()) { McpTools = fake };
                var blocks = ToolCallMapper.Map(calls, null!);
                var result = await exec.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild }, new ResponseOutcome(blocks));
                return (result, blocks);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task A_missing_id_gets_a_fallback_used_by_both_block_and_tool_message(string? id)
        {
            int n = 0;
            var fake = new FakeMcpClients { Respond = (_, _) => $"result {++n}" };
            var a = Call(id);
            var b = Call(id);
            var (result, blocks) = await RunAsync(new List<ToolCallResult> { a, b }, fake);

            Assert.NotNull(a.FallbackId);
            Assert.NotNull(b.FallbackId);
            Assert.NotEqual(a.FallbackId, b.FallbackId);
            Assert.Equal(a.FallbackId, blocks[0].ToolCallId);
            Assert.Equal(b.FallbackId, blocks[1].ToolCallId);
            Assert.Equal("result 1", LoopHelpers.BuildToolResultContent(a, result, blocks));
            Assert.Equal("result 2", LoopHelpers.BuildToolResultContent(b, result, blocks));
        }

        [Fact]
        public async Task A_repeated_id_keeps_the_first_and_re_keys_the_rest()
        {
            int n = 0;
            var fake = new FakeMcpClients { Respond = (_, _) => $"result {++n}" };
            var a = Call("call_1");
            var b = Call("call_1");
            var c = Call("call_1");
            var (result, blocks) = await RunAsync(new List<ToolCallResult> { a, b, c }, fake);

            Assert.Null(a.FallbackId);
            Assert.Equal("call_1", a.ResultId);
            Assert.NotNull(b.FallbackId);
            Assert.NotNull(c.FallbackId);
            Assert.Equal(3, new[] { a.ResultId, b.ResultId, c.ResultId }.Distinct().Count());
            Assert.Equal("call_1", b.Id);   // the tool message's tool_call_id is still the model's
            Assert.Equal(new[] { "result 1", "result 2", "result 3" },
                new[] { a, b, c }.Select(t => LoopHelpers.BuildToolResultContent(t, result, blocks)));
        }

        [Fact]
        public void Unique_ids_are_left_alone_and_mapping_twice_is_stable()
        {
            var calls = new List<ToolCallResult> { Call("x"), Call("y"), Call(null) };
            ToolCallMapper.Map(calls, null!);
            string fallback = calls[2].FallbackId;
            ToolCallMapper.Map(calls, null!);

            Assert.Null(calls[0].FallbackId);
            Assert.Null(calls[1].FallbackId);
            Assert.Equal(fallback, calls[2].FallbackId);
        }
    }

    public class McpPromptTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcpprompt_{Guid.NewGuid():N}");

        public McpPromptTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void Note_is_present_only_while_tools_are_exposed()
        {
            Assert.Equal("", McpPrompt.Note(null));
            var fake = new FakeMcpClients();
            Assert.Equal("", McpPrompt.Note(fake));
            fake.Tools.Add(FakeMcpClients.Tool("comfy", "which"));
            Assert.Equal("\n\n" + McpPrompt.Line, McpPrompt.Note(fake));
        }

        private async Task<JObject> OneHeadlessRequestAsync(IMcpClientManager? clients)
        {
            using var server = new FakeSseServer();
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = new HeadlessSession(new HeadlessOptions
                {
                    RequestTimeoutMinutes = 1, FirstTokenTimeoutMinutes = 1, ManualContextSize = 32768, AgenticLoopMaxDepth = 3,
                }, server.BaseUrl, apiKey: null!, workingDirectory: _dir, buildCommand: "dotnet build",
                    promptFilePath: Path.Combine(_dir, "no-such-prompt.md"));
                session.SetMcpClients(clients!);
                var result = await session.RunTurnAsync("Say done.", ct: CancellationToken.None);
                Assert.Null(result.Error);
                return JObject.Parse(Assert.Single(server.RequestBodies));
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        private static string SystemMessage(JObject request) =>
            (string)request["messages"]!.First(m => (string?)m["role"] == "system")["content"]!;

        [Fact]
        public async Task Headless_prompt_and_tools_carry_MCP_only_when_a_server_is_running()
        {
            var fake = new FakeMcpClients();
            fake.Tools.Add(FakeMcpClients.Tool("comfy", "which"));
            var with = await OneHeadlessRequestAsync(fake);
            Assert.Contains(McpPrompt.Line, SystemMessage(with));
            Assert.Contains(with["tools"]!, t => (string?)t["function"]?["name"] == "mcp__comfy__which");

            var without = await OneHeadlessRequestAsync(new FakeMcpClients());   // attached, nothing exposed
            Assert.DoesNotContain("mcp__", SystemMessage(without));
            Assert.DoesNotContain(without["tools"]!, t => ((string?)t["function"]?["name"])!.StartsWith("mcp__"));

            var detached = await OneHeadlessRequestAsync(null);
            Assert.DoesNotContain(McpPrompt.Line, SystemMessage(detached));
        }
    }

    public class McpManagerStatusTests
    {
        private static McpServerConfig Bogus(string name, bool autoStart = true, string[]? tools = null) => new McpServerConfig
        {
            Name = name,
            Command = Path.Combine(Path.GetTempPath(), $"no_such_{Guid.NewGuid():N}.exe"),
            AutoStart = autoStart,
            Tools = tools,
        };

        [Fact]
        public async Task Statuses_go_from_stopped_to_failed_with_a_short_reason()
        {
            await using var m = new McpClientManager(new[] { Bogus("ghost", autoStart: false, tools: new[] { "x" }) });
            var before = Assert.Single(m.GetStatuses());
            Assert.Equal(McpServerState.Stopped, before.State);
            Assert.True(before.HasAllowlist);
            Assert.False(before.AutoStart);
            Assert.Null(before.Error);

            Assert.NotNull(await m.StartAsync("ghost"));
            var after = Assert.Single(m.GetStatuses());
            Assert.Equal(McpServerState.Failed, after.State);
            Assert.Equal(0, after.ToolCount);
            Assert.False(string.IsNullOrEmpty(after.Error));
            Assert.DoesNotContain("stderr", after.Error);
            Assert.DoesNotContain("[MCP ERROR]", after.Error);
        }

        [Fact]
        public async Task Restart_clears_the_failure_budget()
        {
            await using var m = new McpClientManager(new[] { Bogus("ghost") });
            await m.StartAsync("ghost");
            await m.StartAsync("ghost");
            Assert.Contains("not relaunching", await m.StartAsync("ghost"));   // budget spent

            string? err = await m.RestartAsync("ghost");
            Assert.NotNull(err);
            Assert.DoesNotContain("not relaunching", err);                      // it really tried again
            Assert.Contains("no MCP server named 'nope'", await m.RestartAsync("nope"));
        }

        [Theory]
        [InlineData("[MCP ERROR] server 'comfy' failed to start: file not found.\nstderr tail:\nx", "file not found.")]
        [InlineData("[MCP ERROR] server 'comfy': tool 'x' timed out after 5s", "tool 'x' timed out after 5s")]
        [InlineData("did not start within 60 s", "did not start within 60 s")]
        public void ShortReason_keeps_only_the_cause(string error, string expected)
        {
            Assert.Equal(expected, McpClientManager.ShortReason(error));
        }

        [Fact]
        public async Task CallCount_counts_every_call()
        {
            await using var m = new McpClientManager(new[] { Bogus("ghost") });
            await m.CallToolAsync("ghost", "t", null);
            await m.CallToolAsync("nope", "t", null);
            Assert.Equal(2, m.CallCount);
        }
    }
}
