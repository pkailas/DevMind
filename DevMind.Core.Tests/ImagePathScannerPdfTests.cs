// File: ImagePathScannerPdfTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// ImagePathScanner.ScanAttachments: the same three passes as Scan, also returning PDFs
// (.pdf extension AND %PDF- magic bytes). Scan itself still never returns a PDF. The PDF
// files here are just the magic bytes — detection never opens the document.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ImagePathScannerPdfTests : IDisposable
    {
        private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF-1.4\n%fake body\n");
        private static readonly byte[] PngMagic =
            { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };

        private readonly string _dir;

        public ImagePathScannerPdfTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_scanpdf_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private string WriteFile(string name, byte[] content)
        {
            string path = Path.Combine(_dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        [Fact]
        public void ScanAttachments_DoubleQuotedPdfWithSpaces_Detected()
        {
            string pdf = WriteFile("Annual Report 2026.pdf", PdfMagic);
            var found = ImagePathScanner.ScanAttachments($"summarize \"{pdf}\" please", null);

            var hit = Assert.Single(found.Pdfs);
            Assert.Equal(pdf, hit.FullPath);
            Assert.Equal(PdfMagic.Length, hit.SizeBytes);
            Assert.Empty(found.Images);
        }

        [Fact]
        public void ScanAttachments_SingleQuotedPdf_Detected()
        {
            string pdf = WriteFile("q report.pdf", PdfMagic);
            var found = ImagePathScanner.ScanAttachments($"read '{pdf}' and tell me", null);
            Assert.Equal(pdf, Assert.Single(found.Pdfs).FullPath);
        }

        [Fact]
        public void ScanAttachments_BarePdf_Detected()
        {
            string pdf = WriteFile("report.pdf", PdfMagic);
            var found = ImagePathScanner.ScanAttachments($"what does {pdf} say?", null);
            Assert.Equal(pdf, Assert.Single(found.Pdfs).FullPath);
        }

        [Fact]
        public void ScanAttachments_BareRootedPdfWithSpaces_Detected()
        {
            string pdf = WriteFile(Path.Combine("My Docs", "big  manual.pdf"), PdfMagic);
            var found = ImagePathScanner.ScanAttachments($"open {pdf} now", null);
            Assert.Equal(pdf, Assert.Single(found.Pdfs).FullPath);
        }

        [Fact]
        public void ScanAttachments_RelativePdf_ResolvesAgainstWorkingDirectory()
        {
            WriteFile("local.pdf", PdfMagic);
            var found = ImagePathScanner.ScanAttachments("check local.pdf here", _dir);
            Assert.Equal(Path.Combine(_dir, "local.pdf"), Assert.Single(found.Pdfs).FullPath);
        }

        [Fact]
        public void ScanAttachments_PdfExtensionButNotAPdf_NotDetected()
        {
            string fake = WriteFile("fake.pdf", Encoding.ASCII.GetBytes("just some text"));
            var found = ImagePathScanner.ScanAttachments($"see {fake} and \"{fake}\"", null);
            Assert.Empty(found.Pdfs);
            Assert.Empty(found.Images);
        }

        [Fact]
        public void ScanAttachments_PdfContentWithoutPdfExtension_NotDetected()
        {
            string doc = WriteFile("scan.bin", PdfMagic);
            var found = ImagePathScanner.ScanAttachments($"see {doc} and \"{doc}\"", null);
            Assert.Empty(found.Pdfs);
        }

        [Fact]
        public void ScanAttachments_EmptyPdf_NotDetected()
        {
            string empty = WriteFile("empty.pdf", Array.Empty<byte>());
            Assert.Empty(ImagePathScanner.ScanAttachments($"see {empty}", null).Pdfs);
        }

        [Fact]
        public void ScanAttachments_ImageAndPdf_EachReturnedOnce_InTheirOwnList()
        {
            string png = WriteFile("shot one.png", PngMagic);
            string pdf = WriteFile("spec.pdf", PdfMagic);
            var found = ImagePathScanner.ScanAttachments(
                $"compare \"{png}\" with {pdf}; again {pdf} and '{png}'", null);

            Assert.Equal(png, Assert.Single(found.Images).FullPath);
            Assert.Equal(pdf, Assert.Single(found.Pdfs).FullPath);
        }

        [Fact]
        public void ScanAttachments_TwoPdfs_InAppearanceOrder()
        {
            string b = WriteFile("b.pdf", PdfMagic);
            string a = WriteFile("a.pdf", PdfMagic);
            var found = ImagePathScanner.ScanAttachments($"first {b} then \"{a}\"", null);
            Assert.Equal(new[] { b, a }, found.Pdfs.Select(p => p.FullPath));
        }

        [Fact]
        public void Scan_StillIgnoresPdfs()
        {
            string png = WriteFile("x.png", PngMagic);
            string pdf = WriteFile("x.pdf", PdfMagic);
            var hits = ImagePathScanner.Scan($"{pdf} and {png}", null);
            Assert.Equal(png, Assert.Single(hits).FullPath);
        }
    }
}
