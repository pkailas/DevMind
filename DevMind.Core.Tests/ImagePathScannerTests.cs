// File: ImagePathScannerTests.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for typed-message image auto-attach detection: token/quote extraction,
// relative-path resolution, single quotes vs prose apostrophes, unquoted rooted
// paths with spaces, magic-byte validation (a renamed text file must never
// attach), dedupe, size cap, and data-URI construction. Test images are header
// bytes + filler — Scan only sniffs, it never decodes.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ImagePathScannerTests : IDisposable
    {
        private static readonly byte[] PngMagic =
            { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        private static readonly byte[] JpegMagic = { 0xFF, 0xD8, 0xFF, 0xE0 };

        private readonly string _dir;

        public ImagePathScannerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_scan_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private string WriteFile(string name, byte[] content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        private string WriteFileIn(string folder, string name, byte[] content)
        {
            Directory.CreateDirectory(Path.Combine(_dir, folder));
            return WriteFile(Path.Combine(folder, name), content);
        }

        // ── Detection ─────────────────────────────────────────────────────────

        [Fact]
        public void Scan_BareAbsolutePath_Detected()
        {
            string png = WriteFile("shot.png", PngMagic);
            var hits = ImagePathScanner.Scan($"why is {png} blank?", workingDirectory: null);

            var hit = Assert.Single(hits);
            Assert.Equal(png, hit.FullPath);
            Assert.Equal("image/png", hit.MimeType);
            Assert.Equal(PngMagic.Length, hit.SizeBytes);
        }

        [Fact]
        public void Scan_QuotedPathWithSpaces_Detected()
        {
            string png = WriteFile("Screen Shot 2026.png", PngMagic);
            var hits = ImagePathScanner.Scan($"look at \"{png}\" please", null);
            Assert.Equal(png, Assert.Single(hits).FullPath);
        }

        [Fact]
        public void Scan_TrailingProsePunctuation_Trimmed()
        {
            string png = WriteFile("err.png", PngMagic);
            var hits = ImagePathScanner.Scan($"what's wrong with {png}?", null);
            Assert.Equal(png, Assert.Single(hits).FullPath);
        }

        [Fact]
        public void Scan_RelativePath_ResolvesAgainstWorkingDirectory()
        {
            WriteFile("local.png", PngMagic);
            var hits = ImagePathScanner.Scan("check local.png here", workingDirectory: _dir);
            Assert.Equal(Path.Combine(_dir, "local.png"), Assert.Single(hits).FullPath);
        }

        [Fact]
        public void Scan_SamePathMentionedTwice_AttachesOnce()
        {
            string png = WriteFile("dup.png", PngMagic);
            var hits = ImagePathScanner.Scan($"compare {png} with {png}", null);
            Assert.Single(hits);
        }

        [Fact]
        public void Scan_MultipleDistinctImages_AllDetectedInOrder()
        {
            string a = WriteFile("a.png", PngMagic);
            string b = WriteFile("b.jpg", JpegMagic);
            var hits = ImagePathScanner.Scan($"diff {a} and \"{b}\"", null);

            Assert.Equal(2, hits.Count);
            Assert.Equal("image/png", hits[0].MimeType);
            Assert.Equal("image/jpeg", hits[1].MimeType);
        }

        // ── Single quotes ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Scan_SingleQuotedPathWithSpaces_Detected()
        {
            string png = WriteFileIn("Single Q", "shot.png", PngMagic);
            var hits = ImagePathScanner.Scan($"look at '{png}' please", null);
            Assert.Equal(png, Assert.Single(hits).FullPath);
        }

        [Fact]
        public void Scan_ProseApostrophes_DoNotBlockOtherPaths()
        {
            string png = WriteFile("prose.png", PngMagic);
            var hits = ImagePathScanner.Scan(
                $"'tis odd: don't worry, it's Paul's {png} that I can't open, isn't it'", null);
            Assert.Equal(png, Assert.Single(hits).FullPath);
        }

        [Fact]
        public void Scan_RejectedQuotedSegments_DoNotConsumeInnerPaths()
        {
            string a = WriteFile("inner-a.png", PngMagic);
            string b = WriteFile("inner-b.png", PngMagic);
            string missing = Path.Combine(_dir, "missing.png");
            var hits = ImagePathScanner.Scan($"'see {a} now' and \"{missing} or {b}\"", null);

            Assert.Equal(2, hits.Count);
            Assert.Equal(a, hits[0].FullPath);
            Assert.Equal(b, hits[1].FullPath);
        }

        // ── Unquoted rooted paths with spaces ─────────────────────────────────────────────

        [Fact]
        public void Scan_UnquotedRootedPathWithSpaces_MidSentence_DetectedOnce()
        {
            string png = WriteFileIn("My Shots", "a b.png", PngMagic);
            var hits = ImagePathScanner.Scan($"please check {png} and tell me what you see", null);
            Assert.Equal(png, Assert.Single(hits).FullPath);
        }

        [Fact]
        public void Scan_UnquotedPathWithDoubleSpace_DetectedWithExactSpacing()
        {
            string png = WriteFileIn("My  Shots", "x.png", PngMagic);
            var hits = ImagePathScanner.Scan($"what about {png} here", null);
            Assert.Equal(png, Assert.Single(hits).FullPath);
        }

        [Fact]
        public void Scan_UnquotedNonexistentPathWithSpaces_OtherPathsStillDetected()
        {
            string missing = Path.Combine(_dir, "No Such", "c d.png");
            string ok = WriteFile("ok.png", PngMagic);
            var hits = ImagePathScanner.Scan($"{missing} then {ok} too", null);
            Assert.Equal(ok, Assert.Single(hits).FullPath);
        }

        [Fact]
        public void Scan_ExtendedPath_LongestValidWins()
        {
            WriteFile("a.png", PngMagic);
            string longer = WriteFile("a.png b.png", PngMagic);
            var hits = ImagePathScanner.Scan($"open {Path.Combine(_dir, "a.png")} b.png now", null);
            Assert.Equal(longer, Assert.Single(hits).FullPath);
        }

        [Theory]
        [InlineData("", ".")]
        [InlineData("", ", and more")]
        [InlineData("(", ") ok")]
        [InlineData("", "?).")]
        public void Scan_ExtendedPath_SurroundingPunctuation_Trimmed(string prefix, string suffix)
        {
            string png = WriteFileIn("Punct Dir", "a b.png", PngMagic);
            var hits = ImagePathScanner.Scan($"is {prefix}{png}{suffix}", null);
            Assert.Equal(png, Assert.Single(hits).FullPath);
        }

        [Fact]
        public void Scan_DragDropForms_QuotedAndBare_BothDetected()
        {
            // Windows Terminal may paste a dropped path quoted or bare; both must work.
            string png = WriteFileIn("Drop Zone", "Screen Shot.png", PngMagic);
            Assert.Equal(png, Assert.Single(ImagePathScanner.Scan($"\"{png}\" ", null)).FullPath);
            Assert.Equal(png, Assert.Single(ImagePathScanner.Scan($"{png} ", null)).FullPath);
        }


        // ── Rejection ─────────────────────────────────────────────────────────

        [Fact]
        public void Scan_RenamedTextFile_RejectedByMagicBytes()
        {
            string fake = WriteFile("fake.png", Encoding.ASCII.GetBytes("hello, not an image"));
            Assert.Empty(ImagePathScanner.Scan($"see {fake}", null));
        }

        [Fact]
        public void Scan_NonexistentAndNonImageTokens_Ignored()
        {
            Assert.Empty(ImagePathScanner.Scan(
                $"delete {Path.Combine(_dir, "gone.png")} and read notes.txt and doc.pdf", _dir));
        }

        [Fact]
        public void Scan_EmptyAndOversizedFiles_Rejected()
        {
            WriteFile("empty.png", Array.Empty<byte>());
            var big = new byte[ImagePathScanner.MaxImageBytes + 1];
            PngMagic.CopyTo(big, 0);
            WriteFile("big.png", big);

            Assert.Empty(ImagePathScanner.Scan("empty.png and big.png", _dir));
        }

        [Fact]
        public void Scan_MimeComesFromMagicBytes_NotExtension()
        {
            // JPEG bytes in a .png-named file: still a real image, sniffed type wins.
            string lying = WriteFile("actually-jpeg.png", JpegMagic);
            Assert.Equal("image/jpeg", Assert.Single(ImagePathScanner.Scan(lying, null)).MimeType);
        }

        // ── SniffMime / BuildDataUri ──────────────────────────────────────────

        [Theory]
        [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
        [InlineData(new byte[] { 0xFF, 0xD8, 0xFF }, "image/jpeg")]
        [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "image/gif")]
        [InlineData(new byte[] { (byte)'B', (byte)'M', 0, 0 }, "image/bmp")]
        public void SniffMime_KnownSignatures(byte[] header, string expected)
        {
            Assert.Equal(expected, ImagePathScanner.SniffMime(header, header.Length));
        }

        [Fact]
        public void SniffMime_Webp_RequiresRiffAndWebpMarkers()
        {
            byte[] webp = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBP");
            Assert.Equal("image/webp", ImagePathScanner.SniffMime(webp, webp.Length));

            byte[] riffOnly = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVE");
            Assert.Null(ImagePathScanner.SniffMime(riffOnly, riffOnly.Length));
        }

        [Fact]
        public void BuildDataUri_EmbedsMimeAndBase64Content()
        {
            string png = WriteFile("uri.png", PngMagic);
            var img = Assert.Single(ImagePathScanner.Scan(png, null));

            string uri = ImagePathScanner.BuildDataUri(img);
            Assert.StartsWith("data:image/png;base64,", uri);
            Assert.Equal(PngMagic, Convert.FromBase64String(uri.Substring("data:image/png;base64,".Length)));
        }
    }
}
