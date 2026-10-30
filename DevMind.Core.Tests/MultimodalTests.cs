// File: MultimodalTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the multimodal (image) input path:
//   * ChatMessage.ContentParts construction (5-param vs 6-param constructor)
//   * LlmClient.RebuildContentParts — compaction must not silently drop images
//   * LlmClient.EstimateMessageTokens — budget math must count image parts
//   * The pre-send context guard in SendMessageAsync counts image parts too
//     (first turn and after a server n_past count)

//   * End-to-end: SendMessageAsync against a fake SSE server, asserting the
//     ACTUAL request JSON — content is an OpenAI-style parts array (text +
//     image_url) for image sends, a flat string for text-only sends, and
//     StagePendingImage is consumed exactly once.
//   * ChatMessage.ImagePaths: saved-image paths recorded 1:1 with image parts on send,
//     null entries for path-less staging, and carried through micro-compact rewrites.
//   * LlmClient.DropImagesFromHistory: image parts replaced by placeholder lines naming the
//     saved copies, idempotent, and absent from the next request body.

//
// The fake server is a raw TcpListener (not HttpListener) so the tests run
// without Windows URL-ACL reservations. It answers every POST with a minimal
// SSE stream and records each request body for assertions.

using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class MultimodalTests
    {
        // ── ChatMessage construction ──────────────────────────────────────────

        [Fact]
        public void ChatMessage_FiveParamConstructor_HasNullContentParts()
        {
            var msg = new ChatMessage("user", "hello", 1);
            Assert.Equal("user", msg.Role);
            Assert.Equal("hello", msg.Content);
            Assert.Null(msg.ContentParts);
        }

        [Fact]
        public void ChatMessage_SixParamConstructor_StoresContentParts()
        {
            var parts = MakeParts("hello", "data:image/png;base64,AAAA");
            var msg = new ChatMessage("user", "hello", 1, null, null, parts);
            Assert.Same(parts, msg.ContentParts);
            Assert.Equal("hello", msg.Content); // text copy retained for text-only consumers
        }

        // ── RebuildContentParts (compaction image preservation) ───────────────

        [Fact]
        public void RebuildContentParts_NullParts_ReturnsNull()
        {
            Assert.Null(LlmClient.RebuildContentParts(null, "trimmed"));
        }

        [Fact]
        public void RebuildContentParts_ReplacesTextAndPreservesImage()
        {
            var parts = MakeParts("original long text", "data:image/png;base64,AAAA");

            var rebuilt = LlmClient.RebuildContentParts(parts, "trimmed");

            Assert.NotNull(rebuilt);
            Assert.Equal(2, rebuilt!.Count);
            Assert.Equal("text", (string?)rebuilt[0]["type"]);
            Assert.Equal("trimmed", (string?)rebuilt[0]["text"]);
            Assert.Equal("image_url", (string?)rebuilt[1]["type"]);
            Assert.Equal("data:image/png;base64,AAAA", (string?)rebuilt[1]["image_url"]?["url"]);
        }

        [Fact]
        public void RebuildContentParts_PreservesMultipleImages_AndClonesThem()
        {
            var parts = MakeParts("text", "data:image/png;base64,ONE");
            parts.Add(new JObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JObject { ["url"] = "data:image/jpeg;base64,TWO" }
            });

            var rebuilt = LlmClient.RebuildContentParts(parts, "new");

            Assert.Equal(3, rebuilt!.Count);
            Assert.Equal("data:image/png;base64,ONE", (string?)rebuilt[1]["image_url"]?["url"]);
            Assert.Equal("data:image/jpeg;base64,TWO", (string?)rebuilt[2]["image_url"]?["url"]);
            // Deep clones — mutating the rebuilt copy must not touch the original.
            Assert.NotSame(parts[1], rebuilt[1]);
        }

        // ── EstimateMessageTokens (budget math counts images) ─────────────────

        [Fact]
        public void EstimateMessageTokens_TextOnly_MatchesTextEstimate()
        {
            var textOnly = new ChatMessage("user", new string('x', 400), 1);
            // (400 / 4) + 4 = 104 — the pre-existing text heuristic, no image term.
            Assert.Equal(104, LlmClient.EstimateMessageTokens(textOnly));
        }

        [Fact]
        public void EstimateMessageTokens_AddsFlatEstimatePerImagePart()
        {
            var text = new string('x', 400);
            var oneImage = new ChatMessage("user", text, 1, null, null,
                MakeParts(text, "data:image/png;base64,AAAA"));
            var twoImages = new ChatMessage("user", text, 1, null, null,
                MakeParts(text, "data:image/png;base64,AAAA"));
            twoImages.ContentParts!.Add(new JObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JObject { ["url"] = "data:image/png;base64,BBBB" }
            });

            int textTokens = LlmClient.EstimateMessageTokens(new ChatMessage("user", text, 1));
            Assert.Equal(textTokens + LlmClient.ImageTokenEstimatePerPart,
                LlmClient.EstimateMessageTokens(oneImage));
            Assert.Equal(textTokens + 2 * LlmClient.ImageTokenEstimatePerPart,
                LlmClient.EstimateMessageTokens(twoImages));
        }

        // ── End-to-end: request JSON actually sent over the wire ─────────────

        [Fact]
        public async Task SendMessageAsync_WithImageBase64_SendsContentPartsArray()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);
            const string dataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==";

            await SendAndAwaitAsync(client, "what is in this image?", dataUri);

            var body = JObject.Parse(server.RequestBodies.Single());
            var messages = (JArray)body["messages"]!;
            var userMsg = messages.Last!;
            Assert.Equal("user", (string?)userMsg["role"]);

            // content must be the OpenAI parts array llama-server's
            // oaicompat parser expects: [{type:text,text},{type:image_url,image_url:{url}}]
            var content = Assert.IsType<JArray>(userMsg["content"]);
            Assert.Equal(2, content.Count);
            Assert.Equal("text", (string?)content[0]["type"]);
            Assert.Equal("what is in this image?", (string?)content[0]["text"]);
            Assert.Equal("image_url", (string?)content[1]["type"]);
            Assert.Equal(dataUri, (string?)content[1]["image_url"]?["url"]);
        }

        [Fact]
        public async Task SendMessageAsync_TextOnly_SendsFlatStringContent()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);

            await SendAndAwaitAsync(client, "plain text question", imageBase64: null);

            var body = JObject.Parse(server.RequestBodies.Single());
            var userMsg = ((JArray)body["messages"]!).Last!;
            // Text-only behavior unchanged: content is a flat string, not an array.
            Assert.Equal(JTokenType.String, userMsg["content"]!.Type);
            Assert.Equal("plain text question", (string?)userMsg["content"]);
        }

        [Fact]
        public async Task StagePendingImage_Accumulates_AllImagesAttachToOneMessageInOrder()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);

            // Two staged images (e.g. a rasterized PDF page range) → ONE message with
            // a text part followed by both image parts, in staging order.
            client.StagePendingImage("data:image/png;base64,PAGEONE");
            client.StagePendingImage("data:image/png;base64,PAGETWO");
            await SendAndAwaitAsync(client, "summarize these pages", imageBase64: null);
            await SendAndAwaitAsync(client, "follow-up (text-only expected)", imageBase64: null);

            var first = JObject.Parse(server.RequestBodies[0]);
            var user = ((JArray)first["messages"]!).Last(m => (string?)m["role"] == "user");
            var content = Assert.IsType<JArray>(user["content"]);
            Assert.Equal(3, content.Count);
            Assert.Equal("text", (string?)content[0]["type"]);
            Assert.Equal("data:image/png;base64,PAGEONE", (string?)content[1]["image_url"]?["url"]);
            Assert.Equal("data:image/png;base64,PAGETWO", (string?)content[2]["image_url"]?["url"]);

            // Both consumed — the follow-up send is flat text again.
            var second = JObject.Parse(server.RequestBodies[1]);
            var secondUser = ((JArray)second["messages"]!).Last(m => (string?)m["role"] == "user");
            Assert.Equal(JTokenType.String, secondUser["content"]!.Type);
        }

        [Fact]
        public async Task StagePendingImage_Null_ClearsAllStagedImages()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);

            client.StagePendingImage("data:image/png;base64,AAAA");
            client.StagePendingImage("data:image/png;base64,BBBB");
            client.StagePendingImage(null); // clear everything staged
            await SendAndAwaitAsync(client, "should be text-only", imageBase64: null);

            var body = JObject.Parse(server.RequestBodies.Single());
            var user = ((JArray)body["messages"]!).Last(m => (string?)m["role"] == "user");
            Assert.Equal(JTokenType.String, user["content"]!.Type);
        }

        [Fact]
        public async Task StagePendingImage_AttachesToNextSend_AndIsConsumedOnce()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);
            const string dataUri = "data:image/jpeg;base64,/9j/4AAQSkZJRg==";

            client.StagePendingImage(dataUri);
            await SendAndAwaitAsync(client, "first send (image expected)", imageBase64: null);
            await SendAndAwaitAsync(client, "second send (text-only expected)", imageBase64: null);

            Assert.Equal(2, server.RequestBodies.Count);

            var first = JObject.Parse(server.RequestBodies[0]);
            var firstUser = ((JArray)first["messages"]!)
                .Last(m => (string?)m["role"] == "user");
            var content = Assert.IsType<JArray>(firstUser["content"]);
            Assert.Equal(dataUri, (string?)content[1]["image_url"]?["url"]);

            var second = JObject.Parse(server.RequestBodies[1]);
            var secondUser = ((JArray)second["messages"]!)
                .Last(m => (string?)m["role"] == "user");
            // Staged image consumed by the first send — the second is flat text again.
            Assert.Equal(JTokenType.String, secondUser["content"]!.Type);
        }

        // ── Saved-image paths recorded alongside image parts ──────────────────
        //
        // Every image part in history must carry the path of its saved copy (or null), in
        // order, so a later step can swap the base64 for a pointer at the file.

        [Fact]
        public async Task StagePendingImage_WithPaths_RecordsImagePathsAlignedWithImageParts()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);

            client.StagePendingImage("data:image/png;base64,ONE", @"C:\imgs\001-one.png");
            client.StagePendingImage("data:image/png;base64,TWO", null);           // save failed
            client.StagePendingImage("data:image/png;base64,THREE", @"C:\imgs\003-three.png");
            await SendAndAwaitAsync(client, "look at these", imageBase64: null);

            var msg = client.HistorySnapshotForTests().Single(m => m.Role == "user" && m.ContentParts != null);
            Assert.Equal(new[] { "ONE", "TWO", "THREE" },
                ImageUrls(msg).Select(u => u.Substring(u.IndexOf(',') + 1)).ToArray());
            Assert.Equal(new string?[] { @"C:\imgs\001-one.png", null, @"C:\imgs\003-three.png" }, msg.ImagePaths!.ToArray());
        }

        [Fact]
        public async Task StagePendingImage_OneArg_RecordsNullPathEntries()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);

            client.StagePendingImage("data:image/png;base64,AAAA");
            client.StagePendingImage("data:image/png;base64,BBBB");
            await SendAndAwaitAsync(client, "two images", imageBase64: "data:image/png;base64,ARG");

            var msg = client.HistorySnapshotForTests().Single(m => m.Role == "user" && m.ContentParts != null);
            Assert.Equal(3, ImageUrls(msg).Count);          // argument first, then the staged two
            Assert.Equal(new string?[] { null, null, null }, msg.ImagePaths!.ToArray());
        }

        [Fact]
        public void WithText_CarriesImagePathsAndRebuildsParts()
        {
            var paths = new[] { @"C:\imgs\001-a.png" };
            var msg = new ChatMessage("user", "original", 3, null, null,
                MakeParts("original", "data:image/png;base64,AAAA"), paths);

            var rewritten = msg.WithText("trimmed");

            Assert.Equal("trimmed", rewritten.Content);
            Assert.Equal(3, rewritten.Turn);
            Assert.Equal("trimmed", (string?)rewritten.ContentParts![0]["text"]);
            Assert.Single(ImageUrls(rewritten));
            Assert.Equal(paths, rewritten.ImagePaths);
        }

        [Fact]
        public async Task MicroCompact_RewritingAnImageMessage_PreservesImagePaths()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);

            // Pinned task prompt, then an image-bearing [READ:] message, then a re-read of the
            // same file — which makes the image message stale, so Pass 3 rewrites it.
            await SendAndAwaitAsync(client, "the task", imageBase64: null);
            client.StagePendingImage("data:image/png;base64,PIC", @"C:\imgs\001-pic.png");
            await SendAndAwaitAsync(client, "[READ:a.cs]\n" + new string('a', 2000), imageBase64: null);
            await SendAndAwaitAsync(client, "[READ:a.cs]\n" + new string('b', 2000), imageBase64: null);

            await client.MicroCompactToolResultsAsync(force: true, neededTokens: 0);

            var msg = client.HistorySnapshotForTests().Single(m => m.ContentParts != null);
            Assert.Contains("[file content trimmed]", msg.Content, StringComparison.Ordinal); // it WAS rewritten
            Assert.Single(ImageUrls(msg));
            Assert.Equal(new[] { @"C:\imgs\001-pic.png" }, msg.ImagePaths);
        }

        // ── DropImagesFromHistory (end-of-turn image release) ──────────────────
        //
        // After a turn, image parts are replaced with placeholder lines naming each saved copy,
        // so later requests stop resending the base64.

        [Fact]
        public async Task DropImagesFromHistory_ReplacesImagesWithPlaceholdersInOrder()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);

            await SendAndAwaitAsync(client, "text-only first", imageBase64: null);
            client.StagePendingImage("data:image/png;base64,ONE", @"C:\imgs\001-one.png");
            client.StagePendingImage("data:image/png;base64,TWO", null);           // save failed
            client.StagePendingImage("data:image/png;base64,THREE", @"C:\imgs\003-three.png");
            await SendAndAwaitAsync(client, "look at these", imageBase64: null);

            var before = client.HistorySnapshotForTests();
            var original = before.Single(m => m.ContentParts != null);
            int index = before.ToList().IndexOf(original);
            var textOnlyBefore = before.Where(m => m.ContentParts == null).ToList();

            Assert.Equal(3, client.DropImagesFromHistory());

            var after = client.HistorySnapshotForTests();
            Assert.Equal(before.Count, after.Count);
            var msg = after[index];
            Assert.Null(msg.ContentParts);
            Assert.Null(msg.ImagePaths);
            Assert.Equal(original.Role, msg.Role);
            Assert.Equal(original.Turn, msg.Turn);
            Assert.Equal(
                "look at these\n" +
                "[image removed from context — saved at C:\\imgs\\001-one.png]\n" +
                "[image removed from context — not saved]\n" +
                "[image removed from context — saved at C:\\imgs\\003-three.png]",
                msg.Content);

            // Text-only messages are the same instances, untouched.
            Assert.All(textOnlyBefore, m => Assert.Contains(m, after));
            Assert.DoesNotContain(after, m => m.ContentParts != null);
        }

        [Fact]
        public async Task DropImagesFromHistory_SecondCallReturnsZero_AndTextOnlyHistoryReturnsZero()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);

            await SendAndAwaitAsync(client, "no images here", imageBase64: null);
            Assert.Equal(0, client.DropImagesFromHistory());

            await SendAndAwaitAsync(client, "one image", imageBase64: "data:image/png;base64,ARG");
            Assert.Equal(1, client.DropImagesFromHistory());
            var once = client.HistorySnapshotForTests();

            Assert.Equal(0, client.DropImagesFromHistory()); // idempotent
            Assert.Equal(once, client.HistorySnapshotForTests());
            Assert.Contains(once, m => m.Content == "one image\n[image removed from context — not saved]");
        }

        [Fact]
        public async Task DropImagesFromHistory_NextRequestCarriesPlaceholderAndNoImageUrl()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);

            client.StagePendingImage("data:image/png;base64,PIC", @"C:\imgs\001-pic.png");
            await SendAndAwaitAsync(client, "what is this?", imageBase64: null);
            Assert.Contains("image_url", server.RequestBodies[0], StringComparison.Ordinal); // precondition

            Assert.Equal(1, client.DropImagesFromHistory());
            await SendAndAwaitAsync(client, "and now?", imageBase64: null);

            var body = server.RequestBodies[1];
            Assert.DoesNotContain("image_url", body, StringComparison.Ordinal);
            Assert.DoesNotContain("base64,PIC", body, StringComparison.Ordinal);
            var earlierUser = ((JArray)JObject.Parse(body)["messages"]!)
                .First(m => (string?)m["role"] == "user");
            Assert.Equal(JTokenType.String, earlierUser["content"]!.Type);
            Assert.Equal("what is this?\n[image removed from context — saved at C:\\imgs\\001-pic.png]",
                (string?)earlierUser["content"]);
        }

        [Fact]
        public async Task DropImagesFromHistory_AfterGuardAbortedSend_ReleasesTheUnsentImages()
        {
            // The pre-send guard aborts before any POST but the image message stays in history;
            // releasing it must still work (and leaves the history small enough to send).
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);
            for (int i = 0; i < ImagesPastCeiling; i++)
                client.StagePendingImage($"data:image/png;base64,PAGE{i}", $@"C:\imgs\{i:000}-p.png");
            var (error, _) = await SendCapturingErrorAsync(client, "summarize these pages");
            Assert.NotNull(error);
            Assert.Empty(server.RequestBodies);

            Assert.Equal(ImagesPastCeiling, client.DropImagesFromHistory());

            var (retryError, _) = await SendCapturingErrorAsync(client, "try again");
            Assert.Null(retryError);
            Assert.DoesNotContain("image_url", server.RequestBodies.Single(), StringComparison.Ordinal);
        }

        private static List<string> ImageUrls(ChatMessage msg) =>
            msg.ContentParts!
                .Where(p => (string?)p["type"] == "image_url")
                .Select(p => (string)p["image_url"]!["url"]!)
                .ToList();

        // ── Pre-send context guard counts image parts ─────────────────────────

        //
        // Images live in ContentParts, not Content. The pre-send guard in SendMessageAsync
        // ("Cannot fit in context window") used the text-only estimator, so a message
        // carrying many page images passed it and the server rejected the request. With
        // FakeLlmOptions the ceiling is ManualContextSize (32,768); 40 images at
        // ImageTokenEstimatePerPart (1,024) each are well past it while the text is tiny.

        private const int ImagesPastCeiling = 40;

        [Fact]
        public async Task ContextGuard_FirstTurn_ImagesPastCeiling_AbortsWithoutRequest()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);
            for (int i = 0; i < ImagesPastCeiling; i++)
                client.StagePendingImage($"data:image/png;base64,PAGE{i}");

            var (error, tokens) = await SendCapturingErrorAsync(client, "summarize these pages");

            Assert.NotNull(error);
            Assert.Contains("Context window is full", error!.Message, StringComparison.Ordinal);
            Assert.Contains("Cannot fit in context window", tokens, StringComparison.Ordinal);
            Assert.Empty(server.RequestBodies); // aborted before any chat POST
        }

        [Fact]
        public async Task ContextGuard_FirstTurn_SameTextWithoutImages_Sends()
        {
            using var server = new FakeSseServer();
            var (client, _) = CreateConfiguredClient(server);

            var (error, tokens) = await SendCapturingErrorAsync(client, "summarize these pages");

            Assert.Null(error);
            Assert.DoesNotContain("Cannot fit in context window", tokens, StringComparison.Ordinal);
            Assert.Single(server.RequestBodies);
        }

        // The n_past branch: after a server count exists, only messages added since
        // _lastServerCountIndex are estimated — the new image-bearing message among them.
        [Fact]
        public async Task ContextGuard_AfterServerCount_ImagesPastCeiling_AbortsWithoutRequest()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(TextSseWithNPast(1_000));
            var (client, _) = CreateConfiguredClient(server);

            await SendAndAwaitAsync(client, "first turn, text only", imageBase64: null);
            Assert.Equal(1_000, client.LastContextUsed); // precondition: the n_past branch is live

            for (int i = 0; i < ImagesPastCeiling; i++)
                client.StagePendingImage($"data:image/png;base64,PAGE{i}");
            var (error, tokens) = await SendCapturingErrorAsync(client, "now these pages");

            Assert.NotNull(error);
            Assert.Contains("Cannot fit in context window", tokens, StringComparison.Ordinal);
            Assert.Single(server.RequestBodies); // only the first turn reached the server
        }

        [Fact]
        public async Task ContextGuard_AfterServerCount_SameTextWithoutImages_Sends()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(TextSseWithNPast(1_000));
            var (client, _) = CreateConfiguredClient(server);

            await SendAndAwaitAsync(client, "first turn, text only", imageBase64: null);
            Assert.Equal(1_000, client.LastContextUsed);

            var (error, tokens) = await SendCapturingErrorAsync(client, "now these pages");

            Assert.Null(error);
            Assert.DoesNotContain("Cannot fit in context window", tokens, StringComparison.Ordinal);
            Assert.Equal(2, server.RequestBodies.Count);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>A text reply whose terminal chunk carries llama-server timings with n_past,
        /// so the client records LastContextUsed and anchors _lastServerCountIndex.</summary>
        private static string TextSseWithNPast(int nPast) =>
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]," +
            "\"timings\":{\"prompt_n\":" + nPast + ",\"cache_n\":0,\"n_past\":" + nPast +
            ",\"prompt_ms\":10.0,\"predicted_n\":5,\"predicted_ms\":50.0,\"n_ctx\":32768}}\n\n" +
            "data: [DONE]\n\n";

        /// <summary>Sends and returns the onError exception (null on success) plus all streamed
        /// onToken text, so guard tests can assert the abort instead of rethrowing it.</summary>
        private static async Task<(Exception? error, string tokens)> SendCapturingErrorAsync(
            LlmClient client, string message)
        {
            var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tokens = new System.Text.StringBuilder();
            client.IncrementTurn();
            await client.SendMessageAsync(
                message,
                onToken: t => { lock (tokens) tokens.Append(t); },
                onComplete: () => done.TrySetResult(null),
                onError: ex => done.TrySetResult(ex));
            Assert.True(await Task.WhenAny(done.Task, Task.Delay(15000)) == done.Task,
                "SendMessageAsync did not complete within 15s");
            Exception? error = await done.Task;
            lock (tokens) return (error, tokens.ToString());
        }


        private static JArray MakeParts(string text, string imageUrl) => new JArray
        {
            new JObject { ["type"] = "text", ["text"] = text },
            new JObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JObject { ["url"] = imageUrl }
            }
        };

        private static (LlmClient client, FakeLlmOptions options) CreateConfiguredClient(FakeSseServer server)
        {
            var options = new FakeLlmOptions();
            var client = new LlmClient(options);

            // DEVMIND_SERVER_TYPE skips the /v1/models auto-detect probe and
            // ManualContextSize skips the context-size probe, so Configure()
            // performs no HTTP — the only requests the fake server sees are
            // the /chat/completions POSTs under test.
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                client.Configure(server.BaseUrl, apiKey: null);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
            return (client, options);
        }

        private static async Task SendAndAwaitAsync(LlmClient client, string message, string? imageBase64)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.IncrementTurn();
            await client.SendMessageAsync(
                message,
                onToken: _ => { },
                onComplete: () => done.TrySetResult(true),
                onError: ex => done.TrySetException(ex),
                imageBase64: imageBase64);
            Assert.True(await Task.WhenAny(done.Task, Task.Delay(15000)) == done.Task,
                "SendMessageAsync did not complete within 15s");
            await done.Task; // rethrow onError exception, if any
        }

    }
}
