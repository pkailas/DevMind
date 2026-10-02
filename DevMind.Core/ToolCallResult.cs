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

        /// <summary>
        /// Set by ToolCallMapper.Map when <see cref="Id"/> is missing or repeats an earlier
        /// call's in the same turn: a unique stand-in used only to key this call's result
        /// (<see cref="ResultId"/>). The tool message still carries <see cref="Id"/> as its
        /// tool_call_id, because it has to match the assistant message already in history.
        /// </summary>
        public string FallbackId { get; set; }

        /// <summary>The id this call's result is filed and looked up under: <see cref="FallbackId"/> if set, else <see cref="Id"/>.</summary>
        public string ResultId => FallbackId ?? Id;

        /// <summary>
        /// Set by ToolCallMapper.Map when the call cannot run as sent: a required argument is
        /// missing or blank, a patch_file edit has no 'find', or the tool is unknown (H-53/H-54).
        /// The call is not executed; this text is its tool result, so the model learns which
        /// key it got wrong instead of reading a raw ArgumentNullException or nothing at all.
        /// </summary>
        public string ArgumentError { get; set; }

        /// <summary>
        /// Set by ToolCallMapper.Map when it rewrote the arguments before running the call:
        /// patch_file alias keys renamed to find/replace (H-56). Journaled once per call, so
        /// how often the model reaches for the aliases is visible.
        /// </summary>
        public string ArgumentNote { get; set; }

        /// <summary>Reasoning/thinking text from the model, if present.</summary>
        public string ThinkingText { get; set; }
    }
}
