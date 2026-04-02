// File: ToolCallResult.cs  v7.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;

namespace DevMind
{
    /// <summary>
    /// Unified representation of a single tool call extracted from the LLM response.
    /// Normalized across ik_llama.cpp and Ollama OpenAI-compatible formats.
    /// </summary>
    public sealed class ToolCallResult
    {
        /// <summary>Tool call ID (e.g., "call_abc123"). Used for tool result messages.</summary>
        public string Id { get; set; }

        /// <summary>Function name (e.g., "read_file", "patch_file").</summary>
        public string Name { get; set; }

        /// <summary>Parsed arguments — all values are strings; the mapper parses types as needed.</summary>
        public Dictionary<string, string> Arguments { get; set; } = new Dictionary<string, string>();

        /// <summary>Reasoning/thinking text from the model, if present.</summary>
        public string ThinkingText { get; set; }
    }
}
