// File: McpAgentLoopTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// MCP client, part 2: external tools in the agent loop. What they look like to the model
// (ToolRegistry + McpClientManager.GetExposedTools), how a call maps (ToolCallMapper), how
// it runs (AgenticExecutor through the IMcpToolInvoker seam), and how the result gets back
// to the right tool_call (LoopHelpers, keyed by call id).

using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class McpToolExposureTests
    {
        private static McpToolInfo Tool(string server, string name, string description = "does things", JObject? schema = null) =>
            new McpToolInfo
            {
                Server = server,
                Name = name,
                QualifiedName = McpToolName.Build(server, name),
                Description = description,
                InputSchema = schema ?? JObject.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
            };

        [Fact]
        public void NoArg_BuildToolsArray_is_unchanged_by_the_overload()
        {
            var plain = ToolRegistry.BuildToolsArray();
            Assert.True(JToken.DeepEquals(plain, ToolRegistry.BuildToolsArray(null!)));
            Assert.True(JToken.DeepEquals(plain, ToolRegistry.BuildToolsArray(Array.Empty<JObject>())));
            Assert.DoesNotContain(plain, t => ((string?)t["function"]?["name"])?.StartsWith("mcp__") == true);
            Assert.Equal(ToolRegistry.ToolCount, plain.Count);
        }

        [Fact]
        public void Extras_are_appended_after_the_static_set_as_clones()
        {
            var extra = McpClientManager.BuildExposedTools(new[] { Tool("comfy", "which") }, null);
            var arr = ToolRegistry.BuildToolsArray(extra);

            Assert.Equal(ToolRegistry.ToolCount + 1, arr.Count);
            var last = (JObject)arr[arr.Count - 1];
            Assert.Equal("function", (string?)last["type"]);
            Assert.Equal("mcp__comfy__which", (string?)last["function"]!["name"]);
            Assert.Equal("[comfy MCP] does things", (string?)last["function"]!["description"]);
            Assert.Equal("string", (string?)last["function"]!["parameters"]!["properties"]!["path"]!["type"]);
            Assert.Null(extra[0].Parent);   // the cached object was cloned, not parented into the request
        }

        [Fact]
        public void Empty_schema_becomes_an_empty_object_schema()
        {
            var t = Assert.Single(McpClientManager.BuildExposedTools(new[] { Tool("comfy", "server_info", schema: new JObject()) }, null));
            Assert.True(JToken.DeepEquals(JObject.Parse("""{"type":"object","properties":{}}"""), t["function"]!["parameters"]));
        }

        [Fact]
        public void Names_over_64_chars_or_with_bad_characters_are_skipped_with_a_warning()
        {
            var warnings = new List<string>();
            string longTool = new string('t', 64 - "mcp__comfy__".Length + 1);   // 65 chars qualified
            string fitTool = new string('t', 64 - "mcp__comfy__".Length);        // exactly 64
            var exposed = McpClientManager.BuildExposedTools(new[]
            {
                Tool("comfy", longTool), Tool("comfy", fitTool), Tool("comfy", "has.dot"), Tool("comfy", "ok"),
            }, warnings.Add);

            Assert.Equal(new[] { "mcp__comfy__" + fitTool, "mcp__comfy__ok" },
                exposed.Select(t => (string)t["function"]!["name"]!));
            Assert.Equal(2, warnings.Count);
            Assert.Contains(warnings, w => w.Contains(longTool));
            Assert.Contains(warnings, w => w.Contains("has.dot"));
        }

        [Fact]
        public async Task Only_started_servers_are_exposed_and_the_allowlist_holds()
        {
            await using var m = new McpClientManager(new[]
            {
                new McpServerConfig { Name = "comfy", Command = "c.exe", Tools = new[] { "which" } },
                new McpServerConfig { Name = "other", Command = "o.exe" },
            });
            Assert.Empty(m.GetExposedTools());   // nothing started

            m.SeedExposedToolsForTest("comfy", new[] { Tool("comfy", "which"), Tool("comfy", "run_workflow") });
            var names = m.GetExposedTools().Select(t => (string)t["function"]!["name"]!).ToList();
            Assert.Equal(new[] { "mcp__comfy__which" }, names);
        }

        [Fact]
        public async Task A_request_carries_the_started_servers_tools_and_calls_come_back_typed()
        {
            await using var m = new McpClientManager(new[] { new McpServerConfig { Name = "comfy", Command = "c.exe" } });
            m.SeedExposedToolsForTest("comfy", new[] { Tool("comfy", "run_workflow") });

            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse(
                "mcp__comfy__run_workflow", """{"n":2,"flag":true,"none":null,"o":{"a":[1,"x"]},"s":"2"}"""));
            var client = new LlmClient(new FakeLlmOptions()) { McpClients = m };
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try { client.Configure(server.BaseUrl, apiKey: null!); }
            finally { Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior); }

            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.IncrementTurn();
            await client.SendMessageAsync("go", onToken: _ => { }, onComplete: () => done.TrySetResult(true),
                onError: ex => done.TrySetException(ex));
            Assert.True(await Task.WhenAny(done.Task, Task.Delay(15000)) == done.Task, "turn did not complete");

            // On the wire: the static catalogue plus the MCP tool.
            var tools = (JArray)JObject.Parse(server.RequestBodies[0])["tools"]!;
            Assert.Equal(ToolRegistry.ToolCount + 1, tools.Count);
            Assert.Contains(tools, t => (string?)t["function"]?["name"] == "mcp__comfy__run_workflow");

            // Back from the model: the typed object survives next to the flattened strings.
            var call = Assert.Single(client.LastToolCalls!);
            Assert.Equal("True", call.Arguments["flag"]);   // the lossy map, unchanged
            var raw = call.RawArguments;
            Assert.NotNull(raw);
            Assert.Equal(JTokenType.Integer, raw!["n"]!.Type);
            Assert.Equal(JTokenType.Boolean, raw["flag"]!.Type);
            Assert.Equal(JTokenType.Null, raw["none"]!.Type);
            Assert.Equal(JTokenType.String, raw["s"]!.Type);
            Assert.Equal("x", (string?)raw["o"]!["a"]![1]);
        }
    }

    public class McpToolMappingTests
    {
        [Fact]
        public void Mcp_name_maps_to_McpCall_with_server_tool_and_typed_args()
        {
            var raw = JObject.Parse("""{"workflow":{"1":{"class_type":"KSampler"}},"seed":42}""");
            var tc = new ToolCallResult
            {
                Id = "call_7", Name = "mcp__comfy__run_workflow",
                Arguments = new Dictionary<string, string> { ["seed"] = "42" },
                RawArguments = raw,
            };
            var block = Assert.Single(ToolCallMapper.Map(new List<ToolCallResult> { tc }, "dotnet build"));

            Assert.Equal(BlockType.McpCall, block.Type);
            Assert.Equal("comfy", block.McpServer);
            Assert.Equal("run_workflow", block.McpTool);
            Assert.Equal("call_7", block.ToolCallId);
            Assert.True(block.FromToolCall);
            Assert.True(JToken.DeepEquals(raw, block.McpArguments));
            Assert.NotSame(raw, block.McpArguments);
            Assert.Equal(JTokenType.Integer, block.McpArguments!["seed"]!.Type);
        }

        [Fact]
        public void Without_raw_arguments_the_string_map_is_forwarded()
        {
            var tc = new ToolCallResult { Id = "c", Name = "mcp__comfy__which", Arguments = new Dictionary<string, string> { ["a"] = "b" } };
            var block = Assert.Single(ToolCallMapper.Map(new List<ToolCallResult> { tc }, null!));
            Assert.Equal("b", (string?)block.McpArguments!["a"]);
        }

        [Theory]
        [InlineData("mcp__comfy")]
        [InlineData("mcp__comfy__")]
        [InlineData("mcp__Comfy__which")]
        [InlineData("mcp____which")]
        public void Malformed_mcp_names_fall_to_the_unknown_tool_default(string name)
        {
            var block = Assert.Single(ToolCallMapper.Map(new List<ToolCallResult> { new ToolCallResult { Id = "c", Name = name } }, null!));
            Assert.Equal(BlockType.Text, block.Type);
            Assert.Equal($"[Unknown tool call: {name}]", block.Content);
        }

        [Fact]
        public void Non_mcp_names_are_unchanged()
        {
            var tc = new ToolCallResult { Id = "c", Name = "web_fetch", Arguments = new Dictionary<string, string> { ["url"] = "http://x" } };
            var block = Assert.Single(ToolCallMapper.Map(new List<ToolCallResult> { tc }, null!));
            Assert.Equal(BlockType.WebFetch, block.Type);
            Assert.Equal("http://x", block.Url);
        }

        [Fact]
        public void An_mcp_call_is_a_directive_and_a_mutation()
        {
            var outcome = new ResponseOutcome(new List<ResponseBlock> { new ResponseBlock { Type = BlockType.McpCall } });
            Assert.True(outcome.HasMcpCalls);
            Assert.True(outcome.HasAnyDirective);
            Assert.False(outcome.IsReadOnly);
        }

        [Fact]
        public void Training_log_records_server_and_tool()
        {
            var calls = JsonlTrainingLogger.ExtractToolCalls(new List<ResponseBlock>
            {
                new ResponseBlock { Type = BlockType.McpCall, McpServer = "comfy", McpTool = "which" },
            });
            var entry = Assert.Single(calls!);
            Assert.Equal("mcp", entry.Type);
            Assert.Equal("comfy", entry.Server);
            Assert.Equal("which", entry.Tool);

            var result = new ExecutionResult();
            result.ToolResultContents[McpToolName.ResultKey("call_1")] = "ok";
            Assert.Equal("mcp", Assert.Single(JsonlTrainingLogger.ExtractToolResults(result)!).Type);
        }
    }

    public class McpExecutorTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcpexec_{Guid.NewGuid():N}");

        public McpExecutorTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private sealed class FakeInvoker : IMcpToolInvoker
        {
            public Func<string, string, JObject?, string> Respond { get; set; } = (s, t, a) => $"{s}.{t} {a?.ToString(Newtonsoft.Json.Formatting.None)}";
            public Exception? Throw { get; set; }
            public List<(string server, string tool, JObject? args)> Calls { get; } = new();

            public Task<string> CallToolAsync(string server, string tool, JObject? args, CancellationToken ct)
            {
                Calls.Add((server, tool, args));
                if (Throw != null) throw Throw;
                return Task.FromResult(Respond(server, tool, args));
            }
        }

        private sealed class ModeOptions : ILlmOptions
        {
            public ApprovalMode Mode { get; set; } = ApprovalMode.Auto;
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
            public ApprovalMode ApprovalMode => Mode;
            public int AgenticLoopMaxDepth => 25;
            public int AgenticContextLimitPercent => 0;
        }

        private static ToolCallResult Call(string id, string name, string argsJson = "{}") =>
            new ToolCallResult { Id = id, Name = name, RawArguments = JObject.Parse(argsJson) };

        private async Task<(ExecutionResult result, List<ResponseBlock> blocks, FakeHost host)> RunAsync(
            IMcpToolInvoker? invoker, ApprovalMode mode, bool confirm, params ToolCallResult[] calls)
        {
            var host = new FakeHost(_dir) { ConfirmAnswer = confirm };
            var exec = new AgenticExecutor(host, new ModeOptions { Mode = mode }) { McpTools = invoker! };
            var blocks = ToolCallMapper.Map(calls.ToList(), null!);
            var result = await exec.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild }, new ResponseOutcome(blocks));
            return (result, blocks, host);
        }

        [Fact]
        public async Task Success_lands_under_the_call_id_and_reaches_its_tool_message()
        {
            var fake = new FakeInvoker();
            var tc = Call("call_1", "mcp__comfy__which", """{"n":1}""");
            var (result, blocks, host) = await RunAsync(fake, ApprovalMode.Auto, true, tc);

            Assert.Empty(result.Errors);
            Assert.Equal("comfy.which {\"n\":1}", result.ToolResultContents[McpToolName.ResultKey("call_1")]);
            Assert.Equal("comfy.which {\"n\":1}", LoopHelpers.BuildToolResultContent(tc, result, blocks));
            var (server, tool, args) = Assert.Single(fake.Calls);
            Assert.Equal(("comfy", "which"), (server, tool));
            Assert.Equal(JTokenType.Integer, args!["n"]!.Type);
            Assert.Contains("[MCP] comfy.which running…", host.Output);
            Assert.Empty(host.ConfirmPrompts);   // Auto never asks
        }

        [Fact]
        public async Task Two_identical_calls_in_one_turn_do_not_collide()
        {
            int n = 0;
            var fake = new FakeInvoker { Respond = (_, _, _) => $"render #{++n}" };
            var a = Call("call_a", "mcp__comfy__run_workflow", """{"w":1}""");
            var b = Call("call_b", "mcp__comfy__run_workflow", """{"w":1}""");
            var (result, blocks, _) = await RunAsync(fake, ApprovalMode.Auto, true, a, b);

            Assert.Equal(2, fake.Calls.Count);
            Assert.Equal("render #1", LoopHelpers.BuildToolResultContent(a, result, blocks));
            Assert.Equal("render #2", LoopHelpers.BuildToolResultContent(b, result, blocks));
        }

        [Fact]
        public async Task Error_text_goes_to_Errors_and_the_model_without_throwing()
        {
            var fake = new FakeInvoker { Respond = (_, _, _) => "[MCP ERROR] server 'comfy' failed to start.\nstderr tail:\nboom" };
            var tc = Call("call_1", "mcp__comfy__which");
            var (result, blocks, host) = await RunAsync(fake, ApprovalMode.Auto, true, tc);

            Assert.Single(result.Errors);
            Assert.StartsWith("[MCP ERROR] server 'comfy'", LoopHelpers.BuildToolResultContent(tc, result, blocks));
            Assert.Contains("[MCP] comfy.which: [MCP ERROR] server 'comfy' failed to start.", host.Output);
        }

        [Fact]
        public async Task A_throwing_invoker_is_contained()
        {
            var fake = new FakeInvoker { Throw = new InvalidOperationException("kaboom") };
            var tc = Call("call_1", "mcp__comfy__which");
            var (result, blocks, _) = await RunAsync(fake, ApprovalMode.Auto, true, tc);

            Assert.Contains(result.Errors, e => e.Contains("kaboom"));
            Assert.Contains("kaboom", LoopHelpers.BuildToolResultContent(tc, result, blocks));
        }

        [Fact]
        public async Task No_invoker_attached_is_an_error_not_a_crash()
        {
            var tc = Call("call_1", "mcp__comfy__which");
            var (result, blocks, _) = await RunAsync(null, ApprovalMode.Auto, true, tc);

            Assert.Single(result.Errors);
            Assert.Contains("no MCP client is attached", LoopHelpers.BuildToolResultContent(tc, result, blocks));
        }

        [Fact]
        public async Task Oversize_result_is_capped_with_a_truncation_note()
        {
            var fake = new FakeInvoker { Respond = (_, _, _) => new string('x', AgenticExecutor.MaxMcpResultChars + 500) };
            var tc = Call("call_1", "mcp__comfy__which");
            var (result, blocks, _) = await RunAsync(fake, ApprovalMode.Auto, true, tc);

            string text = LoopHelpers.BuildToolResultContent(tc, result, blocks);
            Assert.StartsWith(new string('x', 100), text);
            Assert.EndsWith($"[MCP: result truncated at {AgenticExecutor.MaxMcpResultChars:N0} of {AgenticExecutor.MaxMcpResultChars + 500:N0} chars]", text);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public async Task Manual_mode_asks_like_run_shell_and_a_decline_runs_nothing()
        {
            var fake = new FakeInvoker();
            var tc = Call("call_1", "mcp__comfy__run_workflow", """{"w":1}""");
            var (result, blocks, host) = await RunAsync(fake, ApprovalMode.Manual, confirm: false, tc);

            Assert.Contains(host.ConfirmPrompts, p => p.StartsWith("Run MCP tool comfy.run_workflow {\"w\":1}"));
            Assert.Empty(fake.Calls);
            Assert.Contains("declined", LoopHelpers.BuildToolResultContent(tc, result, blocks));
        }

        [Fact]
        public async Task Manual_mode_approved_runs()
        {
            var fake = new FakeInvoker();
            var (_, _, host) = await RunAsync(fake, ApprovalMode.Manual, confirm: true, Call("call_1", "mcp__comfy__which"));
            Assert.Single(host.ConfirmPrompts);
            Assert.Single(fake.Calls);
        }

        [Fact]
        public async Task Plan_mode_refuses_without_asking()
        {
            var fake = new FakeInvoker();
            var tc = Call("call_1", "mcp__comfy__run_workflow");
            var (result, blocks, host) = await RunAsync(fake, ApprovalMode.Plan, confirm: true, tc);

            Assert.Empty(fake.Calls);
            Assert.Empty(host.ConfirmPrompts);
            Assert.StartsWith("[PLAN MODE] Refused: Run MCP tool comfy.run_workflow", LoopHelpers.BuildToolResultContent(tc, result, blocks));
        }
    }
}
