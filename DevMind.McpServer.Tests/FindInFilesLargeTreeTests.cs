// File: FindInFilesLargeTreeTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// find_in_files over the TTA help tree (minified one-line HTML pages) sent a 36.7 MiB
// tools/call result. Claude Desktop's stdio reader caps one message at 32 MiB, so it closed
// the connection mid-call ("The connection to the connector closed while the tool was
// running") and every DevMind tool was "no longer available" until a restart. The server
// itself never crashed. Two guards now: find/grep clip long lines (FileReadTools), and the
// dispatcher caps every tool result (McpServices.CapResult).

using System.Text.Json;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class FindInFilesLargeTreeTests : IDisposable
    {
        private const int ClientMessageLimit = 32 * 1024 * 1024;   // Claude Desktop ReadBuffer
        private const string Needle = "Federated security";

        private readonly string _dir;
        private readonly McpServices _svc;
        private readonly DevMindTools _tools;

        public FindInFilesLargeTreeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcp_findbig_{Guid.NewGuid():N}");
            // 130 pages of ~245 KB, each one line holding '<' / '>' heavy markup — the shape that
            // escapes 6x in JSON. The unclipped result would be ~24.5M chars / ~37 MiB on the wire.
            string filler = string.Concat(Enumerable.Repeat("<li><a href=\"t.html\">Topic</a></li>", 3_500));
            for (int i = 0; i < 130; i++)
            {
                string sub = Path.Combine(_dir, "help", $"Area{i % 7}", "html");
                Directory.CreateDirectory(sub);
                File.WriteAllText(Path.Combine(sub, $"p{i:D3}.html"),
                    $"<html><body>{filler}<p>{Needle} settings</p>{filler}</body></html>");
            }
            // The server's own working directory is elsewhere: root is passed explicitly, as
            // Claude Desktop (no --dir) must.
            _svc = new McpServices(Path.GetTempPath());
            _tools = new DevMindTools(_svc);
        }

        public void Dispose()
        {
            _svc.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>Bytes the result costs inside a tools/call response, escaped the way the
        /// MCP SDK's System.Text.Json serializer escapes it.</summary>
        private static int WireBytes(string text) =>
            JsonSerializer.SerializeToUtf8Bytes(new { content = new[] { new { type = "text", text } } }).Length;

        [Fact]
        public async Task FindInFiles_OverLargeTree_ReturnsASmallTruncatedResult()
        {
            string result = await _tools.FindInFiles(Needle, "*.html", root: Path.Combine(_dir, "help"));

            int bytes = WireBytes(result);
            Assert.True(bytes < 256 * 1024, $"tools/call result is {bytes:N0} bytes on the wire");
            Assert.StartsWith($"find_in_files results for \"{Needle}\" in *.html (100+ matches", result);
            Assert.Contains("narrow your root or glob", result);
            Assert.Equal(FileReadTools.FindMaxMatches,
                result.Split('\n').Count(l => l.Contains($"{Needle} settings", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task Dispatcher_CapsAnyOversizedToolResult_UnderTheClientLimit()
        {
            // Worst case for escaping: all '<' (6 bytes each in JSON). 8M chars would be 48 MB.
            string huge = new string('<', 8_000_000);
            string result = await _svc.EnqueueAsync(() => Task.FromResult(huge), CancellationToken.None);

            Assert.True(result.Length < McpServices.MaxResultChars + 500);
            Assert.Contains("output truncated by the DevMind MCP server", result);
            Assert.Contains("Narrow the request", result);
            Assert.True(WireBytes(result) < ClientMessageLimit, $"capped result is {WireBytes(result):N0} bytes on the wire");
        }

        [Fact]
        public void CapResult_DoesNotSplitASurrogatePair()
        {
            // An emoji straddling the cut: the high surrogate must not be left dangling.
            string s = new string('a', McpServices.MaxResultChars - 1) + "\U0001F600" + "tail";
            string capped = McpServices.CapResult(s);
            Assert.StartsWith(new string('a', McpServices.MaxResultChars - 1) + "\n[output truncated", capped);
        }

        [Fact]
        public void CapResult_LeavesNormalResultsUntouched()
        {
            string s = new string('x', 10_000);
            Assert.Same(s, McpServices.CapResult(s));
        }
    }
}
