// File: LibraryTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the /library RAG stack:
//   * EmbeddingClient — truncation/renormalization math and wire round trip
//     against the fake embeddings endpoint.
//   * LibraryStore — INTEGRATION against the real SQL Server 2025 instance
//     (WIN-SQL002,14330 / DevMindRAG): schema, upsert-replace, KNN ordering via
//     native VECTOR_DISTANCE. Soft-skipped when the server is unreachable so the
//     suite still runs on machines without the rig.
//   * DocumentLibrarian — end-to-end ingest (fake chat SSE + fake embeddings +
//     real SQL) and query, plus augmented-prompt formatting.

using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class LibraryTests
    {
        private const string ConnectionString =
            "Server=WIN-SQL002,14330;Database=DevMindRAG;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5";

        // ── EmbeddingClient ───────────────────────────────────────────────────

        [Fact]
        public void TruncateAndNormalize_TruncatesAndYieldsUnitVector()
        {
            var full = new float[4096];
            for (int i = 0; i < full.Length; i++) full[i] = (i % 7) - 3;

            var v = EmbeddingClient.TruncateAndNormalize(full, 1024);

            Assert.Equal(1024, v.Length);
            double norm = 0;
            foreach (float f in v) norm += (double)f * f;
            Assert.Equal(1.0, Math.Sqrt(norm), 3);
        }

        [Fact]
        public void ToJsonArray_InvariantBracketedList()
        {
            string json = EmbeddingClient.ToJsonArray(new[] { 0.5f, -0.25f, 1f });
            Assert.Equal("[0.5,-0.25,1]", json);
        }

        [Fact]
        public async Task EmbedAsync_AgainstFakeServer_Returns1024DimUnitVector()
        {
            using var server = new FakeSseServer();
            using var client = new EmbeddingClient(server.BaseUrl);

            float[] v = await client.EmbedAsync("hello library", CancellationToken.None);

            Assert.Equal(EmbeddingClient.Dimensions, v.Length);
            double norm = 0;
            foreach (float f in v) norm += (double)f * f;
            Assert.Equal(1.0, Math.Sqrt(norm), 3);
            Assert.Single(server.EmbeddingRequestBodies);
            Assert.Contains("hello library", server.EmbeddingRequestBodies[0]);
        }

        // ── LibraryStore (real SQL Server 2025) ───────────────────────────────

        [Fact]
        public async Task LibraryStore_UpsertSearchListRemove_RoundTrip()
        {
            if (!await SqlAvailableAsync()) return; // soft-skip off-rig

            var store = new LibraryStore(ConnectionString);
            await store.EnsureSchemaAsync(CancellationToken.None);

            string sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            int docId = await store.UpsertDocumentAsync(
                "test-doc.pdf", $"X:\\tests\\{sha}.pdf", 4, sha, CancellationToken.None);
            try
            {
                // Two chunks with orthogonal-ish embeddings.
                var embA = Unit(0);
                var embB = Unit(500);
                await store.AddChunkAsync(docId, 1, 2, "chunk about archiving", embA, CancellationToken.None);
                await store.AddChunkAsync(docId, 3, 4, "chunk about validation", embB, CancellationToken.None);

                // Query WITH embA — its chunk must rank first at ~0 distance.
                var hits = await store.SearchAsync(embA, 2, CancellationToken.None);
                Assert.True(hits.Count >= 2);
                Assert.Equal("chunk about archiving", hits[0].Notes);
                Assert.True(hits[0].Distance < 0.001, $"self-distance {hits[0].Distance}");
                Assert.True(hits[1].Distance > hits[0].Distance);

                var docs = await store.ListDocumentsAsync(CancellationToken.None);
                var mine = docs.Single(d => d.Id == docId);
                Assert.Equal(2, mine.ChunkCount);
                Assert.Equal(4, mine.Pages);

                // Re-ingest same sha → replaced, chunks reset.
                int docId2 = await store.UpsertDocumentAsync(
                    "test-doc.pdf", $"X:\\tests\\{sha}.pdf", 4, sha, CancellationToken.None);
                Assert.NotEqual(docId, docId2);
                docId = docId2;
                var docsAfter = await store.ListDocumentsAsync(CancellationToken.None);
                Assert.Equal(0, docsAfter.Single(d => d.Id == docId).ChunkCount);
            }
            finally
            {
                Assert.True(await store.RemoveDocumentAsync(docId, CancellationToken.None));
            }
        }

        // ── LibraryStore: GetDocumentIdByPath + DeleteChunksInRange ────────────

        [Fact]
        public async Task LibraryStore_GetDocumentIdByPathAndDeleteChunksInRange_RoundTrip()
        {
            if (!await SqlAvailableAsync()) return; // soft-skip off-rig

            var store = new LibraryStore(ConnectionString);
            await store.EnsureSchemaAsync(CancellationToken.None);

            string sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            string path = $"X:\\tests\\{sha}.pdf";
            int docId = await store.UpsertDocumentAsync(
                "range-test.pdf", path, 10, sha, CancellationToken.None);
            try
            {
                // GetDocumentIdByPathAsync returns the correct id
                int? foundId = await store.GetDocumentIdByPathAsync(path, CancellationToken.None);
                Assert.Equal(docId, foundId);

                // Nonexistent path returns null
                Assert.Null(await store.GetDocumentIdByPathAsync("X:\\nope.pdf", CancellationToken.None));

                // Add 3 chunks: pages 1-3, 4-6, 7-10
                await store.AddChunkAsync(docId, 1, 3, "chunk-A", Unit(0), CancellationToken.None);
                await store.AddChunkAsync(docId, 4, 6, "chunk-B", Unit(1), CancellationToken.None);
                await store.AddChunkAsync(docId, 7, 10, "chunk-C", Unit(2), CancellationToken.None);

                var docs = await store.ListDocumentsAsync(CancellationToken.None);
                Assert.Equal(3, docs.Single(d => d.Id == docId).ChunkCount);

                // Delete range pages 3-7 — should remove chunk-A (1-3 overlaps 3) and chunk-B (4-6 overlaps 3-7)
                // chunk-C (7-10) also overlaps at page 7, so all 3 should be deleted
                await store.DeleteChunksInRangeAsync(docId, 3, 7, CancellationToken.None);

                var docsAfter = await store.ListDocumentsAsync(CancellationToken.None);
                Assert.Equal(0, docsAfter.Single(d => d.Id == docId).ChunkCount);

                // Re-add chunks for a more selective test
                await store.AddChunkAsync(docId, 1, 3, "chunk-A", Unit(0), CancellationToken.None);
                await store.AddChunkAsync(docId, 4, 6, "chunk-B", Unit(1), CancellationToken.None);
                await store.AddChunkAsync(docId, 7, 10, "chunk-C", Unit(2), CancellationToken.None);

                // Delete range pages 5-5 — only chunk-B (4-6) overlaps
                await store.DeleteChunksInRangeAsync(docId, 5, 5, CancellationToken.None);

                var docsAfter2 = await store.ListDocumentsAsync(CancellationToken.None);
                Assert.Equal(2, docsAfter2.Single(d => d.Id == docId).ChunkCount);

                // Documents.Pages is unchanged (still 10)
                Assert.Equal(10, docsAfter2.Single(d => d.Id == docId).Pages);
            }
            finally
            {
                Assert.True(await store.RemoveDocumentAsync(docId, CancellationToken.None));
            }
        }

        // ── LibraryStore: atomic ReplaceChunksInRange (build-then-swap) ────────

        [Fact]
        public async Task LibraryStore_ReplaceChunksInRange_AtomicSwap_ReplacesOnlyOverlap()
        {
            if (!await SqlAvailableAsync()) return; // soft-skip off-rig

            var store = new LibraryStore(ConnectionString);
            await store.EnsureSchemaAsync(CancellationToken.None);

            string sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            string path = $"X:\\tests\\{sha}.pdf";
            int docId = await store.UpsertDocumentAsync(
                "swap-test.pdf", path, 10, sha, CancellationToken.None);
            try
            {
                // Three original chunks with distinctive embeddings.
                await store.AddChunkAsync(docId, 1, 3, "orig-A", Unit(700), CancellationToken.None);
                await store.AddChunkAsync(docId, 4, 6, "orig-B", Unit(701), CancellationToken.None);
                await store.AddChunkAsync(docId, 7, 10, "orig-C", Unit(702), CancellationToken.None);

                // Atomically swap the middle range (pages 4-6) for a new single chunk.
                var replacement = new List<PendingChunk>
                {
                    new PendingChunk { FirstPage = 4, LastPage = 6, Notes = "new-B", Embedding = Unit(703) },
                };
                await store.ReplaceChunksInRangeAsync(docId, 4, 6, replacement, CancellationToken.None);

                // Count is still 3 (A, new-B, C): the overlap was swapped, not duplicated.
                var docs = await store.ListDocumentsAsync(CancellationToken.None);
                Assert.Equal(3, docs.Single(d => d.Id == docId).ChunkCount);

                // Chunks OUTSIDE the range survive untouched.
                Assert.Equal("orig-A", (await store.SearchAsync(Unit(700), 1, CancellationToken.None))[0].Notes);
                Assert.Equal("orig-C", (await store.SearchAsync(Unit(702), 1, CancellationToken.None))[0].Notes);

                // The IN-range chunk was replaced: new-B is now present (old orig-B is gone —
                // proven by count==3 with A, new-B, C being the only chunks).
                Assert.Equal("new-B", (await store.SearchAsync(Unit(703), 1, CancellationToken.None))[0].Notes);

                // Documents.Pages is unchanged (still 10).
                Assert.Equal(10, docs.Single(d => d.Id == docId).Pages);
            }
            finally
            {
                Assert.True(await store.RemoveDocumentAsync(docId, CancellationToken.None));
            }
        }

        // ── DocumentLibrarian E2E: fake chat + fake embeddings + real SQL ─────

        [Fact]
        public async Task Librarian_IngestThenQuery_RetrievesProvenancedChunks()
        {
            if (!await SqlAvailableAsync()) return; // soft-skip off-rig

            using var server = new FakeSseServer { ResponseText = "NOTES-FOR-LIBRARY" };
            string pdf = PdfTestFiles.WriteTempPdf(widthPts: 200, heightPts: 300, pages: 2);
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            int docId = 0;
            var store = new LibraryStore(ConnectionString);
            try
            {
                var result = await DocumentLibrarian.IngestAsync(
                    new FakeLlmOptions(), server.BaseUrl, apiKey: null,
                    embeddingEndpointUrl: server.BaseUrl, connectionString: ConnectionString,
                    pdfPath: pdf, chunkSize: 1, progress: null, ct: CancellationToken.None);
                docId = result.DocumentId;

                Assert.Equal(2, result.Chunks);
                Assert.Equal(2, result.Pages);
                Assert.Equal(2, server.RequestBodies.Count);          // one vision call per page
                Assert.Equal(2, server.EmbeddingRequestBodies.Count); // one embedding per chunk

                var hits = await DocumentLibrarian.QueryAsync(
                    server.BaseUrl, ConnectionString, "what are these notes?", 4, CancellationToken.None);
                Assert.Contains(hits, h => h.Notes == "NOTES-FOR-LIBRARY");

                string prompt = DocumentLibrarian.BuildAugmentedPrompt("what are these notes?", hits);
                Assert.Contains("[LIBRARY EXCERPTS]", prompt);
                Assert.Contains("pages 1-1", prompt);
                Assert.Contains("[QUESTION]", prompt);
                Assert.EndsWith("what are these notes?", prompt);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
                File.Delete(pdf);
                if (docId > 0)
                    await store.RemoveDocumentAsync(docId, CancellationToken.None);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static float[] Unit(int hotIndex)
        {
            var v = new float[EmbeddingClient.Dimensions];
            v[hotIndex] = 1f;
            return v;
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
