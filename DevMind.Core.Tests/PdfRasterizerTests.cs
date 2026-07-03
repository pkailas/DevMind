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
        /// Writes a minimal one-page PDF with a correct xref table. Object byte
        /// offsets are computed while assembling so the file is strictly valid.
        /// </summary>
        private static string WriteTempPdf(int widthPts, int heightPts)
        {
            string[] objects =
            {
                "1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n",
                "2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n",
                $"3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 {widthPts} {heightPts}]>>\nendobj\n",
            };

            var sb = new StringBuilder();
            sb.Append("%PDF-1.4\n");
            var offsets = new long[objects.Length];
            for (int i = 0; i < objects.Length; i++)
            {
                offsets[i] = Encoding.ASCII.GetByteCount(sb.ToString());
                sb.Append(objects[i]);
            }

            long xrefOffset = Encoding.ASCII.GetByteCount(sb.ToString());
            sb.Append("xref\n0 4\n0000000000 65535 f \n");
            foreach (long off in offsets)
                sb.Append($"{off:D10} 00000 n \n");
            sb.Append($"trailer\n<</Size 4/Root 1 0 R>>\nstartxref\n{xrefOffset}\n%%EOF");

            string path = Path.Combine(Path.GetTempPath(), $"devmind_test_{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
            return path;
        }
    }
}
