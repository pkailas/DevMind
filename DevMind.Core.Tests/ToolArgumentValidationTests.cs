// File: ToolArgumentValidationTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-53 / H-54 / H-55 (VLink.Warehouses jobs 1971-1977, 2026-10-02).
//
//   H-53  read_file / grep_file / create_file sent with the path under the wrong key reached
//         the executor with a null FileName: "[READ ERROR] : Value cannot be null. (Parameter
//         'key')", "[FILE ERROR] : ... (Parameter 'path2')". The model had to guess the fix.
//   H-54  patch_file edits keyed old_text/new_text were skipped one by one, the call fell back
//         to a top-level find that did not exist, and PatchEngine skipped the empty FIND.
//   H-55  a grep_file and a read_file of the SAME file in one turn: both results were filed
//         under the file name, the read overwrote the grep, and both calls got the read back.
//         job-1973 concluded "grep_file is broken for patterns containing parentheses" from a
//         grep that had found 5 matches. (grep/find are substring matchers, not regex — an
//         unbalanced paren was never the problem.)

using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ToolArgumentValidationTests : IDisposable
    {
        private readonly string _dir;

        public ToolArgumentValidationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h53_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private string NoPromptFile => Path.Combine(_dir, "no-system-prompt.md");

        private static ToolCallResult Call(string name, params (string key, string value)[] args)
        {
            var tc = new ToolCallResult { Id = "call_1", Name = name };
            foreach (var (k, v) in args) tc.Arguments[k] = v;
            return tc;
        }

        private static string Map(ToolCallResult tc, out List<ResponseBlock> blocks)
        {
            blocks = ToolCallMapper.Map(new List<ToolCallResult> { tc }, "dotnet build");
            return LoopHelpers.BuildToolResultContent(tc, new ExecutionResult(), blocks);
        }

        // ── H-53: missing / misnamed required arguments ─────────────────────────────

        [Fact]
        public void ReadFile_PathUnderWrongKey_IsAToolErrorNamingTheKeys_AndMapsToNothingExecutable()
        {
            var tc = Call("read_file", ("path", @"C:\x\a.cs"), ("start_line", "3"));

            string toolResult = Map(tc, out var blocks);

            Assert.Equal(
                "[TOOL ERROR] read_file: missing required argument 'filename'. Received: path, start_line. " +
                "Expected: filename (required), start_line, end_line, force_full.",
                toolResult);
            Assert.All(blocks, b => Assert.Equal(BlockType.Text, b.Type));
        }

        [Fact]
        public void GrepFile_PathUnderWrongKey_IsAToolError()
        {
            var tc = Call("grep_file", ("pattern", "Connect"), ("path", @"C:\x\a.cs"));

            string toolResult = Map(tc, out var blocks);

            Assert.Contains("grep_file: missing required argument 'filename'. Received: pattern, path.", toolResult);
            Assert.DoesNotContain(blocks, b => b.Type == BlockType.Grep);
        }

        [Fact]
        public void BlankRequiredArgument_IsAToolError_ButEmptyContentIsAllowed()
        {
            Assert.Contains("missing required argument 'command'", Map(Call("run_shell", ("command", "  ")), out _));

            var emptyFile = Call("create_file", ("filename", @"C:\x\empty.txt"), ("content", ""));
            Map(emptyFile, out var blocks);
            Assert.Null(emptyFile.ArgumentError);
            Assert.Equal(BlockType.File, Assert.Single(blocks).Type);
        }

        [Fact]
        public void WellFormedCalls_MapExactlyAsBefore()
        {
            var read = Call("read_file", ("filename", @"C:\x\a.cs"), ("start_line", "3"), ("end_line", "9"));
            Map(read, out var readBlocks);
            Assert.Null(read.ArgumentError);
            var rb = Assert.Single(readBlocks);
            Assert.Equal(BlockType.ReadRequest, rb.Type);
            Assert.Equal(@"C:\x\a.cs", rb.FileName);
            Assert.Equal(3, rb.RangeStart);
            Assert.Equal(9, rb.RangeEnd);

            var patch = Call("patch_file", ("filename", @"C:\x\a.cs"),
                ("edits", "[{\"find\":\"a\",\"replace\":\"b\"},{\"find\":\"c\",\"replace\":\"\"}]"));
            Map(patch, out var patchBlocks);
            Assert.Null(patch.ArgumentError);
            Assert.Equal("PATCH C:\\x\\a.cs\nFIND:\na\nREPLACE:\nb\nFIND:\nc\nREPLACE:\n\nEND_PATCH",
                Assert.Single(patchBlocks).Content);

            var single = Call("patch_file", ("filename", @"C:\x\a.cs"), ("find", "a"), ("replace", "b"));
            Map(single, out var singleBlocks);
            Assert.Null(single.ArgumentError);
            Assert.Equal("PATCH C:\\x\\a.cs\nFIND:\na\nREPLACE:\nb\nEND_PATCH", Assert.Single(singleBlocks).Content);
        }

        [Fact]
        public void UnknownTool_ToolResultSaysSo_InsteadOfExecuted()
        {
            Assert.Equal("[TOOL ERROR] Unknown tool call: frobnicate", Map(Call("frobnicate"), out _));
        }

        [Theory]
        [InlineData(BlockType.ReadRequest, "read_file")]
        [InlineData(BlockType.Grep, "grep_file")]
        [InlineData(BlockType.File, "create_file")]
        public async Task Executor_BlockWithNoFileName_ReportsTheCause_NotANullException(BlockType type, string tool)
        {
            // Belt and braces: a block built without going through ToolCallMapper.
            var block = new ResponseBlock { Type = type, FileName = null, Pattern = "x", Content = "c", ToolCallId = "call_1" };
            var executor = new AgenticExecutor(new BufferedAgenticHost(_dir), new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            var result = await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild },
                new ResponseOutcome(new List<ResponseBlock> { block }));

            string error = Assert.Single(result.Errors);
            Assert.Equal($"{tool}: no file name was provided — pass the file path in 'filename'.", error);
            Assert.Empty(Directory.GetFiles(_dir));
        }

        // ── H-54: patch_file edit keys ──────────────────────────────────────────────

        [Fact]
        public void PatchFile_EditsKeyedOldTextNewText_IsAnErrorNamingTheEditAndItsKeys()
        {
            var tc = Call("patch_file", ("filename", @"C:\x\a.cs"),
                ("edits", "[{\"old_text\":\"a\",\"new_text\":\"b\"}]"));

            Assert.Equal(
                "[TOOL ERROR] patch_file: edit 1 has no 'find' key (keys: old_text, new_text). " +
                "Each edit must be {\"find\": ..., \"replace\": ...}.",
                Map(tc, out var blocks));
            Assert.DoesNotContain(blocks, b => b.Type == BlockType.Patch);
        }

        [Fact]
        public void PatchFile_OneBadEditAmongGoodOnes_RejectsTheWholeCall_NoFallbackToTopLevelFind()
        {
            var tc = Call("patch_file", ("filename", @"C:\x\a.cs"), ("find", "a"), ("replace", "b"),
                ("edits", "[{\"find\":\"a\",\"replace\":\"b\"},{\"find\":\"c\",\"new_text\":\"d\"}]"));

            string toolResult = Map(tc, out var blocks);

            Assert.Contains("edit 2 has no 'replace' key (keys: find, new_text)", toolResult);
            Assert.DoesNotContain(blocks, b => b.Type == BlockType.Patch);
        }

        [Fact]
        public async Task PatchFile_OldTextNewText_EndToEnd_FileUntouched_ModelToldWhy()
        {
            string file = Path.Combine(_dir, "a.txt");
            File.WriteAllText(file, "alpha\nbeta\n");
            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("patch_file",
                "{\"filename\":\"" + file.Replace("\\", "\\\\") + "\"," +
                "\"edits\":[{\"old_text\":\"alpha\",\"new_text\":\"ALPHA\"}]}"));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));

            var result = await Run(server);

            Assert.Null(result.Error);
            Assert.Equal("alpha\nbeta\n", File.ReadAllText(file));
            Assert.DoesNotContain(result.Actions, a => a.Kind == "patch");
            string toolMsg = Assert.Single(ToolMessages(server.RequestBodies[1]));
            Assert.Contains("patch_file: edit 1 has no 'find' key (keys: old_text, new_text)", toolMsg);
        }

        [Fact]
        public async Task CreateFile_PathUnderWrongKey_EndToEnd_NothingWritten_NoNullException()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("create_file",
                "{\"path\":\"made.txt\",\"content\":\"x\"}"));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));

            var result = await Run(server);

            Assert.Null(result.Error);
            Assert.False(File.Exists(Path.Combine(_dir, "made.txt")));
            Assert.Empty(result.Actions);
            string toolMsg = Assert.Single(ToolMessages(server.RequestBodies[1]));
            Assert.Contains("create_file: missing required argument 'filename'. Received: path, content.", toolMsg);
            Assert.DoesNotContain("Value cannot be null", server.RequestBodies[1]);
        }

        // ── H-55: one turn, grep + read of the same file ────────────────────────────

        [Fact]
        public async Task GrepAndReadOfOneFile_InOneTurn_EachCallGetsItsOwnResult()
        {
            string file = Path.Combine(_dir, "MailKitSmtpTransportTests.cs");
            File.WriteAllText(file,
                "public Task ConnectAsync(string host)\n" +
                "{\n" +
                "    Calls.Add(\"connect\");\n" +
                "}\n");
            using var server = new FakeSseServer();
            // job-1973's pattern verbatim — an unbalanced '(' and a quote.
            server.SseQueue.Add(TwoToolCallsSse(
                ("call_grep", "grep_file", new JObject { ["pattern"] = "ConnectAsync|Calls.Add(\"connect", ["filename"] = file }),
                ("call_read", "read_file", new JObject { ["filename"] = file, ["start_line"] = 2, ["end_line"] = 2 })));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));

            var result = await Run(server);

            Assert.Null(result.Error);
            var tools = ToolMessagesById(server.RequestBodies[1]);
            Assert.Contains("(2 matches)", tools["call_grep"]);
            Assert.Contains("Calls.Add(\"connect\");", tools["call_grep"]);
            Assert.DoesNotContain("matches)", tools["call_read"]);
            Assert.NotEqual(tools["call_grep"], tools["call_read"]);
        }

        // ── helpers ─────────────────────────────────────────────────────────────────

        private async Task<HeadlessAgentResult> Run(FakeSseServer server)
        {
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            TextWriter prevOut = Console.Out;
            Console.SetOut(new StringWriter());
            try
            {
                return await HeadlessAgent.RunAsync(
                    "Do the thing.",
                    new HeadlessOptions
                    {
                        RequestTimeoutMinutes = 1,
                        FirstTokenTimeoutMinutes = 1,
                        ManualContextSize = 32768,
                        AgenticLoopMaxDepth = 6,
                    },
                    server.BaseUrl, apiKey: null!,
                    workingDirectory: _dir, buildCommand: "dotnet build",
                    ct: CancellationToken.None,
                    promptFilePath: NoPromptFile);
            }
            finally
            {
                Console.SetOut(prevOut);
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        private static string TwoToolCallsSse(params (string id, string name, JObject args)[] calls)
        {
            var toolCalls = new JArray();
            for (int i = 0; i < calls.Length; i++)
                toolCalls.Add(new JObject
                {
                    ["index"] = i,
                    ["id"] = calls[i].id,
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = calls[i].name,
                        ["arguments"] = calls[i].args.ToString(Newtonsoft.Json.Formatting.None),
                    },
                });
            var chunk = new JObject { ["choices"] = new JArray(new JObject { ["delta"] = new JObject { ["tool_calls"] = toolCalls } }) };
            return "data: " + chunk.ToString(Newtonsoft.Json.Formatting.None) + "\n\n" +
                   "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
                   "data: [DONE]\n\n";
        }

        private static IEnumerable<JObject> ToolMessageObjects(string requestBody)
            => JObject.Parse(requestBody)["messages"]!.OfType<JObject>().Where(m => (string?)m["role"] == "tool");

        private static List<string> ToolMessages(string requestBody)
            => ToolMessageObjects(requestBody).Select(m => (string)m["content"]!).ToList();

        private static Dictionary<string, string> ToolMessagesById(string requestBody)
            => ToolMessageObjects(requestBody).ToDictionary(m => (string)m["tool_call_id"]!, m => (string)m["content"]!);
    }
}
