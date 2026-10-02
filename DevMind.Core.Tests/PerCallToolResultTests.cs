// File: PerCallToolResultTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-55 (finished): every tool call in a turn gets back exactly its own result.
//
// d3801fb gave the read-side tools per-call results. Two cases were left:
//   * patch_file reported from turn-wide state (PatchedPaths, Errors) and filed its context
//     echo under the file path — the same key read_file used — so a patch and a read of one
//     file in one turn, or two patches of it, reported each other's results.
//   * patches were applied after EVERY other call in the turn, so patch_file then read_file
//     read the pre-patch text (and patch_file then run_build built unpatched code).
// The write tools (create/append/delete/rename) had the same turn-wide-list problem: a
// failed create_file after a successful one was told "[File created: <the other file>]".

using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class PerCallToolResultTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _file;

        public PerCallToolResultTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h55_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "Target.cs");
            File.WriteAllText(_file, "class Target\n{\n    int alpha = 1;\n    int beta = 2;\n}\n");
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private JObject Patch(string find, string replace)
            => new JObject { ["filename"] = _file, ["find"] = find, ["replace"] = replace };

        private JObject Read() => new JObject { ["filename"] = _file };

        // A range read: after a patch the file counts as already read, and a full re-read
        // returns the outline by design (ContextEngine.RenderReadBlock) — not this bug.
        private JObject ReadRange() => new JObject { ["filename"] = _file, ["start_line"] = 1, ["end_line"] = 6 };

        [Fact]
        public async Task PatchThenRead_SameTurn_PatchGetsItsResult_ReadSeesThePatchedFile()
        {
            var tools = await RunOneTurn(
                ("call_patch", "patch_file", Patch("int alpha = 1;", "int ALPHA = 10;")),
                ("call_read", "read_file", ReadRange()));

            Assert.StartsWith("[PATCH applied to ", tools["call_patch"]);
            Assert.Contains("ALPHA = 10", tools["call_read"]);
            Assert.DoesNotContain("alpha = 1", tools["call_read"]);
            Assert.DoesNotContain("[PATCH applied", tools["call_read"]);
        }

        [Fact]
        public async Task ReadThenPatch_SameTurn_ReadSeesTheOriginal_PatchGetsItsResult()
        {
            var tools = await RunOneTurn(
                ("call_read", "read_file", Read()),
                ("call_patch", "patch_file", Patch("int alpha = 1;", "int ALPHA = 10;")));

            Assert.Contains("alpha = 1", tools["call_read"]);
            Assert.DoesNotContain("ALPHA", tools["call_read"]);
            Assert.StartsWith("[PATCH applied to ", tools["call_patch"]);
            Assert.Contains("ALPHA = 10", File.ReadAllText(_file));
        }

        [Fact]
        public async Task TwoPatchesOfOneFile_SameTurn_EachGetsItsOwnResult()
        {
            var tools = await RunOneTurn(
                ("call_ok", "patch_file", Patch("int alpha = 1;", "int ALPHA = 10;")),
                ("call_bad", "patch_file", Patch("int gamma = 3;", "int GAMMA = 30;")));

            Assert.StartsWith("[PATCH applied to ", tools["call_ok"]);
            Assert.DoesNotContain("PATCH-FAILED", tools["call_ok"]);
            Assert.StartsWith("[PATCH-FAILED:", tools["call_bad"]);
            Assert.DoesNotContain("[PATCH applied", tools["call_bad"]);
        }

        [Fact]
        public async Task TwoCreateFiles_SameTurn_TheFailedOneIsNotToldTheOtherSucceeded()
        {
            string good = Path.Combine(_dir, "made.txt");
            // Outside the working directory: the headless host refuses the write.
            string outside = Path.Combine(Path.GetTempPath(), $"devmind_h55_outside_{Guid.NewGuid():N}", "nope.txt");

            var tools = await RunOneTurn(
                ("call_good", "create_file", new JObject { ["filename"] = good, ["content"] = "x" }),
                ("call_bad", "create_file", new JObject { ["filename"] = outside, ["content"] = "y" }));

            Assert.Equal($"[File created: {good}]", tools["call_good"]);
            Assert.StartsWith("[CREATE_FILE FAILED: no file was created", tools["call_bad"]);
            Assert.DoesNotContain("made.txt", tools["call_bad"]);
            Assert.False(File.Exists(outside));
        }

        // ── helpers ─────────────────────────────────────────────────────────────────

        /// <summary>Runs one headless turn whose first response makes <paramref name="calls"/>
        /// in that order, then task_done; returns the tool messages of the second request by id.</summary>
        private async Task<Dictionary<string, string>> RunOneTurn(params (string id, string name, JObject args)[] calls)
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(ToolCallsSse(calls));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            TextWriter prevOut = Console.Out;
            Console.SetOut(new StringWriter());
            try
            {
                var result = await HeadlessAgent.RunAsync(
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
                    promptFilePath: Path.Combine(_dir, "no-system-prompt.md"));
                Assert.Null(result.Error);
            }
            finally
            {
                Console.SetOut(prevOut);
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }

            return JObject.Parse(server.RequestBodies[1])["messages"]!
                .OfType<JObject>()
                .Where(m => (string?)m["role"] == "tool")
                .ToDictionary(m => (string)m["tool_call_id"]!, m => (string)m["content"]!);
        }

        private static string ToolCallsSse((string id, string name, JObject args)[] calls)
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
    }
}
