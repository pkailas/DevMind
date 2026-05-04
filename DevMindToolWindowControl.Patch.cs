// File: DevMindToolWindowControl.Patch.cs  v7.15  v6.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace DevMind
{
    public partial class DevMindToolWindowControl : UserControl
    {
        // ── VS document reload ────────────────────────────────────────────────

        /// <summary>
        /// Forces VS to reload the in-memory document buffer from disk without showing
        /// the "file changed externally" prompt. Uses IVsPersistDocData.ReloadDocData,
        /// the standard VSSDK pattern for programmatic file reload.
        /// Must be called on the main thread (SwitchToMainThreadAsync before calling).
        /// </summary>
        private static void ReloadDocumentFromDisk(string fullPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var rdt = ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (rdt == null) return;

            uint docCookie;
            IVsHierarchy hierarchy;
            uint itemId;
            IntPtr docDataPtr;
            int hr = rdt.FindAndLockDocument(
                (uint)_VSRDTFLAGS.RDT_NoLock,
                fullPath,
                out hierarchy,
                out itemId,
                out docDataPtr,
                out docCookie);

            if (hr != Microsoft.VisualStudio.VSConstants.S_OK || docDataPtr == IntPtr.Zero)
                return;

            try
            {
                var docData = System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(docDataPtr)
                    as IVsPersistDocData;
                docData?.ReloadDocData(0);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.Release(docDataPtr);
            }
        }

        // ── UNDO command ──────────────────────────────────────────────────────

        private Task ApplyUndoAsync()
        {
            if (_patchBackupStack.Count == 0)
            {
                AppendOutput("[UNDO] Nothing to undo.\n", OutputColor.Error);
                return Task.CompletedTask;
            }

            var (originalPath, backupPath) = _patchBackupStack.Pop();
            try
            {
                if (!File.Exists(backupPath))
                {
                    AppendOutput($"[UNDO] Backup file missing: {backupPath}\n", OutputColor.Error);
                    return Task.CompletedTask;
                }
                File.Copy(backupPath, originalPath, overwrite: true);
                try { File.Delete(backupPath); } catch { }
                _undoCount++;
                int remaining = _patchBackupStack.Count;
                AppendOutput($"[UNDO] Restored {Path.GetFileName(originalPath)} (undo depth remaining: {remaining})\n", OutputColor.Success);
                InputTextBox.Text = "";
            }
            catch (Exception ex)
            {
                AppendOutput($"[UNDO] Failed: {ex.Message}\n", OutputColor.Error);
            }
            return Task.CompletedTask;
        }

        // ── PATCH command (legacy all-in-one path) ────────────────────────────

        // Returns the full path of the file that was patched, or null if patching failed/was deferred.
        private async Task<string> ApplyPatchAsync(string input, bool clearInput = true)
        {
            try
            {
                // Parse first line: "PATCH <filename>"
                var lines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
                string fileName = lines[0].Substring("PATCH ".Length).Trim();
                if (string.IsNullOrEmpty(fileName))
                {
                    AppendOutput("[PATCH] No filename specified.\n", OutputColor.Error);
                    return null;
                }

                // Strip outer markdown fences from body before parsing
                int firstNl = input.IndexOf('\n');
                if (firstNl >= 0)
                {
                    string header = input.Substring(0, firstNl + 1);
                    string body   = PatchEngine.StripMarkdownFenceLines(input.Substring(firstNl + 1));
                    input = header + body;
                }

                var rawBlocks = PatchEngine.ParsePatchBlocks(input);
                if (rawBlocks.Count == 0)
                {
                    AppendOutput("[PATCH] Invalid syntax — must contain at least one FIND: and REPLACE: pair.\n", OutputColor.Error);
                    return null;
                }

                var blocks = rawBlocks;

                // Resolve file path — support partial path hints (e.g. "Services/Foo.cs")
                string normalizedFileName = fileName.Replace('\\', '/');
                string fileNameOnly;
                try
                {
                    fileNameOnly = Path.GetFileName(normalizedFileName);
                }
                catch (ArgumentException diagEx)
                {
                    AppendOutput($"[DEBUG] Path operation failed on string: \"{normalizedFileName}\"\n", OutputColor.Error);
                    AppendOutput($"[PATCH error: {diagEx.Message}]\n", OutputColor.Error);
                    return null;
                }
                string fullPath = await FindFileInSolutionAsync(fileNameOnly, normalizedFileName)
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileNameOnly);

                if (!File.Exists(fullPath))
                {
                    AppendOutput($"[PATCH] File not found: {fullPath}\n", OutputColor.Warning);
                    return null;
                }

                var (content, fileEncoding) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                bool fileUsesCrlf = content.Contains("\r\n");

                // Validate ALL blocks first — all-or-nothing semantics
                var (normContent, normToOrig) = PatchEngine.NormalizeWithMap(content);
                var resolvedBlocks = new List<(int origStart, int origEnd, string replaceText)>();

                for (int i = 0; i < blocks.Count; i++)
                {
                    var (findText, replaceText) = blocks[i];
                    string normFind = Regex.Replace(findText, @"\s+", " ").Trim();
                    if (string.IsNullOrEmpty(normFind))
                    {
                        AppendOutput($"[PATCH] Block {i + 1}: FIND is empty after fence stripping — skipping.\n", OutputColor.Error);
                        continue;
                    }

                    int normIdx = normContent.IndexOf(normFind, StringComparison.Ordinal);
                    int origStart = 0, origEnd = 0;

                    if (normIdx < 0)
                    {
                        var fuzzy = PatchEngine.FindFuzzyMatch(content, findText, normFind);
                        if (fuzzy == null)
                        {
                            AppendOutput($"[PATCH] Block {i + 1}: FIND text not found in {fileName} — no changes made.\n", OutputColor.Error);
                            return null;
                        }
                        int fuzzyLine = content.Substring(0, fuzzy.Value.origStart).Count(c => c == '\n') + 1;
                        string matchedText = content.Substring(fuzzy.Value.origStart, fuzzy.Value.origEnd - fuzzy.Value.origStart).Trim();
                        string fuzzyPreview = matchedText.Length > 120 ? matchedText.Substring(0, 120) + "…" : matchedText;
                        string fuzzyNormReplace = replaceText.Replace("\r\n", "\n");
                        string fuzzyFinalReplace = fileUsesCrlf ? fuzzyNormReplace.Replace("\n", "\r\n") : fuzzyNormReplace;
                        resolvedBlocks.Add((fuzzy.Value.origStart, fuzzy.Value.origEnd, fuzzyFinalReplace));
                        if (_loopState.AgenticDepth > 0)
                        {
                            // Agentic loop — auto-accept without prompting.
                            // continue to skip the tail resolvedBlocks.Add() below (block already added above).
                            AppendOutput($"[PATCH] Agentic auto-accepted fuzzy match at line {fuzzyLine} ({fuzzy.Value.similarity:P0} similarity).\n", OutputColor.Dim);
                            continue;
                        }
                        else
                        {
                            AppendOutput($"[PATCH] Block {i + 1}: Fuzzy match at line {fuzzyLine} ({fuzzy.Value.similarity:P0} similarity).\n", OutputColor.Dim);
                            AppendOutput($"  Matched text: {fuzzyPreview}\n", OutputColor.Dim);
                            // Suspend — keyboard confirmation required
                            _pendingFuzzyPatch = (fullPath, fileEncoding, fileName, content, resolvedBlocks);
                            AppendOutput("\n[FUZZY] Fuzzy match — press 1 to apply or 2 to cancel.\n", OutputColor.Dim);
                            TerminalInputBox.Focus();
                            return null;
                        }
                    }
                    else
                    {
                        // Ambiguity check on exact match
                        int secondNormIdx = normContent.IndexOf(normFind, normIdx + 1, StringComparison.Ordinal);
                        if (secondNormIdx >= 0)
                        {
                            int line1 = content.Substring(0, normToOrig[normIdx]).Count(c => c == '\n') + 1;
                            int line2 = content.Substring(0, normToOrig[secondNormIdx]).Count(c => c == '\n') + 1;
                            AppendOutput(
                                $"[PATCH] Block {i + 1}: Ambiguous FIND — matched at line {line1} and line {line2} in {fileName}. " +
                                $"Add more surrounding context to make the match unique.\n",
                                OutputColor.Error);
                            return null;
                        }
                        origStart = normToOrig[normIdx];
                        while (origStart > 0 && content[origStart - 1] != '\n')
                            origStart--;
                        origEnd = (normIdx + normFind.Length < normToOrig.Length)
                            ? normToOrig[normIdx + normFind.Length]
                            : content.Length;
                    }

                    // Normalize line endings to match file
                    string normalizedReplace = replaceText.Replace("\r\n", "\n");
                    string finalReplace = fileUsesCrlf
                        ? normalizedReplace.Replace("\n", "\r\n")
                        : normalizedReplace;

                    resolvedBlocks.Add((origStart, origEnd, finalReplace));
                }

                if (resolvedBlocks.Count == 0)
                {
                    AppendOutput("[PATCH] No changes resolved — file not modified.\n", OutputColor.Error);
                    return null;
                }

                var resolveResult = new PatchResolveResult
                {
                    FullPath        = fullPath,
                    FileName        = fileName,
                    OriginalContent = content,
                    FileEncoding    = fileEncoding,
                    ResolvedBlocks  = resolvedBlocks
                };
                string backupDir = Path.Combine(Path.GetTempPath(), "DevMind");
                var applyResult = PatchEngine.ApplyPatch(resolveResult, backupDir);
                if (!applyResult.Success)
                {
                    AppendOutput($"[PATCH] Error: {applyResult.Error}\n", OutputColor.Error);
                    return null;
                }

                if (applyResult.BackupPath != null)
                {
                    if (_patchBackupStack.Count >= PatchBackupStackLimit)
                    {
                        var oldest = _patchBackupStack.ToArray().Last();
                        try { File.Delete(oldest.backupPath); } catch { }
                        var entries = _patchBackupStack.ToArray().Reverse().Skip(1).ToArray();
                        _patchBackupStack.Clear();
                        foreach (var e in entries) _patchBackupStack.Push(e);
                    }
                    _patchBackupStack.Push((fullPath, applyResult.BackupPath));
                }

                _llmClient.FileCache.Store(Path.GetFileName(fullPath), applyResult.UpdatedContent);

                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ReloadDocumentFromDisk(fullPath);
                }
                catch { /* non-fatal — file was written, just not auto-reloaded */ }

                int undosAvailable = _patchBackupStack.Count;
                AppendOutput($"[PATCH] Applied to {fullPath} (undo depth: {undosAvailable})\n", OutputColor.Success);
                if (clearInput)
                    InputTextBox.Text = "";
                return fullPath;
            }
            catch (Exception ex)
            {
                AppendOutput($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return null;
            }
        }

        private async Task ApplyPendingFuzzyPatchAsync(
            (string fullPath, Encoding fileEncoding, string fileName, string content, List<(int origStart, int origEnd, string replaceText)> resolvedBlocks) pending)
        {
            try
            {
                var resolveResult = new PatchResolveResult
                {
                    FullPath        = pending.fullPath,
                    FileName        = pending.fileName,
                    OriginalContent = pending.content,
                    FileEncoding    = pending.fileEncoding,
                    ResolvedBlocks  = pending.resolvedBlocks
                };
                string backupDir = Path.Combine(Path.GetTempPath(), "DevMind");
                var applyResult = PatchEngine.ApplyPatch(resolveResult, backupDir);
                if (!applyResult.Success)
                {
                    AppendOutput($"[PATCH] Error: {applyResult.Error}\n", OutputColor.Error);
                    return;
                }

                if (applyResult.BackupPath != null)
                {
                    if (_patchBackupStack.Count >= PatchBackupStackLimit)
                    {
                        var oldest = _patchBackupStack.ToArray().Last();
                        try { File.Delete(oldest.backupPath); } catch { }
                        var entries = _patchBackupStack.ToArray().Reverse().Skip(1).ToArray();
                        _patchBackupStack.Clear();
                        foreach (var e in entries) _patchBackupStack.Push(e);
                    }
                    _patchBackupStack.Push((pending.fullPath, applyResult.BackupPath));
                }

                _llmClient.FileCache.Store(Path.GetFileName(pending.fullPath), applyResult.UpdatedContent);

                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ReloadDocumentFromDisk(pending.fullPath);
                }
                catch { /* non-fatal — file was written, just not auto-reloaded */ }

                int undosAvailable = _patchBackupStack.Count;
                AppendOutput($"[PATCH] Applied to {pending.fullPath} (undo depth: {undosAvailable})\n", OutputColor.Success);
            }
            catch (Exception ex)
            {
                AppendOutput($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
            }
        }

        // ── PATCH resolution (preview gate support) ───────────────────────────

        /// <summary>
        /// Resolves a PATCH block to determine what would change, without applying.
        /// Returns a <see cref="PatchResolveResult"/> with match confidence and
        /// resolved blocks, or null if parsing/matching failed.
        /// Used by the diff preview gate to show a card before committing.
        /// </summary>
        internal async Task<PatchResolveResult> ResolvePatchAsync(string input, bool fromToolCall = false)
        {
            try
            {
                var lines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
                string fileName = lines[0].Substring("PATCH ".Length).Trim();
                if (string.IsNullOrEmpty(fileName))
                {
                    AppendOutput("[PATCH] No filename specified.\n", OutputColor.Error);
                    return null;
                }

                string normalizedFileName = fileName.Replace('\\', '/');
                string fileNameOnly;
                try { fileNameOnly = Path.GetFileName(normalizedFileName); }
                catch (ArgumentException diagEx)
                {
                    AppendOutput($"[DEBUG] Path operation failed on string: \"{normalizedFileName}\"\n", OutputColor.Error);
                    AppendOutput($"[PATCH error: {diagEx.Message}]\n", OutputColor.Error);
                    return null;
                }

                string fullPath = await FindFileInSolutionAsync(fileNameOnly, normalizedFileName)
                    ?? Path.Combine(_shellRunner.WorkingDirectory, fileNameOnly);

                if (!File.Exists(fullPath))
                {
                    AppendOutput($"[PATCH] File not found: {fullPath}\n", OutputColor.Warning);
                    return null;
                }

                var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(fullPath);
                return PatchEngine.ResolvePatch(input, fullPath, fileName, content, encoding, fromToolCall, AppendOutput);
            }
            catch (Exception ex)
            {
                AppendOutput($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return null;
            }
        }

        /// <summary>
        /// Applies a previously resolved patch. Called after the diff preview gate
        /// approves the change. Returns the full path on success, null on failure.
        /// </summary>
        internal async Task<string> ApplyResolvedPatchAsync(PatchResolveResult resolved)
        {
            try
            {
                string backupDir = Path.Combine(Path.GetTempPath(), "DevMind");
                var result = PatchEngine.ApplyPatch(resolved, backupDir);

                if (!result.Success)
                {
                    AppendOutput($"[PATCH] Error: {result.Error}\n", OutputColor.Error);
                    return null;
                }

                if (result.BackupPath != null)
                {
                    if (_patchBackupStack.Count >= PatchBackupStackLimit)
                    {
                        var oldest = _patchBackupStack.ToArray().Last();
                        try { File.Delete(oldest.backupPath); } catch { }
                        var entries = _patchBackupStack.ToArray().Reverse().Skip(1).ToArray();
                        _patchBackupStack.Clear();
                        foreach (var e in entries) _patchBackupStack.Push(e);
                    }
                    _patchBackupStack.Push((resolved.FullPath, result.BackupPath));
                }

                _llmClient.FileCache.Store(Path.GetFileName(resolved.FullPath), result.UpdatedContent);

                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ReloadDocumentFromDisk(resolved.FullPath);
                }
                catch { /* non-fatal — file was written, just not auto-reloaded */ }

                _patchCount++;
                int undosAvailable = _patchBackupStack.Count;
                AppendOutput($"[PATCH] Applied to {resolved.FullPath} (undo depth: {undosAvailable})\n", OutputColor.Success);
                return resolved.FullPath;
            }
            catch (Exception ex)
            {
                AppendOutput($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
                return null;
            }
        }

    }
}
