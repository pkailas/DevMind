// File: PdfRasterizerTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for /image PDF support: PdfRasterizer (PDFium via Docnet.Core) and the
// built-in PNG encoder. The test PDF is generated at runtime with a correct
// xref table (offsets computed while assembling), so no binary fixture is
// checked in and PDFium parses it strictly rather than via reconstruction.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class PdfRasterizerTests
    {
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        [Fact]
        public void RenderPageToPng_MinimalPdf_ProducesValidPngWithCorrectAspect()
        {
            // Landscape page: 400×200 pt → rendered PNG must be wider than tall.
            string path = WriteTempPdf(widthPts: 400, heightPts: 200);
            try
            {
                var rendered = PdfRasterizer.RenderPageToPng(path, pageNumber: 1);

                Assert.Equal(1, rendered.PageCount);
                Assert.True(rendered.Width > rendered.Height,
                    $"landscape page rendered {rendered.Width}x{rendered.Height}");

                // Valid PNG: signature, IHDR dims matching the reported render size.
                Assert.True(rendered.PngBytes.Length > PngSignature.Length + 25);
                for (int i = 0; i < PngSignature.Length; i++)
                    Assert.Equal(PngSignature[i], rendered.PngBytes[i]);
                Assert.Equal(rendered.Width, ReadBigEndian(rendered.PngBytes, 16));
                Assert.Equal(rendered.Height, ReadBigEndian(rendered.PngBytes, 20));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void RenderPageToPng_PageOutOfRange_ThrowsWithPageCount()
        {
            string path = WriteTempPdf(widthPts: 200, heightPts: 400);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(
                    () => PdfRasterizer.RenderPageToPng(path, pageNumber: 5));
                Assert.Contains("Page 5", ex.Message);
                Assert.Contains("1 page", ex.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void RenderPagesToPng_Range_RendersEachPageOnce()
        {
            string path = WriteTempPdf(widthPts: 200, heightPts: 400, pages: 3);
            try
            {
                var pages = PdfRasterizer.RenderPagesToPng(path, 2, 3);

                Assert.Equal(2, pages.Count);
                Assert.All(pages, p =>
                {
                    Assert.Equal(3, p.PageCount);
                    Assert.True(p.PngBytes.Length > 0);
                    Assert.True(p.Height > p.Width); // portrait MediaBox
                });
                Assert.Equal(3, PdfRasterizer.GetPageCount(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(null, 7, 1, 1)]        // no spec → page 1
        [InlineData("all", 7, 1, 7)]       // all pages
        [InlineData("ALL", 7, 1, 7)]       // case-insensitive
        [InlineData("3", 7, 3, 3)]         // single page
        [InlineData("2-5", 7, 2, 5)]       // range
        [InlineData("7-7", 7, 7, 7)]       // single-page range
        public void ParsePageSpec_ValidSpecs_ResolveToExpectedRange(
            string spec, int pageCount, int expectedFirst, int expectedLast)
        {
            var (first, last) = PdfRasterizer.ParsePageSpec(spec, pageCount);
            Assert.Equal(expectedFirst, first);
            Assert.Equal(expectedLast, last);
        }

        [Theory]
        [InlineData("0", 7)]      // pages are 1-based
        [InlineData("8", 7)]      // beyond last page
        [InlineData("5-3", 7)]    // reversed range
        [InlineData("2-9", 7)]    // range past the end
        [InlineData("abc", 7)]    // not a spec
        [InlineData("1-", 7)]     // malformed range
        public void ParsePageSpec_InvalidSpecs_ThrowUserPresentableError(string spec, int pageCount)
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => PdfRasterizer.ParsePageSpec(spec, pageCount));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }

        [Theory]
        [InlineData(0, 5, 394, 1, 5)]      // fresh document → first chunk
        [InlineData(5, 5, 394, 6, 10)]     // resumes after last attached page
        [InlineData(390, 5, 394, 391, 394)] // final chunk clamps to the last page
        [InlineData(3, 20, 10, 4, 10)]     // chunk larger than remainder clamps
        public void NextChunk_ReturnsSequentialClampedRanges(
            int lastAttached, int chunkSize, int pageCount, int expectedFirst, int expectedLast)
        {
            var chunk = PdfRasterizer.NextChunk(lastAttached, chunkSize, pageCount);
            Assert.NotNull(chunk);
            Assert.Equal((expectedFirst, expectedLast), chunk!.Value);
        }

        [Theory]
        [InlineData(394, 5, 394)]  // exactly exhausted
        [InlineData(400, 5, 394)]  // cursor past the end
        public void NextChunk_ExhaustedDocument_ReturnsNull(int lastAttached, int chunkSize, int pageCount)
        {
            Assert.Null(PdfRasterizer.NextChunk(lastAttached, chunkSize, pageCount));
        }

        [Fact]
        public void NextChunk_InvalidChunkSize_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PdfRasterizer.NextChunk(0, 0, 10));
        }

        [Fact]
        public void PngEncoder_CompositesAlphaOverWhite_AndWritesIhdrDims()
        {
            // 2×1 BGRA: fully transparent pixel + opaque red pixel.
            byte[] bgra =
            {
                0, 0, 0, 0,        // transparent → must become white, not black
                0, 0, 255, 255,    // opaque red (BGRA)
            };

            byte[] png = PngEncoder.EncodeRgbFromBgra(bgra, width: 2, height: 1);

            for (int i = 0; i < PngSignature.Length; i++)
                Assert.Equal(PngSignature[i], png[i]);
            Assert.Equal(2, ReadBigEndian(png, 16));
            Assert.Equal(1, ReadBigEndian(png, 20));

            // Decode the single IDAT scanline back out and check the pixels:
            // filter 0, then RGB white (255,255,255) and RGB red (255,0,0).
            byte[] raw = InflateIdat(png);
            Assert.Equal(new byte[] { 0, 255, 255, 255, 255, 0, 0 }, raw);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static int ReadBigEndian(byte[] buf, int offset) =>
            (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];

        /// <summary>Extracts the IDAT payload (single chunk) and zlib-inflates it.</summary>
        private static byte[] InflateIdat(byte[] png)
        {
            int pos = 8; // skip signature
            while (pos < png.Length)
            {
                int len = ReadBigEndian(png, pos);
                string type = Encoding.ASCII.GetString(png, pos + 4, 4);
                if (type == "IDAT")
                {
                    using var input = new MemoryStream(png, pos + 8, len);
                    using var z = new System.IO.Compression.ZLibStream(
                        input, System.IO.Compression.CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    z.CopyTo(output);
                    return output.ToArray();
                }
                pos += 12 + len; // length + type + data + crc
            }
            throw new InvalidOperationException("no IDAT chunk found");
        }

        /// <summary>
        /// Writes a minimal PDF (1..N identical pages) with a correct xref table. Object
        /// byte offsets are computed while assembling so the file is strictly valid.
        /// </summary>
        private static string WriteTempPdf(int widthPts, int heightPts, int pages = 1)
        {
            var kids = new StringBuilder();
            for (int i = 0; i < pages; i++)
                kids.Append($"{3 + i} 0 R ");

            var objects = new List<string>
            {
                "1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n",
                $"2 0 obj\n<</Type/Pages/Kids[{kids.ToString().TrimEnd()}]/Count {pages}>>\nendobj\n",
            };
            for (int i = 0; i < pages; i++)
                objects.Add($"{3 + i} 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 {widthPts} {heightPts}]>>\nendobj\n");

            var sb = new StringBuilder();
            sb.Append("%PDF-1.4\n");
            var offsets = new long[objects.Count];
            for (int i = 0; i < objects.Count; i++)
            {
                offsets[i] = Encoding.ASCII.GetByteCount(sb.ToString());
                sb.Append(objects[i]);
            }

            long xrefOffset = Encoding.ASCII.GetByteCount(sb.ToString());
            sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (long off in offsets)
                sb.Append($"{off:D10} 00000 n \n");
            sb.Append($"trailer\n<</Size {objects.Count + 1}/Root 1 0 R>>\nstartxref\n{xrefOffset}\n%%EOF");

            string path = Path.Combine(Path.GetTempPath(), $"devmind_test_{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
            return path;
        }
    }
}
