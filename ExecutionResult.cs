// File: ExecutionResult.cs  v1.2.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;

namespace DevMind
{
    /// <summary>
    /// Captures what happened when the actions from a response were executed.
    /// Fed back into the next agentic iteration so the resolver knows the
    /// outcome of the previous round.
    /// </summary>
    public class ExecutionResult
    {
        public int    PatchesApplied { get; set; }
        public int    PatchesFailed  { get; set; }
        public int?   ShellExitCode  { get; set; }
        public string ShellOutput    { get; set; }

        /// <summary>
        /// Shorthand: a shell command ran and exited with code 0.
        /// </summary>
        public bool BuildSucceeded => ShellExitCode.HasValue && ShellExitCode.Value == 0;

        public List<string> FilesCreated  { get; set; }
        public List<string> FilesDeleted  { get; set; }  // full paths of successfully deleted files
        public List<string> FilesRenamed  { get; set; }  // "oldPath → newPath" strings for successfully renamed files
        public List<string> PatchedPaths  { get; set; }  // full paths of successfully patched files
        public List<string> Errors        { get; set; }
        public string       LastShellCommand { get; set; }  // last shell command that was run

        public ExecutionResult()
        {
            ShellOutput       = string.Empty;
            LastShellCommand  = string.Empty;
            FilesCreated      = new List<string>();
            FilesDeleted      = new List<string>();
            FilesRenamed      = new List<string>();
            PatchedPaths      = new List<string>();
            Errors            = new List<string>();
        }

        /// <summary>
        /// Returns a default instance representing a turn in which nothing was executed.
        /// </summary>
        public static ExecutionResult None()
        {
            return new ExecutionResult();
        }
    }
}
