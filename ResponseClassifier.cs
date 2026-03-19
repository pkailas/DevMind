// File: ResponseClassifier.cs  v1.0.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;

namespace DevMind
{
    /// <summary>
    /// Classifies a raw LLM response string into a <see cref="ResponseOutcome"/>.
    /// Abstraction boundary between callers and the ResponseParser internals.
    /// </summary>
    public static class ResponseClassifier
    {
        /// <summary>
        /// Parses <paramref name="responseText"/> and returns a <see cref="ResponseOutcome"/>
        /// with pre-computed action flags.
        /// </summary>
        public static ResponseOutcome Classify(string responseText)
        {
            List<ResponseBlock> blocks = ResponseParser.Parse(responseText);
            return new ResponseOutcome(blocks);
        }

        /// <summary>
        /// Wraps an already-parsed block list in a <see cref="ResponseOutcome"/>
        /// without re-parsing. Useful in tests.
        /// </summary>
        public static ResponseOutcome Classify(List<ResponseBlock> blocks)
        {
            return new ResponseOutcome(blocks);
        }
    }
}
