// File: LlmClient.cs  v5.6
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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
        private readonly int _contextSize = 16716;
        private const int MaxConversationTurns = 4;

        public int MaxPromptTokens => (int)(_contextSize * 0.80);

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

            // Phase 4: squeeze PATCH, SHELL, and READ blocks before trim (order matters: PATCH first)
            int patchCount = SqueezePatchResults();
            int shellCount = SqueezeShellResults();
            int readCount  = SqueezeReadContent();
            int totalSqueezed = patchCount + shellCount + readCount;
            if (totalSqueezed > 0)
                onToken($"\n[CONTEXT] Squeezed {totalSqueezed} block(s) — {patchCount} PATCH, {shellCount} SHELL, {readCount} READ.\n");

            // Phase 2: sliding window trim — runs before budget guard so token count reflects trimmed history
            int trimmed = TrimConversationHistory(MaxConversationTurns);
            if (trimmed > 0)
            {
                // history is 0-indexed: [system] + pairs + [current user]
                int kept = (_conversationHistory.Count - 2) / 2; // user/assistant pairs retained
                onToken($"\n[CONTEXT] History trimmed to {kept} turns (removed {trimmed} messages).\n");
            }

            // Phase 3 (Level 3) budget guard — truncate current-turn READ content if still over budget
            string level3Msg = ApplyBudgetGuardLevel3();
            if (level3Msg != null)
                onToken(level3Msg);

            // Token count log
            int totalTokens = 0;
            foreach (var msg in _conversationHistory)
                totalTokens += EstimateTokens(msg.Content);

            int budget = MaxPromptTokens;
            if (totalTokens > budget)
            {
                onToken($"\n[CONTEXT] WARNING: Budget exceeded after truncation. Response quality may be reduced.\n");
            }
            else
            {
                int pct = budget > 0 ? (int)((totalTokens * 100.0) / budget) : 0;
                onToken($"\n[CONTEXT] {totalTokens} / {budget} tokens ({pct}%)\n");
            }

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

        private static readonly Regex _reWarnings = new Regex(@"(\d+)\s+Warning", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
                count++;
            }

            return count;
        }

        /// <summary>
        /// Replaces old SHELL result blocks in non-current user messages with compact summaries.
        /// Returns the number of messages squeezed.
        /// </summary>
        private int SqueezeShellResults()
        {
            int count = 0;
            int lastIndex = _conversationHistory.Count - 1;

            for (int i = 1; i < lastIndex; i++)
            {
                var msg = _conversationHistory[i];
                if (msg.Role != "user") continue;
                if (!msg.Content.Contains("[SHELL-RESULT:")) continue;
                if (msg.Content.StartsWith("[SQUEEZED]", StringComparison.Ordinal)) continue;

                string original = msg.Content;

                int tagStart = original.IndexOf("[SHELL-RESULT:", StringComparison.Ordinal);
                int tagEnd   = original.IndexOf(']', tagStart);
                string command = tagStart >= 0 && tagEnd > tagStart
                    ? original.Substring(tagStart + 14, tagEnd - tagStart - 14)
                    : "unknown";

                string squeezed;
                if (original.IndexOf("Build succeeded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    original.IndexOf("0 Error(s)", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var warnMatch = _reWarnings.Match(original);
                    int warnings = warnMatch.Success ? int.Parse(warnMatch.Groups[1].Value) : 0;
                    squeezed = $"[SQUEEZED][SHELL] {command} → succeeded, {warnings} warnings, 0 errors.";
                }
                else if (original.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         original.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var errorLines = new List<string>();
                    foreach (var line in original.Split('\n'))
                    {
                        if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            line.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            errorLines.Add(line.Trim());
                            if (errorLines.Count >= 5) break;
                        }
                    }
                    string detail = errorLines.Count > 0 ? "\n" + string.Join("\n", errorLines) : "";
                    squeezed = $"[SQUEEZED][SHELL] {command} → FAILED:{detail}";
                }
                else
                {
                    int lineCount = original.Split('\n').Length;
                    squeezed = $"[SQUEEZED][SHELL] {command} → completed ({lineCount} lines of output).";
                }

                _conversationHistory[i] = new ChatMessage(msg.Role, squeezed);
                count++;
            }

            return count;
        }

        private static readonly Regex _reClass  = new Regex(@"class\s+(\w+)",                                              RegexOptions.Compiled);
        private static readonly Regex _reMethod = new Regex(@"(?:private|public|protected|internal)\s+\S+\s+(\w+)\s*\(", RegexOptions.Compiled);

        /// <summary>
        /// Replaces old READ file blocks in non-current user messages with compact summaries.
        /// Returns the number of messages squeezed.
        /// </summary>
        private int SqueezeReadContent()
        {
            int count = 0;
            int lastIndex = _conversationHistory.Count - 1;

            for (int i = 1; i < lastIndex; i++)
            {
                var msg = _conversationHistory[i];
                if (msg.Role != "user") continue;
                if (!msg.Content.Contains("[READ:")) continue;
                if (msg.Content.StartsWith("[SQUEEZED]", StringComparison.Ordinal)) continue;

                string original = msg.Content;

                // Extract filename from first [READ:...] tag
                int tagStart = original.IndexOf("[READ:", StringComparison.Ordinal);
                int tagEnd   = original.IndexOf(']', tagStart);
                string filename = tagStart >= 0 && tagEnd > tagStart
                    ? original.Substring(tagStart + 6, tagEnd - tagStart - 6)
                    : "unknown";

                int lineCount   = original.Split('\n').Length;
                int tokenCount  = EstimateTokens(original);

                // Collect up to 10 unique class + method names
                var names = new List<string>();
                foreach (Match m in _reClass.Matches(original))
                {
                    string n = m.Groups[1].Value;
                    if (!names.Contains(n)) names.Add(n);
                    if (names.Count >= 10) break;
                }
                if (names.Count < 10)
                {
                    foreach (Match m in _reMethod.Matches(original))
                    {
                        string n = m.Groups[1].Value;
                        if (!names.Contains(n)) names.Add(n);
                        if (names.Count >= 10) break;
                    }
                }

                string detected = names.Count > 0 ? string.Join(", ", names) : "(none)";
                string squeezed = $"[SQUEEZED][READ:{filename}] {lineCount} lines, ~{tokenCount} tokens. Detected: {detected}. Re-READ if needed.";

                _conversationHistory[i] = new ChatMessage(msg.Role, squeezed);
                count++;
            }

            return count;
        }

        private const int BudgetGuardKeepHead = 200;
        private const int BudgetGuardKeepTail = 50;

        /// <summary>
        /// Budget Guard Level 3: if total estimated tokens still exceed MaxPromptTokens after
        /// squeezing and trimming, truncates [READ:] file content in the current user message
        /// (last entry in _conversationHistory) to head+tail lines.
        /// Returns a notification string to emit via onToken, or null if no action was taken.
        /// </summary>
        private string ApplyBudgetGuardLevel3()
        {
            int totalTokens = 0;
            foreach (var msg in _conversationHistory)
                totalTokens += EstimateTokens(msg.Content);

            if (totalTokens <= MaxPromptTokens)
                return null;

            int lastIndex = _conversationHistory.Count - 1;
            var currentMsg = _conversationHistory[lastIndex];
            if (currentMsg.Role != "user") return null;

            string content = currentMsg.Content;
            int readTagStart = content.IndexOf("[READ:", StringComparison.Ordinal);
            if (readTagStart < 0) return null;

            int readTagEnd = content.IndexOf(']', readTagStart);
            string filename = readTagStart >= 0 && readTagEnd > readTagStart
                ? content.Substring(readTagStart + 6, readTagEnd - readTagStart - 6)
                : "unknown";

            // Find the file content: everything after the [READ:...] tag line up to next tag or end
            int contentStart = readTagEnd + 1;
            if (contentStart < content.Length && content[contentStart] == '\n')
                contentStart++;

            // Find where READ content ends (next [tag] or end of string)
            int nextTag = content.IndexOf("\n[", contentStart, StringComparison.Ordinal);
            string fileContent = nextTag >= 0
                ? content.Substring(contentStart, nextTag - contentStart)
                : content.Substring(contentStart);

            string[] lines = fileContent.Split('\n');
            int originalLines = lines.Length;

            if (originalLines <= BudgetGuardKeepHead + BudgetGuardKeepTail)
                return null; // nothing to cut

            var headLines = new string[BudgetGuardKeepHead];
            var tailLines = new string[BudgetGuardKeepTail];
            Array.Copy(lines, 0, headLines, 0, BudgetGuardKeepHead);
            Array.Copy(lines, originalLines - BudgetGuardKeepTail, tailLines, 0, BudgetGuardKeepTail);

            int omitted = originalLines - BudgetGuardKeepHead - BudgetGuardKeepTail;
            string truncated = string.Join("\n", headLines)
                + $"\n[... {omitted} lines omitted by budget guard ...]\n"
                + string.Join("\n", tailLines);

            string newContent = nextTag >= 0
                ? content.Substring(0, contentStart) + truncated + content.Substring(nextTag)
                : content.Substring(0, contentStart) + truncated;

            _conversationHistory[lastIndex] = new ChatMessage(currentMsg.Role, newContent);

            int newTotal = 0;
            foreach (var msg in _conversationHistory)
                newTotal += EstimateTokens(msg.Content);

            return $"\n[CONTEXT] Level 3: Truncated {filename} from {originalLines} to {BudgetGuardKeepHead + BudgetGuardKeepTail} lines to fit budget.\n";
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
            return messagesToRemove;
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
                ["stream"] = true,
                ["thinking"] = new JObject
                {
                    ["type"] = "disabled"
                }
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
