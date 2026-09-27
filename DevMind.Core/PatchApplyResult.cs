// File: PatchApplyResult.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

namespace DevMind
{
    /// <summary>
    /// Result of applying a resolved patch via <see cref="PatchEngine.ApplyPatch"/>.
    /// </summary>
    public class PatchApplyResult
    {
        /// <summary>True when the file was written successfully.</summary>
        public bool Success { get; set; }

        /// <summary>File content after applying the patch. Populated on success.</summary>
        public string UpdatedContent { get; set; }

        /// <summary>
        /// Full path of the backup file created before writing.
        /// Null if backup creation failed (non-fatal — patch was still applied).
        /// </summary>
        public string BackupPath { get; set; }

        /// <summary>Error message when Success is false.</summary>
        public string Error { get; set; }

        /// <summary>
        /// H-34: true when the patch failed because an edit did not (or could not) land — the
        /// file changed after the patch was resolved and its FIND no longer matches, or the
        /// post-write check did not find an edit in the file. <see cref="Error"/> then starts
        /// with "edit N did not land" or names the changed file, and is meant for the model as
        /// is (not prefixed with "Write failed").
        /// </summary>
        public bool NotLanded { get; set; }
    }
}
