// File: PatchConfidence.cs  v1.0.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;

namespace DevMind
{
    /// <summary>
    /// Indicates how a PATCH FIND block was matched against the file content.
    /// </summary>
    public enum PatchConfidence
    {
        /// <summary>Whitespace-normalized exact match.</summary>
        Exact,
        /// <summary>Fuzzy Levenshtein match (≥85% similarity).</summary>
        Fuzzy
    }

    /// <summary>
    /// Result of resolving a PATCH — carries the resolved blocks, confidence,
    /// and file metadata needed by the diff preview gate.
    /// </summary>
    public class PatchResolveResult
    {
        public string FullPath { get; set; }
        public string FileName { get; set; }
        public string OriginalContent { get; set; }
        public System.Text.Encoding FileEncoding { get; set; }
        public PatchConfidence Confidence { get; set; }
        public List<(int origStart, int origEnd, string replaceText)> ResolvedBlocks { get; set; }
        public List<(string findText, string replaceText)> ParsedPairs { get; set; }

        public PatchResolveResult()
        {
            ResolvedBlocks = new List<(int, int, string)>();
            ParsedPairs = new List<(string, string)>();
        }
    }
}
