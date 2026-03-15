// File: DevMindToolWindowControl.Patch.cs  v5.6
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using EnvDTE;

namespace DevMind
{
    public partial class DevMindToolWindowControl : UserControl
    {
        // Keyed by full path — populated by ApplyPatchAsync/ApplyPendingFuzzyPatch, consumed by PATCH-RESULT injector in xaml.cs
        private readonly Dictionary<string, string> _patchDiffCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

        // ── PATCH command ─────────────────────────────────────────────────────

        /// <summary>
        /// Collapses all whitespace runs to a single space character and returns
        /// a mapping array where normToOrig[i] is the position in the original
        /// string that corresponds to position i in the normalized string.
        /// </summary>
        private static (string normalized, int[] normToOrig) NormalizeWithMap(string text)
        {
            var sb = new StringBuilder(text.Length);
            var map = new List<int>(text.Length);
            bool inWhitespace = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    if (!inWhitespace)
                    {
                        sb.Append(' ');
                        map.Add(i);
                        inWhitespace = true;
                    }
                }
                else
                {
                    sb.Append(text[i]);
                    map.Add(i);
                    inWhitespace = false;
                }
            }
            return (sb.ToString(), map.ToArray());
        }

        /// <summary>
        /// Reads a file detecting and preserving its BOM/encoding.
        /// Returns the text content and the encoding to use when writing back.
        /// </summary>
        private static (string content, Encoding encoding) ReadFilePreservingEncoding(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return (new UTF8Encoding(true).GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode);

            return (Encoding.UTF8.GetString(bytes), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>
        /// Standard dynamic-programming Levenshtein edit distance.
        /// </summary>
        private static int LevenshteinDistance(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = a[i - 1] == b[j - 1]
                        ? d[i - 1, j - 1]
                        : 1 + Math.Min(d[i - 1, j - 1], Math.Min(d[i - 1, j], d[i, j - 1]));
            return d[a.Length, b.Length];
        }

        /// <summary>
        /// Slides an N-line window over content (N = line count of findText) and scores
        /// each window against normFind using Levenshtein similarity.
        /// Returns the best match only when it exceeds the threshold AND is unambiguous.
        /// </summary>
        private static (int origStart, int origEnd, double similarity)? FindFuzzyMatch(
            string content, string findText, string normFind, double threshold = 0.85)
        {
            int windowSize = findText.Split('\n').Length;

            // Build line list with absolute char offsets
            var lines = new List<(int start, int end)>();
            int pos = 0;
            while (pos < content.Length)
            {
                int nl = content.IndexOf('\n', pos);
                int end = nl >= 0 ? nl + 1 : content.Length;
                lines.Add((pos, end));
                pos = end;
                if (nl < 0) break;
            }

            double bestSim = -1, secondSim = -1;
            int bestStart = -1, bestEnd = -1;

            for (int i = 0; i <= lines.Count - windowSize; i++)
            {
                int wStart = lines[i].start;
                int wEnd   = lines[i + windowSize - 1].end;
                string window     = content.Substring(wStart, wEnd - wStart);
                string normWindow = Regex.Replace(window, @"\s+", " ").Trim();

                int maxLen = Math.Max(normFind.Length, normWindow.Length);
                if (maxLen == 0) continue;
                double sim = 1.0 - (double)LevenshteinDistance(normFind, normWindow) / maxLen;

                if (sim > bestSim)
                {
                    secondSim = bestSim;
                    bestSim   = sim;
                    bestStart = wStart;
                    bestEnd   = wEnd;
                }
                else if (sim > secondSim)
                {
                    secondSim = sim;
                }
            }

            if (bestSim < threshold) return null;
            // Require a meaningful gap over the runner-up to avoid ambiguous fuzzy matches.
            // Gap of 0.05 (5 percentage points) means the best must clearly outrank second-best.
            if (bestSim - secondSim < 0.05) return null;

            return (bestStart, bestEnd, bestSim);
        }

        /// <summary>
        /// Removes lines that are solely markdown code fence markers (``` with optional language tag).
        /// </summary>
        private static string StripMarkdownFenceLines(string text)
        {
            var lines = text.Split('\n');
            var kept = lines.Where(l => !Regex.IsMatch(l, @"^\s*```[a-zA-Z]*\s*$"));
            return string.Join("\n", kept);
        }

        private static List<(string findText, string replaceText)> ParsePatchBlocks(string input)
        {
            var results = new List<(string, string)>();
            // Skip first line (PATCH <filename>)
            int cursor = input.IndexOf('\n');
            if (cursor < 0) return results;
            cursor++;

            while (cursor < input.Length)
            {
                int findIdx = input.IndexOf("FIND:", cursor, StringComparison.OrdinalIgnoreCase);
                if (findIdx < 0) break;

                int replaceIdx = input.IndexOf("REPLACE:", findIdx, StringComparison.OrdinalIgnoreCase);
                if (replaceIdx < 0) break;

                int findContentStart = input.IndexOf('\n', findIdx) + 1;
                // Skip an opening markdown fence line (e.g. ```csharp) immediately after FIND:
                // so that the fence does not become part of findText and does not affect
                // the REPLACE: boundary offset calculation.
                {
                    int peekEnd = input.IndexOf('\n', findContentStart);
                    if (peekEnd > findContentStart)
                    {
                        string peekLine = input.Substring(findContentStart, peekEnd - findContentStart);
                        if (Regex.IsMatch(peekLine, @"^\s*```[a-zA-Z]*\s*$"))
                            findContentStart = peekEnd + 1;
                    }
                }
                string findText = input.Substring(findContentStart, replaceIdx - findContentStart);
                if (findText.EndsWith("\r\n")) findText = findText.Substring(0, findText.Length - 2);
                else if (findText.EndsWith("\n")) findText = findText.Substring(0, findText.Length - 1);

                int replaceContentStart = input.IndexOf('\n', replaceIdx) + 1;

                // Next FIND: or end of string marks the end of this REPLACE block
                int nextFindIdx = input.IndexOf("FIND:", replaceContentStart, StringComparison.OrdinalIgnoreCase);
                string rawReplace = nextFindIdx >= 0
                    ? input.Substring(replaceContentStart, nextFindIdx - replaceContentStart)
                    : input.Substring(replaceContentStart);

                if (rawReplace.EndsWith("\r\n")) rawReplace = rawReplace.Substring(0, rawReplace.Length - 2);
                else if (rawReplace.EndsWith("\n")) rawReplace = rawReplace.Substring(0, rawReplace.Length - 1);

                // Collect REPLACE lines, stopping at:
                //   • a bare closing fence (```)
                //   • a SHELL: directive
                //   • a "---" line that is followed only by empty lines or directive lines
                //     (SHELL:/PATCH /FILE:/READ /READ! ) — i.e. a block separator, not YAML content.
                //
                // AutoExecutePatchAsync strips trailing "---" only when it is the last line of the
                // block. When SHELL: directives follow the last PATCH block, "---" is NOT the last
                // line, so it arrives here and must be detected as a terminator contextually.
                var splitReplaceLines = rawReplace.Split('\n');
                var replaceLines = new List<string>();
                for (int ri = 0; ri < splitReplaceLines.Length; ri++)
                {
                    var rl = splitReplaceLines[ri];
                    if (Regex.IsMatch(rl, @"^\s*```\s*$")) break;
                    if (rl.TrimStart().StartsWith("SHELL:", StringComparison.OrdinalIgnoreCase)) break;
                    if (rl.Trim() == "---")
                    {
                        // Peek ahead: treat "---" as a terminator only when every non-empty
                        // remaining line is a known directive keyword.
                        bool isTerminator = true;
                        for (int rj = ri + 1; rj < splitReplaceLines.Length; rj++)
                        {
                            string rest = splitReplaceLines[rj].Trim();
                            if (rest.Length == 0) continue;
                            // Case-sensitive: LLM directives are always uppercase; lowercase
                            // variants (e.g. bash "read", makefile "shell") must not trigger.
                            if (rest.StartsWith("SHELL:")      ||
                                rest.StartsWith("PATCH ")      ||
                                rest.StartsWith("FILE:")       ||
                                rest.StartsWith("READ! ")      ||
                                rest.StartsWith("READ ")       ||
                                rest.StartsWith("SCRATCHPAD:"))
                                continue;
                            isTerminator = false;
                            break;
                        }
                        if (isTerminator) break;
                    }
                    replaceLines.Add(rl);
                }
                string replaceText = string.Join("\n", replaceLines);
                if (replaceText.EndsWith("\r\n")) replaceText = replaceText.Substring(0, replaceText.Length - 2);
                else if (replaceText.EndsWith("\n")) replaceText = replaceText.Substring(0, replaceText.Length - 1);

                findText    = StripMarkdownFenceLines(findText);
                replaceText = StripMarkdownFenceLines(replaceText);

                results.Add((findText, replaceText));
                cursor = nextFindIdx >= 0 ? nextFindIdx : input.Length;
            }
            return results;
        }

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

                // Strip outer markdown fences that models like DeepSeek wrap around PATCH output
                // (e.g. ```plaintext ... ```) before parsing — prevents fence lines leaking into files.
                // Preserve the first line (PATCH <filename>) intact, then strip from the rest.
                int firstNl = input.IndexOf('\n');
                if (firstNl >= 0)
                {
                    string header = input.Substring(0, firstNl + 1);
                    string body   = StripMarkdownFenceLines(input.Substring(firstNl + 1));
                    input = header + body;
                }

                // Parse all FIND/REPLACE pairs — supports multi-block patches
                var blocks = ParsePatchBlocks(input);
                if (blocks.Count == 0)
                {
                    AppendOutput("[PATCH] Invalid syntax — must contain at least one FIND: and REPLACE: pair.\n", OutputColor.Error);
                    return null;
                }

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
                    ?? Path.Combine(_terminalWorkingDir, fileNameOnly);

                if (!File.Exists(fullPath))
                {
                    AppendOutput($"[PATCH] File not found: {fullPath}\n", OutputColor.Error);
                    return null;
                }

                var (content, fileEncoding) = ReadFilePreservingEncoding(fullPath);
                bool fileUsesCrlf = content.Contains("\r\n");

                // Validate ALL blocks first — all-or-nothing semantics
                var (normContent, normToOrig) = NormalizeWithMap(content);
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
                        // Exact normalized match failed — try fuzzy line-window fallback
                        var fuzzy = FindFuzzyMatch(content, findText, normFind);
                        if (fuzzy == null)
                        {
                            AppendOutput($"[PATCH] Block {i + 1}: FIND text not found in {fileName} — no changes made.\n", OutputColor.Error);
                            return null;
                        }
                        int fuzzyLine = content.Substring(0, fuzzy.Value.origStart).Count(c => c == '\n') + 1;
                        string matchedText = content.Substring(fuzzy.Value.origStart, fuzzy.Value.origEnd - fuzzy.Value.origStart).Trim();
                        string fuzzyPreview = matchedText.Length > 120 ? matchedText.Substring(0, 120) + "…" : matchedText;
                        // Resolve line endings for the fuzzy block and add to prior resolved blocks
                        string fuzzyNormReplace = replaceText.Replace("\r\n", "\n");
                        string fuzzyFinalReplace = fileUsesCrlf ? fuzzyNormReplace.Replace("\n", "\r\n") : fuzzyNormReplace;
                        resolvedBlocks.Add((fuzzy.Value.origStart, fuzzy.Value.origEnd, fuzzyFinalReplace));
                        if (_agenticDepth > 0)
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
                        // Walk back to include leading indentation on the same line,
                        // otherwise the indentation from content and from finalReplace double up
                        while (origStart > 0 && content[origStart - 1] != '\n')
                            origStart--;
                        origEnd   = (normIdx + normFind.Length < normToOrig.Length)
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

                // Guard: all-or-nothing — if no blocks resolved, nothing to write
                if (resolvedBlocks.Count == 0)
                {
                    AppendOutput("[PATCH] No changes resolved — file not modified.\n", OutputColor.Error);
                    return null;
                }

                // Apply in reverse order so earlier positions aren't shifted by later edits
                resolvedBlocks.Sort((a, b) => b.origStart.CompareTo(a.origStart));
                var updated = content;
                foreach (var (origStart, origEnd, finalReplace) in resolvedBlocks)
                    updated = updated.Substring(0, origStart) + finalReplace + updated.Substring(origEnd);

                // Back up original before writing — enables UNDO
                try
                {
                    string backupDir = Path.Combine(Path.GetTempPath(), "DevMind");
                    Directory.CreateDirectory(backupDir);
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    string backupPath = Path.Combine(backupDir, $"{Path.GetFileName(fullPath)}.{timestamp}.bak");
                    File.Copy(fullPath, backupPath, overwrite: true);
                    if (_patchBackupStack.Count >= PatchBackupStackLimit)
                    {
                        // Discard oldest backup file to keep temp storage bounded
                        var oldest = _patchBackupStack.ToArray().Last();
                        try { File.Delete(oldest.backupPath); } catch { }
                        // Rebuild stack without the oldest entry
                        var entries = _patchBackupStack.ToArray().Reverse().Skip(1).ToArray();
                        _patchBackupStack.Clear();
                        foreach (var e in entries) _patchBackupStack.Push(e);
                    }
                    _patchBackupStack.Push((fullPath, backupPath));
                }
                catch { /* backup failure is non-fatal — patch still applies */ }

                File.WriteAllText(fullPath, updated, fileEncoding);

                // Store updated content in cache (replaces invalidation — no disk re-read needed)
                _llmClient._fileCache.Store(Path.GetFileName(fullPath), updated);

                // Build diff view for PATCH-RESULT injection (sorted ascending for display)
                _patchDiffCache[fullPath] = BuildPatchDiffView(content, updated, resolvedBlocks);

                // Refresh _readContext so the agentic loop reasons from the current file state.
                if (_readContext != null)
                {
                    string patchedFileName = Path.GetFileName(fullPath);
                    int entryStart = _readContext.IndexOf($"\n\n{patchedFileName}\n```\n", StringComparison.OrdinalIgnoreCase);
                    if (entryStart >= 0)
                    {
                        int blockEnd = _readContext.IndexOf("\n```\n\n", entryStart + patchedFileName.Length, StringComparison.Ordinal);
                        if (blockEnd >= 0)
                        {
                            _readContext = _readContext.Remove(entryStart, blockEnd + "\n```\n\n".Length - entryStart);
                            int lineCount = updated.Split('\n').Length;
                            _readContext += $"The following files have been loaded for context:\n\n{patchedFileName}\n```\n{updated}\n```\n\n";
                            AppendOutput($"[PATCH] Context refreshed: {patchedFileName} ({lineCount} lines)\n", OutputColor.Dim);
                        }
                    }
                }

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

        private void ApplyPendingFuzzyPatch(
            (string fullPath, Encoding fileEncoding, string fileName, string content, List<(int origStart, int origEnd, string replaceText)> resolvedBlocks) pending)
        {
            try
            {
                pending.resolvedBlocks.Sort((a, b) => b.origStart.CompareTo(a.origStart));
                var updated = pending.content;
                foreach (var (origStart, origEnd, finalReplace) in pending.resolvedBlocks)
                    updated = updated.Substring(0, origStart) + finalReplace + updated.Substring(origEnd);

                try
                {
                    string backupDir = Path.Combine(Path.GetTempPath(), "DevMind");
                    Directory.CreateDirectory(backupDir);
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    string backupPath = Path.Combine(backupDir, $"{Path.GetFileName(pending.fullPath)}.{timestamp}.bak");
                    File.Copy(pending.fullPath, backupPath, overwrite: true);
                    if (_patchBackupStack.Count >= PatchBackupStackLimit)
                    {
                        var oldest = _patchBackupStack.ToArray().Last();
                        try { File.Delete(oldest.backupPath); } catch { }
                        var entries = _patchBackupStack.ToArray().Reverse().Skip(1).ToArray();
                        _patchBackupStack.Clear();
                        foreach (var e in entries) _patchBackupStack.Push(e);
                    }
                    _patchBackupStack.Push((pending.fullPath, backupPath));
                }
                catch { /* backup failure is non-fatal */ }

                File.WriteAllText(pending.fullPath, updated, pending.fileEncoding);

                // Store updated content in cache (replaces invalidation — no disk re-read needed)
                _llmClient._fileCache.Store(Path.GetFileName(pending.fullPath), updated);

                // Build diff view for PATCH-RESULT injection
                _patchDiffCache[pending.fullPath] = BuildPatchDiffView(pending.content, updated, pending.resolvedBlocks);

                // Refresh _readContext so the agentic loop reasons from the current file state.
                if (_readContext != null)
                {
                    string patchedFileName = Path.GetFileName(pending.fullPath);
                    int entryStart = _readContext.IndexOf($"\n\n{patchedFileName}\n```\n", StringComparison.OrdinalIgnoreCase);
                    if (entryStart >= 0)
                    {
                        int blockEnd = _readContext.IndexOf("\n```\n\n", entryStart + patchedFileName.Length, StringComparison.Ordinal);
                        if (blockEnd >= 0)
                        {
                            _readContext = _readContext.Remove(entryStart, blockEnd + "\n```\n\n".Length - entryStart);
                            int lineCount = updated.Split('\n').Length;
                            _readContext += $"The following files have been loaded for context:\n\n{patchedFileName}\n```\n{updated}\n```\n\n";
                            AppendOutput($"[PATCH] Context refreshed: {patchedFileName} ({lineCount} lines)\n", OutputColor.Dim);
                        }
                    }
                }

                int undosAvailable = _patchBackupStack.Count;
                AppendOutput($"[PATCH] Applied to {pending.fullPath} (undo depth: {undosAvailable})\n", OutputColor.Success);
            }
            catch (Exception ex)
            {
                AppendOutput($"[PATCH] Error: {ex.Message}\n", OutputColor.Error);
            }
        }

        // ── PATCH diff view ──────────────────────────────────────────────────

        /// <summary>
        /// Builds a compact diff-context view of what changed: ±3 lines of surrounding context
        /// plus the replaced region with >>> CHANGED: / >>> ADDED: markers.
        /// Line numbers reflect positions in the new (post-patch) file.
        /// </summary>
        private static string BuildPatchDiffView(
            string oldContent,
            string updatedContent,
            List<(int origStart, int origEnd, string replaceText)> resolvedBlocks,
            int contextLines = 3)
        {
            // Normalize to LF for consistent line counting and display
            string oldNorm = oldContent.Replace("\r\n", "\n").Replace("\r", "\n");
            string newNorm = updatedContent.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] newLines = newNorm.Split('\n');

            var sb = new StringBuilder();

            // Sort ascending by origStart for display (blocks were applied in descending order)
            var sorted = resolvedBlocks.OrderBy(b => b.origStart).ToList();
            int cumDelta = 0;  // cumulative char-level shift from earlier (lower origStart) replacements

            foreach (var (origStart, origEnd, replaceText) in sorted)
            {
                string replaceNorm  = replaceText.Replace("\r\n", "\n").Replace("\r", "\n");
                string[] replaceLines = replaceNorm.Split('\n');

                // Char position of this block's start in the new content
                int newStartChar = origStart + cumDelta;
                newStartChar = Math.Min(newStartChar, newNorm.Length);

                // 1-based line number in new file where replacement starts
                int newLineNum = newNorm.Substring(0, newStartChar).Count(c => c == '\n') + 1;
                int newEndLine = newLineNum + replaceLines.Length - 1;

                // How many lines the old block spanned (for CHANGED vs ADDED labelling)
                int safeLen    = Math.Min(origEnd - origStart, oldNorm.Length - origStart);
                string oldBlock = safeLen > 0 ? oldNorm.Substring(origStart, safeLen) : string.Empty;
                int oldLineCount = oldBlock.Length > 0 ? oldBlock.Split('\n').Length : 0;

                sb.AppendLine($"--- Changed region (lines {newLineNum}-{newEndLine}) ---");

                // Pre-context lines (from new file)
                int preStart = Math.Max(0, newLineNum - 1 - contextLines);
                for (int i = preStart; i < newLineNum - 1 && i < newLines.Length; i++)
                    sb.AppendLine($"{i + 1}:     {newLines[i]}");

                // Replacement lines with change markers
                for (int i = 0; i < replaceLines.Length; i++)
                {
                    int lineNum = newLineNum + i;
                    string marker = i < oldLineCount ? ">>> CHANGED:" : ">>> ADDED:  ";
                    sb.AppendLine($"{lineNum}: {marker} {replaceLines[i]}");
                }

                // Post-context lines (from new file)
                for (int i = newEndLine; i < newEndLine + contextLines && i < newLines.Length; i++)
                    sb.AppendLine($"{i + 1}:     {newLines[i]}");

                sb.AppendLine("--- End of changes ---");

                cumDelta += replaceText.Length - (origEnd - origStart);
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        // ── AUTO-PATCH ────────────────────────────────────────────────────────

        // Returns distinct full paths of files that were successfully patched.
        private async Task<List<string>> AutoExecutePatchAsync(string llmResponse)
        {
            var patched = new List<string>();
            var patchStartPattern = new Regex(@"^PATCH\s+\S+\.\S+", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var matches = patchStartPattern.Matches(llmResponse);
            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : llmResponse.Length;
                string block = llmResponse.Substring(start, end - start).TrimEnd();

                // Strip any trailing bare markdown fence (```) or "---" separator that wraps/follows the PATCH block.
                // Fences appear when models like DeepSeek emit ```lang PATCH ... ``` around their output.
                // "---" appears when models separate consecutive PATCH blocks with a horizontal rule.
                var blockLines = block.Split('\n').ToList();
                while (blockLines.Count > 0 && (
                    Regex.IsMatch(blockLines[blockLines.Count - 1], @"^\s*```\s*$") ||
                    blockLines[blockLines.Count - 1].Trim() == "---"))
                    blockLines.RemoveAt(blockLines.Count - 1);
                block = string.Join("\n", blockLines);

                // Auto-READ the target file if it is not already loaded into _readContext.
                // This ensures the model always has current file state before patching,
                // regardless of whether it explicitly issued a READ directive.
                string firstLine = block.Split('\n')[0];
                string blockFileName = firstLine.Substring("PATCH ".Length).Trim();
                string blockFileNameOnly = Path.GetFileName(blockFileName.Replace('\\', '/'));
                if (!string.IsNullOrEmpty(blockFileNameOnly))
                {
                    // Resolve full path to match the format stored in _readContext by ApplyReadCommandAsync
                    string resolvedPath = await FindFileInSolutionAsync(blockFileNameOnly, blockFileName.Replace('\\', '/'))
                        ?? Path.Combine(_terminalWorkingDir, blockFileNameOnly);
                    bool alreadyLoaded = _readContext != null && _readContext.Contains(resolvedPath);
                    if (!alreadyLoaded)
                    {
                        AppendOutput($"[AUTO-READ] Loading {blockFileNameOnly} before patch...\n", OutputColor.Dim);
                        await ApplyReadCommandAsync($"READ {blockFileName}");
                    }
                }

                string appliedPath = await ApplyPatchAsync(block, clearInput: false);
                if (appliedPath != null && !patched.Contains(appliedPath))
                    patched.Add(appliedPath);
            }
            return patched;
        }
    }
}
