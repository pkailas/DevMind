// File: DocumentLibrarian.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The /library feature's orchestrator: RAG over vision-derived page notes.
//   Ingest — DocumentDigester-style chunk pass (vision notes per page range) on a
//   private LlmClient, each note embedded (EmbeddingClient) and stored with
//   provenance in SQL Server 2025 (LibraryStore, native VECTOR). Embed once.
//   Query — embed the question, retrieve the nearest chunks across the whole
//   library, and hand back provenance-labelled excerpts for context injection.
// Vision-grounded on purpose: scanned documents, diagrams and layout-heavy pages
// work — text-extraction RAG sees none of that.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>Outcome of a library ingestion.</summary>
    public sealed class LibraryIngestResult
    {
        public int DocumentId { get; set; }
        public int Chunks { get; set; }
        public int Pages { get; set; }
    }

    /// <summary>Ingest/query orchestration for the document library.</summary>
    public static class DocumentLibrarian
    {
        /// <summary>Chunks retrieved per question — enough coverage without flooding context.</summary>
        public const int DefaultTopK = 6;

        /// <summary>
        /// Ingests a PDF: vision notes per chunk (chat endpoint), one embedding per
        /// chunk (embedding endpoint), stored with page provenance. Re-ingesting the
        /// same or a changed file replaces the prior rows.
        /// </summary>
        public static async Task<LibraryIngestResult> IngestAsync(
            ILlmOptions options,
            string chatEndpointUrl,
            string apiKey,
            string embeddingEndpointUrl,
            string connectionString,
            string pdfPath,
            int chunkSize,
            Action<string> progress,
            CancellationToken ct)
        {
            string name = Path.GetFileName(pdfPath);
            string sha256 = ComputeSha256(pdfPath);
            int pageCount = PdfRasterizer.GetPageCount(pdfPath);
            int totalChunks = (pageCount + chunkSize - 1) / chunkSize;

            var store = new LibraryStore(connectionString);
            await store.EnsureSchemaAsync(ct).ConfigureAwait(false);

            progress?.Invoke($"[LIBRARY] Ingesting {name}: {pageCount} page(s) → {totalChunks} chunk(s) of up to {chunkSize}.\n");

            using (var chat = new LlmClient(options))
            using (var embedder = new EmbeddingClient(embeddingEndpointUrl))
            {
                chat.Configure(chatEndpointUrl, apiKey);
                int documentId = await store.UpsertDocumentAsync(name, pdfPath, pageCount, sha256, ct).ConfigureAwait(false);

                int lastPage = 0;
                int chunkIndex = 0;
                var swAll = Stopwatch.StartNew();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = PdfRasterizer.NextChunk(lastPage, chunkSize, pageCount);
                    if (chunk == null)
                        break;
                    var (first, last) = chunk.Value;
                    chunkIndex++;

                    var sw = Stopwatch.StartNew();
                    var pages = PdfRasterizer.RenderPagesToPng(pdfPath, first, last);
                    chat.ClearHistory();
                    foreach (var page in pages)
                        chat.StagePendingImage($"data:image/png;base64,{Convert.ToBase64String(page.PngBytes)}");
                    chat.IncrementTurn();

                    string prompt =
                        $"These images are pages {first}-{last} of {pageCount} from the document \"{name}\". " +
                        DocumentDigester.ChunkInstruction;
                    string note = await DocumentDigester.AskAsync(chat, prompt, DocumentDigester.ChunkMaxTokens, ct)
                        .ConfigureAwait(false);
                    note = DocumentDigester.TruncateNote(note, DocumentDigester.MaxChunkNoteChars);

                    float[] embedding = await embedder.EmbedAsync(note, ct).ConfigureAwait(false);
                    await store.AddChunkAsync(documentId, first, last, note, embedding, ct).ConfigureAwait(false);

                    progress?.Invoke($"[LIBRARY] chunk {chunkIndex}/{totalChunks} (pages {first}-{last}) " +
                                     $"noted + embedded in {sw.Elapsed.TotalSeconds:F0}s.\n");
                    lastPage = last;
                }

                progress?.Invoke($"[LIBRARY] {name} ingested in {swAll.Elapsed.TotalMinutes:F1} min — " +
                                 $"{chunkIndex} chunk(s) searchable.\n");
                return new LibraryIngestResult { DocumentId = documentId, Chunks = chunkIndex, Pages = pageCount };
            }
        }

        /// <summary>Embeds the question and returns the nearest library chunks.</summary>
        public static async Task<List<LibraryHit>> QueryAsync(
            string embeddingEndpointUrl,
            string connectionString,
            string question,
            int topK,
            CancellationToken ct)
        {
            using (var embedder = new EmbeddingClient(embeddingEndpointUrl))
            {
                float[] queryEmbedding = await embedder.EmbedAsync(question, ct).ConfigureAwait(false);
                var store = new LibraryStore(connectionString);
                return await store.SearchAsync(queryEmbedding, topK, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Builds the augmented user message for a library question: provenance-labelled
        /// excerpts followed by the question, with grounding instructions.
        /// </summary>
        public static string BuildAugmentedPrompt(string question, List<LibraryHit> hits)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Answer the question using the library excerpts below — vision-derived notes " +
                          "from ingested PDF documents, labelled with document name and page range. " +
                          "Cite the document and pages you draw from. If the excerpts do not contain " +
                          "the answer, say so instead of guessing.");
            sb.AppendLine();
            sb.AppendLine("[LIBRARY EXCERPTS]");
            for (int i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                sb.AppendLine($"({i + 1}) {h.DocumentName} — pages {h.FirstPage}-{h.LastPage} (distance {h.Distance:F3}):");
                sb.AppendLine(h.Notes);
                sb.AppendLine();
            }
            sb.AppendLine("[QUESTION]");
            sb.Append(question);
            return sb.ToString();
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(64);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
