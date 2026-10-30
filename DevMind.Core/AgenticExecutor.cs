// File: AgenticExecutor.cs  v7.8
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

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

        /// <summary>Invoked after each tool block has executed (headless stall watchdog,
        /// H-37). Null = no-op.</summary>
        public Action<BlockType> BlockExecuted { get; set; }

        /// <summary>
        /// Runs mcp__server__tool calls (BlockType.McpCall). Null — the default — means no MCP
        /// client is attached, and an MCP call reports that as an error. Set by LoopDriver from
        /// <see cref="LoopDriver.McpTools"/>; deliberately not an IAgenticHost method, since MCP
        /// calls are host-agnostic.
        /// </summary>
        public IMcpToolInvoker McpTools { get; set; }

        // Repetition guard — tracks consecutive identical READ/GREP requests to break infinite loops
        private string _lastReadKey;

        // The reason line for approval dialogs in the batch being executed (see ApprovalReasonFor):
        // its label ("Why" for visible prose, "Why (thinking)" for the reasoning fallback) and text.
        private string _approvalReason;
        private string _approvalReasonLabel = ProseReasonLabel;
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

            _approvalReason = null;
            _approvalReasonLabel = ProseReasonLabel;



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
                        // No tool call produced this action (nothing in Core builds a
                        // RunShell action with a command; only tests do), so there is no tool
                        // name to show — say what it is rather than borrow run_shell's.
                        if (!await ApproveMutationAsync(MutationKind.Shell,
                                $"Run shell: {DescribeCommand(action.ShellCommand)}", result,
                                ApprovalDetail("shell (direct action, no tool call)", action.ShellCommand)))
                        {
                            result.ShellExitCode     = 1;
                            result.ShellOutput       = ShellRefusalOutput(result);
                            result.LastShellCommand  = action.ShellCommand;
                            return result;
                        }
                        try
                        {
                            var (exitCode, output) = await _host.RunShellAsync(action.ShellCommand);
                            result.ShellExitCode     = exitCode;
                            result.ShellOutput       = WithBuildHints(output);
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

            // One reason per response: every gated call in it shares the prose the model wrote
            // (or, when it wrote none, the end of its reasoning).
            (_approvalReasonLabel, _approvalReason) = ApprovalReasonFor(outcome);

            // Patches run in call order (H-55 follow-up). They used to be applied as one batch
            // after EVERY other block, so patch_file then read_file of the same file in one
            // turn read the pre-patch text, and patch_file then run_build built unpatched
            // code. Consecutive patch calls still form one batch (one diff-preview card); the
            // batch is applied before the next non-patch call runs.
            var pendingPatches = new List<ResponseBlock>();

            foreach (var block in outcome.Blocks)
            {
                // H-56: the mapper rewrote this call's arguments (patch_file alias keys).
                // One entry per call, so the journal shows how often it fires.
                if (block.ArgumentNote != null && (processPatches || block.Type != BlockType.Patch)
                    && _host is IActionJournal journal)
                    journal.RecordAction("tool_args", block.ArgumentNote, success: true);

                if (block.Type == BlockType.Patch)
                {
                    if (processPatches) pendingPatches.Add(block);
                    continue;
                }
                if (block.Type != BlockType.Text && pendingPatches.Count > 0)
                    await FlushPatchesAsync(pendingPatches, result);

                // Write tools report into turn-wide lists (FilesCreated, Errors, ...), so a
                // failed create_file after a successful one was told "[File created: <the
                // other file>]". Snapshot the lists and give the call only what it added.
                WriteCallScope writeScope = WriteCallScope.Begin(block, result);

                switch (block.Type)
                {
                    case BlockType.Done:
                        // Render task_done.summary so the user sees the answer when the model
                        // packs its response into the summary parameter instead of prose tokens.
                        if (!string.IsNullOrWhiteSpace(block.Content))
                        {
                            // A skin may collapse a summary that repeats streamed prose, but
                            // only once it KNOWS this is a task_done — so the kind is set first.
                            _host.SetAnswerKind(AnswerKind.TaskDone);
                            _host.AppendAnswer(block.Content.TrimEnd('\r', '\n') + "\n");
                        }
                        break;

                    case BlockType.NeedsInput:
                        // Render ask_caller's questions so they land in the transcript/UI.
                        if (!string.IsNullOrWhiteSpace(block.Content))
                        {
                            // Questions are NEVER collapsed — a person is waiting to answer them.
                            _host.SetAnswerKind(AnswerKind.AskCaller);
                            _host.AppendAnswer(block.Content.TrimEnd('\r', '\n') + "\n");
                        }
                        break;

                    case BlockType.Scratchpad:
                        try
                        {
                            _host.UpdateScratchpad(block.Content);
                            // A successful update used to leave no trace at all — the only
                            // output was the catch below — so the job transcript could not show
                            // whether the scratchpad was ever used. Length, not content: the
                            // content is state the model re-reads, not something to replay.
                            int len = block.Content?.Trim().Length ?? 0;
                            _host.AppendOutput(len == 0
                                ? "[SCRATCHPAD] cleared\n"
                                : $"[SCRATCHPAD] updated ({len} chars)\n", OutputColor.Dim);
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[SCRATCHPAD ERROR] {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.File:
                        if (!processFiles) break;
                        if (MissingFileName(block, "create_file", result)) break;
                        string createPrompt = $"Create file {block.FileName} ({block.Content?.Length ?? 0:N0} chars)?";
                        if (!await ApproveMutationAsync(MutationKind.CreateFile, createPrompt, result,
                                ApprovalDetail(block.ToolName ?? "create_file", createPrompt)))
                            break;
                        try
                        {
                            string savedPath = await _host.SaveFileAsync(block.FileName, block.Content, block.FromToolCall);
                            if (!string.IsNullOrEmpty(savedPath))
                            {
                                result.FilesCreated.Add(savedPath);
                                // A created file is all new: every line is a changed line.
                                RecordLint(result, savedPath, block.Content, null);
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
                        if (MissingFileName(block, "append_file", result)) break;
                        string appendPrompt = $"Append to {block.FileName} ({block.Content?.Length ?? 0:N0} chars)?";
                        if (!await ApproveMutationAsync(MutationKind.AppendFile, appendPrompt, result,
                                ApprovalDetail(block.ToolName ?? "append_file", appendPrompt)))
                            break;
                        try
                        {
                            string appendedPath = await _host.AppendFileAsync(block.FileName, block.Content);
                            if (!string.IsNullOrEmpty(appendedPath))
                            {
                                result.FilesAppended.Add(appendedPath);
                                RecordAppendLint(result, appendedPath, block.Content);
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
                        // Never reached: patches are queued above and applied in batches
                        // (FlushPatchesAsync) in call order.
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
                        // run_shell and run_build both map to a Shell block; ToolName (set by
                        // ToolCallMapper) says which one the model actually called.
                        string shellVerb = block.ToolName == "run_build" ? "Run build" : "Run shell";
                        if (!await ApproveMutationAsync(MutationKind.Shell,
                                $"{shellVerb}: {DescribeCommand(block.Command)}", result,
                                ApprovalDetail(block.ToolName ?? "run_shell", block.Command)))
                        {
                            // The model reads ShellOutput verbatim (LoopHelpers' run_shell
                            // case returns it when non-empty), so the decline reaches it as
                            // the command's own output rather than as silence it would read
                            // as success. A non-zero code keeps every "did it work?" check
                            // on the failure branch.
                            result.ShellExitCode    = 1;
                            result.ShellOutput      = ShellRefusalOutput(result);
                            result.LastShellCommand = block.Command;
                            break;
                        }
                        try
                        {
                           var (exitCode, output) = await _host.RunShellAsync(block.Command, block.ShellTimeoutSeconds, block.ShellDetach);
                            result.ShellExitCode    = exitCode;
                            result.ShellOutput      = WithBuildHints(output);
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
                        if (MissingFileName(block, "grep_file", result)) break;
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
                            {
                                result.ToolResultContents[block.FileName] = grepContent;
                                RecordCallResult(result, block, grepContent);
                            }
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
                            {
                                result.ToolResultContents[block.GlobPattern ?? ""] = findContent;
                                RecordCallResult(result, block, findContent);
                            }
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
                            {
                                result.ToolResultContents[block.ListFilesGlob ?? ""] = listContent;
                                RecordCallResult(result, block, listContent);
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[LIST ERROR] {block.ListFilesGlob}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.Delete:
                        if (MissingFileName(block, "delete_file", result)) break;
                        string deletePrompt = $"Delete file {block.FileName}?";
                        if (!await ApproveMutationAsync(MutationKind.DeleteFile, deletePrompt, result,
                                ApprovalDetail(block.ToolName ?? "delete_file", deletePrompt)))
                            break;
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
                        string renamePrompt = $"Rename {block.RenameFrom} to {block.RenameTo}?";
                        if (!await ApproveMutationAsync(MutationKind.RenameFile, renamePrompt, result,
                                ApprovalDetail(block.ToolName ?? "rename_file", renamePrompt)))
                            break;
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
                        if (MissingFileName(block, "diff_file", result)) break;
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
                            {
                                result.ToolResultContents[block.FileName] = diffContent;
                                RecordCallResult(result, block, diffContent);
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[DIFF ERROR] {block.FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.Test:
                        // run_tests executes code (the test assemblies), so it is gated
                        // exactly like run_shell / run_build: Auto runs, Manual asks, Plan
                        // refuses. The harness's own verify_tests run is NOT a model tool
                        // call and never comes through here.
                        string testsWhat = DescribeTests(block);
                        if (!await ApproveMutationAsync(MutationKind.Shell,
                                $"Run tests: {testsWhat}", result,
                                ApprovalDetail(block.ToolName ?? "run_tests", TestsDetail(block))))
                        {
                            // LoopHelpers' run_tests case returns ShellOutput verbatim, so the
                            // refusal reaches the model as the run's own result, never as
                            // "[Tests executed]" or an empty pass.
                            result.ShellExitCode    = 1;
                            result.ShellOutput      = ShellRefusalOutput(result, tests: true);
                            result.LastShellCommand = $"TEST {block.TestProject}";
                            break;
                        }
                        try
                        {
                           string testSummary = await _host.RunTestsAsync(block.TestProject, block.TestFilter, block.TestTimeoutSeconds);
                            bool allPassed = testSummary != null
                                && !testSummary.Contains("FAILED:")
                                && !testSummary.Contains("failed,");
                            _host.AppendOutput(testSummary + "\n",
                                allPassed ? OutputColor.Success : OutputColor.Error);
                            // Inject results into shell context so the agentic loop sees them
                            result.ShellOutput      = WithBuildHints(testSummary);
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
                        if (MissingFileName(block, "read_file", result)) break;
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
                            {
                                result.ToolResultContents[block.FileName] = readContent;
                                RecordCallResult(result, block, readContent);
                            }
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
                        // save_memory writes .devmind/memory into the repo: a mutation. In Plan
                        // the refusal goes into the tool result the model reads (MemoryContent),
                        // the same path every other refusal uses.
                        if (ApprovalPolicy.Decide(_options.ApprovalMode, MutationKind.Memory)
                            == ApprovalDecision.Refuse)
                        {
                            string refusal = ApprovalPolicy.PlanRefusalMessage("save_memory");
                            block.MemoryDescription = refusal;
                            result.Errors.Add(refusal);
                            _host.AppendOutput($"[PLAN MODE] Refused save_memory [{block.MemoryTopic}]\n", OutputColor.Dim);
                            break;
                        }
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

                    case BlockType.SearchMemory:
                        try
                        {
                            string searchResult = await _host.SearchMemoryAsync(block.MemorySearchPattern);
                            result.ToolResultContents["search_memory"] = searchResult;
                        }
                        catch (Exception ex)
                        {
                            result.ToolResultContents["search_memory"] = $"Error searching memory: {ex.Message}";
                            _host.AppendOutput($"[MEMORY ERROR] {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.QueryLibrary:
                        try
                        {
                            string libraryResult = await _host.QueryLibraryAsync(
                                block.LibraryQuestion, block.LibraryTopK, block.LibraryDocFilter, _cancellationToken);
                            result.ToolResultContents["query_library"] = libraryResult;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            result.ToolResultContents["query_library"] = $"Error querying library: {ex.Message}";
                            _host.AppendOutput($"[LIBRARY ERROR] {ex.Message}\n", OutputColor.Error);
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
                                block.Pattern, block.MaxResults, block.Language, block.FileName);
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
                            string fetchContent = await _host.WebFetchAsync(block.Url, block.FetchOffset);
                            if (fetchContent != null)
                            {
                                // Two pages of one URL in a turn must not overwrite each other:
                                // the call id is authoritative (H-55), and the url+offset key keeps
                                // the per-turn map (training log, fallback lookup) distinct too.
                                result.ToolResultContents[LoopHelpers.WebFetchResultKey(block.Url, block.FetchOffset)] = fetchContent;
                                RecordCallResult(result, block, fetchContent);
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[WEB ERROR] fetch {block.Url}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.LearnSearch:
                        try
                        {
                            int? learnSearchCap = block.MaxResults > 0 ? (int?)block.MaxResults : null;
                            string learnSearchContent = await _host.LearnSearchAsync(block.Pattern, learnSearchCap);
                            if (learnSearchContent != null)
                                result.ToolResultContents[block.Pattern ?? ""] = learnSearchContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[LEARN ERROR] search \"{block.Pattern}\": {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.LearnFetch:
                        try
                        {
                            string learnFetchContent = await _host.LearnFetchAsync(block.Url);
                            if (learnFetchContent != null)
                                result.ToolResultContents[block.Url ?? ""] = learnFetchContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[LEARN ERROR] fetch {block.Url}: {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.LearnCodeSearch:
                        try
                        {
                            int? learnCodeSearchCap = block.MaxResults > 0 ? (int?)block.MaxResults : null;
                            string learnCodeSearchContent = await _host.LearnCodeSearchAsync(block.Pattern, learnCodeSearchCap);
                            if (learnCodeSearchContent != null)
                                result.ToolResultContents[block.Pattern ?? ""] = learnCodeSearchContent;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[LEARN ERROR] code_search \"{block.Pattern}\": {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.RunSql:
                        // run_sql with allow_write can change a database: a mutation. In Plan
                        // the refusal goes into the tool result the model reads, the same path
                        // every other refusal uses. The read-only default of the same tool is
                        // not a mutation — it looks at data the way read_file looks at files —
                        // and stays free in Plan.
                        if (block.SqlAllowWrite
                            && ApprovalPolicy.Decide(_options.ApprovalMode, MutationKind.SqlWrite)
                                == ApprovalDecision.Refuse)
                        {
                            string sqlRefusal = ApprovalPolicy.PlanRefusalMessage("run_sql (allow_write)");
                            result.ToolResultContents["run_sql"] = sqlRefusal;
                            result.Errors.Add(sqlRefusal);
                            _host.AppendOutput("[PLAN MODE] Refused: run_sql (allow_write)\n", OutputColor.Dim);
                            break;
                        }
                        try
                        {
                            // Connection is resolved in the host/SqlExecutor by precedence:
                            // explicit connection string -> named connection -> cwd appsettings.
                            string sqlResult = await _host.RunSqlAsync(
                                block.SqlQuery,
                                block.SqlConnString,
                                block.SqlConnName,
                                block.SqlAllowWrite,
                                block.SqlMaxRows,
                                block.SqlCommandTimeout);
                            result.ToolResultContents["run_sql"] = sqlResult;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[SQL ERROR] {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.Debug:
                        try
                        {
                            // Delegates to the host's DAP integration (same engine as the /debug
                            // slash command). Result is the status/output line handed back to the model.
                            string debugResult = await _host.RunDebugAsync(block.DebugCommand, block.DebugArgs);
                            result.ToolResultContents["debug"] = debugResult;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[DEBUG ERROR] {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.RecallCache:
                        try
                        {
                            string recalled = await _host.RecallCacheAsync(block.RecallCacheCommand);
                            result.ToolResultContents["recall_cache"] = recalled;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[RECALL ERROR] {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.ListCache:
                        try
                        {
                            string manifest = await _host.ListCacheAsync();
                            result.ToolResultContents["list_cache"] = manifest;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(ex.Message);
                            _host.AppendOutput($"[LIST_CACHE ERROR] {ex.Message}\n", OutputColor.Error);
                        }
                        break;

                    case BlockType.McpCall:
                        await ExecuteMcpCallAsync(block, result);
                        break;

                    case BlockType.ViewImage:
                        ExecuteViewImage(block, result);
                        break;

                    // Text, Done — already handled during streaming or by resolver
                    default:
                        break;
                }

                writeScope?.End(result);

                if (block.Type is not (BlockType.Text or BlockType.Patch))
                    BlockExecuted?.Invoke(block.Type);
            }

            // ── Trailing PATCH batch (diff preview gate) ───────────────────────
            if (pendingPatches.Count > 0)
                await FlushPatchesAsync(pendingPatches, result);

            return result;
        }

        /// <summary>
        /// One write call's share of the turn-wide result lists: what it added between
        /// <see cref="Begin"/> and <see cref="End"/>, formatted by LoopHelpers exactly as the
        /// turn-level result would be, and filed under the call's id (H-55).
        /// </summary>
        private sealed class WriteCallScope
        {
            private ResponseBlock _block;
            private string _tool;
            private int _created, _appended, _deleted, _renamed, _errors;

            public static WriteCallScope Begin(ResponseBlock block, ExecutionResult result)
            {
                string tool = block.Type switch
                {
                    BlockType.File       => "create_file",
                    BlockType.AppendFile => "append_file",
                    BlockType.Delete     => "delete_file",
                    BlockType.Rename     => "rename_file",
                    _ => null,
                };
                if (tool == null || block.ToolCallId == null) return null;
                return new WriteCallScope
                {
                    _block = block, _tool = tool,
                    _created = result.FilesCreated.Count, _appended = result.FilesAppended.Count,
                    _deleted = result.FilesDeleted.Count, _renamed = result.FilesRenamed.Count,
                    _errors = result.Errors.Count,
                };
            }

            public void End(ExecutionResult result)
            {
                var own = new ExecutionResult { LintNotes = result.LintNotes };
                own.FilesCreated.AddRange(result.FilesCreated.Skip(_created));
                own.FilesAppended.AddRange(result.FilesAppended.Skip(_appended));
                own.FilesDeleted.AddRange(result.FilesDeleted.Skip(_deleted));
                own.FilesRenamed.AddRange(result.FilesRenamed.Skip(_renamed));
                own.Errors.AddRange(result.Errors.Skip(_errors));

                var call = new ToolCallResult { Name = _tool };
                if (_block.Type == BlockType.Rename)
                {
                    call.Arguments["old_filename"] = _block.RenameFrom ?? "";
                    call.Arguments["new_filename"] = _block.RenameTo ?? "";
                }
                else
                {
                    call.Arguments["filename"] = _block.FileName ?? "";
                }
                result.ToolResultsByCallId[_block.ToolCallId] = LoopHelpers.BuildToolResultContent(call, own, null);
            }
        }

        private async Task FlushPatchesAsync(List<ResponseBlock> pendingPatches, ExecutionResult result)
        {
            var batch = new List<ResponseBlock>(pendingPatches);
            pendingPatches.Clear();
            await ExecuteBatchPatchesAsync(batch, result);
            BlockExecuted?.Invoke(BlockType.Patch);
        }

        /// <summary>
        /// Resolves all PATCH blocks, determines whether diff preview cards are
        /// needed, shows them if so, and applies approved patches.
        /// </summary>

        /// <summary>
        /// Hard ceiling on one MCP result before it enters the turn. Normal oversize is NOT
        /// handled here: LlmClient.AddToolResultMessage nearline-caps every tool result over
        /// its ingest threshold (8,000 chars by default) to an excerpt + recall_cache handle +
        /// a full spill file, which keeps the whole text recoverable. web_fetch's own 8,000-char
        /// paging is deliberately not copied: an MCP tool has no offset to page with, so a cut
        /// would throw that recoverable text away. This
        /// only stops a runaway server from pushing megabytes into the cache and the spill.
        /// </summary>
        internal const int MaxMcpResultChars = 200_000;

        /// <summary>
        /// Runs one mcp__server__tool call. Same approval class as run_shell (MutationKind.Shell):
        /// an external tool can have arbitrary side effects (run_workflow renders, install_node
        /// installs) and the executor cannot tell which. The result, or the error text, goes
        /// into ToolResultContents under the call id, so two identical calls in one turn
        /// each get their own. Never throws.
        /// </summary>
        private async Task ExecuteMcpCallAsync(ResponseBlock block, ExecutionResult result)
        {
            string label = $"{block.McpServer}.{block.McpTool}";
            string key = McpToolName.ResultKey(block.ToolCallId);

            if (McpTools == null)
            {
                string none = $"[MCP ERROR] {label}: no MCP client is attached to this session, so " +
                              $"mcp__{block.McpServer}__{block.McpTool} cannot run.";
                result.Errors.Add(none);
                result.ToolResultContents[key] = none;
                _host.AppendOutput($"[MCP] {label}: no MCP client attached\n", OutputColor.Error);
                return;
            }

            string mcpDetail = ApprovalDetail($"MCP {block.McpServer}/{block.McpTool}",
                (block.McpArguments ?? new JObject()).ToString(Newtonsoft.Json.Formatting.Indented));
            if (!await ApproveMutationAsync(MutationKind.Shell, $"Run MCP tool {label} {DescribeMcpArgs(block.McpArguments)}", result, mcpDetail))
            {
                // ApproveMutationAsync put the decline / plan-mode refusal in Errors; hand the
                // model that exact text as the tool's result.
                result.ToolResultContents[key] = result.Errors[result.Errors.Count - 1];
                return;
            }

            _host.AppendOutput($"[MCP] {label} running…\n", OutputColor.Dim);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string text = await McpTools.CallToolAsync(
                    block.McpServer, block.McpTool, block.McpArguments ?? new JObject(), _cancellationToken) ?? "";
                int fullLength = text.Length;
                if (text.Length > MaxMcpResultChars)
                    text = text.Substring(0, MaxMcpResultChars) +
                           $"\n[MCP: result truncated at {MaxMcpResultChars:N0} of {text.Length:N0} chars]";
                result.ToolResultContents[key] = text;

                McpCallOutcome outcome = McpJournal.Classify(text);
                if (outcome != McpCallOutcome.Ok)
                {
                    result.Errors.Add(text);
                    _host.AppendOutput($"[MCP] {label}: {FirstLine(text)}\n", OutputColor.Error);
                }
                else
                {
                    _host.AppendOutput($"[MCP] {label} done ({text.Length:N0} chars)\n", OutputColor.Dim);
                }
                JournalMcpCall(block, outcome, fullLength, clock.Elapsed);
            }
            catch (Exception ex)
            {
                // IMcpToolInvoker reports failures as text; this is its cancellation (Stop) or a
                // broken implementation. Either way the loop gets an error, not an exception.
                string msg = $"[MCP ERROR] {label}: {ex.Message}";
                result.Errors.Add(msg);
                result.ToolResultContents[key] = msg;
                _host.AppendOutput($"[MCP] {label}: {ex.Message}\n", OutputColor.Error);
                JournalMcpCall(block, McpCallOutcome.Error, msg.Length, clock.Elapsed);
            }
        }

        /// <summary>
        /// One "mcp" journal entry per call that actually ran — ok, error or timeout alike — so
        /// a reviewer sees what the agent did to external systems. Recorded here, where the
        /// transcript's "[MCP] …" lines are written, so the two cannot disagree. Hosts without
        /// a journal (the TUI) skip it; their transcript is the record. A declined or
        /// plan-refused call ran nothing and is not journaled, as with run_shell.
        /// </summary>
        private void JournalMcpCall(ResponseBlock block, McpCallOutcome outcome, int resultChars, TimeSpan elapsed)
        {
            if (_host is not IActionJournal journal) return;
            journal.RecordAction("mcp",
                McpJournal.Detail(block.McpServer, block.McpTool, block.McpArguments, outcome, resultChars, elapsed),
                outcome == McpCallOutcome.Ok);
        }

        private static string DescribeMcpArgs(JObject args)
        {
            string json = args == null ? "{}" : args.ToString(Newtonsoft.Json.Formatting.None);
            return json.Length <= 200 ? json : json.Substring(0, 200) + "…";
        }

        private static string FirstLine(string text)
        {
            int nl = text.IndexOf('\n');
            return nl < 0 ? text : text.Substring(0, nl);
        }

        /// <summary>
        /// Gates one mutation on the current mode's decision. True means go ahead.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The mode is read from <c>_options</c> HERE, once per mutation, so a runtime /mode
        /// flip takes effect at the next dispatch and never part-way through a batch that is
        /// already running.
        /// </para>
        /// <para>
        /// <see cref="ApprovalPolicy.Decide"/> is the decision: Auto allows, Manual asks, Plan
        /// refuses. The refusal message the model receives depends on WHICH one fired — a
        /// manual decline says "the user declined"; a Plan refusal says "plan mode", because
        /// telling the model it was simply declined would have it retry the same tool, and
        /// plan mode is a state, not a mood.
        /// </para>
        /// <para>
        /// On a decline or refusal the cause goes into <c>result.Errors</c> and the caller
        /// simply does not perform the operation. That is deliberate reuse: an empty
        /// FilesCreated / FilesAppended / FilesDeleted / FilesRenamed list already makes
        /// LoopHelpers build a BuildWriteFailure tool_result, and it relays result.Errors as
        /// the cause. So a declined write reports to the model as a failure with the reason
        /// attached, through the same path a sandbox refusal takes — rather than a second
        /// success-shaped message that would have to be kept honest separately.
        /// </para>
        /// </remarks>
        /// <param name="kind">What the mutation is, for the policy decision.</param>
        /// <param name="prompt">The short one-line description. It is what the transcript's
        /// [SKIPPED] / [PLAN MODE] lines and the plan refusal sent to the model carry, so it
        /// must stay one line.</param>
        /// <param name="result">Receives the decline / refusal cause.</param>
        /// <param name="detail">The full text for an approval dialog (see <see cref="ApprovalDetail"/>):
        /// the tool name, then the whole command or arguments. Only the Ask branch uses it.</param>
        private async Task<bool> ApproveMutationAsync(MutationKind kind, string prompt, ExecutionResult result, string detail)
        {
            ApprovalDecision decision = ApprovalPolicy.Decide(_options.ApprovalMode, kind);
            if (decision == ApprovalDecision.Allow)
                return true;

            if (decision == ApprovalDecision.Refuse)
            {
                string refusal = ApprovalPolicy.PlanRefusalMessage(prompt);
                result.Errors.Add(refusal);
                _host.AppendOutput($"[PLAN MODE] Refused: {prompt}\n", OutputColor.Dim);
                return false;
            }

            // Ask. ConfirmActionAsync (not ConfirmContinueAsync) — the approval question
            // must not be posed under the token-budget guard's title/buttons. Hosts that
            // do not override it inherit the interface default, which routes here to
            // ConfirmContinueAsync, so headless and test fakes behave exactly as before.
            bool approved = await _host.ConfirmActionAsync(prompt, WithReason(detail ?? prompt, _approvalReason, _approvalReasonLabel));
            if (approved)
                return true;

            result.Errors.Add(
                "The user declined this action in manual approval mode. It was NOT performed.");
            _host.AppendOutput($"[SKIPPED] user declined \u2014 {prompt}\n", OutputColor.Warning);
            return false;
        }

        /// <summary>
        /// The run_shell output the model reads when the command was refused. Plan mode says
        /// plan mode (so the model stops trying to act); a manual decline keeps the exact
        /// wording it always had, because a model that was told "plan mode" about a user's
        /// _Stop answer would be misinformed about why.
        /// </summary>
        private static string ShellRefusalOutput(ExecutionResult result, bool tests = false)
        {
            bool planRefusal = result.Errors.Count > 0
                && result.Errors[result.Errors.Count - 1].StartsWith("[PLAN MODE]", StringComparison.Ordinal);
            if (planRefusal)
                return result.Errors[^1];
            if (tests)
                return "The user declined to run these tests in manual approval mode. They were NOT run; do not assume they passed.";
            return "The user declined to run this command in manual approval mode. It was NOT executed \u2014 do not assume any of its effects happened.";
        }

        /// <summary>
        /// The approval dialog's text: "Tool: &lt;name&gt;" on the first line, then the full action
        /// with its own line breaks. Paul's popup used to show only the one-line summary
        /// (`Run shell: python -c " … (1093 chars total)`), which is useless for deciding; the
        /// dialog caps the height and says how much is hidden, so the text is not cut here.
        /// </summary>
        internal static string ApprovalDetail(string tool, string body)
        {
            string text = (body ?? string.Empty).Replace("\r", string.Empty).Trim();
            return text.Length == 0 ? $"Tool: {tool}" : $"Tool: {tool}\n{text}";
        }

        /// <summary>Longest reason the executor hands the dialog. The TUI holds it to 3
        /// wrapped rows on screen; this keeps a runaway paragraph from being carried at all.</summary>
        internal const int MaxApprovalReasonChars = 400;

        /// <summary>Label of a reason taken from the response's visible prose.</summary>
        internal const string ProseReasonLabel = "Why";

        /// <summary>Label of a reason taken from the response's reasoning (thinking) text, so
        /// the operator can tell it from something the model actually said.</summary>
        internal const string ThinkingReasonLabel = "Why (thinking)";

        /// <summary>
        /// How much of the END of a response's reasoning the fallback looks at. Only its last
        /// paragraph is shown and that is capped at <see cref="MaxApprovalReasonChars"/>, so a
        /// long thinking pass is never regex-scanned or copied whole.
        /// </summary>
        internal const int ReasoningTailChars = 8_000;

        /// <summary>
        /// Puts "&lt;label&gt;: &lt;reason&gt;" under the dialog text's "Tool:" line. No reason, no line.
        /// </summary>
        internal static string WithReason(string detail, string reason, string label = ProseReasonLabel)
        {
            if (string.IsNullOrEmpty(reason) || detail == null) return detail;
            int nl = detail.IndexOf('\n');
            return nl < 0
                ? $"{detail}\n{label}: {reason}"
                : $"{detail.Substring(0, nl)}\n{label}: {reason}{detail.Substring(nl)}";
        }

        /// <summary>
        /// The reason line for this response's approvals: the visible prose's last paragraph
        /// (<see cref="ApprovalReason"/>) labelled "Why"; when that yields nothing, the last
        /// paragraph of the same response's reasoning (<see cref="ResponseOutcome.Reasoning"/>)
        /// labelled "Why (thinking)". Null reason when neither has anything to say.
        /// </summary>
        internal static (string label, string reason) ApprovalReasonFor(ResponseOutcome outcome)
        {
            string prose = ApprovalReason(outcome?.Blocks);
            if (prose != null) return (ProseReasonLabel, prose);
            string thought = ReasoningReason(outcome?.Reasoning);
            return thought != null ? (ThinkingReasonLabel, thought) : (ProseReasonLabel, null);
        }

        /// <summary>
        /// The last paragraph of a response's reasoning text, cleaned like the prose reason
        /// (think tags themselves, leaked tool-call syntax, status lines) but keeping the text
        /// inside think tags — here that text IS the content. Looks only at the last
        /// <see cref="ReasoningTailChars"/> characters.
        /// </summary>
        internal static string ReasoningReason(string reasoning)
        {
            if (string.IsNullOrWhiteSpace(reasoning)) return null;
            string tail = reasoning.Length > ReasoningTailChars
                ? reasoning.Substring(reasoning.Length - ReasoningTailChars)
                : reasoning;
            return LastParagraphReason(tail);
        }

        /// <summary>
        /// The model's reason for this response's tool calls: the last paragraph of the visible
        /// prose it wrote alongside them. LoopDriver puts that prose (the response's content,
        /// already stripped of reasoning by ThinkFilter) first in the block list as a Text
        /// block. Defensively drops any think region, leaked tool-call syntax and DevMind's
        /// own status lines, collapses whitespace, and cuts at
        /// <see cref="MaxApprovalReasonChars"/> with "…". Null when there is nothing to say.
        /// </summary>
        internal static string ApprovalReason(IEnumerable<ResponseBlock> blocks)
        {
            if (blocks == null) return null;
            string prose = string.Join("\n\n", blocks
                .Where(b => b.Type == BlockType.Text && !string.IsNullOrWhiteSpace(b.Content))
                .Select(b => b.Content));
            if (prose.Length == 0) return null;

            prose = prose.Replace("\r", string.Empty);
            // A whole think region, then anything before a stray close tag (its open tag went
            // to the reasoning channel), then any lone open tag and what follows it.
            prose = System.Text.RegularExpressions.Regex.Replace(prose, @"<think>.*?</think>", "",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            int close = prose.LastIndexOf("</think>", StringComparison.Ordinal);
            if (close >= 0) prose = prose.Substring(close + "</think>".Length);
            int open = prose.IndexOf("<think>", StringComparison.Ordinal);
            if (open >= 0) prose = prose.Substring(0, open);
            return LastParagraphReason(prose);
        }

        /// <summary>
        /// Shared tail of both reasons: drops leaked tool-call syntax, any remaining think tags
        /// and DevMind's status lines, takes the last non-empty paragraph, collapses whitespace
        /// and cuts at <see cref="MaxApprovalReasonChars"/> with "…". Null when nothing is left.
        /// </summary>
        private static string LastParagraphReason(string text)
        {
            string prose = HeadlessAgent.StripStatusLines(HeadlessAgent.SanitizeAnswer(text.Replace("\r", string.Empty)));

            string[] paragraphs = System.Text.RegularExpressions.Regex.Split(prose, @"\n[ \t]*\n");
            string last = paragraphs.LastOrDefault(p => !string.IsNullOrWhiteSpace(p));
            if (last == null) return null;

            string reason = System.Text.RegularExpressions.Regex.Replace(last.Trim(), @"\s+", " ");
            if (reason.Length > MaxApprovalReasonChars)
            {
                int cut = reason.LastIndexOf(' ', MaxApprovalReasonChars);
                if (cut < MaxApprovalReasonChars / 2) cut = MaxApprovalReasonChars;
                reason = reason.Substring(0, cut).TrimEnd() + "…";
            }
            return reason;
        }


        /// <summary>run_tests as a one-line prompt: the project (or the host's auto-detect)
        /// and the filter when there is one.</summary>
        private static string DescribeTests(ResponseBlock block)
        {
            string project = string.IsNullOrWhiteSpace(block.TestProject)
                ? "(auto-detected project)" : block.TestProject.Trim();
            string text = string.IsNullOrWhiteSpace(block.TestFilter)
                ? project : $"{project} --filter {block.TestFilter.Trim()}";
            return DescribeCommand(text);
        }

        /// <summary>run_tests for the approval dialog: every argument the host will use to
        /// build the `dotnet test` invocation.</summary>
        private static string TestsDetail(ResponseBlock block)
        {
            string text = "project: " + (string.IsNullOrWhiteSpace(block.TestProject)
                ? "(none; the host auto-detects one)" : block.TestProject.Trim());
            if (!string.IsNullOrWhiteSpace(block.TestFilter))
                text += "\nfilter: " + block.TestFilter.Trim();
            if (block.TestTimeoutSeconds.HasValue)
                text += $"\ntimeout: {block.TestTimeoutSeconds.Value} s";
            return text;
        }


        /// <summary>
        /// A shell command as a one-line prompt: first line, plus a length when there is more.
        /// The full text is already in the transcript above, and a here-string pasted whole
        /// into a confirm dialog is unreadable at exactly the moment reading matters.
        /// </summary>
        private static string DescribeCommand(string command)
        {
            string text = (command ?? string.Empty).Trim();
            int newline = text.IndexOf('\n');
            if (newline < 0)
                return text.Length <= 120 ? text : text.Substring(0, 120) + "\u2026";

            string first = text.Substring(0, newline).TrimEnd();
            if (first.Length > 120) first = first.Substring(0, 120) + "\u2026";
            return $"{first} \u2026 ({text.Length} chars total)";
        }

        // \u2500\u2500 Write-time lint and build hints (H-19) \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        // Advisory only: a rule that fires appends a line to the tool result; nothing here can
        // fail or undo the write or the build it is commenting on.

        /// <summary>
        /// H-53 guard: a file tool that reached the executor with no file name. ToolCallMapper
        /// rejects such calls first, so this only catches a block built some other way — but it
        /// reports the cause instead of the raw "Value cannot be null. (Parameter 'key')".
        /// </summary>
        private bool MissingFileName(ResponseBlock block, string tool, ExecutionResult result)
        {
            if (!string.IsNullOrWhiteSpace(block.FileName)) return false;
            string msg = $"{tool}: no file name was provided — pass the file path in 'filename'.";
            result.Errors.Add(msg);
            _host.AppendOutput($"[{tool.ToUpperInvariant()} ERROR] {msg}\n", OutputColor.Error);
            if (block.ToolCallId != null) result.ToolResultsByCallId[block.ToolCallId] = $"[ERROR: {msg}]";
            return true;
        }

        /// <summary>
        /// At most this many images are attached to one request by view_image: the calls past
        /// it in one response are refused (nothing staged) and told to view the rest next.
        /// </summary>
        public const int MaxViewImagesPerRequest = 10;

        /// <summary>
        /// view_image: resolves the path exactly as read_file does (the host's working directory,
        /// <see cref="FileReadTools.ResolvePath"/>), validates it with the auto-attach checks
        /// (<see cref="ImagePathScanner.TryValidate"/>) and records the image in
        /// <see cref="ExecutionResult.StagedImages"/> for LoopDriver to stage on the next
        /// request. Read-only, so no approval. Every outcome is the call's own text result; a
        /// rejected file stages nothing. The per-request cap counts this response's staged
        /// images — one executor result is one request's worth.
        /// </summary>
        private void ExecuteViewImage(ResponseBlock block, ExecutionResult result)
        {
            string requested = block.FileName;
            string text;      // the call's tool result
            string logLine;   // its one-line transcript form
            if (string.IsNullOrWhiteSpace(requested))
            {
                logLine = "[VIEW_IMAGE ERROR] no path was provided";
                text = "[VIEW_IMAGE ERROR] no path was provided — pass the image file's path in 'path'. Nothing was attached.";
            }
            else if (result.StagedImages.Count >= MaxViewImagesPerRequest)
            {
                logLine = $"[VIEW_IMAGE ERROR] {requested}: too many images for one request";
                text = $"[VIEW_IMAGE ERROR] too many images for one request — view the rest next. {MaxViewImagesPerRequest} " +
                       $"images are already attached to your next request; {requested} was NOT attached.";
            }
            else
            {
                try
                {
                    FileResolution res = FileReadTools.ResolvePath(requested, _host.GetWorkingDirectory());
                    if (res.Path == null || !System.IO.File.Exists(res.Path))
                    {
                        logLine = $"[VIEW_IMAGE ERROR] {requested}: file not found";
                        text = "[VIEW_IMAGE ERROR] " + FilePathResolver.BuildFileNotFoundMessage("view_image", requested, res).TrimEnd()
                               + "\nNothing was attached.";
                    }
                    // Canonical form (FileReadTools.FileKey): the resolver hands an absolute hint
                    // back with forward slashes, and this path is what the placeholder names later.
                    else if (!ImagePathScanner.TryValidate(FileReadTools.FileKey(res.Path), out DetectedImage image, out string why))
                    {
                        string full = FileReadTools.FileKey(res.Path);
                        logLine = $"[VIEW_IMAGE ERROR] {full}: {why}";
                        text = $"[VIEW_IMAGE ERROR] {full}: {why}. Nothing was attached.";
                    }
                    else
                    {
                        result.StagedImages.Add((ImagePathScanner.BuildDataUri(image), image.FullPath));
                        text = $"Image {System.IO.Path.GetFileName(image.FullPath)} ({image.MimeType}, " +
                               $"{image.SizeBytes / 1024.0:F0} KB) attached to your next request.";
                        logLine = $"[VIEW_IMAGE] {image.FullPath} ({image.MimeType}, {image.SizeBytes / 1024.0:F0} KB) — attached to the next request";
                    }
                }
                catch (Exception ex)
                {
                    logLine = $"[VIEW_IMAGE ERROR] {requested}: {ex.Message}";
                    text = $"[VIEW_IMAGE ERROR] {requested}: {ex.Message}. Nothing was attached.";
                }
            }

            bool failed = text.StartsWith("[VIEW_IMAGE ERROR]", StringComparison.Ordinal);
            _host.AppendOutput(logLine + "\n", failed ? OutputColor.Warning : OutputColor.Dim);
            result.ToolResultContents["view_image"] = text;
            RecordCallResult(result, block, text);
        }

        /// <summary>Files a read-side result under its call id as well (H-55), so two calls on
        /// the same file in one turn each get their own result.</summary>
        private static void RecordCallResult(ExecutionResult result, ResponseBlock block, string content)
        {
            if (block.ToolCallId != null) result.ToolResultsByCallId[block.ToolCallId] = content;
        }

        /// <summary>Lint <paramref name="content"/> (the file as written) and file any lines under its path.</summary>
        private static void RecordLint(ExecutionResult result, string fullPath, string content, ISet<int> changedLines)
        {
            IReadOnlyList<string> notes = WriteLint.Check(fullPath, content, changedLines);
            if (notes.Count == 0) return;
            if (!result.LintNotes.TryGetValue(fullPath, out List<string> list))
                result.LintNotes[fullPath] = list = new List<string>();
            foreach (string n in notes)
                if (!list.Contains(n)) list.Add(n);
        }

        /// <summary>The appended text is the changed region: the last N lines of the file now on disk.</summary>
        private static void RecordAppendLint(ExecutionResult result, string fullPath, string appended)
        {
            if (string.IsNullOrEmpty(appended)) return;
            try
            {
                string after = System.IO.File.ReadAllText(fullPath);
                int total = WriteLint.SplitLines(after).Length;
                int added = WriteLint.SplitLines(appended.TrimEnd('\r', '\n')).Length;
                RecordLint(result, fullPath, after, WriteLint.LineRange(total - added + 1, added + 1));
            }
            catch { /* lint is advisory \u2014 never fail the append over it */ }
        }

        // H-34: after a patch's post-write check fails, PatchEngine restores the file; only if that
        // restore itself failed (its error says ROLLBACK FAILED) is "not modified" untrue.
        private static string NotModifiedSuffix(string cause)
            => cause != null && cause.Contains("ROLLBACK FAILED", StringComparison.Ordinal) ? "" : " File was NOT modified.";

        /// <summary>The changed region of a patch: lines of the patched file that were not in the original.</summary>
        private static void RecordPatchLint(ExecutionResult result, string fullPath, string before)
        {
            try
            {
                string after = System.IO.File.ReadAllText(fullPath);
                RecordLint(result, fullPath, after, WriteLint.ChangedLines(before ?? string.Empty, after));
            }
            catch { /* lint is advisory \u2014 never fail the patch over it */ }
        }

        /// <summary>Build/test output as the agent will read it: binary regions collapsed to a
        /// marker (job-1694 dumped raw JPEG bytes into its context), then any known-trap hints appended.</summary>
        private string WithBuildHints(string output)
        {
            if (string.IsNullOrEmpty(output)) return output ?? string.Empty;
            output = BinaryOutputMask.Collapse(output);
            string workingDirectory;
            try { workingDirectory = _host.GetWorkingDirectory(); }
            catch { workingDirectory = null; }
            return BuildErrorHints.Annotate(output, workingDirectory);
        }

        /// <summary>
        /// Applies one batch of consecutive patch calls. Totals (PatchesApplied/Failed,
        /// PatchedPaths, Errors, the path-keyed context echo) go into <paramref name="result"/>
        /// as before; each call ALSO gets its own result in ToolResultsByCallId (H-55), built by
        /// the same formatter from only that call's outcome — two patches of one file, or a patch
        /// and a read of it, no longer report each other's results.
        /// </summary>
        private async Task ExecuteBatchPatchesAsync(
            List<ResponseBlock> patchBlocks,
            ExecutionResult result)
        {
            var scoped = new ExecutionResult[patchBlocks.Count];
            for (int k = 0; k < scoped.Length; k++)
                scoped[k] = new ExecutionResult { LintNotes = result.LintNotes };
            try
            {
                await ExecuteBatchPatchesCoreAsync(patchBlocks, result, scoped);
            }
            finally
            {
                for (int k = 0; k < patchBlocks.Count; k++)
                {
                    string id = patchBlocks[k].ToolCallId;
                    if (id == null) continue;
                    var call = new ToolCallResult { Name = "patch_file" };
                    call.Arguments["filename"] = patchBlocks[k].FileName ?? "";
                    result.ToolResultsByCallId[id] = LoopHelpers.BuildToolResultContent(call, scoped[k], null);
                }
            }
        }

        private async Task ExecuteBatchPatchesCoreAsync(
            List<ResponseBlock> patchBlocks,
            ExecutionResult result,
            ExecutionResult[] scoped)
        {
            // Every outcome is recorded twice: in the turn totals and in the call's own scope.
            void Failed(int k, string error)
            {
                result.PatchesFailed++;
                result.Errors.Add(error);
                scoped[k].PatchesFailed++;
                scoped[k].Errors.Add(error);
            }

            void Applied(int k, string patchedPath, string originalContent)
            {
                result.PatchesApplied++;
                result.PatchedPaths.Add(patchedPath);
                scoped[k].PatchesApplied++;
                scoped[k].PatchedPaths.Add(patchedPath);
                RecordPatchLint(result, patchedPath, originalContent);
                string ctxEcho = _host.TakePatchContextEcho(patchedPath);
                if (!string.IsNullOrEmpty(ctxEcho))
                {
                    result.ToolResultContents[patchedPath] = ctxEcho;
                    scoped[k].ToolResultContents[patchedPath] = ctxEcho;
                }
                _lastReadKey = null;
                _lastReadRepeatCount = 0;
            }

            // Plan mode refuses patches outright: no card, no application. The refusal goes
            // into result.Errors per patch, so the model receives the [PLAN MODE] message —
            // the same path a declined write uses — and nothing below ever runs.
            if (ApprovalPolicy.Decide(_options.ApprovalMode, MutationKind.Patch)
                == ApprovalDecision.Refuse)
            {
                for (int k = 0; k < patchBlocks.Count; k++)
                {
                    var block = patchBlocks[k];
                    string failedFile = string.IsNullOrEmpty(block.FileName)
                        ? "unknown" : System.IO.Path.GetFileName(block.FileName);
                    string refusal = ApprovalPolicy.PlanRefusalMessage($"patch {failedFile}");
                    Failed(k, $"[PATCH-REFUSED:{failedFile}] {refusal}");
                    _host.AppendOutput($"[PLAN MODE] Refused: patch {failedFile}\n", OutputColor.Dim);
                }
                return;
            }

            // Manual mode means every patch is looked at, exact matches included — which
            // is precisely what AlwaysConfirmPatch already does, so it is folded in here
            // rather than given a second mechanism that could disagree with it.
            bool alwaysConfirm = _options.AlwaysConfirmPatch
                || ApprovalPolicy.Requires(_options.ApprovalMode, MutationKind.Patch);

            // Phase 1: Resolve all patches (parse + match, no side effects)
            var resolved = new List<PatchResolveResult>();
            var resolveIndices = new List<int>(); // maps resolved[] index to patchBlocks[] index

            for (int i = 0; i < patchBlocks.Count; i++)
            {
                var block = patchBlocks[i];
                try
                {
                    var (resolveResult, failureReason) = await _host.ResolvePatchAsync(block.Content, block.FromToolCall);
                    if (resolveResult != null)
                    {
                        resolved.Add(resolveResult);
                        resolveIndices.Add(i);
                    }
                    else
                    {
                        string failedFile = string.IsNullOrEmpty(block.FileName)
                            ? "unknown" : System.IO.Path.GetFileName(block.FileName);
                        string cause = string.IsNullOrEmpty(failureReason) ? "unknown" : failureReason;
                        Failed(i, $"[PATCH-FAILED:{failedFile}] Resolve failed: {cause} File was NOT modified.");
                    }
                }
                catch (Exception ex)
                {
                    string failedFile = string.IsNullOrEmpty(block.FileName)
                        ? "unknown" : System.IO.Path.GetFileName(block.FileName);
                    Failed(i, $"[PATCH-FAILED:{failedFile}] Resolve error: {ex.Message}");
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
                int k = resolveIndices[i];
                if (!alwaysConfirm && r.Confidence == PatchConfidence.Exact)
                {
                    // Auto-apply immediately — no card, no await
                    try
                    {
                        var (patchedPath, failureReason) = await _host.ApplyResolvedPatchAsync(r);
                        if (patchedPath != null)
                        {
                            Applied(k, patchedPath, r.OriginalContent);
                        }
                        else
                        {
                            string failedFile = string.IsNullOrEmpty(r.FileName)
                                ? "unknown" : System.IO.Path.GetFileName(r.FileName);
                            string cause = string.IsNullOrEmpty(failureReason) ? "unknown" : failureReason;
                            Failed(k, $"[PATCH-FAILED:{failedFile}] Apply failed: {cause}{NotModifiedSuffix(cause)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        string failedFile = string.IsNullOrEmpty(r.FileName)
                            ? "unknown" : System.IO.Path.GetFileName(r.FileName);
                        Failed(k, $"[PATCH-FAILED:{failedFile}] Apply error: {ex.Message}");
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
                    for (int i = 0; i < needPreview.Count; i++)
                        Failed(resolveIndices[needPreviewIndices[i]],
                            $"[PATCH-SKIPPED: {needPreview[i].FileName} — user cancelled. Re-READ the file if you need to try again.]");
                    return;
                }

                // Apply approved, inject SKIPPED for rejected
                for (int i = 0; i < needPreview.Count; i++)
                {
                    int k = resolveIndices[needPreviewIndices[i]];
                    if (approvedIndices.Contains(i))
                    {
                        try
                        {
                            var (patchedPath, failureReason) = await _host.ApplyResolvedPatchAsync(needPreview[i]);
                            if (patchedPath != null)
                            {
                                Applied(k, patchedPath, needPreview[i].OriginalContent);
                            }
                            else
                            {
                                string cause = string.IsNullOrEmpty(failureReason) ? "unknown" : failureReason;
                                Failed(k, $"[PATCH-FAILED:{needPreview[i].FileName}] Apply failed: {cause}{NotModifiedSuffix(cause)}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Failed(k, $"[PATCH-FAILED:{needPreview[i].FileName}] Apply error: {ex.Message}");
                            _host.AppendOutput($"[PATCH ERROR] {needPreview[i].FileName}: {ex.Message}\n", OutputColor.Error);
                        }
                    }
                    else
                    {
                        Failed(k, $"[PATCH-SKIPPED: {needPreview[i].FileName} — user rejected this change. Re-READ the file if you need to try again.]");
                        _host.AppendOutput(
                            $"[PATCH] Skipped {needPreview[i].FileName} — user rejected.\n",
                            OutputColor.Dim);
                    }
                }
            }
        }

    }
}
