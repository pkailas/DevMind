// File: AgenticExecutor.cs  v1.3.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Linq;
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

        // Repetition guard — tracks consecutive identical READ/GREP requests to break infinite loops
        private string _lastReadKey;
        private int _lastReadRepeatCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="AgenticExecutor"/> class.
        /// </summary>
        /// <param name="host">The agentic host that provides side-effect operations.</param>
        public AgenticExecutor(IAgenticHost host)
        {
            _host = host;
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

                case ActionType.LoadAndResubmit:
                    return await ExecuteLoadAndResubmitAsync(action, outcome);

                case ActionType.RetryWithCorrection:
                    if (!string.IsNullOrEmpty(action.CorrectionPrompt))
                        _host.AppendOutput(action.CorrectionPrompt + "\n", OutputColor.Dim);
                    return ExecutionResult.None();

                case ActionType.ContinueAgentic:
                    return ExecutionResult.None();

                case ActionType.Stop:
                    if (!string.IsNullOrEmpty(action.StopReason))
                        _host.AppendOutput(action.StopReason + "\n", OutputColor.Dim);
                    return ExecutionResult.None();

                case ActionType.AskUser:
                    await _host.ShowConfirmationAsync(action.StopReason ?? string.Empty);
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
                            string savedPath = await _host.SaveFileAsync(block.FileName, block.Content);
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

                    case BlockType.Patch:
                        if (!processPatches) break;
                        try
                        {
                            string patchedPath = await _host.ApplyPatchAsync(block.Content);
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
                        break;

                    case BlockType.Shell:
                        if (!processShell) break;
                        try
                        {
                            var (exitCode, output) = await _host.RunShellAsync(block.Command);
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
                            // GrepFileAsync injects results into _readContext and emits status as side effects
                            await _host.GrepFileAsync(block.Pattern, block.FileName, grepStart, grepEnd);
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
                            // FindInFilesAsync injects results into _readContext and emits status as side effects
                            await _host.FindInFilesAsync(block.Pattern, block.GlobPattern, findStart, findEnd);
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[FIND ERROR] {block.GlobPattern}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    // Text, ReadRequest, Done — already handled during streaming or by resolver
                    default:
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Loads files specified in <paramref name="action"/>.FilesToRead and runs
        /// any GREP operations from <paramref name="outcome"/>.Blocks. After loading,
        /// signals the main loop to resubmit the request with the newly loaded content.
        /// </summary>
        /// <param name="action">The agentic action containing files to read.</param>
        /// <param name="outcome">The response outcome that may contain GREP blocks.</param>
        /// <returns>An <see cref="ExecutionResult"/> with None status to trigger resubmission.</returns>
        private async Task<ExecutionResult> ExecuteLoadAndResubmitAsync(
            AgenticAction action,
            ResponseOutcome outcome)
        {
            foreach (string fileName in action.FilesToRead)
            {
                // Find the matching ReadRequest block to get range/force parameters
                var readBlock = outcome?.Blocks?.FirstOrDefault(
                    b => b.Type == BlockType.ReadRequest
                      && string.Equals(b.FileName, fileName, StringComparison.OrdinalIgnoreCase));

                int rangeStart    = readBlock?.RangeStart    ?? 0;
                int rangeEnd      = readBlock?.RangeEnd      ?? 0;
                bool forceFullRead = readBlock?.ForceFullRead ?? false;

                string readKey = rangeStart > 0
                    ? $"{fileName}:{rangeStart}-{rangeEnd}"
                    : fileName;
                if (string.Equals(readKey, _lastReadKey, StringComparison.OrdinalIgnoreCase))
                    _lastReadRepeatCount++;
                else { _lastReadKey = readKey; _lastReadRepeatCount = 1; }
                if (_lastReadRepeatCount >= 3)
                {
                    _host.AppendOutput(
                        "[READ returned same content 3 times — possible truncation or parsing issue. " +
                        "Proceeding with available data. Use a different line range or try GREP to locate what you need.]\n",
                        OutputColor.Dim);
                    continue;
                }

                try
                {
                    await _host.LoadFileContentAsync(fileName, rangeStart, rangeEnd, forceFullRead);
                }
                catch (Exception ex)
                {
                    _host.AppendOutput($"[READ ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                }
            }

            // Execute any GREP blocks — results are injected into context as a side effect
            if (outcome?.Blocks != null)
            {
                foreach (var block in outcome.Blocks)
                {
                    if (block.Type != BlockType.Grep) continue;
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
                            continue;
                        }
                        // GrepFileAsync injects results into _readContext and emits status as side effects
                        await _host.GrepFileAsync(block.Pattern, block.FileName, grepStart, grepEnd);
                    }
                    catch (Exception ex)
                    {
                        _host.AppendOutput($"[GREP ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                    }
                }
            }

            // Execute any FIND blocks — results are injected into context as a side effect
            if (outcome?.Blocks != null)
            {
                foreach (var block in outcome.Blocks)
                {
                    if (block.Type != BlockType.Find) continue;
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
                            continue;
                        }
                        // FindInFilesAsync injects results into _readContext and emits status as side effects
                        await _host.FindInFilesAsync(block.Pattern, block.GlobPattern, findStart, findEnd);
                    }
                    catch (Exception ex)
                    {
                        _host.AppendOutput($"[FIND ERROR] {block.GlobPattern}: {ex.Message}\n", OutputColor.Error);
                    }
                }
            }

            _host.AppendOutput("[AUTO-READ] File(s) loaded — resubmitting...\n", OutputColor.Dim);

            // The main loop handles resubmission; we just signal nothing was executed
            return ExecutionResult.None();
        }
    }
}
