// File: DocumentDigester.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Map-reduce PDF digestion for the /digest command: rasterize the document in
// chunks (PdfRasterizer), summarize each chunk as a vision request on a PRIVATE
// LlmClient conversation (so the user's session context is never consumed by
// page images), then synthesize the accumulated chunk notes into one digest.
// The host displays the digest and injects it into the main conversation.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>Outcome of a document digestion run.</summary>
    public sealed class DigestResult
    {
        /// <summary>The synthesized Markdown digest.</summary>
        public string Digest { get; set; }

        /// <summary>Number of page chunks summarized.</summary>
        public int ChunksProcessed { get; set; }

        /// <summary>Total pages in the source document.</summary>
        public int PageCount { get; set; }
    }

    /// <summary>
    /// Digests a whole PDF: chunk → vision-summarize → synthesize.
    /// </summary>
    public static class DocumentDigester
    {
        private const string DigestSystemPrompt =
            "You are a meticulous document analyst. You read document page images and produce " +
            "accurate, dense, well-organized notes and digests. Never invent content that is " +
            "not visible on the pages.";

        private const string ChunkInstruction =
            "Write dense factual notes capturing the substantive content of these pages: topics, " +
            "definitions, procedures, configuration items, tables, and important details, citing " +
            "page numbers. If the pages are covers, tables of contents, or blank, briefly note the " +
            "document structure they reveal instead. Notes only — no preamble.";

        /// <summary>
        /// Runs the full digestion. Uses its own LlmClient (fresh conversation per chunk)
        /// against the same endpoint, so the caller's conversation history is untouched.
        /// Progress lines stream through <paramref name="progress"/>; cancellation aborts
        /// between and within chunks.
        /// </summary>
        public static async Task<DigestResult> RunAsync(
            ILlmOptions options,
            string endpointUrl,
            string apiKey,
            string pdfPath,
            int chunkSize,
            Action<string> progress,
            CancellationToken ct)
        {
            string name = Path.GetFileName(pdfPath);
            int pageCount = PdfRasterizer.GetPageCount(pdfPath);
            int totalChunks = (pageCount + chunkSize - 1) / chunkSize;
            progress?.Invoke($"[DIGEST] {name}: {pageCount} page(s) → {totalChunks} chunk(s) of up to {chunkSize}.\n");

            using (var client = new LlmClient(options))
            {
                client.Configure(endpointUrl, apiKey);

                var notes = new List<string>(totalChunks);
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

                    // Fresh conversation per chunk: page images must not accumulate.
                    client.ClearHistory();
                    foreach (var page in pages)
                        client.StagePendingImage($"data:image/png;base64,{Convert.ToBase64String(page.PngBytes)}");
                    client.IncrementTurn();

                    string chunkPrompt =
                        $"These images are pages {first}-{last} of {pageCount} from the document \"{name}\". " +
                        ChunkInstruction;
                    string note = await AskAsync(client, chunkPrompt, ct).ConfigureAwait(false);
                    notes.Add($"--- Pages {first}-{last} ---\n{note}");

                    progress?.Invoke($"[DIGEST] chunk {chunkIndex}/{totalChunks} (pages {first}-{last}) done in " +
                                     $"{sw.Elapsed.TotalSeconds:F0}s — {note.Length} chars of notes.\n");
                    lastPage = last;
                }

                ct.ThrowIfCancellationRequested();
                progress?.Invoke($"[DIGEST] synthesizing digest from {notes.Count} chunk note(s)...\n");

                client.ClearHistory();
                client.IncrementTurn();
                string synthesisPrompt =
                    $"Below are sequential reading notes covering all {pageCount} pages of \"{name}\". " +
                    "Synthesize ONE well-structured Markdown digest of the document: its purpose, the main " +
                    "sections and what each covers, key concepts/procedures/configuration items, and notable " +
                    "specifics worth remembering. Comprehensive but non-repetitive.\n\n" +
                    string.Join("\n\n", notes);
                string digest = await AskAsync(client, synthesisPrompt, ct).ConfigureAwait(false);

                progress?.Invoke($"[DIGEST] finished in {swAll.Elapsed.TotalMinutes:F1} min.\n");
                return new DigestResult
                {
                    Digest = digest,
                    ChunksProcessed = notes.Count,
                    PageCount = pageCount,
                };
            }
        }

        /// <summary>
        /// One request/response round trip on the private client, collecting visible
        /// tokens. SendMessageAsync swallows OperationCanceledException without firing
        /// either callback, so cancellation is propagated by cancelling the completion
        /// source directly.
        /// </summary>
        private static async Task<string> AskAsync(LlmClient client, string prompt, CancellationToken ct)
        {
            var collected = new StringBuilder();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => done.TrySetCanceled(ct)))
            {
                await client.SendMessageAsync(
                    prompt,
                    onToken: token =>
                    {
                        if (!IsStatusLine(token))
                            collected.Append(token);
                    },
                    onComplete: () => done.TrySetResult(true),
                    onError: ex => done.TrySetException(ex),
                    combinedSystemPrompt: DigestSystemPrompt,
                    cancellationToken: ct).ConfigureAwait(false);
                await done.Task.ConfigureAwait(false);
            }
            return StripThink(collected.ToString()).Trim();
        }

        /// <summary>
        /// True for DevMind's own status/diagnostic lines ([CONTEXT], [LLM], [DROPPED], …)
        /// injected through onToken — they are not model content and must not enter notes.
        /// Mirrors the TUI's heuristic: a leading all-caps bracket tag.
        /// </summary>
        private static bool IsStatusLine(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            int i = 0;
            while (i < token.Length && char.IsWhiteSpace(token[i])) i++;
            if (i >= token.Length || token[i] != '[') return false;
            i++;
            if (i >= token.Length || !char.IsUpper(token[i])) return false;
            for (; i < token.Length; i++)
            {
                char c = token[i];
                if (c == ']') return true;
                if (!(char.IsUpper(c) || char.IsDigit(c) || c == '_' || c == '-' || c == ' ')) return false;
            }
            return false;
        }

        /// <summary>
        /// Removes reasoning re-synthesized as &lt;think&gt; blocks (llama-server's native
        /// reasoning channel) — notes must be the answer, not the deliberation. A dangling
        /// unclosed &lt;think&gt; (truncated response) drops everything from the tag on.
        /// </summary>
        private static string StripThink(string text)
        {
            string result = Regex.Replace(text, "<think>.*?</think>", "", RegexOptions.Singleline);
            int dangling = result.IndexOf("<think>", StringComparison.Ordinal);
            if (dangling >= 0)
                result = result.Substring(0, dangling);
            return result;
        }
    }
}
