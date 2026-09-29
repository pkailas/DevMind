// File: McpClientTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// MCP client, part 1: the "mcpServers" config block, the mcp__server__tool name,
// result-to-text conversion, and the manager's failure contract (never throws, stderr
// tail in the message, exactly one automatic restart). The live comfy-mcp smoke test is
// skipped unless DEVMIND_MCP_SMOKE=1 — it needs the real server installed.

using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class McpServerConfigTests
    {
        private static (IReadOnlyList<McpServerConfig> servers, List<string> warnings) Parse(string json)
        {
            var warnings = new List<string>();
            using var doc = JsonDocument.Parse(json);
            return (McpServerConfig.ParseBlock(doc.RootElement, warnings.Add), warnings);
        }

        [Fact]
        public void Absent_block_is_feature_off()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"devmind_mcpcfg_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_GLOBAL_DIR");
            try
            {
                Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", dir);
                File.WriteAllText(Path.Combine(dir, "devmind.json"), "{ \"depthCap\": 10 }");
                Assert.Empty(McpServerConfig.Load());
                Assert.False(McpClientManager.FromGlobalConfig().IsEnabled);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", prior);
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Empty_block_and_non_object_block_yield_nothing()
        {
            Assert.Empty(Parse("{}").servers);
            Assert.Empty(Parse("[]").servers);
            Assert.Empty(Parse("null").servers);
        }

        [Fact]
        public void Full_entry_parses_every_field()
        {
            var (servers, warnings) = Parse("""
                {
                  "comfy": {
                    "command": "C:\\tools\\comfy-mcp.exe",
                    "args": ["--flag", "value"],
                    "env": { "COMFY_BIN": "C:\\tools\\comfy.exe", "DROP_ME": null },
                    "tools": ["server_info", "run_workflow"],
                    "callTimeoutSeconds": 900
                  }
                }
                """);
            Assert.Empty(warnings);
            var s = Assert.Single(servers);
            Assert.Equal("comfy", s.Name);
            Assert.Equal(@"C:\tools\comfy-mcp.exe", s.Command);
            Assert.Equal(new[] { "--flag", "value" }, s.Args);
            Assert.Equal(@"C:\tools\comfy.exe", s.Env["COMFY_BIN"]);
            Assert.True(s.Env.ContainsKey("DROP_ME"));
            Assert.Null(s.Env["DROP_ME"]);
            Assert.Equal(new[] { "server_info", "run_workflow" }, s.Tools);
            Assert.Equal(900, s.CallTimeoutSeconds);
        }

        [Fact]
        public void Minimal_entry_gets_defaults()
        {
            var (servers, warnings) = Parse("""{ "x": { "command": "x.exe" } }""");
            Assert.Empty(warnings);
            var s = Assert.Single(servers);
            Assert.Empty(s.Args);
            Assert.Empty(s.Env);
            Assert.Null(s.Tools);
            Assert.Equal(McpServerConfig.DefaultCallTimeoutSeconds, s.CallTimeoutSeconds);
            Assert.Equal(120, s.CallTimeoutSeconds);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-5")]
        [InlineData("\"900\"")]
        [InlineData("1.5")]
        public void Bad_timeout_falls_back_to_default_with_warning(string value)
        {
            var (servers, warnings) = Parse($$"""{ "x": { "command": "x.exe", "callTimeoutSeconds": {{value}} } }""");
            Assert.Equal(120, Assert.Single(servers).CallTimeoutSeconds);
            Assert.Contains(warnings, w => w.Contains("callTimeoutSeconds"));
        }

        [Fact]
        public void Bad_entries_are_skipped_with_a_warning_and_siblings_survive()
        {
            var (servers, warnings) = Parse("""
                {
                  "good":        { "command": "good.exe" },
                  "no-command":  { "args": [] },
                  "blank-cmd":   { "command": "  " },
                  "not-object":  "good.exe",
                  "bad-args":    { "command": "x.exe", "args": "--flag" },
                  "bad-arg-el":  { "command": "x.exe", "args": [1] },
                  "bad-env":     { "command": "x.exe", "env": { "A": 1 } },
                  "bad-tools":   { "command": "x.exe", "tools": "run" },
                  "http":        { "type": "http", "url": "http://localhost:1/mcp" },
                  "Upper":       { "command": "x.exe" },
                  "dbl__under":  { "command": "x.exe" },
                  "trailing_":   { "command": "x.exe" },
                  "also-good":   { "command": "npx", "type": "stdio" }
                }
                """);
            Assert.Equal(new[] { "good", "also-good" }, servers.Select(s => s.Name));
            Assert.Equal(11, warnings.Count);
            Assert.All(warnings, w => Assert.Contains("skipping", w));
        }

        [Fact]
        public void Allowlist_filters_and_absent_allows_all()
        {
            var (servers, _) = Parse("""
                { "a": { "command": "a.exe", "tools": ["run_workflow"] },
                  "b": { "command": "b.exe" } }
                """);
            var a = servers.Single(s => s.Name == "a");
            var b = servers.Single(s => s.Name == "b");
            Assert.True(a.AllowsTool("run_workflow"));
            Assert.False(a.AllowsTool("which"));
            Assert.False(a.AllowsTool("RUN_WORKFLOW"));
            Assert.True(b.AllowsTool("anything"));
        }

        [Fact]
        public void Empty_allowlist_exposes_nothing_and_warns()
        {
            var (servers, warnings) = Parse("""{ "a": { "command": "a.exe", "tools": [] } }""");
            Assert.False(Assert.Single(servers).AllowsTool("which"));
            Assert.Contains(warnings, w => w.Contains("empty allowlist"));
        }

        [Fact]
        public void TuiConfig_save_round_trips_the_block_verbatim()
        {
            // TuiConfig re-serializes only its own properties: without the raw McpServers
            // property, the next /mode save would silently delete the operator's servers.
            string dir = Path.Combine(Path.GetTempPath(), $"devmind_mcpcfg_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_GLOBAL_DIR");
            try
            {
                Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", dir);
                string path = Path.Combine(dir, "devmind.json");
                File.WriteAllText(path, """
                    { "approvalMode": "auto",
                      "mcpServers": { "comfy": { "command": "c.exe", "args": ["x"], "callTimeoutSeconds": 900, "custom": { "k": 1 } } } }
                    """);

                var cfg = TuiConfig.Load();
                cfg.ApprovalMode = ApprovalMode.Manual;
                cfg.Save();

                var reparsed = JObject.Parse(File.ReadAllText(path));
                Assert.Equal("manual", (string?)reparsed["approvalMode"]);
                Assert.True(JToken.DeepEquals(
                    JObject.Parse("""{ "comfy": { "command": "c.exe", "args": ["x"], "callTimeoutSeconds": 900, "custom": { "k": 1 } } }"""),
                    reparsed["mcpServers"]));
                Assert.Equal(900, Assert.Single(McpServerConfig.Load()).CallTimeoutSeconds);

                // Absent stays absent — Save must not invent an empty block.
                File.WriteAllText(path, """{ "approvalMode": "auto" }""");
                TuiConfig.Load().Save();
                Assert.Null(JObject.Parse(File.ReadAllText(path))["mcpServers"]);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", prior);
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    public class McpToolNameTests
    {
        [Theory]
        [InlineData("comfy", "run_workflow")]
        [InlineData("a", "b")]
        [InlineData("my-server_2", "tool__with__doubles")]
        [InlineData("_lead", "x")]
        public void Build_parse_round_trip(string server, string tool)
        {
            string q = McpToolName.Build(server, tool);
            Assert.StartsWith("mcp__", q);
            Assert.True(McpToolName.TryParse(q, out var s, out var t));
            Assert.Equal(server, s);
            Assert.Equal(tool, t);
        }

        [Fact]
        public void Build_shape()
        {
            Assert.Equal("mcp__comfy__which", McpToolName.Build("comfy", "which"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("read_file")]
        [InlineData("mcp__")]
        [InlineData("mcp__comfy")]
        [InlineData("mcp__comfy__")]
        [InlineData("mcp____tool")]
        [InlineData("mcp__Comfy__tool")]
        [InlineData("MCP__comfy__tool")]
        public void Parse_rejects_non_mcp_names(string? name)
        {
            Assert.False(McpToolName.TryParse(name, out _, out _));
        }

        [Theory]
        [InlineData("Comfy")]
        [InlineData("a__b")]
        [InlineData("a_")]
        [InlineData("a b")]
        [InlineData("")]
        public void Build_rejects_invalid_server_names(string server)
        {
            Assert.Throws<ArgumentException>(() => McpToolName.Build(server, "t"));
        }
    }

    public class McpResultFormatTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcpfmt_{Guid.NewGuid():N}");

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void Text_blocks_are_concatenated()
        {
            var r = new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = "one" }, new TextContentBlock { Text = "two" } },
            };
            Assert.Equal("one\ntwo", McpClientManager.FormatResult("s", r, _dir));
        }

        [Fact]
        public void Image_is_saved_to_disk_and_referenced()
        {
            byte[] png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            var r = new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = "done" }, ImageContentBlock.FromBytes(png, "image/png") },
            };
            string text = McpClientManager.FormatResult("s", r, _dir);
            var lines = text.Split('\n');
            Assert.Equal("done", lines[0]);
            Assert.StartsWith("[image saved: ", lines[1]);
            string path = lines[1].Substring("[image saved: ".Length).TrimEnd(']');
            Assert.StartsWith(_dir, path);
            Assert.EndsWith(".png", path);
            Assert.Equal(png, File.ReadAllBytes(path));
        }

        [Fact]
        public void Structured_content_is_pretty_json_when_there_is_no_text()
        {
            using var doc = JsonDocument.Parse("""{"workspace_path":"C:\\w","n":1}""");
            var r = new CallToolResult { Content = new List<ContentBlock>(), StructuredContent = doc.RootElement.Clone() };
            string text = McpClientManager.FormatResult("s", r, _dir);
            Assert.Contains("\"workspace_path\": \"C:\\\\w\"", text);
            Assert.Contains("\n", text);
        }

        [Fact]
        public void Structured_content_is_not_duplicated_next_to_text()
        {
            using var doc = JsonDocument.Parse("""{"a":1}""");
            var r = new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = "{\"a\":1}" } },
                StructuredContent = doc.RootElement.Clone(),
            };
            Assert.Equal("{\"a\":1}", McpClientManager.FormatResult("s", r, _dir));
        }

        [Fact]
        public void Error_result_is_marked()
        {
            var r = new CallToolResult { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = "bad" } } };
            Assert.Equal("[MCP TOOL ERROR] bad", McpClientManager.FormatResult("s", r, _dir));
        }

        [Fact]
        public void JObject_arguments_convert_to_json_elements()
        {
            var args = McpClientManager.ToArguments(JObject.Parse("""{"s":"x","n":2,"o":{"k":[1,2]},"z":null}"""));
            Assert.NotNull(args);
            Assert.Equal("x", ((JsonElement)args!["s"]!).GetString());
            Assert.Equal(2, ((JsonElement)args["n"]!).GetInt32());
            Assert.Equal(2, ((JsonElement)args["o"]!).GetProperty("k").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, ((JsonElement)args["z"]!).ValueKind);
            Assert.Null(McpClientManager.ToArguments(null));
        }
    }

    public class McpClientManagerFailureTests
    {
        [Fact]
        public async Task Unknown_server_and_disallowed_tool_return_errors()
        {
            await using var m = new McpClientManager(new[]
            {
                new McpServerConfig { Name = "a", Command = "a.exe", Tools = new[] { "only" } },
            });
            Assert.Contains("no MCP server named 'nope'", await m.CallToolAsync("nope", "t", null));
            Assert.Contains("not in this server's \"tools\" allowlist", await m.CallToolAsync("a", "other", null));
            Assert.Null(m.GetServerProcessId("a"));   // the allowlist check started nothing
        }

        [Fact]
        public async Task Missing_executable_never_throws_and_retries_exactly_once()
        {
            string bogus = Path.Combine(Path.GetTempPath(), $"no_such_{Guid.NewGuid():N}.exe");
            await using var m = new McpClientManager(new[] { new McpServerConfig { Name = "ghost", Command = bogus } });

            string first = await m.CallToolAsync("ghost", "t", null);
            Assert.StartsWith("[MCP ERROR] server 'ghost' failed to start", first);
            Assert.DoesNotContain("not relaunching", first);

            string second = await m.CallToolAsync("ghost", "t", null);   // the one automatic restart
            Assert.StartsWith("[MCP ERROR] server 'ghost' failed to start", second);

            string third = await m.CallToolAsync("ghost", "t", null);
            Assert.Contains("not relaunching", third);

            var listing = await m.ListToolsAsync("ghost");
            Assert.Empty(listing.Tools);
            Assert.Contains("ghost", listing.Error);
        }

        [Fact]
        public async Task Server_that_dies_at_startup_reports_its_stderr_tail()
        {
            string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            await using var m = new McpClientManager(new[]
            {
                new McpServerConfig { Name = "crash", Command = cmd, Args = new[] { "/c", "echo boom-from-stderr 1>&2 & exit 3" } },
            });
            string err = await m.CallToolAsync("crash", "t", null);
            Assert.StartsWith("[MCP ERROR] server 'crash' failed to start", err);
            Assert.Contains("boom-from-stderr", err);
        }
    }

    /// <summary>Skips unless DEVMIND_MCP_SMOKE=1 (needs the real comfy-mcp install and "comfy" in devmind.json).</summary>
    public sealed class McpSmokeFactAttribute : FactAttribute
    {
        public McpSmokeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DEVMIND_MCP_SMOKE") != "1")
                Skip = "Live MCP smoke test: set DEVMIND_MCP_SMOKE=1 to run.";
        }
    }

    public class McpClientSmokeTests
    {
        [McpSmokeFact]
        public async Task Comfy_server_lists_calls_and_dies_on_dispose()
        {
            var entry = McpServerConfig.Load().SingleOrDefault(s => s.Name == "comfy");
            Assert.True(entry != null, "no \"comfy\" entry under mcpServers in the global devmind.json");

            // The allowlist is the operator's choice and is covered by unit tests; this test is
            // about the transport, so it sees every tool the server offers.
            var config = new McpServerConfig
            {
                Name = entry!.Name, Command = entry.Command, Args = entry.Args, Env = entry.Env,
                CallTimeoutSeconds = entry.CallTimeoutSeconds,
            };

            int pid;
            var m = new McpClientManager(new[] { config });
            try
            {
                var listing = await m.ListToolsAsync("comfy");
                Assert.Null(listing.Error);
                Assert.NotEmpty(listing.Tools);
                var which = Assert.Single(listing.Tools, t => t.Name == "which");
                Assert.Equal("mcp__comfy__which", which.QualifiedName);
                Assert.Equal("object", (string?)which.InputSchema["type"]);

                string text = await m.CallToolAsync("comfy", "which", new JObject());
                Assert.Contains("workspace_path", text);

                pid = m.GetServerProcessId("comfy") ?? throw new Xunit.Sdk.XunitException("no server PID");
            }
            finally
            {
                await m.DisposeAsync();
            }

            Assert.False(IsRunning(pid), $"MCP server process {pid} survived DisposeAsync");
        }

        private static bool IsRunning(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
