// File: BufferedAgenticHost.cs  v1.0 (promoted from DevMind.Cli/ConsoleAgenticHost.cs v1.1)
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// UI-agnostic IAgenticHost: all the file/shell/patch/memory/LSP logic the console skin
// used, with the console-specific pieces made pluggable so headless callers
// (DevMind.McpServer's devmind_task_* jobs) can run it with NO console at all — in an
// MCP process stdout is the JSON-RPC wire, so a single stray Console.Write corrupts
// the protocol.
//
//   * Output   → an Action<string, OutputColor> sink (ctor param; null = discard).
//                ConsoleAgenticHost passes an ANSI console writer.
//   * Prompts  → protected virtuals with HEADLESS-SAFE defaults: write guard
//                auto-approves, agentic-continue auto-continues, patch preview
//                auto-approves every patch. ConsoleAgenticHost overrides all three
//                with the interactive Console.ReadLine versions.
//   * Journal  → every mutating action (file save/append/patch/delete/rename, shell
//                command, test run, merge conflict) is recorded in Actions — the
//                audit trail a delegating agent (e.g. Claude Code) reviews afterward.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>One recorded host action, for the delegation audit trail.</summary>
    public sealed class HostAction
    {
        /// <summary>Action kind: shell | save | append | patch | delete | rename | test | conflict.</summary>
        public string Kind { get; set; }
        /// <summary>Human-readable summary (path, command, exit code...).</summary>
        public string Detail { get; set; }
        public bool Success { get; set; }
    }

    /// <summary>
    /// UI-agnostic implementation of <see cref="IAgenticHost"/> with buffered output and
    /// auto-approving interaction defaults (safe for headless use). Interactive hosts
    /// subclass and override the prompt virtuals; see file header.
    /// </summary>
    public class BufferedAgenticHost : IAgenticHost
    {
        // ── Fields ───────────────────────────────────────────────────────────────

        private readonly Action<string, OutputColor> _outputSink;

        // Delegation audit trail — see RecordAction. Guarded by _actionsLock: tool
        // execution is sequential per turn, but readers (job status snapshots) may
        // arrive from other threads.
        private readonly List<HostAction> _actions = new List<HostAction>();
        private readonly object _actionsLock = new object();

        // Patch-staleness tracking: patches applied per file since the model last READ
        // it. Field evidence (job-11): four overlapping patches against a stale view
        // corrupted a file ("Ambiguous FIND — matched at line 305 and line 378"); the
        // repair job that re-read after every patch applied 17+ cleanly. Enforced only
        // for headless runs (RestrictWritesToWorkingDirectory).
        private const int StalePatchThreshold = 3;
        private readonly Dictionary<string, int> _patchesSinceRead =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly ShellRunner _shellRunner;
        private readonly FileContentCache _fileCache = new FileContentCache();

        // Tracks which filenames have been read this session — controls outline vs. full on re-read.
        private readonly HashSet<string> _filesRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Tracks which filenames were accessed during the current user turn — feeds write guard.
        private readonly HashSet<string> _taskReadFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Baseline content keyed by full path, captured before first mutation — powers DIFF.
        private readonly Dictionary<string, string> _fileSnapshots =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly MemoryManager _memoryManager;

        // Shared Core facade for the five LSP tools — gating and error wrapping live there.
        private readonly LspToolService _lspTools;

        // Patch undo stack — mirrors the WPF extension's backup stack behavior.
        private const int PatchBackupStackLimit = 10;
        private readonly Stack<(string filePath, string backupPath)> _patchBackupStack =
            new Stack<(string, string)>();

        // When true, ShowDiffPreviewAsync auto-approves remaining patches this session
        // (set by typing 'a' at the four-way prompt). Cleared on ResetSession().
        private bool _alwaysApprove;

        // Pending merge conflict state — stored so /resolve can handle it without blocking input.
        private PendingConflictState _pendingConflict;


       // Called by ShowDiffPreviewAsync on 'q' to cancel the enclosing agentic turn.
        // Wired to cts.Cancel() in Program.cs (Commit 4). Defaults to no-op.
        private readonly Action _cancelTurn;

        // Set by the REPL loop before each agentic turn so RunShellAsync respects Ctrl+C.
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;

        // Task scratchpad — stores cross-turn state from SCRATCHPAD directives.
        // Injected into the system prompt each turn by Program.cs.
        private string _taskScratchpad = "";

        // ── Construction ─────────────────────────────────────────────────────────

        public BufferedAgenticHost(string workingDirectory, Action cancelTurn = null,
            Action<string, OutputColor> outputSink = null)
        {
            _shellRunner  = new ShellRunner(workingDirectory);
            _lspTools     = new LspToolService(workingDirectory);
            _cancelTurn   = cancelTurn ?? (() => { });
            _outputSink   = outputSink ?? ((_, __) => { });
            if (!string.IsNullOrEmpty(workingDirectory))
                _memoryManager = new MemoryManager(workingDirectory);
        }

        // ── Write sandbox ────────────────────────────────────────────────────────

        /// <summary>
        /// When true, file-tool writes (save/append/patch/delete/rename) resolving
        /// OUTSIDE the working directory are blocked and journaled. Headless delegation
        /// turns this on: local models sometimes hallucinate absolute paths (a live run
        /// wrote to /home/user/… → C:\home\user\… — a sandbox escape); interactive skins
        /// leave it off, where absolute paths are deliberate. Shell commands are not
        /// sandboxed — that is the accepted full-auto trust boundary.
        /// </summary>
        public bool RestrictWritesToWorkingDirectory { get; set; }

        /// <summary>
        /// Headless shell blocklist. Field evidence (job-11): a confused agent ran
        /// "git show HEAD~1:file | Set-Content file", overwriting a working-tree file
        /// and destroying two earlier tasks' uncommitted changes; another job taskkilled
        /// the operator's running API to unblock a build. Recovery-from-git and process
        /// control are reserved for the human operator in delegated runs.
        /// </summary>
        internal static bool IsBlockedHeadlessCommand(string command, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(command)) return false;
            string c = command.ToLowerInvariant();

            bool has(string s) => c.Contains(s, StringComparison.Ordinal);

            if (has("git restore") || has("git checkout --") || has("git checkout .")
                || has("git checkout head") || has("git reset --hard") || has("git clean"))
            {
                reason = "restores/discards working-tree files from git";
                return true;
            }

            if (has("git show") && (has(">") || has("set-content") || has("out-file") || has("| sc ")))
            {
                reason = "writes git object content over working-tree files";
                return true;
            }

            if (has("taskkill") || has("stop-process") || has("kill -9"))
            {
                reason = "kills processes";
                return true;
            }

            return false;
        }

        /// <summary>False (and journals the block) when the sandbox is on and
        /// <paramref name="fullPath"/> falls outside the working directory or the
        /// dedicated devmind output directory (<c>&lt;temp&gt;/devmind</c>).</summary>
        private bool IsWriteAllowed(string fullPath, string operation)
        {
            if (!RestrictWritesToWorkingDirectory) return true;
            try
            {
                string root = Path.GetFullPath(_shellRunner.WorkingDirectory ?? Directory.GetCurrentDirectory())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string devmindRoot = OutputDirectory
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(fullPath);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(devmindRoot, StringComparison.OrdinalIgnoreCase))
                    return true;

                RecordAction("blocked", $"{operation} outside working directory: {full}", success: false);
                AppendOutput($"[SANDBOX] {operation} blocked — {full} is outside the working directory. " +
                             "Use a path relative to the working directory.\n", OutputColor.Error);
                return false;
            }
            catch
            {
                return false; // unparseable path — treat as outside
            }
        }

        // ── Large-output spill ─────────────────────────────────────────────────────

        /// <summary>The dedicated DevMind output/scratch directory (<c>&lt;temp&gt;/devmind</c>).
        /// Headless runs are permitted to write here (see <see cref="IsWriteAllowed"/>) so large
        /// reports and spilled tool output land outside the working tree instead of polluting the
        /// repo — the failure mode that put a committed <c>dm_output.txt</c> in the working dir.</summary>
        public static string OutputDirectory { get; } =
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "devmind"));

        /// <summary>Writes <paramref name="content"/> to <c>dm_out_&lt;label&gt;.txt</c> under
        /// <see cref="OutputDirectory"/> and returns a short preview plus a pointer to the full file.
        /// Used when a tool result is too large to inline sensibly. This is a host-internal write —
        /// it does NOT route through the write sandbox, so it can never be blocked.</summary>
        internal static string SpillLargeOutput(string label, string content, int previewChars = 200)
        {
            Directory.CreateDirectory(OutputDirectory);

            string safeLabel = string.IsNullOrWhiteSpace(label) ? "out" : label;
            foreach (char c in Path.GetInvalidFileNameChars())
                safeLabel = safeLabel.Replace(c, '_');

            string outputPath = Path.Combine(OutputDirectory, $"dm_out_{safeLabel}.txt");
            File.WriteAllText(outputPath, content);

            string preview = content.Length > previewChars ? content.Substring(0, previewChars) : content;
            return $"[Output too large ({content.Length:N0} chars) — written to: {outputPath}]\n" +
                   $"{preview}...\n[See file for full output]";
        }

        // ── Action journal ───────────────────────────────────────────────────────

        /// <summary>Snapshot of all actions recorded since construction / last clear.</summary>
        public IReadOnlyList<HostAction> GetActions()
        {
            lock (_actionsLock) return _actions.ToArray();
        }

        public void ClearActions()
        {
            lock (_actionsLock) _actions.Clear();
        }

        private void RecordAction(string kind, string detail, bool success = true)
        {
            lock (_actionsLock)
                _actions.Add(new HostAction { Kind = kind, Detail = detail, Success = success });
        }

        // ── Context lifecycle helpers called by the REPL ──────────────────────────

        /// <summary>Called at the start of each user-initiated turn to reset the write guard set.</summary>
        public void ResetTaskContext() => _taskReadFiles.Clear();

       /// <summary>Called on /restart to reset session-scoped caches.</summary>
        public void ResetSession()
        {
            _filesRead.Clear();
            _fileSnapshots.Clear();
            _fileCache.InvalidateAll();
            _taskReadFiles.Clear();
            _alwaysApprove = false;
            _taskScratchpad = "";
            _pendingConflict = null;
        }


        // ── IAgenticHost.AppendOutput ─────────────────────────────────────────────

        /// <summary>All host output funnels here and into the ctor sink — NEVER Console.</summary>
        protected void AppendOutput(string text, OutputColor color = OutputColor.Normal)
        {
            if (string.IsNullOrEmpty(text)) return;
            _outputSink(text, color);
        }

        void IAgenticHost.AppendOutput(string text, OutputColor color) => AppendOutput(text, color);

        // ── IAgenticHost.RunShellAsync ────────────────────────────────────────────

       async Task<(int exitCode, string output)> IAgenticHost.RunShellAsync(string command, int? timeoutSeconds)
        {
            if (RestrictWritesToWorkingDirectory && IsBlockedHeadlessCommand(command, out string blockReason))
            {
                RecordAction("blocked", $"shell ({blockReason}): {command}", success: false);
                AppendOutput($"[SHELL GUARD] Blocked ({blockReason}): {command}\n", OutputColor.Error);
                return (1,
                    $"[BLOCKED] This command is not allowed in delegated tasks: {blockReason}. " +
                    "Do NOT restore files from git history (it can destroy uncommitted work from " +
                    "earlier tasks) and do NOT kill processes. To fix a broken file, READ its " +
                    "current content and apply corrective patches instead.");
            }

            AppendOutput($"[SHELL] > {command}\n", OutputColor.Dim);
            var progress = new Progress<ShellOutputLine>(line =>
                AppendOutput(line.Line + "\n", line.IsError ? OutputColor.Error : OutputColor.Normal));
            var (output, exitCode) = await _shellRunner.ExecuteAsync(command, CancellationToken, timeoutSeconds, progress);
            RecordAction("shell", $"{command} (exit {exitCode})", exitCode == 0);
            return (exitCode, output);
        }

        // ── IAgenticHost.SaveFileAsync ────────────────────────────────────────────

        async Task<string> IAgenticHost.SaveFileAsync(string fileName, string content, bool fromToolCall)
        {
            string fileNameOnly = SafeGetFileName(fileName);

            if (!IsFileKnownToTask(fileNameOnly))
            {
                bool approved = await ConfirmUnreadFileWriteAsync(fileNameOnly);
                if (!approved)
                {
                    AppendOutput($"[WRITE GUARD] File write to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                    return null;
                }
                _taskReadFiles.Add(fileNameOnly);
            }

            // Block if a conflict is pending from a previous write attempt
            if (_pendingConflict != null)
            {
                AppendOutput($"[MERGE CONFLICT] Cannot write to \"{fileNameOnly}\" — pending conflict on \"{_pendingConflict.FilePath}\" must be resolved first. Use /resolve accept_proposed, /resolve accept_current, or /resolve cancel.\n", OutputColor.Error);
                return null;
            }

            string fileContent = fromToolCall ? content : PatchEngine.StripOuterCodeFence(content);

            try
            {
                string fullPath = ResolveWritePath(fileName);
                if (!IsWriteAllowed(fullPath, "write"))
                    return null;
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // New file — no merge gate needed
                if (!File.Exists(fullPath))
                {
                    File.WriteAllText(fullPath, fileContent);
                    _fileCache.Store(FileCacheKey(fullPath), fileContent);
                    int newFileLines = fileContent.Split('\n').Length;
                    AppendOutput($"[FILE] Saved {fileNameOnly} ({newFileLines} lines)\n", OutputColor.Success);
                    RecordAction("save", $"{fullPath} (new, {newFileLines} lines)");
                    return fullPath;
                }

                // Existing file — run three-way merge gate
                string currentText = File.ReadAllText(fullPath);
                string baseText = _fileCache.GetFull(FileCacheKey(fullPath));

                MergeCheckResult merge = ThreeWayMergeCheck.CheckAndMerge(baseText, fileContent, currentText);

                if (merge.UsedFallback)
                {
                    // Two-way fallback: no cache entry existed — this is overwrite detection only,
                    // NOT a true three-way merge. Log a warning to debug output.
                    Trace.Event("merge_fallback", $"SaveFileAsync: two-way fallback for \"{fileNameOnly}\" — no base cache entry. Overwrite detection only, not true three-way merge.");
                }


                if (merge.HasConflicts)
                {
                    // Store conflict state and return control to input loop — do NOT block.
                    _pendingConflict = new PendingConflictState
                    {
                        FilePath = fullPath,
                        BaseContent = baseText ?? string.Empty,
                        ProposedContent = fileContent,
                        CurrentContent = currentText,
                        MergeResult = merge,
                        UsedFallback = merge.UsedFallback
                    };

                    // Report conflict blocks to output
                    RecordAction("conflict", $"write blocked: {fullPath}", success: false);
                    AppendOutput($"\n[MERGE CONFLICT] Write to \"{fileNameOnly}\" blocked by merge conflict.\n", OutputColor.Error);
                    for (int i = 0; i < merge.Conflicts.Count; i++)
                    {
                        var c = merge.Conflicts[i];
                        AppendOutput($"  Conflict #{i + 1} at line {c.LineNumber}:\n", OutputColor.Warning);
                        AppendOutput($"    Base:      {ThreeWayMergeCheck.Truncate(c.BaseText, 60)}\n", OutputColor.Dim);
                        AppendOutput($"    Proposed:  {ThreeWayMergeCheck.Truncate(c.ProposedText, 60)}\n", OutputColor.Success);
                        AppendOutput($"    Current:   {ThreeWayMergeCheck.Truncate(c.CurrentText, 60)}\n", OutputColor.Error);
                    }
                    AppendOutput($"  Resolution: type /resolve accept_proposed, /resolve accept_current, or /resolve cancel\n\n", OutputColor.Warning);

                    return null;
                }

                // No conflicts — write the merged text
                string finalContent = merge.MergedText;
                File.WriteAllText(fullPath, finalContent);
                _fileCache.Store(FileCacheKey(fullPath), finalContent);
                int lineCount = finalContent.Split('\n').Length;
                AppendOutput($"[FILE] Saved {fileNameOnly} ({lineCount} lines){(merge.UsedFallback ? " [two-way fallback]" : "")}\n", OutputColor.Success);
                RecordAction("save", $"{fullPath} ({lineCount} lines)");
                return fullPath;
            }
            catch (Exception ex)
            {
                AppendOutput($"[FILE ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return null;
            }
        }

        private static string TruncateText(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            string firstLine = text.Split('\n')[0].Trim();
            if (firstLine.Length <= maxChars) return firstLine;
            return firstLine.Substring(0, maxChars) + "...";
        }



        // ── IAgenticHost.AppendFileAsync ──────────────────────────────────────────

        async Task<string> IAgenticHost.AppendFileAsync(string fileName, string content)
        {
            string fileNameOnly = SafeGetFileName(fileName);

            if (!IsFileKnownToTask(fileNameOnly))
            {
                bool approved = await ConfirmUnreadFileWriteAsync(fileNameOnly);
                if (!approved)
                {
                    AppendOutput($"[WRITE GUARD] File append to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                    return null;
                }
                _taskReadFiles.Add(fileNameOnly);
            }

            // Block if a conflict is pending
            if (_pendingConflict != null)
            {
                AppendOutput($"[MERGE CONFLICT] Cannot append to \"{fileNameOnly}\" — pending conflict on \"{_pendingConflict.FilePath}\" must be resolved first. Use /resolve accept_proposed, /resolve accept_current, or /resolve cancel.\n", OutputColor.Error);
                return null;
            }

            try
            {
                string resolvedPath = FindFile(fileNameOnly, fileName.Replace('\\', '/'))
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileName);
                if (!IsWriteAllowed(resolvedPath, "append"))
                    return null;

                // New file — no merge gate needed
                if (!File.Exists(resolvedPath))
                {
                    string dir = Path.GetDirectoryName(resolvedPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(resolvedPath, content);
                    _fileCache.Store(FileCacheKey(resolvedPath), content);
                    AppendOutput($"[APPEND] Created {fileNameOnly}\n", OutputColor.Success);
                    RecordAction("append", $"{resolvedPath} (created)");
                    return resolvedPath;
                }

                // Existing file — always re-read from disk and refresh cache before merge (requirement #3)
                string currentText = File.ReadAllText(resolvedPath);
                _fileCache.Store(FileCacheKey(resolvedPath), currentText);

                // For append: proposed = currentText + separator + content
                string separator = currentText.Length > 0 && !currentText.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "";
                string proposedText = currentText + separator + content;

                string baseText = _fileCache.GetFull(FileCacheKey(resolvedPath));

                MergeCheckResult merge = ThreeWayMergeCheck.CheckAndMerge(baseText, proposedText, currentText);

                if (merge.UsedFallback)
                {
                    Trace.Event("merge_fallback", $"AppendFileAsync: two-way fallback for \"{fileNameOnly}\" — no base cache entry. Overwrite detection only.");
                }


                if (merge.HasConflicts)
                {
                    _pendingConflict = new PendingConflictState
                    {
                        FilePath = resolvedPath,
                        BaseContent = baseText ?? string.Empty,
                        ProposedContent = proposedText,
                        CurrentContent = currentText,
                        MergeResult = merge,
                        UsedFallback = merge.UsedFallback
                    };

                    RecordAction("conflict", $"append blocked: {resolvedPath}", success: false);
                    AppendOutput($"\n[MERGE CONFLICT] Append to \"{fileNameOnly}\" blocked by merge conflict.\n", OutputColor.Error);
                    for (int i = 0; i < merge.Conflicts.Count; i++)
                    {
                        var c = merge.Conflicts[i];
                        AppendOutput($"  Conflict #{i + 1} at line {c.LineNumber}:\n", OutputColor.Warning);
                        AppendOutput($"    Base:      {ThreeWayMergeCheck.Truncate(c.BaseText, 60)}\n", OutputColor.Dim);
                        AppendOutput($"    Proposed:  {ThreeWayMergeCheck.Truncate(c.ProposedText, 60)}\n", OutputColor.Success);
                        AppendOutput($"    Current:   {ThreeWayMergeCheck.Truncate(c.CurrentText, 60)}\n", OutputColor.Error);
                    }
                    AppendOutput($"  Resolution: type /resolve accept_proposed, /resolve accept_current, or /resolve cancel\n\n", OutputColor.Warning);
                    return null;
                }

                // No conflicts — write the merged text
                File.WriteAllText(resolvedPath, merge.MergedText);
                _fileCache.Store(FileCacheKey(resolvedPath), merge.MergedText);
                AppendOutput($"[APPEND] Appended to {fileNameOnly}{(merge.UsedFallback ? " [two-way fallback]" : "")}\n", OutputColor.Success);
                RecordAction("append", resolvedPath);
                return resolvedPath;
            }
            catch (Exception ex)
            {
                AppendOutput($"[APPEND ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return null;
            }
        }


        // ── IAgenticHost.GetWorkingDirectory ──────────────────────────────────────

        string IAgenticHost.GetWorkingDirectory() => _shellRunner.WorkingDirectory;

       // ── IAgenticHost scratchpad ──────────────────────────────────────────────

        void IAgenticHost.UpdateScratchpad(string content)
        {
            _taskScratchpad = string.IsNullOrWhiteSpace(content) ? "" : content.Trim();
        }

       string IAgenticHost.TaskScratchpad => TaskScratchpad;

        /// <summary>Gets the current task scratchpad content.</summary>
        public string TaskScratchpad => _taskScratchpad;

        /// <summary>
        /// Resolves a pending merge conflict. Call from REPL /resolve handler.
        /// Returns status message or null when no conflict is pending.
        /// </summary>
        public string ResolvePendingConflict(string choice)
        {
            if (_pendingConflict == null)
                return "[MERGE] No pending conflict to resolve.";

            var pc = _pendingConflict;

            if (choice == "cancel")
            {
                _pendingConflict = null;
                return "[MERGE] Conflict cancelled — pending patch discarded.";
            }

            if (choice == "accept_proposed")
            {
                try
                {
                    File.WriteAllText(pc.FilePath, pc.ProposedContent);
                    string fileNameOnly = SafeGetFileName(pc.FilePath);
                    _fileCache.Store(FileCacheKey(pc.FilePath), pc.ProposedContent);
                    _pendingConflict = null;
                    AppendOutput($"[MERGE] Accepted proposed content for {fileNameOnly}\n", OutputColor.Success);
                    return $"[MERGE] Accepted proposed content for {fileNameOnly}";
                }
                catch (Exception ex)
                {
                    return $"[MERGE ERROR] Failed to write: {ex.Message}";
                }
            }

            if (choice == "accept_current")
            {
                _pendingConflict = null;
                AppendOutput($"[MERGE] Kept current content for {SafeGetFileName(pc.FilePath)} — change discarded\n", OutputColor.Dim);
                return $"[MERGE] Kept current content — change discarded.";
            }

            return "[MERGE] Unknown choice. Usage: /resolve accept_proposed | accept_current | cancel";
        }


        // ── IAgenticHost.DeleteFileAsync ──────────────────────────────────────────

        Task<string> IAgenticHost.DeleteFileAsync(string filename)
        {
            string fileNameOnly = SafeGetFileName(filename);
            string resolvedPath = FindFile(fileNameOnly, filename.Replace('\\', '/'))
                ?? Path.Combine(_shellRunner.WorkingDirectory, filename);

            if (!File.Exists(resolvedPath))
                return Task.FromResult(BuildFileNotFoundMessage("DELETE", filename));

            if (!IsWriteAllowed(resolvedPath, "delete"))
                return Task.FromResult(
                    "DELETE blocked: path is outside the working directory — use a relative path.");

            try
            {
                File.Delete(resolvedPath);
                RecordAction("delete", resolvedPath);
                return Task.FromResult($"Deleted: {resolvedPath}");
            }
            catch (Exception ex)
            {
                return Task.FromResult($"DELETE: failed to delete {resolvedPath} — {ex.Message}");
            }
        }

        // ── IAgenticHost.RenameFileAsync ──────────────────────────────────────────

        Task<string> IAgenticHost.RenameFileAsync(string oldFilename, string newFilename)
        {
            string oldNameOnly = SafeGetFileName(oldFilename);
            string oldPath = FindFile(oldNameOnly, oldFilename.Replace('\\', '/'))
                ?? Path.Combine(_shellRunner.WorkingDirectory, oldFilename);

            if (!File.Exists(oldPath))
                return Task.FromResult(BuildFileNotFoundMessage("RENAME", oldFilename));

            bool newHasDir = newFilename.Contains('/') || newFilename.Contains('\\');
            string newPath = newHasDir
                ? Path.Combine(Path.GetDirectoryName(oldPath) ?? _shellRunner.WorkingDirectory,
                               newFilename.Replace('/', Path.DirectorySeparatorChar))
                : Path.Combine(Path.GetDirectoryName(oldPath) ?? _shellRunner.WorkingDirectory, newFilename);

            if (File.Exists(newPath))
                return Task.FromResult($"RENAME: destination already exists — {newPath}");

            if (!IsWriteAllowed(oldPath, "rename") || !IsWriteAllowed(newPath, "rename"))
                return Task.FromResult(
                    "RENAME blocked: path is outside the working directory — use relative paths.");

            try
            {
                File.Move(oldPath, newPath);
                _fileCache.Invalidate(FileCacheKey(oldPath));
                RecordAction("rename", $"{oldPath} → {newPath}");
                return Task.FromResult($"Renamed: {oldPath} → {newPath}");
            }
            catch (Exception ex)
            {
                return Task.FromResult($"RENAME: failed to rename {oldPath} → {newPath} — {ex.Message}");
            }
        }

        // ── IAgenticHost.GetPatchBackupCount ──────────────────────────────────────

        int IAgenticHost.GetPatchBackupCount() => _patchBackupStack.Count;

        // ── IAgenticHost.RecallMemoryAsync ────────────────────────────────────────

        Task<string> IAgenticHost.RecallMemoryAsync(string topic)
        {
            if (_memoryManager == null)
                return Task.FromResult("Memory not available: no working directory");

            string content = _memoryManager.LoadTopic(topic);
            if (content == null)
            {
                AppendOutput($"[MEMORY] Topic not found: {topic}\n", OutputColor.Dim);
                return Task.FromResult($"Topic not found: {topic}");
            }

            AppendOutput($"[MEMORY] Recalled: {topic}\n", OutputColor.Dim);
            return Task.FromResult(content);
        }

        // ── IAgenticHost.SaveMemoryAsync ──────────────────────────────────────────

        Task<string> IAgenticHost.SaveMemoryAsync(string topic, string content, string description)
        {
            if (_memoryManager == null)
                return Task.FromResult("Memory not available: no working directory");

            _memoryManager.SaveTopic(topic, content, description);
            string desc = string.IsNullOrEmpty(description) ? topic : description;
            AppendOutput($"[MEMORY] Saved: [{topic}] {desc}\n", OutputColor.Success);
            return Task.FromResult($"Memory saved: [{topic}] {desc}");
        }

        // ── IAgenticHost.ListMemoryTopicsAsync ────────────────────────────────────

        Task<string> IAgenticHost.ListMemoryTopicsAsync()
        {
            if (_memoryManager == null)
                return Task.FromResult("Memory not available: no working directory");

            string index = _memoryManager.LoadIndex();
            if (string.IsNullOrWhiteSpace(index))
            {
                var topics = _memoryManager.ListTopics();
                if (topics.Count == 0)
                {
                    AppendOutput("[MEMORY] No memory topics found.\n", OutputColor.Dim);
                    return Task.FromResult("No memory topics found. Use save_memory to create one.");
                }
                string list = string.Join("\n", topics.Select(t => $"- [{t}]"));
                AppendOutput($"[MEMORY] {topics.Count} topic(s) available.\n", OutputColor.Dim);
                return Task.FromResult(list);
            }

            AppendOutput("[MEMORY] Topics listed.\n", OutputColor.Dim);
            return Task.FromResult(index);
        }

        // ── IAgenticHost.SearchMemoryAsync ────────────────────────────────────────

        Task<string> IAgenticHost.SearchMemoryAsync(string pattern)
        {
            if (_memoryManager == null)
                return Task.FromResult("Memory not available: no working directory");

            string result = _memoryManager.SearchTopics(pattern);
            if (result == null)
            {
                AppendOutput("[MEMORY] No memory topics to search.\n", OutputColor.Dim);
                return Task.FromResult("No memory topics found. Use save_memory to create one.");
            }

            AppendOutput($"[MEMORY] Searched topics for \"{pattern}\"\n", OutputColor.Dim);
            return Task.FromResult(result);
        }

        // ── IAgenticHost.QueryLibraryAsync ────────────────────────────────────────

        async Task<string> IAgenticHost.QueryLibraryAsync(string question, int topK, CancellationToken cancellationToken)
        {
            var config = TuiConfig.Load();
            AppendOutput($"[LIBRARY] Query: \"{question}\"\n", OutputColor.Dim);
            return await DocumentLibrarian.QueryAsTextAsync(
                config.LibraryEmbeddingEndpoint, config.LibraryConnectionString,
                question, topK, cancellationToken).ConfigureAwait(false);
        }

        // ── IAgenticHost LSP tools (delegate to shared Core LspToolService) ───────

        async Task<string> IAgenticHost.GetDiagnosticsAsync(string filename)
        {
            string fullPath = ResolveLspPath(filename);
            if (fullPath == null) return BuildFileNotFoundMessage("get_diagnostics", filename);
            AppendOutput($"[LSP] get_diagnostics {SafeGetFileName(fullPath)}\n", OutputColor.Dim);
            return await _lspTools.GetDiagnosticsAsync(fullPath, CancellationToken);
        }

        async Task<string> IAgenticHost.GoToDefinitionAsync(string filename, int line, int character)
        {
            string fullPath = ResolveLspPath(filename);
            if (fullPath == null) return BuildFileNotFoundMessage("go_to_definition", filename);
            AppendOutput($"[LSP] go_to_definition {SafeGetFileName(fullPath)}:{line}:{character}\n", OutputColor.Dim);
            return await _lspTools.GoToDefinitionAsync(fullPath, line, character, CancellationToken);
        }

        async Task<string> IAgenticHost.FindReferencesAsync(string filename, int line, int character)
        {
            string fullPath = ResolveLspPath(filename);
            if (fullPath == null) return BuildFileNotFoundMessage("find_references", filename);
            AppendOutput($"[LSP] find_references {SafeGetFileName(fullPath)}:{line}:{character}\n", OutputColor.Dim);
            return await _lspTools.FindReferencesAsync(fullPath, line, character, CancellationToken);
        }

        async Task<string> IAgenticHost.HoverAsync(string filename, int line, int character)
        {
            string fullPath = ResolveLspPath(filename);
            if (fullPath == null) return BuildFileNotFoundMessage("hover", filename);
            AppendOutput($"[LSP] hover {SafeGetFileName(fullPath)}:{line}:{character}\n", OutputColor.Dim);
            return await _lspTools.HoverAsync(fullPath, line, character, CancellationToken);
        }

        async Task<string> IAgenticHost.FindSymbolAsync(string query, int maxResults, string language)
        {
            AppendOutput($"[LSP] find_symbol \"{query}\"\n", OutputColor.Dim);
            return await _lspTools.FindSymbolAsync(query, maxResults, language, CancellationToken);
        }

        // ── IAgenticHost web tools (delegate to shared Core WebTools) ─────────────

        async Task<string> IAgenticHost.WebSearchAsync(string query, int? maxResults)
        {
            AppendOutput($"[WEB] search: {query}\n", OutputColor.Dim);
            return await WebTools.WebSearchAsync(query, maxResults, CancellationToken);
        }

       async Task<string> IAgenticHost.WebFetchAsync(string url)
        {
            AppendOutput($"[WEB] fetch: {url}\n", OutputColor.Dim);
            return await WebTools.WebFetchAsync(url, CancellationToken);
        }

        // Last connection that opened successfully this session — sticky reuse so a stateless
        // run_sql call need not re-supply the connection. Session-scoped (instance field), not static.
        private string _lastSuccessfulSqlConnectionString;

        async Task<string> IAgenticHost.RunSqlAsync(string query, string connectionString, string connectionName, bool allowWrite,
            int maxRows, int commandTimeout)
        {
            // Resolve by precedence (explicit -> named -> session sticky -> cwd appsettings). The console
            // skin has no named-connection store, so only explicit, session sticky, and appsettings apply.
            var workingDir = ((IAgenticHost)this).GetWorkingDirectory();
            var resolved = SqlExecutor.ResolveConnectionString(
                connectionString, connectionName, namedConnections: null, _lastSuccessfulSqlConnectionString, workingDir, out var resolveError);
            if (resolved == null)
            {
                AppendOutput($"[SQL ERROR] {resolveError}\n", OutputColor.Error);
                return $"[ERROR] {resolveError}";
            }

            // Mask for logging (never echo the real connection string)
            var masked = SqlExecutor.MaskConnectionString(resolved);
            AppendOutput($"[SQL] executing query (connection: {masked})\n", OutputColor.Dim);

            var result = SqlExecutor.ExecuteQuery(query, resolved, allowWrite, maxRows, commandTimeout, out var connectionOpened);
            if (connectionOpened)
                _lastSuccessfulSqlConnectionString = resolved; // cache the known-good connection for this session

            // Spill to a file if the result is very large, rather than flooding the context.
            if (result.Length > 4000)
                result = SpillLargeOutput("sql", result);

            AppendOutput($"[SQL] {result}\n", OutputColor.Success);
            return result;
        }

        Task<string> IAgenticHost.RunDebugAsync(string command, IReadOnlyDictionary<string, string> args)
        {
            // DAP debugging is wired into the TUI host only (it drives a netcoredbg session and
            // streams stop/output events to the transcript). The console skin has no debugger.
            const string msg = "The debug tool is only available in the DevMind TUI, not the console skin.";
            AppendOutput($"[DEBUG] {msg}\n", OutputColor.Warning);
            return Task.FromResult($"[ERROR] {msg}");
        }

        /// <summary>The LLM's nearline cache, wired by the session owner — used by recall_cache. May be null.</summary>
        public NearlineCache NearlineCache { get; set; }

        /// <summary>Max characters of a recalled result returned to the model (mirrors the history cap).</summary>
        private const int MaxRecallChars = 50_000;

        Task<string> IAgenticHost.RecallCacheAsync(string handle)
        {
            if (NearlineCache == null)
                return Task.FromResult("[recall_cache] nearline cache is not available in this host.");
            if (string.IsNullOrWhiteSpace(handle))
                return Task.FromResult("[recall_cache] no handle provided. Pass a handle like \"nl-7\" or a cache key like \"read:file.cs\".");

            // Accept either a breadcrumb handle ("nl-7") or a raw cache key ("read:file.cs",
            // "tool:call_3") — after a brainwash the breadcrumbs are gone, so keys advertised
            // in the synthetic prompt must be recallable directly.
            string key = NearlineCache.GetKeyForHandle(handle) ?? handle;

            string content = NearlineCache.Retrieve(key);
            if (content == null)
                return Task.FromResult($"[recall_cache] no cached content for '{handle}' — unknown handle/key, or the entry was evicted.");

            if (content.Length > MaxRecallChars)
            {
                int originalLength = content.Length;
                content = content.Substring(0, MaxRecallChars) + $"\n[truncated — {originalLength} chars]";
            }

            AppendOutput($"[RECALL] {handle} → {key} ({content.Length} chars)\n", OutputColor.Dim);
            return Task.FromResult(content);
        }

        Task<bool> IAgenticHost.ConfirmContinueAsync(string message) => ConfirmContinueCoreAsync(message);

        /// <summary>Agentic checkpoint ("depth cap reached — continue?"). Headless default:
        /// always continue (the caller bounds the run with depth cap + timeout instead).
        /// Interactive hosts override with a real prompt.</summary>
        protected virtual Task<bool> ConfirmContinueCoreAsync(string message)
        {
            AppendOutput($"\n[AGENTIC] {message} — auto-continuing (headless).\n", OutputColor.Warning);
            return Task.FromResult(true);
        }

        /// <summary>Resolves an LSP tool's filename argument to an existing full path, or null.</summary>
        private string ResolveLspPath(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return null;
            return FindFile(SafeGetFileName(filename), filename.Replace('\\', '/'));
        }

        // ── IAgenticHost.LoadFileContentAsync ────────────────────────────────────

        Task<string> IAgenticHost.LoadFileContentAsync(
            string fileName, int rangeStart, int rangeEnd, bool forceFullRead)
        {
            if (fileName.StartsWith("git ", StringComparison.OrdinalIgnoreCase))
                return LoadGitContentAsync(fileName, rangeStart);

            return LoadFileContentCoreAsync(fileName, rangeStart, rangeEnd, forceFullRead);
        }

        // ── IAgenticHost.GrepFileAsync ────────────────────────────────────────────

        Task<string> IAgenticHost.GrepFileAsync(string pattern, string filename, int? startLine, int? endLine)
        {
            const int MaxMatches = 50;

            string fileNameOnly = SafeGetFileName(filename);
            string resolvedPath = FindFile(fileNameOnly, filename.Replace('\\', '/'));
            if (resolvedPath == null || !File.Exists(resolvedPath))
                return Task.FromResult(BuildFileNotFoundMessage("GREP", filename));

            string cacheKey = FileCacheKey(resolvedPath);
            _fileCache.InvalidateIfStale(cacheKey, resolvedPath); // out-of-band writes
            if (!_fileCache.Contains(cacheKey))
            {
                string diskContent;
                try { diskContent = File.ReadAllText(resolvedPath); }
                catch (Exception ex) { return Task.FromResult($"GREP: error reading {filename} — {ex.Message}"); }
                _fileCache.Store(cacheKey, diskContent);
            }

            int totalFileLines = _fileCache.GetLineCount(cacheKey);
            int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
            int scanEnd   = endLine.HasValue   ? Math.Min(totalFileLines, endLine.Value) : totalFileLines;

            // Full call parameters + effective window in the transcript line — a bare
            // "0 matches" summary made the job-8 false negative undiagnosable from logs.
            string grepScope = $"[lines {scanStart}-{scanEnd} of {totalFileLines}" +
                $"{(startLine.HasValue || endLine.HasValue ? $", requested start_line={(startLine?.ToString() ?? "-")} end_line={(endLine?.ToString() ?? "-")}" : "")}]";

            var matcher = SearchPattern.BuildMatcher(pattern);
            var matches = new List<(int lineNum, string lineText)>();
            for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
            {
                string lineContent = _fileCache.GetLineRange(cacheKey, lineNum, lineNum);
                if (lineContent == null) continue;
                if (matcher(lineContent))
                    matches.Add((lineNum, lineContent));
            }

            if (matches.Count == 0)
            {
                AppendOutput($"[GREP] no matches for \"{pattern}\" in {filename} {grepScope}\n", OutputColor.Dim);
                return Task.FromResult($"GREP: no matches for \"{pattern}\" in {filename}");
            }

            int totalMatches = matches.Count;
            bool truncated = totalMatches > MaxMatches;
            if (truncated) matches = matches.GetRange(0, MaxMatches);

            int maxLineNum = matches[matches.Count - 1].lineNum;
            int numWidth = maxLineNum.ToString().Length;

            string header = truncated
                ? $"GREP results for \"{pattern}\" in {filename} ({MaxMatches} of {totalMatches} matches — narrow your pattern or use a line range):"
                : $"GREP results for \"{pattern}\" in {filename} ({totalMatches} match{(totalMatches == 1 ? "" : "es")}):";

            var sb = new StringBuilder();
            sb.AppendLine(header);
            foreach (var (lineNum, lineText) in matches)
                sb.AppendLine($"  {lineNum.ToString().PadLeft(numWidth)}: {lineText.TrimEnd()}");

            _taskReadFiles.Add(fileNameOnly);
            AppendOutput($"[GREP] {totalMatches} match{(totalMatches == 1 ? "" : "es")} for \"{pattern}\" in {filename} {grepScope}\n", OutputColor.Success);
            return Task.FromResult(sb.ToString().TrimEnd('\r', '\n'));
        }

        // ── IAgenticHost.FindInFilesAsync ─────────────────────────────────────────

        Task<string> IAgenticHost.FindInFilesAsync(string pattern, string globPattern, int? startLine, int? endLine)
        {
            const int MaxMatches = 100;

            string searchDir = _shellRunner.WorkingDirectory;
            string normalizedGlob = globPattern.Replace('\\', '/');
            string filePattern = normalizedGlob;
            string effectiveRoot = searchDir;
            int lastSlash = normalizedGlob.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                string dirPart = normalizedGlob.Substring(0, lastSlash);
                filePattern = normalizedGlob.Substring(lastSlash + 1);
                string candidate = Path.Combine(searchDir, dirPart.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(candidate)) effectiveRoot = candidate;
            }

            IEnumerable<string> files;
            try
            {
                files = ContextEngine.SafeEnumerateFilesGlob(effectiveRoot, filePattern)
                    .Where(f => !ContextEngine.IsNoisePath(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                return Task.FromResult($"FIND: error enumerating files for {globPattern} — {ex.Message}");
            }

            var findMatcher = SearchPattern.BuildMatcher(pattern);
            var allMatches = new List<(string fileLabel, int lineNum, string lineText)>();
            bool hitCap = false;

            foreach (string filePath in files)
            {
                if (hitCap) break;
                string fileNameOnly = SafeGetFileName(filePath);
                string cacheKey = FileCacheKey(filePath);

                _fileCache.InvalidateIfStale(cacheKey, filePath); // out-of-band writes
                if (!_fileCache.Contains(cacheKey))
                {
                    // Skip cloud/OneDrive placeholders (would download), binaries, oversized.
                    if (ContextEngine.ShouldSkipForContentSearch(filePath)) continue;

                    string diskContent;
                    try { diskContent = File.ReadAllText(filePath); }
                    catch { continue; }
                    _fileCache.Store(cacheKey, diskContent);
                }

                int totalFileLines = _fileCache.GetLineCount(cacheKey);
                int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
                int scanEnd   = endLine.HasValue   ? Math.Min(totalFileLines, endLine.Value) : totalFileLines;

                for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
                {
                    string lineContent = _fileCache.GetLineRange(cacheKey, lineNum, lineNum);
                    if (lineContent == null) continue;
                    if (findMatcher(lineContent))
                    {
                        allMatches.Add((fileNameOnly, lineNum, lineContent));
                        if (allMatches.Count >= MaxMatches) { hitCap = true; break; }
                    }
                }
            }

            if (allMatches.Count == 0)
            {
                AppendOutput($"[FIND] no matches for \"{pattern}\" in {globPattern}\n", OutputColor.Dim);
                return Task.FromResult($"FIND: no matches for \"{pattern}\" in {globPattern}");
            }

            int shownCount = allMatches.Count;
            string findHeader = hitCap
                ? $"FIND results for \"{pattern}\" in {globPattern} ({MaxMatches}+ matches — narrow your pattern or add a line range):"
                : $"FIND results for \"{pattern}\" in {globPattern} ({shownCount} match{(shownCount == 1 ? "" : "es")}):";

            var sb = new StringBuilder();
            sb.AppendLine(findHeader);
            foreach (var (fileLabel, lineNum, lineText) in allMatches)
                sb.AppendLine($"  {fileLabel}:{lineNum}: {lineText.TrimEnd()}");

            AppendOutput($"[FIND] {(hitCap ? MaxMatches + "+" : shownCount.ToString())} match{(shownCount == 1 ? "" : "es")} for \"{pattern}\" in {globPattern}\n", OutputColor.Success);
            return Task.FromResult(sb.ToString().TrimEnd('\r', '\n'));
        }

        // ── IAgenticHost.ListFilesAsync ───────────────────────────────────────────

        Task<string> IAgenticHost.ListFilesAsync(string glob, bool recursive, CancellationToken cancellationToken)
        {
            const int Cap = 200;

            string searchDir = _shellRunner.WorkingDirectory;
            if (string.IsNullOrEmpty(searchDir))
                return Task.FromResult("[ERROR: working directory not set]");

            string normalizedGlob = (glob ?? "").Replace('\\', '/');
            string filePattern = normalizedGlob;
            string effectiveRoot = searchDir;
            int lastSlash = normalizedGlob.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                string dirPart = normalizedGlob.Substring(0, lastSlash);
                filePattern = normalizedGlob.Substring(lastSlash + 1);
                string candidate = Path.Combine(searchDir, dirPart.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(candidate)) effectiveRoot = candidate;
            }

            if (string.IsNullOrWhiteSpace(filePattern))
                return Task.FromResult("[ERROR: glob pattern is empty]");

            IEnumerable<string> matches;
            try
            {
                matches = recursive
                    ? ContextEngine.SafeEnumerateFilesGlob(effectiveRoot, filePattern).Where(f => !ContextEngine.IsNoisePath(f))
                    : Directory.EnumerateFiles(effectiveRoot, filePattern, SearchOption.TopDirectoryOnly).Where(f => !ContextEngine.IsNoisePath(f));
            }
            catch (Exception ex)
            {
                return Task.FromResult($"[ERROR: {ex.Message}]");
            }

            var sorted = matches
                .Select(Path.GetFullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sorted.Count == 0)
                return Task.FromResult("[no matches]");

            var sb = new StringBuilder();
            int shown = Math.Min(sorted.Count, Cap);
            for (int i = 0; i < shown; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sb.AppendLine(sorted[i]);
            }
            if (sorted.Count > Cap)
                sb.AppendLine($"[truncated — {sorted.Count - Cap} more matches]");

            AppendOutput($"[LIST] {shown} file{(shown == 1 ? "" : "s")} matching \"{glob}\"\n", OutputColor.Dim);
            return Task.FromResult(sb.ToString().TrimEnd());
        }

        // ── IAgenticHost.RunTestsAsync ────────────────────────────────────────────
        // v1: raw console output. TRX parsing deferred until ParseTrxSummary moves to Core.

       async Task<string> IAgenticHost.RunTestsAsync(string project, string filter, int? timeoutSeconds)
        {
            if (string.IsNullOrWhiteSpace(project))
            {
                try
                {
                    string[] csprojFiles = Directory.GetFiles(_shellRunner.WorkingDirectory, "*.csproj",
                        SearchOption.TopDirectoryOnly);
                    if (csprojFiles.Length == 1)
                    {
                        project = csprojFiles[0];
                        AppendOutput($"[TEST] Auto-detected project: {Path.GetFileName(project)}\n", OutputColor.Dim);
                    }
                    else if (csprojFiles.Length > 1)
                    {
                        project = csprojFiles[0];
                        AppendOutput($"[TEST] Multiple .csproj files found — using {Path.GetFileName(project)}\n", OutputColor.Dim);
                    }
                    else return "[TEST] No project specified and no .csproj found in working directory.";
                }
                catch { return "[TEST] No project specified."; }
            }

            // Resolve bare project name (no path separators) by searching for a matching .csproj
            bool looksLikeBare = !project.Contains('/') && !project.Contains('\\');
            if (looksLikeBare && !string.IsNullOrEmpty(_shellRunner.WorkingDirectory))
            {
                string searchName = project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    ? project : project + ".csproj";
                try
                {
                    string[] found = Directory.GetFiles(_shellRunner.WorkingDirectory, searchName,
                        SearchOption.AllDirectories);
                    if (found.Length > 0) project = found[0];
                }
                catch { }
            }

            string filterArg = !string.IsNullOrWhiteSpace(filter) ? $" --filter \"{filter.Trim('\"')}\"" : "";
            string quotedProject = project.Contains(' ') ? $"\"{project}\"" : project;
            string cmd = $"dotnet test {quotedProject} --no-build --verbosity normal{filterArg}";

            AppendOutput($"[TEST] > {cmd}\n", OutputColor.Dim);

            try
            {
                var (output, exitCode) = await _shellRunner.ExecuteAsync(cmd, CancellationToken, timeoutSeconds);
                RecordAction("test", $"{cmd} (exit {exitCode})", exitCode == 0);
                return string.IsNullOrWhiteSpace(output)
                    ? $"TEST: no output (exit code {exitCode})"
                    : output;
            }
            catch (Exception ex)
            {
                return $"[TEST] Failed to run tests: {ex.Message}";
            }
        }

        // ── IAgenticHost.GetFileDiffAsync ─────────────────────────────────────────

        Task<string> IAgenticHost.GetFileDiffAsync(string filename)
        {
            string fileNameOnly = SafeGetFileName(filename);
            string resolvedPath = FindFile(fileNameOnly, filename.Replace('\\', '/'))
                ?? Path.Combine(_shellRunner.WorkingDirectory, filename);

            if (!_fileSnapshots.ContainsKey(resolvedPath))
            {
                AppendOutput($"[DIFF] {filename}: not modified this session\n", OutputColor.Dim);
                return Task.FromResult($"DIFF: No changes — {filename} has not been modified this session.");
            }

            string original = _fileSnapshots[resolvedPath];
            string current;
            try { current = File.ReadAllText(resolvedPath); }
            catch (Exception ex) { return Task.FromResult($"DIFF: error reading {filename} — {ex.Message}"); }

            string normOld = original.Replace("\r\n", "\n").Replace("\r", "\n");
            string normNew = current.Replace("\r\n", "\n").Replace("\r", "\n");

            if (string.Equals(normOld, normNew, StringComparison.Ordinal))
            {
                AppendOutput($"[DIFF] {filename}: no changes\n", OutputColor.Dim);
                return Task.FromResult($"DIFF: No changes detected in {filename}.");
            }

            string[] oldLines = normOld.Split('\n');
            string[] newLines = normNew.Split('\n');
            string diffResult = DiffHelper.GenerateUnifiedDiff(filename, oldLines, newLines);

            AppendOutput($"[DIFF] {filename}: changes shown ({oldLines.Length} → {newLines.Length} lines)\n", OutputColor.Dim);
            return Task.FromResult(diffResult);
        }

        // ── IAgenticHost.ResolvePatchAsync ────────────────────────────────────────

        async Task<PatchResolveResult> IAgenticHost.ResolvePatchAsync(string patchContent, bool fromToolCall)
        {
            // Staleness guard (headless): too many patches since the last read means the
            // model's view of the file has drifted — the precondition for overlapping-
            // patch corruption. Thrown (not null) so the exact guidance reaches the
            // model via the executor's error channel. Deliberately BEFORE the main try.
            if (RestrictWritesToWorkingDirectory)
            {
                string headerLine = (patchContent ?? string.Empty).Split('\n')[0];
                string guardName = SafeGetFileName(headerLine.Length > 5 ? headerLine.Substring(5).Trim() : "");
                if (guardName.Length > 0
                    && _patchesSinceRead.TryGetValue(guardName, out int applied)
                    && applied >= StalePatchThreshold)
                {
                    RecordAction("blocked", $"patch guard: {applied} patches to {guardName} since last read", success: false);
                    AppendOutput($"[PATCH GUARD] {guardName}: {applied} patches since last read — read required.\n", OutputColor.Warning);
                    throw new InvalidOperationException(
                        $"[PATCH GUARD] {applied} patches have already been applied to {guardName} since you last read it — " +
                        "your view of the file is stale, which is how overlapping patches corrupt files. " +
                        "READ the affected line range of the file first, then patch against its CURRENT content.");
                }
            }

            try
            {
                // Extract filename from "PATCH <filename>" header line
                string firstLine = (patchContent ?? string.Empty).Split('\n')[0];
                string blockFileName = firstLine.Length > 5 ? firstLine.Substring(5).Trim() : string.Empty;

                if (string.IsNullOrEmpty(blockFileName))
                {
                    AppendOutput("[PATCH] No filename specified.\n", OutputColor.Error);
                    return null;
                }

                string normalizedHint = blockFileName.Replace('\\', '/');
                string fileNameOnly   = SafeGetFileName(blockFileName);

                // Write guard
                if (!IsFileKnownToTask(fileNameOnly))
                {
                    bool approved = await ConfirmUnreadFileWriteAsync(fileNameOnly);
                    if (!approved)
                    {
                        AppendOutput($"[WRITE GUARD] Patch to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                        return null;
                    }
                    _taskReadFiles.Add(fileNameOnly);
                }

                // Resolve file path; load into cache if absent
                string fullPath = FindFile(fileNameOnly, normalizedHint)
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileNameOnly);

                if (!File.Exists(fullPath))
                {
                    AppendOutput($"[PATCH] File not found: {fullPath}\n", OutputColor.Warning);
                    return null;
                }

                if (!IsWriteAllowed(fullPath, "patch"))
                    return null;

                _fileCache.InvalidateIfStale(FileCacheKey(fullPath), fullPath); // out-of-band writes
                if (!_fileCache.Contains(FileCacheKey(fullPath)))
                {
                    AppendOutput($"[AUTO-READ] Loading {fileNameOnly} before patch...\n", OutputColor.Dim);
                    var (cached, _enc) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                    _fileCache.Store(FileCacheKey(fullPath), cached);
                    _filesRead.Add(fileNameOnly);
                    _taskReadFiles.Add(fileNameOnly);
                }

                CaptureFileSnapshot(fullPath);

                var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                return PatchEngine.ResolvePatch(patchContent, fullPath, blockFileName, content, encoding,
                    fromToolCall, AppendOutput);
            }
            catch (Exception ex)
            {
                AppendOutput($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return null;
            }
        }

        // ── IAgenticHost.ApplyResolvedPatchAsync ──────────────────────────────────

        Task<string> IAgenticHost.ApplyResolvedPatchAsync(PatchResolveResult resolved)
        {
            try
            {
                // Block if a conflict is pending
                if (_pendingConflict != null)
                {
                    AppendOutput($"[MERGE CONFLICT] Cannot apply patch — pending conflict on \"{_pendingConflict.FilePath}\" must be resolved first. Use /resolve accept_proposed, /resolve accept_current, or /resolve cancel.\n", OutputColor.Error);
                    return Task.FromResult<string>(null);
                }

                string fileNameOnly = SafeGetFileName(resolved.FullPath);

                // Existing file — run merge gate before applying patch
                string currentText = File.ReadAllText(resolved.FullPath);
                string baseText = _fileCache.GetFull(FileCacheKey(resolved.FullPath));

                // Compute proposed content from resolved blocks (before disk write)
                string proposedText = ComputePatchedContent(resolved);

                MergeCheckResult merge = ThreeWayMergeCheck.CheckAndMerge(baseText, proposedText, currentText);

                if (merge.UsedFallback)
                {
                    Trace.Event("merge_fallback", $"ApplyResolvedPatchAsync: two-way fallback for \"{fileNameOnly}\" — no base cache entry. Overwrite detection only.");
                }


                if (merge.HasConflicts)
                {
                    _pendingConflict = new PendingConflictState
                    {
                        FilePath = resolved.FullPath,
                        BaseContent = baseText ?? string.Empty,
                        ProposedContent = proposedText,
                        CurrentContent = currentText,
                        MergeResult = merge,
                        UsedFallback = merge.UsedFallback
                    };

                    RecordAction("conflict", $"patch blocked: {resolved.FullPath}", success: false);
                    AppendOutput($"\n[MERGE CONFLICT] Patch to \"{fileNameOnly}\" blocked by merge conflict.\n", OutputColor.Error);
                    for (int i = 0; i < merge.Conflicts.Count; i++)
                    {
                        var c = merge.Conflicts[i];
                        AppendOutput($"  Conflict #{i + 1} at line {c.LineNumber}:\n", OutputColor.Warning);
                        AppendOutput($"    Base:      {ThreeWayMergeCheck.Truncate(c.BaseText, 60)}\n", OutputColor.Dim);
                        AppendOutput($"    Proposed:  {ThreeWayMergeCheck.Truncate(c.ProposedText, 60)}\n", OutputColor.Success);
                        AppendOutput($"    Current:   {ThreeWayMergeCheck.Truncate(c.CurrentText, 60)}\n", OutputColor.Error);
                    }
                    AppendOutput($"  Resolution: type /resolve accept_proposed, /resolve accept_current, or /resolve cancel\n\n", OutputColor.Warning);
                    return Task.FromResult<string>(null);
                }

                // No conflicts — apply patch to disk
                string backupDir = Path.Combine(Path.GetTempPath(), "DevMind");
                var result = PatchEngine.ApplyPatch(resolved, backupDir);

                if (!result.Success)
                {
                    AppendOutput($"[PATCH] Error: {result.Error}\n", OutputColor.Error);
                    return Task.FromResult<string>(null);
                }

                if (result.BackupPath != null)
                {
                    if (_patchBackupStack.Count >= PatchBackupStackLimit)
                    {
                        var entries = _patchBackupStack.ToArray();
                        var oldest  = entries[entries.Length - 1];
                        try { File.Delete(oldest.backupPath); } catch { }
                        _patchBackupStack.Clear();
                        for (int i = entries.Length - 2; i >= 0; i--)
                            _patchBackupStack.Push(entries[i]);
                    }
                    _patchBackupStack.Push((resolved.FullPath, result.BackupPath));
                }

                _fileCache.Store(FileCacheKey(resolved.FullPath), result.UpdatedContent);

                int undosAvailable = _patchBackupStack.Count;
                AppendOutput($"[PATCH] Applied to {resolved.FullPath} (undo depth: {undosAvailable}){(merge.UsedFallback ? " [two-way fallback]" : "")}\n",
                    OutputColor.Success);
                RecordAction("patch", resolved.FullPath);
                _patchesSinceRead[fileNameOnly] = _patchesSinceRead.GetValueOrDefault(fileNameOnly) + 1;
                return Task.FromResult(resolved.FullPath);
            }
            catch (Exception ex)
            {
                AppendOutput($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return Task.FromResult<string>(null);
            }
        }


        // ── IAgenticHost.ShowDiffPreviewAsync ─────────────────────────────────────
        // Diff rendering + approval loop live here; the DECISION is a virtual so
        // interactive hosts can prompt (y/n/a/q) while the headless default approves.

        /// <summary>A patch-preview decision returned by <see cref="PromptPatchDecisionAsync"/>.</summary>
        protected enum PatchDecision { Approve, Skip, ApproveAll, CancelTurn }

        async Task<List<int>> IAgenticHost.ShowDiffPreviewAsync(
            List<PatchResolveResult> resolvedPatches, CancellationToken cancellationToken)
        {
            var approved = new List<int>();

            for (int i = 0; i < resolvedPatches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var r = resolvedPatches[i];
                string badge = r.Confidence == PatchConfidence.Fuzzy ? " [Fuzzy ⚠]" : " [Exact ✓]";

                // Print unified diff for this patch
                AppendOutput($"\n[PATCH] {r.FileName}{badge}\n", OutputColor.Dim);
                string patched  = ComputePatchedContent(r);
                string[] oldLns = r.OriginalContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                string[] newLns = patched.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                AppendOutput(DiffHelper.GenerateUnifiedDiff(r.FileName, oldLns, newLns) + "\n",
                    OutputColor.Normal);

                if (_alwaysApprove)
                {
                    approved.Add(i);
                    AppendOutput($"[PATCH] Auto-approved ({i + 1}/{resolvedPatches.Count})\n", OutputColor.Dim);
                    continue;
                }

                switch (await PromptPatchDecisionAsync(r))
                {
                    case PatchDecision.Approve:
                        approved.Add(i);
                        break;
                    case PatchDecision.ApproveAll:
                        _alwaysApprove = true;
                        approved.Add(i);
                        break;
                    case PatchDecision.CancelTurn:
                        _cancelTurn();
                        throw new OperationCanceledException();
                    // Skip: fall through without approving.
                }
            }

            return approved;
        }

        /// <summary>Per-patch approval decision. Headless default: approve everything
        /// (full-auto within the working directory; the journal audits what changed).
        /// Interactive hosts override with the four-way y/n/a/q prompt.</summary>
        protected virtual Task<PatchDecision> PromptPatchDecisionAsync(PatchResolveResult resolved)
            => Task.FromResult(PatchDecision.Approve);

        // ── Private helpers ───────────────────────────────────────────────────────

        private bool IsFileKnownToTask(string fileNameOnly)
            => _taskReadFiles.Contains(fileNameOnly) || _taskReadFiles.Count == 0;

        /// <summary>Write guard for files never read this task. Headless default: approve
        /// and journal it (full-auto within the working directory by design — the
        /// journal is the audit trail). Interactive hosts override with a y/N prompt.</summary>
        protected virtual Task<bool> ConfirmUnreadFileWriteAsync(string fileNameOnly)
        {
            AppendOutput(
                $"[WRITE GUARD] \"{fileNameOnly}\" was not read during this task — auto-approved (headless).\n",
                OutputColor.Warning);
            return Task.FromResult(true);
        }

        private string ResolveWritePath(string fileName)
        {
            if (Path.IsPathRooted(fileName)) return fileName;
            return Path.Combine(_shellRunner.WorkingDirectory ?? Directory.GetCurrentDirectory(), fileName);
        }

        // Resolves a filename to its full path without VS DTE.
        // Priority: absolute path → hint-relative to workingDir → exact name in workingDir → recursive search.
        private string FindFile(string fileNameOnly, string hintPath)
        {
            if (Path.IsPathRooted(hintPath) && File.Exists(hintPath)) return hintPath;

            if (!string.IsNullOrEmpty(_shellRunner.WorkingDirectory))
            {
                string byHint = Path.Combine(_shellRunner.WorkingDirectory,
                    hintPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(byHint)) return byHint;

                string byName = Path.Combine(_shellRunner.WorkingDirectory, fileNameOnly);
                if (File.Exists(byName)) return byName;

                try
                {
                    string[] found = Directory.GetFiles(_shellRunner.WorkingDirectory, fileNameOnly,
                        SearchOption.AllDirectories);
                    string[] clean = found.Where(f => !ContextEngine.IsNoisePath(f)).ToArray();
                    if (clean.Length == 1) return clean[0];
                    if (clean.Length > 1)
                    {
                        // Prefer the path whose suffix matches the hint
                        string normalized = hintPath.Replace('\\', '/');
                        string best = clean.FirstOrDefault(f =>
                            f.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
                        return best ?? clean[0];
                    }
                }
                catch { }
            }

            return null;
        }

        private string BuildFileNotFoundMessage(string directive, string filename)
        {
            const int MaxFiles = 50;
            string searchDir = _shellRunner.WorkingDirectory;

            List<string> csFiles = null;
            try
            {
                csFiles = Directory.GetFiles(searchDir, "*.cs", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { }

            if (csFiles == null || csFiles.Count == 0)
                return $"{directive}: file not found — {filename}";

            var sb = new StringBuilder();
            sb.AppendLine($"{directive}: file not found — {filename}");
            sb.AppendLine("Project files:");
            int shown = Math.Min(csFiles.Count, MaxFiles);
            for (int i = 0; i < shown; i++) sb.AppendLine($"  {csFiles[i]}");
            if (csFiles.Count > MaxFiles) sb.AppendLine($"  ... and {csFiles.Count - MaxFiles} more");
            return sb.ToString().TrimEnd('\r', '\n');
        }

        // Applies resolved patch blocks to OriginalContent in memory — used by ShowDiffPreviewAsync
        // to generate the before/after diff without writing to disk.
        private static string ComputePatchedContent(PatchResolveResult r)
        {
            var blocks = r.ResolvedBlocks
                .OrderByDescending(b => b.origStart)
                .ToList();
            string updated = r.OriginalContent;
            foreach (var (origStart, origEnd, finalReplace) in blocks)
                updated = updated.Substring(0, origStart) + finalReplace + updated.Substring(origEnd);
            return updated;
        }

        private void CaptureFileSnapshot(string fullPath)
        {
            if (_fileSnapshots.ContainsKey(fullPath)) return;
            try { _fileSnapshots[fullPath] = File.ReadAllText(fullPath); }
            catch { }
        }

        private static string SafeGetFileName(string path)
        {
            try { return Path.GetFileName(path.Replace('\\', '/')); }
            catch { return path; }
        }

        /// <summary>
        /// Canonical <see cref="_fileCache"/> key: the FULL path, never the bare file
        /// name. Repos routinely hold many same-named files (Program.cs); bare-name keys
        /// let a FIND scan of one Program.cs poison GREP/READ/merge-base of every other
        /// one — the job-8 false-negative postmortem (grep of Parsely.Api/Program.cs
        /// served ParselyExtractionHarness/Program.cs cached by an earlier **/*.cs FIND).
        /// </summary>
        private static string FileCacheKey(string fullPath)
        {
            try { return Path.GetFullPath(fullPath); }
            catch { return fullPath; }
        }

        // ── Private async helpers ─────────────────────────────────────────────────

        private async Task<string> LoadFileContentCoreAsync(
            string fileName, int rangeStart, int rangeEnd, bool forceFullRead)
        {
            try
            {
                string fileNameOnly = SafeGetFileName(fileName);
                string fullPath = FindFile(fileNameOnly, fileName.Replace('\\', '/'));

                if (fullPath == null || !File.Exists(fullPath))
                {
                    AppendOutput($"[READ] File not found: {fileName}\n", OutputColor.Warning);
                    return BuildFileNotFoundMessage("READ", fileName);
                }

                CaptureFileSnapshot(fullPath);

                if (rangeStart > 0)
                {
                    string cacheKey = FileCacheKey(fullPath);
                    _fileCache.InvalidateIfStale(cacheKey, fullPath); // out-of-band writes
                    if (!_fileCache.Contains(cacheKey))
                    {
                        var (diskContent, _) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                        _fileCache.Store(cacheKey, diskContent);
                    }

                    _taskReadFiles.Add(fileNameOnly);
                    _patchesSinceRead.Remove(fileNameOnly); // model refreshed its view
                    int totalLines = _fileCache.GetLineCount(cacheKey);

                    if (rangeStart > rangeEnd) { int t = rangeStart; rangeStart = rangeEnd; rangeEnd = t; }
                    int clampedEnd   = Math.Min(rangeEnd,   totalLines);
                    int clampedStart = Math.Max(1, rangeStart);

                    string rangeContent = _fileCache.GetLineRange(cacheKey, clampedStart, clampedEnd);
                    if (rangeContent == null)
                    {
                        AppendOutput($"[READ] Range {rangeStart}-{rangeEnd} out of bounds for {fileNameOnly} ({totalLines} lines)\n", OutputColor.Error);
                        return $"[READ] Range {rangeStart}-{rangeEnd} out of bounds for {fileNameOnly} ({totalLines} lines)";
                    }

                    var rawLines = rangeContent.Split('\n');
                    var numbered = new StringBuilder();
                    for (int i = 0; i < rawLines.Length; i++)
                        numbered.AppendLine($"{clampedStart + i}: {rawLines[i].TrimEnd('\r')}");

                    bool clamped = clampedEnd < rangeEnd;
                    string rangeBlock = ContextEngine.RenderReadRangeBlock(
                        fileNameOnly, clampedStart, clampedEnd, totalLines, numbered.ToString(), clamped);

                    AppendOutput(
                        $"[READ] {fileNameOnly}:{clampedStart}-{clampedEnd} ({clampedEnd - clampedStart + 1} lines){(clamped ? " [clamped]" : "")}\n",
                        OutputColor.Success);
                    return rangeBlock;
                }

                // Full / outline path
                var (content, _enc) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                _fileCache.Store(FileCacheKey(fullPath), content);
                _taskReadFiles.Add(fileNameOnly);
                _patchesSinceRead.Remove(fileNameOnly); // model refreshed its view
                int lineCount = content.Split('\n').Length;

                bool alreadyRead = _filesRead.Contains(fileNameOnly);
                _filesRead.Add(fileNameOnly);

                string rendered = ContextEngine.RenderReadBlock(
                    fileNameOnly, content, lineCount, forceFullRead, alreadyRead, out bool wasOutline);

                AppendOutput(wasOutline
                    ? $"[READ] {fullPath} ({lineCount} lines — outline{(alreadyRead ? ", re-read" : "")})\n"
                    : $"[READ] Loaded {fullPath} ({lineCount} lines)\n",
                    OutputColor.Success);

                return rendered;
            }
            catch (Exception ex)
            {
                AppendOutput($"[READ ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return $"[ERROR reading {fileName}: {ex.Message}]";
            }
        }

        private async Task<string> LoadGitContentAsync(string fileName, int rangeStart)
        {
            string gitRoot = FindGitRoot();
            if (gitRoot == null)
            {
                AppendOutput("[READ] git: not a git repository\n", OutputColor.Error);
                return "[READ] git: not a git repository\n";
            }

            string command, header;

            if (fileName.StartsWith("git log", StringComparison.OrdinalIgnoreCase))
            {
                int count;
                if (rangeStart > 0)
                {
                    count = rangeStart;
                }
                else
                {
                    string countPart = fileName.Substring("git log".Length).Trim();
                    count = 10;
                    if (!string.IsNullOrEmpty(countPart)) int.TryParse(countPart, out count);
                }
                count = Math.Max(1, Math.Min(count, 50));
                command = $"git log --oneline --no-decorate -{count}";
                header  = $"[READ] git log (last {count} commits)";
            }
            else if (fileName.StartsWith("git diff", StringComparison.OrdinalIgnoreCase))
            {
                string diffArgs = fileName.Substring("git diff".Length).Trim();
                command = string.IsNullOrEmpty(diffArgs) ? "git diff" : $"git diff {diffArgs}";
                header  = string.IsNullOrEmpty(diffArgs) ? "[READ] git diff (working changes)" : $"[READ] git diff {diffArgs}";
            }
            else
            {
                string errMsg = $"[READ] Unrecognized git command: {fileName}";
                AppendOutput(errMsg + "\n", OutputColor.Error);
                return errMsg + "\n";
            }

            string savedDir = _shellRunner.WorkingDirectory;
            _shellRunner.ChangeDirectory(gitRoot);
            string output;
            int exitCode;
            try
            {
                (output, exitCode) = await _shellRunner.ExecuteAsync(command, CancellationToken);
            }
            finally
            {
                _shellRunner.ChangeDirectory(savedDir);
            }

            if (exitCode != 0)
            {
                string errMsg = $"{header}\n(error — exit code {exitCode})\n{output}\n";
                AppendOutput(errMsg, OutputColor.Error);
                return errMsg;
            }

            const int MaxDiffLines = 500;
            string[] outputLines = output.Split('\n');
            string truncatedOutput;
            if (outputLines.Length > MaxDiffLines)
            {
                int omitted = outputLines.Length - MaxDiffLines;
                truncatedOutput = string.Join("\n", outputLines.Take(MaxDiffLines))
                    + $"\n[... {omitted} lines omitted — use READ git diff <filename> for specific files]";
            }
            else
            {
                truncatedOutput = output;
            }

            if (string.IsNullOrWhiteSpace(truncatedOutput)) truncatedOutput = "(no output)";

            AppendOutput($"{header}\n", OutputColor.Success);
            return $"{header}\n```\n{truncatedOutput}\n```\n\n";
        }

        private string FindGitRoot()
        {
            string dir = _shellRunner.WorkingDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
                string parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            return null;
        }
    }
}
