// File: ContextOverflow.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Recognises the one server rejection DevMind can do something about: the request did not
// fit in the model's context window.
//
// Why this is worth its own type: the rejection is the only moment DevMind receives a
// MEASURED prompt size. Everywhere else it estimates — chars/4 and an n_past heuristic that
// drifts from what the tokenizer actually produced. The error body carries both the exact
// token count the server measured and the exact ceiling it measured against, so the recovery
// path can size a compaction against real numbers instead of its own guess.
//
// Detection is layered because the shape varies by server build:
//   1. the structured type, which llama.cpp b10499 and later emit;
//   2. the message text, for builds that return a bare 400 with prose;
//   3. HTTP 413, which some proxies substitute before the model server is reached.
//
// Everything else is somebody else's error. A 400 for an unknown model, a 500, a malformed
// body: all false, so the caller throws exactly what it threw before. Nothing here throws —
// a parser that can fail turns one bad response into a crash on the error path.

using System;
using Newtonsoft.Json.Linq;

namespace DevMind
{
    /// <summary>
    /// What the server reported when it rejected a request for size. Absent numbers are 0,
    /// never -1 or null: callers size a compaction from these, and a sentinel that reads as
    /// a quantity is how a sentinel becomes a bug.
    /// </summary>
    public sealed class ContextOverflowInfo
    {
        /// <summary>The prompt size the server measured, or 0 when it did not report one.</summary>
        public int PromptTokens { get; set; }

        /// <summary>The context window the server measured against, or 0 when not reported.</summary>
        public int ContextSize { get; set; }

        /// <summary>The server's own message, verbatim, or empty. Surfaced, never parsed for meaning.</summary>
        public string ServerMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Classifies an HTTP error response as "the prompt did not fit" or "something else".
    /// </summary>
    public static class ContextOverflow
    {
        /// <summary>The structured discriminator llama.cpp emits for this condition.</summary>
        private const string OverflowType = "exceed_context_size_error";

        /// <summary>
        /// The stable fragment of the prose message, for builds that do not send a type.
        /// Matched case-insensitively on a substring rather than parsed: the numbers around
        /// it move between builds, this phrase has not.
        /// </summary>
        private const string OverflowPhrase = "exceeds the available context size";

        /// <summary>
        /// True when <paramref name="statusCode"/> and <paramref name="body"/> describe a
        /// context-window overflow, with whatever numbers the server supplied in
        /// <paramref name="info"/>. False for every other failure, including a 400 carrying a
        /// different error type and any body that is not JSON. Never throws.
        /// </summary>
        public static bool TryParse(int statusCode, string body, out ContextOverflowInfo info)
        {
            info = null;

            // A payload too large IS this condition however the body is worded, and some
            // proxies send it with no body at all.
            bool statusAlone = statusCode == 413;

            // Only 400 and 413 can be this. A 500 carrying the same words is the server
            // failing, not the server refusing — status wins, deliberately, so a crash
            // during generation is never retried as if it were an oversized prompt.
            if (!statusAlone && statusCode != 400)
                return false;

            var parsed = new ContextOverflowInfo();
            bool matched = statusAlone;

            JObject error = TryReadError(body);
            if (error != null)
            {
                string type = (string)error["type"] ?? string.Empty;
                string message = (string)error["message"] ?? string.Empty;

                parsed.ServerMessage = message;
                parsed.PromptTokens = ReadCount(error["n_prompt_tokens"]);
                parsed.ContextSize = ReadCount(error["n_ctx"]);

                if (string.Equals(type, OverflowType, StringComparison.OrdinalIgnoreCase))
                    matched = true;
                else if (message.IndexOf(OverflowPhrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    matched = true;
            }

            if (!matched)
                return false;

            info = parsed;
            return true;
        }

        /// <summary>
        /// The <c>error</c> object from an OpenAI-shaped error body, or null when the body is
        /// empty, not JSON, or not that shape. Swallows everything: this runs on a path that
        /// is already handling a failure.
        /// </summary>
        private static JObject TryReadError(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;

            try
            {
                return JObject.Parse(body)["error"] as JObject;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// A non-negative token count from a JSON value, or 0 for anything unusable — absent,
        /// null, a string, a float, or negative.
        /// </summary>
        private static int ReadCount(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return 0;

            try
            {
                int value = token.Value<int>();
                return value > 0 ? value : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
