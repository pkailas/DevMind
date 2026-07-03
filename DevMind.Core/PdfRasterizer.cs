// File: PdfRasterizer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Renders a single PDF page to a PNG for multimodal LLM input (/image <path> [page]).
// Vision encoders consume raster images, not documents — llama-server's multimodal
// parser only accepts image/audio/video parts — so PDF pages must be rasterized
// before they can be attached to a message.
//
// Rendering: Docnet.Core (MIT PDFium wrapper, native libs bundled per-RID).
// Encoding: a minimal built-in PNG writer (RGB8, single IDAT via ZLibStream) —
// no imaging package needed, keeps the dependency surface at exactly PDFium.

using System;
using System.IO;
using System.IO.Compression;
using Docnet.Core;
using Docnet.Core.Models;

namespace DevMind
{
    /// <summary>A rasterized PDF page plus the source document's page count.</summary>
    public sealed class PdfPageImage
    {
        /// <summary>PNG-encoded page image (RGB, white background).</summary>
        public byte[] PngBytes { get; set; }

        /// <summary>Rendered width in pixels.</summary>
        public int Width { get; set; }

        /// <summary>Rendered height in pixels.</summary>
        public int Height { get; set; }

        /// <summary>Total pages in the source PDF (for "page X/Y" display).</summary>
        public int PageCount { get; set; }
    }

    /// <summary>
    /// Rasterizes PDF pages to PNG via PDFium (Docnet.Core).
    /// </summary>
    public static class PdfRasterizer
    {
        /// <summary>
        /// Render viewport (smaller × larger side, orientation-aware) the page is scaled
        /// to fit. 1240×1754 is A4 at ~150 DPI — text stays legible to a vision encoder
        /// while keeping the image-token cost of a page reasonable (~2K tokens on a
        /// Qwen3-VL-class merger).
        /// </summary>
        public const int MaxRenderDimOne = 1240;

        /// <inheritdoc cref="MaxRenderDimOne"/>
        public const int MaxRenderDimTwo = 1754;

        /// <summary>
        /// Renders one page (1-based) of the PDF at <paramref name="pdfPath"/> to a PNG.
        /// Throws <see cref="InvalidOperationException"/> with a user-presentable message
        /// when the page number is out of range; PDFium errors (corrupt/encrypted files)
        /// surface as Docnet exceptions for the caller to wrap.
        /// </summary>
        public static PdfPageImage RenderPageToPng(string pdfPath, int pageNumber)
        {
            var pages = RenderPagesToPng(pdfPath, pageNumber, pageNumber);
            return pages[0];
        }

        /// <summary>
        /// Renders an inclusive 1-based page range to PNGs with a single document open.
        /// Throws <see cref="InvalidOperationException"/> (user-presentable) when the
        /// range falls outside the document.
        /// </summary>
        public static System.Collections.Generic.List<PdfPageImage> RenderPagesToPng(
            string pdfPath, int firstPage, int lastPage)
        {
            using (var docReader = DocLib.Instance.GetDocReader(
                pdfPath, new PageDimensions(MaxRenderDimOne, MaxRenderDimTwo)))
            {
                int pageCount = docReader.GetPageCount();
                if (firstPage < 1 || lastPage > pageCount || firstPage > lastPage)
                {
                    string what = firstPage == lastPage ? $"Page {firstPage} is" : $"Pages {firstPage}-{lastPage} are";
                    throw new InvalidOperationException(
                        $"{what} out of range — the PDF has {pageCount} page(s).");
                }

                var results = new System.Collections.Generic.List<PdfPageImage>(lastPage - firstPage + 1);
                for (int page = firstPage; page <= lastPage; page++)
                {
                    using (var pageReader = docReader.GetPageReader(page - 1))
                    {
                        int width = pageReader.GetPageWidth();
                        int height = pageReader.GetPageHeight();
                        byte[] bgra = pageReader.GetImage();

                        results.Add(new PdfPageImage
                        {
                            PngBytes = PngEncoder.EncodeRgbFromBgra(bgra, width, height),
                            Width = width,
                            Height = height,
                            PageCount = pageCount,
                        });
                    }
                }
                return results;
            }
        }

        /// <summary>Returns the page count of the PDF (opens and closes the document).</summary>
        public static int GetPageCount(string pdfPath)
        {
            using (var docReader = DocLib.Instance.GetDocReader(
                pdfPath, new PageDimensions(MaxRenderDimOne, MaxRenderDimTwo)))
            {
                return docReader.GetPageCount();
            }
        }

        /// <summary>
        /// Parses a /image page spec against a document's page count:
        /// null/blank → page 1; "all" → every page; "7" → that page; "2-5" → inclusive range.
        /// Throws <see cref="InvalidOperationException"/> with a user-presentable message
        /// for malformed specs or out-of-range pages.
        /// </summary>
        public static (int First, int Last) ParsePageSpec(string spec, int pageCount)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return (1, 1);

            if (spec.Equals("all", StringComparison.OrdinalIgnoreCase))
                return (1, pageCount);

            int dash = spec.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(spec, out int page) || page < 1)
                    throw new InvalidOperationException(
                        $"Invalid page '{spec}' — use a page number, a range like 2-5, or 'all'.");
                if (page > pageCount)
                    throw new InvalidOperationException(
                        $"Page {page} is out of range — the PDF has {pageCount} page(s).");
                return (page, page);
            }

            if (!int.TryParse(spec.Substring(0, dash), out int first)
                || !int.TryParse(spec.Substring(dash + 1), out int last)
                || first < 1 || last < 1)
                throw new InvalidOperationException(
                    $"Invalid page range '{spec}' — use a range like 2-5, a single page, or 'all'.");
            if (first > last)
                throw new InvalidOperationException(
                    $"Invalid page range '{spec}' — the first page must not exceed the last.");
            if (last > pageCount)
                throw new InvalidOperationException(
                    $"Pages {first}-{last} are out of range — the PDF has {pageCount} page(s).");
            return (first, last);
        }
    }

    /// <summary>
    /// Minimal PNG encoder: 8-bit RGB, filter 0 scanlines, one zlib IDAT chunk.
    /// PDFium renders BGRA with a transparent background where the page has no ink,
    /// so pixels are composited over white before encoding (a PDF page is paper).
    /// </summary>
    internal static class PngEncoder
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static byte[] EncodeRgbFromBgra(byte[] bgra, int width, int height)
        {
            if (bgra == null || width <= 0 || height <= 0 || bgra.Length < width * height * 4)
                throw new ArgumentException($"BGRA buffer too small for {width}x{height}.");

            // Filtered scanlines: each row is a 0 (None) filter byte + RGB triples.
            var raw = new byte[height * (1 + width * 3)];
            int p = 0;
            for (int y = 0; y < height; y++)
            {
                raw[p++] = 0; // filter: None
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x * 4;
                    byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], a = bgra[i + 3];
                    if (a != 255)
                    {
                        // Composite over white: c' = c·a/255 + 255·(1−a/255)
                        r = (byte)((r * a + 255 * (255 - a)) / 255);
                        g = (byte)((g * a + 255 * (255 - a)) / 255);
                        b = (byte)((b * a + 255 * (255 - a)) / 255);
                    }
                    raw[p++] = r;
                    raw[p++] = g;
                    raw[p++] = b;
                }
            }

            byte[] idat;
            using (var compressed = new MemoryStream())
            {
                // ZLibStream emits the full zlib format (header + deflate + adler32) PNG requires.
                using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                    z.Write(raw, 0, raw.Length);
                idat = compressed.ToArray();
            }

            var ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, (uint)width);
            WriteBigEndian(ihdr, 4, (uint)height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 2;  // color type: truecolor RGB
            ihdr[10] = 0; // compression
            ihdr[11] = 0; // filter method
            ihdr[12] = 0; // interlace: none

            using (var png = new MemoryStream())
            {
                png.Write(Signature, 0, Signature.Length);
                WriteChunk(png, "IHDR", ihdr);
                WriteChunk(png, "IDAT", idat);
                WriteChunk(png, "IEND", Array.Empty<byte>());
                return png.ToArray();
            }
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBigEndian(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);

            var typeBytes = new byte[4];
            for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);

            uint crc = 0xFFFFFFFF;
            crc = UpdateCrc(crc, typeBytes);
            crc = UpdateCrc(crc, data);
            var crcBytes = new byte[4];
            WriteBigEndian(crcBytes, 0, crc ^ 0xFFFFFFFF);
            s.Write(crcBytes, 0, 4);
        }

        private static void WriteBigEndian(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static uint UpdateCrc(uint crc, byte[] data)
        {
            foreach (byte b in data)
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
