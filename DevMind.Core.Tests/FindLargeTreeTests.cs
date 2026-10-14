// File: FindLargeTreeTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// find_in_files over a large tree of minified help HTML (TTA help\AdvancedStudio: 1,214 pages,
// each one ~245 KB line with the nav — and so the pattern — inlined) returned 100 whole lines,
// 24.5M chars. As one JSON-RPC message that was 36.7 MiB, over Claude Desktop's 32 MiB read
// buffer: the client dropped the connection and every DevMind tool went "no longer available".
// Matching lines are now clipped to a window around the match, so the result stays small.

using System.Text.Json;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class FindLargeTreeTests : IDisposable
    {
        private const string Needle = "Federated security";
        private const int Pages = 130;           // > FindMaxMatches, so the cap is hit
        private const int PageChars = 245_000;   // one line per page, like the minified help

        private readonly string _dir;
        private readonly FileReadTools _tools;

        public FindLargeTreeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_findbig_{Guid.NewGuid():N}");
            string filler = string.Concat(Enumerable.Repeat("<li><a href=\"topic.html\">Topic &amp; more</a></li>", PageChars / 50 / 2));
            for (int i = 0; i < Pages; i++)
            {
                string sub = Path.Combine(_dir, "help", $"Section{i % 5}");
                Directory.CreateDirectory(sub);
                File.WriteAllText(Path.Combine(sub, $"page{i:D3}.html"),
                    $"<html><body>{filler}<p>Configure {Needle} for the tenant.</p>{filler}</body></html>");
            }
            _tools = new FileReadTools(FileReadPolicy.Mcp, _dir, new FileContentCache(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), new FileSnapshotStore(capacity: 8), shell: null);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void LargeTreeOfOneLinePages_ResultStaysSmall_AndSaysHowToNarrow()
        {
            FileReadResult r = _tools.Find(Needle, "*.html", Path.Combine(_dir, "help"), null, null);

            // The failure mode itself: the serialized result must be nowhere near 32 MiB.
            int wireBytes = JsonSerializer.Serialize(r.Text).Length;
            Assert.True(wireBytes < 256 * 1024, $"find_in_files result serializes to {wireBytes:N0} bytes");

            string[] lines = r.Text.Split('\n');
            Assert.Contains($"{FileReadTools.FindMaxMatches}+ matches", lines[0]);
            Assert.Contains("root or glob", lines[0]);
            string[] hits = lines.Where(l => l.StartsWith("  page", StringComparison.Ordinal)).ToArray();
            Assert.Equal(FileReadTools.FindMaxMatches, hits.Length);
            Assert.All(hits, h =>
            {
                Assert.Contains($"Configure {Needle} for the tenant.", h);   // the match is kept, centred
                Assert.Contains("[line clipped", h);
                Assert.True(h.Length < FileReadTools.MaxMatchLineChars + 100, $"hit line is {h.Length} chars");
            });
            Assert.Contains("narrow your root or glob", lines[^1]);
        }

        [Fact]
        public void Find_HonoursCancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                _tools.Find(Needle, "*.html", Path.Combine(_dir, "help"), null, null, cts.Token));
        }

        [Fact]
        public void Grep_ClipsALongMatchingLine()
        {
            string file = Path.Combine(_dir, "help", "Section0", "page000.html");
            FileReadResult r = _tools.Grep(Needle, file, null, null);
            Assert.True(r.Text.Length < 1_000, $"grep_file result is {r.Text.Length:N0} chars");
            Assert.Contains($"Configure {Needle} for the tenant.", r.Text);
            Assert.Contains("[line clipped", r.Text);
        }

        [Theory]
        [InlineData(0)]          // match at the very start: no leading "…"
        [InlineData(5_000)]      // match in the middle: "…" both ends
        [InlineData(9_990)]      // match at the very end: no trailing "…"
        public void ClipMatchLine_KeepsTheMatchInTheWindow(int at)
        {
            string line = new string('x', at) + "NEEDLE" + new string('y', 10_000 - at);
            string clipped = FileReadTools.ClipMatchLine(line, "zzz|needle", out bool wasClipped);
            Assert.True(wasClipped);
            Assert.Contains("NEEDLE", clipped);
            Assert.Equal(at > 0, clipped.StartsWith('…'));
            Assert.Equal(at < 9_990, clipped.Contains("… [line clipped", StringComparison.Ordinal));
            Assert.EndsWith($"[line clipped — {line.Length:N0} chars]", clipped);
        }

        [Fact]
        public void ClipMatchLine_LeavesShortLinesAlone()
        {
            Assert.Equal("  short line", FileReadTools.ClipMatchLine("  short line   ", "short", out bool clipped));
            Assert.False(clipped);
        }
    }
}
