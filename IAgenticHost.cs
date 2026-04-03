// File: IAgenticHost.cs  v7.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// Abstracts all side effects away from the agentic decision logic.
    /// <see cref="AgenticExecutor"/> calls these methods;
    /// <see cref="DevMindToolWindowControl"/> implements them by delegating
    /// to the existing partial class methods.
    /// </summary>
    public interface IAgenticHost
    {
        /// <summary>
        /// Apply a PATCH block to a file. Returns the resolved file path on
        /// success, or null if the patch failed (FIND text not found, fuzzy
        /// match rejected, ambiguous match, file not found, etc.).
        /// The patchContent parameter is the full PATCH block text including
        /// the "PATCH filename" header line and all FIND:/REPLACE: pairs.
        /// </summary>
        Task<string> ApplyPatchAsync(string patchContent);

        /// <summary>
        /// Run a shell command and capture its output.
        /// Returns the exit code and combined stdout+stderr output.
        /// Uses the current terminal working directory.
        /// </summary>
        Task<(int exitCode, string output)> RunShellAsync(string command);

        /// <summary>
        /// Save a FILE: block to disk. The fileName may be relative (resolved
        /// against the active project directory) or absolute. Opens the file
        /// in the VS editor after saving if OpenFileAfterGeneration is enabled.
        /// Returns the full path of the saved file.
        /// </summary>
        Task<string> SaveFileAsync(string fileName, string content, bool fromToolCall = false);

        /// <summary>
        /// Load the content of a file into context for the LLM.
        /// Handles file resolution (FindFileInSolutionAsync), outline-first
        /// behavior for large files, and line-range reads.
        /// Returns the file content that was loaded (or outline).
        /// </summary>
        Task<string> LoadFileContentAsync(string fileName, int rangeStart = 0,
            int rangeEnd = 0, bool forceFullRead = false);

        /// <summary>
        /// Append text to the output panel with the specified color.
        /// Must be safe to call from any thread (implementation handles
        /// dispatcher marshalling).
        /// </summary>
        void AppendOutput(string text, OutputColor color = OutputColor.Normal);

        /// <summary>
        /// Resubmit a prompt to the LLM. Used for auto-READ resubmission
        /// and retry-with-correction. Returns the raw response text.
        /// The caller (executor) will classify the new response.
        /// </summary>
        Task<string> ResubmitPromptAsync(string prompt);

        /// <summary>
        /// Show a confirmation dialog to the user and return their choice.
        /// Used for AskUser actions (e.g., fuzzy patch confirmation when
        /// not in agentic mode).
        /// </summary>
        Task<bool> ShowConfirmationAsync(string message);

        /// <summary>
        /// Update the scratchpad content in LlmClient.
        /// Called when a Scratchpad block is found in the response.
        /// </summary>
        void UpdateScratchpad(string content);

        /// <summary>
        /// Get the current terminal working directory.
        /// Used by the executor to resolve relative file paths.
        /// </summary>
        string GetWorkingDirectory();

        /// <summary>
        /// Searches a file for lines containing the pattern (case-insensitive substring match).
        /// Returns formatted results with line numbers, capped at maxMatches.
        /// </summary>
        Task<string> GrepFileAsync(string pattern, string filename, int? startLine, int? endLine);

        /// <summary>
        /// Searches all files matching the glob pattern for lines containing the search pattern
        /// (case-insensitive substring match). Returns filename:line: content for each match,
        /// capped at 100 results total across all files.
        /// </summary>
        Task<string> FindInFilesAsync(string pattern, string globPattern, int? startLine, int? endLine);

        /// <summary>
        /// Deletes a file from disk. Closes the file in the VS editor first if it is open.
        /// Returns "Deleted: {fullPath}" on success, or an error message on failure.
        /// Does NOT modify .csproj or other project references.
        /// </summary>
        Task<string> DeleteFileAsync(string filename);

        /// <summary>
        /// Renames (moves) a file on disk. Closes the old file in the VS editor if open,
        /// then opens the new file. Invalidates the FileContentCache for the old filename.
        /// Returns "Renamed: {oldPath} → {newPath}" on success, or an error message on failure.
        /// Does NOT update references in other files.
        /// </summary>
        Task<string> RenameFileAsync(string oldFilename, string newFilename);

        /// <summary>
        /// Returns a unified-style diff of the file compared to its snapshot at the start
        /// of the conversation. If the file has not been modified this session, returns a
        /// "no changes" message. Caps output at 200 lines.
        /// </summary>
        Task<string> GetFileDiffAsync(string filename);

        /// <summary>
        /// Runs dotnet test on the specified project with an optional filter.
        /// Parses TRX output into a compact summary: total/pass/fail/skip counts,
        /// plus details for each failed test (name, duration, error message).
        /// Falls back to raw console output if TRX parsing fails.
        /// </summary>
        Task<string> RunTestsAsync(string project, string filter);

        /// <summary>
        /// Resolves a PATCH block without applying it. Returns a
        /// <see cref="PatchResolveResult"/> with confidence and match data,
        /// or null if resolution failed.
        /// </summary>
        Task<PatchResolveResult> ResolvePatchAsync(string patchContent, bool fromToolCall = false);

        /// <summary>
        /// Applies a previously resolved PATCH. Returns the full path on success, null on failure.
        /// </summary>
        Task<string> ApplyResolvedPatchAsync(PatchResolveResult resolved);

        /// <summary>
        /// Presents diff preview cards for a batch of resolved patches and awaits
        /// user decisions. Returns the list of indices that were approved.
        /// If the cancellation token is triggered, all pending cards are cancelled.
        /// </summary>
        Task<List<int>> ShowDiffPreviewAsync(
            List<PatchResolveResult> resolvedPatches,
            CancellationToken cancellationToken);

        /// <summary>
        /// Load a memory topic file and return its content.
        /// Returns the topic content, or an error message if not found.
        /// </summary>
        Task<string> RecallMemoryAsync(string topic);

        /// <summary>
        /// Save content to a memory topic file for cross-session persistence.
        /// Returns a confirmation message.
        /// </summary>
        Task<string> SaveMemoryAsync(string topic, string content, string description);

        /// <summary>
        /// List all available memory topics with their descriptions.
        /// Returns a formatted list of topics.
        /// </summary>
        Task<string> ListMemoryTopicsAsync();
    }
}
