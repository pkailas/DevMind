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
//   * PDFs (magic bytes %PDF-): no pages -> the page count only; pages -> each page rendered
//     and staged as a PNG with the PDF's path and its page number (nothing saved to disk), all
//     or nothing against the per-request cap; the page's placeholder says pages="N".

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

        // ── PDFs ────────────────────────────────────────────────────────────────

        /// <summary>A generated PDF of blank pages, moved into <paramref name="dir"/>.</summary>
        private static string WritePdf(string dir, string name, int pages)
        {
            string temp = PdfTestFiles.WriteTempPdf(612, 792, pages);
            string path = Path.Combine(dir, name);
            File.Move(temp, path);
            return path;
        }

        private static ResponseBlock ViewPdfBlock(string path, string? pages, string id)
            => new ResponseBlock { Type = BlockType.ViewImage, FileName = path, PageSpec = pages, ToolCallId = id, FromToolCall = true };

        private static bool IsPngDataUri(string dataUri)
        {
            const string prefix = "data:image/png;base64,";
            if (!dataUri.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            byte[] bytes = Convert.FromBase64String(dataUri.Substring(prefix.Length));
            return bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';
        }

        [Fact]
        public void ViewImageCall_WithPages_MapsThePageSpec()
        {
            var tc = new ToolCallResult { Name = "view_image", Id = "call_v" };
            tc.Arguments["path"] = @"C:\docs\a.pdf";
            tc.Arguments["pages"] = "2-5";

            var block = Assert.Single(ToolCallMapper.Map(new List<ToolCallResult> { tc }, buildCommand: "dotnet build"));

            Assert.Null(tc.ArgumentError);
            Assert.Equal(BlockType.ViewImage, block.Type);
            Assert.Equal("2-5", block.PageSpec);
        }

        [Fact]
        public async Task Pdf_WithoutPages_ReportsThePageCount_AndStagesNothing()
        {
            using var env = new Env();
            string pdf = WritePdf(env.Host.GetWorkingDirectory(), "doc.pdf", 3);

            var result = await ExecuteAsync(env, ViewPdfBlock(pdf, null, "call_1"));

            Assert.Empty(result.StagedImages);
            Assert.Equal("PDF doc.pdf has 3 page(s). Call view_image again with pages (e.g. \"1\" or \"1-3\").",
                result.ToolResultsByCallId["call_1"]);
        }

        [Fact]
        public async Task Pdf_Pages2To3_StagesTwoPngPages_WithThePdfPathAndTheirPageNumbers()
        {
            using var env = new Env();
            string pdf = WritePdf(env.Host.GetWorkingDirectory(), "doc.pdf", 4);

            var result = await ExecuteAsync(env, ViewPdfBlock(pdf, "2-3", "call_1"));

            Assert.Equal(2, result.StagedImages.Count);
            Assert.All(result.StagedImages, s => Assert.True(IsPngDataUri(s.DataUri), "staged page is not a PNG data: URI"));
            Assert.All(result.StagedImages, s => Assert.Equal(pdf, s.SavedPath));
            Assert.Equal(new int?[] { 2, 3 }, result.StagedImages.Select(s => s.Page).ToArray());
            Assert.Equal("PDF doc.pdf pages 2-3 of 4 attached to your next request (2 image(s)).", result.ToolResultsByCallId["call_1"]);
            Assert.Empty(Directory.GetFiles(env.Host.GetWorkingDirectory(), "*.png"));   // nothing written to disk
        }

        [Fact]
        public async Task Pdf_PagesOnAnImageFile_AreIgnored()
        {
            using var env = new Env();
            string png = WritePng(env.Host.GetWorkingDirectory(), "shot.png");

            var result = await ExecuteAsync(env, ViewPdfBlock(png, "2-3", "call_1"));

            var staged = Assert.Single(result.StagedImages);
            Assert.Equal(png, staged.SavedPath);
            Assert.Null(staged.Page);
        }

        [Fact]
        public async Task Pdf_RangePastThePerRequestCap_IsRefused_AndStagesNothing()
        {
            using var env = new Env();
            string pdf = WritePdf(env.Host.GetWorkingDirectory(), "big.pdf", AgenticExecutor.MaxViewImagesPerRequest + 1);

            var result = await ExecuteAsync(env, ViewPdfBlock(pdf, "all", "call_1"));

            Assert.Empty(result.StagedImages);
            string text = result.ToolResultsByCallId["call_1"];
            Assert.StartsWith("[VIEW_IMAGE ERROR]", text, StringComparison.Ordinal);
            Assert.Contains($"only {AgenticExecutor.MaxViewImagesPerRequest} more can be attached", text, StringComparison.Ordinal);
            Assert.Contains("Nothing was attached", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Pdf_RangePastWhatEarlierCallsLeft_IsRefused_AndStagesNoPage()
        {
            using var env = new Env();
            string dir = env.Host.GetWorkingDirectory();
            var blocks = new List<ResponseBlock>();
            for (int i = 0; i < 8; i++)
                blocks.Add(ViewBlock(WritePng(dir, $"p{i}.png"), $"img_{i}"));
            string pdf = WritePdf(dir, "doc.pdf", 5);
            blocks.Add(ViewPdfBlock(pdf, "1-3", "pdf_1"));   // 3 pages, 2 left: refused whole
            blocks.Add(ViewPdfBlock(pdf, "4-5", "pdf_2"));   // exactly the 2 left: attached

            var result = await ExecuteAsync(env, blocks.ToArray());

            string refused = result.ToolResultsByCallId["pdf_1"];
            Assert.StartsWith("[VIEW_IMAGE ERROR]", refused, StringComparison.Ordinal);
            Assert.Contains("only 2 more can be attached", refused, StringComparison.Ordinal);
            Assert.Equal(AgenticExecutor.MaxViewImagesPerRequest, result.StagedImages.Count);
            Assert.Equal(new int?[] { 4, 5 }, result.StagedImages.Where(s => s.Page.HasValue).Select(s => s.Page).ToArray());
        }

        [Theory]
        [InlineData("7", "Page 7 is out of range")]
        [InlineData("2-9", "out of range")]
        [InlineData("abc", "Invalid page 'abc'")]
        [InlineData("3-2", "Invalid page range")]
        public async Task Pdf_BadOrOutOfRangePages_AreAnError_AndStageNothing(string spec, string expected)
        {
            using var env = new Env();
            string pdf = WritePdf(env.Host.GetWorkingDirectory(), "doc.pdf", 3);

            var result = await ExecuteAsync(env, ViewPdfBlock(pdf, spec, "call_1"));

            Assert.Empty(result.StagedImages);
            string text = result.ToolResultsByCallId["call_1"];
            Assert.StartsWith("[VIEW_IMAGE ERROR]", text, StringComparison.Ordinal);
            Assert.Contains(expected, text, StringComparison.Ordinal);
            Assert.EndsWith("Nothing was attached.", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CorruptPdf_IsAnError_AndStagesNothing()
        {
            using var env = new Env();
            string bad = Path.Combine(env.Host.GetWorkingDirectory(), "bad.pdf");
            File.WriteAllText(bad, "%PDF-1.4\nthis is not really a pdf");

            var result = await ExecuteAsync(env, ViewPdfBlock(bad, "1", "call_1"));

            Assert.Empty(result.StagedImages);
            string text = result.ToolResultsByCallId["call_1"];
            Assert.StartsWith("[VIEW_IMAGE ERROR]", text, StringComparison.Ordinal);
            Assert.EndsWith("Nothing was attached.", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task FileNamedPdf_ThatIsNotAPdf_IsNotAnImage()
        {
            using var env = new Env();
            string fake = Path.Combine(env.Host.GetWorkingDirectory(), "fake.pdf");
            File.WriteAllText(fake, "plain text with a pdf extension");

            var result = await ExecuteAsync(env, ViewPdfBlock(fake, "1", "call_1"));

            Assert.Empty(result.StagedImages);
            string text = result.ToolResultsByCallId["call_1"];
            Assert.StartsWith("[VIEW_IMAGE ERROR]", text, StringComparison.Ordinal);
            Assert.Contains("not an image", text, StringComparison.Ordinal);
        }

        private static ChatMessage PdfPageMessage(string pdf)
        {
            var parts = new JArray
            {
                new JObject { ["type"] = "text", ["text"] = "look" },
                new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = "data:image/png;base64,AAAA" } },
                new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = "data:image/png;base64,BBBB" } },
            };
            return new ChatMessage("user", "look", 1, null, null, parts,
                new[] { pdf, @"C:\shots\plain.png" }, new[] { true, true }, new int?[] { 3, null });
        }

        [Fact]
        public void WithText_CarriesImagePages()
        {
            var msg = PdfPageMessage(@"C:\docs\spec.pdf");

            var rewritten = msg.WithText("shorter");

            Assert.Equal(new int?[] { 3, null }, rewritten.ImagePages);
            Assert.Equal(msg.ImagePaths, rewritten.ImagePaths);
        }

        [Fact]
        public void ReleasedPdfPage_PlaceholderNamesThePageAndThePdf()
        {
            const string pdf = @"C:\docs\spec.pdf";
            string pagePlaceholder = $"[image removed from context — page 3 of {pdf}; call view_image with pages=\"3\" to see it again]";
            string imagePlaceholder = @"[image removed from context — saved at C:\shots\plain.png; call view_image to see it again]";

            var transient = PdfPageMessage(pdf).WithText("look").WithTransientImagesReleased(out int released);
            Assert.Equal(2, released);
            Assert.Equal("look\n" + pagePlaceholder + "\n" + imagePlaceholder, transient.Content);

            var dropped = PdfPageMessage(pdf).WithImagesReleased(out int dropCount);
            Assert.Equal(2, dropCount);
            Assert.Contains(pagePlaceholder, dropped.Content, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Headless_ViewedPdfPages_RideTheNextRequestOnly_ThenTheirPagePlaceholders()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"devmind_viewpdf_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                string pdf = WritePdf(dir, "manual.pdf", 4);

                using var server = new FakeSseServer();
                server.SseQueue.Add(FakeSseServer.BuildToolCallSse("view_image",
                    new JObject { ["path"] = pdf, ["pages"] = "2-3" }.ToString(Newtonsoft.Json.Formatting.None)));
                server.SseQueue.Add(FakeSseServer.BuildToolCallSse("scratchpad", "{\"content\":\"read them\"}"));
                server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));

                string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
                TextWriter prevOut = Console.Out;
                Console.SetOut(new StringWriter());
                try
                {
                    var result = await HeadlessAgent.RunAsync(
                        "Read pages 2-3 of the manual.",
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
                var pages = ImageUrls(server.RequestBodies[1]);
                Assert.Equal(2, pages.Count);
                Assert.All(pages, u => Assert.True(IsPngDataUri(u), "page is not a PNG data: URI"));
                Assert.Empty(ImageUrls(server.RequestBodies[2]));
                Assert.Equal("PDF manual.pdf pages 2-3 of 4 attached to your next request (2 image(s)).",
                    ToolResults(server.RequestBodies[1]).Single());

                var texts = UserTexts(server.RequestBodies[2]);
                foreach (int page in new[] { 2, 3 })
                {
                    string placeholder = $"[image removed from context — page {page} of {pdf}; call view_image with pages=\"{page}\" to see it again]";
                    Assert.Contains(texts, t => t.Contains(placeholder, StringComparison.Ordinal));
                }
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
