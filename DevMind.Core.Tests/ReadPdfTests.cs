// File: ReadPdfTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// read_pdf — the model-callable PDF text reader, on runtime-generated PDFs with real text
// (PdfTestFiles.WriteTempTextPdf; a null page has no content stream, like a scan).
//   * Extraction: PdfPageAttachments.ExtractText returns each page's text, LF-only, with
//     HasText false for a page without a text layer.
//   * Executor: no pages -> page count + text/scanned page lists; pages -> "--- page N ---"
//     blocks; a scanned page -> the view_image hint; over the cap -> stops at a page boundary
//     (or cuts a single huge page) with a continuation hint; errors are [READ_PDF ERROR].
//   * Mapping, and the ingest-cap exemption that keeps a read_pdf result verbatim.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class ReadPdfTests
    {
        private const string Page1 = "Invoice 4711\nVendor: Acme Widgets";
        private const string Page2 = "Line items follow on this page\nWidget A 10 x 3.50";
        private const string Page4 = "Totals and payment terms\nNet 30 days";

        /// <summary>Four pages: text, text, no text layer (scan), text.</summary>
        private static string WriteSample(string dir, string name = "doc.pdf")
            => MoveInto(dir, name, PdfTestFiles.WriteTempTextPdf(Page1, Page2, null, Page4));

        private static string MoveInto(string dir, string name, string temp)
        {
            string path = Path.Combine(dir, name);
            File.Move(temp, path);
            return path;
        }

        private static ResponseBlock ReadBlock(string path, string? pages, string id)
            => new ResponseBlock { Type = BlockType.ReadPdf, FileName = path, PageSpec = pages, ToolCallId = id, FromToolCall = true };

        private static async Task<string> ReadAsync(Env env, string path, string? pages)
        {
            var result = await new AgenticExecutor(env.Host, new FakeLlmOptions())
                .ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild },
                    new ResponseOutcome(new List<ResponseBlock> { ReadBlock(path, pages, "call_1") }));
            Assert.Empty(result.StagedImages);   // read_pdf never attaches anything
            return result.ToolResultsByCallId["call_1"];
        }

        // ── Extraction ──────────────────────────────────────────────────────────

        [Fact]
        public void ExtractText_ReturnsEachPagesText_LfOnly_AndFlagsThePageWithoutText()
        {
            string pdf = PdfTestFiles.WriteTempTextPdf(Page1, Page2, null, Page4);
            try
            {
                PdfTextResult result = PdfPageAttachments.ExtractText(pdf, 1, 4);

                Assert.True(result.Succeeded, result.Message);
                Assert.Equal(new[] { 1, 2, 3, 4 }, result.Pages.Select(p => p.PageNumber));
                Assert.Equal(Page1, result.Pages[0].Text);
                Assert.Equal(Page2, result.Pages[1].Text);
                Assert.Equal("", result.Pages[2].Text);
                Assert.Equal(Page4, result.Pages[3].Text);
                Assert.Equal(new[] { true, true, false, true }, result.Pages.Select(p => p.HasText));
                Assert.Equal(Page1.Length, result.Pages[0].CharCount);
                Assert.DoesNotContain(result.Pages, p => p.Text.Contains('\r'));
            }
            finally
            {
                File.Delete(pdf);
            }
        }

        [Fact]
        public void ExtractText_OutOfRange_AndCorrupt_AreFailureKinds()
        {
            string pdf = PdfTestFiles.WriteTempTextPdf(Page1);
            string bad = Path.Combine(Path.GetTempPath(), $"devmind_test_{Guid.NewGuid():N}.pdf");
            File.WriteAllText(bad, "%PDF-1.4\nthis is not really a pdf");
            try
            {
                var outOfRange = PdfPageAttachments.ExtractText(pdf, 1, 2);
                Assert.Equal(PdfRenderFailure.OutOfRange, outOfRange.Failure);
                Assert.Contains("out of range", outOfRange.Message, StringComparison.Ordinal);
                Assert.Empty(outOfRange.Pages);

                Assert.Equal(PdfRenderFailure.Unreadable, PdfPageAttachments.ExtractText(bad, 1, 1).Failure);
            }
            finally
            {
                File.Delete(pdf);
                File.Delete(bad);
            }
        }

        [Fact]
        public void ShortStamp_IsNotATextLayer()
        {
            // "Page 3 of 10" is what a scanner stamps on an image page: under the threshold.
            string pdf = PdfTestFiles.WriteTempTextPdf("Page 3 of 10");
            try
            {
                var page = Assert.Single(PdfPageAttachments.ExtractText(pdf, 1, 1).Pages);
                Assert.Equal("Page 3 of 10", page.Text);
                Assert.False(page.HasText);
            }
            finally
            {
                File.Delete(pdf);
            }
        }

        // ── Executor ────────────────────────────────────────────────────────────

        [Fact]
        public async Task NoPages_ReportsTheCount_AndWhichPagesHaveText()
        {
            using var env = new Env();
            string pdf = WriteSample(env.Host.GetWorkingDirectory());
            int total = Page1.Length + Page2.Length + Page4.Length;

            string text = await ReadAsync(env, pdf, null);

            Assert.Equal(
                $"PDF {pdf}: 4 page(s). Text layer: pages with text: 1-2, 4; " +
                "pages without text (scanned — use view_image with pages): 3. " +
                $"Total text: {total} chars. Call read_pdf with pages (e.g. \"1-3\") to read the text; " +
                "each call returns up to 40,000 chars.",
                text);
        }

        [Fact]
        public async Task Pages2to3_ReturnsTwoPageBlocks_AndTheScannedPageHint()
        {
            using var env = new Env();
            string pdf = WriteSample(env.Host.GetWorkingDirectory());

            string text = await ReadAsync(env, pdf, "2-3");

            Assert.Equal(
                $"PDF {pdf} — pages 2-3 of 4:\n" +
                "--- page 2 ---\n" + Page2 + "\n" +
                "--- page 3 ---\n" + "[no text layer — call view_image with pages=\"3\"]",
                text);
        }

        [Fact]
        public async Task SinglePage_ReturnsItsBlock()
        {
            using var env = new Env();
            string pdf = WriteSample(env.Host.GetWorkingDirectory());

            string text = await ReadAsync(env, pdf, "4");

            Assert.Equal($"PDF {pdf} — page 4 of 4:\n--- page 4 ---\n" + Page4, text);
        }

        [Fact]
        public async Task RelativePath_ResolvesAgainstTheWorkingDirectory_LikeReadFile()
        {
            using var env = new Env();
            string pdf = WriteSample(env.Host.GetWorkingDirectory(), "rel.pdf");

            string text = await ReadAsync(env, "rel.pdf", "1");

            Assert.StartsWith($"PDF {pdf} — page 1 of 4:\n--- page 1 ---\n" + Page1, text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task OverTheRealCap_StopsAtAPageBoundary_WithTheNextRange()
        {
            // Three ~20,000-char pages: page 1 fits, page 2 would pass 40,000.
            string line = new string('x', 79);
            string bigPage = string.Join("\n", Enumerable.Repeat(line, 250));
            using var env = new Env();
            string pdf = MoveInto(env.Host.GetWorkingDirectory(), "big.pdf",
                PdfTestFiles.WriteTempTextPdf(bigPage, bigPage, bigPage));

            string text = await ReadAsync(env, pdf, "all");

            Assert.EndsWith("\n[truncated — call read_pdf again with pages=\"2-3\" for the rest]", text, StringComparison.Ordinal);
            Assert.Contains("--- page 1 ---\n" + bigPage + "\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain("--- page 2 ---", text, StringComparison.Ordinal);
            Assert.True(text.Length <= ReadPdfTool.MaxOutputChars + 200, $"result is {text.Length} chars");
        }

        [Fact]
        public void OverASmallCap_StopsBeforeThePageThatDoesNotFit()
        {
            string pdf = PdfTestFiles.WriteTempTextPdf(Page1, Page2, null, Page4);
            try
            {
                // The cap fits the header and page 1's block, not page 2's.
                string kept = $"PDF {pdf} — pages 1-4 of 4:\n--- page 1 ---\n" + Page1 + "\n";
                var (text, log) = ReadPdfTool.Read(pdf, "1-4", maxChars: kept.Length + 10);

                Assert.Equal(kept + "[truncated — call read_pdf again with pages=\"2-4\" for the rest]", text);
                Assert.EndsWith("(truncated)", log, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(pdf);
            }
        }

        [Fact]
        public void OneHugePage_IsCut_AndTheHintPointsAtViewImageAndTheNextPages()
        {
            string big = string.Join("\n", Enumerable.Repeat("The quick brown fox jumps over the lazy dog", 40));
            string pdf = PdfTestFiles.WriteTempTextPdf(big, Page2);
            try
            {
                var (text, _) = ReadPdfTool.Read(pdf, "1-2", maxChars: 300);

                string header = $"PDF {pdf} — pages 1-2 of 2:\n--- page 1 ---\n";
                Assert.StartsWith(header + big.Substring(0, 50), text, StringComparison.Ordinal);
                Assert.EndsWith(
                    "[truncated — page 1 is cut at 300 chars (view_image with pages=\"1\" shows the whole page); " +
                    "call read_pdf again with pages=\"2\" for the rest]",
                    text, StringComparison.Ordinal);
                Assert.DoesNotContain("--- page 2 ---", text, StringComparison.Ordinal);
                Assert.True(text.IndexOf("\n[truncated", StringComparison.Ordinal) <= 300);
            }
            finally
            {
                File.Delete(pdf);
            }
        }

        [Fact]
        public async Task FileThatIsNotAPdf_IsAnError()
        {
            using var env = new Env();
            string fake = Path.Combine(env.Host.GetWorkingDirectory(), "fake.pdf");
            File.WriteAllText(fake, "plain text with a pdf extension");

            string text = await ReadAsync(env, fake, null);

            Assert.StartsWith("[READ_PDF ERROR]", text, StringComparison.Ordinal);
            Assert.Contains("not a PDF", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task MissingFile_IsNotFound()
        {
            using var env = new Env();

            string text = await ReadAsync(env, Path.Combine(env.Host.GetWorkingDirectory(), "nope.pdf"), "1");

            Assert.StartsWith("[READ_PDF ERROR]", text, StringComparison.Ordinal);
            Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("5", "out of range")]
        [InlineData("2-9", "out of range")]
        [InlineData("abc", "Invalid page 'abc'")]
        [InlineData("3-2", "Invalid page range")]
        public async Task BadOrOutOfRangePages_AreAnError(string spec, string expected)
        {
            using var env = new Env();
            string pdf = WriteSample(env.Host.GetWorkingDirectory());

            string text = await ReadAsync(env, pdf, spec);

            Assert.StartsWith($"[READ_PDF ERROR] {pdf}: ", text, StringComparison.Ordinal);
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CorruptPdf_IsAnError()
        {
            using var env = new Env();
            string bad = Path.Combine(env.Host.GetWorkingDirectory(), "bad.pdf");
            File.WriteAllText(bad, "%PDF-1.4\nthis is not really a pdf");

            string text = await ReadAsync(env, bad, null);

            Assert.StartsWith("[READ_PDF ERROR]", text, StringComparison.Ordinal);
            Assert.Contains("could not be read", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Ranges_CompressConsecutivePages()
        {
            Assert.Equal("none", ReadPdfTool.Ranges(Array.Empty<int>()));
            Assert.Equal("1-3, 5, 7-8", ReadPdfTool.Ranges(new[] { 1, 2, 3, 5, 7, 8 }));
        }

        // ── Mapping and result handling ─────────────────────────────────────────

        [Fact]
        public void ReadPdfCall_MapsToAReadPdfBlockWithPathAndPages()
        {
            var tc = new ToolCallResult { Name = "read_pdf", Id = "call_r" };
            tc.Arguments["path"] = @"C:\docs\a.pdf";
            tc.Arguments["pages"] = "2-3";

            var block = Assert.Single(ToolCallMapper.Map(new List<ToolCallResult> { tc }, buildCommand: "dotnet build"));

            Assert.Equal(BlockType.ReadPdf, block.Type);
            Assert.Equal(@"C:\docs\a.pdf", block.FileName);
            Assert.Equal("2-3", block.PageSpec);
            Assert.Equal("call_r", block.ToolCallId);
        }

        [Fact]
        public void ReadPdfCall_WithoutAPath_IsAnArgumentError()
        {
            var tc = new ToolCallResult { Name = "read_pdf", Id = "call_r" };
            tc.Arguments["filename"] = @"C:\docs\a.pdf";

            ToolCallMapper.Map(new List<ToolCallResult> { tc }, buildCommand: "dotnet build");

            Assert.NotNull(tc.ArgumentError);
            Assert.Contains("'path'", tc.ArgumentError, StringComparison.Ordinal);
        }

        [Fact]
        public void ReadPdfResult_IsExemptFromIngestCapping()
        {
            var client = new LlmClient(new FakeLlmOptions());
            string big = new string('p', 20_000); // over the 8,000 default, but read_pdf pages itself

            client.AddToolResultMessage("call_r", big, toolName: "read_pdf");

            Assert.Equal(big, client.LastToolResultContent);
            Assert.Equal(0, client.NearlineCache.Count);
        }
    }
}
