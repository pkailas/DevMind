// File: PatchMarkerCollisionTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-78: a patch_file tool call whose FIND contained `{ replace: true }` corrupted the file
// (Parsely job-2804, ExceptionsPage.tsx): a stray `};` and a literal `REPLACE:` line were left
// behind and blocks moved. ToolCallMapper turned the structured find/replace back into the text
// PATCH format and PatchEngine.ParsePatchBlocks re-parsed it, finding the REPLACE: marker with a
// case-insensitive, unanchored IndexOf — so the `replace:` inside the code ended the FIND.
//
// Tool-call pairs now travel verbatim (ResponseBlock.PatchPairs → PatchEngine.ResolvePairs).
// These tests drive the real mapper, executor and host, and compare the file byte for byte.

using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class PatchMarkerCollisionTests : IDisposable
    {
        private readonly string _dir;

        public PatchMarkerCollisionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h78_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        // The job-2804 shape: a tab switch that writes the URL with { replace: true }.
        private const string Page =
            "export function ExceptionsPage() {\n" +
            "  const [searchParams, setSearchParams] = useSearchParams();\n" +
            "\n" +
            "  const onTabChange = (next: string) => {\n" +
            "    setSearchParams(next === 'review' ? {} : { tab: next }, { replace: true });\n" +
            "  };\n" +
            "\n" +
            "  return <Tabs onChange={onTabChange} />;\n" +
            "}\n";

        private string Write(string name, string text)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
            return path;
        }

        private static string ReadBytes(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));

        private static ToolCallResult PatchCall(string file, string find, string replace) => new()
        {
            Id = "call-h78", Name = "patch_file",
            Arguments = new Dictionary<string, string> { ["filename"] = file, ["find"] = find, ["replace"] = replace },
        };

        private static ToolCallResult EditsCall(string file, params (string find, string replace)[] edits) => new()
        {
            Id = "call-h78-edits", Name = "patch_file",
            Arguments = new Dictionary<string, string>
            {
                ["filename"] = file,
                ["edits"] = new JArray(edits.Select(e => new JObject { ["find"] = e.find, ["replace"] = e.replace }))
                    .ToString(Newtonsoft.Json.Formatting.None),
            },
        };

        private async Task<(ExecutionResult result, string transcript)> RunAsync(ToolCallResult call)
        {
            var transcript = new StringBuilder();
            IAgenticHost host = new BufferedAgenticHost(_dir, outputSink: (t, _) => transcript.Append(t));
            var blocks = ToolCallMapper.Map(new List<ToolCallResult> { call }, buildCommand: "dotnet build");
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);
            var result = await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild }, new ResponseOutcome(blocks));
            return (result, transcript.ToString());
        }

        private static void AssertApplied(ExecutionResult result, string transcript)
        {
            Assert.DoesNotContain("PATCH-FAILED", transcript);
            Assert.Empty(result.Errors);
            Assert.Equal(1, result.PatchesApplied);
        }

        // ── job-2804 ──

        [Fact]
        public async Task FindContainingReplaceColon_AppliesExactly()
        {
            string path = Write("ExceptionsPage.tsx", Page);
            const string find =
                "  const onTabChange = (next: string) => {\n" +
                "    setSearchParams(next === 'review' ? {} : { tab: next }, { replace: true });\n" +
                "  };";
            const string replace =
                "  const onTabChange = (next: string) => {\n" +
                "    setSearchParams(next === 'review' ? {} : { tab: next }, { replace: true });\n" +
                "    setPage(1);\n" +
                "  };";

            var (result, transcript) = await RunAsync(PatchCall(path, find, replace));

            AssertApplied(result, transcript);
            Assert.Equal(Page.Replace(find, replace), ReadBytes(path));
        }

        [Fact]
        public async Task ReplaceContainingFindColon_AppliesExactly()
        {
            string path = Write("ExceptionsPage.tsx", Page);
            const string find = "  return <Tabs onChange={onTabChange} />;";
            const string replace =
                "  const query = { find: x, FIND: y };\n" +
                "  return <Tabs onChange={onTabChange} query={query} />;";

            var (result, transcript) = await RunAsync(PatchCall(path, find, replace));

            AssertApplied(result, transcript);
            Assert.Equal(Page.Replace(find, replace), ReadBytes(path));
        }

        // A line that IS exactly a marker: unrepresentable in the text format, fine as pairs.
        [Fact]
        public async Task LinesThatAreExactlyMarkers_ApplyExactly()
        {
            const string original =
                "# Patch format\n" +
                "\n" +
                "FIND:\n" +
                "old text\n" +
                "REPLACE:\n" +
                "new text\n";
            string path = Write("format.md", original);
            const string find = "FIND:\nold text\nREPLACE:\nnew text";
            const string replace = "FIND:\nold text\nREPLACE:\nnewer text\nSHELL: echo kept\n---\nEND";

            var (result, transcript) = await RunAsync(PatchCall(path, find, replace));

            AssertApplied(result, transcript);
            Assert.Equal(original.Replace(find, replace), ReadBytes(path));
        }

        [Fact]
        public async Task EditsList_OnePairContainingReplaceColon_AllApplyExactly()
        {
            string path = Write("ExceptionsPage.tsx", Page);
            var edits = new[]
            {
                ("  const [searchParams, setSearchParams] = useSearchParams();",
                 "  const [searchParams, setSearchParams] = useSearchParams();\n  const [page, setPage] = useState(1);"),
                ("    setSearchParams(next === 'review' ? {} : { tab: next }, { replace: true });",
                 "    setSearchParams(next === 'review' ? {} : { tab: next }, { replace: true });\n    setPage(1);"),
                ("  return <Tabs onChange={onTabChange} />;",
                 "  return <Tabs onChange={onTabChange} page={page} />;"),
            };

            var (result, transcript) = await RunAsync(EditsCall(path, edits));

            AssertApplied(result, transcript);
            string expected = Page;
            foreach (var (f, r) in edits) expected = expected.Replace(f, r);
            Assert.Equal(expected, ReadBytes(path));
        }

        // CRLF file: the pairs path keeps the file's ending, as the text path did.
        [Fact]
        public async Task CrlfFile_FindContainingReplaceColon_StaysCrlf()
        {
            string crlfPage = Page.Replace("\n", "\r\n");
            string path = Write("ExceptionsPage.tsx", crlfPage);
            const string find = "    setSearchParams(next === 'review' ? {} : { tab: next }, { replace: true });";
            const string replace = find + "\n    setPage(1);";

            var (result, transcript) = await RunAsync(PatchCall(path, find, replace));

            AssertApplied(result, transcript);
            Assert.Equal(crlfPage.Replace(find, find + "\r\n    setPage(1);"), ReadBytes(path));
        }

        // The mapper still emits the text form (display, logging, the PATCH header) next to the pairs.
        [Fact]
        public void Mapper_CarriesVerbatimPairs()
        {
            var block = Assert.Single(ToolCallMapper.Map(
                new List<ToolCallResult> { PatchCall("a.tsx", "x({ replace: true })", "y") }, buildCommand: "dotnet build"));

            Assert.Equal(BlockType.Patch, block.Type);
            var pair = Assert.Single(block.PatchPairs);
            Assert.Equal("x({ replace: true })", pair.findText);
            Assert.Equal("y", pair.replaceText);
            Assert.StartsWith("PATCH a.tsx\n", block.Content);
        }
    }
}
