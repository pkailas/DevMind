// File: MultimodalTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the multimodal (image) input path:
//   * ChatMessage.ContentParts construction (5-param vs 6-param constructor)
//   * LlmClient.RebuildContentParts — compaction must not silently drop images
//   * LlmClient.EstimateMessageTokens — budget math must count image parts
//   * End-to-end: SendMessageAsync against a fake SSE server, asserting the
//     ACTUAL request JSON — content is an OpenAI-style parts array (text +
//     image_url) for image sends, a flat string for text-only sends, and
//     StagePendingImage is consumed exactly once.
//
// The fake server is a raw TcpListener (not HttpListener) so the tests run
// without Windows URL-ACL reservations. It answers every POST with a minimal
// SSE stream and records each request body for assertions.

using System.Net;
using System.Net.Sockets;
using System.Text;
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

        // ── Helpers ───────────────────────────────────────────────────────────

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

        /// <summary>
        /// Minimal single-threaded HTTP server over TcpListener: accepts sequential
        /// POSTs, records each body, and replies with a fixed SSE stream
        /// (one content delta + [DONE]). TcpListener binds an ephemeral loopback
        /// port, so no admin rights / URL ACLs are needed.
        /// </summary>
        private sealed class FakeSseServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _acceptLoop;

            public List<string> RequestBodies { get; } = new();
            public string BaseUrl { get; }

            public FakeSseServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                BaseUrl = $"http://127.0.0.1:{port}/v1";
                _acceptLoop = Task.Run(AcceptLoopAsync);
            }

            private async Task AcceptLoopAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient tcp;
                    try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (SocketException) { return; }

                    using (tcp)
                    using (var stream = tcp.GetStream())
                    {
                        var request = await ReadRequestAsync(stream);
                        if (request == null) continue;
                        var (method, body) = request.Value;

                        // Record only the chat POSTs under test — the client also sends
                        // health/models GET probes, which must not pollute assertions.
                        bool isChatPost = method == "POST" && body.Length > 0;
                        if (isChatPost)
                        {
                            lock (RequestBodies) RequestBodies.Add(body);
                        }

                        byte[] payload;
                        string contentType;
                        if (isChatPost)
                        {
                            const string sse =
                                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
                                "data: [DONE]\n\n";
                            payload = Encoding.UTF8.GetBytes(sse);
                            contentType = "text/event-stream";
                        }
                        else
                        {
                            payload = Encoding.UTF8.GetBytes("{\"data\":[]}");
                            contentType = "application/json";
                        }
                        string headers =
                            "HTTP/1.1 200 OK\r\n" +
                            $"Content-Type: {contentType}\r\n" +
                            $"Content-Length: {payload.Length}\r\n" +
                            "Connection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
                        await stream.WriteAsync(payload);
                        await stream.FlushAsync();
                    }
                }
            }

            private static async Task<(string method, string body)?> ReadRequestAsync(NetworkStream stream)
            {
                // Read until end of headers.
                var headerBuf = new MemoryStream();
                var one = new byte[1];
                while (true)
                {
                    int n = await stream.ReadAsync(one.AsMemory(0, 1));
                    if (n == 0) return null; // connection closed (e.g. probe)
                    headerBuf.WriteByte(one[0]);
                    if (headerBuf.Length >= 4)
                    {
                        var b = headerBuf.GetBuffer();
                        long len = headerBuf.Length;
                        if (b[len - 4] == '\r' && b[len - 3] == '\n' && b[len - 2] == '\r' && b[len - 1] == '\n')
                            break;
                    }
                }

                string headerText = Encoding.ASCII.GetString(headerBuf.ToArray());
                string[] headerLines = headerText.Split("\r\n");
                string method = headerLines[0].Split(' ')[0]; // "POST /v1/chat/completions HTTP/1.1"
                int contentLength = 0;
                foreach (string line in headerLines)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line.Substring(15).Trim());
                }
                if (contentLength == 0) return (method, string.Empty);

                var bodyBytes = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int n = await stream.ReadAsync(bodyBytes.AsMemory(read, contentLength - read));
                    if (n == 0) break;
                    read += n;
                }
                return (method, Encoding.UTF8.GetString(bodyBytes, 0, read));
            }

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
                try { _acceptLoop.Wait(2000); } catch { /* accept loop teardown races are fine */ }
                _cts.Dispose();
            }
        }

        /// <summary>Minimal ILlmOptions: manual context size (no probes), all compaction
        /// machinery effectively idle so history stays exactly what the test appends.</summary>
        private sealed class FakeLlmOptions : ILlmOptions
        {
            public string SystemPrompt => "You are a test assistant.";
            public string ModelName => "test-model";
            public int RequestTimeoutMinutes => 1;
            public int FirstTokenTimeoutMinutes => 1;
            public bool ShowDebugOutput => false;
            public bool ShowContextBudget => false;
            public bool ShowLlmThinking => false;
            public ContextEvictionMode ContextEviction => ContextEvictionMode.Off;
            public int ManualContextSize => 32768;
            public LlmServerType ServerType => LlmServerType.LlamaServer;
            public string CustomContextEndpoint => null!; // Core is nullable-oblivious; null means "no custom endpoint"
            public int MicroCompactThreshold => 99;
            public bool MicroCompactSummarize => false;
            public bool MicroCompactBrainwash => false;
            public bool AlwaysConfirmPatch => false;
            public int AgenticLoopMaxDepth => 25;
            public int AgenticContextLimitPercent => 0;
        }
    }
}
