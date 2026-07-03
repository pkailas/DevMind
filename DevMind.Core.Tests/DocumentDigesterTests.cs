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

        private static JToken LastUserMessage(string requestBody)
        {
            var body = JObject.Parse(requestBody);
            return ((JArray)body["messages"]!).Last(m => (string?)m["role"] == "user");
        }
    }
}
