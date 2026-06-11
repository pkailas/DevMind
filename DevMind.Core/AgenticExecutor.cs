// File: AgenticExecutor.cs  v7.6
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// Executes an <see cref="AgenticAction"/> by calling <see cref="IAgenticHost"/> methods.
    /// This is the only class in the state machine that has side effects.
    /// The executor contains no decision logic — all intelligence lives in
    /// <see cref="AgenticActionResolver"/>.
    /// </summary>
    public class AgenticExecutor
    {
        private readonly IAgenticHost _host;
        private readonly ILlmOptions _options;
        private CancellationToken _cancellationToken;

        // Repetition guard — tracks consecutive identical READ/GREP requests to break infinite loops
        private string _lastReadKey;
        private int _lastReadRepeatCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="AgenticExecutor"/> class.
        /// </summary>
        /// <param name="host">The agentic host that provides side-effect operations.</param>
        /// <param name="options">Runtime options controlling pipeline behavior.</param>
        public AgenticExecutor(IAgenticHost host, ILlmOptions options)
        {
            _host = host;
            _options = options;
        }

        /// <summary>
        /// Sets the cancellation token for the current execution cycle.
        /// Used to cancel pending diff preview cards on Stop.
        /// </summary>
        public void SetCancellationToken(CancellationToken token)
        {
            _cancellationToken = token;
        }

        /// <summary>
        /// Executes the prescribed action against the host, iterating
        /// <paramref name="outcome"/>.Blocks in order when applicable.
        /// Returns an <see cref="ExecutionResult"/> describing what happened.
        /// </summary>
        public async Task<ExecutionResult> ExecuteAsync(
            AgenticAction action,
            ResponseOutcome outcome)
        {
            if (action == null)
                return ExecutionResult.None();

            switch (action.Type)
            {
                case ActionType.ApplyAndBuild:
                    return await ExecuteBlocksAsync(outcome,
                        processFiles: true,
                        processPatches: true,
                        processShell: true);

                case ActionType.RunShell:
                    if (!string.IsNullOrEmpty(action.ShellCommand))
                    {
                        var result = new ExecutionResult();
                        try
                        {
                            var (exitCode, output) = await _host.RunShellAsync(action.ShellCommand);
                            result.ShellExitCode     = exitCode;
                            result.ShellOutput       = output ?? string.Empty;
                            result.LastShellCommand  = action.ShellCommand;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[SHELL ERROR] {ex.Message}\n", OutputColor.Error);
                        }
                        return result;
                    }
                    return await ExecuteBlocksAsync(outcome,
                        processFiles: false,
                        processPatches: false,
                        processShell: true);

                case ActionType.CreateFile:
                    return await ExecuteBlocksAsync(outcome,
                        processFiles: true,
                        processPatches: true,
                        processShell: false);

                case ActionType.Stop:
                    if (!string.IsNullOrEmpty(action.StopReason))
                        _host.AppendOutput(action.StopReason + "\n", OutputColor.Dim);
                    return ExecutionResult.None();

                default:
                    return ExecutionResult.None();
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Iterates through <paramref name="outcome"/>.Blocks in order and executes
        /// the requested block types based on the process flags. Each block is wrapped
        /// in its own try/catch so a failure in one block does not abort the rest.
        /// </summary>
        /// <param name="outcome">The response outcome containing blocks to execute.</param>
        /// <param name="processFiles">Whether to process FILE blocks.</param>
        /// <param name="processPatches">Whether to process PATCH blocks.</param>
        /// <param name="processShell">Whether to process SHELL blocks.</param>
        /// <returns>An <see cref="ExecutionResult"/> with aggregated results from all blocks.</returns>
        private async Task<ExecutionResult> ExecuteBlocksAsync(
            ResponseOutcome outcome,
            bool processFiles,
            bool processPatches,
            bool processShell)
        {
            var result = new ExecutionResult();

            if (outcome?.Blocks == null)
                return result;

            foreach (var block in outcome.Blocks)
            {
                switch (block.Type)
                {
                    case BlockType.Done:
                        // Render task_done.summary so the user sees the answer when the model
                        // packs its response into the summary parameter instead of prose tokens.
                        if (!string.IsNullOrWhiteSpace(block.Content))
                            _host.AppendOutput(block.Content.TrimEnd('\r', '\n') + "\n", OutputColor.Normal);
                        break;

                    case BlockType.Scratchpad:
                        try
                        {
                            _host.UpdateScratchpad(block.Content);
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[SCRATCHPAD ERROR] {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.File:
                        if (!processFiles) break;
                        try
                        {
                            string savedPath = await _host.SaveFileAsync(block.FileName, block.Content, block.FromToolCall);
                            if (!string.IsNullOrEmpty(savedPath))
                            {
                                result.FilesCreated.Add(savedPath);
                                _lastReadKey = null;
                                _lastReadRepeatCount = 0;
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[FILE ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.AppendFile:
                        if (!processFiles) break;
                        try
                        {
                            string appendedPath = await _host.AppendFileAsync(block.FileName, block.Content);
                            if (!string.IsNullOrEmpty(appendedPath))
                            {
                                result.FilesAppended.Add(appendedPath);
                                _lastReadKey = null;
                                _lastReadRepeatCount = 0;
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[APPEND ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.Patch:
                        // Patches are handled in a batch after the loop — skip individual processing.
                        // See batch patch handling below.
                        break;

                    case BlockType.Shell:
                        if (!processShell) break;
                        // A null command reaches here when run_build could not resolve a build
                        // command for the working directory — fail with guidance, don't crash.
                        if (string.IsNullOrWhiteSpace(block.Command))
                        {
                            const string noCommandMsg =
                                "No build command configured or detected for the working directory. " +
                                "Set DEVMIND_BUILD_COMMAND or pass --build-command, or use run_shell " +
                                "with an explicit build command.";
                            result.Errors.Add(noCommandMsg);
                            _host.AppendOutput($"[SHELL ERROR] {noCommandMsg}\n", OutputColor.Error);
                            break;
                        }
                        try
                        {
                           var (exitCode, output) = await _host.RunShellAsync(block.Command, block.ShellTimeoutSeconds);
                            result.ShellExitCode    = exitCode;
                            result.ShellOutput      = output ?? string.Empty;
                            result.LastShellCommand = block.Command;
                            _lastReadKey = null;
                            _lastReadRepeatCount = 0;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[SHELL ERROR] {block.Command}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.Grep:
                        try
                        {
                            int? grepStart = block.RangeStart > 0 ? (int?)block.RangeStart : null;
                            int? grepEnd   = block.RangeEnd   > 0 ? (int?)block.RangeEnd   : null;
                            string grepKey = $"GREP:{block.Pattern}@{block.FileName}" +
                                (grepStart.HasValue ? $":{grepStart}-{grepEnd}" : "");
                            if (string.Equals(grepKey, _lastReadKey, StringComparison.OrdinalIgnoreCase))
                                _lastReadRepeatCount++;
                            else { _lastReadKey = grepKey; _lastReadRepeatCount = 1; }
                            if (_lastReadRepeatCount >= 3)
                            {
                                _host.AppendOutput(
                                    "[READ returned same content 3 times — possible truncation or parsing issue. " +
                                    "Proceeding with available data. Use a different line range or try GREP to locate what you need.]\n",
                                    OutputColor.Dim);
                                break;
                            }
                            string grepContent = await _host.GrepFileAsync(block.Pattern, block.FileName, grepStart, grepEnd);
                            if (grepContent != null)
                                result.ToolResultContents[block.FileName] = grepContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[GREP ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.Find:
                        try
                        {
                            int? findStart = block.RangeStart > 0 ? (int?)block.RangeStart : null;
                            int? findEnd   = block.RangeEnd   > 0 ? (int?)block.RangeEnd   : null;
                            string findKey = $"FIND:{block.Pattern}@{block.GlobPattern}" +
                                (findStart.HasValue ? $":{findStart}-{findEnd}" : "");
                            if (string.Equals(findKey, _lastReadKey, StringComparison.OrdinalIgnoreCase))
                                _lastReadRepeatCount++;
                            else { _lastReadKey = findKey; _lastReadRepeatCount = 1; }
                            if (_lastReadRepeatCount >= 3)
                            {
                                _host.AppendOutput(
                                    "[FIND returned same content 3 times — possible truncation or parsing issue. " +
                                    "Proceeding with available data. Use a different glob or line range.]\n",
                                    OutputColor.Dim);
                                break;
                            }
                            string findContent = await _host.FindInFilesAsync(block.Pattern, block.GlobPattern, findStart, findEnd);
                            if (findContent != null)
                                result.ToolResultContents[block.GlobPattern] = findContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[FIND ERROR] {block.GlobPattern}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.ListFiles:
                        try
                        {
                            string listKey = $"LIST:{block.ListFilesGlob}:{block.ListFilesRecursive}";
                            if (string.Equals(listKey, _lastReadKey, StringComparison.OrdinalIgnoreCase))
                                _lastReadRepeatCount++;
                            else { _lastReadKey = listKey; _lastReadRepeatCount = 1; }
                            if (_lastReadRepeatCount >= 3)
                            {
                                _host.AppendOutput(
                                    "[LIST returned same content 3 times — possible parsing issue. " +
                                    "Proceeding with available data. Try a different glob pattern.]\n",
                                    OutputColor.Dim);
                                break;
                            }
                            string listContent = await _host.ListFilesAsync(
                                block.ListFilesGlob, block.ListFilesRecursive, _cancellationToken);
                            if (listContent != null)
                                result.ToolResultContents[block.ListFilesGlob ?? ""] = listContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[LIST ERROR] {block.ListFilesGlob}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.Delete:
                        try
                        {
                            string deleteResult = await _host.DeleteFileAsync(block.FileName);
                            bool deleted = deleteResult != null && deleteResult.StartsWith("Deleted:", StringComparison.Ordinal);
                            _host.AppendOutput($"[DELETE] {deleteResult}\n",
                                deleted ? OutputColor.Success : OutputColor.Error);
                            if (deleted)
                            {
                                string deletedPath = deleteResult.Substring("Deleted:".Length).Trim();
                                result.FilesDeleted.Add(deletedPath);
                            }
                            else if (!string.IsNullOrEmpty(deleteResult))
                            {
                                result.Errors.Add(deleteResult);
                            }
                            _lastReadKey = null;
                            _lastReadRepeatCount = 0;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[DELETE ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.Rename:
                        try
                        {
                            string renameResult = await _host.RenameFileAsync(block.RenameFrom, block.RenameTo);
                            bool renamed = renameResult != null && renameResult.StartsWith("Renamed:", StringComparison.Ordinal);
                            _host.AppendOutput($"[RENAME] {renameResult}\n",
                                renamed ? OutputColor.Success : OutputColor.Error);
                            if (renamed)
                            {
                                result.FilesRenamed.Add(renameResult.Substring("Renamed:".Length).Trim());
                            }
                            else if (!string.IsNullOrEmpty(renameResult))
                            {
                                result.Errors.Add(renameResult);
                            }
                            _lastReadKey = null;
                            _lastReadRepeatCount = 0;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[RENAME ERROR] {block.RenameFrom}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.Diff:
                        try
                        {
                            string diffKey = $"DIFF:{block.FileName}";
                            if (string.Equals(diffKey, _lastReadKey, StringComparison.OrdinalIgnoreCase))
                                _lastReadRepeatCount++;
                            else { _lastReadKey = diffKey; _lastReadRepeatCount = 1; }
                            if (_lastReadRepeatCount >= 3)
                            {
                                _host.AppendOutput(
                                    "[DIFF returned same content 3 times — skipping.]\n",
                                    OutputColor.Dim);
                                break;
                            }
                            string diffContent = await _host.GetFileDiffAsync(block.FileName);
                            if (diffContent != null)
                                result.ToolResultContents[block.FileName] = diffContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[DIFF ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.Test:
                        try
                        {
                           string testSummary = await _host.RunTestsAsync(block.TestProject, block.TestFilter, block.TestTimeoutSeconds);
                            bool allPassed = testSummary != null
                                && !testSummary.Contains("FAILED:")
                                && !testSummary.Contains("failed,");
                            _host.AppendOutput(testSummary + "\n",
                                allPassed ? OutputColor.Success : OutputColor.Error);
                            // Inject results into shell context so the agentic loop sees them
                            result.ShellOutput      = testSummary ?? string.Empty;
                            result.ShellExitCode    = allPassed ? 0 : 1;
                            result.LastShellCommand = $"TEST {block.TestProject}";
                            _lastReadKey = null;
                            _lastReadRepeatCount = 0;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[TEST ERROR] {block.TestProject}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.ReadRequest:
                        try
                        {
                            string readKey = block.RangeStart > 0
                                ? $"{block.FileName}:{block.RangeStart}-{block.RangeEnd}"
                                : block.FileName;
                            if (string.Equals(readKey, _lastReadKey, StringComparison.OrdinalIgnoreCase))
                                _lastReadRepeatCount++;
                            else { _lastReadKey = readKey; _lastReadRepeatCount = 1; }
                            if (_lastReadRepeatCount >= 3)
                            {
                                _host.AppendOutput(
                                    "[READ returned same content 3 times — possible truncation or parsing issue. " +
                                    "Proceeding with available data. Use a different line range or try GREP to locate what you need.]\n",
                                    OutputColor.Dim);
                                break;
                            }
                            string readContent = await _host.LoadFileContentAsync(
                                block.FileName, block.RangeStart, block.RangeEnd,
                                block.ForceFullRead);
                            if (readContent != null)
                                result.ToolResultContents[block.FileName] = readContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[READ ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.RecallMemory:
                        try
                        {
                            string recallResult = await _host.RecallMemoryAsync(block.MemoryTopic);
                            block.MemoryContent = recallResult; // Store for tool result injection
                        }
                        catch (Exception ex)
                        {
                            block.MemoryContent = $"Error recalling memory: {ex.Message}";
                            _host.AppendOutput($"[MEMORY ERROR] {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.SaveMemory:
                        try
                        {
                            string saveResult = await _host.SaveMemoryAsync(
                                block.MemoryTopic, block.MemoryContent, block.MemoryDescription);
                            block.MemoryDescription = saveResult; // Reuse field for result message
                        }
                        catch (Exception ex)
                        {
                            _host.AppendOutput($"[MEMORY ERROR] {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.ListMemory:
                        try
                        {
                            string listResult = await _host.ListMemoryTopicsAsync();
                            block.MemoryContent = listResult; // Store for tool result injection
                        }
                        catch (Exception ex)
                        {
                            block.MemoryContent = $"Error listing memory: {ex.Message}";
                            _host.AppendOutput($"[MEMORY ERROR] {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.GetDiagnostics:
                        try
                        {
                            string diagContent = await _host.GetDiagnosticsAsync(block.FileName);
                            if (diagContent != null)
                                result.ToolResultContents[block.FileName ?? ""] = diagContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[LSP ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.GoToDefinition:
                        try
                        {
                            string defContent = await _host.GoToDefinitionAsync(
                                block.FileName, block.LspLine, block.LspCharacter);
                            if (defContent != null)
                                result.ToolResultContents[block.FileName ?? ""] = defContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[LSP ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.FindReferences:
                        try
                        {
                            string refsContent = await _host.FindReferencesAsync(
                                block.FileName, block.LspLine, block.LspCharacter);
                            if (refsContent != null)
                                result.ToolResultContents[block.FileName ?? ""] = refsContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[LSP ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.Hover:
                        try
                        {
                            string hoverContent = await _host.HoverAsync(
                                block.FileName, block.LspLine, block.LspCharacter);
                            if (hoverContent != null)
                                result.ToolResultContents[block.FileName ?? ""] = hoverContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[LSP ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.FindSymbol:
                        try
                        {
                            string symbolContent = await _host.FindSymbolAsync(
                                block.Pattern, block.MaxResults, block.Language);
                            if (symbolContent != null)
                                result.ToolResultContents[block.Pattern ?? ""] = symbolContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[LSP ERROR] find_symbol \"{block.Pattern}\": {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.WebSearch:
                        try
                        {
                            int? searchCap = block.MaxResults > 0 ? (int?)block.MaxResults : null;
                            string searchContent = await _host.WebSearchAsync(block.Pattern, searchCap);
                            if (searchContent != null)
                                result.ToolResultContents[block.Pattern ?? ""] = searchContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[WEB ERROR] search \"{block.Pattern}\": {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.WebFetch:
                        try
                        {
                            string fetchContent = await _host.WebFetchAsync(block.Url);
                            if (fetchContent != null)
                                result.ToolResultContents[block.Url ?? ""] = fetchContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[WEB ERROR] fetch {block.Url}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    // Text, Done — already handled during streaming or by resolver
                    default:
                        break;
                }
            }

            // ── Batch PATCH processing with diff preview gate ──────────────────
            if (processPatches)
            {
                var patchBlocks = outcome.Blocks
                    .Where(b => b.Type == BlockType.Patch)
                    .ToList();

                if (patchBlocks.Count > 0)
                {
                    await ExecuteBatchPatchesAsync(patchBlocks, result);
                }
            }

            return result;
        }

        /// <summary>
        /// Resolves all PATCH blocks, determines whether diff preview cards are
        /// needed, shows them if so, and applies approved patches.
        /// </summary>
        private async Task ExecuteBatchPatchesAsync(
            List<ResponseBlock> patchBlocks,
            ExecutionResult result)
        {
            bool alwaysConfirm = _options.AlwaysConfirmPatch;

            // Phase 1: Resolve all patches (parse + match, no side effects)
            var resolved = new List<PatchResolveResult>();
            var resolveIndices = new List<int>(); // maps resolved[] index to patchBlocks[] index

            for (int i = 0; i < patchBlocks.Count; i++)
            {
                var block = patchBlocks[i];
                try
                {
                    var resolveResult = await _host.ResolvePatchAsync(block.Content, block.FromToolCall);
                    if (resolveResult != null)
                    {
                        resolved.Add(resolveResult);
                        resolveIndices.Add(i);
                    }
                    else
                    {
                        result.PatchesFailed++;
                        string failedFile = string.IsNullOrEmpty(block.FileName)
                            ? "unknown" : System.IO.Path.GetFileName(block.FileName);
                        result.Errors.Add(
                            $"[PATCH-FAILED:{failedFile}] FIND text not found — file was NOT modified. " +
                            "READ the file to get exact current content, then retry PATCH with correct FIND text.");
                    }
                }
                catch (Exception ex)
                {
                    result.PatchesFailed++;
                    result.Errors.Add(ex.Message);
                    _host.AppendOutput($"[PATCH ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                }
            }

            if (resolved.Count == 0) return;

            // Phase 2: Auto-apply exact matches, collect fuzzy/confirm patches for preview
            var needPreview = new List<PatchResolveResult>();
            var needPreviewIndices = new List<int>(); // index into resolved[]

            for (int i = 0; i < resolved.Count; i++)
            {
                var r = resolved[i];
                if (!alwaysConfirm && r.Confidence == PatchConfidence.Exact)
                {
                    // Auto-apply immediately — no card, no await
                    try
                    {
                        string patchedPath = await _host.ApplyResolvedPatchAsync(r);
                        if (patchedPath != null)
                        {
                            result.PatchesApplied++;
                            result.PatchedPaths.Add(patchedPath);
                            _lastReadKey = null;
                            _lastReadRepeatCount = 0;
                        }
                        else
                        {
                            result.PatchesFailed++;
                            string failedFile = string.IsNullOrEmpty(r.FileName)
                                ? "unknown" : System.IO.Path.GetFileName(r.FileName);
                            result.Errors.Add(
                                $"[PATCH-FAILED:{failedFile}] FIND text not found — file was NOT modified. " +
                                "READ the file to get exact current content, then retry PATCH with correct FIND text.");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.PatchesFailed++;
                        result.Errors.Add(ex.Message);
                        _host.AppendOutput($"[PATCH ERROR] {r.FileName}: {ex.Message}\n", OutputColor.Error);
                    }
                }
                else
                {
                    // Needs user confirmation — queue for preview
                    needPreview.Add(r);
                    needPreviewIndices.Add(i);
                }
            }

            // Phase 3: Show preview cards only for patches that need confirmation
            if (needPreview.Count > 0)
            {
                List<int> approvedIndices;
                try
                {
                    approvedIndices = await _host.ShowDiffPreviewAsync(needPreview, _cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    foreach (var r in needPreview)
                    {
                        result.PatchesFailed++;
                        result.Errors.Add(
                            $"[PATCH-SKIPPED: {r.FileName} \u2014 user cancelled. Re-READ the file if you need to try again.]");
                    }
                    return;
                }

                // Apply approved, inject SKIPPED for rejected
                for (int i = 0; i < needPreview.Count; i++)
                {
                    if (approvedIndices.Contains(i))
                    {
                        try
                        {
                            string patchedPath = await _host.ApplyResolvedPatchAsync(needPreview[i]);
                            if (patchedPath != null)
                            {
                                result.PatchesApplied++;
                                result.PatchedPaths.Add(patchedPath);
                                _lastReadKey = null;
                                _lastReadRepeatCount = 0;
                            }
                            else
                            {
                                result.PatchesFailed++;
                                result.Errors.Add($"[PATCH-FAILED:{needPreview[i].FileName}] Apply failed after approval.");
                            }
                        }
                        catch (Exception ex)
                        {
                            result.PatchesFailed++;
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[PATCH ERROR] {needPreview[i].FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                    }
                    else
                    {
                        result.PatchesFailed++;
                        result.Errors.Add(
                            $"[PATCH-SKIPPED: {needPreview[i].FileName} \u2014 user rejected this change. Re-READ the file if you need to try again.]");
                        _host.AppendOutput(
                            $"[PATCH] Skipped {needPreview[i].FileName} \u2014 user rejected.\n",
                            OutputColor.Dim);
                    }
                }
            }
        }

    }
}
