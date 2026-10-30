// File: PdfPageAttachmentsTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// PdfPageAttachments — the PDF-page mechanics /image and view_image share:
//   * IsPdf sniffs %PDF- from the content: a PDF with any extension is one, a .pdf
//     that isn't, isn't.
//   * Render returns the range's PNG pages numbered first..last, or no pages and why:
//     out of range (PdfRasterizer's message), unreadable (corrupt), or a page over the cap.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class PdfPageAttachmentsTests
    {
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static string WriteTemp(string extension, byte[] content)
        {
            string path = Path.Combine(Path.GetTempPath(), $"devmind_test_{Guid.NewGuid():N}{extension}");
            File.WriteAllBytes(path, content);
            return path;
        }

        /// <summary>A valid PDF moved to <paramref name="extension"/> ("" for none).</summary>
        private static string WritePdfAs(string extension, int pages = 1)
        {
            string pdf = PdfTestFiles.WriteTempPdf(200, 300, pages);
            string moved = Path.ChangeExtension(pdf, null) + extension;
            File.Move(pdf, moved);
            return moved;
        }

        [Theory]
        [InlineData(".pdf")]
        [InlineData(".bin")]
        [InlineData("")]
        public void IsPdf_TrueForPdfContent_WhateverTheExtension(string extension)
        {
            string path = WritePdfAs(extension);
            try { Assert.True(PdfPageAttachments.IsPdf(path)); }
            finally { File.Delete(path); }
        }

        [Fact]
        public void IsPdf_FalseForPdfExtensionWithoutPdfContent()
        {
            string text = WriteTemp(".pdf", Encoding.ASCII.GetBytes("just some text, not a PDF"));
            string png = WriteTemp(".pdf", PngSignature);
            string shortFile = WriteTemp(".pdf", Encoding.ASCII.GetBytes("%PDF"));
            try
            {
                Assert.False(PdfPageAttachments.IsPdf(text));
                Assert.False(PdfPageAttachments.IsPdf(png));
                Assert.False(PdfPageAttachments.IsPdf(shortFile));
            }
            finally
            {
                File.Delete(text);
                File.Delete(png);
                File.Delete(shortFile);
            }
        }

        [Fact]
        public void Render_ReturnsPngPagesNumberedFirstToLast()
        {
            string path = PdfTestFiles.WriteTempPdf(200, 300, pages: 3);
            try
            {
                PdfRenderResult result = PdfPageAttachments.Render(path, 2, 3);

                Assert.True(result.Succeeded);
                Assert.Equal(PdfRenderFailure.None, result.Failure);
                Assert.Equal(new[] { 2, 3 }, result.Pages.Select(p => p.PageNumber));
                Assert.All(result.Pages, p =>
                {
                    Assert.Equal(3, p.PageCount);
                    Assert.Equal(PngSignature, p.PngBytes.Take(8));
                });
                Assert.Equal(result.Pages.Sum(p => (long)p.PngBytes.Length), result.TotalBytes);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Render_OutOfRange_ReturnsRasterizerMessageAndNoPages()
        {
            string path = PdfTestFiles.WriteTempPdf(200, 300, pages: 2);
            try
            {
                PdfRenderResult result = PdfPageAttachments.Render(path, 1, 5);

                Assert.Equal(PdfRenderFailure.OutOfRange, result.Failure);
                Assert.Equal("Pages 1-5 are out of range — the PDF has 2 page(s).", result.Message);
                Assert.Empty(result.Pages);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Render_CorruptPdf_IsUnreadableNotOutOfRange()
        {
            string path = WriteTemp(".pdf", Encoding.ASCII.GetBytes("%PDF-1.4\nthis is not a real PDF body\n"));
            try
            {
                PdfRenderResult result = PdfPageAttachments.Render(path, 1, 1);

                Assert.Equal(PdfRenderFailure.Unreadable, result.Failure);
                Assert.False(string.IsNullOrWhiteSpace(result.Message));
                Assert.Empty(result.Pages);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Render_PageOverCap_ReportsFirstOversizePageAndNoPages()
        {
            string path = PdfTestFiles.WriteTempPdf(200, 300, pages: 3);
            try
            {
                PdfRenderResult result = PdfPageAttachments.Render(path, 2, 3, maxPageBytes: 1);

                Assert.Equal(PdfRenderFailure.PageTooLarge, result.Failure);
                Assert.Equal(2, result.FailedPage);
                Assert.True(result.FailedPageBytes > 1);
                Assert.Empty(result.Pages);
                Assert.Equal(0, result.TotalBytes);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void BuildPngDataUri_IsBase64PngDataUri()
        {
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(PngSignature),
                PdfPageAttachments.BuildPngDataUri(PngSignature));
        }
    }
}
