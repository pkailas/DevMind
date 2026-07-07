// File: VisionNoteClient.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tool-free vision-note client for the /library PDF ingest path against a DEDICATED
// vision endpoint (e.g. Qwen3-VL on vLLM). Unlike LlmClient it never sends tools or
// tool_choice — a plain OpenAI chat-completion with ONE page image per request. That
// is what document-vision servers expect: vLLM rejects tool_choice="required" without
// a tool-call parser, and forcing a tool call would suppress the note entirely. The
// one-image-per-call shape also respects the common `--limit-mm-per-prompt image=1`
// launch; callers group pages into chunks and combine the per-page notes.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>Minimal OpenAI-compatible vision client: one image → one note, no tools.</summary>
    public sealed class VisionNoteClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _chatUrl;
        private readonly string _model;
        private readonly string _apiKey;

        /// <param name="endpointBaseUrl">OpenAI-compatible base, e.g. http://127.0.0.1:8084/v1</param>
        public VisionNoteClient(string endpointBaseUrl, string model, string apiKey)
        {
            _chatUrl = CombineUrl(endpointBaseUrl, "chat/completions");
            _model = model ?? "";
            _apiKey = apiKey ?? "";
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        /// <summary>True when GET {base}/models answers 2xx within <paramref name="timeout"/>.
        /// Never throws — any failure (down, DNS, timeout) returns false so the caller can
        /// warn and fall back.</summary>
        public static async Task<bool> IsReachableAsync(
            string endpointBaseUrl, string apiKey, TimeSpan timeout, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(endpointBaseUrl))
                return false;
            try
            {
                using (var http = new HttpClient { Timeout = timeout })
                using (var req = new HttpRequestMessage(HttpMethod.Get, CombineUrl(endpointBaseUrl, "models")))
                {
                    if (!string.IsNullOrEmpty(apiKey))
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    using (var resp = await http.SendAsync(req, ct).ConfigureAwait(false))
                        return resp.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Generate a note for ONE page image via a tool-free chat completion.</summary>
        public async Task<string> NotePageAsync(
            byte[] pngBytes, string userPrompt, string systemPrompt, int maxTokens, CancellationToken ct)
        {
            string dataUri = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
            var payload = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["messages"] = new object[]
                {
                    new Dictionary<string, object> { ["role"] = "system", ["content"] = systemPrompt },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = new object[]
                        {
                            new Dictionary<string, object> { ["type"] = "text", ["text"] = userPrompt },
                            new Dictionary<string, object>
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new Dictionary<string, object> { ["url"] = dataUri },
                            },
                        },
                    },
                },
                ["max_tokens"] = maxTokens > 0 ? maxTokens : 4096,
                ["temperature"] = 0.3,
                ["stream"] = false,
            };

            using (var req = new HttpRequestMessage(HttpMethod.Post, _chatUrl))
            {
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(_apiKey))
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);

                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            $"Vision endpoint returned {(int)resp.StatusCode}: {Truncate(body, 300)}");

                    using (var doc = JsonDocument.Parse(body))
                    {
                        var choices = doc.RootElement.GetProperty("choices");
                        if (choices.GetArrayLength() == 0)
                            return "";
                        var msg = choices[0].GetProperty("message");
                        return msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                            ? content.GetString() ?? ""
                            : "";
                    }
                }
            }
        }

        private static string CombineUrl(string baseUrl, string suffix)
            => (baseUrl ?? "").TrimEnd('/') + "/" + suffix;

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));

        public void Dispose() => _http.Dispose();
    }
}
