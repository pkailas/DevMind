// File: ContextEngine.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    public static class ContextEngine
    {
        private static readonly HashSet<string> _noisePathSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", ".vs", ".git", "node_modules", "packages", ".idea", "_archive" };

        // Files at or above this line count get an outline injection instead of full content
        private const int ReadOutlineThresholdLines = 100;

        public static bool IsNoisePath(string fullPath) =>
            fullPath.Replace('\\', '/').Split('/')
                .Any(seg => _noisePathSegments.Contains(seg));

        /// <summary>
        /// Recursively enumerates files matching a pattern, silently skipping
        /// any directories that are inaccessible (permission errors, symlink loops, etc.).
        /// Strips glob characters from pattern before matching.
        /// </summary>
        public static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
        {
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

        /// <summary>
        /// Recursively enumerates files using a glob pattern (with * allowed).
        /// </summary>
        public static IEnumerable<string> SafeEnumerateFilesGlob(string root, string globPattern)
        {
            var queue = new Queue<string>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                string dir = queue.Dequeue();
                IEnumerable<string> files = Enumerable.Empty<string>();
                try { files = Directory.EnumerateFiles(dir, globPattern); } catch { }
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

        // Binary / non-text file extensions that a content search must never open.
        private static readonly HashSet<string> _binarySearchExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",".jpeg",".png",".gif",".bmp",".webp",".ico",".tif",".tiff",".heic",".heif",".raw",".dng",".psd",".ai",
            ".mp4",".mov",".avi",".mkv",".webm",".wmv",".flv",".m4v",".mp3",".wav",".flac",".aac",".ogg",".m4a",
            ".zip",".gz",".tgz",".7z",".rar",".tar",".bz2",".xz",".nupkg",".jar",".war",
            ".dll",".exe",".pdb",".so",".dylib",".a",".lib",".obj",".o",".class",".wasm",
            ".pdf",".doc",".docx",".xls",".xlsx",".ppt",".pptx",".odt",".ods",
            ".bin",".dat",".db",".sqlite",".mdf",".ldf",".pack",".idx",
            ".woff",".woff2",".ttf",".otf",".eot",
        };

        private const long MaxSearchableFileBytes = 5L * 1024 * 1024; // 5 MB

        /// <summary>
        /// True when a file must NOT be opened for a content search: an offline / cloud
        /// (OneDrive Files-On-Demand) placeholder whose open would hydrate (download) it, a
        /// known binary type, or an oversized file. Reads only metadata (attributes/length) —
        /// never the content — so the check itself can never trigger a download.
        /// </summary>
        public static bool ShouldSkipForContentSearch(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                FileAttributes a = fi.Attributes;
                // Cloud / offline placeholders. Opening these hydrates them from the cloud.
                const int RecallOnDataAccess = 0x400000; // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
                const int RecallOnOpen       = 0x40000;  // FILE_ATTRIBUTE_RECALL_ON_OPEN
                if ((a & FileAttributes.Offline) != 0) return true;
                if (((int)a & RecallOnDataAccess) != 0) return true;
                if (((int)a & RecallOnOpen) != 0) return true;
                if (_binarySearchExtensions.Contains(fi.Extension)) return true;
                if (fi.Length > MaxSearchableFileBytes) return true;
            }
            catch
            {
                return true; // unreadable / missing — skip rather than risk an open
            }
            return false;
        }

        public static string GetLanguageHint(string fileName)
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

        public static string StripYamlFrontmatter(string content)
        {
            if (content == null) return null;
            string trimmed = content.TrimStart();
            if (!trimmed.StartsWith("---", StringComparison.Ordinal)) return content;

            int firstNewline = trimmed.IndexOf('\n');
            if (firstNewline < 0) return content;

            int closingDash = trimmed.IndexOf("\n---", firstNewline, StringComparison.Ordinal);
            if (closingDash < 0) return content;

            int afterClosing = trimmed.IndexOf('\n', closingDash + 4);
            return afterClosing >= 0 ? trimmed.Substring(afterClosing + 1) : "";
        }

        /// <summary>
        /// Renders a READ result block for a file — either the full content wrapped in
        /// "[READ:filename]\n…\n```\n&lt;content&gt;\n```\n\n" or the outline wrapped in
        /// "[READ:filename] (N lines — outline only…)\n&lt;outline&gt;\n\n".
        /// Pure rendering — no side effects, no _filesReadThisSession updates.
        /// </summary>
        public static string RenderReadBlock(
            string fileNameOnly,
            string content,
            int lineCount,
            bool forceFullRead,
            bool alreadyRead,
            out bool wasOutline)
        {
            bool injectOutline = (alreadyRead || lineCount >= ReadOutlineThresholdLines) && !forceFullRead;
            wasOutline = injectOutline;
            if (injectOutline)
            {
                string outlineText = LlmClient.GenerateOutline(fileNameOnly, content);
                return $"[READ:{fileNameOnly}] ({lineCount} lines — outline only; call read_file again with force_full:true for the whole file, or start_line/end_line for a range)\n{outlineText}\n\n";
            }
            return $"[READ:{fileNameOnly}]\nThe following files have been loaded for context:\n\n{fileNameOnly}\n```\n{content}\n```\n\n";
        }

        /// <summary>
        /// Renders a line-range READ block for a file:
        /// "[READ:filename:start-end] (lines start-end of total[ — clamped])\n```\n&lt;numbered&gt;\n```\n\n".
        /// Pure rendering — no side effects.
        /// </summary>
        public static string RenderReadRangeBlock(
            string fileNameOnly,
            int clampedStart,
            int clampedEnd,
            int totalLines,
            string numberedContent,
            bool clamped)
        {
            string header = clamped
                ? $"[READ:{fileNameOnly}:{clampedStart}-{clampedEnd}] (lines {clampedStart}-{clampedEnd} of {totalLines} total — clamped)"
                : $"[READ:{fileNameOnly}:{clampedStart}-{clampedEnd}] (lines {clampedStart}-{clampedEnd} of {totalLines} total)";
            return $"{header}\n```\n{numberedContent.TrimEnd('\r', '\n')}\n```\n\n";
        }

        public static List<string> ExtractCSharpOutline(string content)
        {
            var results = new List<string>();
            string currentType = null;
            var rawLines = content.Split('\n');

            for (int i = 0; i < rawLines.Length; i++)
            {
                int lineNumber = i + 1;
                string line = rawLines[i].TrimEnd('\r').Trim();

                var typeMatch = Regex.Match(line,
                    @"^(?:(?:public|private|protected|internal|static|abstract|sealed|partial)\s+)*(?:class|struct|interface|enum)\s+(\w+)");
                if (typeMatch.Success)
                {
                    currentType = typeMatch.Value;
                    results.Add($"{lineNumber,6}: {currentType}");
                    continue;
                }

                var methodMatch = Regex.Match(line,
                    @"^(?:(?:public|private|protected|internal)\s+)?(?:(?:static|async|override|virtual|abstract|sealed|new)\s+)*[\w<>\[\],\?\s]+\s+(\w+)\s*\(([^)]*)(\)?)");
                if (methodMatch.Success)
                {
                    string name = methodMatch.Groups[1].Value;
                    var skipKeywords = new HashSet<string> {
                        "if", "for", "foreach", "while", "switch", "catch",
                        "using", "lock", "return", "new", "throw", "typeof",
                        "nameof", "sizeof", "default", "when", "var", "get", "set"
                    };
                    if (skipKeywords.Contains(name)) continue;
                    string sig = methodMatch.Value.Trim();
                    if (sig.Length > 100) sig = sig.Substring(0, 100) + "...";
                    string indent = currentType != null ? "  " : "";
                    results.Add($"{lineNumber,6}: {indent}{sig}");
                    continue;
                }

                var propMatch = Regex.Match(line,
                    @"^(?:(?:public|private|protected|internal)\s+)?(?:(?:static|override|virtual|abstract|new)\s+)*[\w<>\[\],\?\s]+\s+(\w+)\s*(?:\{(?:\s*get|\s*set)|=>)");
                if (propMatch.Success)
                {
                    string name = propMatch.Groups[1].Value;
                    if (name == "get" || name == "set" || name == "new" || name == "return" || name == "var") continue;
                    string sig = propMatch.Value.Trim();
                    if (sig.Contains("=>")) sig = sig.Substring(0, sig.IndexOf("=>", StringComparison.Ordinal)).Trim();
                    if (sig.Contains("{")) sig = sig.Substring(0, sig.IndexOf("{", StringComparison.Ordinal)).Trim();
                    string indent = currentType != null ? "  " : "";
                    results.Add($"{lineNumber,6}: {indent}{sig}");
                }
            }
            return results;
        }

        public static string BuildMessageWithContext(
            string userMessage,
            string selectedText,
            string fileName,
            string fullContent = null,
            string activeProjectPath = null)
        {
            var contextBuilder = new StringBuilder();

            if (!string.IsNullOrEmpty(activeProjectPath))
            {
                contextBuilder.AppendLine($"[ACTIVE PROJECT: {activeProjectPath}]");
                contextBuilder.AppendLine();
            }

            if (!string.IsNullOrEmpty(selectedText))
            {
                string header = string.IsNullOrEmpty(fileName)
                    ? "Selected code:"
                    : $"Selected code from {fileName}:";
                return $"{contextBuilder}{header}\n```{GetLanguageHint(fileName)}\n{selectedText}\n```\n\n{userMessage}";
            }

            if (!string.IsNullOrEmpty(fullContent) && !string.IsNullOrEmpty(fileName))
                return $"{contextBuilder}Active file ({fileName}):\n```{GetLanguageHint(fileName)}\n{fullContent}\n```\n\n{userMessage}";

            if (!string.IsNullOrEmpty(fileName))
                return $"{contextBuilder}[Active file: {fileName}]\n\n{userMessage}";

            return !string.IsNullOrEmpty(activeProjectPath)
                ? $"{contextBuilder}{userMessage}"
                : userMessage;
        }

        /// <summary>
        /// Walks up from startDir looking for a directory containing .git.
        /// Returns the git root directory, or null if not found.
        /// </summary>
        public static string FindGitRoot(string startDir)
        {
            string dir = startDir;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                    return dir;
                string parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            return null;
        }

        /// <summary>
        /// Extracts candidate .cs filenames from a prompt using explicit filename
        /// mentions and PascalCase identifier heuristics.
        /// Does not include the test-file glob path (requires solutionDir from DTE).
        /// </summary>
        public static IEnumerable<string> ExtractFileCandidates(string prompt)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>();

            // 1. Explicit *.cs filenames mentioned in the prompt
            foreach (Match m in Regex.Matches(prompt, @"\b([\w]+\.cs)\b", RegexOptions.IgnoreCase))
            {
                string name = m.Groups[1].Value;
                if (seen.Add(name)) candidates.Add(name);
            }

            // 2. PascalCase words → try <Word>.cs (avoid common non-file words)
            var skipWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "I", "The", "This", "That", "When", "Where", "What", "How",
                "Add", "Fix", "Use", "Get", "Set", "Run", "Can", "Does", "Make",
                "Please", "Also", "Now", "Just", "Note", "PATCH", "READ", "FILE",
                "SHELL", "UNDO", "Wait", "True", "False", "Null"
            };
            foreach (Match m in Regex.Matches(prompt, @"\b([A-Z][a-zA-Z0-9]{2,})\b"))
            {
                string word = m.Groups[1].Value;
                if (skipWords.Contains(word)) continue;
                string name = word + ".cs";
                if (seen.Add(name)) candidates.Add(name);
            }

            return candidates;
        }
    }
}
