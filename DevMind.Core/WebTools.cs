// File: WebTools.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shared web_search / web_fetch implementation used by the McpServer tools and the
// skin hosts (ConsoleAgenticHost, TuiAgenticHost). Single source of truth ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â do not
// copy these bodies into a host; call this class.
//
// Both tools depend on self-hosted services and fail gracefully when unreachable:
//   web_search ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ SearXNG   at DEVMIND_SEARCH_URL (default http://vard-nas:8180)
//   web_fetch  ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ fetcher   at DEVMIND_FETCH_URL  (default http://vard-nas:8181)

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// Shared HTTP implementations for the web_search and web_fetch tools.
    /// All failures (service down, bad JSON, timeout) return a "[tool error] ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¦"
    /// string rather than throwing, so callers can feed the message straight
    /// back to the model.
    /// </summary>
    public static class WebTools
    {
        /// <summary>
        /// Search the web via the local SearXNG instance. Returns a ranked list of
        /// results with title, URL, and snippet, capped at 20.
        /// </summary>
        public static async Task<string> WebSearchAsync(
            string query, int? maxResults, CancellationToken cancellationToken = default)
        {
            try
            {
                int limit = Math.Min(maxResults ?? 10, 20);
                string searxngUrl = Environment.GetEnvironmentVariable("DEVMIND_SEARCH_URL")
                    ?? "http://vard-nas:8180";
                string url = $"{searxngUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json&language=en&safesearch=0";

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.Add("User-Agent", "DevMind/1.0");
                var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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
    
                    return sb.ToString().TrimEnd();
                }
                }
            catch (Exception ex)
            {
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
            try
            {
                string fetcherUrl = Environment.GetEnvironmentVariable("DEVMIND_FETCH_URL")
                    ?? "http://vard-nas:8181";
                string endpoint = $"{fetcherUrl.TrimEnd('/')}/fetch";

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
                var payload = new StringContent(
                    JsonSerializer.Serialize(new { url }),
                    Encoding.UTF8,
                    "application/json");

                var response = await http.PostAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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

                    string msg = detail != null
                        ? $"[web_fetch error] {(int)response.StatusCode} {response.StatusCode}: {detail}"
                        : $"[web_fetch error] {(int)response.StatusCode} {response.StatusCode}";
                    return msg;
                }
                using (var doc = JsonDocument.Parse(json))
                    {
                        // Check for a "detail" field even on 200 (fetcher may return 200 with an error explanation).
                        if (doc.RootElement.TryGetProperty("detail", out var detailProp))
                        {
                            string detail = detailProp.GetString() ?? "";
                            return $"[web_fetch error] Fetcher returned an error: {detail}";
                        }
    
                        string content = doc.RootElement.TryGetProperty("content", out var contentProp)
                            ? contentProp.GetString() ?? ""
                            : "";
    
                    if (string.IsNullOrWhiteSpace(content))
                        return $"[web_fetch] No content extracted from {url}";
    
                    // Cap at 8000 chars to avoid flooding context.
                    const int Cap = 8000;
                    bool capped = content.Length > Cap;
                    string output = capped ? content.Substring(0, Cap) : content;
                    return capped
                        ? $"{output}\n\n[web_fetch: content truncated at {Cap} chars]"
                        : output;
                }
                }
            catch (Exception ex)
            {
                return $"[web_fetch error] {ex.Message}";
            }
        }
    }
}
