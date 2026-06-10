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

        /// <summary>Estimates total history token usage from conversation message lengths.</summary>
        int EstimateHistoryTokens();

        /// <summary>Injects a tool result message into conversation history.</summary>
        void AddToolResultMessage(string toolCallId, string content);

        /// <summary>Streams a message to the LLM endpoint, invoking callbacks as tokens arrive.</summary>
        Task SendMessageAsync(
            string userMessage,
            Action<string> onToken,
            Action onComplete,
            Action<Exception> onError,
            bool deferCompression = false,
            string combinedSystemPrompt = null,
            CancellationToken cancellationToken = default);

       /// <summary>Resets conversation history to the system prompt only.</summary>
        void ClearHistory(bool preserveScratchpad = false);

        /// <summary>
        /// Prepends messages into the conversation history (after the system prompt).
        /// Used by /resume to load prior session messages into context.
        /// </summary>
        void PrependMessages(string[] roles, string[] contents);
    }
}
