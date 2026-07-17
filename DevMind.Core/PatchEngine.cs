// File: PatchEngine.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>
    /// Pure static patch logic — no VS SDK, no WPF, no DTE.
    /// All methods operate on strings and System.IO only.
    /// </summary>
    public static class PatchEngine
    {
        // ── Text stripping helpers ────────────────────────────────────────────

        /// <summary>
        /// Removes lines that are solely markdown code fence markers (``` with optional language tag).
        /// </summary>
        public static string StripMarkdownFenceLines(string text)
        {
            var lines = text.Split('\n');
            var kept = lines.Where(l => !Regex.IsMatch(l, @"^\s*```[a-zA-Z]*\s*$"));
            return string.Join("\n", kept);
        }

        /// <summary>
        /// Strips a leading opening code fence line (e.g. ```csharp) and/or a trailing
        /// closing code fence line (```) from extracted FIND/REPLACE text. Only the first
        /// and last lines are examined so interior fence-like lines in the actual source are
        /// left untouched.
        /// </summary>
        public static string StripOuterCodeFence(string text)
        {
            var lines = text.Split('\n').ToList();
            if (lines.Count == 0) return text;

            // Find and strip leading opening fence — matches ```  or ```csharp etc.
            // Skips leading blank lines to find the fence.
            int openIdx = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;
                if (Regex.IsMatch(lines[i], @"^\s*```[\w#+]*\s*$"))
                    openIdx = i;
                break; // only inspect the first non-blank line
            }
            bool hadOpeningFence = openIdx >= 0;
            if (hadOpeningFence)
                lines.RemoveAt(openIdx);

            if (lines.Count == 0) return string.Empty;

            // Strip closing fence (and everything after it when an opening fence was found).
            // Search from the end for the closing ```.
            if (hadOpeningFence)
            {
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    if (Regex.IsMatch(lines[i], @"^\s*```\s*$"))
                    {
                        // Remove the fence and everything after it.
                        lines.RemoveRange(i, lines.Count - i);
                        break;
                    }
                }
            }
            else
            {
                // No opening fence — only strip a trailing bare ```.
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                        continue;
                    if (Regex.IsMatch(lines[i], @"^\s*```\s*$"))
                        lines.RemoveAt(i);
                    break;
                }
            }

            return string.Join("\n", lines);
        }

        private static readonly string[] _hallucinatedTerminators =
        {
            "END_PATCH", "END_FILE", "END_REPLACE", "END_FIND", "END"
        };

        /// <summary>
        /// Removes lines that are known LLM-hallucinated block terminators:
        /// END_PATCH, END_FILE, END_REPLACE, END_FIND, END, or a line of three or more dashes.
        /// </summary>
        public static string StripHallucinatedTerminators(string text)
        {
            var lines = text.Split('\n');
            var kept = lines.Where(l =>
            {
                string t = l.Trim();
                foreach (var term in _hallucinatedTerminators)
                    if (t.Equals(term, StringComparison.OrdinalIgnoreCase))
                        return false;
                if (Regex.IsMatch(t, @"^-{3,}$"))
                    return false;
                return true;
            });
            return string.Join("\n", kept);
        }

        // ── Parsing ───────────────────────────────────────────────────────────

        /// <summary>
        /// Parses all FIND:/REPLACE: pairs from a PATCH block.
        /// The first line (PATCH filename) is skipped.
        /// </summary>
        public static List<(string findText, string replaceText)> ParsePatchBlocks(
            string input, bool fromToolCall = false)
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
                // In tool_use mode, backticks are legitimate content — skip this stripping.
                if (!fromToolCall)
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
                if (findText.EndsWith("\r\n", StringComparison.Ordinal)) findText = findText.Substring(0, findText.Length - 2);
                else if (findText.EndsWith("\n", StringComparison.Ordinal)) findText = findText.Substring(0, findText.Length - 1);

                int replaceContentStart = input.IndexOf('\n', replaceIdx) + 1;

                // Next FIND: or end of string marks the end of this REPLACE block
                int nextFindIdx = input.IndexOf("FIND:", replaceContentStart, StringComparison.OrdinalIgnoreCase);
                string rawReplace = nextFindIdx >= 0
                    ? input.Substring(replaceContentStart, nextFindIdx - replaceContentStart)
                    : input.Substring(replaceContentStart);

                if (rawReplace.EndsWith("\r\n", StringComparison.Ordinal)) rawReplace = rawReplace.Substring(0, rawReplace.Length - 2);
                else if (rawReplace.EndsWith("\n", StringComparison.Ordinal)) rawReplace = rawReplace.Substring(0, rawReplace.Length - 1);

                // Collect REPLACE lines, stopping at:
                //   • a bare closing fence (```)
                //   • a SHELL: directive
                //   • a "---" line followed only by directive lines (block separator, not YAML content)
                var splitReplaceLines = rawReplace.Split('\n');
                var replaceLines = new List<string>();
                for (int ri = 0; ri < splitReplaceLines.Length; ri++)
                {
                    var rl = splitReplaceLines[ri];
                    if (!fromToolCall && Regex.IsMatch(rl, @"^\s*```\s*$")) break;
                    if (rl.TrimStart().StartsWith("SHELL:", StringComparison.OrdinalIgnoreCase)) break;
                    if (rl.Trim() == "---")
                    {
                        bool isTerminator = true;
                        for (int rj = ri + 1; rj < splitReplaceLines.Length; rj++)
                        {
                            string rest = splitReplaceLines[rj].Trim();
                            if (rest.Length == 0) continue;
                            if (rest.StartsWith("SHELL:", StringComparison.Ordinal)      ||
                                rest.StartsWith("PATCH ", StringComparison.Ordinal)      ||
                                rest.StartsWith("FILE:", StringComparison.Ordinal)       ||
                                rest.StartsWith("READ! ", StringComparison.Ordinal)      ||
                                rest.StartsWith("READ ", StringComparison.Ordinal)       ||
                                rest.StartsWith("SCRATCHPAD:", StringComparison.Ordinal))
                                continue;
                            isTerminator = false;
                            break;
                        }
                        if (isTerminator) break;
                    }
                    replaceLines.Add(rl);
                }
                string replaceText = string.Join("\n", replaceLines);
                if (replaceText.EndsWith("\r\n", StringComparison.Ordinal)) replaceText = replaceText.Substring(0, replaceText.Length - 2);
                else if (replaceText.EndsWith("\n", StringComparison.Ordinal)) replaceText = replaceText.Substring(0, replaceText.Length - 1);

                findText    = StripHallucinatedTerminators(findText);
                replaceText = StripHallucinatedTerminators(replaceText);

                if (!fromToolCall)
                {
                    findText    = StripOuterCodeFence(StripMarkdownFenceLines(findText));
                    replaceText = StripOuterCodeFence(StripMarkdownFenceLines(replaceText));
                }

                results.Add((findText, replaceText));
                cursor = nextFindIdx >= 0 ? nextFindIdx : input.Length;
            }
            return results;
        }

        // ── Normalization ─────────────────────────────────────────────────────

        /// <summary>
        /// Collapses all whitespace runs to a single space character and returns
        /// a mapping array where normToOrig[i] is the position in the original
        /// string that corresponds to position i in the normalized string.
        /// </summary>
        public static (string normalized, int[] normToOrig) NormalizeWithMap(string text)
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

        // ── File I/O ──────────────────────────────────────────────────────────

        /// <summary>
        /// Reads a file detecting and preserving its BOM/encoding.
        /// Returns the text content and the encoding to use when writing back.
        /// </summary>
       public static (string content, Encoding encoding) ReadFilePreservingEncoding(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                // UTF-8 with BOM detected.
                // For script files (.cmd, .bat, .sh, .ps1), strip the BOM so the patched file
                // is written back without one — prevents garbled output in shells that don't
                // expect a BOM (cmd.exe, PowerShell, bash).
                bool isScriptFile = IsScriptFileExtension(path);
                if (isScriptFile)
                    return (new UTF8Encoding(true).GetString(bytes, 3, bytes.Length - 3),
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return (new UTF8Encoding(true).GetString(bytes, 3, bytes.Length - 3),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2),
                        Encoding.BigEndianUnicode);

            return (Encoding.UTF8.GetString(bytes), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>
        /// Returns true for script file extensions where a UTF-8 BOM causes problems
        /// (cmd.exe, PowerShell, bash interpret the BOM bytes as part of the shebang/command).
        /// </summary>
        private static bool IsScriptFileExtension(string path)
        {
           string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            return ext == ".cmd" || ext == ".bat" || ext == ".sh" || ext == ".ps1";
        }

        // ── Matching algorithms ───────────────────────────────────────────────

        /// <summary>
        /// Standard dynamic-programming Levenshtein edit distance.
        /// </summary>
        public static int LevenshteinDistance(string a, string b)
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
        public static (int origStart, int origEnd, double similarity)? FindFuzzyMatch(
            string content, string findText, string normFind, double threshold = 0.85)
        {
            int windowSize = findText.Split('\n').Length;

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
            if (bestSim - secondSim < 0.05) return null;

            return (bestStart, bestEnd, bestSim);
        }

        // ── Core operations ───────────────────────────────────────────────────

        /// <summary>
        /// Resolves a PATCH block against pre-loaded file content.
        /// Does not perform any file lookup or VS/UI operations — all I/O and
        /// error reporting is handled by the caller.
        /// </summary>
        /// <param name="patchInput">Full PATCH block text including the "PATCH filename" header line.</param>
        /// <param name="fullPath">Resolved absolute path of the target file (for PatchResolveResult).</param>
        /// <param name="fileName">Display name used in reporter messages.</param>
        /// <param name="fileContent">Current content of the target file.</param>
        /// <param name="fileEncoding">Encoding detected from the target file.</param>
        /// <param name="fromToolCall">True when patchInput comes from structured JSON tool_use (skip fence stripping).</param>
        /// <param name="reporter">Receives status/error messages in place of AppendOutput.</param>
        /// <returns>Resolved patch result, or null if parsing or matching failed.</returns>
        public static PatchResolveResult ResolvePatch(
            string patchInput,
            string fullPath,
            string fileName,
            string fileContent,
            Encoding fileEncoding,
            bool fromToolCall,
            Action<string, OutputColor> reporter)
        {
            // Strip markdown fences from body when not in tool_use mode
            if (!fromToolCall)
            {
                int firstNl = patchInput.IndexOf('\n');
                if (firstNl >= 0)
                {
                    string header = patchInput.Substring(0, firstNl + 1);
                    string body   = StripMarkdownFenceLines(patchInput.Substring(firstNl + 1));
                    patchInput = header + body;
                }
            }

            var rawBlocks = ParsePatchBlocks(patchInput, fromToolCall);
            if (rawBlocks.Count == 0)
            {
                reporter("[PATCH] Invalid syntax — must contain at least one FIND: and REPLACE: pair.\n",
                    OutputColor.Error);
                return null;
            }

            return ResolvePairs(rawBlocks, fullPath, fileName, fileContent, fileEncoding, reporter);
        }

        /// <summary>
        /// Resolves already-parsed find/replace pairs against pre-loaded file content.
        /// Structured (tool-call) patches enter HERE directly with their verbatim
        /// strings — they must never round-trip through the text PATCH format, whose
        /// FIND:/REPLACE:/SHELL: markers collide with literal content (field lesson:
        /// a replace string containing the word "find:" was silently truncated at it).
        /// </summary>
        public static PatchResolveResult ResolvePairs(
            List<(string findText, string replaceText)> rawBlocks,
            string fullPath,
            string fileName,
            string fileContent,
            Encoding fileEncoding,
            Action<string, OutputColor> reporter)
        {
            bool fileUsesCrlf = fileContent.Contains("\r\n");
            var (normContent, normToOrig) = NormalizeWithMap(fileContent);
            var resolvedBlocks = new List<(int origStart, int origEnd, string replaceText)>();
            var confidence = PatchConfidence.Exact;

            for (int i = 0; i < rawBlocks.Count; i++)
            {
                var (findText, replaceText) = rawBlocks[i];
                string normFind = Regex.Replace(findText, @"\s+", " ").Trim();
                if (string.IsNullOrEmpty(normFind))
                {
                    reporter($"[PATCH] Block {i + 1}: FIND is empty after fence stripping — skipping.\n",
                        OutputColor.Error);
                    continue;
                }

                int normIdx = normContent.IndexOf(normFind, StringComparison.Ordinal);
                int origStart = 0, origEnd = 0;

                if (normIdx < 0)
                {
                    var fuzzy = FindFuzzyMatch(fileContent, findText, normFind);
                    if (fuzzy == null)
                    {
                        reporter($"[PATCH] Block {i + 1}: FIND text not found in {fileName} — no changes made.\n",
                            OutputColor.Error);
                        return null;
                    }
                    confidence = PatchConfidence.Fuzzy;
                    string fuzzyNormReplace = replaceText.Replace("\r\n", "\n");
                    string fuzzyFinalReplace = fileUsesCrlf
                        ? fuzzyNormReplace.Replace("\n", "\r\n")
                        : fuzzyNormReplace;
                    resolvedBlocks.Add((fuzzy.Value.origStart, fuzzy.Value.origEnd, fuzzyFinalReplace));
                    int fuzzyLine = fileContent.Substring(0, fuzzy.Value.origStart).Count(c => c == '\n') + 1;
                    reporter(
                        $"[PATCH] Block {i + 1}: Fuzzy match at line {fuzzyLine} ({fuzzy.Value.similarity:P0} similarity).\n",
                        OutputColor.Dim);
                    continue;
                }

                // Ambiguity check on exact match
                int secondNormIdx = normContent.IndexOf(normFind, normIdx + 1, StringComparison.Ordinal);
                if (secondNormIdx >= 0)
                {
                    int line1 = fileContent.Substring(0, normToOrig[normIdx]).Count(c => c == '\n') + 1;
                    int line2 = fileContent.Substring(0, normToOrig[secondNormIdx]).Count(c => c == '\n') + 1;
                    reporter(
                        $"[PATCH] Block {i + 1}: Ambiguous FIND — matched at line {line1} and line {line2} in {fileName}. " +
                        $"Add more surrounding context to make the match unique.\n",
                        OutputColor.Error);
                    return null;
                }

                origStart = normToOrig[normIdx];
                // Include the line's leading indentation in the replaced span ONLY
                // when everything before the match on its line is whitespace (the
                // classic full-line match, where the replacement carries its own
                // indentation). A match that starts mid-line keeps origStart exactly
                // at the match: the old unconditional walk-back to line start
                // silently deleted the text before a mid-line match (field lesson:
                // a suffix match on a one-line enum erased the line's prefix).
                int lineStart = origStart;
                while (lineStart > 0 && fileContent[lineStart - 1] != '\n')
                    lineStart--;
                bool onlyIndentationBefore = true;
                for (int k = lineStart; k < origStart; k++)
                {
                    if (fileContent[k] != ' ' && fileContent[k] != '\t')
                    {
                        onlyIndentationBefore = false;
                        break;
                    }
                }
                if (onlyIndentationBefore)
                    origStart = lineStart;
                origEnd = (normIdx + normFind.Length < normToOrig.Length)
                    ? normToOrig[normIdx + normFind.Length]
                    : fileContent.Length;

                string normalizedReplace = replaceText.Replace("\r\n", "\n");
                string finalReplace = fileUsesCrlf
                    ? normalizedReplace.Replace("\n", "\r\n")
                    : normalizedReplace;
                resolvedBlocks.Add((origStart, origEnd, finalReplace));
            }

            if (resolvedBlocks.Count == 0)
            {
                reporter("[PATCH] No changes resolved — file not modified.\n", OutputColor.Error);
                return null;
            }

            return new PatchResolveResult
            {
                FullPath        = fullPath,
                FileName        = fileName,
                OriginalContent = fileContent,
                FileEncoding    = fileEncoding,
                Confidence      = confidence,
                ResolvedBlocks  = resolvedBlocks,
                ParsedPairs     = rawBlocks
            };
        }

        /// <summary>
        /// Applies a resolved patch: computes the updated content, creates a timestamped
        /// backup, and writes the file. Does not touch the backup stack, file cache, or
        /// VS document reload — those are the extension wrapper's responsibility.
        /// </summary>
        /// <param name="resolved">The resolve result from <see cref="ResolvePatch"/>.</param>
        /// <param name="backupDir">Directory where the backup file is written.</param>
        public static PatchApplyResult ApplyPatch(PatchResolveResult resolved, string backupDir)
        {
            try
            {
                // Apply in reverse order so earlier positions aren't shifted by later edits
                resolved.ResolvedBlocks.Sort((a, b) => b.origStart.CompareTo(a.origStart));
                var updated = resolved.OriginalContent;
                foreach (var (origStart, origEnd, finalReplace) in resolved.ResolvedBlocks)
                    updated = updated.Substring(0, origStart) + finalReplace + updated.Substring(origEnd);

                // Create backup before writing — non-fatal if it fails
                string backupPath = null;
                try
                {
                    Directory.CreateDirectory(backupDir);
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    backupPath = Path.Combine(backupDir,
                        $"{Path.GetFileName(resolved.FullPath)}.{timestamp}.bak");
                    File.Copy(resolved.FullPath, backupPath, overwrite: true);
                }
                catch { backupPath = null; }

                File.WriteAllText(resolved.FullPath, updated, resolved.FileEncoding);

                return new PatchApplyResult
                {
                    Success       = true,
                    UpdatedContent = updated,
                    BackupPath    = backupPath
                };
            }
            catch (Exception ex)
            {
                return new PatchApplyResult { Success = false, Error = ex.Message };
            }
        }
    }
}
