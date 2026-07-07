// File: LibraryStore.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// SQL Server 2025 vector store for the /library feature: documents + vision-note
// chunks with native VECTOR(1024) embeddings, searched via VECTOR_DISTANCE('cosine').
// Lives in its own `lib` schema so it coexists with anything else in the database.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace DevMind
{
    /// <summary>A library document row (for /library listing).</summary>
    public sealed class LibraryDocument
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Pages { get; set; }
        public int ChunkCount { get; set; }
        public DateTime IngestedAtUtc { get; set; }
    }

    /// <summary>A retrieved chunk with provenance (for context injection).</summary>
    public sealed class LibraryHit
    {
        public string DocumentName { get; set; }
        public int FirstPage { get; set; }
        public int LastPage { get; set; }
        public string Notes { get; set; }
        public double Distance { get; set; }
    }

    /// <summary>
    /// Persistence layer for the document library on SQL Server 2025.
    /// </summary>
    public sealed class LibraryStore
    {
        private readonly string _connectionString;

        public LibraryStore(string connectionString) => _connectionString = connectionString;

        /// <summary>Creates the lib schema and tables when missing (idempotent).</summary>
        public async Task EnsureSchemaAsync(CancellationToken ct)
        {
            const string ddl = @"
IF SCHEMA_ID('lib') IS NULL EXEC('CREATE SCHEMA lib');
IF OBJECT_ID('lib.Documents') IS NULL
CREATE TABLE lib.Documents (
    Id INT IDENTITY PRIMARY KEY,
    Name NVARCHAR(500) NOT NULL,
    Path NVARCHAR(1000) NOT NULL,
    Pages INT NOT NULL,
    Sha256 CHAR(64) NOT NULL,
    IngestedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_lib_Documents_Sha256 UNIQUE (Sha256)
);
IF OBJECT_ID('lib.Chunks') IS NULL
CREATE TABLE lib.Chunks (
    Id INT IDENTITY PRIMARY KEY,
    DocumentId INT NOT NULL REFERENCES lib.Documents(Id) ON DELETE CASCADE,
    FirstPage INT NOT NULL,
    LastPage INT NOT NULL,
    Notes NVARCHAR(MAX) NOT NULL,
    Embedding VECTOR(1024) NOT NULL
);";
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand(ddl, conn))
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Registers a document, replacing any prior ingestion of the same content
        /// (matched by SHA-256 — re-ingesting a changed file creates a new row, the
        /// old one is removed by path). Returns the new document id.
        /// </summary>
        public async Task<int> UpsertDocumentAsync(
            string name, string path, int pages, string sha256, CancellationToken ct)
        {
            const string sql = @"
DELETE FROM lib.Documents WHERE Sha256 = @sha OR Path = @path;
INSERT INTO lib.Documents (Name, Path, Pages, Sha256) VALUES (@name, @path, @pages, @sha);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@path", path);
                    cmd.Parameters.AddWithValue("@pages", pages);
                    cmd.Parameters.AddWithValue("@sha", sha256);
                    return (int)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Stores one chunk's notes + embedding.</summary>
        public async Task AddChunkAsync(
            int documentId, int firstPage, int lastPage, string notes, float[] embedding,
            CancellationToken ct)
        {
            const string sql = @"
INSERT INTO lib.Chunks (DocumentId, FirstPage, LastPage, Notes, Embedding)
VALUES (@doc, @first, @last, @notes, CAST(@emb AS VECTOR(1024)));";
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@doc", documentId);
                    cmd.Parameters.AddWithValue("@first", firstPage);
                    cmd.Parameters.AddWithValue("@last", lastPage);
                    cmd.Parameters.AddWithValue("@notes", notes);
                    cmd.Parameters.AddWithValue("@emb", EmbeddingClient.ToJsonArray(embedding));
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Nearest chunks to the query embedding across the whole library.</summary>
        public async Task<List<LibraryHit>> SearchAsync(float[] queryEmbedding, int topK, CancellationToken ct)
        {
            const string sql = @"
SELECT TOP (@k) d.Name, c.FirstPage, c.LastPage, c.Notes,
       VECTOR_DISTANCE('cosine', c.Embedding, CAST(@q AS VECTOR(1024))) AS Dist
FROM lib.Chunks c
JOIN lib.Documents d ON d.Id = c.DocumentId
ORDER BY Dist ASC;";
            var hits = new List<LibraryHit>();
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@k", topK);
                    cmd.Parameters.AddWithValue("@q", EmbeddingClient.ToJsonArray(queryEmbedding));
                    using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            hits.Add(new LibraryHit
                            {
                                DocumentName = reader.GetString(0),
                                FirstPage = reader.GetInt32(1),
                                LastPage = reader.GetInt32(2),
                                Notes = reader.GetString(3),
                                Distance = Convert.ToDouble(reader.GetValue(4)),
                            });
                        }
                    }
                }
            }
            return hits;
        }

        /// <summary>All documents with chunk counts, newest first.</summary>
        public async Task<List<LibraryDocument>> ListDocumentsAsync(CancellationToken ct)
        {
            const string sql = @"
SELECT d.Id, d.Name, d.Pages, COUNT(c.Id) AS ChunkCount, d.IngestedAt
FROM lib.Documents d
LEFT JOIN lib.Chunks c ON c.DocumentId = d.Id
GROUP BY d.Id, d.Name, d.Pages, d.IngestedAt
ORDER BY d.IngestedAt DESC;";
            var docs = new List<LibraryDocument>();
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand(sql, conn))
                using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        docs.Add(new LibraryDocument
                        {
                            Id = reader.GetInt32(0),
                            Name = reader.GetString(1),
                            Pages = reader.GetInt32(2),
                            ChunkCount = reader.GetInt32(3),
                            IngestedAtUtc = reader.GetDateTime(4),
                        });
                    }
                }
            }
            return docs;
        }

        /// <summary>Look up a document id by file path. Returns null when not found.</summary>
        public async Task<int?> GetDocumentIdByPathAsync(string path, CancellationToken ct)
        {
            const string sql = "SELECT Id FROM lib.Documents WHERE Path = @path;";
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@path", path);
                    object result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    if (result == null || result == System.DBNull.Value)
                        return null;
                    return (int)result;
                }
            }
        }

        /// <summary>Delete chunks overlapping a page range (for partial re-ingest).</summary>
        public async Task DeleteChunksInRangeAsync(int documentId, int startPage, int endPage, CancellationToken ct)
        {
            const string sql = @"
DELETE FROM lib.Chunks
WHERE DocumentId = @doc AND FirstPage <= @end AND LastPage >= @start;";
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@doc", documentId);
                    cmd.Parameters.AddWithValue("@start", startPage);
                    cmd.Parameters.AddWithValue("@end", endPage);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Removes a document (and its chunks via cascade) by id. True when found.</summary>
        public async Task<bool> RemoveDocumentAsync(int documentId, CancellationToken ct)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new SqlCommand("DELETE FROM lib.Documents WHERE Id = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", documentId);
                    return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
                }
            }
        }
    }
}
