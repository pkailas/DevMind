// File: LlmClient.cs  v7.12
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

        /// <summary>
        /// Nearline RAM cache for content trimmed by MicroCompactToolResults.
        /// Allows instant recall of trimmed tool results and READ blocks.
        /// </summary>
        public NearlineCache NearlineCache { get; } = new NearlineCache();

        private string _taskScratchpad = "";
        private const int ScratchpadMaxTokens = 200;
        private int _currentTurn;

        /// <summary>
        /// After the first compaction, stores the working-budget percentage target.
        /// Subsequent compactions trim until at or below this watermark.
        /// 0 means no watermark set yet (first compaction targets 40%).
        /// Reset to 0 on ClearHistory().
        /// </summary>
        private int _microCompactWatermark;

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

        /// <summary>
        /// Parsed tool calls from the last LLM response, or null if the response
        /// contained no tool_calls. Set during <see cref="SendMessageAsync"/>.
        /// </summary>
        public List<ToolCallResult> LastToolCalls { get; private set; }

        // ── Server-reported timings from last SSE response ──────────────────
        public int LastPromptTokens { get; private set; }
        public int LastGeneratedTokens { get; private set; }
        public double LastPromptMs { get; private set; }
        public double LastGeneratedMs { get; private set; }
        public int LastContextUsed { get; private set; }   // n_past
        public int ServerContextSize { get; private set; } // n_ctx

        /// <summary>
        /// Conversation history count at the time LastContextUsed was last updated.
        /// Messages at indices &gt;= this value have not yet been counted by the server.
        /// </summary>
        private int _lastServerCountIndex;

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

            // Tier 1: Trim stale tool result content in-place — runs ALWAYS, including agentic resubmits
            {
                string compactMsg = MicroCompactToolResults();
                if (compactMsg != null)
                    onToken(compactMsg);
            }

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
            // Tool_use sessions use softer thresholds (70%/85%) because tool_call/result
            // groups are heavier per logical turn — earlier trimming prevents cliff-edge drops.
            {
                // Skip soft/hard trim when MicroCompact is the active strategy.
                // MicroCompact handles context pressure at its own threshold.
                // The soft trim interferes by changing the conversation prefix (breaks KV cache
                // on hybrid models like Mamba) and reclaims too little to prevent server 500s.
                int microCompactThreshold = DevMindOptions.Instance.MicroCompactThreshold;
                if (microCompactThreshold > 0)
                {
                    // Let MicroCompact handle it — skip soft/hard trim entirely
                }
                else
                {
                    // Original soft/hard trim logic for when MicroCompact is disabled
                    _budget.Assess(_conversationHistory, EstimateTokens);
                    int totalHistoryTokens = _budget.SystemPromptUsed + _budget.WorkingHistoryUsed + _budget.ProtectedTurnsUsed
                        + EstimateTokens(_conversationHistory[_conversationHistory.Count - 1].Content);

                    // Detect tool_use session: any "tool" role message in history
                    bool isToolUseSession = false;
                    for (int ti = 1; ti < _conversationHistory.Count; ti++)
                    {
                        if (_conversationHistory[ti].Role == "tool")
                        {
                            isToolUseSession = true;
                            break;
                        }
                    }

                    double hardPct = isToolUseSession ? 0.85 : 0.95;
                    double softPct = isToolUseSession ? 0.70 : 0.80;
                    bool overHard = totalHistoryTokens > (int)(_budget.HistoryHardLimit * hardPct);
                    bool overSoft = totalHistoryTokens > (int)(_budget.HistoryHardLimit * softPct);

                    if (overHard)
                    {
                        // Hard budget: drop 4 oldest turn-groups aggressively without summaries
                        int trimmed = TrimOldestTurns(4, withSummaries: false);
                        if (trimmed > 0)
                        {
                            int kept = Math.Max(0, (_conversationHistory.Count - 2) / 2);
                            onToken($"\n[CONTEXT] Hard trim: dropped {trimmed} messages — {kept} turns remaining.\n");
                        }
                    }
                    else if (overSoft)
                    {
                        // Soft budget: drop 2 oldest turn-groups with summaries
                        int trimmed = TrimOldestTurns(2, withSummaries: true);
                        if (trimmed > 0)
                        {
                            int kept = Math.Max(0, (_conversationHistory.Count - 2) / 2);
                            onToken($"\n[CONTEXT] Soft trim: dropped {trimmed} messages — {kept} turns remaining.\n");
                        }
                    }
                }
            }

            // Budget display — bucket breakdown
            _budget.Assess(_conversationHistory, EstimateTokens);
            int workingUsed   = _budget.WorkingHistoryUsed;
            int workingLimit  = _budget.WorkingHistoryLimit;
            int headroomLimit = _budget.ResponseHeadroomLimit;
            int workingPct    = workingLimit > 0 ? (int)(workingUsed * 100.0 / workingLimit) : 0;

            // Use real server context usage when available
            int grandTotal;
            if (LastContextUsed > 0 && ServerContextSize > 0)
            {
                // Server told us exactly how many tokens are cached (n_past).
                // Only estimate the NEW user message being added this turn —
                // everything else (system prompt, prior turns, tool results) is already in n_past.
                grandTotal = LastContextUsed + EstimateTokens(_conversationHistory[_conversationHistory.Count - 1].Content);
            }
            else
            {
                // No server data yet (first turn) — fall back to full estimation
                grandTotal = 0;
                foreach (var msg in _conversationHistory)
                    grandTotal += EstimateTokens(msg.Content);
            }

            // Compare against server's actual context size when known, otherwise use DevMind's budget.
            // The server knows its own limit — ResponseHeadroom is a DevMind concept, not a server constraint.
            int contextCeiling = ServerContextSize > 0 ? ServerContextSize : _budget.HistoryHardLimit;
            if (grandTotal > contextCeiling)
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
                string lastDataLine = null; // Track last SSE data line for tool_calls extraction

                // Accumulate tool calls across SSE delta chunks.
                // llama-server streams tool_calls incrementally:
                //   chunk 1: id, name, arguments="" (start)
                //   chunk N: arguments partial string (append)
                //   final:   empty delta with finish_reason="tool_calls"
                // We accumulate per-index: id, name, arguments (concatenated).
                JArray accumulatedToolCalls = null;
                // Per-index argument builders — tool call index → accumulated arguments string
                var toolCallArgBuilders = new Dictionary<int, StringBuilder>();
                // Per-index metadata (id, name) — captured from the first chunk for each index
                var toolCallMeta = new Dictionary<int, JObject>();

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

                    lastDataLine = data;

                    string token = ParseContentDelta(data);
                    if (token != null)
                    {
                        firstTokenReceived = true;
                        fullResponse.Append(token);
                        onToken(token);
                    }

                    // Accumulate streamed tool_calls deltas
                    AccumulateToolCallDelta(data, toolCallArgBuilders, toolCallMeta);
                }

                // Build accumulated tool calls from streamed deltas
                accumulatedToolCalls = BuildAccumulatedToolCalls(toolCallArgBuilders, toolCallMeta);

                // Check for tool_calls in the SSE stream
                if (DevMindOptions.Instance.ShowDebugOutput)
                    onToken($"\n[DIAG-SSE] lastDataLine: {(lastDataLine?.Length > 500 ? lastDataLine.Substring(0, 500) + "..." : lastDataLine)}\n");
                if (DevMindOptions.Instance.ShowDebugOutput)
                    onToken($"\n[DIAG-TOOLS] Accumulated {accumulatedToolCalls?.Count ?? 0} tool call(s)\n");

                // ── Parse server timings from lastDataLine ────────────────────
                ParseTimings(lastDataLine);

                // Emit visible status if model responded with tool calls but no content
                if (accumulatedToolCalls != null && accumulatedToolCalls.Count > 0 && string.IsNullOrWhiteSpace(fullResponse.ToString()))
                    onToken("\n[TOOL_USE] Processing tool call(s)...\n");

                LastToolCalls = null;
                JArray rawToolCalls = null;

                // Priority 1: Use accumulated tool calls from streamed deltas
                if (accumulatedToolCalls != null && accumulatedToolCalls.Count > 0)
                {
                    LastToolCalls = ParseToolCallsFromArray(accumulatedToolCalls);
                    rawToolCalls = accumulatedToolCalls;
                }

                // Priority 2: Fallback — check lastDataLine for non-streamed message.tool_calls
                if (LastToolCalls == null && lastDataLine != null)
                {
                    var parseResult = ParseToolCalls(lastDataLine);
                    if (parseResult != null && parseResult.Count > 0)
                    {
                        LastToolCalls = parseResult;
                        rawToolCalls = ExtractRawToolCalls(lastDataLine);
                    }
                }

                _conversationHistory.Add(new ChatMessage("assistant", fullResponse.ToString(),
                    _currentTurn, toolCalls: rawToolCalls));

                // ── Emit server timings status line ─────────────────────────
                if (LastGeneratedTokens > 0
                    && (DevMindOptions.Instance.ShowContextBudget || DevMindOptions.Instance.ShowDebugOutput))
                {
                    double genSec = LastGeneratedMs / 1000.0;
                    double tokPerSec = genSec > 0 ? LastGeneratedTokens / genSec : 0;
                    int ctxTotal = ServerContextSize > 0 ? ServerContextSize : _contextSize;
                    int ctxPct = ctxTotal > 0 ? (int)(LastContextUsed * 100.0 / ctxTotal) : 0;
                    onToken($"\n[LLM] {LastGeneratedTokens:N0} tok in {genSec:F1}s ({tokPerSec:F1} tok/s) | Prompt: {LastPromptTokens:N0} tok ({ctxPct}% ctx)\n");
                }

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
            NearlineCache.Clear();
            _microCompactWatermark = 0;
            LastContextUsed = 0;
            _lastServerCountIndex = 0;
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

            if (mode == ContextEvictionMode.Off)
            {
                if (showDebug)
                    diagLog.AppendLine($"\n[CONTEXT] Eviction check: turn={_currentTurn}, mode={mode}, skipped");
                System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] Eviction: mode=Off, skipped");
                return showDebug ? diagLog.ToString() : null;
            }

            // Drop age: messages older than this are removed entirely.
            // No warm/cold tiers — append-only means we only DROP, never rewrite.
            int dropAge = mode == ContextEvictionMode.Aggressive ? 5 : 8;

            // Tool call + tool result pairs are 2-3x heavier per logical turn and carry
            // low residual value once acted upon. Use a shorter drop age for them.
            int toolDropAge = Math.Max(2, dropAge - 3);

            if (showDebug)
            {
                diagLog.AppendLine($"\n[CONTEXT] Eviction check: turn={_currentTurn}, mode={mode}, message count={_conversationHistory.Count}, dropAge={dropAge}, toolDropAge={toolDropAge}");
            }

            int dropped = 0;
            var indicesToRemove = new List<int>();
            var dropSummaryParts = new List<string>();

            for (int i = 1; i < _conversationHistory.Count; i++)
            {
                var msg = _conversationHistory[i];
                int age = _currentTurn - msg.Turn;

                // Tool role messages and assistant messages with tool_calls use a shorter drop age
                bool isToolRelated = msg.Role == "tool"
                    || (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0);
                int effectiveDropAge = isToolRelated ? toolDropAge : dropAge;

                if (age <= effectiveDropAge) continue;
                if (IsPinnedMessage(msg)) continue;

                // Build snippet before dropping
                string snippet = BuildDropSnippet(msg);
                if (snippet != null) dropSummaryParts.Add(snippet);

                indicesToRemove.Add(i);
                dropped++;
            }

            // Ensure tool call/result pairs are dropped as a unit: if an assistant message
            // with tool_calls is dropped, also drop all immediately following tool role
            // messages (and vice versa). This prevents orphaned tool_calls or tool results.
            var extraIndices = new HashSet<int>();
            foreach (int idx in indicesToRemove)
            {
                var msg = _conversationHistory[idx];

                // Assistant with tool_calls → also drop following tool results
                if (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                {
                    for (int j = idx + 1; j < _conversationHistory.Count; j++)
                    {
                        if (_conversationHistory[j].Role == "tool")
                            extraIndices.Add(j);
                        else
                            break;
                    }
                }

                // Tool result → also drop the preceding assistant with tool_calls
                if (msg.Role == "tool" && idx > 1)
                {
                    // Walk back to find the assistant message that initiated these tool calls
                    for (int j = idx - 1; j >= 1; j--)
                    {
                        if (_conversationHistory[j].Role == "tool")
                            continue; // skip other tool results in the same batch
                        if (_conversationHistory[j].Role == "assistant"
                            && _conversationHistory[j].ToolCalls != null
                            && _conversationHistory[j].ToolCalls.Count > 0)
                        {
                            extraIndices.Add(j);
                        }
                        break;
                    }
                }
            }

            // Merge extra indices
            foreach (int idx in extraIndices)
            {
                if (!indicesToRemove.Contains(idx) && !IsPinnedMessage(_conversationHistory[idx]))
                {
                    string snippet = BuildDropSnippet(_conversationHistory[idx]);
                    if (snippet != null) dropSummaryParts.Add(snippet);
                    indicesToRemove.Add(idx);
                    dropped++;
                }
            }

            // Sort for reverse removal
            indicesToRemove.Sort();

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

        // ── Tier 1: MicroCompact ─────────────────────────────────────────────
        // Trims the CONTENT of old tool result messages in-place — without removing
        // messages. Preserves conversation structure (tool_call/tool_result pairing)
        // while reclaiming context tokens from bulky payloads that are no longer needed.
        // Tool result messages (Role == "tool") and user messages containing [READ: blocks
        // are eligible. Assistant messages are never modified. The first user message
        // (original task prompt) and the 5 most recent user messages with READ blocks are protected.
        // Returns a log string to emit via onToken, or null if nothing changed.

        /// <summary>Minimum content length before a tool result is eligible for trimming.</summary>
        private const int MicroCompactMinLength = 200;

        /// <summary>Age (in turns) beyond which tool results are pre-tagged as stale.</summary>
        private const int StaleToolAgeTurns = 10;

        /// <summary>
        /// Returns the dynamic keep-recent count based on the current turn.
        /// Turns 1-6: keep 5, Turns 7-12: keep 3, Turns 13+: keep 2.
        /// </summary>
        private int GetSlidingKeepRecent()
        {
            if (_currentTurn <= 6) return 5;
            if (_currentTurn <= 12) return 3;
            return 2;
        }

        /// <summary>
        /// Identifies stale message indices that should be trimmed first during compaction.
        /// Criteria: superseded builds, superseded scratchpads, re-read files, and old tool results.
        /// Rebuilt on every call — not persisted.
        /// </summary>
        private System.Collections.Generic.HashSet<int> PreTagStaleMessages(int firstUserIndex)
        {
            var staleIndices = new System.Collections.Generic.HashSet<int>();
            int histCount = _conversationHistory.Count;

            // Track latest build result, latest scratchpad, and latest read per filename
            int latestBuildIndex = -1;
            int latestScratchpadIndex = -1;
            var latestReadByFile = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Forward scan to find the latest of each category
            for (int i = 1; i < histCount; i++)
            {
                var msg = _conversationHistory[i];
                if (msg.Content == null) continue;

                // Track latest build result (tool or assistant with build output)
                if (msg.Role == "tool" || msg.Role == "assistant")
                {
                    if (msg.Content.IndexOf("Build succeeded", StringComparison.OrdinalIgnoreCase) >= 0
                        || msg.Content.IndexOf("Build FAILED", StringComparison.OrdinalIgnoreCase) >= 0)
                        latestBuildIndex = i;
                }

                // Track latest scratchpad
                if (msg.Content.IndexOf("SCRATCHPAD:", StringComparison.Ordinal) >= 0)
                    latestScratchpadIndex = i;

                // Track latest READ per filename (in user messages)
                if (msg.Role == "user" && msg.Content.IndexOf("[READ:", StringComparison.Ordinal) >= 0)
                {
                    // Extract filenames from [READ: filename] tags
                    int searchFrom = 0;
                    while (searchFrom < msg.Content.Length)
                    {
                        int tagStart = msg.Content.IndexOf("[READ:", searchFrom, StringComparison.Ordinal);
                        if (tagStart < 0) break;
                        int tagEnd = msg.Content.IndexOf(']', tagStart);
                        if (tagEnd < 0) break;
                        string fname = msg.Content.Substring(tagStart + 6, tagEnd - tagStart - 6).Trim();
                        if (fname.Length > 0)
                            latestReadByFile[fname] = i;
                        searchFrom = tagEnd + 1;
                    }
                }
            }

            // Second pass: mark superseded messages as stale
            for (int i = 1; i < histCount; i++)
            {
                if (i == firstUserIndex) continue; // never tag the pinned task prompt

                var msg = _conversationHistory[i];
                if (msg.Content == null) continue;

                // Superseded build outputs
                if (latestBuildIndex > 0 && i < latestBuildIndex
                    && (msg.Role == "tool" || msg.Role == "assistant")
                    && (msg.Content.IndexOf("Build succeeded", StringComparison.OrdinalIgnoreCase) >= 0
                        || msg.Content.IndexOf("Build FAILED", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    staleIndices.Add(i);
                }

                // Superseded scratchpads
                if (latestScratchpadIndex > 0 && i < latestScratchpadIndex
                    && msg.Content.IndexOf("SCRATCHPAD:", StringComparison.Ordinal) >= 0)
                {
                    staleIndices.Add(i);
                }

                // Re-read files (user messages with READ blocks where a newer READ of the same file exists)
                if (msg.Role == "user" && msg.Content.IndexOf("[READ:", StringComparison.Ordinal) >= 0)
                {
                    int searchFrom = 0;
                    bool allReadsSuperseded = true;
                    bool hasAnyRead = false;
                    while (searchFrom < msg.Content.Length)
                    {
                        int tagStart = msg.Content.IndexOf("[READ:", searchFrom, StringComparison.Ordinal);
                        if (tagStart < 0) break;
                        int tagEnd = msg.Content.IndexOf(']', tagStart);
                        if (tagEnd < 0) break;
                        string fname = msg.Content.Substring(tagStart + 6, tagEnd - tagStart - 6).Trim();
                        hasAnyRead = true;
                        if (fname.Length > 0 && latestReadByFile.TryGetValue(fname, out int latestIdx) && latestIdx > i)
                        {
                            // This read is superseded
                        }
                        else
                        {
                            allReadsSuperseded = false;
                        }
                        searchFrom = tagEnd + 1;
                    }
                    // Only mark stale if ALL reads in this message are superseded
                    if (hasAnyRead && allReadsSuperseded)
                        staleIndices.Add(i);
                }

                // Tool results older than StaleToolAgeTurns
                if (msg.Role == "tool" && _currentTurn - msg.Turn >= StaleToolAgeTurns)
                {
                    staleIndices.Add(i);
                }
            }

            return staleIndices;
        }

        /// <summary>
        /// Computes the current working-budget percentage (quick estimate without full Assess).
        /// Uses server-reported n_past when available for more accurate measurement.
        /// </summary>
        private int ComputeWorkingPct()
        {
            // Use real server token counts when available — n_past / n_ctx gives
            // the actual context fill percentage, not a bucket-relative estimate.
            if (LastContextUsed > 0 && ServerContextSize > 0)
            {
                // Add estimates for messages added since last server response
                int newMsgEst = 0;
                for (int i = _lastServerCountIndex; i < _conversationHistory.Count; i++)
                    newMsgEst += EstimateTokens(_conversationHistory[i].Content);
                return (int)((LastContextUsed + newMsgEst) * 100.0 / ServerContextSize);
            }

            // Fallback: estimate from working bucket
            int workingLimit = _budget?.WorkingHistoryLimit ?? 1;
            if (workingLimit <= 0) workingLimit = 1;
            int count = _conversationHistory.Count;
            int workingUsedEstimate = 0;
            int protStart = count - 5;
            for (int i = 1; i < count && i < protStart; i++)
                workingUsedEstimate += EstimateTokens(_conversationHistory[i].Content);
            return (int)(workingUsedEstimate * 100.0 / workingLimit);
        }

        /// <summary>
        /// Trims stale tool result content in-place using a watermark-based strategy.
        /// Stale messages (superseded builds, re-read files, old tool results) are trimmed first.
        /// Keep-recent count slides down as turn count increases.
        /// After passes 2-3, a Pass 4 trims oldest non-pinned messages until the watermark target is met.
        /// Called before <see cref="EvictStaleContext"/> on every send.
        /// </summary>
        public string MicroCompactToolResults()
        {
            bool showDebug = DevMindOptions.Instance.ShowDebugOutput;

            // ── Threshold gate: only compact when context pressure is high ────
            int threshold = DevMindOptions.Instance.MicroCompactThreshold;
            if (threshold == 0)
                return null; // disabled

            int workingPct = ComputeWorkingPct();

            if (workingPct < threshold)
            {
                if (showDebug)
                    System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] MicroCompact: skipped — working {workingPct}% < threshold {threshold}%");
                return null;
            }

            // ── Determine watermark target ────────────────────────────────────
            int watermarkTarget = _microCompactWatermark > 0 ? _microCompactWatermark : 40;

            // ── Pin the original task prompt ──────────────────────────────────
            int firstUserIndex = -1;
            for (int i = 1; i < _conversationHistory.Count; i++)
            {
                if (_conversationHistory[i].Role == "user")
                {
                    firstUserIndex = i;
                    break;
                }
            }

            int trimmed = 0;
            int savedChars = 0;
            int keepRecent = GetSlidingKeepRecent();

            // ── Pre-tag stale messages ────────────────────────────────────────
            var staleIndices = PreTagStaleMessages(firstUserIndex);

            // ── Pass 1: identify the N most recent tool result indices ────────
            var protectedToolIndices = new System.Collections.Generic.HashSet<int>();
            int toolCount = 0;
            for (int i = _conversationHistory.Count - 1; i >= 1 && toolCount < keepRecent; i--)
            {
                if (_conversationHistory[i].Role == "tool")
                {
                    protectedToolIndices.Add(i);
                    toolCount++;
                }
            }

            // ── Pass 2: trim stale tool results first, then unprotected ──────
            for (int i = 1; i < _conversationHistory.Count; i++)
            {
                var msg = _conversationHistory[i];

                if (msg.Role != "tool") continue;
                if (msg.Content == null || msg.Content.Length <= MicroCompactMinLength) continue;

                // Stale-tagged messages skip protection checks — trim them first
                bool isStale = staleIndices.Contains(i);

                if (!isStale)
                {
                    if (protectedToolIndices.Contains(i)) continue;

                    // Preserve tool results that carry important state (unless stale)
                    string c2 = msg.Content;
                    if (c2.IndexOf("[PATCH", StringComparison.OrdinalIgnoreCase) >= 0
                        || c2.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                        || c2.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                }

                string c = msg.Content;
                int origLen = c.Length;
                string cacheKey = $"tool:{msg.ToolCallId ?? i.ToString()}";
                string breadcrumb = BuildToolResultBreadcrumb(c, origLen);
                NearlineCache.Store(cacheKey, c, breadcrumb);
                _conversationHistory[i] = new ChatMessage(msg.Role,
                    breadcrumb, msg.Turn, msg.ToolCalls, msg.ToolCallId);
                savedChars += origLen;
                trimmed++;
            }

            // ── Pass 3: trim [READ: blocks from older user messages ───────────
            var protectedUserReadIndices = new System.Collections.Generic.HashSet<int>();
            int userReadCount = 0;
            for (int i = _conversationHistory.Count - 1; i >= 1 && userReadCount < keepRecent; i--)
            {
                var msg = _conversationHistory[i];
                if (msg.Role == "user" && msg.Content != null
                    && msg.Content.IndexOf("[READ:", StringComparison.Ordinal) >= 0)
                {
                    protectedUserReadIndices.Add(i);
                    userReadCount++;
                }
            }

            for (int i = 1; i < _conversationHistory.Count; i++)
            {
                var msg = _conversationHistory[i];

                if (msg.Role != "user") continue;
                if (i == firstUserIndex) continue;                        // always protect pinned task prompt
                if (msg.Content == null) continue;
                if (msg.Content.IndexOf("[READ:", StringComparison.Ordinal) < 0) continue;

                // Stale-tagged user messages skip keep-recent protection
                bool isStale = staleIndices.Contains(i);
                if (!isStale && protectedUserReadIndices.Contains(i)) continue;

                int origLen = msg.Content.Length;
                CacheReadBlocks(msg.Content);
                string compacted = CompactReadBlocks(msg.Content);
                if (compacted.Length >= origLen) continue;                 // nothing trimmed

                _conversationHistory[i] = new ChatMessage(msg.Role,
                    compacted, msg.Turn, msg.ToolCalls, msg.ToolCallId);
                savedChars += origLen - compacted.Length;
                trimmed++;
            }

            // ── Pass 4: trim-to-watermark — further trim if still above target ─
            int currentPct = ComputeWorkingPct();
            if (currentPct > watermarkTarget)
            {
                // Protect the last 2 messages unconditionally (beyond keep-recent tool protection)
                int absoluteProtectStart = _conversationHistory.Count - 2;

                for (int i = 1; i < _conversationHistory.Count && currentPct > watermarkTarget; i++)
                {
                    if (i == firstUserIndex) continue;                     // pinned task prompt
                    if (i >= absoluteProtectStart) continue;               // last 2 messages

                    var msg = _conversationHistory[i];
                    if (msg.Content == null || msg.Content.Length <= MicroCompactMinLength) continue;

                    // Already a breadcrumb — skip
                    if (msg.Content.StartsWith("[cached", StringComparison.OrdinalIgnoreCase)) continue;
                    if (msg.Content.StartsWith("[DROPPED", StringComparison.OrdinalIgnoreCase)) continue;

                    int origLen = msg.Content.Length;
                    string replacement;

                    if (msg.Role == "tool")
                    {
                        string cacheKey = $"tool:{msg.ToolCallId ?? i.ToString()}";
                        string breadcrumb = BuildToolResultBreadcrumb(msg.Content, origLen);
                        NearlineCache.Store(cacheKey, msg.Content, breadcrumb);
                        replacement = breadcrumb;
                    }
                    else if (msg.Role == "user" && msg.Content.IndexOf("[READ:", StringComparison.Ordinal) >= 0)
                    {
                        CacheReadBlocks(msg.Content);
                        replacement = CompactReadBlocks(msg.Content);
                        if (replacement.Length >= origLen) continue;
                    }
                    else if (msg.Role == "assistant" && msg.Content.Length > MicroCompactMinLength)
                    {
                        // Trim long assistant messages to first 200 chars + breadcrumb
                        int keepLen = 200;
                        if (msg.Content.Length <= keepLen) continue;
                        replacement = msg.Content.Substring(0, keepLen)
                            + $"\n[... trimmed {origLen - keepLen} chars to reclaim context ...]";
                    }
                    else
                    {
                        continue;
                    }

                    _conversationHistory[i] = new ChatMessage(msg.Role,
                        replacement, msg.Turn, msg.ToolCalls, msg.ToolCallId);
                    savedChars += origLen - replacement.Length;
                    trimmed++;
                    currentPct = ComputeWorkingPct();
                }
            }

            if (trimmed == 0)
                return null;

            // Set watermark after first successful compaction
            int finalPct = ComputeWorkingPct();
            if (_microCompactWatermark == 0)
                _microCompactWatermark = finalPct;

            string logMsg = $"\n[CONTEXT] Compacting — one-time cache rebuild expected.\n[CONTEXT] MicroCompact: trimmed {trimmed} message(s), reclaimed ~{savedChars / 4} tokens. Working budget: {finalPct}% (watermark: {_microCompactWatermark}%).\n";
            if (showDebug)
            {
                System.Diagnostics.Debug.WriteLine($"[DevMind TRACE] MicroCompact: {trimmed} trimmed, {savedChars} chars saved, workingPct={workingPct}%→{finalPct}%, threshold={threshold}%, watermark={_microCompactWatermark}%");
                logMsg += $"[DIAG] MicroCompact: trimmed {trimmed} messages, cache holds {NearlineCache.Count} entries ({NearlineCache.EstimatedTokens} est. tokens), stale-tagged {staleIndices.Count}\n";
            }

            return logMsg;
        }

        /// <summary>
        /// Builds a semantic breadcrumb for a trimmed tool result based on content type.
        /// </summary>
        private static string BuildToolResultBreadcrumb(string content, int origLen)
        {
            // read_file results — look for filename and line count
            if (content.IndexOf("[READ:", StringComparison.Ordinal) >= 0)
            {
                int tagStart = content.IndexOf("[READ:", StringComparison.Ordinal);
                int tagEnd = content.IndexOf(']', tagStart);
                string filename = tagEnd > tagStart
                    ? content.Substring(tagStart + 6, tagEnd - tagStart - 6).Trim()
                    : "unknown";
                int lineCount = 0;
                for (int j = 0; j < content.Length; j++)
                    if (content[j] == '\n') lineCount++;
                return $"[cached — {filename}, {lineCount} lines]";
            }

            // Build results
            if (content.IndexOf("Build succeeded", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("0 Error(s)", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int warnings = 0;
                int wIdx = content.IndexOf("Warning(s)", StringComparison.OrdinalIgnoreCase);
                if (wIdx > 0)
                {
                    int numStart = wIdx - 1;
                    while (numStart > 0 && char.IsDigit(content[numStart - 1])) numStart--;
                    if (numStart < wIdx && int.TryParse(content.Substring(numStart, wIdx - numStart).Trim(), out int w))
                        warnings = w;
                }
                return $"[cached — build succeeded, {warnings} warnings]";
            }

            // Shell results — look for exit code
            if (content.IndexOf("exit code", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("[SHELL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return $"[cached — shell output, {origLen} chars]";
            }

            // Grep/find results — look for match pattern
            if (content.IndexOf("matches", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("GREP:", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("FIND:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return $"[cached — search results, {origLen} chars]";
            }

            // Test results
            if (content.IndexOf("TEST RESULTS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bool passed = content.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0;
                return $"[cached — tests {(passed ? "passed" : "failed")}]";
            }

            // Fallback
            return $"[cached — {origLen} chars]";
        }

        /// <summary>
        /// Extracts [READ:filename] blocks from user message content and stores each
        /// file's content in the NearlineCache before the content is compacted away.
        /// </summary>
        private void CacheReadBlocks(string content)
        {
            int pos = 0;
            while (pos < content.Length)
            {
                int tagStart = content.IndexOf("[READ:", pos, StringComparison.Ordinal);
                if (tagStart < 0) break;

                int tagEnd = content.IndexOf(']', tagStart);
                if (tagEnd < 0) break;

                string filename = content.Substring(tagStart + 6, tagEnd - tagStart - 6).Trim();
                int nextTag = content.IndexOf("[READ:", tagEnd + 1, StringComparison.Ordinal);
                int blockEnd = nextTag >= 0 ? nextTag : content.Length;
                string blockContent = content.Substring(tagEnd + 1, blockEnd - tagEnd - 1);

                if (!string.IsNullOrWhiteSpace(filename) && blockContent.Length > 0)
                {
                    string cacheKey = $"read:{filename}";
                    NearlineCache.Store(cacheKey, blockContent, $"[cached — {filename}]");
                }

                pos = blockEnd;
            }
        }

        /// <summary>
        /// Replaces file content inside [READ:filename] blocks with a placeholder while
        /// preserving the [READ:filename] header so the model knows the file was read.
        /// </summary>
        private static string CompactReadBlocks(string content)
        {
            var sb = new StringBuilder(content.Length);
            int pos = 0;

            while (pos < content.Length)
            {
                int tagStart = content.IndexOf("[READ:", pos, StringComparison.Ordinal);
                if (tagStart < 0)
                {
                    sb.Append(content, pos, content.Length - pos);
                    break;
                }

                // Copy everything before the tag
                sb.Append(content, pos, tagStart - pos);

                int tagEnd = content.IndexOf(']', tagStart);
                if (tagEnd < 0)
                {
                    sb.Append(content, tagStart, content.Length - tagStart);
                    break;
                }

                // Copy the [READ:filename] header
                string header = content.Substring(tagStart, tagEnd - tagStart + 1);
                sb.Append(header);

                // Find the end of this READ block — next [READ: or end of string
                int nextTag = content.IndexOf("[READ:", tagEnd + 1, StringComparison.Ordinal);
                int blockEnd = nextTag >= 0 ? nextTag : content.Length;

                // Replace file content with placeholder
                sb.Append("\n[file content trimmed]\n");
                pos = blockEnd;
            }

            return sb.ToString();
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
        /// Drops the oldest complete turn-groups from history.
        /// A turn-group is either:
        ///   - tool_use group: assistant (with tool_calls) + all following tool results + next user message
        ///   - regular group: assistant + user (existing pair behavior)
        /// Dropping complete groups prevents orphaned tool_call/tool_result messages.
        /// Always preserves index 0 (system) and the last entry (pending user message).
        /// When <paramref name="withSummaries"/> is true, inserts a drop summary; otherwise drops silently.
        /// Also skips any pinned content (DevMind.md, SCRATCHPAD) at the start of history.
        /// Returns the number of messages actually removed.
        /// </summary>
        private int TrimOldestTurns(int groupsToRemove, bool withSummaries)
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

            int lastRemovable = _conversationHistory.Count - 1; // exclude last (current user)

            // Build a list of complete turn-groups starting from startIdx
            var groups = new List<int[]>(); // each entry is [groupStart, groupEnd) indices
            int cursor = startIdx;
            while (cursor < lastRemovable && groups.Count < groupsToRemove)
            {
                int groupStart = cursor;
                var msg = _conversationHistory[cursor];

                if (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                {
                    // tool_use group: assistant (with tool_calls) + all following tool results + optional user
                    cursor++; // past assistant
                    while (cursor < lastRemovable && _conversationHistory[cursor].Role == "tool")
                        cursor++; // consume all tool results
                    if (cursor < lastRemovable && _conversationHistory[cursor].Role == "user")
                        cursor++; // consume the following user message
                }
                else
                {
                    // Regular group: current message + next message (classic pair)
                    cursor++;
                    if (cursor < lastRemovable)
                        cursor++;
                }

                groups.Add(new[] { groupStart, cursor });
            }

            if (groups.Count == 0)
                return 0;

            // Calculate total messages to remove
            int totalEnd = groups[groups.Count - 1][1];
            int messagesToRemove = totalEnd - startIdx;

            if (messagesToRemove <= 0)
                return 0;

            if (withSummaries)
            {
                var summaryParts = new List<string>();
                for (int i = startIdx; i < totalEnd && i < _conversationHistory.Count; i++)
                {
                    string snippet = BuildDropSnippet(_conversationHistory[i]);
                    if (snippet != null) summaryParts.Add(snippet);
                }
                string dropSummary = summaryParts.Count > 0
                    ? $"[DROPPED] {groups.Count} group(s): {string.Join(", ", summaryParts)}"
                    : $"[DROPPED] {groups.Count} group(s) removed for budget";

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

            if (msg.Role == "tool")
            {
                string t = c.Trim();
                if (t.Length > 60) t = t.Substring(0, 60) + "...";
                return $"tool result: {t}";
            }

            if (msg.Role == "assistant")
            {
                if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                    return $"tool call ({msg.ToolCalls.Count} call(s))";
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
                var msgObj = new JObject
                {
                    ["role"]    = msg.Role,
                    ["content"] = msg.Content
                };

                // Assistant messages with tool_calls must include the tool_calls array
                if (msg.Role == "assistant" && msg.ToolCalls != null)
                {
                    msgObj["tool_calls"] = msg.ToolCalls;
                }

                // Tool result messages must include tool_call_id
                if (msg.Role == "tool" && msg.ToolCallId != null)
                {
                    msgObj["tool_call_id"] = msg.ToolCallId;
                }

                messages.Add(msgObj);
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

        /// <summary>
        /// Extracts server-reported timings from the last SSE data line.
        /// Updates LastPromptTokens, LastGeneratedTokens, etc. if the timings object is present.
        /// </summary>
        private void ParseTimings(string lastData)
        {
            if (string.IsNullOrEmpty(lastData)) return;
            try
            {
                var obj = JObject.Parse(lastData);
                var timings = obj?["timings"];
                if (timings == null) return;

                int promptN = timings["prompt_n"]?.Value<int>() ?? 0;
                double promptMs = timings["prompt_ms"]?.Value<double>() ?? 0;
                int predictedN = timings["predicted_n"]?.Value<int>() ?? 0;
                double predictedMs = timings["predicted_ms"]?.Value<double>() ?? 0;
                int nCtx = timings["n_ctx"]?.Value<int>() ?? 0;
                int nPast = timings["n_past"]?.Value<int>() ?? 0;

                if (promptN > 0) LastPromptTokens = promptN;
                if (promptMs > 0) LastPromptMs = promptMs;
                if (predictedN > 0) LastGeneratedTokens = predictedN;
                if (predictedMs > 0) LastGeneratedMs = predictedMs;
                if (nCtx > 0) ServerContextSize = nCtx;
                if (nPast > 0)
                {
                    LastContextUsed = nPast;
                    _lastServerCountIndex = _conversationHistory.Count;
                }
            }
            catch
            {
                // Timings missing or malformed — leave previous values unchanged
            }
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

        /// <summary>
        /// Parses tool_calls from the last SSE data line. Checks both
        /// <c>choices[0].message.tool_calls</c> (non-streamed) and
        /// <c>choices[0].delta.tool_calls</c> (streamed delta).
        /// Returns null if no tool_calls found.
        /// </summary>
        private static List<ToolCallResult> ParseToolCalls(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                var choice = obj?["choices"]?[0];
                if (choice == null) return null;

                // Check finish_reason — tool_calls indicates tool use
                var finishReason = choice["finish_reason"]?.ToString();

                // Look for tool_calls in message (non-streamed) or delta (streamed)
                var toolCallsToken = choice["message"]?["tool_calls"]
                    ?? choice["delta"]?["tool_calls"];

                if (toolCallsToken == null || toolCallsToken.Type != JTokenType.Array)
                    return null;

                var toolCallsArray = (JArray)toolCallsToken;
                if (toolCallsArray.Count == 0)
                    return null;

                // Extract reasoning_content / thinking text
                string thinking = choice["message"]?["reasoning_content"]?.ToString()
                    ?? choice["delta"]?["reasoning_content"]?.ToString();

                var results = new List<ToolCallResult>();
                foreach (var tc in toolCallsArray)
                {
                    var fn = tc["function"];
                    if (fn == null) continue;

                    string name = fn["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    // Parse arguments — may be a JSON string or a JSON object
                    var args = new Dictionary<string, string>();
                    var argsToken = fn["arguments"];
                    if (argsToken != null)
                    {
                        JObject argsObj = null;
                        if (argsToken.Type == JTokenType.String)
                        {
                            // OpenAI format: arguments is a JSON string
                            string argsStr = argsToken.ToString();
                            if (!string.IsNullOrEmpty(argsStr))
                            {
                                try { argsObj = JObject.Parse(argsStr); }
                                catch { /* malformed args — skip */ }
                            }
                        }
                        else if (argsToken.Type == JTokenType.Object)
                        {
                            // Ollama native format: arguments is already an object
                            argsObj = (JObject)argsToken;
                        }

                        if (argsObj != null)
                        {
                            foreach (var prop in argsObj.Properties())
                            {
                                args[prop.Name] = prop.Value?.ToString() ?? "";
                            }
                        }
                    }

                    results.Add(new ToolCallResult
                    {
                        Id = tc["id"]?.ToString() ?? $"call_{results.Count}",
                        Name = name,
                        Arguments = args,
                        ThinkingText = thinking
                    });
                }

                return results.Count > 0 ? results : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts the raw tool_calls JArray from the SSE data line for history serialization.
        /// </summary>
        private static JArray ExtractRawToolCalls(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                var choice = obj?["choices"]?[0];
                var toolCallsToken = choice?["message"]?["tool_calls"]
                    ?? choice?["delta"]?["tool_calls"];
                return toolCallsToken as JArray;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Accumulates a single SSE delta chunk's tool_calls into the per-index builders.
        /// First chunk for an index captures id/name; all chunks append arguments.
        /// </summary>
        private static void AccumulateToolCallDelta(string json,
            Dictionary<int, StringBuilder> argBuilders,
            Dictionary<int, JObject> meta)
        {
            try
            {
                var obj = JObject.Parse(json);
                var delta = obj?["choices"]?[0]?["delta"];
                var toolCalls = delta?["tool_calls"] as JArray;
                if (toolCalls == null) return;

                foreach (var tc in toolCalls)
                {
                    int index = tc["index"]?.Value<int>() ?? 0;

                    // First time seeing this index — capture id, type, name
                    if (!meta.ContainsKey(index))
                    {
                        var entry = new JObject();
                        if (tc["id"] != null) entry["id"] = tc["id"].ToString();
                        if (tc["type"] != null) entry["type"] = tc["type"].ToString();
                        var fn = tc["function"];
                        if (fn?["name"] != null) entry["name"] = fn["name"].ToString();
                        meta[index] = entry;
                        argBuilders[index] = new StringBuilder();
                    }

                    // Append arguments fragment (may be empty string on first chunk)
                    var fnArgs = tc["function"]?["arguments"]?.ToString();
                    if (fnArgs != null)
                        argBuilders[index].Append(fnArgs);

                    // Update name if present (in case first chunk didn't have it)
                    if (tc["function"]?["name"] != null && meta[index]["name"] == null)
                        meta[index]["name"] = tc["function"]["name"].ToString();
                }
            }
            catch
            {
                // Malformed chunk — skip silently
            }
        }

        /// <summary>
        /// Builds a complete JArray of tool calls from accumulated delta fragments.
        /// Returns null if no tool calls were accumulated.
        /// </summary>
        private static JArray BuildAccumulatedToolCalls(
            Dictionary<int, StringBuilder> argBuilders,
            Dictionary<int, JObject> meta)
        {
            if (meta.Count == 0) return null;

            var result = new JArray();
            foreach (var kvp in meta)
            {
                int index = kvp.Key;
                var m = kvp.Value;

                var tc = new JObject
                {
                    ["id"] = m["id"]?.ToString() ?? $"call_{index}",
                    ["type"] = m["type"]?.ToString() ?? "function",
                    ["function"] = new JObject
                    {
                        ["name"] = m["name"]?.ToString() ?? "",
                        ["arguments"] = argBuilders.ContainsKey(index)
                            ? argBuilders[index].ToString()
                            : ""
                    }
                };
                result.Add(tc);
            }
            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// Parses a pre-built tool_calls JArray into <see cref="ToolCallResult"/> list.
        /// Used for accumulated streamed tool calls (already assembled).
        /// </summary>
        private static List<ToolCallResult> ParseToolCallsFromArray(JArray toolCallsArray)
        {
            if (toolCallsArray == null || toolCallsArray.Count == 0)
                return null;

            var results = new List<ToolCallResult>();
            foreach (var tc in toolCallsArray)
            {
                var fn = tc["function"];
                if (fn == null) continue;

                string name = fn["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                var args = new Dictionary<string, string>();
                var argsToken = fn["arguments"];
                if (argsToken != null)
                {
                    string argsStr = argsToken.ToString();
                    if (!string.IsNullOrEmpty(argsStr))
                    {
                        try
                        {
                            var argsObj = JObject.Parse(argsStr);
                            foreach (var prop in argsObj.Properties())
                                args[prop.Name] = prop.Value?.ToString() ?? "";
                        }
                        catch { /* malformed args — skip */ }
                    }
                }

                results.Add(new ToolCallResult
                {
                    Id = tc["id"]?.ToString() ?? $"call_{results.Count}",
                    Name = name,
                    Arguments = args
                });
            }

            return results.Count > 0 ? results : null;
        }

        /// <summary>
        /// Appends a tool result message to conversation history.
        /// Called by the onComplete handler after executing each tool call.
        /// </summary>
        public void AddToolResultMessage(string toolCallId, string content)
        {
            _conversationHistory.Add(new ChatMessage("tool", content ?? "",
                _currentTurn, toolCallId: toolCallId));
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
        /// For assistant messages: the raw tool_calls JArray from the response, preserved
        /// for serialization back to the API on subsequent turns.
        /// </summary>
        public JArray ToolCalls { get; }

        /// <summary>
        /// For tool role messages: the tool_call_id this result corresponds to.
        /// </summary>
        public string ToolCallId { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ChatMessage"/> class.
        /// </summary>
        /// <param name="role">The role (system, user, assistant, or tool).</param>
        /// <param name="content">The message text.</param>
        /// <param name="turn">The turn number (0 for system prompt, incremented per user-initiated send).</param>
        /// <param name="toolCalls">For assistant messages with tool calls.</param>
        /// <param name="toolCallId">For tool result messages.</param>
        public ChatMessage(string role, string content, int turn = 0,
            JArray toolCalls = null, string toolCallId = null)
        {
            Role = role;
            Content = content;
            Turn = turn;
            ToolCalls = toolCalls;
            ToolCallId = toolCallId;
        }
    }
}
