// File: TuiAgenticHost.cs  v1.4 (SPIKE)
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Terminal.Gui v2 implementation of IAgenticHost.
// Cribbed from ConsoleAgenticHost — file/shell/patch/memory logic is identical;
// only AppendOutput routes to a Terminal.Gui TextView instead of Console.Write.

// SPIKE: suppress obsolete warnings for Terminal.Gui v2 legacy APIs.
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace DevMind
{
    /// <summary>
    /// Terminal.Gui v2 implementation of IAgenticHost for the DevMind TUI spike.
    /// All file/shell/patch/memory logic cribbed verbatim from ConsoleAgenticHost.
   /// AppendOutput routes to a Terminal.Gui TextView via IApplication.Invoke.
    /// </summary>
    public sealed class TuiAgenticHost : IAgenticHost
    {
        // ── Fields ───────────────────────────────────────────────────────────────

        private readonly ShellRunner _shellRunner;
        private readonly FileContentCache _fileCache = new FileContentCache();

        private readonly HashSet<string> _filesRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _taskReadFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _fileSnapshots =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly MemoryManager _memoryManager;

        private const int PatchBackupStackLimit = 10;
        private readonly Stack<(string filePath, string backupPath)> _patchBackupStack =
            new Stack<(string, string)>();

       private readonly Action _cancelTurn;

        // The Terminal.Gui TextView that receives all output.
        private readonly TextView _outputView;

        // Logical (unwrapped) document position where the next append begins. Valid because
        // the output view is append-only and ALL inserts flow through AppendCore.
        private int _appendRow;
        private int _appendCol;

        // Reflection handles into Terminal.Gui 2.4.4 internals, needed because the wrapped
        // model exposed by the public TextView API is rebuilt (attributes discarded) on every
        // insert — see AppendCore. WordWrapManager and TextModel are internal types, so the
        // member infos are resolved lazily from runtime instances and cached.
        private static readonly FieldInfo WrapManagerField =
            typeof(TextView).GetField("_wrapManager", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo WrapTextModelMethod =
            typeof(TextView).GetMethod("WrapTextModel", BindingFlags.NonPublic | BindingFlags.Instance);
        private static PropertyInfo _wrapModelProperty;      // WordWrapManager.Model
        private static PropertyInfo _textModelCountProperty; // TextModel.Count
        private static MethodInfo _textModelGetLineMethod;   // TextModel.GetLine(int)

        // ── Diagnostics ──────────────────────────────────────────────────────────
        // Set DEVMIND_TUI_DIAG to a file path to trace the color-stamping pipeline
        // (reflection handle resolution, append path taken, spans, exceptions). Inert
        // when unset. Never writes to the UI.

        private static readonly string DiagPath =
            Environment.GetEnvironmentVariable("DEVMIND_TUI_DIAG");
        private static bool _diagHandlesLogged;

        internal static void Diag(string message)
        {
            if (string.IsNullOrEmpty(DiagPath)) return;
            try
            {
                File.AppendAllText(DiagPath,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { }
        }

        private static void DiagHandlesOnce()
        {
            if (_diagHandlesLogged || string.IsNullOrEmpty(DiagPath)) return;
            _diagHandlesLogged = true;
            Diag($"[HANDLES] Terminal.Gui={typeof(TextView).Assembly.GetName().Version} " +
                 $"location={typeof(TextView).Assembly.Location}");
            Diag($"[HANDLES] _wrapManager field: {(WrapManagerField != null ? "OK" : "MISSING")}; " +
                 $"WrapTextModel method: {(WrapTextModelMethod != null ? "OK" : "MISSING")}");
        }

        // Set by the REPL loop before each agentic turn.
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;

        // ── Construction ─────────────────────────────────────────────────────────

        public TuiAgenticHost(string workingDirectory, TextView outputView, Action cancelTurn = null)
        {
            _shellRunner  = new ShellRunner(workingDirectory);
            _outputView   = outputView;
            _cancelTurn   = cancelTurn ?? (() => { });
            if (!string.IsNullOrEmpty(workingDirectory))
                _memoryManager = new MemoryManager(workingDirectory);
        }

        // ── Context lifecycle helpers ────────────────────────────────────────────

        public void ResetTaskContext() => _taskReadFiles.Clear();

        public void ResetSession()
        {
            _filesRead.Clear();
            _fileSnapshots.Clear();
            _fileCache.InvalidateAll();
           _taskReadFiles.Clear();
        }

        // ── IAgenticHost.AppendOutput ─────────────────────────────────────────────

       void IAgenticHost.AppendOutput(string text, OutputColor color)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Terminal.Gui v2: all view mutations must be on the main UI thread.
            // Use the instance-based IApplication.Invoke (not the deprecated static).
            // Invoke marshals to the UI thread via TimedEvents (zero-delay timeouts keyed by
            // a monotonically-increasing unique tick), so callbacks drain strictly FIFO —
            // streamed tokens stay in arrival order.
            //
            // App is null before app.Run() attaches the window (startup banner). At that
            // point we are still on the main thread, so append directly.
            IApplication app = _outputView.App;
            if (app == null)
                AppendCore(text, color);
            else
                app.Invoke(() => AppendCore(text, color));
        }

        // Appends text at the insertion point and stamps the inserted span with the color's
        // per-Cell Attribute. MUST run on the UI thread. All output MUST flow through here
        // (banner, echo, stream tokens, status lines) — _appendRow/_appendCol track the
        // logical (unwrapped) document position of the next append, and any InsertText that
        // bypasses this method desyncs the tracker.
        private void AppendCore(string text, OutputColor color)
        {
            // Normalize line endings. The per-grapheme insert path maps a bare '\r' to
            // Key.Enter, whose handler resets CurrentColumn to 0 WITHOUT adding a line —
            // subsequent cells would be inserted mid-line, garbling the transcript.
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (text.Length == 0) return;

            // Append at the cursor. TextView.InsertText advances the insertion point to
            // the END of the inserted text and calls AdjustViewport()/PositionCursor()
            // per grapheme, so the view auto-scrolls to the newest line on its own.
            //
            // Do NOT reset InsertionPoint afterward. The prior code set it to
            // Point(0, Lines): the setter clamps X=0 to column 0 of the last line, so the
            // NEXT token was inserted at the START of that line — prepending instead of
            // appending. That reversed adjacent fragments during fast streaming
            // ("The" + " project" rendered as " projectThe"). InsertText already leaves
            // the cursor exactly where the next append belongs.
            int startRow = _appendRow;
            int startCol = _appendCol;
            _outputView.InsertText(text);

            // Color (Terminal.Gui 2.4.4): TextView has no public attribute-aware InsertText
            // overload, but its renderer honors a per-Cell Attribute — OnDrawNormalColor
            // does `if (cell.Attribute.HasValue) SetAttribute(cell.Attribute.Value)`. Cell is
            // a record struct in a live List<Cell>, so reassigning an element with
            // `with { Attribute = … }` persists the color into the model.
            //
            // WordWrap (verified against the decompiled 2.4.4 assembly, not from memory):
            // with WordWrap on, TextView maintains TWO models. `_model` (what GetLine/Lines/
            // InsertionPoint expose) is a WRAPPED model that WordWrapManager.WrapModel()
            // REBUILDS FROM SCRATCH after every grapheme insert (InsertText → AdjustViewport →
            // WrapTextModel, taken unconditionally because `flag` starts as NeedsDraw ||
            // _wrapNeeded). Attributes stamped into wrapped cells are therefore discarded on
            // the next insert/resize. The durable store is the UNWRAPPED model
            // (_wrapManager.Model): WrapModel copies each unwrapped cell's Attribute into the
            // rebuilt wrapped rows. So:
            //   1. Stamp the unwrapped model, in logical coordinates tracked by
            //      _appendRow/_appendCol (the wrapped InsertionPoint is useless here).
            //      The span end is read straight off the unwrapped model — insertion is
            //      always at the document end, so end = (Count-1, lastLine.Count). Exact,
            //      no grapheme counting.
            //   2. Force one WrapTextModel() rebuild so the visible wrapped model picks up
            //      the new attributes now — the rebuild that ran inside InsertText happened
            //      BEFORE the stamp, so without this the newest span would render uncolored
            //      until the next append.
            // Neither step touches the insertion point, so in-order streaming is preserved.
            //
            // Known upstream cosmetic quirk (WordWrapManager.WrapModel): when re-applying
            // attributes to wrapped segment j of a logical line it iterates `for (k = j; ...)`
            // and indexes the unwrapped line by k instead of by cumulative segment offset —
            // leaving the first j cells of each continuation row unattributed and shifting
            // colors on mixed-color lines. FixWrappedAttributes() (step 2½ below) re-copies
            // the attributes exactly after each rebuild we control. Rebuilds we cannot hook
            // (window resize) re-introduce the quirk until the next append.
            Terminal.Gui.Drawing.Attribute attr = ResolveAttribute(color);
            DiagHandlesOnce();

            try
            {
                if (_outputView.WordWrap)
                {
                    object model = GetUnwrappedTextModel();
                    if (model == null)
                    {
                        // reflection drift — degrade to monochrome, never mis-stamp
                        Diag($"[APPEND] PATH=wrap-FALLBACK-MONOCHROME color={color} len={text.Length}");
                        return;
                    }

                    int lineCount = (int)_textModelCountProperty.GetValue(model);
                    int endRow = lineCount - 1;
                    var lastLine = (List<Terminal.Gui.Drawing.Cell>)_textModelGetLineMethod
                        .Invoke(model, new object[] { endRow });
                    int endCol = lastLine?.Count ?? 0;

                    StampSpan(
                        row => (List<Terminal.Gui.Drawing.Cell>)_textModelGetLineMethod
                            .Invoke(model, new object[] { row }),
                        lineCount, startRow, startCol, endRow, endCol, attr);

                    WrapTextModelMethod?.Invoke(_outputView, null);
                    FixWrappedAttributes(model);

                    Diag($"[APPEND] PATH=wrap-stamp color={color} len={text.Length} " +
                         $"span=({startRow},{startCol})->({endRow},{endCol}) logicalLines={lineCount}");
                    DiagSampleWrappedTail("POST-STAMP");

                    _appendRow = endRow;
                    _appendCol = endCol;
                }
                else
                {
                    // WordWrap off (e.g. startup banner before the first layout enables it):
                    // _model IS the logical model, so the view's own GetLine/Lines are exact.
                    int lineCount = _outputView.Lines;
                    int endRow = lineCount - 1;
                    int endCol = _outputView.GetLine(endRow)?.Count ?? 0;

                    StampSpan(row => _outputView.GetLine(row),
                        lineCount, startRow, startCol, endRow, endCol, attr);

                    Diag($"[APPEND] PATH=plain-stamp color={color} len={text.Length} " +
                         $"span=({startRow},{startCol})->({endRow},{endCol}) lines={lineCount}");

                    _appendRow = endRow;
                    _appendCol = endCol;
                }
            }
            catch (Exception ex)
            {
                // Keep the app alive — a stamping failure must never take down the UI loop.
                // Text is already inserted; only the color is lost for this span.
                Diag($"[APPEND] EXCEPTION color={color} len={text.Length} ex={ex}");
            }
        }

        // Logs cell/attribute counts for the last few rows of the DISPLAYED (wrapped) model —
        // distinguishes "attributes never landed" from "attributes present but not drawn".
        private void DiagSampleWrappedTail(string tag)
        {
            if (string.IsNullOrEmpty(DiagPath)) return;
            int lines = _outputView.Lines;
            for (int r = Math.Max(0, lines - 3); r < lines; r++)
            {
                List<Terminal.Gui.Drawing.Cell> line = _outputView.GetLine(r);
                if (line == null) continue;
                int attributed = 0;
                string firstAttr = null;
                foreach (Terminal.Gui.Drawing.Cell c in line)
                {
                    if (!c.Attribute.HasValue) continue;
                    attributed++;
                    firstAttr ??= c.Attribute.Value.Foreground.ToString();
                }
                Diag($"[{tag}] viewRow={r} cells={line.Count} attributed={attributed} firstFg={firstAttr ?? "none"}");
            }
        }

        // ── IAgenticHost.RunShellAsync ────────────────────────────────────────────

        async Task<(int exitCode, string output)> IAgenticHost.RunShellAsync(string command)
        {
            AppendOutputLocal($"[SHELL] > {command}\n", OutputColor.Dim);
            var progress = new Progress<ShellOutputLine>(line =>
                AppendOutputLocal(line.Line + "\n", line.IsError ? OutputColor.Error : OutputColor.Normal));
            var (output, exitCode) = await _shellRunner.ExecuteAsync(command, CancellationToken, onLine: progress);
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
                    AppendOutputLocal($"[WRITE GUARD] File write to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                    return null;
                }
                _taskReadFiles.Add(fileNameOnly);
            }

            string fileContent = fromToolCall ? content : PatchEngine.StripOuterCodeFence(content);

            try
            {
                string fullPath = ResolveWritePath(fileName);
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(fullPath, fileContent);
                int lineCount = fileContent.Split('\n').Length;
                AppendOutputLocal($"[FILE] Saved {fileNameOnly} ({lineCount} lines)\n", OutputColor.Success);
                return fullPath;
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[FILE ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return null;
            }
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
                    AppendOutputLocal($"[WRITE GUARD] File append to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                    return null;
                }
                _taskReadFiles.Add(fileNameOnly);
            }

            try
            {
                string resolvedPath = FindFile(fileNameOnly, fileName.Replace('\\', '/'))
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileName);

                if (File.Exists(resolvedPath))
                {
                    string existing = File.ReadAllText(resolvedPath);
                    string separator = existing.Length > 0 && !existing.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "";
                    File.WriteAllText(resolvedPath, existing + separator + content);
                    AppendOutputLocal($"[APPEND] Appended to {fileNameOnly}\n", OutputColor.Success);
                }
                else
                {
                    string dir = Path.GetDirectoryName(resolvedPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(resolvedPath, content);
                    AppendOutputLocal($"[APPEND] Created {fileNameOnly}\n", OutputColor.Success);
                }

                _fileCache.Invalidate(fileNameOnly);
                return resolvedPath;
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[APPEND ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return null;
            }
        }

        // ── IAgenticHost.GetWorkingDirectory ──────────────────────────────────────

        string IAgenticHost.GetWorkingDirectory() => _shellRunner.WorkingDirectory;

        // ── IAgenticHost.UpdateScratchpad ─────────────────────────────────────────

        void IAgenticHost.UpdateScratchpad(string content) { }

        // ── IAgenticHost.DeleteFileAsync ──────────────────────────────────────────

        Task<string> IAgenticHost.DeleteFileAsync(string filename)
        {
            string fileNameOnly = SafeGetFileName(filename);
            string resolvedPath = FindFile(fileNameOnly, filename.Replace('\\', '/'))
                ?? Path.Combine(_shellRunner.WorkingDirectory, filename);

            if (!File.Exists(resolvedPath))
                return Task.FromResult(BuildFileNotFoundMessage("DELETE", filename));

            try
            {
                File.Delete(resolvedPath);
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

            try
            {
                File.Move(oldPath, newPath);
                _fileCache.Invalidate(oldNameOnly);
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
                AppendOutputLocal($"[MEMORY] Topic not found: {topic}\n", OutputColor.Dim);
                return Task.FromResult($"Topic not found: {topic}");
            }

            AppendOutputLocal($"[MEMORY] Recalled: {topic}\n", OutputColor.Dim);
            return Task.FromResult(content);
        }

        // ── IAgenticHost.SaveMemoryAsync ──────────────────────────────────────────

        Task<string> IAgenticHost.SaveMemoryAsync(string topic, string content, string description)
        {
            if (_memoryManager == null)
                return Task.FromResult("Memory not available: no working directory");

            _memoryManager.SaveTopic(topic, content, description);
            string desc = string.IsNullOrEmpty(description) ? topic : description;
            AppendOutputLocal($"[MEMORY] Saved: [{topic}] {desc}\n", OutputColor.Success);
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
                    AppendOutputLocal("[MEMORY] No memory topics found.\n", OutputColor.Dim);
                    return Task.FromResult("No memory topics found. Use save_memory to create one.");
                }
                string list = string.Join("\n", topics.Select(t => $"- [{t}]"));
                AppendOutputLocal($"[MEMORY] {topics.Count} topic(s) available.\n", OutputColor.Dim);
                return Task.FromResult(list);
            }

            AppendOutputLocal("[MEMORY] Topics listed.\n", OutputColor.Dim);
            return Task.FromResult(index);
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

            if (!_fileCache.Contains(fileNameOnly))
            {
                string diskContent;
                try { diskContent = File.ReadAllText(resolvedPath); }
                catch (Exception ex) { return Task.FromResult($"GREP: error reading {filename} — {ex.Message}"); }
                _fileCache.Store(fileNameOnly, diskContent);
            }

            int totalFileLines = _fileCache.GetLineCount(fileNameOnly);
            int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
            int scanEnd   = endLine.HasValue   ? Math.Min(totalFileLines, endLine.Value) : totalFileLines;

            var matches = new List<(int lineNum, string lineText)>();
            for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
            {
               string lineContent = _fileCache.GetLineRange(fileNameOnly, lineNum, lineNum);
                if (lineContent == null) continue;
                if (lineContent.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    matches.Add((lineNum, lineContent));
            }

            if (matches.Count == 0)
            {
                AppendOutputLocal($"[GREP] no matches for \"{pattern}\" in {filename}\n", OutputColor.Dim);
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
            AppendOutputLocal($"[GREP] {totalMatches} match{(totalMatches == 1 ? "" : "es")} for \"{pattern}\" in {filename}\n", OutputColor.Success);
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

            var allMatches = new List<(string fileLabel, int lineNum, string lineText)>();
            bool hitCap = false;

            foreach (string filePath in files)
            {
                if (hitCap) break;
                string fileNameOnly = SafeGetFileName(filePath);

                if (!_fileCache.Contains(fileNameOnly))
                {
                    string diskContent;
                    try { diskContent = File.ReadAllText(filePath); }
                    catch { continue; }
                    _fileCache.Store(fileNameOnly, diskContent);
                }

                int totalFileLines = _fileCache.GetLineCount(fileNameOnly);
                int scanStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
                int scanEnd   = endLine.HasValue   ? Math.Min(totalFileLines, endLine.Value) : totalFileLines;

                for (int lineNum = scanStart; lineNum <= scanEnd; lineNum++)
                {
                   string lineContent = _fileCache.GetLineRange(fileNameOnly, lineNum, lineNum);
                    if (lineContent == null) continue;
                    if (lineContent.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        allMatches.Add((fileNameOnly, lineNum, lineContent));
                        if (allMatches.Count >= MaxMatches) { hitCap = true; break; }
                    }
                }
            }

            if (allMatches.Count == 0)
            {
                AppendOutputLocal($"[FIND] no matches for \"{pattern}\" in {globPattern}\n", OutputColor.Dim);
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

            AppendOutputLocal($"[FIND] {(hitCap ? MaxMatches + "+" : shownCount.ToString())} match{(shownCount == 1 ? "" : "es")} for \"{pattern}\" in {globPattern}\n", OutputColor.Success);
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

            AppendOutputLocal($"[LIST] {shown} file{(shown == 1 ? "" : "s")} matching \"{glob}\"\n", OutputColor.Dim);
            return Task.FromResult(sb.ToString().TrimEnd());
        }

        // ── IAgenticHost.RunTestsAsync ────────────────────────────────────────────

        async Task<string> IAgenticHost.RunTestsAsync(string project, string filter)
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
                        AppendOutputLocal($"[TEST] Auto-detected project: {Path.GetFileName(project)}\n", OutputColor.Dim);
                    }
                    else if (csprojFiles.Length > 1)
                    {
                        project = csprojFiles[0];
                        AppendOutputLocal($"[TEST] Multiple .csproj files found — using {Path.GetFileName(project)}\n", OutputColor.Dim);
                    }
                    else return "[TEST] No project specified and no .csproj found in working directory.";
                }
                catch { return "[TEST] No project specified."; }
            }

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

            AppendOutputLocal($"[TEST] > {cmd}\n", OutputColor.Dim);

            try
            {
                var (output, exitCode) = await _shellRunner.ExecuteAsync(cmd, CancellationToken);
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
                AppendOutputLocal($"[DIFF] {filename}: not modified this session\n", OutputColor.Dim);
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
                AppendOutputLocal($"[DIFF] {filename}: no changes\n", OutputColor.Dim);
                return Task.FromResult($"DIFF: No changes detected in {filename}.");
            }

            string[] oldLines = normOld.Split('\n');
            string[] newLines = normNew.Split('\n');
            string diffResult = DiffHelper.GenerateUnifiedDiff(filename, oldLines, newLines);

            AppendOutputLocal($"[DIFF] {filename}: changes shown ({oldLines.Length} → {newLines.Length} lines)\n", OutputColor.Dim);
            return Task.FromResult(diffResult);
        }

        // ── IAgenticHost.ResolvePatchAsync ────────────────────────────────────────

        async Task<PatchResolveResult> IAgenticHost.ResolvePatchAsync(string patchContent, bool fromToolCall)
        {
            try
            {
                string firstLine = (patchContent ?? string.Empty).Split('\n')[0];
                string blockFileName = firstLine.Length > 5 ? firstLine.Substring(5).Trim() : string.Empty;

                if (string.IsNullOrEmpty(blockFileName))
                {
                    AppendOutputLocal("[PATCH] No filename specified.\n", OutputColor.Error);
                    return null;
                }

                string normalizedHint = blockFileName.Replace('\\', '/');
                string fileNameOnly   = SafeGetFileName(blockFileName);

                if (!IsFileKnownToTask(fileNameOnly))
                {
                    bool approved = await ConfirmUnreadFileWriteAsync(fileNameOnly);
                    if (!approved)
                    {
                        AppendOutputLocal($"[WRITE GUARD] Patch to \"{fileNameOnly}\" blocked.\n", OutputColor.Dim);
                        return null;
                    }
                    _taskReadFiles.Add(fileNameOnly);
                }

                string fullPath = FindFile(fileNameOnly, normalizedHint)
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileNameOnly);

                if (!File.Exists(fullPath))
                {
                    AppendOutputLocal($"[PATCH] File not found: {fullPath}\n", OutputColor.Warning);
                    return null;
                }

                if (!_fileCache.Contains(fileNameOnly))
                {
                    AppendOutputLocal($"[AUTO-READ] Loading {fileNameOnly} before patch...\n", OutputColor.Dim);
                    var (cached, _enc) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                    _fileCache.Store(fileNameOnly, cached);
                    _filesRead.Add(fileNameOnly);
                    _taskReadFiles.Add(fileNameOnly);
                }

                CaptureFileSnapshot(fullPath);

                var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                return PatchEngine.ResolvePatch(patchContent, fullPath, blockFileName, content, encoding,
                    fromToolCall, (text, color) => AppendOutputLocal(text, color));
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return null;
            }
        }

        // ── IAgenticHost.ApplyResolvedPatchAsync ──────────────────────────────────

        Task<string> IAgenticHost.ApplyResolvedPatchAsync(PatchResolveResult resolved)
        {
            try
            {
                string backupDir = Path.Combine(Path.GetTempPath(), "DevMind");
                var result = PatchEngine.ApplyPatch(resolved, backupDir);

                if (!result.Success)
                {
                    AppendOutputLocal($"[PATCH] Error: {result.Error}\n", OutputColor.Error);
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

                _fileCache.Store(SafeGetFileName(resolved.FullPath), result.UpdatedContent);

                int undosAvailable = _patchBackupStack.Count;
                AppendOutputLocal($"[PATCH] Applied to {resolved.FullPath} (undo depth: {undosAvailable})\n",
                    OutputColor.Success);
                return Task.FromResult(resolved.FullPath);
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return Task.FromResult<string>(null);
            }
        }

        // ── IAgenticHost.ShowDiffPreviewAsync ─────────────────────────────────────
        // SPIKE: auto-approve all patches. No interactive y/n/a/q prompt in TUI.

        Task<List<int>> IAgenticHost.ShowDiffPreviewAsync(
            List<PatchResolveResult> resolvedPatches, CancellationToken cancellationToken)
        {
            var approved = new List<int>();

            for (int i = 0; i < resolvedPatches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var r = resolvedPatches[i];
                string badge = r.Confidence == PatchConfidence.Fuzzy ? " [Fuzzy ⚠]" : " [Exact ✓]";

                AppendOutputLocal($"\n[PATCH] {r.FileName}{badge}\n", OutputColor.Dim);
                string patched  = ComputePatchedContent(r);
                string[] oldLns = r.OriginalContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                string[] newLns = patched.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                AppendOutputLocal(DiffHelper.GenerateUnifiedDiff(r.FileName, oldLns, newLns) + "\n",
                    OutputColor.Normal);

                // SPIKE: auto-approve — no interactive prompt in TUI.
                approved.Add(i);
                AppendOutputLocal($"[PATCH] Auto-approved ({i + 1}/{resolvedPatches.Count})\n", OutputColor.Dim);
            }

            return Task.FromResult(approved);
        }

        // ── Private helpers ───────────────────────────────────────────────────────

       internal void AppendOutputLocal(string text, OutputColor color)
        {
            ((IAgenticHost)this).AppendOutput(text, color);
        }

        // ── Color mapping (Terminal.Gui 2.4.4) ────────────────────────────────────
        // Foreground RGB per OutputColor, matching the hex values documented on the
        // OutputColor enum. Background is taken from the view's own Scheme — Program.cs
        // pins the output view's Normal/Editable roles to black, so stamped cells render
        // on the same black background as unstamped cells and fill areas.
        private Terminal.Gui.Drawing.Attribute ResolveAttribute(OutputColor color)
        {
            Terminal.Gui.Drawing.Color fg;
            switch (color)
            {
                case OutputColor.Dim:      fg = new Terminal.Gui.Drawing.Color(0x88, 0x88, 0x88); break; // #888888
                case OutputColor.Input:    fg = new Terminal.Gui.Drawing.Color(0x56, 0x9C, 0xD6); break; // #569CD6
                case OutputColor.Error:    fg = new Terminal.Gui.Drawing.Color(0xF4, 0x47, 0x47); break; // #F44747
                case OutputColor.Success:  fg = new Terminal.Gui.Drawing.Color(0x4E, 0xC9, 0x4E); break; // #4EC94E
                case OutputColor.Thinking: fg = new Terminal.Gui.Drawing.Color(0x6A, 0x6A, 0x8A); break; // #6A6A8A
                case OutputColor.Warning:  fg = new Terminal.Gui.Drawing.Color(0xFF, 0xB9, 0x00); break; // #FFB900
                case OutputColor.Normal:
                default:                   fg = new Terminal.Gui.Drawing.Color(0xCC, 0xCC, 0xCC); break; // #CCCCCC
            }

            Terminal.Gui.Drawing.Color bg = _outputView.GetScheme().Normal.Background;
            return new Terminal.Gui.Drawing.Attribute(fg, bg);
        }

        // Stamps the per-Cell Attribute across the span [start, end) in LOGICAL (unwrapped)
        // line coordinates; newlines do not occupy a cell. getLine returns the live model
        // List<Cell> for a row; reassigning a record-struct Cell element with `with { … }`
        // persists the color. The insertion point is never moved, so in-order streaming
        // append is preserved.
        private static void StampSpan(Func<int, List<Terminal.Gui.Drawing.Cell>> getLine,
            int lineCount, int startRow, int startCol, int endRow, int endCol,
            Terminal.Gui.Drawing.Attribute attr)
        {
            int firstRow = Math.Max(0, startRow);
            int lastRow  = Math.Min(endRow, lineCount - 1);

            for (int row = firstRow; row <= lastRow; row++)
            {
                List<Terminal.Gui.Drawing.Cell> line = getLine(row);
                if (line == null || line.Count == 0) continue;

                int from = (row == startRow) ? startCol : 0;
                int to   = (row == endRow)   ? endCol   : line.Count;
                if (from < 0) from = 0;
                if (to > line.Count) to = line.Count;

                for (int col = from; col < to; col++)
                    line[col] = line[col] with { Attribute = attr };
            }
        }

        // Re-copies span colors into the wrapped model after an externally triggered rebuild
        // (window resize re-wraps via TextView.OnSubViewsLaidOut, re-introducing the upstream
        // attribute-copy quirk). Called from the output view's SubViewsLaidOut handler, which
        // runs after the re-wrap. No-op when WordWrap is off. UI thread only.
        internal void RefreshWrappedColors()
        {
            if (!_outputView.WordWrap) return;
            object model = GetUnwrappedTextModel();
            Diag($"[REFRESH] wordWrap=true model={(model != null ? "OK" : "NULL")} viewLines={_outputView.Lines}");
            if (model != null) FixWrappedAttributes(model);
        }

        // Corrects WordWrapManager.WrapModel's attribute copy (see the "known upstream
        // cosmetic quirk" note in AppendCore) by re-copying attributes from the unwrapped
        // model into the freshly rebuilt wrapped model, exactly. Wrapped rows hold the same
        // cells in the same order as their logical line (one logical line → 1..n contiguous
        // wrapped segments; wrapping never reorders, drops, or merges cells with
        // preserveTrailingSpaces:true), so segments are consumed sequentially per logical
        // line and each wrapped cell takes the attribute of its cumulative-offset source
        // cell. MUST be called immediately after a WrapTextModel rebuild, while
        // _outputView.GetLine still addresses the wrapped model. If the 1:1 correspondence
        // ever breaks (segment longer than the remaining logical cells), the pass aborts
        // rather than smearing colors across wrong cells.
        private void FixWrappedAttributes(object unwrappedModel)
        {
            int logicalCount = (int)_textModelCountProperty.GetValue(unwrappedModel);
            int wrappedCount = _outputView.Lines;
            int wrappedRow = 0;

            for (int li = 0; li < logicalCount && wrappedRow < wrappedCount; li++)
            {
                var logical = (List<Terminal.Gui.Drawing.Cell>)_textModelGetLineMethod
                    .Invoke(unwrappedModel, new object[] { li });
                if (logical == null) return;

                int consumed = 0;
                do
                {
                    List<Terminal.Gui.Drawing.Cell> segment = _outputView.GetLine(wrappedRow);
                    if (segment == null) return;
                    if (consumed + segment.Count > logical.Count)
                    {
                        // mapping skew — abort rather than smear
                        Diag($"[FIX] ABORT skew at logicalLine={li} wrappedRow={wrappedRow} " +
                             $"consumed={consumed} segLen={segment.Count} logicalLen={logical.Count}");
                        return;
                    }

                    for (int k = 0; k < segment.Count; k++)
                        segment[k] = segment[k] with { Attribute = logical[consumed + k].Attribute };

                    consumed += segment.Count;
                    wrappedRow++;
                } while (consumed < logical.Count && wrappedRow < wrappedCount);
            }
        }

        // Resolves the UNWRAPPED TextModel (_wrapManager.Model) via reflection, caching the
        // internal-type member infos on first use. Returns null if WordWrap is not active or
        // any handle cannot be resolved (degrade to monochrome rather than mis-stamp).
        private object GetUnwrappedTextModel()
        {
            if (WrapManagerField == null)
            {
                Diag("[RESOLVE] _wrapManager FieldInfo is null");
                return null;
            }

            object wrapManager = WrapManagerField.GetValue(_outputView);
            if (wrapManager == null)
            {
                Diag("[RESOLVE] _wrapManager instance is null (WordWrap on but no manager?)");
                return null;
            }

            if (_wrapModelProperty == null)
                _wrapModelProperty = wrapManager.GetType()
                    .GetProperty("Model", BindingFlags.Public | BindingFlags.Instance);

            object model = _wrapModelProperty?.GetValue(wrapManager);
            if (model == null)
            {
                Diag($"[RESOLVE] Model property {(_wrapModelProperty == null ? "MISSING on " + wrapManager.GetType().FullName : "returned null")}");
                return null;
            }

            if (_textModelCountProperty == null || _textModelGetLineMethod == null)
            {
                _textModelCountProperty = model.GetType()
                    .GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                _textModelGetLineMethod = model.GetType()
                    .GetMethod("GetLine", BindingFlags.Public | BindingFlags.Instance);
            }

            if (_textModelCountProperty == null || _textModelGetLineMethod == null)
            {
                Diag($"[RESOLVE] TextModel members missing on {model.GetType().FullName}: " +
                     $"Count={(_textModelCountProperty != null ? "OK" : "MISSING")} GetLine={(_textModelGetLineMethod != null ? "OK" : "MISSING")}");
                return null;
            }
            return model;
        }

        private bool IsFileKnownToTask(string fileNameOnly)
            => _taskReadFiles.Contains(fileNameOnly) || _taskReadFiles.Count == 0;

        private Task<bool> ConfirmUnreadFileWriteAsync(string fileNameOnly)
        {
            // SPIKE: auto-approve all writes — no interactive prompt.
            return Task.FromResult(true);
        }

        private string ResolveWritePath(string fileName)
        {
            if (Path.IsPathRooted(fileName)) return fileName;
            return Path.Combine(_shellRunner.WorkingDirectory ?? Directory.GetCurrentDirectory(), fileName);
        }

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
                    AppendOutputLocal($"[READ] File not found: {fileName}\n", OutputColor.Warning);
                    return BuildFileNotFoundMessage("READ", fileName);
                }

                CaptureFileSnapshot(fullPath);

                if (rangeStart > 0)
                {
                    if (!_fileCache.Contains(fileNameOnly))
                    {
                        var (diskContent, _) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                        _fileCache.Store(fileNameOnly, diskContent);
                    }

                    _taskReadFiles.Add(fileNameOnly);
                    int totalLines = _fileCache.GetLineCount(fileNameOnly);

                    if (rangeStart > rangeEnd) { int t = rangeStart; rangeStart = rangeEnd; rangeEnd = t; }
                    int clampedEnd   = Math.Min(rangeEnd,   totalLines);
                    int clampedStart = Math.Max(1, rangeStart);

                    string rangeContent = _fileCache.GetLineRange(fileNameOnly, clampedStart, clampedEnd);
                    if (rangeContent == null)
                    {
                        AppendOutputLocal($"[READ] Range {rangeStart}-{rangeEnd} out of bounds for {fileNameOnly} ({totalLines} lines)\n", OutputColor.Error);
                        return $"[READ] Range {rangeStart}-{rangeEnd} out of bounds for {fileNameOnly} ({totalLines} lines)";
                    }

                    var rawLines = rangeContent.Split('\n');
                    var numbered = new StringBuilder();
                    for (int i = 0; i < rawLines.Length; i++)
                        numbered.AppendLine($"{clampedStart + i}: {rawLines[i].TrimEnd('\r')}");

                    bool clamped = clampedEnd < rangeEnd;
                    string rangeBlock = ContextEngine.RenderReadRangeBlock(
                        fileNameOnly, clampedStart, clampedEnd, totalLines, numbered.ToString(), clamped);

                    AppendOutputLocal(
                        $"[READ] {fileNameOnly}:{clampedStart}-{clampedEnd} ({clampedEnd - clampedStart + 1} lines){(clamped ? " [clamped]" : "")}\n",
                        OutputColor.Success);
                    return rangeBlock;
                }

                var (content, _enc) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                _fileCache.Store(fileNameOnly, content);
                _taskReadFiles.Add(fileNameOnly);
                int lineCount = content.Split('\n').Length;

                bool alreadyRead = _filesRead.Contains(fileNameOnly);
                _filesRead.Add(fileNameOnly);

                string rendered = ContextEngine.RenderReadBlock(
                    fileNameOnly, content, lineCount, forceFullRead, alreadyRead, out bool wasOutline);

                AppendOutputLocal(wasOutline
                    ? $"[READ] {fullPath} ({lineCount} lines — outline{(alreadyRead ? ", re-read" : "")})\n"
                    : $"[READ] Loaded {fullPath} ({lineCount} lines)\n",
                    OutputColor.Success);

                return rendered;
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[READ ERROR] {fileName}: {ex.Message}\n", OutputColor.Error);
                return $"[ERROR reading {fileName}: {ex.Message}]";
            }
        }

        private async Task<string> LoadGitContentAsync(string fileName, int rangeStart)
        {
            string gitRoot = FindGitRoot();
            if (gitRoot == null)
            {
                AppendOutputLocal("[READ] git: not a git repository\n", OutputColor.Error);
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
                AppendOutputLocal(errMsg + "\n", OutputColor.Error);
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
                AppendOutputLocal(errMsg, OutputColor.Error);
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

            AppendOutputLocal($"{header}\n", OutputColor.Success);
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
