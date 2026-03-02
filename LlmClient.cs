// File: LlmClient.cs  v1.2
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// HTTP client for communicating with OpenAI-compatible /v1/chat/completions endpoints
    /// using Server-Sent Events (SSE) for streaming responses.
    /// </summary>
    public sealed class LlmClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly List<ChatMessage> _conversationHistory;
        private const string DefaultSystemPrompt = "You are a helpful coding assistant. Be concise and precise.";
        private string _baseUrl;

        /// <summary>
        /// Initializes a new instance of the <see cref="LlmClient"/> class.
        /// </summary>
        public LlmClient()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
            _conversationHistory = new List<ChatMessage>
            {
                new ChatMessage("system", GetSystemPrompt())
            };
        }

        /// <summary>
        /// Configures the client with the specified endpoint URL and API key.
        /// </summary>
        /// <param name="endpointUrl">The base URL for the API endpoint.</param>
        /// <param name="apiKey">The API key for authentication.</param>
        public void Configure(string endpointUrl, string apiKey)
        {
            _baseUrl = endpointUrl?.TrimEnd('/') ?? "http://127.0.0.1:1234/v1";
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Accept", "text/event-stream");
            if (!string.IsNullOrEmpty(apiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }
        }

        /// <summary>
        /// Sends a user message to the LLM and streams the response tokens via callbacks.
        /// </summary>
        /// <param name="userMessage">The user's message text.</param>
        /// <param name="onToken">Called for each streamed token fragment.</param>
        /// <param name="onComplete">Called when the full response has been received.</param>
        /// <param name="onError">Called if an error occurs during the request.</param>
        /// <param name="cancellationToken">Cancellation token to abort the request.</param>
        public async Task SendMessageAsync(
            string userMessage,
            Action<string> onToken,
            Action onComplete,
            Action<Exception> onError,
            CancellationToken cancellationToken = default)
        {
            UpdateSystemPrompt();
            _conversationHistory.Add(new ChatMessage("user", userMessage));

            string modelName = DevMindOptions.Instance.ModelName;
            string requestJson = BuildRequestJson(modelName);
            string url = _baseUrl + "/chat/completions";

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };

            try
            {
                var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var fullResponse = new StringBuilder();
                    string line;

                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!line.StartsWith("data: ", StringComparison.Ordinal))
                            continue;

                        string data = line.Substring(6).Trim();

                        if (data == "[DONE]")
                            break;

                        string token = ParseContentDelta(data);
                        if (token != null)
                        {
                            fullResponse.Append(token);
                            onToken(token);
                        }
                    }

                    _conversationHistory.Add(new ChatMessage("assistant", fullResponse.ToString()));
                    onComplete();
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelled by user - don't report as error
            }
            catch (Exception ex)
            {
                onError(ex);
            }
        }

        /// <summary>
        /// Tests the connection to the configured LLM endpoint by querying the models list.
        /// </summary>
        /// <returns><c>true</c> if the endpoint is reachable; otherwise <c>false</c>.</returns>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                string url = _baseUrl + "/models";
                var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Clears the conversation history and resets to the system prompt only.
        /// </summary>
        public void ClearHistory()
        {
            _conversationHistory.Clear();
            _conversationHistory.Add(new ChatMessage("system", GetSystemPrompt()));
        }

        /// <summary>
        /// Releases the HTTP client resources.
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        private static string GetSystemPrompt()
        {
            string prompt = DevMindOptions.Instance.SystemPrompt;
            return string.IsNullOrWhiteSpace(prompt) ? DefaultSystemPrompt : prompt;
        }

        private void UpdateSystemPrompt()
        {
            string prompt = GetSystemPrompt();
            if (_conversationHistory.Count > 0 && _conversationHistory[0].Role == "system")
            {
                _conversationHistory[0] = new ChatMessage("system", prompt);
            }
            else
            {
                _conversationHistory.Insert(0, new ChatMessage("system", prompt));
            }
        }

        private string BuildRequestJson(string modelName)
        {
            var messages = new JArray();
            foreach (var msg in _conversationHistory)
            {
                messages.Add(new JObject
                {
                    ["role"] = msg.Role,
                    ["content"] = msg.Content
                });
            }

            var request = new JObject
            {
                ["messages"] = messages,
                ["stream"] = true
            };

            if (!string.IsNullOrWhiteSpace(modelName))
            {
                request["model"] = modelName;
            }

            return request.ToString(Formatting.None);
        }

        private static string ParseContentDelta(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                return obj?["choices"]?[0]?["delta"]?["content"]?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Represents a single message in the chat conversation history.
    /// </summary>
    internal sealed class ChatMessage
    {
        /// <summary>The role of the message sender (system, user, or assistant).</summary>
        public string Role { get; }

        /// <summary>The text content of the message.</summary>
        public string Content { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ChatMessage"/> class.
        /// </summary>
        /// <param name="role">The role (system, user, or assistant).</param>
        /// <param name="content">The message text.</param>
        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }
}
