// File: AgenticActionResolver.cs  v1.1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;
using System.Linq;

namespace DevMind
{
    /// <summary>
    /// Pure static resolver: takes a <see cref="ResponseOutcome"/> and execution
    /// context, returns an <see cref="AgenticAction"/> describing what to do next.
    /// </summary>
    public static class AgenticActionResolver
    {
        /// <summary>
        /// Evaluates the response outcome and previous execution result to decide
        /// the next agentic action. Rules are evaluated in priority order.
        /// </summary>
        /// <param name="outcome">Classified outcome of the latest LLM response.</param>
        /// <param name="previousResult">Result of the previous execution turn, or null on the first iteration.</param>
        /// <param name="currentDepth">Current agentic loop depth (0-based).</param>
        /// <param name="maxDepth">Maximum allowed depth (0 = loop disabled).</param>
        public static AgenticAction Resolve(
            ResponseOutcome outcome,
            ExecutionResult previousResult,
            int currentDepth,
            int maxDepth)
        {
            // ── Guard: null / empty outcome ──────────────────────────────────────
            if (outcome == null || outcome.Blocks == null || outcome.Blocks.Count == 0)
                return AgenticAction.Stop("No response.");

            // ── Rule 1: DONE signal (only when no actionable blocks present) ─────
            // If the response also contains patches/files/shell, let the executor
            // handle those first; the subsequent iteration will see a clean DONE.
            if (outcome.IsDone && !outcome.HasPatches && !outcome.HasFileCreation && !outcome.HasShellCommands)
                return AgenticAction.Stop("Task complete.");

            // ── Rule 2: READ/GREP-only response → auto-load and resubmit ────────
            if (outcome.IsReadOnly)
            {
                List<string> filesToRead = outcome.Blocks
                    .Where(b => b.Type == BlockType.ReadRequest)
                    .Select(b => b.FileName)
                    .ToList();
                // GREP blocks are executed directly by ExecuteLoadAndResubmitAsync;
                // their filenames do not need to be in FilesToRead.
                return AgenticAction.Resubmit(filesToRead);
            }

            // ── Rule 3: File creation (primary intent when FILE: blocks present) ──
            if (outcome.HasFileCreation)
                return new AgenticAction { Type = ActionType.CreateFile };

            // ── Rule 4: Patches and/or shell commands ────────────────────────────
            if (outcome.HasPatches || outcome.HasShellCommands)
                return new AgenticAction { Type = ActionType.ApplyAndBuild };

            // ── Rule 5: Previous build succeeded ─────────────────────────────────
            if (previousResult != null && previousResult.BuildSucceeded)
                return AgenticAction.Stop("Build succeeded — task complete.");

            // ── Rule 6: Previous build failed, still under depth cap ──────────────
            if (previousResult != null
                && previousResult.ShellExitCode.HasValue
                && !previousResult.BuildSucceeded
                && currentDepth < maxDepth
                && maxDepth > 0)
            {
                return AgenticAction.Continue();
            }

            // ── Edge case: all patches failed, no shell ran ───────────────────────
            if (previousResult != null
                && previousResult.PatchesFailed > 0
                && previousResult.PatchesApplied == 0
                && !previousResult.ShellExitCode.HasValue
                && currentDepth < maxDepth
                && maxDepth > 0)
            {
                return AgenticAction.Continue();
            }

            // ── Rule 7: Depth cap reached ─────────────────────────────────────────
            if (maxDepth == 0 || currentDepth >= maxDepth)
                return AgenticAction.Stop($"Depth cap reached ({maxDepth} iterations).");

            // ── Rule 8: Bare code / empty response ────────────────────────────────
            if (outcome.IsEmptyOrBareCode)
                return AgenticAction.Retry(
                    "Your response contained code but no directives. " +
                    "Use PATCH to edit files, SHELL: to run commands, " +
                    "or FILE:/END_FILE to create new files.");

            // ── Rule 9: Fallback — plain text explanation or question ─────────────
            return AgenticAction.Stop("Response complete.");
        }
    }
}
