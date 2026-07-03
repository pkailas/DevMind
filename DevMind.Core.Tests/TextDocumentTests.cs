// File: TextDocumentTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for text-native library ingestion (.md/.txt/.docx):
//   * TextDocumentReader — extension routing, docx XML extraction (runtime-generated
//     minimal OOXML zip, no binary fixtures), paragraph-boundary chunking.
//   * DocumentLibrarian text path — E2E ingest of a markdown file against fake
//     embeddings + real SQL (soft-skipped off-rig): zero chat-model calls, verbatim
//     chunk storage, section provenance labels in the augmented prompt.

using System.IO.Compression;
using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class TextDocumentTests
    {
        private const string ConnectionString =
            "Server=WIN-SQL002,14330;Database=DevMindRAG;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5";

        // ── Extension routing ─────────────────────────────────────────────────

        [Theory]
        [InlineData("notes.md", true)]
        [InlineData("NOTES.MD", true)]
        [InlineData("readme.markdown", true)]
        [InlineData("log.txt", true)]
        [InlineData("spec.docx", true)]
        [InlineData("scan.pdf", false)]
        [InlineData("image.png", false)]
        public void IsTextDocument_RoutesByExtension(string name, bool expected)
        {
            Assert.Equal(expected, TextDocumentReader.IsTextDocument(name));
        }

        // ── Chunking ──────────────────────────────────────────────────────────

        [Fact]
        public void ChunkText_EmptyOrWhitespace_YieldsNoChunks()
        {
            Assert.Empty(TextDocumentReader.ChunkText(""));
            Assert.Empty(TextDocumentReader.ChunkText("   \n\n  \t "));
        }

        [Fact]
        public void ChunkText_SmallText_SingleChunkVerbatimTrimmed()
        {
            var chunks = TextDocumentReader.ChunkText("# Title\n\nBody paragraph.\n");
            Assert.Single(chunks);
            Assert.Equal("# Title\n\nBody paragraph.", chunks[0]);
        }

        [Fact]
        public void ChunkText_BreaksAtParagraphBoundaries_NothingLost()
        {
            // Three ~40-char paragraphs with a 100-char budget → paragraphs 1+2
            // fit together, paragraph 3 starts a new chunk.
            string p1 = new string('a', 40);
            string p2 = new string('b', 40);
            string p3 = new string('c', 40);
            var chunks = TextDocumentReader.ChunkText($"{p1}\n\n{p2}\n\n{p3}", maxChars: 100);

            Assert.Equal(2, chunks.Count);
            Assert.Equal($"{p1}\n\n{p2}", chunks[0]);
            Assert.Equal(p3, chunks[1]);
        }

        [Fact]
        public void ChunkText_OversizedSingleLine_HardSplitsWithoutLoss()
        {
            string monster = new string('x', 250);
            var chunks = TextDocumentReader.ChunkText(monster, maxChars: 100);

            Assert.Equal(3, chunks.Count);
            Assert.All(chunks, c => Assert.True(c.Length <= 100));
            Assert.Equal(monster, string.Concat(chunks));
        }

        [Fact]
        public void ChunkText_NormalizesCrLfParagraphBreaks()
        {
            var chunks = TextDocumentReader.ChunkText("first\r\n\r\nsecond", maxChars: 10);
            Assert.Equal(2, chunks.Count);
            Assert.Equal("first", chunks[0]);
            Assert.Equal("second", chunks[1]);
        }

        // ── .docx extraction ──────────────────────────────────────────────────

        [Fact]
        public void ExtractText_Docx_ParagraphsTabsBreaksAndEmptyRuns()
        {
            string docx = WriteTempDocx(
                "<w:p><w:r><w:t>Hello</w:t></w:r><w:r><w:tab/><w:t>World</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t>Line one</w:t><w:br/><w:t>line two</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t/></w:r></w:p>" +                       // self-closing run must not latch
                "<w:p><w:r><w:t xml:space=\"preserve\"> spaced </w:t></w:r></w:p>");
            try
            {
                string text = TextDocumentReader.ExtractText(docx);
                Assert.Contains("Hello\tWorld", text);
                Assert.Contains("Line one\nline two", text);
                Assert.Contains(" spaced ", text);
                // Paragraphs are separated by blank lines.
                Assert.Contains("Hello\tWorld\n\n", text);
            }
            finally
            {
                File.Delete(docx);
            }
        }

        [Fact]
        public void ExtractText_DocxMissingDocumentXml_Throws()
        {
            string path = Path.Combine(Path.GetTempPath(), $"devmind_test_{Guid.NewGuid():N}.docx");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("unrelated.txt").Open()))
                writer.Write("nope");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => TextDocumentReader.ExtractText(path));
                Assert.Contains("word/document.xml", ex.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ── Librarian text path E2E: fake embeddings + real SQL ──────────────

        [Fact]
        public async Task Librarian_IngestMarkdown_NoChatCalls_VerbatimSectionChunks()
        {
            if (!await SqlAvailableAsync()) return; // soft-skip off-rig

            using var server = new FakeSseServer();
            string md = Path.Combine(Path.GetTempPath(), $"devmind_test_{Guid.NewGuid():N}.md");
            File.WriteAllText(md, "# Archiving Guide\n\nDocuments are archived nightly.\n\n## Validation\n\nChecksums are verified on restore.");
            int docId = 0;
            var store = new LibraryStore(ConnectionString);
            try
            {
                var result = await DocumentLibrarian.IngestAsync(
                    new FakeLlmOptions(), server.BaseUrl, apiKey: null,
                    embeddingEndpointUrl: server.BaseUrl, connectionString: ConnectionString,
                    pdfPath: md, chunkSize: 5, progress: null, ct: CancellationToken.None);
                docId = result.DocumentId;

                Assert.Equal(1, result.Chunks);                       // fits one chunk
                Assert.Empty(server.RequestBodies);                   // NO chat-model calls
                Assert.Single(server.EmbeddingRequestBodies);         // one embedding per chunk

                // Wide topK: the shared rig library holds real documents whose real
                // embeddings may outrank this test's pseudo-embedding at small K.
                var hits = await DocumentLibrarian.QueryAsync(
                    server.BaseUrl, ConnectionString, "how is archiving validated?", 200, CancellationToken.None);
                var mine = hits.Single(h => h.DocumentName == Path.GetFileName(md));
                Assert.Contains("Checksums are verified on restore.", mine.Notes); // verbatim, not summarized
                Assert.Equal(1, mine.FirstPage);

                string prompt = DocumentLibrarian.BuildAugmentedPrompt("q?", new List<LibraryHit> { mine });
                Assert.Contains("section 1", prompt);                 // text docs cite sections, not pages
                Assert.DoesNotContain("pages 1-1", prompt);
            }
            finally
            {
                File.Delete(md);
                if (docId > 0)
                    await store.RemoveDocumentAsync(docId, CancellationToken.None);
            }
        }

        [Fact]
        public async Task Librarian_IngestEmptyTextDoc_ThrowsCleanly()
        {
            string md = Path.Combine(Path.GetTempPath(), $"devmind_test_{Guid.NewGuid():N}.md");
            File.WriteAllText(md, "   \n\n   ");
            try
            {
                // Fails before touching SQL or the embedder, so no rig needed.
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    DocumentLibrarian.IngestAsync(
                        new FakeLlmOptions(), "http://127.0.0.1:1/v1", apiKey: null,
                        embeddingEndpointUrl: "http://127.0.0.1:1/v1", connectionString: "Server=none",
                        pdfPath: md, chunkSize: 5, progress: null, ct: CancellationToken.None));
            }
            finally
            {
                File.Delete(md);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Minimal OOXML container: just word/document.xml around the given body.</summary>
        private static string WriteTempDocx(string bodyXml)
        {
            string path = Path.Combine(Path.GetTempPath(), $"devmind_test_{Guid.NewGuid():N}.docx");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), Encoding.UTF8))
            {
                writer.Write(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                    $"<w:body>{bodyXml}</w:body></w:document>");
            }
            return path;
        }

        private static async Task<bool> SqlAvailableAsync()
        {
            try
            {
                using var conn = new Microsoft.Data.SqlClient.SqlConnection(ConnectionString);
                await conn.OpenAsync();
                return true;
            }
            catch
            {
                return false; // rig not reachable — integration tests soft-skip
            }
        }
    }
}
