// File: ResponseOutcome.cs  v1.8.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;

namespace DevMind
{
    /// <summary>
    /// Wraps the List&lt;ResponseBlock&gt; returned by ResponseParser.Parse() and
    /// adds pre-computed boolean properties so classifiers and resolvers do not
    /// need to re-scan the block list.
    /// </summary>
    public class ResponseOutcome
    {
        public List<ResponseBlock> Blocks { get; }

        public bool HasPatches         { get; }
        public bool HasShellCommands   { get; }
        public bool HasFileCreation    { get; }
        public bool HasReadRequests    { get; }
        public bool HasGrepRequests    { get; }
        public bool HasFindRequests    { get; }
        public bool HasDeleteRequests  { get; }
        public bool HasRenameRequests  { get; }
        public bool HasDiffRequests    { get; }
        public bool HasTestRequests    { get; }
        public bool HasScratchpad      { get; }
        public bool IsDone             { get; }

        /// <summary>
        /// True when Blocks is empty, or contains only Text blocks with no
        /// actionable directives (covers bare code fences the model forgot to
        /// wrap in PATCH).
        /// </summary>
        public bool IsEmptyOrBareCode { get; }

        /// <summary>
        /// True when the response contains READ/GREP/FIND/DIFF requests and
        /// no mutating actions — used to trigger auto-READ resubmission.
        /// DONE does not suppress this: a response with READ + DONE will still
        /// load the file first (the caller checks IsDone after loading).
        /// </summary>
        public bool IsReadOnly { get; }

        /// <summary>
        /// True when the response contains any directive from the set:
        /// READ, SHELL, FILE, FIND, GREP, DELETE, RENAME, DIFF, TEST, PATCH.
        /// Excludes DONE and SCRATCHPAD — those are control signals, not actions.
        /// </summary>
        public bool HasAnyDirective { get; }

        public ResponseOutcome(List<ResponseBlock> blocks)
        {
            Blocks = blocks ?? new List<ResponseBlock>();

            HasPatches        = Blocks.Any(b => b.Type == BlockType.Patch);
            HasShellCommands  = Blocks.Any(b => b.Type == BlockType.Shell);
            HasFileCreation   = Blocks.Any(b => b.Type == BlockType.File);
            HasReadRequests   = Blocks.Any(b => b.Type == BlockType.ReadRequest);
            HasGrepRequests   = Blocks.Any(b => b.Type == BlockType.Grep);
            HasFindRequests   = Blocks.Any(b => b.Type == BlockType.Find);
            HasDeleteRequests = Blocks.Any(b => b.Type == BlockType.Delete);
            HasRenameRequests = Blocks.Any(b => b.Type == BlockType.Rename);
            HasDiffRequests   = Blocks.Any(b => b.Type == BlockType.Diff);
            HasTestRequests   = Blocks.Any(b => b.Type == BlockType.Test);
            HasScratchpad     = Blocks.Any(b => b.Type == BlockType.Scratchpad);
            IsDone            = Blocks.Any(b => b.Type == BlockType.Done);

            bool hasMutatingAction = HasPatches || HasShellCommands || HasFileCreation || HasDeleteRequests || HasRenameRequests || HasTestRequests;
            bool hasInfoGather    = HasReadRequests || HasGrepRequests || HasFindRequests || HasDiffRequests;

            HasAnyDirective   = hasMutatingAction || hasInfoGather;
            IsEmptyOrBareCode = !HasAnyDirective && !IsDone && ContainsBareCodeBlock(Blocks);
            IsReadOnly        = hasInfoGather && !hasMutatingAction;
        }

        /// <summary>
        /// Returns true only when the text blocks contain a fenced code block
        /// (``` delimiters) and no meaningful prose outside the fences.
        /// A response with prose text (greetings, bullet lists, explanations)
        /// that happens to include inline code or code fences is NOT bare code.
        /// </summary>
        private static bool ContainsBareCodeBlock(List<ResponseBlock> blocks)
        {
            // Gather all text content from Text blocks.
            string allText = string.Join("\n",
                blocks.Where(b => b.Type == BlockType.Text)
                      .Select(b => b.Content ?? string.Empty));

            if (string.IsNullOrWhiteSpace(allText))
                return false;

            // Must contain at least one opening ``` fence to be considered code.
            string[] lines = allText.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            bool hasFence = false;
            int proseChars = 0;
            bool insideFence = false;

            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("```"))
                {
                    hasFence = true;
                    insideFence = !insideFence;
                    continue;
                }

                // Count non-whitespace characters outside code fences as prose.
                if (!insideFence)
                    proseChars += trimmed.Length;
            }

            // Only flag as bare code when there IS a code fence AND the prose
            // outside the fence is negligible (< 20 chars, allowing for minor
            // artifacts like a trailing newline).
            return hasFence && proseChars < 20;
        }

        /// <summary>
        /// Returns an instance with an empty block list (nothing parsed).
        /// </summary>
        public static ResponseOutcome Empty()
        {
            return new ResponseOutcome(new List<ResponseBlock>());
        }
    }
}
