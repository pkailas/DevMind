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
        /// <summary>Action kind: shell | save | append | patch | delete | rename | test | conflict | mcp | mcp_restart (and blocked, steer*, nudge).</summary>
        public string Kind { get; set; }
        /// <summary>Human-readable summary (path, command, exit code...).</summary>
        public string Detail { get; set; }
        public bool Success { get; set; }
    }

    /// <summary>
    /// A host that keeps an action journal (the headless <see cref="BufferedAgenticHost"/>; the
    /// TUI has none — its transcript is the record). A capability, not an IAgenticHost member:
    /// the executor journals through it when the host has one (MCP calls, which run in the
    /// executor rather than inside a host operation), and does nothing otherwise.
    /// </summary>
    public interface IActionJournal
    {
        void RecordAction(string kind, string detail, bool success);
    }

    /// <summary>
    /// UI-agnostic implementation of <see cref="IAgenticHost"/> with buffered output and
    /// auto-approving interaction defaults (safe for headless use). Interactive hosts
    /// subclass and override the prompt virtuals; see file header.
    /// </summary>
    public class BufferedAgenticHost : IAgenticHost, IActionJournal
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

        // Locality-aware companion to _patchesSinceRead: line spans edited since the last read of a
        // file, kept in CURRENT-content space (shifted as later patches change line counts). Only a
        // patch that OVERLAPS/ADJOINS a prior edited span advances _patchesSinceRead — distant,
        // non-overlapping edits are safe (unique FIND text, no drift) and must NOT force a re-read.
        private readonly Dictionary<string, List<(int start, int end)>> _editedSpansSinceRead =
            new Dictionary<string, List<(int start, int end)>>(StringComparer.OrdinalIgnoreCase);

        // Edits closer than this many lines to a prior edit count as overlapping (adjacency tolerance).
        private const int PatchAdjacencyLines = 3;
        // Lines of surrounding context echoed back to the model after a patch (post-patch view).
        private const int PatchEchoContextLines = 6;

        // Pending post-patch context echoes, keyed by full path; drained by TakePatchContextEcho.
        private readonly Dictionary<string, string> _pendingPatchEcho =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Files (bare names) whose [READ:] block was recalled from the nearline cache while a
        // fresher version existed on disk (edited since the model's last read, or mtime newer than
        // the cache entry). Cleared on the next real read. A patch that then fails to resolve on one
        // of these files gets a specific "your FIND came from a stale recall" diagnostic instead of
        // the generic one — field evidence (job-1387): the model recalled a pre-patch read, built
        // FIND from it, and on failure fell back to a PowerShell line-index rewrite.
        private readonly HashSet<string> _staleRecallsSinceRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);


        private readonly ShellRunner _shellRunner;
        private readonly FileContentCache _fileCache = new FileContentCache();

        // Tracks which filenames have been read this session — controls outline vs. full on re-read.
        private readonly HashSet<string> _filesRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Tracks which files (resolved full paths) were accessed during the current user turn — feeds write guard.
        private readonly TaskReadSet _taskReadFiles = new TaskReadSet();

        // Baseline content keyed by full path, captured before first mutation — powers DIFF.
        private readonly FileSnapshotStore _fileSnapshots = new FileSnapshotStore();

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

        // The in-flight run_shell / run_build / run_tests call's interrupt window.
        // CancelInFlightShell (MCP request thread) ends the call while the loop awaits it
        // (worker thread).
        private readonly ShellCallInterrupt _shellInterrupt =
            new ShellCallInterrupt(ShellCallInterrupt.DriverSteerMessage);

        /// <summary>Tool result line for a shell call ended by <see cref="CancelInFlightShell"/>.</summary>
        public const string OverrideSteerCancelMessage = ShellCallInterrupt.DriverSteerMessage;

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
        /// The delegating caller set allow_commit (H-48): the headless shell guard then lets
        /// <c>git add</c> / <c>git commit</c> through. Every other mutating git command stays
        /// blocked. Only consulted when <see cref="RestrictWritesToWorkingDirectory"/> is on.
        /// </summary>
        public bool AllowCommit { get; set; }

        /// <summary>
        /// When true (set only by headless delegation that requested no_execute), this
        /// host refuses to spawn a process on any of its execution surfaces: run_shell
        /// commands matching the <c>LoopHelpers.IsExecutableCommand</c> denylist, run_tests
        /// (the test host is itself a running process — the hung-test-host incident is why
        /// this is blocked, not carve-out-able), and debug launch/attach. Build commands
        /// (dotnet build, npm run build, …) are NOT execution and stay allowed. Default
        /// false: the false path is byte-for-byte the pre-change behavior — these are
        /// guard clauses only, nothing else in these methods moves. Not a sandbox: the
        /// denylist is named and bypassable; the guarantee is that these three surfaces
        /// are gated, said plainly in the error text the model sees.
        /// </summary>
        public bool NoExecute { get; set; }

        /// <summary>Headless stall-watchdog record (H-37), set per turn by HeadlessSession.
        /// A shell/test command holds the stall clock for its own timeout while it runs, and
        /// each streamed shell line is a progress tick. Null outside a headless job.</summary>
        public JobLiveness Liveness { get; set; }

        /// <summary>Standard text for a caller-imposed no_execute block: names the
        /// restriction as the caller's, corrects the false premise (DevMind CAN run
        /// programs — this task may not), and prescribes the only next step (build,
        /// never a workaround that would re-trigger execution).</summary>
        private static string NoExecuteBlockMessage(string surface)
        {
            return "[BLOCKED] " + surface + " is not permitted for THIS task: the delegating caller "
                + "set no_execute, so no process may be started. This is a restriction the caller "
                + "applied to this specific task, not a missing DevMind capability — DevMind runs "
                + "executables all the time, they are simply disallowed for this job. Still allowed: "
                + "build commands (dotnet build / run_build) for compile verification. Do NOT try a "
                + "workaround that would start a process. Verify by building, and note in your final "
                + "summary that execution was blocked by the caller.";
        }

        /// <summary>
        /// Headless shell blocklist. Field evidence (job-11): a confused agent ran
        /// "git show HEAD~1:file | Set-Content file", overwriting a working-tree file
        /// and destroying two earlier tasks' uncommitted changes; another job taskkilled
        /// the operator's running API to unblock a build. Recovery-from-git and process
        /// control are reserved for the human operator in delegated runs.
        /// <para>
        /// H-59: reading git objects is allowed — <c>git show HEAD:path</c> to the console, into a
        /// variable or piped to Select-String / Compare-Object. Only a write of that content to a
        /// file inside <paramref name="workingDirectory"/> is blocked (<see cref="GitWriteGuard"/>);
        /// the old test ("git show" plus any "&gt;" anywhere) blocked regexes and quoted strings.
        /// </para>
        /// <para>
        /// H-48: git subcommands that change the repo, index, refs or working tree (stash, checkout,
        /// reset, commit, push, branch creation, config writes, ...) are refused too — job-1862 ran
        /// <c>git stash</c> / <c>git stash pop</c> around a base-suite run. <paramref name="allowCommit"/>
        /// lets add/commit through.
        /// </para>
        /// </summary>
        internal static bool IsBlockedHeadlessCommand(string command, out string reason, string workingDirectory = null, bool allowCommit = false)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(command)) return false;
            string c = command.ToLowerInvariant();

            bool has(string s) => c.Contains(s, StringComparison.Ordinal);

            // Substring backstop for restore-type commands, kept from the job-11 rule: it also
            // catches them inside a nested `cmd /c "..."` or `powershell -Command "..."`.
            if (has("git restore") || has("git checkout --") || has("git checkout .")
                || has("git checkout head") || has("git reset --hard") || has("git clean"))
            {
                reason = GitWriteGuard.RestoreReason;
                return true;
            }

            reason = GitWriteGuard.Classify(command, workingDirectory, allowCommit);
            if (reason != null) return true;

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

        /// <summary>
        /// Public audit seam for the steer feature (RecordAction is private and only
        /// invoked from within the host's own mutating operations, so the loop cannot log
        /// through it). Records a steer's disposition in the SAME action journal as the
        /// host's mutating operations, using distinct kinds so a result that changed
        /// because of an injection at iteration N is explainable from the journal alone:
        /// "steer" (consumed), "steer_rejected", or "steer_unconsumed".
        /// </summary>
        public void RecordSteer(string message, SteerMode mode, SteerDisposition disposition, string reason = null)
        {
            string kind = disposition switch
            {
                SteerDisposition.Consumed   => "steer",
                SteerDisposition.Rejected   => "steer_rejected",
                SteerDisposition.Unconsumed => "steer_unconsumed",
                _ => "steer",
            };
            string modeTag = mode == SteerMode.Override ? "override" : "suggest";
            string detail = reason is null
                ? $"[{modeTag}] {message}"
                : $"[{modeTag}] {message} — {reason}";
            bool success = disposition == SteerDisposition.Consumed;
            RecordAction(kind, detail, success);
        }

        /// <summary>
        /// Cancels the in-flight run_shell / run_build / run_tests call, if there is one, so
        /// the loop reaches its next iteration boundary now instead of when the command ends
        /// or times out (an override steer must not wait out a runaway command). Only the
        /// call is cancelled — never <see cref="CancellationToken"/>, so the job keeps
        /// running. Returns whether a call was cancelled. Thread-safe: called from the MCP
        /// request thread while the loop runs the call on the worker thread.
        /// </summary>
        public bool CancelInFlightShell(string reason) => _shellInterrupt.Cancel(reason);

        // Rewrites a steer-cancelled call's result so the model reads why it ended, and
        // journals it like any other ended call.
        private string ReportSteerCancel(string kind, string command, string reason, string output)
        {
            RecordAction(kind, string.IsNullOrEmpty(reason)
                ? $"{command} (cancelled by override steer)"
                : $"{command} (cancelled by override steer: {reason})", success: false);
            AppendOutput(OverrideSteerCancelMessage + "\n", OutputColor.Dim);
            return _shellInterrupt.ResultFor(output);
        }

        /// <summary>
        /// Public audit seam for the harness nudges (<see cref="HarnessNudges"/>): records an
        /// injected nudge in the action journal as kind "nudge", so a change of course at
        /// iteration N is explainable from the journal alone, like a steer.
        /// </summary>
        public void RecordNudge(string message) => RecordAction("nudge", message);

        /// <summary>Audit seam for H-58: the repeated-test-failure note and the thinking it
        /// switches on and off, journaled as kind "harness_note" (devmind_task_result).</summary>
        public void RecordHarnessNote(string message) => RecordAction("harness_note", message);

        /// <summary>The executor's journal seam (MCP calls). Same journal, same entry shape.</summary>
        void IActionJournal.RecordAction(string kind, string detail, bool success) => RecordAction(kind, detail, success);

        /// <summary>
        /// A manager lifecycle notice (an MCP server relaunched, or relaunching stopped): one
        /// line in the transcript, where the job's "[job] mcp: … started" line also lands, and
        /// one "mcp_restart" entry in the journal. Success is false for "not restarted".
        /// </summary>
        public void RecordMcpNotice(McpNotice notice)
        {
            if (notice == null) return;
            AppendOutput(notice.Text + "\n", OutputColor.Warning);
            RecordAction("mcp_restart", notice.Text.StartsWith("[MCP] ", StringComparison.Ordinal) ? notice.Text.Substring(6) : notice.Text,
                success: notice.Kind == McpNoticeKind.Restarted);
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
            DrainPatchBackups();
        }

        /// <summary>
        /// Removes every PATCH backup from the undo stack, deleting each backup
        /// file as it goes. Session-scoped: backups are undo state, and a new
        /// session has no business undoing the last one's patches.
        /// </summary>
        public void DrainPatchBackups()
        {
            while (_patchBackupStack.Count > 0)
            {
                var (_, backupPath) = _patchBackupStack.Pop();
                try { File.Delete(backupPath); } catch { /* a locked or already-deleted backup must not abort the drain */ }
            }
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

        async Task<(int exitCode, string output)> IAgenticHost.RunShellAsync(string command, int? timeoutSeconds, bool detach)
        {
            // no_execute: denylist the execution invocations. Guard clause only — when the
            // flag is false this check is a no-op and the rest of the method is untouched.
            if (NoExecute && LoopHelpers.IsExecutableCommand(command))
            {
                RecordAction("blocked", $"shell (no_execute): {command}", success: false);
                AppendOutput($"[SHELL GUARD] Blocked (no_execute): {command}\n", OutputColor.Error);
                return (1, NoExecuteBlockMessage("execution of shell command '" + command + "'"));
            }

            if (RestrictWritesToWorkingDirectory
                && IsBlockedHeadlessCommand(command, out string blockReason, _shellRunner.WorkingDirectory, AllowCommit))
            {
                RecordAction("blocked", $"shell ({blockReason}): {command}", success: false);
                AppendOutput($"[SHELL GUARD] Blocked ({blockReason}): {command}\n", OutputColor.Error);
                if (blockReason == GitWriteGuard.MutationReason)
                    return (1,
                        $"[BLOCKED] This command is not allowed in delegated tasks: {blockReason}. " +
                        "Do NOT stash, switch branches, reset, commit or otherwise change the repository — " +
                        "the delegating caller handles version control. To compare against the base, read it " +
                        "instead: `git diff`, `git log`, or `git show <rev>:<path>` to the console or a variable.");
                return (1,
                    $"[BLOCKED] This command is not allowed in delegated tasks: {blockReason}. " +
                    "Do NOT restore files from git history (it can destroy uncommitted work from " +
                    "earlier tasks) and do NOT kill processes. To fix a broken file, READ its " +
                    "current content and apply corrective patches instead. Reading history is fine: " +
                    "`git show <rev>:<path>` to the console, into a variable, or piped to " +
                    "Select-String / Compare-Object is allowed — just do not write it into the working tree.");
            }

            AppendOutput($"[SHELL] > {command}\n", OutputColor.Dim);
            JobLiveness liveness = Liveness;
            var progress = new Progress<ShellOutputLine>(line =>
            {
                liveness?.Tick($"shell output: {command}");
                AppendOutput(line.Line + "\n", line.IsError ? OutputColor.Error : OutputColor.Normal);
            });
            string output;
            int exitCode;
            string steerCancel;
            using (liveness?.BeginToolCall($"shell: {command}",
                       TimeSpan.FromSeconds(ShellRunner.ResolveTimeout(command, timeoutSeconds))))
            using (var call = _shellInterrupt.Begin(CancellationToken))
            {
                // The job token still reaps the whole tree on a job cancel; the call's own
                // token is the interrupt, which spares a detach call's detached children.
                (output, exitCode) = await _shellRunner.ExecuteAsync(
                    command, CancellationToken, timeoutSeconds, progress, detach, interruptToken: call.Token);
                steerCancel = call.End();
            }
            if (steerCancel != null)
                return (exitCode, ReportSteerCancel("shell", command, steerCancel, output));
            RecordAction("shell", $"{command} (exit {exitCode})", exitCode == 0);
            return (exitCode, output);
        }

        // ── IAgenticHost.SaveFileAsync ────────────────────────────────────────────

        async Task<string> IAgenticHost.SaveFileAsync(string fileName, string content, bool fromToolCall)
        {
            // H-53: name the cause; Path.Combine(dir, null) reported "(Parameter 'path2')".
            if (string.IsNullOrWhiteSpace(fileName))
            {
                AppendOutput("[FILE ERROR] no file name was provided.\n", OutputColor.Error);
                return null;
            }
            string fileNameOnly = SafeGetFileName(fileName);

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

                // Write guard — AFTER resolution, on the path that will actually be written
                // (bare-name keying let an unread same-named file pass; see TaskReadSet).
                if (!_taskReadFiles.IsKnown(fullPath))
                {
                    bool approved = await ConfirmUnreadFileWriteAsync(fileNameOnly);
                    if (!approved)
                    {
                        AppendOutput($"[WRITE GUARD] File write to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                        return null;
                    }
                    _taskReadFiles.MarkKnown(fullPath);
                }

                if (!IsWriteAllowed(fullPath, "write"))
                    return null;
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // New file — no merge gate needed
                if (!File.Exists(fullPath))
                {
                    fileContent = TextFileFormat.WriteNew(fullPath, fileContent);   // H-30: repo line ending
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
                // Existing file: keep its BOM/encoding and dominant line ending.
                string finalContent = TextFileFormat.WritePreserving(fullPath, merge.MergedText);
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
            if (string.IsNullOrWhiteSpace(fileName))
            {
                AppendOutput("[APPEND ERROR] no file name was provided.\n", OutputColor.Error);
                return null;
            }
            string fileNameOnly = SafeGetFileName(fileName);

            // Block if a conflict is pending
            if (_pendingConflict != null)
            {
                AppendOutput($"[MERGE CONFLICT] Cannot append to \"{fileNameOnly}\" — pending conflict on \"{_pendingConflict.FilePath}\" must be resolved first. Use /resolve accept_proposed, /resolve accept_current, or /resolve cancel.\n", OutputColor.Error);
                return null;
            }

            try
            {
                FileResolution res = FindFile(fileNameOnly, fileName.Replace('\\', '/'));
                string resolvedPath = res.Path
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileName);

                // Write guard — AFTER resolution, on the path that will actually be written
                // (bare-name keying let an unread same-named file pass; see TaskReadSet).
                if (!_taskReadFiles.IsKnown(resolvedPath))
                {
                    bool approved = await ConfirmUnreadFileWriteAsync(fileNameOnly);
                    if (!approved)
                    {
                        AppendOutput($"[WRITE GUARD] File append to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                        return null;
                    }
                    _taskReadFiles.MarkKnown(resolvedPath);
                }

                if (!IsWriteAllowed(resolvedPath, "append"))
                    return null;

                // New file — no merge gate needed
                if (!File.Exists(resolvedPath))
                {
                    string dir = Path.GetDirectoryName(resolvedPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    content = TextFileFormat.WriteNew(resolvedPath, content);       // H-30: repo line ending
                    _fileCache.Store(FileCacheKey(resolvedPath), content);
                    AppendOutput($"[APPEND] Created {fileNameOnly}\n", OutputColor.Success);
                    RecordAction("append", $"{resolvedPath} (created)");
                    return resolvedPath;
                }

                // Existing file — always re-read from disk and refresh cache before merge (requirement #3)
                string currentText = File.ReadAllText(resolvedPath);
                _fileCache.Store(FileCacheKey(resolvedPath), currentText);

                // For append: proposed = currentText + separator + content. The appended text
                // takes the file's dominant line ending, and the write keeps its BOM/encoding.
                var format = TextFileFormat.Detect(resolvedPath);
                string separator = currentText.Length > 0 && !currentText.EndsWith("\n", StringComparison.Ordinal) ? (format.NewLine ?? "\n") : "";
                string proposedText = currentText + separator + format.NormalizeLineEndings(content);

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
                format.Write(resolvedPath, merge.MergedText);
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
                    string written = TextFileFormat.WritePreserving(pc.FilePath, pc.ProposedContent);
                    string fileNameOnly = SafeGetFileName(pc.FilePath);
                    _fileCache.Store(FileCacheKey(pc.FilePath), written);
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
            FileResolution res = FindFile(fileNameOnly, filename.Replace('\\', '/'));
            string resolvedPath = res.Path
                ?? Path.Combine(_shellRunner.WorkingDirectory, filename);

            if (!File.Exists(resolvedPath))
                return Task.FromResult(BuildFileNotFoundMessage("DELETE", filename, res));

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
            FileResolution res = FindFile(oldNameOnly, oldFilename.Replace('\\', '/'));
            string oldPath = res.Path
                ?? Path.Combine(_shellRunner.WorkingDirectory, oldFilename);

            if (!File.Exists(oldPath))
                return Task.FromResult(BuildFileNotFoundMessage("RENAME", oldFilename, res));

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

        /// <summary>Count of pending PATCH backups on the undo stack (public mirror
        /// of the interface member, for skin-side bookkeeping and tests).</summary>
        public int PatchBackupCount => _patchBackupStack.Count;

        // ── IAgenticHost.RecallMemoryAsync ────────────────────────────────────────

        Task<string> IAgenticHost.RecallMemoryAsync(string topic)
        {
            if (_memoryManager == null)
                return Task.FromResult("Memory not available: no working directory");

            // Layered recall (repo-default; "global:<slug>" for the machine-level
            // version). A same-slug collision resolves to the requested layer and
            // appends a visible note — never silently first-matched.
            var result = _memoryManager.RecallTopic(topic);
            if (result == null)
            {
                AppendOutput($"[MEMORY] Topic not found: {topic}\n", OutputColor.Dim);
                return Task.FromResult($"Topic not found: {topic}");
            }

            AppendOutput($"[MEMORY] Recalled: {topic}\n", OutputColor.Dim);
            return Task.FromResult(
                string.IsNullOrEmpty(result.CollisionNote)
                    ? result.Content
                    : result.Content + "\n\n" + result.CollisionNote);
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

            // Machine-level topics, when any exist, are appended in a labelled
            // "global:<slug>" section — byte-identical to the legacy repo-only
            // listing when none do.
            var globalTopics = _memoryManager.ListGlobalTopics();

            string index = _memoryManager.LoadIndex();
            if (string.IsNullOrWhiteSpace(index))
            {
                var topics = _memoryManager.ListTopics();
                if (topics.Count == 0 && globalTopics.Count == 0)
                {
                    AppendOutput("[MEMORY] No memory topics found.\n", OutputColor.Dim);
                    return Task.FromResult("No memory topics found. Use save_memory to create one.");
                }
                var sb = new StringBuilder();
                if (topics.Count > 0)
                {
                    if (globalTopics.Count > 0) sb.AppendLine("Repo topics:");
                    sb.Append(string.Join("\n", topics.Select(t => $"- [{t}]")));
                }
                if (globalTopics.Count > 0)
                    sb.Append(BuildGlobalTopicSection(globalTopics));
                AppendOutput($"[MEMORY] {topics.Count + globalTopics.Count} topic(s) available.\n", OutputColor.Dim);
                return Task.FromResult(sb.ToString().TrimEnd());
            }

            AppendOutput("[MEMORY] Topics listed.\n", OutputColor.Dim);
            return Task.FromResult(globalTopics.Count == 0 ? index : index + BuildGlobalTopicSection(globalTopics));
        }

        /// <summary>
        /// The labelled machine-level topic section for list_memory_topics output
        /// (host-side mirror of the MCP tool's section). Appended only when a global
        /// topic exists, so output stays byte-identical to the legacy repo-only list
        /// otherwise. Model-facing prose — no code parses it.
        /// </summary>
        private static string BuildGlobalTopicSection(List<string> globalTopics)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"Global (machine-level) topics — recall with recall_memory \"{MemoryManager.GlobalTopicPrefix}<slug>\":");
            foreach (var t in globalTopics)
                sb.AppendLine($"- [{MemoryManager.GlobalTopicPrefix}{t}]");
            return sb.ToString().TrimEnd('\n');
        }

        // ── IAgenticHost.SearchMemoryAsync ────────────────────────────────────────

        Task<string> IAgenticHost.SearchMemoryAsync(string pattern)
        {
            if (_memoryManager == null)
                return Task.FromResult("Memory not available: no working directory");

            // BOTH layers: repo topics first, global topics tagged "global:<slug>"
            // so every hit's layer is visible. Byte-identical to the legacy
            // repo-only result when no global topics exist.
            string result = _memoryManager.SearchTopicsAllLayers(pattern);
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
                question, topK, docFilter: null, cancellationToken).ConfigureAwait(false);
        }

        async Task<string> IAgenticHost.QueryLibraryAsync(string question, int topK, string docFilter, CancellationToken cancellationToken)
        {
            var config = TuiConfig.Load();
            AppendOutput($"[LIBRARY] Query: \"{question}\" (doc_filter: {docFilter ?? "-"})\n", OutputColor.Dim);
            return await DocumentLibrarian.QueryAsTextAsync(
                config.LibraryEmbeddingEndpoint, config.LibraryConnectionString,
                question, topK, docFilter, cancellationToken).ConfigureAwait(false);
        }

        // ── IAgenticHost LSP tools (delegate to shared Core LspToolService) ───────

        async Task<string> IAgenticHost.GetDiagnosticsAsync(string filename)
        {
            FileResolution lspRes = ResolveLspPath(filename);
            string fullPath = lspRes.Path;
            if (fullPath == null) return BuildFileNotFoundMessage("get_diagnostics", filename, lspRes);
            AppendOutput($"[LSP] get_diagnostics {SafeGetFileName(fullPath)}\n", OutputColor.Dim);
            return await _lspTools.GetDiagnosticsAsync(fullPath, CancellationToken);
        }

        async Task<string> IAgenticHost.GoToDefinitionAsync(string filename, int line, int character)
        {
            FileResolution lspRes = ResolveLspPath(filename);
            string fullPath = lspRes.Path;
            if (fullPath == null) return BuildFileNotFoundMessage("go_to_definition", filename, lspRes);
            AppendOutput($"[LSP] go_to_definition {SafeGetFileName(fullPath)}:{line}:{character}\n", OutputColor.Dim);
            return await _lspTools.GoToDefinitionAsync(fullPath, line, character, CancellationToken);
        }

        async Task<string> IAgenticHost.FindReferencesAsync(string filename, int line, int character)
        {
            FileResolution lspRes = ResolveLspPath(filename);
            string fullPath = lspRes.Path;
            if (fullPath == null) return BuildFileNotFoundMessage("find_references", filename, lspRes);
            AppendOutput($"[LSP] find_references {SafeGetFileName(fullPath)}:{line}:{character}\n", OutputColor.Dim);
            return await _lspTools.FindReferencesAsync(fullPath, line, character, CancellationToken);
        }

        async Task<string> IAgenticHost.HoverAsync(string filename, int line, int character)
        {
            FileResolution lspRes = ResolveLspPath(filename);
            string fullPath = lspRes.Path;
            if (fullPath == null) return BuildFileNotFoundMessage("hover", filename, lspRes);
            AppendOutput($"[LSP] hover {SafeGetFileName(fullPath)}:{line}:{character}\n", OutputColor.Dim);
            return await _lspTools.HoverAsync(fullPath, line, character, CancellationToken);
        }

        async Task<string> IAgenticHost.FindSymbolAsync(string query, int maxResults, string language, string path)
        {
            AppendOutput($"[LSP] find_symbol \"{query}\"\n", OutputColor.Dim);
            return await _lspTools.FindSymbolAsync(query, maxResults, language, CancellationToken, path);
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

        // ── IAgenticHost Learn tools (delegate to shared Core LearnTools) ─────────────

        async Task<string> IAgenticHost.LearnSearchAsync(string query, int? maxResults)
        {
            AppendOutput($"[LEARN] search: {query}\n", OutputColor.Dim);
            return await LearnTools.LearnSearchAsync(query, maxResults, CancellationToken);
        }

        async Task<string> IAgenticHost.LearnFetchAsync(string url)
        {
            AppendOutput($"[LEARN] fetch: {url}\n", OutputColor.Dim);
            return await LearnTools.LearnFetchAsync(url, CancellationToken);
        }

        async Task<string> IAgenticHost.LearnCodeSearchAsync(string query, int? maxResults)
        {
            AppendOutput($"[LEARN] code_search: {query}\n", OutputColor.Dim);
            return await LearnTools.LearnCodeSearchAsync(query, maxResults, CancellationToken);
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
            // no_execute: debug launch/attach starts a process (netcoredbg + the target app).
            // Guard clause only — placed before the TUI-unavailable check so a blocked headless
            // task sees the caller-imposed reason, and an unrestricted host is byte-for-byte
            // unchanged.
            if (NoExecute)
            {
                RecordAction("blocked", $"debug (no_execute): {command}", success: false);
                AppendOutput($"[DEBUG GUARD] Blocked (no_execute): {command}\n", OutputColor.Error);
                return Task.FromResult(NoExecuteBlockMessage("debug launch/attach"));
            }

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

            // Staleness banner for recalled file reads. Recall is still served (the model may only
            // need orientation), but a read that predates edits must not be used as FIND source.
            string staleNote = BuildStaleRecallNote(content, key);
            if (staleNote != null)
            {
                AppendOutput($"[RECALL] {handle} → {key} ({content.Length} chars) [STALE]\n", OutputColor.Warning);
                return Task.FromResult(staleNote + "\n\n" + content);
            }

            AppendOutput($"[RECALL] {handle} → {key} ({content.Length} chars)\n", OutputColor.Dim);
            return Task.FromResult(content);
        }

        /// <summary>
        /// If <paramref name="content"/> is a recalled [READ:file] block and that file has a fresher
        /// version than the cached one, returns a banner naming the file; otherwise null. Two signals,
        /// either suffices: (1) the file has been patched since the model's last read (then EVERY cached
        /// read of it predates the latest write), or (2) the on-disk mtime is newer than the nearline
        /// entry's store time (out-of-band write after the trim). Also records the file in
        /// <see cref="_staleRecallsSinceRead"/> so a subsequent patch failure can point at the cause.
        /// </summary>
        private string BuildStaleRecallNote(string content, string key)
        {
            string fileNameOnly = ExtractReadBlockFileName(content);
            if (fileNameOnly == null) return null;

            bool editedSinceRead = _editedSpansSinceRead.TryGetValue(fileNameOnly, out var spans) && spans.Count > 0;

            bool diskNewer = false;
            try
            {
                DateTime? cachedAt = NearlineCache?.GetCachedAtUtc(key);
                FileResolution res = FindFile(fileNameOnly, fileNameOnly);
                if (cachedAt.HasValue && res?.Path != null && File.Exists(res.Path))
                    diskNewer = File.GetLastWriteTimeUtc(res.Path) > cachedAt.Value;
            }
            catch { /* best-effort — a failed check must never break recall */ }

            if (!editedSinceRead && !diskNewer) return null;

            _staleRecallsSinceRead.Add(fileNameOnly);
            string why = editedSinceRead
                ? "it has been patched since you last read it"
                : "the file on disk was modified after this content was cached";
            return $"[STALE RECALL] This is an OLD read of {fileNameOnly} — {why}. " +
                   "Do NOT copy FIND text from it; it will not match. Use read_file (full or the affected range) " +
                   "and build patches from that fresh output.";
        }

        /// <summary>
        /// Bare file name from a leading "[READ:name]" or "[READ:name:12-40]" tag, or null if the
        /// content is not a read block.
        /// </summary>
        internal static string ExtractReadBlockFileName(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            int start = content.IndexOf("[READ:", StringComparison.Ordinal);
            if (start < 0 || start > 64) return null; // must be at/near the top, not an incidental mention
            int end = content.IndexOf(']', start);
            if (end < 0) return null;
            string tag = content.Substring(start + 6, end - start - 6).Trim();
            // Range reads render as "name:12-40" — strip the trailing ":start-end".
            int lastColon = tag.LastIndexOf(':');
            if (lastColon > 0)
            {
                string tail = tag.Substring(lastColon + 1);
                if (tail.Length > 0 && tail.All(ch => char.IsDigit(ch) || ch == '-'))
                    tag = tag.Substring(0, lastColon);
            }
            return tag.Length > 0 ? SafeGetFileName(tag) : null;
        }

        Task<string> IAgenticHost.ListCacheAsync()
        {
            if (NearlineCache == null)
                return Task.FromResult("[list_cache] nearline cache is not available in this host.");
            return Task.FromResult(NearlineCache.BuildManifest());
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
        private FileResolution ResolveLspPath(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return null;
            return FindFile(SafeGetFileName(filename), filename.Replace('\\', '/'));
        }

        // ── Read-side tools: thin adapters over FileReadTools ────────────────────
        // read_file / grep_file / find_in_files / list_files / diff_file behave identically
        // on every surface (FileReadTools owns them). What stays here is host bookkeeping:
        // the transcript line, the write-guard set, and the stale-read nudge state.

        private FileReadTools ReadTools => new FileReadTools(FileReadPolicy.Agent,
            _shellRunner.WorkingDirectory, _fileCache, _filesRead, _fileSnapshots, _shellRunner);

        /// <summary>Prints a read-side result's transcript line and returns its text.</summary>
        private string Report(FileReadResult r)
        {
            if (r.LogLine != null) AppendOutput(r.LogLine + "\n", r.LogColor);
            return r.Text;
        }

        // ── IAgenticHost.LoadFileContentAsync ────────────────────────────────────

        async Task<string> IAgenticHost.LoadFileContentAsync(
            string fileName, int rangeStart, int rangeEnd, bool forceFullRead)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                AppendOutput("[READ ERROR] no file name was provided.\n", OutputColor.Error);
                return "[ERROR: read_file: no file name was provided — pass the file path in 'filename'.]";
            }
            if (FileReadTools.IsGitRequest(fileName))
                return Report(await ReadTools.ReadGitAsync(fileName, rangeStart, CancellationToken));

            try
            {
                // The tool call maps an absent start_line/end_line to 0.
                FileReadResult r = ReadTools.Read(fileName,
                    rangeStart > 0 ? rangeStart : null, rangeEnd > 0 ? rangeEnd : null, forceFullRead);
                if (r.FileLoaded)
                {
                    string fileNameOnly = FileReadTools.SafeGetFileName(fileName);
                    _taskReadFiles.MarkKnown(r.ResolvedPath);
                    _patchesSinceRead.Remove(fileNameOnly);      // model refreshed its view
                    _editedSpansSinceRead.Remove(fileNameOnly);  // stale-overlap tracking reset with it
                    _staleRecallsSinceRead.Remove(fileNameOnly); // fresh view supersedes any stale recall
                }
                return Report(r);
            }
            catch (Exception ex)
            {
                AppendOutput($"[READ ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return $"[ERROR reading {fileName}: {ex.Message}]";
            }
        }

        // ── IAgenticHost.GrepFileAsync ────────────────────────────────────────────

        Task<string> IAgenticHost.GrepFileAsync(string pattern, string filename, int? startLine, int? endLine)
        {
            FileReadResult r = ReadTools.Grep(pattern, filename, startLine, endLine);
            if (r.MatchCount > 0) _taskReadFiles.MarkKnown(r.ResolvedPath);
            return Task.FromResult(Report(r));
        }

        // ── IAgenticHost.FindInFilesAsync ─────────────────────────────────────────

        Task<string> IAgenticHost.FindInFilesAsync(string pattern, string globPattern, int? startLine, int? endLine)
            => Task.FromResult(Report(ReadTools.Find(pattern, globPattern, root: null, startLine, endLine)));

        // ── IAgenticHost.ListFilesAsync ───────────────────────────────────────────

        Task<string> IAgenticHost.ListFilesAsync(string glob, bool recursive, CancellationToken cancellationToken)
            => Task.FromResult(Report(ReadTools.List(glob, recursive, root: null, cancellationToken)));

        // ── IAgenticHost.RunTestsAsync ────────────────────────────────────────────
        // v1: raw console output. TRX parsing deferred until ParseTrxSummary moves to Core.

        async Task<string> IAgenticHost.RunTestsAsync(string project, string filter, int? timeoutSeconds)
        {
            // no_execute: the test host is itself a running process (the hung-test-host
            // incident is the reason this surface is gated). Guard clause only.
            if (NoExecute)
            {
                RecordAction("blocked", "test (no_execute)", success: false);
                AppendOutput("[TEST GUARD] Blocked (no_execute): dotnet test\n", OutputColor.Error);
                return NoExecuteBlockMessage("running the test suite (dotnet test)");
            }

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

            // Builds before testing (H-11) and carries the blame-hang guard (H-08).
            string cmd = DotnetTestCommand.CommandLine(project, filter);

            AppendOutput($"[TEST] > {cmd}\n", OutputColor.Dim);

            using var inFlight = Liveness?.BeginToolCall($"test: {cmd}",
                TimeSpan.FromSeconds(ShellRunner.ResolveTimeout(cmd, timeoutSeconds)));
            using var call = _shellInterrupt.Begin(CancellationToken);
            try
            {
                var (output, exitCode) = await _shellRunner.ExecuteAsync(
                    cmd, CancellationToken, timeoutSeconds, interruptToken: call.Token);
                string steerCancel = call.End();
                if (steerCancel != null)
                    return ReportSteerCancel("test", cmd, steerCancel, output);
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
            => Task.FromResult(Report(ReadTools.Diff(filename)));

        // ── IAgenticHost.ResolvePatchAsync ────────────────────────────────────────

        async Task<(PatchResolveResult, string)> IAgenticHost.ResolvePatchAsync(string patchContent, bool fromToolCall)
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
                    return (null, "No filename specified in the PATCH header — the first line must be 'PATCH <filename>'.");
                }

                string normalizedHint = blockFileName.Replace('\\', '/');
                string fileNameOnly   = SafeGetFileName(blockFileName);

                // Resolve file path; load into cache if absent
                FileResolution res = FindFile(fileNameOnly, normalizedHint);
                string fullPath = res.Path
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileNameOnly);

                if (!File.Exists(fullPath))
                {
                    AppendOutput($"[PATCH] File not found: {fullPath}\n", OutputColor.Warning);
                    return (null, $"File not found: {fullPath}");
                }

                // Write guard — AFTER resolution, on the path that will actually be written
                // (bare-name keying let an unread same-named file pass; see TaskReadSet).
                if (!_taskReadFiles.IsKnown(fullPath))
                {
                    bool approved = await ConfirmUnreadFileWriteAsync(fileNameOnly);
                    if (!approved)
                    {
                        AppendOutput($"[WRITE GUARD] Patch to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                        return (null, $"Write guard declined — READ {fileNameOnly} first, then retry the patch against its current content.");
                    }
                    _taskReadFiles.MarkKnown(fullPath);
                }

                if (!IsWriteAllowed(fullPath, "patch"))
                {
                    AppendOutput($"[PATCH] Write not allowed: {fullPath}\n", OutputColor.Error);
                    return (null, $"Write not allowed — {fileNameOnly} is outside the allowed write root. This patch cannot be applied; use another approach.");
                }

                _fileCache.InvalidateIfStale(FileCacheKey(fullPath), fullPath); // out-of-band writes
                if (!_fileCache.Contains(FileCacheKey(fullPath)))
                {
                    AppendOutput($"[AUTO-READ] Loading {fileNameOnly} before patch...\n", OutputColor.Dim);
                    var (cached, _enc) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                    _fileCache.Store(FileCacheKey(fullPath), cached);
                    _filesRead.Add(FileReadTools.FileKey(fullPath)); // read-set is keyed by full path
                    _taskReadFiles.MarkKnown(fullPath); // same keying as the guard above
                }

                CaptureFileSnapshot(fullPath);

                var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                var patchResult = PatchEngine.ResolvePatch(patchContent, fullPath, blockFileName, content, encoding,
                    fromToolCall, AppendOutput);
                if (patchResult != null) return (patchResult, null);

                // Name the likely cause when the model's source text is known to be stale: it either
                // recalled an old read of this file, or patched it since its last read. Either way the
                // fix is a fresh read — say so explicitly, and rule out the shell-edit workaround.
                bool staleRecall = _staleRecallsSinceRead.Contains(fileNameOnly);
                bool editedSinceRead = _editedSpansSinceRead.TryGetValue(fileNameOnly, out var spans) && spans.Count > 0;
                if (staleRecall || editedSinceRead)
                {
                    string source = staleRecall
                        ? "you recalled an OLD read of this file from the cache"
                        : $"{_patchesSinceRead.GetValueOrDefault(fileNameOnly)} patch(es) were applied to it since you last read it";
                    return (null,
                        $"FIND/REPLACE could not be resolved against the CURRENT {fileNameOnly} — {source}, so your FIND text " +
                        "is probably copied from a stale copy. Fix: read_file the affected range now and copy FIND verbatim from that " +
                        "output, then retry. Do not work around this with shell-based line edits.");
                }

                return (null,
                    "FIND/REPLACE could not be resolved against the file (FIND text not matching, ambiguous, or no-op). The diagnostic above names the cause; act on it — do not retry the identical patch.");
            }
            catch (Exception ex)
            {
                AppendOutput($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return (null, $"Resolve error: {ex.Message}");
            }
        }

        // ── IAgenticHost.ApplyResolvedPatchAsync ──────────────────────────────────

        Task<(string, string)> IAgenticHost.ApplyResolvedPatchAsync(PatchResolveResult resolved)
        {
            try
            {
                // Block if a conflict is pending
                if (_pendingConflict != null)
                {
                    AppendOutput($"[MERGE CONFLICT] Cannot apply patch — pending conflict on \"{_pendingConflict.FilePath}\" must be resolved first. Use /resolve accept_proposed, /resolve accept_current, or /resolve cancel.\n", OutputColor.Error);
                    return Task.FromResult<(string, string)>((null,
                        $"Blocked by a pending merge conflict on {_pendingConflict.FilePath} — that conflict must be resolved before this patch can apply."));
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
                    return Task.FromResult<(string, string)>((null,
                        $"Merge conflict — the file changed outside this patch since it was read. The conflict regions are shown above; re-READ {fileNameOnly} and re-issue the patch against its current content."));
                }

                // No conflicts — apply patch to disk
                string backupDir = Path.Combine(Path.GetTempPath(), "DevMind");
                var result = PatchEngine.ApplyPatch(resolved, backupDir);

                if (!result.Success)
                {
                    AppendOutput($"[PATCH] Error: {result.Error}\n", OutputColor.Error);
                    // H-34: a non-landing edit is reported as what it is ("edit N did not land …"),
                    // never as a write failure the model might simply retry.
                    return Task.FromResult<(string, string)>((null,
                        result.NotLanded ? result.Error : $"Write failed: {result.Error}"));
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

                // This line deliberately reports no backup-stack depth. The stack is an internal
                // safety net that is only ever pushed to, evicted from, and drained — nothing
                // restores from it, and there is no operator command or tool that can. Naming a
                // depth here told the model it held N reversals it had no way to spend.
                AppendOutput($"[PATCH] Applied to {resolved.FullPath}{(merge.UsedFallback ? " [two-way fallback]" : "")}\n",
                    OutputColor.Success);
                RecordAction("patch", resolved.FullPath);

                // Locality-aware staleness accounting (#1) + fresh post-patch context echo (#2).
                // Headless only — the guard and echo are for the no-human-in-the-loop path.
                if (RestrictWritesToWorkingDirectory)
                    UpdatePatchLocalityAndEcho(fileNameOnly, resolved, result.UpdatedContent);

                return Task.FromResult((resolved.FullPath, (string)null));
            }
            catch (Exception ex)
            {
                AppendOutput($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return Task.FromResult<(string, string)>((null, $"Apply error: {ex.Message}"));
            }
        }


        // ── Patch locality guard (#1) + post-patch context echo (#2) ──────────────

        /// <summary>Returns and clears the pending post-patch context echo for a path.</summary>
        string IAgenticHost.TakePatchContextEcho(string fullPath)
        {
            if (fullPath != null && _pendingPatchEcho.TryGetValue(fullPath, out string echo))
            {
                _pendingPatchEcho.Remove(fullPath);
                return echo;
            }
            return null;
        }

        /// <summary>
        /// After a successful patch: (1) advances the staleness counter ONLY when the edit
        /// overlaps/adjoins a region already edited since the last read (the precondition for
        /// stale-view corruption) — distant edits are left free; and (2) stages a fresh numbered
        /// window of the NEW content around the edit so the model can patch again without re-reading.
        /// Edited spans are tracked in current-content line space and shifted as edits change lengths.
        /// </summary>
        private void UpdatePatchLocalityAndEcho(string fileNameOnly, PatchResolveResult resolved, string newContent)
        {
            string oldContent = resolved.OriginalContent ?? string.Empty;

            // Aggregate this patch's edited region across all blocks, in OLD-content line space.
            int minStart = int.MaxValue, maxEnd = 0;
            foreach (var (origStart, origEnd, _replace) in resolved.ResolvedBlocks ?? new List<(int, int, string)>())
            {
                int s = LineAtOffset(oldContent, origStart);
                int e = LineAtOffset(oldContent, Math.Max(origStart, origEnd - 1));
                if (s < minStart) minStart = s;
                if (e > maxEnd)   maxEnd   = e;
            }
            if (minStart == int.MaxValue) return; // nothing resolved to a location

            int delta = CountLines(newContent) - CountLines(oldContent);

            var spans = _editedSpansSinceRead.GetValueOrDefault(fileNameOnly) ?? new List<(int start, int end)>();

            // Advance the staleness counter for the first edit to a file (establishes the region)
            // and for any later edit that overlaps/adjoins a prior edit — i.e. the clustered case
            // that drifts a stale view. A later edit far from every prior edit is safe and is left
            // uncounted, so many distinct sites never force a re-read.
            bool overlaps = spans.Any(sp =>
                minStart <= sp.end + PatchAdjacencyLines && sp.start <= maxEnd + PatchAdjacencyLines);
            if (spans.Count == 0 || overlaps)
                _patchesSinceRead[fileNameOnly] = _patchesSinceRead.GetValueOrDefault(fileNameOnly) + 1;

            // Shift prior spans that sit below this edit into NEW-content space, then record this edit.
            var updated = new List<(int start, int end)>(spans.Count + 1);
            foreach (var sp in spans)
                updated.Add(sp.start > maxEnd ? (sp.start + delta, sp.end + delta) : sp);
            int newEditEnd = Math.Max(minStart, maxEnd + delta);
            updated.Add((minStart, newEditEnd));
            _editedSpansSinceRead[fileNameOnly] = updated;

            _pendingPatchEcho[resolved.FullPath] = BuildPatchEcho(fileNameOnly, newContent, minStart, newEditEnd);
        }

        /// <summary>1-based line number containing character <paramref name="offset"/>.</summary>
        private static int LineAtOffset(string content, int offset)
        {
            if (string.IsNullOrEmpty(content) || offset <= 0) return 1;
            if (offset > content.Length) offset = content.Length;
            int line = 1;
            for (int i = 0; i < offset; i++)
                if (content[i] == '\n') line++;
            return line;
        }

        private static int CountLines(string s) =>
            string.IsNullOrEmpty(s) ? 0 : s.Count(c => c == '\n') + 1;

        /// <summary>Numbered window of the new content around an edited line range — the "current
        /// view after your edit" the model can patch against without re-reading.</summary>
        private static string BuildPatchEcho(string fileNameOnly, string newContent, int editStart, int editEnd)
        {
            string[] lines = newContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int total = lines.Length;
            int from = Math.Max(1, editStart - PatchEchoContextLines);
            int to   = Math.Min(total, editEnd + PatchEchoContextLines);

            var sb = new StringBuilder();
            sb.Append($"[POST-PATCH VIEW] {fileNameOnly}:{from}-{to} — current content after your edit; " +
                      "patch again from this without re-reading:\n");
            for (int i = from; i <= to; i++)
                sb.Append($"{i}: {lines[i - 1]}\n");
            return sb.ToString();
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

        /// <summary>
        /// Resolves a filename to its full path. Delegates to the shared
        /// <see cref="FilePathResolver"/> (same rules as the MCP server and TUI skin):
        /// absolute path → hint-relative to workingDir → bare name in workingDir →
        /// git-root fallback (repo-root-relative hints) → recursive basename search
        /// that refuses to guess an arbitrary same-named file.
        /// </summary>
        private FileResolution FindFile(string fileNameOnly, string hintPath)
            => FilePathResolver.Resolve(fileNameOnly, hintPath,
                _shellRunner.WorkingDirectory);

        /// <summary>
        /// "File not found" message built from the caller's OWN resolution — no second
        /// resolve (which used to re-run git-root discovery plus the AllDirectories
        /// scan for no new information). Never presents the working directory's
        /// top-level files as the project — the model reads that as ground truth and
        /// concludes source files are missing (job-471).
        /// </summary>
        private string BuildFileNotFoundMessage(string directive, string filename, FileResolution resolution)
            => FilePathResolver.BuildFileNotFoundMessage(directive, filename, resolution);

        /// <summary>
        /// Fallback for call sites that only hold a path — delegates to the shared
        /// re-resolving overload, whose normalization is guarded so an empty or
        /// invalid-character filename yields a clean "not found" instead of throwing
        /// (SafeGetFileName already tolerated such input at the first-resolve sites).
        /// </summary>
        private string BuildFileNotFoundMessage(string directive, string filename)
            => FilePathResolver.BuildFileNotFoundMessage(directive, filename,
                _shellRunner.WorkingDirectory);

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

        private void CaptureFileSnapshot(string fullPath) => _fileSnapshots.Capture(fullPath);

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
    }
}
