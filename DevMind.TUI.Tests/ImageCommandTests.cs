// File: ImageCommandTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// /image decides a file's type from its content, never its extension: %PDF- is a PDF
// (pages rasterized and staged as PNGs), anything else must sniff as an image
// (ImagePathScanner.TryValidate) and is sent with the sniffed MIME type.

using System.Text;
using Xunit;

namespace DevMind.TUI.Tests
{
    public sealed class ImageCommandTests : IDisposable
    {
        // A PNG signature is all the sniffer reads; the rest only has to exist.
        private static readonly byte[] PngBytes =
            { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };

        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"devmind_imagecmd_{Guid.NewGuid():N}");
        private readonly List<(string DataUri, string Label)> _staged = new();

        public ImageCommandTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private CommandContext Context(Func<int, int?>? freeTokens = null) => new CommandContext
        {
            WorkingDirectory = _dir,
            StagePendingImage = (uri, label) => _staged.Add((uri, label)),
            FreeContextTokens = freeTokens,
        };

        private string Write(string name, byte[] content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        [Fact]
        public async Task Image_PngNamedJpg_AttachesAsPng()
        {
            Write("shot.jpg", PngBytes);

            var result = await SlashCommand.Dispatch("/image shot.jpg", Context());

            Assert.False(result.IsError, result.Message);
            Assert.Contains("(image/png,", result.Message);
            var staged = Assert.Single(_staged);
            Assert.StartsWith("data:image/png;base64,", staged.DataUri);
            Assert.Equal("shot.jpg", staged.Label);
        }

        [Fact]
        public async Task Image_TextFileNamedPng_IsRejectedWithTheSniffersReason()
        {
            Write("notes.png", Encoding.ASCII.GetBytes("plain text pretending to be a PNG"));

            var result = await SlashCommand.Dispatch("/image notes.png", Context());

            Assert.True(result.IsError);
            Assert.Equal("Cannot attach notes.png: not an image — its contents are not PNG, JPEG, GIF, WebP or BMP.",
                result.Message);
            Assert.Empty(_staged);
        }

        [Fact]
        public async Task Image_TextFileNamedPdf_IsNotTreatedAsPdf()
        {
            Write("fake.pdf", Encoding.ASCII.GetBytes("not a PDF at all"));

            var result = await SlashCommand.Dispatch("/image fake.pdf", Context());

            Assert.True(result.IsError);
            Assert.StartsWith("Cannot attach fake.pdf: not an image", result.Message);
            Assert.Empty(_staged);
        }

        [Theory]
        [InlineData("scan.bin")]
        [InlineData("scan")]
        public async Task Image_PdfWithoutPdfExtension_IsRasterizedAsPdf(string name)
        {
            Write(name, MinimalPdf(pages: 2));

            var result = await SlashCommand.Dispatch($"/image {name} 2", Context());

            Assert.False(result.IsError, result.Message);
            Assert.StartsWith($"PDF page 2 of 2 rasterized and staged: {name} (1 image(s), ", result.Message);
            var staged = Assert.Single(_staged);
            Assert.StartsWith("data:image/png;base64,", staged.DataUri);
            Assert.Equal($"{name} p2", staged.Label);
        }

        [Fact]
        public async Task Image_PageSpecOnAnImage_IsRefused()
        {
            Write("shot.bin", PngBytes);

            var result = await SlashCommand.Dispatch("/image shot.bin 2", Context());

            Assert.True(result.IsError);
            Assert.Equal("A page spec is only supported for PDF files.", result.Message);
            Assert.Empty(_staged);
        }

        // ── PDF fit check (replaces the old fixed 20-page cap) ─────────────────────

        [Fact]
        public async Task Image_PdfRangeOverFreeContext_IsRefused_WithTheRangeThatFits()
        {
            Write("doc.pdf", MinimalPdf(pages: 5));

            var result = await SlashCommand.Dispatch("/image doc.pdf 1-3", Context(_ => 2 * 1024 + 500));

            Assert.True(result.IsError);
            Assert.Equal("That's 3 page(s) (~3,072 tokens); only ~2,548 free in the context. Attach at most 2 " +
                         "(e.g. /image \"doc.pdf\" 1-2, or chunk through with p=2). The PDF has 5 page(s).", result.Message);
            Assert.Empty(_staged);
        }

        [Fact]
        public async Task Image_PdfRangeOverTheOld20PageCap_AttachesWhenItFits()
        {
            Write("long.pdf", MinimalPdf(pages: 21));

            var result = await SlashCommand.Dispatch("/image long.pdf all", Context(_ => 100_000));

            Assert.False(result.IsError, result.Message);
            Assert.Equal(21, _staged.Count);
        }

        [Fact]
        public async Task Image_PdfChunkOverFreeContext_IsRefused_AndTheCursorStays()
        {
            Write("chunk.pdf", MinimalPdf(pages: 4));

            var refused = await SlashCommand.Dispatch("/image chunk.pdf p=3", Context(_ => 1024));
            Assert.True(refused.IsError);
            Assert.Contains("Attach at most 1", refused.Message);
            Assert.Empty(_staged);

            var ok = await SlashCommand.Dispatch("/image chunk.pdf p=1", Context(_ => 1024));
            Assert.False(ok.IsError, ok.Message);
            Assert.Equal("chunk.pdf p1", Assert.Single(_staged).Label); // started at page 1
        }

        [Fact]
        public async Task Image_PdfWithNoRoomLeft_SaysSo()
        {
            Write("full.pdf", MinimalPdf(pages: 2));

            var result = await SlashCommand.Dispatch("/image full.pdf 1", Context(_ => 300));

            Assert.True(result.IsError);
            Assert.Contains("no room for another page", result.Message);
            Assert.Empty(_staged);
        }

        [Fact]
        public async Task Image_PdfWithUnknownFreeContext_SkipsTheCheck()
        {
            Write("any.pdf", MinimalPdf(pages: 3));

            var result = await SlashCommand.Dispatch("/image any.pdf 1-3", Context(_ => null));

            Assert.False(result.IsError, result.Message);
            Assert.Equal(3, _staged.Count);
        }

        /// <summary>A minimal valid PDF (blank pages) with a correct xref table.</summary>
        private static byte[] MinimalPdf(int pages)
        {
            var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + i} 0 R"));
            var objects = new List<string>
            {
                "1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n",
                $"2 0 obj\n<</Type/Pages/Kids[{kids}]/Count {pages}>>\nendobj\n",
            };
            for (int i = 0; i < pages; i++)
                objects.Add($"{3 + i} 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 300]>>\nendobj\n");

            var sb = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            foreach (string obj in objects)
            {
                offsets.Add(sb.Length);
                sb.Append(obj);
            }
            int xref = sb.Length;
            sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (int off in offsets)
                sb.Append($"{off:D10} 00000 n \n");
            sb.Append($"trailer\n<</Size {objects.Count + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }
    }
}
