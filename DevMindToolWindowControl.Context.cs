// File: DevMindToolWindowControl.Context.cs  v5.1
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
        private static readonly HashSet<string> _noisePathSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", ".vs", ".git", "node_modules", "packages", ".idea" };

        private static bool IsNoisePath(string fullPath) =>
            fullPath.Replace('\\', '/').Split('/')
                .Any(seg => _noisePathSegments.Contains(seg));

        /// <summary>
        /// Recursively enumerates files matching a pattern, silently skipping
        /// any directories that are inaccessible (permission errors, symlink loops, etc.).
        /// </summary>
        private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
        {
            // Strip glob characters the LLM might accidentally include in a filename
            string safePattern = pattern.Replace("*", "").Replace("?", "");
            if (string.IsNullOrWhiteSpace(safePattern)) yield break;

            var queue = new Queue<string>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                string dir = queue.Dequeue();
                IEnumerable<string> files = Enumerable.Empty<string>();
                try { files = Directory.EnumerateFiles(dir, safePattern); } catch { }
                foreach (var f in files) yield return f;

                IEnumerable<string> subdirs = Enumerable.Empty<string>();
                try { subdirs = Directory.EnumerateDirectories(dir); } catch { }
                foreach (var sub in subdirs)
                {
                    if (!_noisePathSegments.Contains(Path.GetFileName(sub)))
                        queue.Enqueue(sub);
                }
            }
        }

        private async Task<string> FindFileInSolutionAsync(string fileName, string hint = null)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await VS.GetServiceAsync<DTE, DTE>();
                if (dte?.Solution?.FileName == null) return null;
                var solutionDir = Path.GetDirectoryName(dte.Solution.FileName);
                if (string.IsNullOrEmpty(solutionDir)) return null;

                // Exclude output/tooling folders that could contain stale compiled copies.
                // Use safe recursive enumeration — Directory.GetFiles(AllDirectories) throws
                // on any inaccessible subdirectory (symlinks, ACL-denied folders, etc.)
                var matches = SafeEnumerateFiles(solutionDir, fileName)
                    .Where(m => !IsNoisePath(m))
                    .ToArray();

                // If hint contains a path separator, prefer matches whose path contains the hint
                if (!string.IsNullOrEmpty(hint) && (hint.Contains('/') || hint.Contains('\\')))
                {
                    string normalizedHint = hint.Replace('\\', '/');
                    var hintMatches = matches
                        .Where(m => m.Replace('\\', '/').IndexOf(normalizedHint, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Where(File.Exists)
                        .ToArray();
                    if (hintMatches.Length > 1)
                        AppendOutput($"[FIND] Warning: {hintMatches.Length} matches for '{fileName}' after hint filtering — using first: {hintMatches[0]}\n", OutputColor.Dim);
                    if (hintMatches.Length > 0)
                        return hintMatches[0];
                }

                var existingMatches = matches.Where(File.Exists).ToArray();
                if (existingMatches.Length > 1)
                    AppendOutput($"[FIND] Warning: {existingMatches.Length} matches for '{fileName}' — using first: {existingMatches[0]}\n", OutputColor.Dim);

                return existingMatches.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        // ── Editor context ────────────────────────────────────────────────────

        private async Task<(string selectedText, string fileName, string fullContent)> GetEditorContextAsync()
        {
            try
            {
                var docView = await VS.Documents.GetActiveDocumentViewAsync();
                if (docView?.TextView == null) return (null, null, null);

                string fileName = Path.GetFileName(docView.FilePath ?? "");

                string selectedText = null;
                var selection = docView.TextView.Selection;
                if (selection != null && !selection.IsEmpty)
                {
                    string raw = string.Concat(selection.SelectedSpans.Select(s => s.GetText()));
                    if (!string.IsNullOrWhiteSpace(raw))
                        selectedText = raw;
                }

                string fullContent = null;
                if (selectedText == null)
                {
                    var snapshot = docView.TextView.TextSnapshot;
                    if (snapshot != null && snapshot.LineCount <= 300)
                        fullContent = snapshot.GetText();
                }

                return (selectedText, fileName, fullContent);
            }
            catch
            {
                return (null, null, null);
            }
        }

        private static string GetLanguageHint(string fileName)
        {
            string ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
            return ext switch
            {
                "cs"   => "csharp",
                "vb"   => "vbnet",
                "ts"   => "typescript",
                "js"   => "javascript",
                "py"   => "python",
                "xml"  => "xml",
                "xaml" => "xml",
                "json" => "json",
                "sql"  => "sql",
                "cpp"  => "cpp",
                "cc"   => "cpp",
                "h"    => "cpp",
                "hpp"  => "cpp",
                _      => ext
            };
        }

        // ── DevMind.md context ────────────────────────────────────────────────

        private async Task<string> LoadDevMindContextAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                string solutionDir = System.IO.Path.GetDirectoryName(dte?.Solution?.FullName);
                if (string.IsNullOrEmpty(solutionDir)) return null;

                string mdPath = Directory.GetFiles(solutionDir, "DevMind.md",
                    SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();

                return mdPath != null ? File.ReadAllText(mdPath) : null;
            }
            catch
            {
                return null;
            }
        }

        // ── READ command ──────────────────────────────────────────────────────

        private async Task ApplyReadCommandAsync(string input, bool showOutline = false)
        {
            try
            {
                // Support multi-line input: process each line starting with "READ " or "RELOAD "
                var lines = input.Split('\n');
                foreach (var rawLine in lines)
                {
                    var line = rawLine.TrimEnd('\r');

                    bool isReload = line.StartsWith("RELOAD ", StringComparison.OrdinalIgnoreCase);
                    bool isRead   = line.StartsWith("READ ",   StringComparison.OrdinalIgnoreCase);
                    if (!isRead && !isReload)
                        continue;

                    string hint = isReload
                        ? line.Substring("RELOAD ".Length).Trim().TrimEnd('\r')
                        : line.Substring("READ ".Length).Trim().TrimEnd('\r');

                    if (string.IsNullOrEmpty(hint))
                    {
                        AppendOutput("[READ] No filename specified.\n", OutputColor.Error);
                        continue;
                    }

                    // Normalize separators; extract just the filename for directory search
                    string normalizedHint = hint.Replace('\\', '/');
                    string fileNameOnly = Path.GetFileName(normalizedHint);

                    string fullPath = await FindFileInSolutionAsync(fileNameOnly, normalizedHint)
                        ?? Path.Combine(_terminalWorkingDir, hint);

                    if (!File.Exists(fullPath))
                    {
                        AppendOutput($"[READ] File not found: {hint}\n", OutputColor.Error);
                        continue;
                    }

                    // Duplicate guard — skip if this file is already in context, unless RELOAD was used
                    if (!isReload && _readContext != null && _readContext.Contains(fullPath))
                    {
                        AppendOutput($"[READ] {fileNameOnly} already in context — skipping.\n", OutputColor.Dim);
                        continue;
                    }

                    // RELOAD — remove existing entry so it is replaced with fresh content
                    if (isReload && _readContext != null)
                    {
                        int entryStart = _readContext.IndexOf($"\n\n{fileNameOnly}\n```\n", StringComparison.OrdinalIgnoreCase);
                        if (entryStart >= 0)
                        {
                            int blockEnd = _readContext.IndexOf("\n```\n\n", entryStart + fileNameOnly.Length, StringComparison.Ordinal);
                            if (blockEnd >= 0)
                                _readContext = _readContext.Remove(entryStart, blockEnd + "\n```\n\n".Length - entryStart);
                        }
                    }

                    var (content, _) = ReadFilePreservingEncoding(fullPath);
                    int lineCount = content.Split('\n').Length;

                    _readContext = (_readContext ?? "") +
                        $"The following files have been loaded for context:\n\n{fileNameOnly}\n```\n{content}\n```\n\n";

                    AppendOutput($"[READ] Loaded {fullPath} ({lineCount} lines)\n", OutputColor.Success);

                    if (showOutline)
                    {
                        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                        if (ext == ".cs")
                        {
                            var outline = ExtractCSharpOutline(content);
                            if (outline.Count > 0)
                            {
                                AppendOutput("  Outline:\n", OutputColor.Dim);
                                foreach (var entry in outline)
                                    AppendOutput($"    {entry}\n", OutputColor.Dim);
                            }
                        }
                    }
                }

                InputTextBox.Text = "";
            }
            catch (Exception ex)
            {
                AppendOutput($"[READ] Error: {ex.Message}\n", OutputColor.Error);
            }
        }

        private static List<string> ExtractCSharpOutline(string content)
        {
            var results = new List<string>();
            string currentType = null;

            foreach (var rawLine in content.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').Trim();

                // Detect class/struct/interface/enum declarations
                var typeMatch = Regex.Match(line,
                    @"^(?:(?:public|private|protected|internal|static|abstract|sealed|partial)\s+)*(?:class|struct|interface|enum)\s+(\w+)");
                if (typeMatch.Success)
                {
                    currentType = typeMatch.Value;
                    results.Add(currentType);
                    continue;
                }

                // Detect methods: access modifier(s), return type, name, opening paren
                var methodMatch = Regex.Match(line,
                    @"^(?:(?:public|private|protected|internal)\s+)?(?:(?:static|async|override|virtual|abstract|sealed|new)\s+)*[\w<>\[\],\?\s]+\s+(\w+)\s*\(([^)]*)(\)?)");
                if (methodMatch.Success)
                {
                    string name = methodMatch.Groups[1].Value;
                    // Skip control flow keywords that look like method calls
                    var skipKeywords = new HashSet<string> {
                        "if", "for", "foreach", "while", "switch", "catch",
                        "using", "lock", "return", "new", "throw", "typeof",
                        "nameof", "sizeof", "default", "when", "var", "get", "set"
                    };
                    if (skipKeywords.Contains(name)) continue;
                    string sig = methodMatch.Value.Trim();
                    if (sig.Length > 100) sig = sig.Substring(0, 100) + "...";
                    string indent = currentType != null ? "  " : "";
                    results.Add(indent + sig);
                    continue;
                }

                // Detect properties: type Name { get; set; } or type Name =>
                var propMatch = Regex.Match(line,
                    @"^(?:(?:public|private|protected|internal)\s+)?(?:(?:static|override|virtual|abstract|new)\s+)*[\w<>\[\],\?\s]+\s+(\w+)\s*(?:\{(?:\s*get|\s*set)|=>)");
                if (propMatch.Success)
                {
                    string name = propMatch.Groups[1].Value;
                    if (name == "get" || name == "set" || name == "new" || name == "return" || name == "var") continue;
                    string sig = propMatch.Value.Trim();
                    if (sig.Contains("=>")) sig = sig.Substring(0, sig.IndexOf("=>")).Trim();
                    if (sig.Contains("{")) sig = sig.Substring(0, sig.IndexOf("{")).Trim();
                    string indent = currentType != null ? "  " : "";
                    results.Add(indent + sig);
                }
            }
            return results;
        }

        private static string BuildMessageWithContext(
            string userMessage,
            string selectedText,
            string fileName,
            string fullContent = null)
        {
            if (!string.IsNullOrEmpty(selectedText))
            {
                string header = string.IsNullOrEmpty(fileName)
                    ? "Selected code:"
                    : $"Selected code from {fileName}:";
                return $"{header}\n```{GetLanguageHint(fileName)}\n{selectedText}\n```\n\n{userMessage}";
            }

            if (!string.IsNullOrEmpty(fullContent) && !string.IsNullOrEmpty(fileName))
                return $"Active file ({fileName}):\n```{GetLanguageHint(fileName)}\n{fullContent}\n```\n\n{userMessage}";

            if (!string.IsNullOrEmpty(fileName))
                return $"[Active file: {fileName}]\n\n{userMessage}";

            return userMessage;
        }
    }
}
