// File: LearnTools.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shared learn_search / learn_fetch / learn_code_search implementation used by the
// McpServer tools and the skin hosts (ConsoleAgenticHost, TuiAgenticHost). Single
// source of truth — do not copy these bodies into a host; call this class.
//
// All three tools call Microsoft's hosted Learn MCP server (stateless, no handshake):
//   Endpoint: https://learn.microsoft.com/api/mcp (override via DEVMIND_LEARN_URL)
//   Protocol: JSON-RPC 2.0 POST with SSE-framed response ("data: {json}" lines)
//   Remote tools: microsoft_docs_search, microsoft_docs_fetch, microsoft_code_sample_search

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// Shared HTTP implementations for the learn_search, learn_fetch, and learn_code_search
    /// tools. All failures (service down, bad JSON, timeout) return a "[tool error] ..."
    /// string rather than throwing, so callers can feed the message straight back to the model.
    /// </summary>
    public static class LearnTools
    {
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

        private static string LearnUrl =>
            Environment.GetEnvironmentVariable("DEVMIND_LEARN_URL")
            ?? "https://learn.microsoft.com/api/mcp";

        /// <summary>
        /// Search official Microsoft/Azure documentation. Returns up to 10 results with
        /// title, URL, and excerpt.
        /// </summary>
        public static async Task<string> LearnSearchAsync(
            string query, int? maxResults, CancellationToken cancellationToken = default)
        {
            try
            {
                int limit = Math.Min(maxResults ?? 10, 10);
                string payload = await CallLearnToolAsync(
                    "microsoft_docs_search",
                    $"{{\"query\":{JsonSerializer.Serialize(query)}}}",
                    cancellationToken);
                if (payload == null)
                    return "[learn_search error] No response from Learn MCP server";

                var results = ParseSearchResults(payload);
                if (results == null)
                    return "[learn_search error] Failed to parse search results";

                // Cap to requested limit
                if (results.Count > limit)
                    results.RemoveRange(limit, results.Count - limit);

                if (results.Count == 0)
                    return $"[learn_search] No results found for \"{query}\"";

                var sb = new StringBuilder();
                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    sb.AppendLine($"{i + 1}. {r.Title}");
                    sb.AppendLine($"   URL: {r.Url}");
                    sb.AppendLine($"   {r.Content}");
                    if (i < results.Count - 1)
                        sb.AppendLine();
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"[learn_search error] {ex.Message}";
            }
        }

        /// <summary>
        /// Fetch a specific learn.microsoft.com page and return its content as markdown.
        /// Output is capped at 8000 chars to avoid flooding context.
        /// </summary>
        public static async Task<string> LearnFetchAsync(
            string url, CancellationToken cancellationToken = default)
        {
            try
            {
                string payload = await CallLearnToolAsync(
                    "microsoft_docs_fetch",
                    $"{{\"url\":{JsonSerializer.Serialize(url)}}}",
                    cancellationToken);
                if (payload == null)
                    return $"[learn_fetch error] No response from Learn MCP server for {url}";

                // For fetch, the payload is the markdown content directly (or a JSON object
                // with a content field). Try to parse as JSON first.
                string content;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("content", out var contentProp))
                        content = contentProp.GetString() ?? payload;
                    else if (doc.RootElement.TryGetProperty("markdown", out var mdProp))
                        content = mdProp.GetString() ?? payload;
                    else
                        content = payload;
                }
                catch
                {
                    // Not JSON — treat as raw content
                    content = payload;
                }

                if (string.IsNullOrWhiteSpace(content))
                    return $"[learn_fetch] No content extracted from {url}";

                // Cap at 8000 chars to avoid flooding context (same convention as web_fetch).
                const int Cap = 8000;
                bool capped = content.Length > Cap;
                string output = capped ? content.Substring(0, Cap) : content;
                return capped
                    ? $"{output}\n\n[learn_fetch: content truncated at {Cap} chars]"
                    : output;
            }
            catch (Exception ex)
            {
                return $"[learn_fetch error] {ex.Message}";
            }
        }

        /// <summary>
        /// Search official Microsoft code samples. Returns up to 10 results with
        /// title, URL, and code excerpt.
        /// </summary>
        public static async Task<string> LearnCodeSearchAsync(
            string query, int? maxResults, CancellationToken cancellationToken = default)
        {
            try
            {
                int limit = Math.Min(maxResults ?? 10, 10);
                string payload = await CallLearnToolAsync(
                    "microsoft_code_sample_search",
                    $"{{\"query\":{JsonSerializer.Serialize(query)}}}",
                    cancellationToken);
                if (payload == null)
                    return "[learn_code_search error] No response from Learn MCP server";

                var results = ParseCodeSampleResults(payload);
                if (results == null)
                    return "[learn_code_search error] Failed to parse search results";

                // Cap to requested limit
                if (results.Count > limit)
                    results.RemoveRange(limit, results.Count - limit);

                if (results.Count == 0)
                    return $"[learn_code_search] No results found for \"{query}\"";

                var sb = new StringBuilder();
                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    sb.AppendLine($"{i + 1}. [{r.Language}] {r.Description}");
                    sb.AppendLine($"   Link: {r.Link}");
                    sb.AppendLine($"   ```{r.Language}");
                    sb.AppendLine(r.CodeSnippet);
                    sb.AppendLine("   ```");
                    if (i < results.Count - 1)
                        sb.AppendLine();
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"[learn_code_search error] {ex.Message}";
            }
        }

        // ── Internal helpers (internal for unit-testability) ─────────────────────

        /// <summary>
        /// Call a remote tool on the Learn MCP server via JSON-RPC 2.0.
        /// Sends a POST with SSE-framed response parsing.
        /// Returns the unwrapped payload string, or null on error.
        /// </summary>
        internal static async Task<string> CallLearnToolAsync(
            string toolName, string argumentsJson, CancellationToken cancellationToken)
        {
            // Build JSON-RPC 2.0 request body.
            string requestBody = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"{toolName}\",\"arguments\":{argumentsJson}}}}}";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            using var content = new StringContent(
                requestBody, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, LearnUrl)
            {
                Content = content
            };
            request.Headers.Add("Accept", "application/json, text/event-stream");

            using var response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            string responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            return ParseSseResponse(responseBody);
        }

        /// <summary>
        /// Parse an SSE-framed response body. Extracts the first "data:" line,
        /// JSON-parses it, navigates result.content[0].text, and unwraps the
        /// double-encoded JSON payload.
        /// Returns the unwrapped payload string, or null on error.
        /// </summary>
        internal static string ParseSseResponse(string responseBody)
        {
            // Find the first "data: " line in the SSE stream.
            string dataLine = null;
            foreach (string line in responseBody.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("data: ", StringComparison.Ordinal))
                {
                    dataLine = trimmed.Substring(6);
                    break;
                }
            }

            if (string.IsNullOrEmpty(dataLine))
                return null;

            // Parse the JSON-RPC response.
            JsonDocument rpcDoc;
            try
            {
                rpcDoc = JsonDocument.Parse(dataLine);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Malformed JSON-RPC response: {ex.Message}", ex);
            }

            // Check for JSON-RPC error.
            if (rpcDoc.RootElement.TryGetProperty("error", out var errorProp))
            {
                string errorMsg = errorProp.TryGetProperty("message", out var msg)
                    ? msg.GetString()
                    : errorProp.ToString();
                throw new InvalidOperationException($"Learn MCP error: {errorMsg}");
            }

            // Navigate result.content[0].text
            if (!rpcDoc.RootElement.TryGetProperty("result", out var resultProp))
                return null;

            var result = resultProp;
            if (!result.TryGetProperty("content", out var contentArr)
                || contentArr.GetArrayLength() == 0)
                return null;

            var firstContent = contentArr[0];
            if (!firstContent.TryGetProperty("text", out var textProp))
                return null;

            string textValue = textProp.GetString();
            if (textValue == null)
                return null;

            // The text field is itself a JSON string containing the payload.
            // Try to parse it as JSON and return the raw string representation
            // (caller will parse further as needed).
            try
            {
                using var innerDoc = JsonDocument.Parse(textValue);
                // Return the parsed JSON as a string for further processing.
                return innerDoc.RootElement.ToString();
            }
            catch
            {
                // Not valid JSON — return as-is (might be plain text content).
                return textValue;
            }
        }

        // ── Search result model ─────────────────────────────────────────────────

        internal sealed class SearchResult
        {
            public string Title { get; set; } = "";
            public string Url { get; set; } = "";
            public string Content { get; set; } = "";
        }

        internal sealed class CodeSampleResult
        {
            public string Description { get; set; } = "";
            public string CodeSnippet { get; set; } = "";
            public string Link { get; set; } = "";
            public string Language { get; set; } = "";
        }

        /// <summary>
        /// Parse the unwrapped JSON payload from a docs search tool response into
        /// a list of SearchResult objects. The payload contains a "results" array
        /// with objects having title, contentUrl (or url as fallback), and content fields.
        /// </summary>
        internal static System.Collections.Generic.List<SearchResult> ParseSearchResults(string payload)
        {
            var results = new System.Collections.Generic.List<SearchResult>();

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("results", out var resultsArr))
                return null;

            foreach (var item in resultsArr.EnumerateArray())
            {
                var result = new SearchResult
                {
                    Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Url = item.TryGetProperty("contentUrl", out var cu) ? cu.GetString() ?? ""
                         : (item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : ""),
                    Content = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : ""
                };
                results.Add(result);
            }

            return results;
        }

        /// <summary>
        /// Parse the unwrapped JSON payload from a code sample search tool response into
        /// a list of CodeSampleResult objects. The payload contains a "results" array
        /// with objects having description, codeSnippet, link, and language fields.
        /// </summary>
        internal static System.Collections.Generic.List<CodeSampleResult> ParseCodeSampleResults(string payload)
        {
            var results = new System.Collections.Generic.List<CodeSampleResult>();

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("results", out var resultsArr))
                return null;

            foreach (var item in resultsArr.EnumerateArray())
            {
                var result = new CodeSampleResult
                {
                    Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    CodeSnippet = item.TryGetProperty("codeSnippet", out var cs) ? cs.GetString() ?? "" : "",
                    Link = item.TryGetProperty("link", out var l) ? l.GetString() ?? "" : "",
                    Language = item.TryGetProperty("language", out var lang) ? lang.GetString() ?? "" : ""
                };
                results.Add(result);
            }

            return results;
        }
    }
}
