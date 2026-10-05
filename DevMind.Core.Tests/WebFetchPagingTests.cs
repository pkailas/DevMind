// File: WebFetchPagingTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// web_fetch used to cut every page at 8,000 chars with no way to read the rest (a 15-page
// paper converts to ~49,000), and gave the fetcher a fixed 45 s while Docling needs ~1 s a
// page. These tests pin the replacement: 8,000-char pages addressed by an offset, a footer
// that names the next offset, a per-URL cache so paging does not re-fetch, and the
// DEVMIND_FETCH_TIMEOUT_SECONDS deadline.
//
// Like WebToolsFailureMessageTests, they point DEVMIND_FETCH_URL at a real HttpListener on
// loopback (FakeFetcher below) — no external network — and count the requests it sees, which
// is how "served from cache" is observed. The cache is process-wide, so every test starts and
// ends with it empty and on the real clock.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DevMind.Core.Tests
{
    public class WebFetchPagingTests : IDisposable
    {
        private const string Url = "http://docs.example.invalid/paper.pdf";

        private readonly string? _savedFetchUrl = Environment.GetEnvironmentVariable("DEVMIND_FETCH_URL");
        private readonly string? _savedTimeout = Environment.GetEnvironmentVariable("DEVMIND_FETCH_TIMEOUT_SECONDS");
        private FakeFetcher? _fetcher;

        public WebFetchPagingTests() => WebTools.FetchContentCache.Clear();

        public void Dispose()
        {
            _fetcher?.Dispose();
            Environment.SetEnvironmentVariable("DEVMIND_FETCH_URL", _savedFetchUrl);
            Environment.SetEnvironmentVariable("DEVMIND_FETCH_TIMEOUT_SECONDS", _savedTimeout);
            WebTools.FetchContentCache.Clear();
        }

        /// <summary>Starts a fake fetcher answering every request via <paramref name="respond"/>
        /// (called with the 1-based request number) and points DEVMIND_FETCH_URL at it.</summary>
        private FakeFetcher Serve(Func<int, FakeResponse> respond)
        {
            _fetcher = new FakeFetcher(respond);
            Environment.SetEnvironmentVariable("DEVMIND_FETCH_URL", _fetcher.BaseUrl);
            return _fetcher;
        }

        private FakeFetcher ServeContent(string content) => Serve(_ => FakeResponse.Content(content));

        /// <summary>Deterministic text where every slice is distinct, so a wrong offset shows.</summary>
        private static string Text(int length)
        {
            var rng = new Random(12345);
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++) sb.Append((char)('a' + rng.Next(26)));
            return sb.ToString();
        }

        // ── Paging ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task FirstPage_OfLongContent_IsTheFirst8000Chars_WithAContinueFooterNamingOffset8000()
        {
            string content = Text(20_000);
            ServeContent(content);

            string result = await WebTools.WebFetchAsync(Url);

            Assert.Equal(content.Substring(0, 8000) +
                "\n\n[web_fetch: showing characters 0-8000 of 20000. Call web_fetch again with the same url and offset=8000 to continue.]",
                result);
        }

        [Fact]
        public async Task SecondPage_IsServedFromTheCache_WithoutASecondFetch()
        {
            string content = Text(20_000);
            var fetcher = ServeContent(content);

            await WebTools.WebFetchAsync(Url);
            string page2 = await WebTools.WebFetchAsync(Url, 8000);

            Assert.Equal(1, fetcher.RequestCount);
            Assert.Equal(content.Substring(8000, 8000) +
                "\n\n[web_fetch: showing characters 8000-16000 of 20000. Call web_fetch again with the same url and offset=16000 to continue.]",
                page2);
        }

        [Fact]
        public async Task LastPage_IsTheRemainder_WithAnEndOfContentFooter()
        {
            string content = Text(20_000);
            ServeContent(content);

            await WebTools.WebFetchAsync(Url);
            string last = await WebTools.WebFetchAsync(Url, 16000);

            Assert.Equal(content.Substring(16000) +
                "\n\n[web_fetch: end of content (characters 16000-20000 of 20000)]",
                last);
        }

        [Theory]
        [InlineData(20_000)]
        [InlineData(25_000)]
        public async Task OffsetAtOrPastTheEnd_SaysSo(int offset)
        {
            ServeContent(Text(20_000));

            await WebTools.WebFetchAsync(Url);
            string result = await WebTools.WebFetchAsync(Url, offset);

            Assert.Equal($"[web_fetch: offset {offset} is past the end of the content (20000 chars)]", result);
        }

        [Fact]
        public async Task NegativeOffset_IsTreatedAsZero()
        {
            string content = Text(20_000);
            var fetcher = ServeContent(content);

            string fromZero = await WebTools.WebFetchAsync(Url, 0);
            string fromNegative = await WebTools.WebFetchAsync(Url, -50);

            Assert.Equal(fromZero, fromNegative);
            // Treated as 0 means a fresh fetch too, not a cache read.
            Assert.Equal(2, fetcher.RequestCount);
        }

        [Fact]
        public async Task ContentThatFitsOnePage_IsReturnedUnchanged_WithNoFooter()
        {
            string content = Text(5_000);
            ServeContent(content);

            Assert.Equal(content, await WebTools.WebFetchAsync(Url));
        }

        [Fact]
        public async Task ContentOfExactlyOnePage_HasNoFooter()
        {
            string content = Text(WebTools.FetchPageChars);
            ServeContent(content);

            Assert.Equal(content, await WebTools.WebFetchAsync(Url));
        }

        [Fact]
        public async Task ThinContentWarning_AppliesOnlyToOffsetZero()
        {
            string content = Text(150);
            ServeContent(content);

            string first = await WebTools.WebFetchAsync(Url);
            string later = await WebTools.WebFetchAsync(Url, 100);

            Assert.Contains("[web_fetch warning: only 150 chars", first);
            Assert.Equal(content.Substring(100) + "\n\n[web_fetch: end of content (characters 100-150 of 150)]", later);
        }

        // ── Cache ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task OffsetZero_AlwaysFetchesFresh()
        {
            var fetcher = ServeContent(Text(20_000));

            await WebTools.WebFetchAsync(Url);
            await WebTools.WebFetchAsync(Url);

            Assert.Equal(2, fetcher.RequestCount);
        }

        [Fact]
        public async Task OffsetZero_ReplacesTheCachedContent_SoLaterPagesMatchTheFreshFetch()
        {
            string v1 = Text(20_000);
            string v2 = new string('z', 20_000);
            var fetcher = Serve(n => FakeResponse.Content(n == 1 ? v1 : v2));

            await WebTools.WebFetchAsync(Url);
            await WebTools.WebFetchAsync(Url);
            string page2 = await WebTools.WebFetchAsync(Url, 8000);

            Assert.Equal(2, fetcher.RequestCount);
            Assert.StartsWith(v2.Substring(8000, 8000), page2);
        }

        [Fact]
        public async Task LaterPage_OnACacheMiss_FetchesAgain()
        {
            string content = Text(20_000);
            var fetcher = ServeContent(content);

            string page2 = await WebTools.WebFetchAsync(Url, 8000);

            Assert.Equal(1, fetcher.RequestCount);
            Assert.StartsWith(content.Substring(8000, 8000), page2);
        }

        [Fact]
        public async Task CacheEntry_IsReusedInsideTheTtl_AndRefetchedAfterItExpires()
        {
            DateTime now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
            WebTools.FetchContentCache.Clock = () => now;
            string content = Text(20_000);
            var fetcher = ServeContent(content);

            await WebTools.WebFetchAsync(Url);

            now += TimeSpan.FromMinutes(14);
            await WebTools.WebFetchAsync(Url, 8000);
            Assert.Equal(1, fetcher.RequestCount);

            now += TimeSpan.FromMinutes(2); // 16 minutes after the fetch: expired
            string page = await WebTools.WebFetchAsync(Url, 8000);
            Assert.Equal(2, fetcher.RequestCount);
            Assert.StartsWith(content.Substring(8000, 8000), page);
        }

        [Fact]
        public async Task ErrorResponses_AreNotCached()
        {
            string content = Text(20_000);
            var fetcher = Serve(n => n == 1 ? FakeResponse.Status(500, "{\"detail\":\"converter crashed\"}") : FakeResponse.Content(content));

            string error = await WebTools.WebFetchAsync(Url);
            string page2 = await WebTools.WebFetchAsync(Url, 8000);

            Assert.StartsWith("[web_fetch error] 500", error);
            Assert.Equal(2, fetcher.RequestCount);
            Assert.StartsWith(content.Substring(8000, 8000), page2);
        }

        [Fact]
        public async Task DetailOn200_IsNotCached()
        {
            string content = Text(20_000);
            var fetcher = Serve(n => n == 1 ? FakeResponse.Status(200, "{\"detail\":\"blocked\"}") : FakeResponse.Content(content));

            string error = await WebTools.WebFetchAsync(Url);
            await WebTools.WebFetchAsync(Url, 8000);

            Assert.StartsWith("[web_fetch error] Fetcher returned an error: blocked", error);
            Assert.Equal(2, fetcher.RequestCount);
        }

        [Fact]
        public async Task EmptyExtraction_IsNotCached()
        {
            string content = Text(20_000);
            var fetcher = Serve(n => FakeResponse.Content(n == 1 ? "   " : content));

            string empty = await WebTools.WebFetchAsync(Url);
            await WebTools.WebFetchAsync(Url, 8000);

            Assert.StartsWith("[web_fetch] No content extracted", empty);
            Assert.Equal(2, fetcher.RequestCount);
        }

        [Fact]
        public void Cache_HoldsAtMost64Entries_EvictingTheOldest()
        {
            var cache = new WebTools.FetchCache();
            DateTime now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
            cache.Clock = () => now;

            for (int i = 0; i < WebTools.FetchCache.MaxEntries + 1; i++)
            {
                cache.Put($"http://x/{i}", "content " + i);
                now += TimeSpan.FromSeconds(1);
            }

            Assert.Equal(WebTools.FetchCache.MaxEntries, cache.Count);
            Assert.False(cache.TryGet("http://x/0", out _));
            Assert.True(cache.TryGet("http://x/1", out string second));
            Assert.Equal("content 1", second);
            Assert.True(cache.TryGet($"http://x/{WebTools.FetchCache.MaxEntries}", out _));
        }

        // ── Timeout ────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null, 180)]
        [InlineData("", 180)]
        [InlineData("soon", 180)]
        [InlineData("1", 10)]
        [InlineData("-5", 10)]
        [InlineData("10", 10)]
        [InlineData("300", 300)]
        [InlineData("600", 600)]
        [InlineData("9999", 600)]
        public void FetchTimeoutSetting_DefaultsTo180_AndIsClampedTo10Through600(string? raw, int expected)
        {
            Assert.Equal(expected, WebTools.ResolveFetchTimeoutSeconds(raw));
        }

        [Fact]
        public async Task FetchTimeoutSetting_IsHonoured_AndNamedInTheTimeoutMessage()
        {
            // 10 s is the smallest value the setting accepts; the fake fetcher would answer
            // only after 40 s, so a pass proves the 10 s deadline (not the 180 s default) fired.
            Environment.SetEnvironmentVariable("DEVMIND_FETCH_TIMEOUT_SECONDS", "10");
            Serve(_ => FakeResponse.Content(Text(100), TimeSpan.FromSeconds(40)));

            var sw = Stopwatch.StartNew();
            string result = await WebTools.WebFetchAsync(Url);
            sw.Stop();

            Assert.StartsWith($"[web_fetch error] Timed out after 10s fetching {Url}", result);
            Assert.Contains("DEVMIND_FETCH_TIMEOUT_SECONDS", result);
            Assert.InRange(sw.Elapsed.TotalSeconds, 9, 30);
        }

        // ── Wiring: ToolCallMapper and AgenticExecutor ─────────────────────────

        [Fact]
        public void ToolCallMapper_MapsOffset_AndTheCallId()
        {
            var tc = new ToolCallResult
            {
                Id = "call_7", Name = "web_fetch",
                Arguments = new Dictionary<string, string> { ["url"] = Url, ["offset"] = "16000" },
            };

            var block = Assert.Single(ToolCallMapper.Map(new List<ToolCallResult> { tc }, null!));

            Assert.Equal(BlockType.WebFetch, block.Type);
            Assert.Equal(Url, block.Url);
            Assert.Equal(16000, block.FetchOffset);
            Assert.Equal("call_7", block.ToolCallId);
        }

        [Fact]
        public void ToolCallMapper_WithoutOffset_MapsZero()
        {
            var tc = new ToolCallResult { Id = "c", Name = "web_fetch", Arguments = new Dictionary<string, string> { ["url"] = Url } };

            Assert.Equal(0, Assert.Single(ToolCallMapper.Map(new List<ToolCallResult> { tc }, null!)).FetchOffset);
        }

        [Fact]
        public void ToolRegistry_AdvertisesAnOptionalIntegerOffset()
        {
            var tool = ToolRegistry.BuildToolsArray().Single(t => (string?)t["function"]?["name"] == "web_fetch");
            var parameters = tool["function"]!["parameters"]!;

            Assert.Equal("integer", (string?)parameters["properties"]?["offset"]?["type"]);
            Assert.DoesNotContain("offset", parameters["required"]!.Select(r => r.ToString()));
        }

        [Fact]
        public async Task AgenticExecutor_KeepsTwoPagesOfOneUrlInOneTurnDistinct()
        {
            var host = new FakeHost(Path.GetTempPath()) { WebFetch = (url, offset) => $"page of {url} at {offset}" };
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            var calls = new List<ToolCallResult>
            {
                new ToolCallResult { Id = "a", Name = "web_fetch", Arguments = new Dictionary<string, string> { ["url"] = Url } },
                new ToolCallResult { Id = "b", Name = "web_fetch", Arguments = new Dictionary<string, string> { ["url"] = Url, ["offset"] = "8000" } },
            };
            var blocks = ToolCallMapper.Map(calls, null!);

            ExecutionResult result = await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild }, new ResponseOutcome(blocks));

            Assert.Equal($"page of {Url} at 0", LoopHelpers.BuildToolResultContent(calls[0], result, blocks));
            Assert.Equal($"page of {Url} at 8000", LoopHelpers.BuildToolResultContent(calls[1], result, blocks));
            // The per-turn map keeps both as well (training log, fallback lookup).
            Assert.Equal($"page of {Url} at 0", result.ToolResultContents[Url]);
            Assert.Equal($"page of {Url} at 8000", result.ToolResultContents[LoopHelpers.WebFetchResultKey(Url, 8000)]);
        }

        [Fact]
        public void ToolResultFallback_WithoutACallId_FindsThePageByUrlAndOffset()
        {
            var result = new ExecutionResult();
            result.ToolResultContents[Url] = "first";
            result.ToolResultContents[LoopHelpers.WebFetchResultKey(Url, 8000)] = "second";
            var tc = new ToolCallResult { Name = "web_fetch", Arguments = new Dictionary<string, string> { ["url"] = Url, ["offset"] = "8000" } };

            Assert.Equal("second", LoopHelpers.BuildToolResultContent(tc, result, null!));
        }

        // ── Fake fetcher ───────────────────────────────────────────────────────

        private sealed class FakeResponse
        {
            public int StatusCode { get; init; } = 200;
            public string Body { get; init; } = "";
            public TimeSpan Delay { get; init; }

            public static FakeResponse Content(string content, TimeSpan delay = default)
                => new FakeResponse { Body = JsonSerializer.Serialize(new { content }), Delay = delay };

            public static FakeResponse Status(int status, string body) => new FakeResponse { StatusCode = status, Body = body };
        }

        /// <summary>A loopback HttpListener standing in for the fetcher service: answers each
        /// request from a script and counts how many it saw.</summary>
        private sealed class FakeFetcher : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private int _requests;

            public string BaseUrl { get; }
            public int RequestCount => Volatile.Read(ref _requests);

            public FakeFetcher(Func<int, FakeResponse> respond)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                BaseUrl = $"http://127.0.0.1:{port}";
                _listener.Prefixes.Add(BaseUrl + "/");
                _listener.Start();

                _ = Task.Run(async () =>
                {
                    while (_listener.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = await _listener.GetContextAsync(); }
                        catch { break; }
                        int n = Interlocked.Increment(ref _requests);
                        _ = Task.Run(() => AnswerAsync(ctx, respond(n)));
                    }
                });
            }

            private async Task AnswerAsync(HttpListenerContext ctx, FakeResponse r)
            {
                try
                {
                    if (r.Delay > TimeSpan.Zero) await Task.Delay(r.Delay, _stop.Token);
                    byte[] body = Encoding.UTF8.GetBytes(r.Body);
                    ctx.Response.StatusCode = r.StatusCode;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
                catch { try { ctx.Response.Abort(); } catch { } }
            }

            public void Dispose()
            {
                _stop.Cancel();
                try { _listener.Stop(); _listener.Close(); } catch { }
                _stop.Dispose();
            }
        }
    }
}
