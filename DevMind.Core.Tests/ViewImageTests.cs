// File: ViewImageTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// view_image — the model-callable tool that lets the agent look at an image file itself.
//   * Mapping: the tool call becomes a BlockType.ViewImage block carrying the path.
//   * Executor: a valid image is validated (magic bytes, 10 MB cap) and handed back in
//     ExecutionResult.StagedImages with the file's own path; a non-image, missing or oversized
//     file is a text error that stages nothing; past MaxViewImagesPerRequest in one response
//     the call is refused.
//   * End to end (headless, real LlmClient over FakeSseServer): the image rides the NEXT
//     request only; the request after that carries its placeholder instead.

using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class ViewImageTests
    {
        // A PNG signature is all the sniffer reads; the rest only has to exist.
        private static readonly byte[] PngBytes =
            { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };

        private static string WritePng(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, PngBytes);
            return path;
        }

        private static ResponseBlock ViewBlock(string path, string id)
            => new ResponseBlock { Type = BlockType.ViewImage, FileName = path, ToolCallId = id, FromToolCall = true };

        private static Task<ExecutionResult> ExecuteAsync(Env env, params ResponseBlock[] blocks)
            => new AgenticExecutor(env.Host, new FakeLlmOptions())
                .ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild }, new ResponseOutcome(blocks.ToList()));

        // ── Mapping ─────────────────────────────────────────────────────────────

        [Fact]
        public void ViewImageCall_MapsToAViewImageBlockWithThePath()
        {
            var tc = new ToolCallResult { Name = "view_image", Id = "call_v" };
            tc.Arguments["path"] = @"C:\shots\a.png";

            var block = Assert.Single(ToolCallMapper.Map(new List<ToolCallResult> { tc }, buildCommand: "dotnet build"));

            Assert.Equal(BlockType.ViewImage, block.Type);
            Assert.Equal(@"C:\shots\a.png", block.FileName);
            Assert.Equal("call_v", block.ToolCallId);
        }

        [Fact]
        public void ViewImageCall_WithoutAPath_IsAnArgumentError()
        {
            var tc = new ToolCallResult { Name = "view_image", Id = "call_v" };
            tc.Arguments["filename"] = @"C:\shots\a.png";

            ToolCallMapper.Map(new List<ToolCallResult> { tc }, buildCommand: "dotnet build");

            Assert.NotNull(tc.ArgumentError);
            Assert.Contains("'path'", tc.ArgumentError, StringComparison.Ordinal);
        }

        // ── Executor ────────────────────────────────────────────────────────────

        [Fact]
        public async Task ValidPng_IsStagedWithItsMimeAndItsOwnPath()
        {
            using var env = new Env();
            string png = WritePng(env.Host.GetWorkingDirectory(), "shot.png");

            var result = await ExecuteAsync(env, ViewBlock(png, "call_1"));

            var staged = Assert.Single(result.StagedImages);
            Assert.Equal(png, staged.SavedPath);
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(PngBytes), staged.DataUri);
            Assert.Equal("Image shot.png (image/png, 0 KB) attached to your next request.", result.ToolResultsByCallId["call_1"]);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public async Task RelativePath_ResolvesAgainstTheWorkingDirectory_LikeReadFile()
        {
            using var env = new Env();
            string png = WritePng(env.Host.GetWorkingDirectory(), "rel.png");

            var result = await ExecuteAsync(env, ViewBlock("rel.png", "call_1"));

            Assert.Equal(png, Assert.Single(result.StagedImages).SavedPath);
        }

        [Fact]
        public async Task TextFileNamedPng_IsNotAnImage_AndStagesNothing()
        {
            using var env = new Env();
            string fake = Path.Combine(env.Host.GetWorkingDirectory(), "fake.png");
            File.WriteAllText(fake, "this is not an image");

            var result = await ExecuteAsync(env, ViewBlock(fake, "call_1"));

            Assert.Empty(result.StagedImages);
            string text = result.ToolResultsByCallId["call_1"];
            Assert.StartsWith("[VIEW_IMAGE ERROR]", text, StringComparison.Ordinal);
            Assert.Contains("not an image", text, StringComparison.Ordinal);
            Assert.Contains("Nothing was attached", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task MissingFile_IsNotFound_AndStagesNothing()
        {
            using var env = new Env();
            string missing = Path.Combine(env.Host.GetWorkingDirectory(), "nope.png");

            var result = await ExecuteAsync(env, ViewBlock(missing, "call_1"));

            Assert.Empty(result.StagedImages);
            string text = result.ToolResultsByCallId["call_1"];
            Assert.StartsWith("[VIEW_IMAGE ERROR]", text, StringComparison.Ordinal);
            Assert.Contains("file not found", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task FileOverTheSizeCap_IsTooLarge_AndStagesNothing()
        {
            using var env = new Env();
            string big = Path.Combine(env.Host.GetWorkingDirectory(), "big.png");
            var bytes = new byte[ImagePathScanner.MaxImageBytes + 1];
            Array.Copy(PngBytes, bytes, PngBytes.Length);
            File.WriteAllBytes(big, bytes);

            var result = await ExecuteAsync(env, ViewBlock(big, "call_1"));

            Assert.Empty(result.StagedImages);
            string text = result.ToolResultsByCallId["call_1"];
            Assert.StartsWith("[VIEW_IMAGE ERROR]", text, StringComparison.Ordinal);
            Assert.Contains("too large", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CallPastThePerRequestCap_IsRefused_AndStagesNothing()
        {
            using var env = new Env();
            var blocks = new List<ResponseBlock>();
            for (int i = 0; i <= AgenticExecutor.MaxViewImagesPerRequest; i++)
                blocks.Add(ViewBlock(WritePng(env.Host.GetWorkingDirectory(), $"p{i:00}.png"), $"call_{i}"));

            var result = await ExecuteAsync(env, blocks.ToArray());

            Assert.Equal(AgenticExecutor.MaxViewImagesPerRequest, result.StagedImages.Count);
            Assert.DoesNotContain(result.StagedImages, s => s.SavedPath.EndsWith("p10.png", StringComparison.Ordinal));
            string last = result.ToolResultsByCallId[$"call_{AgenticExecutor.MaxViewImagesPerRequest}"];
            Assert.StartsWith("[VIEW_IMAGE ERROR]", last, StringComparison.Ordinal);
            Assert.Contains("too many images for one request — view the rest next", last, StringComparison.Ordinal);
            Assert.Contains("attached to your next request", result.ToolResultsByCallId["call_9"], StringComparison.Ordinal);
        }

        // ── End to end: headless loop, real LlmClient ───────────────────────────

        [Fact]
        public async Task Headless_ViewedImage_RidesTheNextRequestOnly_ThenItsPlaceholder()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"devmind_viewimg_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                string png = WritePng(dir, "screen.png");
                string dataUri = "data:image/png;base64," + Convert.ToBase64String(PngBytes);

                using var server = new FakeSseServer();
                server.SseQueue.Add(FakeSseServer.BuildToolCallSse("view_image", new JObject { ["path"] = png }.ToString(Newtonsoft.Json.Formatting.None)));
                server.SseQueue.Add(FakeSseServer.BuildToolCallSse("scratchpad", "{\"content\":\"looked at it\"}"));
                server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));

                string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
                TextWriter prevOut = Console.Out;
                Console.SetOut(new StringWriter());
                try
                {
                    var result = await HeadlessAgent.RunAsync(
                        "Look at the screenshot.",
                        new HeadlessOptions
                        {
                            RequestTimeoutMinutes = 1,
                            FirstTokenTimeoutMinutes = 1,
                            ManualContextSize = 32768,
                            AgenticLoopMaxDepth = 6,
                        },
                        server.BaseUrl, apiKey: null!,
                        workingDirectory: dir, buildCommand: "dotnet build",
                        ct: CancellationToken.None,
                        promptFilePath: Path.Combine(dir, "no-system-prompt.md"));
                    Assert.Null(result.Error);
                }
                finally
                {
                    Console.SetOut(prevOut);
                    Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
                }

                Assert.Equal(3, server.RequestBodies.Count);
                Assert.Empty(ImageUrls(server.RequestBodies[0]));
                Assert.Equal(new[] { dataUri }, ImageUrls(server.RequestBodies[1]));   // the request after the call
                Assert.Empty(ImageUrls(server.RequestBodies[2]));                       // and never again
                Assert.DoesNotContain(dataUri, server.RequestBodies[2], StringComparison.Ordinal);

                string placeholder = $"[image removed from context — saved at {png}; call view_image to see it again]";
                Assert.Contains(UserTexts(server.RequestBodies[2]), t => t.Contains(placeholder, StringComparison.Ordinal));

                string toolResult = ToolResults(server.RequestBodies[1]).Single();
                Assert.Equal("Image screen.png (image/png, 0 KB) attached to your next request.", toolResult);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private static IEnumerable<JObject> Messages(string body)
            => ((JArray)JObject.Parse(body)["messages"]!).OfType<JObject>();

        private static List<string> ImageUrls(string body)
            => Messages(body)
                .Select(m => m["content"])
                .OfType<JArray>()
                .SelectMany(parts => parts)
                .Where(p => (string?)p["type"] == "image_url")
                .Select(p => (string)p["image_url"]!["url"]!)
                .ToList();

        private static List<string> UserTexts(string body)
            => Messages(body)
                .Where(m => (string?)m["role"] == "user")
                .Select(m => m["content"] is JArray parts
                    ? string.Join("\n", parts.Where(p => (string?)p["type"] == "text").Select(p => (string?)p["text"]))
                    : (string?)m["content"] ?? "")
                .ToList();

        private static List<string> ToolResults(string body)
            => Messages(body)
                .Where(m => (string?)m["role"] == "tool")
                .Select(m => (string)m["content"]!)
                .ToList();
    }
}
