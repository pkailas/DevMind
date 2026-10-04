// File: ConflictBlock.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.

namespace DevMind
{
    /// <summary>
    /// Represents one conflict region detected during a three-way merge.
    /// </summary>
    public sealed class ConflictBlock
    {
        /// <summary>1-based line number where the conflict starts.</summary>
        public int LineNumber { get; set; }

        /// <summary>Text from the common ancestor (base) at this region.</summary>
        public string BaseText { get; set; } = string.Empty;

        /// <summary>Text proposed by the LLM (our side) at this region.</summary>
        public string ProposedText { get; set; } = string.Empty;

        /// <summary>Text currently on disk (their side) at this region.</summary>
        public string CurrentText { get; set; } = string.Empty;

        /// <summary>1-based line where the region starts in the base (what was read).</summary>
        public int BaseLine { get; set; }

        /// <summary>1-based line where the region starts in the proposed text.</summary>
        public int ProposedLine { get; set; }

        /// <summary>1-based line where the region starts in the file on disk now.</summary>
        public int CurrentLine { get; set; }
    }
}
