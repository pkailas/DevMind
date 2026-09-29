// File: ToolCallResult.cs  v7.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

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

        /// <summary>
        /// The same arguments as a typed JSON object (after the syntax-only repair ladder), or
        /// null when the producer did not set it. <see cref="Arguments"/> flattens every value
        /// through JToken.ToString(), which loses types (true becomes "True", 2 becomes "2",
        /// null becomes ""); external MCP tools validate against their own JSON Schema, so an
        /// mcp__ call forwards this object instead.
        /// </summary>
        public JObject RawArguments { get; set; }

        /// <summary>Reasoning/thinking text from the model, if present.</summary>
        public string ThinkingText { get; set; }
    }
}
