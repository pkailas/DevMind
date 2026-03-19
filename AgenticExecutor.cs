// File: AgenticExecutor.cs  v1.0.0
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
                            result.ShellExitCode = exitCode;
                            result.ShellOutput   = output ?? string.Empty;
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
        /// Iterates outcome.Blocks in order and executes the requested block types.
        /// Each block is wrapped in its own try/catch so a failure in one block
        /// does not abort the rest.
        /// </summary>
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
                                result.FilesCreated.Add(savedPath);
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
                            bool applied = await _host.ApplyPatchAsync(block.Content);
                            if (applied)
                                result.PatchesApplied++;
                            else
                                result.PatchesFailed++;
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
                            result.ShellExitCode = exitCode;
                            result.ShellOutput   = output ?? string.Empty;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[SHELL ERROR] {block.Command}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    // Text, ReadRequest, Done — already handled during streaming or by resolver
                    default:
                        break;
                }
            }

            return result;
        }

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

                try
                {
                    await _host.LoadFileContentAsync(fileName, rangeStart, rangeEnd, forceFullRead);
                }
                catch (Exception ex)
                {
                    _host.AppendOutput($"[READ ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                }
            }

            _host.AppendOutput("[AUTO-READ] File(s) loaded — resubmitting...\n", OutputColor.Dim);

            // The main loop handles resubmission; we just signal nothing was executed
            return ExecutionResult.None();
        }
    }
}
