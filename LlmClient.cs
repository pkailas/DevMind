// File: LlmClient.cs  v5.44
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
        private readonly List<string> _compressionLog = new List<string>();
        private const string DefaultSystemPrompt = "You are a helpful coding assistant. Be concise and precise.";
        internal readonly FileContentCache _fileCache = new FileContentCache();
        private string _taskScratchpad = "";
        private const int ScratchpadMaxTokens = 200;
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
            ApplyHttpClientSettings(_httpClient);
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
            if (!string.IsNullOrEmpty(_apiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            }
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
        /// <param name="deferCompression">When true, all compression phases (0a, 0b, 0c, 4, 2) and
        /// the Level 3 budget guard are skipped. Pass true during agentic iterations to preserve the
        /// llama-server KV cache. Call RunDeferredCompression() after the loop completes.</param>
        /// <param name="cancellationToken">Cancellation token to abort the request.</param>
        public async Task SendMessageAsync(
            string userMessage,
            Action<string> onToken,
            Action onComplete,
            Action<Exception> onError,
            bool deferCompression = false,
            CancellationToken cancellationToken = default)
        {
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

            UpdateSystemPrompt();

            // Clear compression log and strip old context notes before any compression runs,
            // including pre-squeeze (so pre-squeeze entries survive into InjectContextNote).
            _compressionLog.Clear();
            StripContextNotes();

            // PreSqueezeOversizedRead intentionally disabled: the current message must reach the
            // LLM with full file content so it can generate PATCH directives. Post-turn compression
            // (CompressLastUserReadBlocks) outlines READ blocks AFTER the LLM responds.
            _conversationHistory.Add(new ChatMessage("user", userMessage));

            if (!deferCompression)
            {
                // Phase 0a–0c: unconditional — always run regardless of budget state.
                // These methods collapse stale/duplicate blocks that waste tokens with no value.

                // Phase 0a: deduplicate stale READ copies — runs before all other compression
                string dedupMsg = DeduplicateReadBlocks();
                if (dedupMsg != null)
                    onToken(dedupMsg);

                // Phase 0b: collapse stale PATCH snapshots for files patched multiple times
                string chainMsg = CollapsePatchChains();
                if (chainMsg != null)
                    onToken(chainMsg);

                // Phase 0c: eagerly compress shell output older than 1 prior turn
                var (shellMsg0c, _) = CompressStaleShellBlocks(includeRecentTurn: false);
                if (shellMsg0c != null)
                    onToken(shellMsg0c);

                // Assess budget after unconditional passes so squeeze/trim fire on actual pressure.
                _budget.Assess(_conversationHistory, EstimateTokens);

                // Phase 4: squeeze PATCH, SHELL, and READ blocks — fires on soft or hard pressure.
                int patchCount = 0, shellCount = 0, readCount = 0;
                if (_budget.IsWorkingHistoryOverSoft)
                {
                    patchCount = SqueezePatchResults();
                    var (_, shellCountPhase4) = CompressStaleShellBlocks(includeRecentTurn: true);
                    shellCount = shellCountPhase4;
                    readCount  = SqueezeReadContent();
                    int totalSqueezed = patchCount + shellCount + readCount;
                    if (totalSqueezed > 0)
                        onToken($"\n[CONTEXT] Squeezed {totalSqueezed} block(s) — {patchCount} PATCH, {shellCount} SHELL, {readCount} READ.\n");
                }

                // Phase 2: sliding window trim — fires on hard pressure or total history hard limit.
                _budget.Assess(_conversationHistory, EstimateTokens);
                int totalHistoryTokens = _budget.SystemPromptUsed + _budget.WorkingHistoryUsed + _budget.ProtectedTurnsUsed
                    + EstimateTokens(_conversationHistory[_conversationHistory.Count - 1].Content);
                bool overHardLimit = totalHistoryTokens > _budget.HistoryHardLimit;
                if (_budget.IsWorkingHistoryOverHard || overHardLimit)
                {
                    int trimmed = TrimConversationHistory(MaxConversationTurns);
                    if (trimmed > 0)
                    {
                        int kept = (_conversationHistory.Count - 2) / 2;
                        onToken($"\n[CONTEXT] History trimmed to {kept} turns (removed {trimmed} messages).\n");
                    }
                }

                // Phase 3 (Level 3) budget guard — truncate current-turn READ content if still over hard limit.
                string level3Msg = ApplyBudgetGuardLevel3();
                if (level3Msg != null)
                    onToken(level3Msg);
            }

            // Inject [CONTEXT NOTE] into current user message as LLM steering signal
            InjectContextNote();

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

                    string line = await readTask.ConfigureAwait(false);
                    if (line == null)
                        break;

                    // Phase 2 (after first token): only user-cancellation is checked;
                    // HttpClient.Timeout enforces the total request ceiling.
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

                _conversationHistory.Add(new ChatMessage("assistant", fullResponse.ToString()));
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

        /// <summary>
        /// Clears the conversation history and resets to the system prompt only.
        /// </summary>
        /// <param name="preserveScratchpad">When true, the task scratchpad is preserved across the
        /// reset so block-by-block step state survives the context clear between steps.</param>
        public void ClearHistory(bool preserveScratchpad = false)
        {
            _conversationHistory.Clear();
            _conversationHistory.Add(new ChatMessage("system", GetSystemPrompt()));
            if (!preserveScratchpad)
                _taskScratchpad = "";
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
                _conversationHistory[0] = new ChatMessage("system", prompt);
            }
            else
            {
                _conversationHistory.Insert(0, new ChatMessage("system", prompt));
            }
        }

        private static readonly Regex _reWarnings = new Regex(@"(\d+)\s+Warning", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Runs all five compression phases (0a, 0b, 0c, 4, 2) against the current conversation
        /// history. Call this after an agentic loop completes to keep history lean for the next
        /// user-initiated turn, without invalidating the KV cache during agentic iterations.
        /// Returns a log string summarising what was compressed, or null if nothing changed.
        /// </summary>
        public string RunDeferredCompression()
        {
            var log = new StringBuilder();

            string dedupMsg = DeduplicateReadBlocks();
            if (dedupMsg != null) log.Append(dedupMsg);

            string chainMsg = CollapsePatchChains();
            if (chainMsg != null) log.Append(chainMsg);

            var (shellMsg0c, _) = CompressStaleShellBlocks(includeRecentTurn: false);
            if (shellMsg0c != null) log.Append(shellMsg0c);

            _budget.Assess(_conversationHistory, EstimateTokens);

            if (_budget.IsWorkingHistoryOverSoft)
            {
                int patchCount = SqueezePatchResults();
                var (_, shellCount) = CompressStaleShellBlocks(includeRecentTurn: true);
                int readCount = SqueezeReadContent();
                int totalSqueezed = patchCount + shellCount + readCount;
                if (totalSqueezed > 0)
                    log.Append($"\n[CONTEXT] Deferred squeeze: {totalSqueezed} block(s) — {patchCount} PATCH, {shellCount} SHELL, {readCount} READ.\n");
            }

            // Phase 2 (TrimConversationHistory) is intentionally skipped here because it assumes
            // [Count-1] is a pending (unanswered) user message. After agentic loop completion,
            // the last entry is an assistant response, so the index math would be incorrect.
            // The trim will fire on the next SendMessageAsync call when the new user message is present.

            return log.Length > 0 ? log.ToString() : null;
        }

        /// <summary>
        /// Deduplicates [READ:filename] blocks across prior conversation history.
        /// For each filename that appears in more than one prior user message, all occurrences
        /// except the newest are replaced with a one-line superseded marker. This runs before
        /// all other squeeze phases so that SqueezeReadContent only processes genuinely unique reads.
        /// Returns a log string to emit via onToken, or null if nothing was deduplicated.
        /// </summary>
        private string DeduplicateReadBlocks()
        {
            int lastIndex = _conversationHistory.Count - 1;

            // Collect all unsqueezed [READ:filename] occurrences in prior messages [1..lastIndex-1].
            // Key = filename (case-insensitive), Value = list of (messageIndex, charPositionOfTag).
            //
            // Path-consistency assumption: tags are ALWAYS written as [READ:{fileNameOnly}] using
            // Path.GetFileName() in ApplyReadCommandAsync() — never a relative or absolute path.
            // Therefore grouping on the raw extracted filename is always correct; no normalization needed.
            var occurrences = new Dictionary<string, List<(int msgIdx, int charPos)>>(StringComparer.OrdinalIgnoreCase);
            var debugLines = new List<string>();

            for (int i = 1; i < lastIndex; i++)
            {
                var msg = _conversationHistory[i];
                if (msg.Role != "user") continue;

                string content = msg.Content;
                int searchFrom = 0;

                while (true)
                {
                    int tagStart = content.IndexOf("[READ:", searchFrom, StringComparison.Ordinal);
                    if (tagStart < 0) break;

                    // Skip blocks already compressed by a prior squeeze pass:
                    // [SQUEEZED][READ:...] — [SQUEEZED] is exactly 10 chars.
                    bool isSqueezed = tagStart >= 10 &&
                        string.Equals(content.Substring(tagStart - 10, 10), "[SQUEEZED]", StringComparison.Ordinal);

                    int tagEnd = content.IndexOf(']', tagStart);
                    if (tagEnd < 0) break;

                    string filename = content.Substring(tagStart + 6, tagEnd - tagStart - 6);
                    // Strip line-range suffix so [READ:file.cs:400-450] groups with [READ:file.cs]
                    filename = Regex.Replace(filename, @":\d+(?:-\d+)?$", "");

                    // BUG 2 guard: skip malformed tags where the filename is null or whitespace.
                    if (string.IsNullOrWhiteSpace(filename))
                    {
                        debugLines.Add($"[CONTEXT:DEBUG] Skipped empty filename in READ tag at message index {i}");
                        searchFrom = tagEnd + 1;
                        continue;
                    }

                    // Skip already-superseded inline markers from previous dedup passes.
                    if (!isSqueezed && !string.Equals(filename, "SUPERSEDED", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!occurrences.ContainsKey(filename))
                            occurrences[filename] = new List<(int, int)>();
                        occurrences[filename].Add((i, tagStart));
                    }

                    searchFrom = tagEnd + 1;
                }
            }

            // For each filename with more than one occurrence, supersede all but the newest.
            var logLines = new List<string>();

            foreach (var kvp in occurrences)
            {
                string filename = kvp.Key;
                var locs = kvp.Value; // already in discovery order (ascending msgIdx, charPos)

                if (locs.Count <= 1) continue;

                int totalSaved = 0;

                // All entries except the last are stale.
                for (int k = 0; k < locs.Count - 1; k++)
                {
                    int msgIdx = locs[k].msgIdx;
                    int charPos = locs[k].charPos;

                    // Read the current (possibly already partially-modified) message content.
                    string content = _conversationHistory[msgIdx].Content;
                    string role    = _conversationHistory[msgIdx].Role;

                    int tagEnd = content.IndexOf(']', charPos);
                    if (tagEnd < 0) continue;

                    // Block content starts right after the closing ']', optionally after a newline.
                    int contentStart = tagEnd + 1;
                    if (contentStart < content.Length && content[contentStart] == '\n')
                        contentStart++;

                    // Block ends at the next '\n[' sequence (start of next tag) or end of string.
                    int nextTag  = content.IndexOf("\n[", contentStart, StringComparison.Ordinal);
                    int blockEnd = nextTag >= 0 ? nextTag : content.Length;

                    string oldBlock    = content.Substring(charPos, blockEnd - charPos);
                    int    savedTokens = EstimateTokens(oldBlock);

                    string replacement = $"[SQUEEZED][READ:SUPERSEDED] {filename} — see later turn for current content";
                    string newContent  = content.Substring(0, charPos) + replacement + content.Substring(blockEnd);

                    _conversationHistory[msgIdx] = new ChatMessage(role, newContent);

                    // Adjust charPos offsets for any occurrences that live in the same message
                    // and come after the replaced block — the replacement may change string length.
                    // This must cover:
                    //   (a) other stale/kept entries for the SAME filename in this message, and
                    //   (b) entries for OTHER filenames in this message whose blocks follow the
                    //       replaced block — otherwise their stored charPos would be stale when
                    //       their own deduplication loop runs later.
                    int delta = replacement.Length - oldBlock.Length;

                    // (a) Same-filename adjustments
                    for (int j = k + 1; j < locs.Count; j++)
                    {
                        if (locs[j].msgIdx == msgIdx && locs[j].charPos > charPos)
                            locs[j] = (locs[j].msgIdx, locs[j].charPos + delta);
                    }

                    // (b) Cross-filename adjustments — fix positions for every other filename
                    //     whose occurrences in this same message would have shifted.
                    if (delta != 0)
                    {
                        foreach (var otherKvp in occurrences)
                        {
                            if (string.Equals(otherKvp.Key, filename, StringComparison.OrdinalIgnoreCase))
                                continue;
                            var otherLocs = otherKvp.Value;
                            for (int j = 0; j < otherLocs.Count; j++)
                            {
                                if (otherLocs[j].msgIdx == msgIdx && otherLocs[j].charPos > charPos)
                                    otherLocs[j] = (otherLocs[j].msgIdx, otherLocs[j].charPos + delta);
                            }
                        }
                    }

                    totalSaved += savedTokens - EstimateTokens(replacement);
                }

                if (totalSaved > 0)
                {
                    logLines.Add($"[CONTEXT] Deduplicated READ: {filename} (saved ~{totalSaved} tokens)");
                    _compressionLog.Add(filename);
                }
            }

            var allLines = new List<string>();
            allLines.AddRange(debugLines);
            allLines.AddRange(logLines);
            return allLines.Count > 0 ? string.Join("\n", allLines) + "\n" : null;
        }

        /// <summary>
        /// Collapses repeated PATCH-RESULT blocks for the same file across conversation history.
        /// When the agentic loop patches a file multiple times, every intermediate post-patch
        /// snapshot is stale — only the most recent one matters. All older occurrences are replaced
        /// with a compact chain marker so SqueezePatchResults() only ever sees the current snapshot.
        /// Returns a log string to emit via onToken, or null if nothing was collapsed.
        /// </summary>
        private string CollapsePatchChains()
        {
            int lastIndex = _conversationHistory.Count - 1;

            // Collect all [PATCH-RESULT:filename] occurrences in prior messages [1..lastIndex-1].
            // Key = filename (case-insensitive), Value = list of (messageIndex, charPositionOfTag).
            // The chain marker [SQUEEZED][PATCH:CHAIN] does NOT contain "[PATCH-RESULT:", so
            // already-collapsed blocks are invisible to this scan — idempotency is free.
            var occurrences = new Dictionary<string, List<(int msgIdx, int charPos)>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 1; i < lastIndex; i++)
            {
                var msg = _conversationHistory[i];
                if (msg.Role != "user") continue;
                if (!msg.Content.Contains("[PATCH-RESULT:")) continue;

                string content = msg.Content;
                int searchFrom = 0;

                while (true)
                {
                    int tagStart = content.IndexOf("[PATCH-RESULT:", searchFrom, StringComparison.Ordinal);
                    if (tagStart < 0) break;

                    int tagEnd = content.IndexOf(']', tagStart);
                    if (tagEnd < 0) break;

                    string filename = content.Substring(tagStart + 14, tagEnd - tagStart - 14);

                    if (!occurrences.ContainsKey(filename))
                        occurrences[filename] = new List<(int, int)>();
                    occurrences[filename].Add((i, tagStart));

                    searchFrom = tagEnd + 1;
                }
            }

            var logLines = new List<string>();

            foreach (var kvp in occurrences)
            {
                string filename = kvp.Key;
                var locs = kvp.Value; // discovery order: ascending msgIdx, charPos

                if (locs.Count <= 1) continue;

                int totalSaved = 0;
                int collapsed  = 0;

                // All entries except the last carry stale post-patch snapshots.
                for (int k = 0; k < locs.Count - 1; k++)
                {
                    int msgIdx  = locs[k].msgIdx;
                    int charPos = locs[k].charPos;

                    string content = _conversationHistory[msgIdx].Content;
                    string role    = _conversationHistory[msgIdx].Role;

                    int tagEnd = content.IndexOf(']', charPos);
                    if (tagEnd < 0) continue;

                    // Block content starts right after the closing ']', optionally after a newline.
                    int contentStart = tagEnd + 1;
                    if (contentStart < content.Length && content[contentStart] == '\n')
                        contentStart++;

                    // Block ends at the next '\n[' sequence (start of next tag) or end of string.
                    int nextTag  = content.IndexOf("\n[", contentStart, StringComparison.Ordinal);
                    int blockEnd = nextTag >= 0 ? nextTag : content.Length;

                    string oldBlock    = content.Substring(charPos, blockEnd - charPos);
                    int    savedTokens = EstimateTokens(oldBlock);

                    string replacement = $"[SQUEEZED][PATCH:CHAIN] {filename} — patch superseded, see later turn for current state";
                    string newContent  = content.Substring(0, charPos) + replacement + content.Substring(blockEnd);

                    _conversationHistory[msgIdx] = new ChatMessage(role, newContent);

                    int delta = replacement.Length - oldBlock.Length;

                    // (a) Same-filename: adjust positions for remaining stale and kept entries
                    //     in this same message.
                    for (int j = k + 1; j < locs.Count; j++)
                    {
                        if (locs[j].msgIdx == msgIdx && locs[j].charPos > charPos)
                            locs[j] = (locs[j].msgIdx, locs[j].charPos + delta);
                    }

                    // (b) Cross-filename: adjust positions for every other filename's occurrences
                    //     in this same message that follow the replaced block.
                    if (delta != 0)
                    {
                        foreach (var otherKvp in occurrences)
                        {
                            if (string.Equals(otherKvp.Key, filename, StringComparison.OrdinalIgnoreCase))
                                continue;
                            var otherLocs = otherKvp.Value;
                            for (int j = 0; j < otherLocs.Count; j++)
                            {
                                if (otherLocs[j].msgIdx == msgIdx && otherLocs[j].charPos > charPos)
                                    otherLocs[j] = (otherLocs[j].msgIdx, otherLocs[j].charPos + delta);
                            }
                        }
                    }

                    totalSaved += savedTokens - EstimateTokens(replacement);
                    collapsed++;
                }

                if (collapsed > 0)
                {
                    logLines.Add($"[CONTEXT] Collapsed patch chain: {filename} — {collapsed} patch{(collapsed == 1 ? "" : "es")} merged (saved ~{totalSaved} tokens)");
                    _compressionLog.Add(filename);
                }
            }

            return logLines.Count > 0 ? string.Join("\n", logLines) + "\n" : null;
        }

        /// <summary>
        /// Replaces old PATCH result blocks in non-current user messages with compact summaries.
        /// Returns the number of messages squeezed.
        /// </summary>
        private int SqueezePatchResults()
        {
            int count = 0;
            int lastIndex = _conversationHistory.Count - 1;

            for (int i = 1; i < lastIndex; i++)
            {
                var msg = _conversationHistory[i];
                if (msg.Role != "user") continue;
                if (!msg.Content.Contains("[PATCH-RESULT:")) continue;
                if (msg.Content.StartsWith("[SQUEEZED]", StringComparison.Ordinal)) continue;

                string original = msg.Content;

                int tagStart = original.IndexOf("[PATCH-RESULT:", StringComparison.Ordinal);
                int tagEnd   = original.IndexOf(']', tagStart);
                string filename = tagStart >= 0 && tagEnd > tagStart
                    ? original.Substring(tagStart + 14, tagEnd - tagStart - 14)
                    : "unknown";

                string squeezed;
                if (original.IndexOf("Applied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    original.IndexOf("succeeded", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string replaceSnippet = "changes applied";
                    int replaceIdx = original.IndexOf("REPLACE:", StringComparison.OrdinalIgnoreCase);
                    if (replaceIdx >= 0)
                    {
                        int contentStart = original.IndexOf('\n', replaceIdx);
                        if (contentStart >= 0 && contentStart < original.Length - 1)
                        {
                            string raw = original.Substring(contentStart + 1).TrimStart();
                            replaceSnippet = (raw.Length > 80 ? raw.Substring(0, 80) : raw)
                                .Replace('\n', ' ').Trim();
                        }
                    }
                    squeezed = $"[SQUEEZED][PATCH] Applied to {filename} — {replaceSnippet}.";
                }
                else if (original.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         original.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string errorLine = "(unknown error)";
                    foreach (var line in original.Split('\n'))
                    {
                        if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            line.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            errorLine = line.Trim();
                            if (errorLine.Length > 120) errorLine = errorLine.Substring(0, 120);
                            break;
                        }
                    }
                    squeezed = $"[SQUEEZED][PATCH] FAILED on {filename}: {errorLine}.";
                }
                else
                {
                    squeezed = $"[SQUEEZED][PATCH] Applied to {filename} — changes applied.";
                }

                _conversationHistory[i] = new ChatMessage(msg.Role, squeezed);
                _compressionLog.Add(filename);
                count++;
            }

            return count;
        }

        /// <summary>
        /// Compresses [SHELL-RESULT:] blocks in prior turns, replacing each block inline
        /// (preserving surrounding message content such as PATCH-RESULT blocks and agentic headers)
        /// with a compact summary keyed on build outcome.
        /// When <paramref name="includeRecentTurn"/> is false (Phase 0c, unconditional), the most
        /// recent prior user+assistant pair is protected so the LLM retains fresh shell context.
        /// When <paramref name="includeRecentTurn"/> is true (Phase 4, soft-pressure), all prior
        /// turns are eligible, extending coverage to the most recent pair.
        /// Returns a log string and the count of compressed blocks.
        /// </summary>
        private (string logMsg, int count) CompressStaleShellBlocks(bool includeRecentTurn = false)
        {
            int lastIndex = _conversationHistory.Count - 1;

            // History layout:
            //   [0]=system  [1..lastIndex-3]=older turns  [lastIndex-2]=last prior user
            //   [lastIndex-1]=last prior assistant  [lastIndex]=current user (just added)
            // Phase 0c: scan [1..lastIndex-3] — protect the most recent prior pair.
            // Phase 4:  scan [1..lastIndex-1] — cover the most recent prior pair too.
            int scanEnd = includeRecentTurn ? lastIndex : lastIndex - 2;
            if (scanEnd <= 1) return (null, 0);

            int totalCompressed = 0;
            int totalSaved      = 0;

            for (int i = 1; i < scanEnd; i++)
            {
                var msg = _conversationHistory[i];
                if (msg.Role != "user") continue;
                if (!msg.Content.Contains("[SHELL-RESULT:")) continue;
                // Messages already fully squeezed to a single one-liner start with [SQUEEZED].
                if (msg.Content.StartsWith("[SQUEEZED]", StringComparison.Ordinal)) continue;

                // Replace each [SHELL-RESULT:] block in the message one at a time.
                // After each replacement the block no longer contains "[SHELL-RESULT:", so
                // re-scanning from the start naturally moves to the next uncompressed block.
                // This avoids stale charPos tracking across multiple blocks in one message.
                while (true)
                {
                    string content = _conversationHistory[i].Content;

                    int tagStart = content.IndexOf("[SHELL-RESULT:", StringComparison.Ordinal);
                    if (tagStart < 0) break;

                    int tagEnd = content.IndexOf(']', tagStart);
                    if (tagEnd < 0) break;

                    string command = content.Substring(tagStart + 14, tagEnd - tagStart - 14);

                    // Block content starts right after the closing ']', optionally after a newline.
                    int contentStart = tagEnd + 1;
                    if (contentStart < content.Length && content[contentStart] == '\n')
                        contentStart++;

                    // Block ends at the next '\n[' sequence (start of next tag) or end of string.
                    int nextTag  = content.IndexOf("\n[", contentStart, StringComparison.Ordinal);
                    int blockEnd = nextTag >= 0 ? nextTag : content.Length;

                    string oldBlock    = content.Substring(tagStart, blockEnd - tagStart);
                    int    savedTokens = EstimateTokens(oldBlock);

                    string replacement = BuildShellSummary(command, oldBlock);
                    string newContent  = content.Substring(0, tagStart) + replacement + content.Substring(blockEnd);

                    _conversationHistory[i] = new ChatMessage(msg.Role, newContent);

                    totalSaved += savedTokens - EstimateTokens(replacement);
                    totalCompressed++;
                    _compressionLog.Add(command.Length > 30 ? command.Substring(0, 30) + "…" : command);
                }
            }

            if (totalCompressed == 0) return (null, 0);
            return ($"\n[CONTEXT] Compressed {totalCompressed} shell block(s) (saved ~{totalSaved} tokens)\n", totalCompressed);
        }

        /// <summary>
        /// Classifies a [SHELL-RESULT:] block and returns a compact summary string.
        /// Classification priority: build success > build failure > non-build error > success.
        /// </summary>
        private string BuildShellSummary(string command, string blockContent)
        {
            // Build success: MSBuild reports "Build succeeded" or "0 Error(s)".
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
                            sb.AppendLine($"L{pendingDocLine}: {indent}/// {pendingDoc}");
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

                        sb.AppendLine($"L{lineNumber}: {indent}{sig}");
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

        /// <summary>
        /// Replaces old READ file blocks in non-current user messages with structured outlines.
        /// For .cs files: uses GenerateCSharpOutline — namespace, types, signatures, /// docs.
        /// For other types: file-appropriate outline format.
        /// Returns the number of messages squeezed.
        /// </summary>
        private int SqueezeReadContent()
        {
            int count     = 0;
            int lastIndex = _conversationHistory.Count - 1;

            for (int i = 1; i < lastIndex; i++)
            {
                var msg = _conversationHistory[i];
                if (msg.Role != "user") continue;
                if (!msg.Content.Contains("[READ:")) continue;
                if (msg.Content.StartsWith("[SQUEEZED]", StringComparison.Ordinal)) continue;

                string original = msg.Content;

                // Collect all non-compressed, non-superseded READ blocks in this message.
                var blocks = new List<(string filename, int lineCount, string outline)>();
                int searchFrom = 0;

                while (true)
                {
                    int tagStart = original.IndexOf("[READ:", searchFrom, StringComparison.Ordinal);
                    if (tagStart < 0) break;

                    int tagEnd = original.IndexOf(']', tagStart);
                    if (tagEnd < 0) break;

                    string filename = original.Substring(tagStart + 6, tagEnd - tagStart - 6);
                    // Strip line-range suffix so [READ:file.cs:400-450] is treated as file.cs
                    filename = Regex.Replace(filename, @":\d+(?:-\d+)?$", "");

                    // Skip inline blocks already compressed by earlier squeeze phases.
                    bool isSqueezedTag = tagStart >= 10 &&
                        string.Equals(original.Substring(tagStart - 10, 10), "[SQUEEZED]", StringComparison.Ordinal);

                    if (!isSqueezedTag &&
                        !string.Equals(filename, "SUPERSEDED", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(filename, "OUTLINE",    StringComparison.OrdinalIgnoreCase))
                    {
                        int contentStart = tagEnd + 1;
                        if (contentStart < original.Length && original[contentStart] == '\n')
                            contentStart++;

                        int nextTag  = original.IndexOf("\n[", contentStart, StringComparison.Ordinal);
                        int blockEnd = nextTag >= 0 ? nextTag : original.Length;

                        string blockContent = original.Substring(contentStart, blockEnd - contentStart);
                        int    lineCount    = blockContent.Split('\n').Length;
                        string outline      = GenerateOutline(filename, blockContent);

                        blocks.Add((filename, lineCount, outline));
                    }

                    searchFrom = tagEnd + 1;
                }

                if (blocks.Count == 0) continue;

                // Build squeezed message. Single file uses spec format directly.
                // Multiple files use a single [READ:OUTLINE] header to avoid creating
                // secondary [READ:] tags that DeduplicateReadBlocks might misinterpret.
                string squeezed;
                if (blocks.Count == 1)
                {
                    var b = blocks[0];
                    squeezed = $"[SQUEEZED][READ:OUTLINE] {b.filename} ({b.lineCount} lines → outline)\n{b.outline}";
                }
                else
                {
                    int totalLines = 0;
                    var fileNames  = new List<string>();
                    foreach (var b in blocks) { totalLines += b.lineCount; fileNames.Add(b.filename); }

                    var sbAll = new StringBuilder();
                    sbAll.Append($"[SQUEEZED][READ:OUTLINE] {string.Join(", ", fileNames)} ({totalLines} lines total → outlines)");
                    foreach (var b in blocks)
                        sbAll.Append($"\n=== {b.filename} ({b.lineCount} lines) ===\n{b.outline}");
                    squeezed = sbAll.ToString();
                }

                _conversationHistory[i] = new ChatMessage(msg.Role, squeezed);
                foreach (var b in blocks)
                    _compressionLog.Add(b.filename);
                count++;
            }

            return count;
        }

        /// <summary>
        /// <summary>
        /// Immediately outline-compresses all uncompressed [READ:] blocks in the most recent
        /// user message in conversation history. Called by the agentic loop between turns so
        /// the LLM receives full file content for the current turn but history stays lean.
        /// Returns a log string to emit via onToken, or null if nothing was compressed.
        /// </summary>
        public string CompressLastUserReadBlocks()
        {
            // Find the most recent user message
            int targetIndex = -1;
            for (int i = _conversationHistory.Count - 1; i >= 1; i--)
            {
                if (_conversationHistory[i].Role == "user")
                {
                    targetIndex = i;
                    break;
                }
            }
            if (targetIndex < 0) return null;

            var msg = _conversationHistory[targetIndex];
            if (!msg.Content.Contains("[READ:")) return null;

            string original = msg.Content;

            // Collect all uncompressed READ blocks
            var blocks = new List<(string filename, int lineCount, string outline, int blockStart, int blockEnd)>();
            int searchFrom = 0;

            while (true)
            {
                int tagStart = original.IndexOf("[READ:", searchFrom, StringComparison.Ordinal);
                if (tagStart < 0) break;

                int tagEnd = original.IndexOf(']', tagStart);
                if (tagEnd < 0) break;

                string filename = original.Substring(tagStart + 6, tagEnd - tagStart - 6);

                bool isSqueezed = tagStart >= 10 &&
                    string.Equals(original.Substring(tagStart - 10, 10), "[SQUEEZED]", StringComparison.Ordinal);
                bool isSpecial = string.Equals(filename, "SUPERSEDED", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(filename, "OUTLINE",    StringComparison.OrdinalIgnoreCase);

                if (!isSqueezed && !isSpecial)
                {
                    int contentStart = tagEnd + 1;
                    if (contentStart < original.Length && original[contentStart] == '\n')
                        contentStart++;

                    int nextTag  = original.IndexOf("\n[", contentStart, StringComparison.Ordinal);
                    int blockEnd = nextTag >= 0 ? nextTag : original.Length;

                    string blockContent = original.Substring(contentStart, blockEnd - contentStart);
                    int    lineCount    = blockContent.Split('\n').Length;
                    string outline      = GenerateOutline(filename, blockContent);

                    blocks.Add((filename, lineCount, outline, tagStart, blockEnd));
                }

                searchFrom = tagEnd + 1;
            }

            if (blocks.Count == 0) return null;

            // Rebuild the message replacing each block in reverse order to preserve offsets
            var sb = new StringBuilder(original);
            var logParts = new List<string>();
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                var b = blocks[i];
                // Bounds guard: positions were computed from the original string; after prior iterations
                // modified sb at higher positions the lower-region should be unchanged, but guard
                // defensively to prevent "Index was out of range" if any edge case shifts lengths.
                if (b.blockStart < 0 || b.blockEnd > sb.Length || b.blockStart > b.blockEnd)
                    continue;
                int tokensBefore = EstimateTokens(original.Substring(b.blockStart, b.blockEnd - b.blockStart));
                int tokensAfter  = EstimateTokens(b.outline);
                string replacement = $"[SQUEEZED][READ:OUTLINE] {b.filename} ({b.lineCount} lines → outline)\n{b.outline}";
                sb.Remove(b.blockStart, b.blockEnd - b.blockStart);
                sb.Insert(b.blockStart, replacement);
                _compressionLog.Add(b.filename);
                logParts.Add($"{b.filename} ({tokensBefore} tokens → {tokensAfter} tokens)");
            }

            _conversationHistory[targetIndex] = new ChatMessage(msg.Role, sb.ToString());

            logParts.Reverse(); // restore natural reading order
            return $"\n[CONTEXT] Post-turn outline: {string.Join(", ", logParts)}\n";
        }

        /// Pre-squeezes any [READ:filename] block in the incoming userMessage whose token cost
        /// alone exceeds WorkingHistoryLimit. Converts the content to an outline in-place,
        /// before the message is added to _conversationHistory.
        /// Returns the (possibly modified) userMessage and an optional status string for onToken.
        /// </summary>
        private string PreSqueezeOversizedRead(string userMessage, out string logMsg)
        {
            logMsg = null;
            if (string.IsNullOrEmpty(userMessage)) return userMessage;
            if (!userMessage.Contains("[READ:")) return userMessage;

            int workingLimit = _budget.WorkingHistoryLimit;
            if (workingLimit <= 0) return userMessage;

            int tagStart = userMessage.IndexOf("[READ:", StringComparison.Ordinal);
            while (tagStart >= 0)
            {
                // Skip squeezed/superseded/outline tags
                bool isSqueezed = tagStart >= 10 &&
                    string.Equals(userMessage.Substring(tagStart - 10, 10), "[SQUEEZED]", StringComparison.Ordinal);

                int tagEnd = userMessage.IndexOf(']', tagStart);
                if (tagEnd < 0) break;

                string filename = userMessage.Substring(tagStart + 6, tagEnd - tagStart - 6);
                bool isSpecial  = string.Equals(filename, "SUPERSEDED", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(filename, "OUTLINE",    StringComparison.OrdinalIgnoreCase);

                if (!isSqueezed && !isSpecial)
                {
                    int contentStart = tagEnd + 1;
                    if (contentStart < userMessage.Length && userMessage[contentStart] == '\n')
                        contentStart++;

                    int nextTag  = userMessage.IndexOf("\n[", contentStart, StringComparison.Ordinal);
                    int blockEnd = nextTag >= 0 ? nextTag : userMessage.Length;

                    string blockContent = userMessage.Substring(contentStart, blockEnd - contentStart);
                    int    blockTokens  = EstimateTokens(blockContent);

                    if (blockTokens > workingLimit)
                    {
                        string[] lines     = blockContent.Split('\n');
                        int      lineCount = lines.Length;

                        string truncated;
                        if (lineCount <= BudgetGuardKeepHead + BudgetGuardKeepTail)
                        {
                            truncated = blockContent;
                        }
                        else
                        {
                            int omitted = lineCount - BudgetGuardKeepHead - BudgetGuardKeepTail;
                            var head    = new string[BudgetGuardKeepHead];
                            var tail    = new string[BudgetGuardKeepTail];
                            Array.Copy(lines, 0, head, 0, BudgetGuardKeepHead);
                            Array.Copy(lines, lineCount - BudgetGuardKeepTail, tail, 0, BudgetGuardKeepTail);
                            truncated = string.Join("\n", head)
                                + $"\n[... {omitted} lines omitted — use READ with line range to see specific sections ...]\n"
                                + string.Join("\n", tail);
                        }

                        string replacement = $"[SQUEEZED][READ:{filename}]\n{truncated}";

                        userMessage = userMessage.Substring(0, tagStart) + replacement
                            + (nextTag >= 0 ? userMessage.Substring(nextTag) : "");

                        int truncatedTokens = EstimateTokens(truncated);
                        logMsg = $"\n[CONTEXT] Pre-squeezed oversized READ: {filename} ({blockTokens} tokens → {truncatedTokens} tokens head+tail)\n";
                        _compressionLog.Add(filename);

                        // Restart scan from beginning — userMessage changed
                        tagStart = userMessage.IndexOf("[READ:", 0, StringComparison.Ordinal);
                        continue;
                    }
                }

                tagStart = userMessage.IndexOf("[READ:", tagEnd + 1, StringComparison.Ordinal);
            }

            return userMessage;
        }

        private const int BudgetGuardKeepHead = 200;
        private const int BudgetGuardKeepTail = 50;

        /// <summary>
        /// Budget Guard Level 3: if total estimated tokens still exceed HistoryHardLimit after
        /// squeezing and trimming, truncates every uncompressed [READ:] block in the current user
        /// message (last entry in _conversationHistory) to head+tail lines, stopping early if the
        /// budget comes within the hard limit. Blocks are processed in reverse order so that char
        /// positions remain valid across successive replacements.
        /// Returns a notification string to emit via onToken, or null if no action was taken.
        /// </summary>
        private string ApplyBudgetGuardLevel3()
        {
            if (EstimateHistoryTokens() <= _budget.HistoryHardLimit)
                return null;

            int lastIndex = _conversationHistory.Count - 1;
            var currentMsg = _conversationHistory[lastIndex];
            if (currentMsg.Role != "user") return null;

            string content = currentMsg.Content;
            if (!content.Contains("[READ:")) return null;

            // Collect all uncompressed READ blocks: (filename, blockStart, blockEnd, originalLineCount).
            // blockStart = start of [READ:...] tag; blockEnd = end of block content.
            var blocks = new List<(string filename, int contentStart, int blockEnd, int lineCount)>();
            int searchFrom = 0;

            while (true)
            {
                int tagStart = content.IndexOf("[READ:", searchFrom, StringComparison.Ordinal);
                if (tagStart < 0) break;

                // Skip already-squeezed tags: [SQUEEZED][READ:...]
                bool isSqueezed = tagStart >= 10 &&
                    string.Equals(content.Substring(tagStart - 10, 10), "[SQUEEZED]", StringComparison.Ordinal);

                int tagEnd = content.IndexOf(']', tagStart);
                if (tagEnd < 0) break;

                string filename = content.Substring(tagStart + 6, tagEnd - tagStart - 6);
                // Strip line-range suffix (e.g. [READ:file.cs:100-200] → file.cs)
                filename = Regex.Replace(filename, @":\d+(?:-\d+)?$", "");

                bool isSpecial = string.Equals(filename, "SUPERSEDED", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(filename, "OUTLINE",    StringComparison.OrdinalIgnoreCase);

                if (!isSqueezed && !isSpecial)
                {
                    int cs = tagEnd + 1;
                    if (cs < content.Length && content[cs] == '\n') cs++;

                    int nextTag  = content.IndexOf("\n[", cs, StringComparison.Ordinal);
                    int blockEnd = nextTag >= 0 ? nextTag : content.Length;

                    string fileContent = content.Substring(cs, blockEnd - cs);
                    int    lineCount   = fileContent.Split('\n').Length;

                    blocks.Add((filename, cs, blockEnd, lineCount));
                }

                searchFrom = tagEnd + 1;
            }

            if (blocks.Count == 0) return null;

            // Process in reverse order so earlier block positions stay valid.
            var truncatedFiles = new List<string>();

            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                // Re-check budget before each truncation — stop if already within limit.
                if (EstimateHistoryTokens() <= _budget.HistoryHardLimit) break;

                var (filename, cs, blockEnd, lineCount) = blocks[i];

                if (lineCount <= BudgetGuardKeepHead + BudgetGuardKeepTail)
                    continue; // block is already small enough; try the next one

                // Re-read content from history (prior iterations may have modified the message).
                string current = _conversationHistory[lastIndex].Content;

                // The positions collected earlier still map to the correct location because we
                // process in reverse — no earlier-in-string block has been modified yet.
                string fileContent = current.Substring(cs, blockEnd - cs);
                string[] lines     = fileContent.Split('\n');
                int      origLines = lines.Length;

                var headLines = new string[BudgetGuardKeepHead];
                var tailLines = new string[BudgetGuardKeepTail];
                Array.Copy(lines, 0, headLines, 0, BudgetGuardKeepHead);
                Array.Copy(lines, origLines - BudgetGuardKeepTail, tailLines, 0, BudgetGuardKeepTail);

                int    omitted  = origLines - BudgetGuardKeepHead - BudgetGuardKeepTail;
                string truncated = string.Join("\n", headLines)
                    + $"\n[... {omitted} lines omitted by budget guard ...]\n"
                    + string.Join("\n", tailLines);

                string newContent = current.Substring(0, cs) + truncated + current.Substring(blockEnd);
                _conversationHistory[lastIndex] = new ChatMessage(currentMsg.Role, newContent);

                _compressionLog.Add(filename);
                truncatedFiles.Add($"{filename} ({origLines}→{BudgetGuardKeepHead + BudgetGuardKeepTail} lines)");
            }

            if (truncatedFiles.Count == 0) return null;

            truncatedFiles.Reverse(); // restore natural reading order
            return $"\n[CONTEXT] Level 3: Truncated {string.Join(", ", truncatedFiles)} to fit budget.\n";
        }

        /// <summary>
        /// Removes the oldest user/assistant pairs from history, keeping at most <paramref name="maxTurns"/> pairs.
        /// Always preserves index 0 (system) and the last entry (pending user message).
        /// Returns the number of messages removed.
        /// </summary>
        private int TrimConversationHistory(int maxTurns)
        {
            // Layout: [0]=system, [1..Count-2]=prior turns (user/assistant pairs), [Count-1]=current user
            // Prior turn count (complete pairs only — last entry is the not-yet-answered user message)
            int priorMessages = _conversationHistory.Count - 2; // excludes system and current user
            if (priorMessages <= 0)
                return 0;

            int priorPairs = priorMessages / 2;
            if (priorPairs <= maxTurns)
                return 0;

            int pairsToRemove = priorPairs - maxTurns;
            int messagesToRemove = pairsToRemove * 2;
            _conversationHistory.RemoveRange(1, messagesToRemove);
            _compressionLog.Add($"(history trim: {pairsToRemove} turn{(pairsToRemove == 1 ? "" : "s")} removed)");
            return messagesToRemove;
        }

        /// <summary>
        /// Strips any [CONTEXT NOTE] prefix injected in a prior call from all user messages
        /// in [1..N-2] (prior turns). The current user message (N-1) is not yet present when
        /// this runs — it is added before this call in SendMessageAsync.
        /// The prefix format is: "[CONTEXT NOTE] ...\n\n" — everything up to and including
        /// the first blank line.
        /// </summary>
        private void StripContextNotes()
        {
            const string marker = "[CONTEXT NOTE]";
            int last = _conversationHistory.Count - 1;
            // Strip from all prior user messages (skip system [0], skip current user [last])
            for (int i = 1; i < last; i++)
            {
                var msg = _conversationHistory[i];
                if (msg.Role != "user") continue;
                string content = msg.Content;
                if (!content.StartsWith(marker, StringComparison.Ordinal)) continue;

                // Find the blank line that terminates the note header
                int blankLine = content.IndexOf("\n\n", marker.Length, StringComparison.Ordinal);
                if (blankLine >= 0)
                    _conversationHistory[i] = new ChatMessage("user", content.Substring(blankLine + 2));
            }
        }

        /// <summary>
        /// Prepends a compact [CONTEXT NOTE] to the current user message (last in history)
        /// listing every filename/command logged in _compressionLog. The note is under ~60 tokens
        /// and tells the LLM which files were compressed so it can request READs if needed.
        /// Does nothing if _compressionLog is empty.
        /// </summary>
        private void InjectContextNote()
        {
            if (_compressionLog.Count == 0) return;

            int last = _conversationHistory.Count - 1;
            if (last < 0) return;

            // Deduplicate while preserving order
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<string>();
            foreach (var entry in _compressionLog)
            {
                if (seen.Add(entry))
                    unique.Add(entry);
            }

            // Budget the file list: fixed template costs ~23 tokens (~92 chars).
            // Reserve 37 tokens (~148 chars) for the file list to stay under 60 tokens total.
            const int MaxListChars = 148;
            string affected;
            int shown = 0;
            var sb = new StringBuilder();
            foreach (var name in unique)
            {
                string part = shown == 0 ? name : ", " + name;
                if (sb.Length + part.Length > MaxListChars)
                {
                    int remaining = unique.Count - shown;
                    sb.Append($", and {remaining} more");
                    shown = unique.Count; // exit loop
                    break;
                }
                sb.Append(part);
                shown++;
            }
            affected = sb.ToString();

            string note = $"[CONTEXT NOTE] {unique.Count} block(s) compressed. Affected: {affected}. Use READ to reload any file you need.\n\n";

            var current = _conversationHistory[last];
            _conversationHistory[last] = new ChatMessage(current.Role, note + current.Content);
        }

        private string BuildRequestJson(string modelName)
        {
            var messages = new JArray();
            int lastIdx   = _conversationHistory.Count - 1;
            for (int mi = 0; mi <= lastIdx; mi++)
            {
                var msg     = _conversationHistory[mi];
                string content = msg.Content;

                // Prepend task scratchpad to the last (current) user message only.
                // This keeps _conversationHistory clean — scratchpad is never stored in history.
                if (mi == lastIdx && msg.Role == "user" && !string.IsNullOrEmpty(_taskScratchpad))
                    content = $"[TASK STATE]\n{_taskScratchpad}\n[/TASK STATE]\n\n{content}";

                messages.Add(new JObject
                {
                    ["role"]    = msg.Role,
                    ["content"] = content
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
