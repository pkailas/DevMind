// File: DocumentDigesterTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// End-to-end tests for /digest's engine: a real (generated) multi-page PDF is
// chunk-rasterized and driven through a real LlmClient against the fake SSE
// server, asserting the actual wire traffic — N multimodal chunk requests
// followed by one text-only synthesis request carrying the chunk notes.

using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class DocumentDigesterTests
    {
        [Fact]
        public async Task RunAsync_ChunksDocument_ThenSynthesizes()
        {
            using var server = new FakeSseServer { ResponseText = "NOTES" };
            string pdf = PdfTestFiles.WriteTempPdf(widthPts: 200, heightPts: 300, pages: 3);
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            var progressLines = new List<string>();
            try
            {
                var result = await DocumentDigester.RunAsync(
                    new FakeLlmOptions(), server.BaseUrl, apiKey: null,
                    pdf, chunkSize: 2,
                    progress: line => progressLines.Add(line),
                    ct: CancellationToken.None);

                // 3 pages at chunk size 2 → chunks (1-2) and (3), then synthesis.
                Assert.Equal(2, result.ChunksProcessed);
                Assert.Equal(3, result.PageCount);
                Assert.Equal("NOTES", result.Digest);
                Assert.Equal(3, server.RequestBodies.Count);

                // Chunk 1: multimodal — text part + 2 page images.
                var chunk1User = LastUserMessage(server.RequestBodies[0]);
                var parts1 = Assert.IsType<JArray>(chunk1User["content"]);
                Assert.Equal(3, parts1.Count);
                Assert.Contains("pages 1-2 of 3", (string?)parts1[0]["text"]);

                // Chunk 2: multimodal — text part + 1 page image.
                var chunk2User = LastUserMessage(server.RequestBodies[1]);
                var parts2 = Assert.IsType<JArray>(chunk2User["content"]);
                Assert.Equal(2, parts2.Count);
                Assert.Contains("pages 3-3 of 3", (string?)parts2[0]["text"]);

                // Synthesis: text-only, carrying both chunk notes with page labels.
                var synthUser = LastUserMessage(server.RequestBodies[2]);
                Assert.Equal(JTokenType.String, synthUser["content"]!.Type);
                string synthesis = (string)synthUser["content"]!;
                Assert.Contains("--- Pages 1-2 ---", synthesis);
                Assert.Contains("--- Pages 3-3 ---", synthesis);
                Assert.Contains("NOTES", synthesis);

                // Progress reported per chunk and for synthesis.
                Assert.Contains(progressLines, l => l.Contains("chunk 1/2"));
                Assert.Contains(progressLines, l => l.Contains("chunk 2/2"));
                Assert.Contains(progressLines, l => l.Contains("synthesizing"));

                // Runaway guard: every chunk request carries the chunk output ceiling,
                // the synthesis request its larger one. Without these an uncapped
                // repetition loop runs to the server's -n budget (observed: 28K tokens).
                Assert.Equal(DocumentDigester.ChunkMaxTokens,
                    (int)JObject.Parse(server.RequestBodies[0])["max_tokens"]!);
                Assert.Equal(DocumentDigester.ChunkMaxTokens,
                    (int)JObject.Parse(server.RequestBodies[1])["max_tokens"]!);
                Assert.Equal(DocumentDigester.SynthesisMaxTokens,
                    (int)JObject.Parse(server.RequestBodies[2])["max_tokens"]!);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
                File.Delete(pdf);
            }
        }

        [Fact]
        public async Task RunAsync_Cancelled_ThrowsOperationCanceled()
        {
            using var server = new FakeSseServer();
            string pdf = PdfTestFiles.WriteTempPdf(widthPts: 200, heightPts: 300, pages: 2);
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel(); // cancelled before the first chunk

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    DocumentDigester.RunAsync(
                        new FakeLlmOptions(), server.BaseUrl, apiKey: null,
                        pdf, chunkSize: 1, progress: null, ct: cts.Token));
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
                File.Delete(pdf);
            }
        }

        [Fact]
        public void TruncateNote_ShortNote_ReturnedUnchanged()
        {
            string note = "short note";
            Assert.Same(note, DocumentDigester.TruncateNote(note, 100));
        }

        [Fact]
        public void TruncateNote_RunawayNote_CappedWithMarker()
        {
            // Field case: a code-listing chunk produced 135K chars of "notes".
            string runaway = new string('x', 135_363);
            string capped = DocumentDigester.TruncateNote(runaway, DocumentDigester.MaxChunkNoteChars);

            Assert.True(capped.Length < 21_000, $"capped length {capped.Length}");
            Assert.StartsWith(new string('x', 100), capped);
            Assert.Contains("truncated", capped);
            Assert.Contains("115,363", capped); // chars dropped, formatted
        }

        [Fact]
        public void FitNotesToBudget_UnderBudget_Untouched()
        {
            var notes = new List<string> { new string('a', 100), new string('b', 100) };
            Assert.False(DocumentDigester.FitNotesToBudget(notes, budgetChars: 1000));
            Assert.Equal(100, notes[0].Length);
        }

        [Fact]
        public void FitNotesToBudget_OverBudget_TrimsEachToEqualShare()
        {
            var notes = new List<string>
            {
                new string('a', 60_000),
                new string('b', 60_000),
                new string('c', 500), // already small — stays intact
            };
            Assert.True(DocumentDigester.FitNotesToBudget(notes, budgetChars: 30_000));

            // Per-note share = 30000/3 = 10000 (+ truncation marker).
            Assert.InRange(notes[0].Length, 10_000, 10_200);
            Assert.InRange(notes[1].Length, 10_000, 10_200);
            Assert.Equal(500, notes[2].Length);
            Assert.Contains("truncated", notes[0]);
        }

        [Fact]
        public void TrimDegenerateTail_EmojiFlood_RemovedEntirely()
        {
            // The field case: a good digest followed by thousands of chars of a
            // cycling emoji sequence (no stop token emitted).
            string digest = "## Key Takeaway\nThis is a stateless OData V4 service.";
            string flood = string.Concat(Enumerable.Repeat("🌐🔗⚙️✅📦📈🔍📖🛠️💡🎯📌🔖🗂️📜🔍📊🛡️🔐", 120));

            string cleaned = DocumentDigester.TrimDegenerateTail(digest + " " + flood);

            Assert.Equal(digest, cleaned);
        }

        [Fact]
        public void TrimDegenerateTail_RepeatingTextTail_CollapsedToOneOccurrence()
        {
            string text = "The service returns a URL." + string.Concat(Enumerable.Repeat(" and so on, and so on", 20));
            string cleaned = DocumentDigester.TrimDegenerateTail(text);

            Assert.StartsWith("The service returns a URL.", cleaned);
            // At most one occurrence of the repeated phrase survives.
            int occurrences = (cleaned.Length - cleaned.Replace(" and so on, and so on", "").Length)
                / " and so on, and so on".Length;
            Assert.True(occurrences <= 1, $"phrase survived {occurrences} times");
        }

        [Fact]
        public void TrimDegenerateTail_ParagraphLengthLoop_Collapsed()
        {
            // Field case 2: the model looped a whole sentence/paragraph (~150 chars) —
            // beyond the original 64-char period window, so 113K chars slipped through.
            string paragraph = "The RAP ArchiveLink URL Generator exposes SAP's internal ArchiveLink " +
                               "capability as a modern OData service for external consumers to request upload URLs. ";
            string text = "## Notes\nReal content here.\n" + string.Concat(Enumerable.Repeat(paragraph, 30));

            string cleaned = DocumentDigester.TrimDegenerateTail(text);

            int occurrences = (cleaned.Length - cleaned.Replace(paragraph, "").Length) / paragraph.Length;
            Assert.True(occurrences <= 1, $"paragraph survived {occurrences} times");
            Assert.StartsWith("## Notes", cleaned);
        }

        [Fact]
        public void TrimDegenerateTail_CleanText_Unchanged()
        {
            string text = "A perfectly normal digest with a conclusion.";
            Assert.Same(text, DocumentDigester.TrimDegenerateTail(text));
        }

        [Fact]
        public void TrimDegenerateTail_LegitimateTrailingEmoji_Kept()
        {
            // A couple of trailing emoji are normal model style — only walls get stripped.
            string text = "Deployment complete 🎯";
            Assert.Equal(text, DocumentDigester.TrimDegenerateTail(text));
        }

        private static JToken LastUserMessage(string requestBody)
        {
            var body = JObject.Parse(requestBody);
            return ((JArray)body["messages"]!).Last(m => (string?)m["role"] == "user");
        }
    }
}
