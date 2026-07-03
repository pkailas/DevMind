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
            "Write concise, dense factual notes (aim for under 500 words) capturing the substantive " +
            "content of these pages: topics, definitions, procedures, configuration items, and " +
            "important details, citing page numbers. Summarize code listings and long tables by " +
            "their purpose, names/signatures, and notable logic — do NOT transcribe them line by " +
            "line. Never repeat yourself. If the pages are covers, tables of contents, or blank, " +
            "briefly note the document structure they reveal instead. Notes only — no preamble.";

        /// <summary>
        /// Hard cap on a single chunk's notes (~5K tokens). Guards against the model
        /// transcribing code-listing pages instead of summarizing them — observed in the
        /// field: 5 pages of a development guide produced 135K chars of "notes" and a
        /// 278s chunk, quintupling the synthesis prompt.
        /// </summary>
        internal const int MaxChunkNoteChars = 20_000;

        /// <summary>
        /// Budget for the combined notes fed to the synthesis call (~50K tokens). When
        /// exceeded, every note is trimmed to an equal share so the reduce step stays
        /// well inside the context window regardless of document size.
        /// </summary>
        internal const int TotalNotesBudgetChars = 200_000;

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
                    int rawNoteLength = note.Length;
                    note = TruncateNote(note, MaxChunkNoteChars);
                    notes.Add($"--- Pages {first}-{last} ---\n{note}");

                    progress?.Invoke($"[DIGEST] chunk {chunkIndex}/{totalChunks} (pages {first}-{last}) done in " +
                                     $"{sw.Elapsed.TotalSeconds:F0}s — {rawNoteLength} chars of notes" +
                                     (note.Length < rawNoteLength
                                         ? $" (truncated to {MaxChunkNoteChars} — bulk content, likely code listings)"
                                         : "") + ".\n");
                    lastPage = last;
                }

                ct.ThrowIfCancellationRequested();
                if (FitNotesToBudget(notes, TotalNotesBudgetChars))
                    progress?.Invoke($"[DIGEST] combined notes exceeded the {TotalNotesBudgetChars:N0}-char synthesis " +
                                     "budget — trimmed each chunk's notes to an equal share.\n");
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
        /// Truncates a runaway note to <paramref name="maxChars"/> with an explicit marker,
        /// so the synthesis step knows content was dropped rather than silently missing.
        /// </summary>
        internal static string TruncateNote(string note, int maxChars)
        {
            if (note == null || note.Length <= maxChars)
                return note;
            return note.Substring(0, maxChars) +
                $"\n[... truncated {note.Length - maxChars:N0} of {note.Length:N0} chars — bulk content (likely transcribed code/tables) dropped ...]";
        }

        /// <summary>
        /// Trims every note to an equal share when their combined size exceeds the
        /// synthesis budget (floor 2,000 chars each so short documents keep substance).
        /// Returns true when trimming was applied.
        /// </summary>
        internal static bool FitNotesToBudget(List<string> notes, int budgetChars)
        {
            long total = 0;
            foreach (string note in notes)
                total += note.Length;
            if (total <= budgetChars || notes.Count == 0)
                return false;

            int perNote = Math.Max(2000, budgetChars / notes.Count);
            bool trimmed = false;
            for (int i = 0; i < notes.Count; i++)
            {
                string fitted = TruncateNote(notes[i], perNote);
                if (!ReferenceEquals(fitted, notes[i]))
                {
                    notes[i] = fitted;
                    trimmed = true;
                }
            }
            return trimmed;
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
