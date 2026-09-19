// File: WebToolsFailureMessageTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for web_search / web_fetch failure messages: a single
// catch (Exception ex) used to emit the same message for a DOWN backend
// (HttpRequestException: connection refused / DNS) as for an UNPARSEABLE
// response (JsonException), so an agent could not tell "your search backend is
// offline, stop retrying" from "that query was malformed, try another". The
// messages must now be distinguishable by cause, and the final catch-all must
// say the cause is unclassified rather than implying one.
//
// WebTools reads DEVMIND_SEARCH_URL / DEVMIND_FETCH_URL at call time, so each
// test points the env var at either an unreachable address (transport failure)
// or a live garbage-JSON listener (parse failure) and then invokes the tool.

using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    public class WebToolsFailureMessageTests : IDisposable
    {
        private string? _savedSearch = null;
        private string? _savedFetch = null;
        private bool _hadSearch, _hadFetch;

        private const string UnreachableUrl = "http://127.0.0.1:1"; // nothing listens here

        public WebToolsFailureMessageTests()
        {
            _savedSearch = Environment.GetEnvironmentVariable("DEVMIND_SEARCH_URL");
            _savedFetch = Environment.GetEnvironmentVariable("DEVMIND_FETCH_URL");
            _hadSearch = _savedSearch != null;
            _hadFetch = _savedFetch != null;
        }

        public void Dispose()
        {
            Restore("DEVMIND_SEARCH_URL", _savedSearch, _hadSearch);
            Restore("DEVMIND_FETCH_URL", _savedFetch, _hadFetch);
        }

        private static void Restore(string name, string? saved, bool hadValue)
        {
            if (hadValue) Environment.SetEnvironmentVariable(name, saved);
            else Environment.SetEnvironmentVariable(name, null);
        }

        private void PointBothAt(string url)
        {
            Environment.SetEnvironmentVariable("DEVMIND_SEARCH_URL", url);
            Environment.SetEnvironmentVariable("DEVMIND_FETCH_URL", url);
        }

        // ── Transport failure: backend down / connection refused ──────────────

        [Fact]
        public async Task WebSearch_TransportFailure_IsReportedAsTransportNotParse()
        {
            // 127.0.0.1:1 has no listener: the request dies in the socket layer
            // (HttpRequestException). The message must identify a transport
            // failure, not a response-parse problem.
            PointBothAt(UnreachableUrl);
            string result = await WebTools.WebSearchAsync("anything", null);

            Assert.Contains("web_search error", result);
            Assert.Contains("transport", result, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unparseable", result, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task WebFetch_TransportFailure_IsReportedAsTransportNotParse()
        {
            PointBothAt(UnreachableUrl);
            string result = await WebTools.WebFetchAsync("http://example.invalid/page");

            Assert.Contains("web_fetch error", result);
            Assert.Contains("transport", result, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unparseable", result, System.StringComparison.OrdinalIgnoreCase);
        }

        // ── Parse failure: backend up but response is not valid JSON ──────────

        [Fact]
        public async Task WebSearch_ParseFailure_IsReportedAsParseNotTransport()
        {
            // A live listener that returns 200 with a garbage (non-JSON) body:
            // the connection SUCCEEDS and only JsonDocument.Parse fails. The
            // message must identify a parse failure — the opposite of the
            // transport case above.
            PointBothAt(StartGarbageJsonListener());
            string result = await WebTools.WebSearchAsync("anything", null);

            Assert.Contains("web_search error", result);
            Assert.Contains("unparseable", result, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("transport", result, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task WebSearch_TransportAndParseProduceDistinguishableMessages()
        {
            // The core of the finding: two different causes must yield two
            // different messages an agent can act on differently.
            PointBothAt(UnreachableUrl);
            string transportMsg = await WebTools.WebSearchAsync("anything", null);

            PointBothAt(StartGarbageJsonListener());
            string parseMsg = await WebTools.WebSearchAsync("anything", null);

            Assert.NotEqual(transportMsg, parseMsg);
            Assert.Contains("transport", transportMsg, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unparseable", parseMsg, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task WebFetch_ParseFailure_IsReportedAsParseNotTransport()
        {
            PointBothAt(StartGarbageJsonListener());
            string result = await WebTools.WebFetchAsync("http://example.invalid/page");

            Assert.Contains("web_fetch error", result);
            Assert.Contains("unparseable", result, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("transport", result, System.StringComparison.OrdinalIgnoreCase);
        }

        // ── Helper: a real HttpListener that answers 200 with invalid JSON ────
        // This proves the parse path fires (the connection succeeded), so the
        // parse/transport distinction is exercised end-to-end, not by mocking.
        private static string StartGarbageJsonListener()
        {
            var listener = new HttpListener();
            Uri baseUri = FindFreePort();
            listener.Prefixes.Add(baseUri.ToString());
            listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (listener.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = await listener.GetContextAsync(); }
                        catch { break; }
                        byte[] body = System.Text.Encoding.UTF8.GetBytes("<html>this is not json</html>");
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                        ctx.Response.Close();
                    }
                }
                catch { /* listener shutting down */ }
                finally
                {
                    try { listener.Stop(); } catch { }
                }
            });
            return baseUri.ToString().TrimEnd('/');
        }

        private static Uri FindFreePort()
        {
            // Bind to port 0 to let the OS pick a free port, read it, release it.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return new Uri($"http://127.0.0.1:{port}/");
        }
    }
}
