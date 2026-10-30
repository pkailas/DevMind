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

        private CommandContext Context() => new CommandContext
        {
            WorkingDirectory = _dir,
            StagePendingImage = (uri, label) => _staged.Add((uri, label)),
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
