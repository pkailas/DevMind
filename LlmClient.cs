// File: LlmClient.cs  v7.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// Tracks token budget allocation across named buckets for a single LLM context window.
    /// Buckets are percentages of loaded_context_length and are tuned via the const fields.
    /// ResponseHeadroom is a hard reservation — history must never consume it.
    /// </summary>
    internal class ContextBudget
    {
        // Bucket percentages of loaded_context_length — easy to tune.
        public const double SystemPromptPct     = 0.25;
        public const double ResponseHeadroomPct = 0.15;
        public const double ProtectedTurnsPct   = 0.15;
        public const double WorkingHistoryPct   = 0.45;

        /// <summary>Maximum context window size in tokens.</summary>
        public int TotalLimit           { get; }
        /// <summary>Token budget allocated for the system prompt.</summary>
        /// <returns>System prompt token limit.</returns>
        public int SystemPromptLimit    => (int)(TotalLimit * SystemPromptPct);
        /// <summary>Token budget reserved for LLM response generation.</summary>
        /// <returns>Response headroom token limit.</returns>
        public int ResponseHeadroomLimit=> (int)(TotalLimit * ResponseHeadroomPct);
        /// <summary>Token budget for protected conversation turns.</summary>
        /// <returns>Protected turns token limit.</returns>
        public int ProtectedTurnsLimit  => (int)(TotalLimit * ProtectedTurnsPct);
        /// <summary>Token budget for working conversation history.</summary>
        /// <returns>Working history token limit.</returns>
        public int WorkingHistoryLimit  => (int)(TotalLimit * WorkingHistoryPct);


        // Hard ceiling for all history combined (system + turns + current).
        // History MUST NOT exceed this — the remainder is reserved for response generation.
        public int HistoryHardLimit     => TotalLimit - ResponseHeadroomLimit;

        // Current usage per bucket — populated by Assess().
        public int SystemPromptUsed    { get; private set; }
        public int WorkingHistoryUsed  { get; private set; }
        public int ProtectedTurnsUsed  { get; private set; }

        // Soft trigger: working history is 80% full → squeeze encouraged but not forced.
        public bool IsWorkingHistoryOverSoft => WorkingHistoryUsed > (int)(WorkingHistoryLimit * 0.80);
        // Hard trigger: working history is 95% full → squeeze required.
        public bool IsWorkingHistoryOverHard => WorkingHistoryUsed > (int)(WorkingHistoryLimit * 0.95);

        public ContextBudget(int totalContextLength)
        {
            TotalLimit = totalContextLength > 0 ? totalContextLength : 13372;
        }

        /// <summary>
        /// Measures token usage per bucket from the current conversation history.
        /// Layout: [0]=system, [1..lastIndex-ProtectedPairs*2-1]=working history,
        ///         [lastIndex-ProtectedPairs*2..lastIndex-1]=protected turns, [lastIndex]=current user.
        /// "Protected turns" = last 2 prior user/assistant pairs before current message.
        /// "Working history" = everything between system and protected turns.
        /// Current user message tokens are NOT counted here (tracked separately via EstimateTokens).
        /// </summary>
        internal void Assess(List<ChatMessage> history, Func<string, int> estimateTokens)
        {
            SystemPromptUsed   = 0;
            WorkingHistoryUsed = 0;
            ProtectedTurnsUsed = 0;

            if (history == null || history.Count == 0) return;

            // [0] = system prompt
            SystemPromptUsed = estimateTokens(history[0].Content);

            int lastIndex = history.Count - 1; // current (pending) user message
            // Prior complete pairs: indices [1..lastIndex-1], in pairs of user+assistant.
            // We protect the last 2 pairs = 4 messages at indices [lastIndex-4..lastIndex-1].
            const int ProtectedPairs = 2;
            int protectedMessages = ProtectedPairs * 2;
            int protectedStart = lastIndex - protectedMessages; // first protected message index

            for (int i = 1; i < lastIndex; i++)
            {
                int tokens = estimateTokens(history[i].Content);
                if (i >= protectedStart)
                    ProtectedTurnsUsed += tokens;
                else
                    WorkingHistoryUsed += tokens;
            }
        }
    }

    /// <summary>
    /// HTTP client for communicating with OpenAI-compatible /v1/chat/completions endpoints
    /// using Server-Sent Events (SSE) for streaming responses.
    /// </summary>
    public sealed class LlmClient : IDisposable
    {
        private HttpClient _httpClient;
        private readonly List<ChatMessage> _conversationHistory;
        private readonly List<string> _pendingDebugLog = new List<string>();
        private const string DefaultSystemPrompt = "You are a helpful coding assistant. Be concise and precise.";
        internal readonly FileContentCache _fileCache = new FileContentCache();
        private string _taskScratchpad = "";
        private const int ScratchpadMaxTokens = 200;
        private int _currentTurn;

        /// <summary>
        /// Tracks filenames (case-insensitive) that have been fully read this session.
        /// When a file IS in this set, subsequent READs produce an outline instead of full content.
        /// Cleared on ClearHistory().
        /// </summary>
        internal readonly HashSet<string> _filesReadThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _baseUrl;
        private string _apiKey;
        private int _contextSize = 13372; // fallback default
        private ContextBudget _budget;
        private Task _contextDetectionTask; // awaited in SendMessageAsync to ensure accurate budget on first message

        private const int MaxConversationTurns = 4;

        // MaxPromptTokens = hard limit for all history (leaves ResponseHeadroom for LLM output).
        public int MaxPromptTokens => _budget?.HistoryHardLimit ?? (int)(_contextSize * 0.85);

        // Tokens reserved for LLM response generation — never consumed by history.
        public int ResponseHeadroomTokens => _budget?.ResponseHeadroomLimit ?? (int)(_contextSize * 0.15);

        private static int EstimateTokens(string text) => (text?.Length ?? 0) / 4 + 4;

        public int EstimateHistoryTokens()
        {
            int total = 0;
            foreach (var msg in _conversationHistory)
                total += EstimateTokens(msg.Content);
            return total;
        }

        /// <summary>
        /// Current estimated prompt token usage as a percentage of MaxPromptTokens, clamped 0–100.
        /// </summary>
        public int ContextBudgetPercent
        {
            get
            {
                int budget = MaxPromptTokens;
                if (budget <= 0) return 100;
                int pct = (int)(EstimateHistoryTokens() * 100.0 / budget);
                return pct < 0 ? 0 : pct > 100 ? 100 : pct;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LlmClient"/> class.
        /// </summary>
        public LlmClient()
        {
            // Keep idle TCP connections alive for 10 minutes — prevents the pool from
            // killing connections mid-request when the server is processing a large prompt
            // (net48 equivalent of SocketsHttpHandler.PooledConnectionIdleTimeout).
            System.Net.ServicePointManager.MaxServicePointIdleTime = 600_000;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(DevMindOptions.Instance.RequestTimeoutMinutes) };
            _conversationHistory = new List<ChatMessage>
            {
                new ChatMessage("system", GetSystemPrompt())
            };
            _budget = new ContextBudget(_contextSize);
        }

        /// <summary>
        /// Configures the client with the specified endpoint URL and API key.
        /// </summary>
        /// <param name="endpointUrl">The base URL for the API endpoint.</param>
        /// <param name="apiKey">The API key for authentication.</param>
        public void Configure(string endpointUrl, string apiKey)
        {
            _baseUrl = endpointUrl?.TrimEnd('/') ?? "http://127.0.0.1:1234/v1";
            _apiKey  = apiKey;
            string apiKeyState = _apiKey == null ? "null" : _apiKey.Length == 0 ? "empty" : $"set (length={_apiKey.Length})";
            Debug.WriteLine($"[DevMind TRACE] Configure() called — _apiKey is {apiKeyState}, endpoint={_baseUrl}");
            if (DevMindOptions.Instance.ShowDebugOutput)
                _pendingDebugLog.Add($"\n[DEBUG] Configure() — API key: {apiKeyState}, endpoint: {_baseUrl}\n");
            RecreateHttpClient();
            _contextDetectionTask = DetectContextSizeAsync();
        }

        /// <summary>
        /// Applies endpoint-specific settings (timeout, headers) to the given HttpClient.
        /// </summary>
        private void ApplyHttpClientSettings(HttpClient client)
        {
            client.Timeout = TimeSpan.FromMinutes(DevMindOptions.Instance.RequestTimeoutMinutes);
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Accept", "text/event-stream");
            Debug.WriteLine($"[DevMind TRACE] ApplyHttpClientSettings — _apiKey is {(_apiKey == null ? "null" : _apiKey.Length == 0 ? "empty" : $"set (length={_apiKey.Length})")}");
            if (!string.IsNullOrEmpty(_apiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            }
            var authHeader = client.DefaultRequestHeaders.Authorization;
            bool authSet = authHeader != null;
            Debug.WriteLine($"[DevMind TRACE] ApplyHttpClientSettings — Authorization header {(authSet ? $"SET: scheme={authHeader.Scheme}, param length={authHeader.Parameter?.Length ?? 0}" : "NOT SET (null)")}");
            if (DevMindOptions.Instance.ShowDebugOutput)
                _pendingDebugLog.Add($"\n[DEBUG] ApplyHttpClientSettings() — Authorization header: {(authSet ? "SET" : "NOT SET")}\n");
        }

        /// <summary>
        /// Disposes the current HttpClient and creates a fresh one with the same settings.
        /// Called when a health check detects a stale TCP connection.
        /// </summary>
        private void RecreateHttpClient()
        {
            var old = _httpClient;
            _httpClient = new HttpClient();
            ApplyHttpClientSettings(_httpClient);
            old?.Dispose();
        }

        /// <summary>
        /// Sends a lightweight GET to /health (or /v1/models as fallback) with a 3-second timeout.
        /// If the probe fails, the HttpClient is disposed and recreated with a fresh SocketsHttpHandler
        /// so the subsequent chat request goes over a live TCP connection.
        /// </summary>
        private async Task EnsureConnectionHealthAsync(CancellationToken cancellationToken)
        {
            string serverRoot = _baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? _baseUrl.Substring(0, _baseUrl.Length - 3)
                : _baseUrl;

            string[] probeUrls = { serverRoot + "/health", _baseUrl + "/models" };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            foreach (string probeUrl in probeUrls)
            {
                try
                {
                    var probe = await _httpClient.GetAsync(probeUrl, cts.Token).ConfigureAwait(false);
                    // Any HTTP response (even 404) means the TCP connection is alive.
                    return;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 3-second probe timed out — stale connection; try next probe URL.
                }
                catch (HttpRequestException)
                {
                    // Network-level failure — stale connection; try next probe URL.
                }
            }

            // All probes failed — recreate the HttpClient with a fresh connection pool.
            RecreateHttpClient();
        }

        /// <summary>
        /// Detects the context window size using an endpoint appropriate for the configured server type.
        /// Falls back to the hardcoded default (13372) and logs a warning if detection fails.
        /// </summary>
        public async Task DetectContextSizeAsync()
        {
            var opts = DevMindOptions.Instance;

            int manual = opts?.ManualContextSize ?? 0;
            if (manual > 0)
            {
                _contextSize = manual;
                _budget = new ContextBudget(_contextSize);
                Debug.WriteLine($"[DevMind] Using manual context size: {_contextSize}");
                if (DevMindOptions.Instance.ShowDebugOutput)
                    _pendingDebugLog.Add($"\n[DEBUG] DetectContextSizeAsync() — manual context override: {_contextSize:N0} tokens\n");
                return;
            }

            if (DevMindOptions.Instance.ShowDebugOutput)
                _pendingDebugLog.Add($"\n[DEBUG] DetectContextSizeAsync() — auto-detecting context size (no manual override)\n");

            LlmServerType serverType = opts?.ServerType ?? LlmServerType.LlamaServer;

            string serverRoot = _baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? _baseUrl.Substring(0, _baseUrl.Length - 3)
                : _baseUrl;

            try
            {
                int detected = 0;
                string url;

                switch (serverType)
                {
                    case LlmServerType.LlamaServer:
                        url = serverRoot + "/props";
                        Debug.WriteLine($"[DevMind] Context detection: server=llama-server, endpoint={url}");
                        detected = await DetectFromLlamaPropsAsync(url).ConfigureAwait(false);
                        break;

                    case LlmServerType.LmStudio:
                        url = serverRoot + "/api/v0/models";
                        Debug.WriteLine($"[DevMind] Context detection: server=LM Studio, endpoint={url}");
                        detected = await DetectFromLmStudioModelsAsync(url).ConfigureAwait(false);
                        break;

                    case LlmServerType.Custom:
                        string customPath = opts?.CustomContextEndpoint?.Trim() ?? "";
                        url = string.IsNullOrEmpty(customPath)
                            ? serverRoot + "/props"
                            : (customPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                ? customPath
                                : serverRoot + (customPath.StartsWith("/") ? customPath : "/" + customPath));
                        Debug.WriteLine($"[DevMind] Context detection: server=Custom, endpoint={url}");
                        detected = await DetectFromCustomEndpointAsync(url).ConfigureAwait(false);
                        break;

                    default:
                        Debug.WriteLine($"[DevMind] Context detection: unknown server type, using default {_contextSize}");
                        return;
                }

                if (detected > 0)
                {
                    _contextSize = detected;
                    _budget = new ContextBudget(_contextSize);
                    Debug.WriteLine($"[DevMind] Context detection: n_ctx={_contextSize}");
                }
                else
                {
                    Debug.WriteLine($"[DevMind] Context detection WARNING: could not read n_ctx from endpoint, using fallback {_contextSize}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DevMind] Context detection WARNING: exception during detection ({ex.GetType().Name}: {ex.Message}), using fallback {_contextSize}");
            }
        }

        /// <summary>Reads n_ctx from llama-server's GET /props → default_generation_settings.n_ctx.</summary>
        private async Task<int> DetectFromLlamaPropsAsync(string url)
        {
            var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return 0;
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var data = JObject.Parse(content);
            return data?["default_generation_settings"]?["n_ctx"]?.Value<int>() ?? 0;
        }

        /// <summary>Reads loaded_context_length from LM Studio's GET /api/v0/models for the loaded model.</summary>
        private async Task<int> DetectFromLmStudioModelsAsync(string url)
        {
            var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return 0;
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var data = JObject.Parse(content);
            if (data?["data"] is JArray models)
            {
                foreach (var model in models)
                {
                    var modelObj = model as JObject;
                    if (modelObj?["state"]?.ToString() == "loaded")
                    {
                        int? ctx = modelObj["loaded_context_length"]?.Value<int>();
                        if (ctx.HasValue && ctx.Value > 0) return ctx.Value;
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// Reads n_ctx from a custom endpoint — tries root n_ctx first, then
        /// default_generation_settings.n_ctx as a fallback.
        /// </summary>
        private async Task<int> DetectFromCustomEndpointAsync(string url)
        {
            var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return 0;
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var data = JObject.Parse(content);
            int? ctx = data?["n_ctx"]?.Value<int>();
            if (ctx.HasValue && ctx.Value > 0) return ctx.Value;
            ctx = data?["default_generation_settings"]?["n_ctx"]?.Value<int>();
            return ctx.HasValue && ctx.Value > 0 ? ctx.Value : 0;
        }

        /// <summary>
        /// Sends a user message to the LLM and streams the response tokens via callbacks.
        /// </summary>
        /// <param name="userMessage">The user's message text.</param>
        /// <param name="onToken">Called for each streamed token fragment.</param>
        /// <param name="onComplete">Called when the full response has been received.</param>
        /// <param name="onError">Called if an error occurs during the request.</param>
        /// <param name="deferCompression">When true, eviction and budget management are skipped.
        /// Pass true during agentic iterations to preserve the KV cache prefix.</param>
        /// <param name="cancellationToken">Cancellation token to abort the request.</param>
        public async Task SendMessageAsync(
            string userMessage,
            Action<string> onToken,
            Action onComplete,
            Action<Exception> onError,
            bool deferCompression = false,
            CancellationToken cancellationToken = default)
        {
            System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] SendMessageAsync ENTER — userMessage length={userMessage?.Length ?? 0}, deferCompression={deferCompression}");

            // Ensure context-size detection has completed before computing budget math.
            // If detection is still in-flight (common on the first message after launch),
            // wait up to 5 seconds. If it times out or the send is cancelled first,
            // proceed with the fallback default and log a warning token.
            if (_contextDetectionTask != null && !_contextDetectionTask.IsCompleted)
            {
                await Task.WhenAny(_contextDetectionTask, Task.Delay(5000, cancellationToken)).ConfigureAwait(false);
                if (!_contextDetectionTask.IsCompleted)
                    onToken($"\n[CONTEXT] Warning: context-size detection timed out — using fallback {_contextSize:N0} tokens.\n");
            }
            _contextDetectionTask = null; // clear after first message; re-set on next Configure()

            // Flush buffered debug messages from Configure/DetectContextSize/ApplyHttpClientSettings.
            if (_pendingDebugLog.Count > 0)
            {
                foreach (string msg in _pendingDebugLog)
                    onToken(msg);
                _pendingDebugLog.Clear();
            }

            UpdateSystemPrompt();

            // ── Append-only context management ─────────────────────────────────────
            // What you store is what you send. What you send is what the cache sees.
            // No post-append modifications. Budget managed by DROP (removing turns)
            // and pre-append truncation only.

            // Drop stale turns before adding the new message
            if (!deferCompression)
            {
                string evictionMsg = EvictStaleContext();
                if (evictionMsg != null)
                    onToken(evictionMsg);
            }

            // Pre-append budget guard: truncate oversized READs before adding to history
            if (!deferCompression)
            {
                string budgetMsg = null;
                userMessage = ApplyPreAppendBudgetGuard(userMessage, out budgetMsg);
                if (budgetMsg != null)
                    onToken(budgetMsg);
            }

            // Append user message — immutable from this point forward
            _conversationHistory.Add(new ChatMessage("user", userMessage, _currentTurn));

            // ── Always-on budget guard ────────────────────────────────────────────
            // Runs on EVERY call including agentic resubmits (deferCompression=true).
            // Context grows fast during agentic iterations; without this guard the
            // window fills up and crashes before TrimConversationHistory can fire.
            {
                _budget.Assess(_conversationHistory, EstimateTokens);
                int totalHistoryTokens = _budget.SystemPromptUsed + _budget.WorkingHistoryUsed + _budget.ProtectedTurnsUsed
                    + EstimateTokens(_conversationHistory[_conversationHistory.Count - 1].Content);

                bool overHard = totalHistoryTokens > (int)(_budget.HistoryHardLimit * 0.95);
                bool overSoft = totalHistoryTokens > (int)(_budget.HistoryHardLimit * 0.80);

                if (overHard)
                {
                    // Hard budget (95%): drop 4 oldest turns aggressively without summaries
                    int trimmed = TrimOldestTurns(4, withSummaries: false);
                    if (trimmed > 0)
                    {
                        int kept = Math.Max(0, (_conversationHistory.Count - 2) / 2);
                        onToken($"\n[CONTEXT] Hard trim: dropped {trimmed} messages — {kept} turns remaining.\n");
                    }
                }
                else if (overSoft)
                {
                    // Soft budget (80%): drop 2 oldest turns with summaries
                    int trimmed = TrimOldestTurns(2, withSummaries: true);
                    if (trimmed > 0)
                    {
                        int kept = Math.Max(0, (_conversationHistory.Count - 2) / 2);
                        onToken($"\n[CONTEXT] Soft trim: dropped {trimmed} messages — {kept} turns remaining.\n");
                    }
                }
            }

            // Budget display — bucket breakdown
            _budget.Assess(_conversationHistory, EstimateTokens);
            int workingUsed   = _budget.WorkingHistoryUsed;
            int workingLimit  = _budget.WorkingHistoryLimit;
            int headroomLimit = _budget.ResponseHeadroomLimit;
            int workingPct    = workingLimit > 0 ? (int)(workingUsed * 100.0 / workingLimit) : 0;

            int grandTotal = 0;
            foreach (var msg in _conversationHistory)
                grandTotal += EstimateTokens(msg.Content);

            if (grandTotal > _budget.HistoryHardLimit)
            {
                onToken($"\n[CONTEXT] CRITICAL: Cannot fit in context window — aborting send\n");
                onError(new InvalidOperationException(
                    "Context window is full. The conversation history exceeds the model's context limit. " +
                    "Please start a new conversation (/restart) or reduce task scope."));
                return;
            }

            onToken($"\n[CONTEXT] Working: {workingUsed:N0} / {workingLimit:N0} ({workingPct}%) | Response headroom: {headroomLimit:N0} reserved\n");

            // Health check — probe the server with a cheap GET before committing the
            // full chat payload. If the probe times out or fails, RecreateHttpClient()
            // swaps in a fresh connection pool so the chat request goes over a live TCP connection.
            await EnsureConnectionHealthAsync(cancellationToken).ConfigureAwait(false);

            string modelName = DevMindOptions.Instance.ModelName;
            string requestJson = BuildRequestJson(modelName);

            string url = _baseUrl + "/chat/completions";

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };

            System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] About to POST to {url} — requestJson length={requestJson.Length}");

            try
            {
                var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var fullResponse = new StringBuilder();
                bool firstTokenReceived = false;
                var firstTokenDeadline = DateTime.UtcNow.AddMinutes(
                    DevMindOptions.Instance.FirstTokenTimeoutMinutes);

                while (true)
                {
                    Task<string> readTask = reader.ReadLineAsync();

                    if (!firstTokenReceived)
                    {
                        // Phase 1: race each read against the first-token deadline.
                        var remaining = firstTokenDeadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero)
                            throw new TimeoutException(
                                $"No response received within {DevMindOptions.Instance.FirstTokenTimeoutMinutes} minute(s). " +
                                "The server may still be processing the prompt. " +
                                "Increase 'First Token Timeout' in Tools > Options > DevMind.");

                        var timeoutTask = Task.Delay(remaining, cancellationToken);
                        var winner = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);

                        if (winner == timeoutTask)
                        {
                            // If user cancelled, ThrowIfCancellationRequested gives clean exit.
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new TimeoutException(
                                $"No response received within {DevMindOptions.Instance.FirstTokenTimeoutMinutes} minute(s). " +
                                "The server may still be processing the prompt. " +
                                "Increase 'First Token Timeout' in Tools > Options > DevMind.");
                        }
                    }

                    // Phase 2 (after first token): race each read against a short
                    // cancellation-aware delay so Stop responds promptly even when
                    // the server holds the connection open between SSE lines.
                    if (firstTokenReceived)
                    {
                        while (!readTask.IsCompleted)
                        {
                            var pollTask = Task.Delay(500, cancellationToken);
                            await Task.WhenAny(readTask, pollTask).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    string line = await readTask.ConfigureAwait(false);
                    if (line == null)
                        break;

                    cancellationToken.ThrowIfCancellationRequested();

                    if (!line.StartsWith("data: ", StringComparison.Ordinal))
                        continue;

                    string data = line.Substring(6).Trim();

                    if (data == "[DONE]")
                        break;

                    string token = ParseContentDelta(data);
                    if (token != null)
                    {
                        firstTokenReceived = true;
                        fullResponse.Append(token);
                        onToken(token);
                    }
                }

                _conversationHistory.Add(new ChatMessage("assistant", fullResponse.ToString(), _currentTurn));
                onComplete();
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

        /// <summary>Gets the current task scratchpad content.</summary>
        public string TaskScratchpad => _taskScratchpad;

        /// <summary>Gets the current turn number.</summary>
        public int CurrentTurn => _currentTurn;

        /// <summary>
        /// Advances the turn counter. Call this once per user-initiated send from the input box.
        /// Do NOT call for agentic resubmits — those share the current turn.
        /// </summary>
        public void IncrementTurn() => _currentTurn++;

        /// <summary>
        /// Clears the conversation history and resets to the system prompt only.
        /// </summary>
        /// <param name="preserveScratchpad">When true, the task scratchpad is preserved across the
        /// reset so block-by-block step state survives the context clear between steps.</param>
        public void ClearHistory(bool preserveScratchpad = false)
        {
            _conversationHistory.Clear();
            _conversationHistory.Add(new ChatMessage("system", GetSystemPrompt()));
            _filesReadThisSession.Clear();
            if (!preserveScratchpad)
            {
                _taskScratchpad = "";
                _currentTurn = 0;
            }
        }

        /// <summary>
        /// Stores the LLM's latest SCRATCHPAD content, trimming from the top if it exceeds
        /// ScratchpadMaxTokens. Returns a log line if trimming occurred, null otherwise.
        /// </summary>
        public string UpdateScratchpad(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                _taskScratchpad = "";
                return null;
            }

            // Trim from the top (oldest lines) until within budget
            string trimmed = content;
            string logMsg  = null;
            if (EstimateTokens(trimmed) > ScratchpadMaxTokens)
            {
                string[] lines = trimmed.Split('\n');
                int lo = 0;
                while (lo < lines.Length && EstimateTokens(string.Join("\n", lines, lo, lines.Length - lo)) > ScratchpadMaxTokens)
                    lo++;
                trimmed = string.Join("\n", lines, lo, lines.Length - lo);
                logMsg  = "\n[CONTEXT] Scratchpad trimmed to 200 tokens\n";
            }

            _taskScratchpad = trimmed;
            return logMsg;
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
                // Skip replacement if the prompt text is identical — preserves the KV cache prefix.
                if (string.Equals(_conversationHistory[0].Content, prompt, StringComparison.Ordinal))
                    return;

                _conversationHistory[0] = new ChatMessage("system", prompt);
            }
            else
            {
                _conversationHistory.Insert(0, new ChatMessage("system", prompt));
            }
        }

        private static readonly Regex _reWarnings = new Regex(@"(\d+)\s+Warning", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ── Tiered Context Eviction ─────────────────────────────────────────────

        /// <summary>
        /// Drops stale messages based on their age relative to the current turn.
        /// Append-only: messages are only REMOVED, never rewritten. No warm/cold tiers.
        /// Drop summaries are inserted so the model knows what was removed.
        /// Returns a log string to emit via onToken, or null if nothing changed.
        /// </summary>
        public string EvictStaleContext()
        {
            var mode = DevMindOptions.Instance.ContextEviction;
            bool showDebug = DevMindOptions.Instance.ShowDebugOutput;

            var diagLog = new System.Text.StringBuilder();

            if (showDebug)
            {
                diagLog.AppendLine($"\n[CONTEXT] Eviction check: turn={_currentTurn}, mode={mode}, message count={_conversationHistory.Count}");
            }

            if (mode == ContextEvictionMode.Off)
            {
                System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] Eviction: mode=Off, skipped");
                return showDebug ? diagLog.ToString() : null;
            }

            // Drop age: messages older than this are removed entirely.
            // No warm/cold tiers — append-only means we only DROP, never rewrite.
            int dropAge = mode == ContextEvictionMode.Aggressive ? 5 : 8;

            int dropped = 0;
            var indicesToRemove = new List<int>();
            var dropSummaryParts = new List<string>();

            for (int i = 1; i < _conversationHistory.Count; i++)
            {
                var msg = _conversationHistory[i];
                int age = _currentTurn - msg.Turn;

                if (age <= dropAge) continue;
                if (IsPinnedMessage(msg)) continue;

                // Build snippet before dropping
                string snippet = BuildDropSnippet(msg);
                if (snippet != null) dropSummaryParts.Add(snippet);

                indicesToRemove.Add(i);
                dropped++;
            }

            // Remove in reverse index order to preserve indices
            if (indicesToRemove.Count > 0)
            {
                for (int i = indicesToRemove.Count - 1; i >= 0; i--)
                {
                    int idx = indicesToRemove[i];
                    if (idx > 0 && idx < _conversationHistory.Count)
                        _conversationHistory.RemoveAt(idx);
                }

                // Insert drop summary so the model knows what happened
                if (dropSummaryParts.Count > 0)
                {
                    string summary = $"[DROPPED] {dropped} message(s) evicted: {string.Join(", ", dropSummaryParts)}";
                    _conversationHistory.Insert(1, new ChatMessage("system", summary));
                }
            }

            if (dropped == 0)
            {
                if (showDebug)
                    diagLog.AppendLine("[CONTEXT] Eviction result: no messages affected");
                return diagLog.Length > 0 ? diagLog.ToString() : null;
            }

            diagLog.AppendLine($"[CONTEXT] Eviction: {dropped} message(s) dropped");

            if (showDebug)
            {
                diagLog.AppendLine($"[DEBUG] Dropped indices: [{string.Join(", ", indicesToRemove)}]");
            }

            return diagLog.ToString();
        }

        /// <summary>
        /// Returns true if the message should never be evicted regardless of age.
        /// Pinned: system prompt, DevMind.md content.
        /// </summary>
        private bool IsPinnedMessage(ChatMessage msg)
        {
            if (msg.Role == "system") return true;

            string c = msg.Content;
            if (c.IndexOf("[DevMind.md]", StringComparison.Ordinal) >= 0) return true;

            return false;
        }

        /// <summary>
        /// Classifies a [SHELL-RESULT:] block and returns a compact summary string.
        /// Used for pre-append compression — classify shell output before adding to history.
        /// Classification priority: build success > build failure > non-build error > success.
        /// </summary>
        private string BuildShellSummary(string command, string blockContent)
        {
            bool isBuildSuccess =
                blockContent.IndexOf("Build succeeded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                blockContent.IndexOf("0 Error(s)", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isBuildSuccess)
            {
                var warnMatch = _reWarnings.Match(blockContent);
                int warnings = warnMatch.Success ? int.Parse(warnMatch.Groups[1].Value) : 0;
                return $"[SQUEEZED][SHELL:OK] {command} — BUILD SUCCEEDED ({warnings} warnings)";
            }

            bool hasError =
                blockContent.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                blockContent.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0;

            // Build failure: MSBuild "Build FAILED" or any "N Error(s)" line.
            bool isBuildFailure = hasError && (
                blockContent.IndexOf("Build FAILED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                blockContent.IndexOf("Error(s)", StringComparison.OrdinalIgnoreCase) >= 0);

            if (isBuildFailure)
            {
                var allErrorLines = new List<string>();
                foreach (var line in blockContent.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length > 0 &&
                        trimmed.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                        allErrorLines.Add(trimmed);
                }

                var sb = new StringBuilder($"[SQUEEZED][SHELL:ERR] {command} — BUILD FAILED");
                int keep = allErrorLines.Count < 3 ? allErrorLines.Count : 3;
                for (int j = 0; j < keep; j++)
                {
                    string line = allErrorLines[j];
                    if (line.Length > 120) line = line.Substring(0, 120);
                    sb.Append("\n  ").Append(line);
                }
                int omitted = allErrorLines.Count - keep;
                if (omitted > 0)
                    sb.Append($"\n  ({omitted} more errors omitted)");
                return sb.ToString();
            }

            if (hasError)
            {
                // Non-build failure: keep the first 5 non-empty output lines.
                var kept = new List<string>();
                foreach (var line in blockContent.Split('\n'))
                {
                    string trimmed = line.TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;
                    kept.Add(trimmed);
                    if (kept.Count >= 5) break;
                }
                return $"[SQUEEZED][SHELL:ERR] {command} — failed\n{string.Join("\n", kept)}";
            }

            // Non-build success: no error indicators.
            return $"[SQUEEZED][SHELL:OK] {command} — completed";
        }

        // Legacy regex fields — retained for compatibility; outline generation now uses GenerateCSharpOutline.
        private static readonly Regex _reClass  = new Regex(@"class\s+(\w+)",                                              RegexOptions.Compiled);
        private static readonly Regex _reMethod = new Regex(@"(?:private|public|protected|internal)\s+\S+\s+(\w+)\s*\(", RegexOptions.Compiled);

        // ── Outline helpers ────────────────────────────────────────────────────

        /// <summary>Strips XML tags from a string using a char-by-char scan (no regex).</summary>
        private static string StripXmlTags(string s)
        {
            var sb = new StringBuilder();
            bool inTag = false;
            foreach (char c in s)
            {
                if      (c == '<') inTag = true;
                else if (c == '>') inTag = false;
                else if (!inTag)   sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>Returns true if the trimmed line starts with a C# access or member modifier.</summary>
        private static bool StartsWithAccessModifier(string t)
        {
            return t.StartsWith("public ")    || t.StartsWith("private ")   ||
                   t.StartsWith("protected ") || t.StartsWith("internal ")  ||
                   t.StartsWith("static ")    || t.StartsWith("abstract ")  ||
                   t.StartsWith("override ")  || t.StartsWith("virtual ")   ||
                   t.StartsWith("sealed ")    || t.StartsWith("extern ")    ||
                   t.StartsWith("new ")       || t.StartsWith("readonly ");
        }

        /// <summary>
        /// Returns true if <paramref name="keyword"/> appears as a whole word in <paramref name="t"/>
        /// (not part of a longer identifier).
        /// </summary>
        private static bool ContainsTypeKeyword(string t, string keyword)
        {
            int idx = 0;
            while ((idx = t.IndexOf(keyword, idx, StringComparison.Ordinal)) >= 0)
            {
                bool validBefore = idx == 0 || (!char.IsLetterOrDigit(t[idx - 1]) && t[idx - 1] != '_');
                int  afterIdx    = idx + keyword.Length;
                bool validAfter  = afterIdx >= t.Length || (!char.IsLetterOrDigit(t[afterIdx]) && t[afterIdx] != '_');
                if (validBefore && validAfter) return true;
                idx++;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the trimmed line looks like a C# declaration worth including in the outline.
        /// Excludes field declarations, statements, blank lines, and using directives.
        /// </summary>
        private static bool IsCSharpDeclaration(string t)
        {
            // Namespace
            if (t.StartsWith("namespace ")) return true;

            // Type declarations (keyword may be preceded by access modifiers)
            if (ContainsTypeKeyword(t, "class")     || ContainsTypeKeyword(t, "struct") ||
                ContainsTypeKeyword(t, "interface") || ContainsTypeKeyword(t, "enum")   ||
                ContainsTypeKeyword(t, "record"))
                return true;

            // Must have an access/member modifier to be a typed member declaration
            if (!StartsWithAccessModifier(t)) return false;

            // Method, constructor, indexer: has an opening parenthesis
            if (t.IndexOf('(') >= 0) return true;

            // Property with inline accessor block or expression body
            if (t.IndexOf("{ get",  StringComparison.Ordinal) >= 0 ||
                t.IndexOf("{get",   StringComparison.Ordinal) >= 0 ||
                t.IndexOf("{ set",  StringComparison.Ordinal) >= 0 ||
                t.IndexOf("{set",   StringComparison.Ordinal) >= 0 ||
                t.IndexOf("=>",     StringComparison.Ordinal) >= 0)
                return true;

            // Anything else starting with an access modifier (plain field) → skip
            return false;
        }

        /// <summary>
        /// Generates a structural outline of a C# file using line-by-line parsing.
        /// Includes: namespace, type declarations, method/property signatures, first /// summary line.
        /// Excludes: using directives, field declarations, method bodies, blank lines.
        /// </summary>
        public static string GenerateCSharpOutline(string content)
        {
            var    sb            = new StringBuilder();
            int    braceDepth    = 0;
            string pendingDoc    = null;
            int    pendingDocLine = 0;
            int    lineNumber    = 0;

            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    string t = line.Trim();

                    if (t.Length == 0) { pendingDoc = null; continue; }

                    // XML doc comment: capture first non-empty content line of <summary>
                    if (t.StartsWith("///"))
                    {
                        if (pendingDoc == null)
                        {
                            string text = StripXmlTags(t.Substring(3));
                            if (text.Length > 0)
                            {
                                pendingDoc     = text;
                                pendingDocLine = lineNumber;
                            }
                        }
                        continue;
                    }

                    // Other comments, preprocessor, using directives: skip and clear doc
                    if (t.StartsWith("//") || t.StartsWith("#") || t.StartsWith("using ") ||
                        t.StartsWith("[assembly"))
                    {
                        pendingDoc = null;
                        continue;
                    }

                    // Attribute lines (e.g., [Obsolete], [SuppressMessage]): skip but
                    // preserve pendingDoc so it applies to the following declaration.
                    if (t.StartsWith("[") && !IsCSharpDeclaration(t))
                        continue;

                    // Count braces on this line once (used for both sig trimming and depth update).
                    int lineOpens = 0, lineCloses = 0;
                    foreach (char c in t)
                    {
                        if      (c == '{') lineOpens++;
                        else if (c == '}') lineCloses++;
                    }

                    // Emit declaration if we're at a shallow scope (not inside a method body).
                    // braceDepth 0 = namespace/file, 1 = outer type, 2 = member/nested type,
                    // 3 = member of nested type. Depth 4+ is deep inside method bodies — stop there.
                    // Valid C# cannot have access-modifier declarations inside method bodies, so
                    // IsCSharpDeclaration produces no false positives at depth 3.
                    if (braceDepth <= 3 && IsCSharpDeclaration(t))
                    {
                        string indent = braceDepth == 0 ? "" :
                                        braceDepth == 1 ? "  " :
                                        braceDepth == 2 ? "    " : "      ";

                        if (pendingDoc != null)
                        {
                            sb.AppendLine($"{pendingDocLine,6}: {indent}/// {pendingDoc}");
                            pendingDoc = null;
                        }

                        // Show full line when braces are balanced (e.g., auto-properties).
                        // Trim at the first '{' when the line opens a multi-line body.
                        string sig;
                        if (lineOpens > lineCloses)
                        {
                            int braceIdx = t.IndexOf('{');
                            sig = braceIdx > 0 ? t.Substring(0, braceIdx).TrimEnd() : t;
                        }
                        else
                        {
                            sig = t;
                        }

                        sb.AppendLine($"{lineNumber,6}: {indent}{sig}");
                    }
                    else
                    {
                        pendingDoc = null;
                    }

                    // Update brace depth (after emission so depth reflects outer scope at emit time).
                    braceDepth += lineOpens - lineCloses;
                    if (braceDepth < 0) braceDepth = 0;
                }
            }

            string result = sb.ToString().TrimEnd();
            return result.Length > 0 ? result : "(no declarations found)";
        }

        /// <summary>Extracts a XAML element name and key attributes for the outline.</summary>
        private static string ExtractXamlTag(string line)
        {
            int start   = line.IndexOf('<') + 1;
            int nameEnd = start;
            while (nameEnd < line.Length && line[nameEnd] != ' ' && line[nameEnd] != '>' && line[nameEnd] != '/')
                nameEnd++;
            string name = line.Substring(start, nameEnd - start);

            var attrs = new List<string>();
            foreach (string attr in new[] { "x:Name", "x:Class", "x:Key", "Name" })
            {
                int idx = line.IndexOf(attr + "=\"", StringComparison.Ordinal);
                if (idx < 0) continue;
                int valStart = idx + attr.Length + 2;
                int valEnd   = line.IndexOf('"', valStart);
                if (valEnd > valStart)
                    attrs.Add($"{attr}=\"{line.Substring(valStart, valEnd - valStart)}\"");
            }
            return attrs.Count > 0 ? $"<{name} {string.Join(" ", attrs)}>" : $"<{name}>";
        }

        /// <summary>Generates a XAML element tree outline to depth 2 with key attributes.</summary>
        private static string GenerateXamlOutline(string content)
        {
            var sb    = new StringBuilder();
            int depth = 0;

            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string t = line.Trim();
                    if (!t.StartsWith("<") || t.StartsWith("<!--") || t.StartsWith("<?")) continue;

                    bool closing     = t.StartsWith("</");
                    bool selfClosing = t.EndsWith("/>") || (t.Contains("/>") && !closing);

                    if (closing) { depth--; continue; }
                    if (depth <= 2)
                        sb.AppendLine($"{new string(' ', depth * 2)}{ExtractXamlTag(t)}");
                    if (!selfClosing) depth++;
                }
            }

            string result = sb.ToString().TrimEnd();
            return result.Length > 0 ? result : "(empty)";
        }

        /// <summary>Generates a JSON outline showing top-level keys with value types.</summary>
        private static string GenerateJsonOutline(string content)
        {
            var sb    = new StringBuilder();
            int depth = 0;

            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string t = line.Trim().TrimEnd(',');

                    // Update depth based on bracket characters
                    foreach (char c in t)
                    {
                        if (c == '{' || c == '[') depth++;
                        else if (c == '}' || c == ']') depth--;
                    }

                    if (depth != 1) continue;

                    // Match "key": value at top level (depth was 1 before this line's braces)
                    if (!t.StartsWith("\"")) continue;
                    int keyEnd = t.IndexOf("\":", 1);
                    if (keyEnd < 0) continue;

                    string key  = t.Substring(0, keyEnd + 1);
                    string rest = t.Substring(keyEnd + 2).Trim();
                    string valueType = rest.StartsWith("{")  ? "object" :
                                       rest.StartsWith("[")  ? "array"  :
                                       rest.StartsWith("\"") ? "string" :
                                       rest == "true" || rest == "false" ? "bool" :
                                       rest == "null" ? "null" : "number";
                    sb.AppendLine($"  {key}: {valueType}");
                }
            }

            string result = sb.ToString().TrimEnd();
            return result.Length > 0 ? result : "(empty)";
        }

        /// <summary>Generates an outline of MSBuild project files showing PropertyGroup/ItemGroup contents.</summary>
        private static string GenerateProjOutline(string content)
        {
            var    sb           = new StringBuilder();
            bool   inGroup      = false;

            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string t = line.Trim();

                    if (t.StartsWith("<PropertyGroup") || t.StartsWith("<ItemGroup"))
                    {
                        sb.AppendLine(t.Replace(">", "").Replace("/", "").Trim() + ">");
                        inGroup = true;
                        continue;
                    }
                    if (t.StartsWith("</PropertyGroup") || t.StartsWith("</ItemGroup"))
                    {
                        inGroup = false;
                        continue;
                    }
                    if (!inGroup || !t.StartsWith("<") || t.StartsWith("<!--")) continue;

                    // Extract element name
                    int nameEnd = 1;
                    while (nameEnd < t.Length && t[nameEnd] != ' ' && t[nameEnd] != '>' && t[nameEnd] != '/')
                        nameEnd++;
                    string elem = t.Substring(1, nameEnd - 1);

                    // Extract inner text (for PropertyGroup values)
                    int innerStart = t.IndexOf('>') + 1;
                    int innerEnd   = t.LastIndexOf('<');
                    string value   = innerStart > 0 && innerEnd > innerStart
                        ? t.Substring(innerStart, innerEnd - innerStart).Trim()
                        : "";
                    if (value.Length > 60) value = value.Substring(0, 60) + "...";

                    sb.AppendLine(value.Length > 0 ? $"  <{elem}>{value}" : $"  <{elem}>");
                }
            }

            string result = sb.ToString().TrimEnd();
            return result.Length > 0 ? result : "(empty)";
        }

        /// <summary>Generates a head+tail outline for non-code file types.</summary>
        private static string GenerateGenericOutline(string content)
        {
            string[] lines = content.Split('\n');
            if (lines.Length <= 8) return content.TrimEnd();

            const int head = 5, tail = 3;
            var parts = new StringBuilder();
            for (int k = 0; k < head && k < lines.Length; k++)
                parts.AppendLine(lines[k].TrimEnd('\r'));
            parts.Append($"... ({lines.Length - head - tail} lines) ...");
            for (int k = lines.Length - tail; k < lines.Length; k++)
                parts.Append("\n" + lines[k].TrimEnd('\r'));
            return parts.ToString().TrimEnd();
        }

        /// <summary>Dispatches to the appropriate outline generator based on file extension.</summary>
        internal static string GenerateOutline(string filename, string blockContent)
        {
            string ext = Path.GetExtension(filename ?? "").TrimStart('.').ToLowerInvariant();
            switch (ext)
            {
                case "cs":      return GenerateCSharpOutline(blockContent);
                case "xaml":    return GenerateXamlOutline(blockContent);
                case "json":
                case "jsonc":   return GenerateJsonOutline(blockContent);
                case "csproj":
                case "slnx":
                case "props":
                case "targets": return GenerateProjOutline(blockContent);
                default:        return GenerateGenericOutline(blockContent);
            }
        }

        private const int BudgetGuardKeepHead = 200;
        private const int BudgetGuardKeepTail = 50;

        /// <summary>
        /// Pre-append budget guard: truncates oversized [READ:] blocks in the userMessage string
        /// BEFORE it is added to _conversationHistory. Preserves append-only invariant.
        /// Returns the (possibly modified) userMessage and an optional log string.
        /// </summary>
        private string ApplyPreAppendBudgetGuard(string userMessage, out string logMsg)
        {
            logMsg = null;
            if (string.IsNullOrEmpty(userMessage)) return userMessage;
            if (!userMessage.Contains("[READ:")) return userMessage;

            // Estimate what total would be with this message added
            int currentTotal = EstimateHistoryTokens();
            int msgTokens = EstimateTokens(userMessage);
            if (currentTotal + msgTokens <= _budget.HistoryHardLimit) return userMessage;

            // Truncate oversized READ blocks to head+tail
            var truncatedFiles = new List<string>();
            int tagStart = userMessage.IndexOf("[READ:", StringComparison.Ordinal);
            while (tagStart >= 0)
            {
                bool isSqueezed = tagStart >= 10 &&
                    string.Equals(userMessage.Substring(tagStart - 10, 10), "[SQUEEZED]", StringComparison.Ordinal);

                int tagEnd = userMessage.IndexOf(']', tagStart);
                if (tagEnd < 0) break;

                string filename = userMessage.Substring(tagStart + 6, tagEnd - tagStart - 6);
                bool isSpecial = string.Equals(filename, "SUPERSEDED", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(filename, "OUTLINE", StringComparison.OrdinalIgnoreCase);

                if (!isSqueezed && !isSpecial)
                {
                    int contentStart = tagEnd + 1;
                    if (contentStart < userMessage.Length && userMessage[contentStart] == '\n')
                        contentStart++;

                    int nextTag = userMessage.IndexOf("\n[", contentStart, StringComparison.Ordinal);
                    int blockEnd = nextTag >= 0 ? nextTag : userMessage.Length;

                    string blockContent = userMessage.Substring(contentStart, blockEnd - contentStart);
                    string[] lines = blockContent.Split('\n');
                    int lineCount = lines.Length;

                    if (lineCount > BudgetGuardKeepHead + BudgetGuardKeepTail)
                    {
                        int omitted = lineCount - BudgetGuardKeepHead - BudgetGuardKeepTail;
                        var head = new string[BudgetGuardKeepHead];
                        var tail = new string[BudgetGuardKeepTail];
                        Array.Copy(lines, 0, head, 0, BudgetGuardKeepHead);
                        Array.Copy(lines, lineCount - BudgetGuardKeepTail, tail, 0, BudgetGuardKeepTail);
                        string truncated = string.Join("\n", head)
                            + $"\n[... {omitted} lines omitted — use READ with line range to see specific sections ...]\n"
                            + string.Join("\n", tail);

                        userMessage = userMessage.Substring(0, contentStart) + truncated
                            + (nextTag >= 0 ? userMessage.Substring(nextTag) : "");

                        truncatedFiles.Add($"{Regex.Replace(filename, @":\d+(?:-\d+)?$", "")} ({lineCount}→{BudgetGuardKeepHead + BudgetGuardKeepTail} lines)");

                        // Re-check: if now within budget, stop truncating
                        if (currentTotal + EstimateTokens(userMessage) <= _budget.HistoryHardLimit)
                            break;

                        // Restart scan — string changed
                        tagStart = userMessage.IndexOf("[READ:", 0, StringComparison.Ordinal);
                        continue;
                    }
                }

                tagStart = userMessage.IndexOf("[READ:", tagEnd + 1, StringComparison.Ordinal);
            }

            if (truncatedFiles.Count > 0)
                logMsg = $"\n[CONTEXT] Budget guard: Truncated {string.Join(", ", truncatedFiles)} to fit budget.\n";

            return userMessage;
        }

        /// <summary>
        /// Removes the oldest user/assistant pairs from history, keeping at most <paramref name="maxTurns"/> pairs.
        /// Always preserves index 0 (system) and the last entry (pending user message).
        /// Returns the number of messages removed.
        /// </summary>
        private int TrimConversationHistory(int maxTurns)
        {
            // Layout: [0]=system, [1..Count-2]=prior turns (user/assistant pairs), [Count-1]=current user
            int priorMessages = _conversationHistory.Count - 2;
            if (priorMessages <= 0)
                return 0;

            int priorPairs = priorMessages / 2;
            if (priorPairs <= maxTurns)
                return 0;

            int pairsToRemove = priorPairs - maxTurns;
            int messagesToRemove = pairsToRemove * 2;

            // Build a drop summary before removing — preserves awareness of what happened
            var summaryParts = new List<string>();
            for (int i = 1; i <= messagesToRemove && i < _conversationHistory.Count; i++)
            {
                var msg = _conversationHistory[i];
                string snippet = BuildDropSnippet(msg);
                if (snippet != null) summaryParts.Add(snippet);
            }
            string dropSummary = summaryParts.Count > 0
                ? $"[DROPPED] Turns 1-{pairsToRemove}: {string.Join(", ", summaryParts)}"
                : $"[DROPPED] {pairsToRemove} turn(s) removed for budget";

            _conversationHistory.RemoveRange(1, messagesToRemove);

            // Insert summary as a system-role message so the model knows what happened
            _conversationHistory.Insert(1, new ChatMessage("system", dropSummary));

            return messagesToRemove;
        }

        /// <summary>
        /// Drops a specific number of oldest turn-pairs from history.
        /// Always preserves index 0 (system) and the last entry (pending user message).
        /// When <paramref name="withSummaries"/> is true, inserts a drop summary; otherwise drops silently.
        /// Also skips any pinned content (DevMind.md, SCRATCHPAD) at the start of history.
        /// Returns the number of messages actually removed.
        /// </summary>
        private int TrimOldestTurns(int pairsToRemove, bool withSummaries)
        {
            // Layout: [0]=system, [1..Count-2]=prior turns, [Count-1]=current user
            // Find the first removable index (skip pinned system messages after index 0)
            int startIdx = 1;
            while (startIdx < _conversationHistory.Count - 1)
            {
                string c = _conversationHistory[startIdx].Content;
                if (c.Contains("[DevMind.md]") || c.Contains("[TASK STATE]") || c.Contains("[DROPPED]"))
                    startIdx++;
                else
                    break;
            }

            int removableMessages = _conversationHistory.Count - 1 - startIdx; // exclude last (current user)
            if (removableMessages <= 0)
                return 0;

            int removablePairs = removableMessages / 2;
            int actualPairs = Math.Min(pairsToRemove, removablePairs);
            if (actualPairs <= 0)
                return 0;

            int messagesToRemove = actualPairs * 2;

            if (withSummaries)
            {
                var summaryParts = new List<string>();
                for (int i = startIdx; i < startIdx + messagesToRemove && i < _conversationHistory.Count; i++)
                {
                    string snippet = BuildDropSnippet(_conversationHistory[i]);
                    if (snippet != null) summaryParts.Add(snippet);
                }
                string dropSummary = summaryParts.Count > 0
                    ? $"[DROPPED] {actualPairs} turn(s): {string.Join(", ", summaryParts)}"
                    : $"[DROPPED] {actualPairs} turn(s) removed for budget";

                _conversationHistory.RemoveRange(startIdx, messagesToRemove);
                _conversationHistory.Insert(startIdx, new ChatMessage("system", dropSummary));
            }
            else
            {
                _conversationHistory.RemoveRange(startIdx, messagesToRemove);
            }

            return messagesToRemove;
        }

        /// <summary>
        /// Builds a short snippet describing a message for drop summaries.
        /// </summary>
        private static string BuildDropSnippet(ChatMessage msg)
        {
            string c = msg.Content;
            if (c.Length == 0) return null;

            if (c.Contains("[READ:"))
            {
                var files = new List<string>();
                int sf = 0;
                while (true)
                {
                    int ts = c.IndexOf("[READ:", sf, StringComparison.Ordinal);
                    if (ts < 0) break;
                    int te = c.IndexOf(']', ts);
                    if (te < 0) break;
                    string fn = c.Substring(ts + 6, te - ts - 6);
                    if (!string.Equals(fn, "SUPERSEDED", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(fn, "OUTLINE", StringComparison.OrdinalIgnoreCase))
                        files.Add(fn);
                    sf = te + 1;
                }
                if (files.Count > 0) return $"READ {string.Join(", ", files)}";
            }

            if (c.Contains("[PATCH-RESULT:")) return "PATCH applied";
            if (c.Contains("[SHELL-RESULT:"))
            {
                bool ok = c.IndexOf("Build succeeded", StringComparison.OrdinalIgnoreCase) >= 0;
                return ok ? "build succeeded" : "shell command";
            }

            if (msg.Role == "assistant")
            {
                string t = c.Trim();
                if (t.Length > 40) t = t.Substring(0, 40) + "...";
                return $"response: {t}";
            }

            return null;
        }

        private string BuildRequestJson(string modelName)
        {
            // Append-only: serialize _conversationHistory exactly as-is.
            // No phantom injections — the JSON payload is a 1:1 mirror of the list.
            var messages = new JArray();
            int lastIdx   = _conversationHistory.Count - 1;
            for (int mi = 0; mi <= lastIdx; mi++)
            {
                var msg = _conversationHistory[mi];
                messages.Add(new JObject
                {
                    ["role"]    = msg.Role,
                    ["content"] = msg.Content
                });
            }

            var request = new JObject
            {
                ["messages"] = messages,
                ["stream"] = true
            };

            if (!DevMindOptions.Instance.ShowLlmThinking)
            {
                request["thinking"] = new JObject { ["type"] = "disabled" };
            }

            if (!string.IsNullOrWhiteSpace(modelName))
            {
                request["model"] = modelName;
            }

            // Include tool definitions when DirectiveMode is ToolUse or Auto
            var directiveMode = DevMindOptions.Instance.DirectiveMode;
            if (directiveMode != DirectiveMode.TextDirective)
            {
                request["tools"] = ToolRegistry.BuildToolsArray();
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
    /// Immutable by design — once added to _conversationHistory, content never changes.
    /// Context management uses DROP (removing entire messages) rather than rewriting.
    /// </summary>
    internal sealed class ChatMessage
    {
        /// <summary>The role of the message sender (system, user, or assistant).</summary>
        public string Role { get; }

        /// <summary>The text content of the message.</summary>
        public string Content { get; }

        /// <summary>The turn number this message belongs to. Used by context eviction.</summary>
        public int Turn { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ChatMessage"/> class.
        /// </summary>
        /// <param name="role">The role (system, user, or assistant).</param>
        /// <param name="content">The message text.</param>
        /// <param name="turn">The turn number (0 for system prompt, incremented per user-initiated send).</param>
        public ChatMessage(string role, string content, int turn = 0)
        {
            Role = role;
            Content = content;
            Turn = turn;
        }
    }
}
