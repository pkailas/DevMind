// File: WebTools.cs  v2.0
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
        private const int FetchTimeoutSeconds  = 45;
        private const int ThinContentThreshold = 200;

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
                return $"[web_search error] {ex.Message}";
            }
        }

        /// <summary>
        /// Fetch a URL via the local fetcher service and return its content as clean
        /// text, capped at 8,000 characters.
        /// </summary>
        public static async Task<string> WebFetchAsync(
            string url, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
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
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(FetchTimeoutSeconds));
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
                        });
                        return $"[web_fetch] No content extracted from {url}";
                    }

                    bool isThin = content.Length < ThinContentThreshold;

                    // Cap at 8000 chars to avoid flooding context.
                    const int Cap = 8000;
                    bool capped = content.Length > Cap;
                    string output = capped ? content.Substring(0, Cap) : content;

                    DmTrace.Event("info", "web_fetch.result", new Dictionary<string, object>
                    {
                        ["url"] = url,
                        ["status"] = (int)response.StatusCode,
                        ["elapsed_ms"] = elapsedMs,
                        ["content_len"] = content.Length,
                        ["thin"] = isThin,
                        ["truncated"] = capped,
                    });

                    if (capped)
                    {
                        output = $"{output}\n\n[web_fetch: content truncated at {Cap} chars]";
                    }
                    if (isThin)
                    {
                        output = $"{output}\n\n[web_fetch warning: only {content.Length} chars extracted from {url} — the page may require JavaScript rendering, or the fetcher may have been served a bot-check shell. Treat this content as possibly incomplete.]";
                    }
                    return output;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                long elapsedMs = sw.ElapsedMilliseconds;
                DmTrace.Event("info", "web_fetch.timeout", new Dictionary<string, object>
                {
                    ["url"] = url,
                    ["elapsed_ms"] = elapsedMs,
                    ["timeout_s"] = FetchTimeoutSeconds,
                });
                return $"[web_fetch error] Timed out after {FetchTimeoutSeconds}s fetching {url}";
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
                return $"[web_fetch error] {ex.Message}";
            }
        }
    }
}
