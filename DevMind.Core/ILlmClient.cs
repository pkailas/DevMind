// File: ILlmClient.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// Abstraction over <see cref="LlmClient"/> for testability (C.1b TestBed)
    /// and Phase D CLI REPL (which cannot depend on the WPF-coupled concrete class).
    /// Exposes the members consumed by <see cref="LoopDriver"/> and
    /// <see cref="LoopHelpers"/>, plus the send/clear surface callers need to drive the loop.
    /// </summary>
    public interface ILlmClient
    {
        /// <summary>Parsed tool calls from the last LLM response; null when no tool calls were made.</summary>
        List<ToolCallResult> LastToolCalls { get; }

        /// <summary>Server-reported context window size (n_ctx). 0 until detected.</summary>
        int ServerContextSize { get; }

        /// <summary>Hard history limit (server context minus response headroom). 0 until detected.</summary>
        int MaxPromptTokens { get; }

        /// <summary>Server-reported tokens-in-use from the last response (n_past). 0 until first response.</summary>
        int LastContextUsed { get; }

        /// <summary>Generated-token count from the last response's server timings (predicted_n). 0 until first response.</summary>
        int LastGeneratedTokens { get; }

        /// <summary>Live running output-token count for the IN-PROGRESS response, read from each
        /// SSE chunk's <c>usage.completion_tokens</c>. Increments across content, reasoning, and
        /// tool_call chunks alike (unlike a content-delta counter). Reset to 0 at the start of each
        /// send; finalized to predicted_n at completion. 0 when the server does not report usage.</summary>
        int LiveGeneratedTokens { get; }

        /// <summary>Live running input-token count for the in-progress response, read from each SSE
        /// chunk's <c>usage.prompt_tokens</c>. 0 when the server does not report usage.</summary>
        int LivePromptTokens { get; }

        /// <summary>Server-true live generation rate (tokens/second) for the in-progress response,
        /// read from llama-server's per-chunk <c>timings.predicted_per_second</c> (request must set
        /// <c>timings_per_token</c>). Spec-decode aware. 0 when the server reports no per-chunk
        /// timings (e.g. vLLM), so callers fall back to a client wall-clock rate.</summary>
        double LiveTokensPerSecond { get; }

        /// <summary>Generation wall time in milliseconds from the last response's server timings (predicted_ms). 0 until first response.</summary>
        double LastGeneratedMs { get; }

        /// <summary>Prompt-token count from the last response's server timings (prompt_n). 0 until first response.</summary>
        int LastPromptTokens { get; }

        /// <summary>1-based index of the current user turn (incremented by <see cref="LlmClient.IncrementTurn"/>).</summary>
        int CurrentTurn { get; }

        /// <summary>The active system prompt (first message in conversation history), or null when none.</summary>
        string SystemPromptContent { get; }

        /// <summary>The most recent micro-compaction summary, or null when none has occurred.</summary>
        string LastCompactionSummary { get; }

        /// <summary>Estimates total history token usage from conversation message lengths.</summary>
        int EstimateHistoryTokens();

        /// <summary>Injects a tool result message into conversation history. <paramref name="toolName"/>
        /// lets the ingest-capping policy exempt tools whose results must arrive verbatim (recall_cache).</summary>
        void AddToolResultMessage(string toolCallId, string content, string toolName = null);

        /// <summary>Stages an image (data: URI or raw base64) for the next
        /// <see cref="SendMessageAsync"/> call. Staged images accumulate — call once per
        /// image (e.g. each page of a PDF range) and the next send attaches all of them
        /// to that one message, consumed exactly once. Null/whitespace clears all staged
        /// images. Used by the TUI /image command.</summary>
        void StagePendingImage(string imageDataUri);

       /// <summary>Streams a message to the LLM endpoint, invoking callbacks as tokens arrive.</summary>
        /// <param name="imageBase64">Optional base64-encoded image data (data: URI format). When provided,
        /// the message is sent as multimodal content with both text and image parts.</param>
        Task SendMessageAsync(
            string userMessage,
            Action<string> onToken,
            Action onComplete,
            Action<Exception> onError,
            bool deferCompression = false,
            string combinedSystemPrompt = null,
            CancellationToken cancellationToken = default,
            bool forceToolChoiceRequired = false,
            string imageBase64 = null,
            int maxTokens = 0);

       /// <summary>Resets conversation history to the system prompt only.</summary>
        void ClearHistory(bool preserveScratchpad = false);

        /// <summary>
        /// Prepends messages into the conversation history (after the system prompt).
        /// Used by /resume to load prior session messages into context.
        /// </summary>
        void PrependMessages(string[] roles, string[] contents);
    }
}
