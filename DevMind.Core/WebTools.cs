// File: WebTools.cs  v2.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shared web_search / web_fetch implementation used by the McpServer tools and the
// skin hosts (ConsoleAgenticHost, TuiAgenticHost). Single source of truth — do not
// copy these bodies into a host; call this class.
//
// Both tools depend on self-hosted services and fail gracefully when unreachable:
//   web_search -> SearXNG   at DEVMIND_SEARCH_URL (default http://vard-nas:8180)
//   web_fetch  -> fetcher   at DEVMIND_FETCH_URL  (default http://vard-nas:8181)
//
// web_fetch returns 8,000-char pages: an offset argument reads further, and the full content
// is cached per URL (FetchCache, 15 min) so paging does not re-fetch. Its deadline is
// DEVMIND_FETCH_TIMEOUT_SECONDS (default 180, clamped 10..600).
//
// Observability: both methods emit trace events via DevMind.Trace.Event():
//   web_search.*  — result, error, timeout, cancelled, exception
//   web_fetch.*   — result, error, timeout, cancelled, exception
// These require DEVMIND_TRACE_ENABLED=true and DEVMIND_TRACE_DIR to be set,
// otherwise the calls are no-ops (zero allocation, zero I/O).

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DmTrace = DevMind.Trace;

namespace DevMind
{
    /// <summary>
    /// Shared HTTP implementations for the web_search and web_fetch tools.
    /// All failures (service down, bad JSON, timeout) return a "[tool error] ..."
    /// string rather than throwing, so callers can feed the message straight
    /// back to the model.
    /// </summary>
    public static class WebTools
    {
        private const int SearchTimeoutSeconds = 15;
        private const int ThinContentThreshold = 200;

        // web_fetch deadline: DEVMIND_FETCH_TIMEOUT_SECONDS, read per call like DEVMIND_FETCH_URL.
        // The fetcher converts PDFs/Office docs through Docling (~1 page/s) and can fall back to a
        // headless-browser render after a plain GET (30 s + 45 s), so the old fixed 45 s could
        // never fit a long document.
        internal const int DefaultFetchTimeoutSeconds = 180;
        internal const int MinFetchTimeoutSeconds = 10;
        internal const int MaxFetchTimeoutSeconds = 600;

        /// <summary>Characters web_fetch returns per call; the rest is read by paging with offset.</summary>
        public const int FetchPageChars = 8000;

        /// <summary>Full fetched content per URL, so paging (offset &gt; 0) does not re-fetch.</summary>
        internal static readonly FetchCache FetchContentCache = new FetchCache();

        /// <summary>DEVMIND_FETCH_TIMEOUT_SECONDS as an effective deadline: unset or not an
        /// integer gives the default, anything else is clamped to 10..600.</summary>
        internal static int ResolveFetchTimeoutSeconds(string raw)
            => int.TryParse(raw, out int s)
                ? Math.Clamp(s, MinFetchTimeoutSeconds, MaxFetchTimeoutSeconds)
                : DefaultFetchTimeoutSeconds;

        // Single shared HttpClient for the whole process — creating one per request leaks
        // sockets (connections linger in TIME_WAIT and can exhaust ephemeral ports under load).
        // Its Timeout is left infinite because it is process-wide; per-call deadlines are applied
        // via a linked CancellationTokenSource in each method instead.
        private static readonly HttpClient _http = CreateSharedHttpClient();

        private static HttpClient CreateSharedHttpClient()
        {
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Add("User-Agent", "DevMind/1.0");
            return http;
        }

        /// <summary>
        /// Search the web via the local SearXNG instance. Returns a ranked list of
        /// results with title, URL, and snippet, capped at 20.
        /// </summary>
        public static async Task<string> WebSearchAsync(
            string query, int? maxResults, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                int limit = Math.Min(maxResults ?? 10, 20);
                string searxngUrl = Environment.GetEnvironmentVariable("DEVMIND_SEARCH_URL")
                    ?? "http://vard-nas:8180";
                string url = $"{searxngUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json&language=en&safesearch=0";

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(SearchTimeoutSeconds));
                using var response = await _http.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                // Surface the actual HTTP status on non-200 responses.
                if (!response.IsSuccessStatusCode)
                {
                    string detail = null;
                    try
                    {
                        using (var errDoc = JsonDocument.Parse(json))
                        {
                            if (errDoc.RootElement.TryGetProperty("detail", out var d))
                                detail = d.GetString();
                        }
                    }
                    catch { /* not JSON */ }

                    long elapsedMs = sw.ElapsedMilliseconds;
                    string truncatedQuery = query.Length > 200 ? query.Substring(0, 200) : query;
                    DmTrace.Event("info", "web_search.error", new Dictionary<string, object>
                    {
                        ["query"] = truncatedQuery,
                        ["status"] = (int)response.StatusCode,
                        ["elapsed_ms"] = elapsedMs,
                        ["detail"] = detail,
                    });

                    string msg = detail != null
                        ? $"[web_search error] {(int)response.StatusCode} {response.StatusCode}: {detail}"
                        : $"[web_search error] {(int)response.StatusCode} {response.StatusCode}";
                    return msg;
                }
                using (var doc = JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("results", out var results))
                    {
                        return $"[web_search error] Unexpected response from search service";
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine($"web_search results for \"{query}\":");
                    int count = 0;
                    foreach (var result in results.EnumerateArray())
                    {
                        if (count >= limit) break;
                        string title   = result.TryGetProperty("title",   out var t) ? t.GetString() ?? "" : "";
                        string resUrl  = result.TryGetProperty("url",     out var u) ? u.GetString() ?? "" : "";
                        string snippet = result.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                        sb.AppendLine($"\n[{count + 1}] {title}");
                        sb.AppendLine($"    URL: {resUrl}");
                        if (!string.IsNullOrWhiteSpace(snippet))
                            sb.AppendLine($"    {snippet.Trim()}");
                        count++;
                    }
                    if (count == 0)
                        return $"web_search: no results for \"{query}\"";

                    long elapsedMs = sw.ElapsedMilliseconds;
                    string truncatedQuery = query.Length > 200 ? query.Substring(0, 200) : query;
                    DmTrace.Event("info", "web_search.result", new Dictionary<string, object>
                    {
                        ["query"] = truncatedQuery,
                        ["elapsed_ms"] = elapsedMs,
                        ["result_count"] = count,
                    });

                    return sb.ToString().TrimEnd();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                long elapsedMs = sw.ElapsedMilliseconds;
                string truncatedQuery = query.Length > 200 ? query.Substring(0, 200) : query;
                DmTrace.Event("info", "web_search.timeout", new Dictionary<string, object>
                {
                    ["query"] = truncatedQuery,
                    ["elapsed_ms"] = elapsedMs,
                    ["timeout_s"] = SearchTimeoutSeconds,
                });
                return $"[web_search error] Timed out after {SearchTimeoutSeconds}s searching for \"{query}\"";
            }
            catch (OperationCanceledException)
            {
                long elapsedMs = sw.ElapsedMilliseconds;
                string truncatedQuery = query.Length > 200 ? query.Substring(0, 200) : query;
                DmTrace.Event("debug", "web_search.cancelled", new Dictionary<string, object>
                {
                    ["query"] = truncatedQuery,
                    ["elapsed_ms"] = elapsedMs,
                });
                return "[web_search] Cancelled by caller.";
            }
            catch (HttpRequestException hre)
            {
                long elapsedMs = sw.ElapsedMilliseconds;
                string truncatedQuery = query.Length > 200 ? query.Substring(0, 200) : query;
                DmTrace.Event("info", "web_search.transport_error", new Dictionary<string, object>
                {
                    ["query"] = truncatedQuery,
                    ["elapsed_ms"] = elapsedMs,
                    ["message"] = hre.Message,
                });
                // Transport failure: the SearXNG backend is unreachable (down, DNS, connection
                // refused). This is distinct from a malformed query — stop retrying, fix the backend.
                return $"[web_search error] Search backend unreachable (transport failure): {hre.Message}. " +
                       "The SearXNG service at DEVMIND_SEARCH_URL is down or unreachable; retrying the same " +
                       "query will keep failing until the service is restored.";
            }
            catch (JsonException jex)
            {
                long elapsedMs = sw.ElapsedMilliseconds;
                string truncatedQuery = query.Length > 200 ? query.Substring(0, 200) : query;
                DmTrace.Event("info", "web_search.parse_error", new Dictionary<string, object>
                {
                    ["query"] = truncatedQuery,
                    ["elapsed_ms"] = elapsedMs,
                    ["message"] = jex.Message,
                });
                // Parse failure: the backend responded but the response was not valid/expected JSON.
                // This is distinct from a down backend — try a different query or check the response.
                return $"[web_search error] Search backend returned an unparseable response (not valid JSON): {jex.Message}. " +
                       "The SearXNG service IS reachable; the response format was unexpected. Try rewording the query or verify the service version.";
            }
            catch (Exception ex)
            {
                long elapsedMs = sw.ElapsedMilliseconds;
                string truncatedQuery = query.Length > 200 ? query.Substring(0, 200) : query;
                DmTrace.Event("info", "web_search.exception", new Dictionary<string, object>
                {
                    ["query"] = truncatedQuery,
                    ["elapsed_ms"] = elapsedMs,
                    ["exception"] = ex.GetType().Name,
                    ["message"] = ex.Message,
                });
                // Unclassified: we do not know the cause — say so rather than implying one.
                return $"[web_search error] Unclassified error (cause undetermined): {ex.Message}";
            }
        }

        /// <summary>
        /// Fetch a URL via the local fetcher service and return one page of its content as
        /// clean text: characters [offset, offset + 8,000), with a footer naming the next
        /// offset while more remains. offset 0 always fetches fresh; offset &gt; 0 is served
        /// from the per-URL cache when the content is still there, and re-fetched otherwise.
        /// </summary>
        public static async Task<string> WebFetchAsync(
            string url, int offset = 0, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (offset < 0) offset = 0;
            int timeoutSeconds = ResolveFetchTimeoutSeconds(
                Environment.GetEnvironmentVariable("DEVMIND_FETCH_TIMEOUT_SECONDS"));

            if (offset > 0 && url != null && FetchContentCache.TryGet(url, out string cached))
            {
                TraceFetchResult(url, null, sw.ElapsedMilliseconds, cached, offset, cacheHit: true);
                return FormatFetchPage(url, cached, offset);
            }

            try
            {
                string fetcherUrl = Environment.GetEnvironmentVariable("DEVMIND_FETCH_URL")
                    ?? "http://vard-nas:8181";
                string endpoint = $"{fetcherUrl.TrimEnd('/')}/fetch";

                using var payload = new StringContent(
                    JsonSerializer.Serialize(new { url }),
                    Encoding.UTF8,
                    "application/json");

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using var response = await _http.PostAsync(endpoint, payload, timeoutCts.Token).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                // Surface the fetcher actual HTTP status code on non-200 responses.
                if (!response.IsSuccessStatusCode)
                {
                    string detail = null;
                    try
                    {
                        using (var errDoc = JsonDocument.Parse(json))
                        {
                            if (errDoc.RootElement.TryGetProperty("detail", out var d))
                                detail = d.GetString();
                        }
                    }
                    catch { /* not JSON */ }

                    long elapsedMs = sw.ElapsedMilliseconds;
                    DmTrace.Event("info", "web_fetch.error", new Dictionary<string, object>
                    {
                        ["url"] = url,
                        ["status"] = (int)response.StatusCode,
                        ["elapsed_ms"] = elapsedMs,
                        ["detail"] = detail,
                    });

                    string msg = detail != null
                        ? $"[web_fetch error] {(int)response.StatusCode} {response.StatusCode}: {detail}"
                        : $"[web_fetch error] {(int)response.StatusCode} {response.StatusCode}";
                    return msg;
                }
                using (var doc = JsonDocument.Parse(json))
                {
                    long elapsedMs = sw.ElapsedMilliseconds;

                    // Check for a "detail" field even on 200 (fetcher may return 200 with an error explanation).
                    if (doc.RootElement.TryGetProperty("detail", out var detailProp))
                    {
                        string detail = detailProp.GetString() ?? "";
                        DmTrace.Event("info", "web_fetch.error", new Dictionary<string, object>
                        {
                            ["url"] = url,
                            ["status"] = 200,
                            ["elapsed_ms"] = elapsedMs,
                            ["detail"] = detail,
                        });
                        return $"[web_fetch error] Fetcher returned an error: {detail}";
                    }

                    string content = doc.RootElement.TryGetProperty("content", out var contentProp)
                        ? contentProp.GetString() ?? ""
                        : "";

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        DmTrace.Event("info", "web_fetch.result", new Dictionary<string, object>
                        {
                            ["url"] = url,
                            ["status"] = (int)response.StatusCode,
                            ["elapsed_ms"] = elapsedMs,
                            ["content_len"] = 0,
                            ["thin"] = true,
                            ["truncated"] = false,
                            ["offset"] = offset,
                            ["cache_hit"] = false,
                        });
                        return $"[web_fetch] No content extracted from {url}";
                    }

                    // Only a real page is cached — never an error or an empty extraction.
                    FetchContentCache.Put(url, content);
                    TraceFetchResult(url, (int)response.StatusCode, elapsedMs, content, offset, cacheHit: false);
                    return FormatFetchPage(url, content, offset);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                long elapsedMs = sw.ElapsedMilliseconds;
                DmTrace.Event("info", "web_fetch.timeout", new Dictionary<string, object>
                {
                    ["url"] = url,
                    ["elapsed_ms"] = elapsedMs,
                    ["timeout_s"] = timeoutSeconds,
                    ["offset"] = offset,
                });
                return $"[web_fetch error] Timed out after {timeoutSeconds}s fetching {url} " +
                       "(the limit is DEVMIND_FETCH_TIMEOUT_SECONDS)";
            }
            catch (OperationCanceledException)
            {
                long elapsedMs = sw.ElapsedMilliseconds;
                DmTrace.Event("debug", "web_fetch.cancelled", new Dictionary<string, object>
                {
                    ["url"] = url,
                    ["elapsed_ms"] = elapsedMs,
                });
                return "[web_fetch] Cancelled by caller.";
            }
            catch (HttpRequestException hre)
            {
                long elapsedMs = sw.ElapsedMilliseconds;
                DmTrace.Event("info", "web_fetch.transport_error", new Dictionary<string, object>
                {
                    ["url"] = url,
                    ["elapsed_ms"] = elapsedMs,
                    ["message"] = hre.Message,
                });
                // Transport failure: the fetcher service is unreachable (down, DNS, connection refused).
                // Distinct from a page the fetcher could not parse — stop retrying, fix the fetcher.
                return $"[web_fetch error] Fetcher service unreachable (transport failure): {hre.Message}. " +
                       "The fetcher at DEVMIND_FETCH_URL is down or unreachable; retrying will keep failing " +
                       "until the service is restored.";
            }
            catch (JsonException jex)
            {
                long elapsedMs = sw.ElapsedMilliseconds;
                DmTrace.Event("info", "web_fetch.parse_error", new Dictionary<string, object>
                {
                    ["url"] = url,
                    ["elapsed_ms"] = elapsedMs,
                    ["message"] = jex.Message,
                });
                // Parse failure: the fetcher responded but its response was not valid/expected JSON.
                // Distinct from a down fetcher — the fetcher IS up, its response was malformed.
                return $"[web_fetch error] Fetcher returned an unparseable response (not valid JSON): {jex.Message}. " +
                       "The fetcher service IS reachable; the response format was unexpected. Verify the fetcher version or try a different URL.";
            }
            catch (Exception ex)
            {
                long elapsedMs = sw.ElapsedMilliseconds;
                DmTrace.Event("info", "web_fetch.exception", new Dictionary<string, object>
                {
                    ["url"] = url,
                    ["elapsed_ms"] = elapsedMs,
                    ["exception"] = ex.GetType().Name,
                    ["message"] = ex.Message,
                });
                // Unclassified: we do not know the cause — say so rather than implying one.
                return $"[web_fetch error] Unclassified error (cause undetermined): {ex.Message}";
            }
        }

        private static void TraceFetchResult(string url, int? status, long elapsedMs, string content, int offset, bool cacheHit)
        {
            DmTrace.Event("info", "web_fetch.result", new Dictionary<string, object>
            {
                ["url"] = url,
                ["status"] = status,
                ["elapsed_ms"] = elapsedMs,
                ["content_len"] = content.Length,
                ["thin"] = content.Length < ThinContentThreshold,
                ["truncated"] = offset + FetchPageChars < content.Length,
                ["offset"] = offset,
                ["cache_hit"] = cacheHit,
            });
        }

        /// <summary>
        /// One page of <paramref name="content"/> starting at <paramref name="offset"/>, with the
        /// footer that tells the model where it is: "continue at offset=N" while more remains,
        /// "end of content" on a later last page, nothing when the whole text fit at offset 0.
        /// </summary>
        internal static string FormatFetchPage(string url, string content, int offset)
        {
            int total = content.Length;
            if (offset >= total)
                return $"[web_fetch: offset {offset} is past the end of the content ({total} chars)]";

            int end = Math.Min(total, offset + FetchPageChars);
            // Never split a surrogate pair across two pages.
            if (end < total && end - offset > 1 && char.IsHighSurrogate(content[end - 1])) end--;

            string output = content.Substring(offset, end - offset);
            if (end < total)
                output += $"\n\n[web_fetch: showing characters {offset}-{end} of {total}. " +
                          $"Call web_fetch again with the same url and offset={end} to continue.]";
            else if (offset > 0)
                output += $"\n\n[web_fetch: end of content (characters {offset}-{total} of {total})]";

            if (offset == 0 && total < ThinContentThreshold)
                output += $"\n\n[web_fetch warning: only {total} chars extracted from {url} — the page may require JavaScript rendering, or the fetcher may have been served a bot-check shell. Treat this content as possibly incomplete.]";
            return output;
        }

        /// <summary>
        /// Process-wide cache of full web_fetch content per URL: 15-minute TTL, at most 64 entries
        /// (the oldest is evicted), thread-safe. It lives here, in the one static class every
        /// host calls, so the TUI, headless jobs and the MCP server tool all page the same way;
        /// each process has its own. Tests reset it with <see cref="Clear"/> and drive expiry
        /// through <see cref="Clock"/>.
        /// </summary>
        internal sealed class FetchCache
        {
            internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
            internal const int MaxEntries = 64;

            private readonly object _gate = new object();
            private readonly Dictionary<string, (string Content, DateTime StoredUtc)> _entries =
                new Dictionary<string, (string, DateTime)>(StringComparer.Ordinal);

            /// <summary>UTC clock; tests replace it to expire entries without waiting.</summary>
            internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

            internal int Count { get { lock (_gate) return _entries.Count; } }

            internal bool TryGet(string url, out string content)
            {
                lock (_gate)
                {
                    if (_entries.TryGetValue(url, out var e))
                    {
                        if (Clock() - e.StoredUtc < Ttl)
                        {
                            content = e.Content;
                            return true;
                        }
                        _entries.Remove(url);
                    }
                    content = null;
                    return false;
                }
            }

            internal void Put(string url, string content)
            {
                if (url == null || string.IsNullOrWhiteSpace(content)) return;
                lock (_gate)
                {
                    _entries[url] = (content, Clock());
                    while (_entries.Count > MaxEntries)
                    {
                        string oldest = null;
                        DateTime oldestAt = DateTime.MaxValue;
                        foreach (var kv in _entries)
                            if (kv.Value.StoredUtc < oldestAt) { oldest = kv.Key; oldestAt = kv.Value.StoredUtc; }
                        _entries.Remove(oldest);
                    }
                }
            }

            /// <summary>Empties the cache and restores the real clock.</summary>
            internal void Clear()
            {
                lock (_gate)
                {
                    _entries.Clear();
                    Clock = () => DateTime.UtcNow;
                }
            }
        }
    }
}
