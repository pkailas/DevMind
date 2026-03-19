// File: IAgenticHost.cs  v1.0.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

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
        /// Apply a PATCH block to a file. Returns true if the patch was
        /// applied successfully, false if it failed (fuzzy match rejected,
        /// file not found, ambiguous match, etc.).
        /// The patchContent parameter is the full PATCH block text including
        /// the "PATCH filename" header line and all FIND:/REPLACE: pairs.
        /// </summary>
        Task<bool> ApplyPatchAsync(string patchContent);

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
        Task<string> SaveFileAsync(string fileName, string content);

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
    }
}
