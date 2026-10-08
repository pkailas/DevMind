// File: LlmEnums.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.ComponentModel;

namespace DevMind
{
    /// <summary>
    /// Identifies the type of LLM server for context-size detection.
    /// </summary>
    public enum LlmServerType
    {
        [Description("llama-server")]
        LlamaServer,
        [Description("LM Studio")]
        LmStudio,
        [Description("vLLM")]
        Vllm,
        [Description("Custom")]
        Custom,
        // Appended, not inserted: a value's number never moves. Strata speaks llama-server's
        // protocol (/props, timings, chat_template_kwargs) and, until it gets its own handling,
        // takes every LlamaServer branch — see SpeaksLlamaServerProtocol.
        [Description("Strata")]
        Strata
    }

    /// <summary>Questions about an <see cref="LlmServerType"/> that more than one value answers alike.</summary>
    public static class LlmServerTypeExtensions
    {
        /// <summary>
        /// True for a server that speaks llama-server's protocol: GET /props for the context
        /// size, per-chunk <c>timings</c> on request, and reasoning toggled through
        /// <c>chat_template_kwargs</c>. llama-server itself, and Strata, which mirrors it.
        /// </summary>
        public static bool SpeaksLlamaServerProtocol(this LlmServerType serverType)
            => serverType is LlmServerType.LlamaServer or LlmServerType.Strata;
    }

    /// <summary>
    /// Controls how aggressively old context is compressed via tiered eviction.
    /// </summary>
    public enum ContextEvictionMode
    {
        [Description("Off")]
        Off,
        [Description("Balanced")]
        Balanced,
        [Description("Aggressive")]
        Aggressive
    }
}
