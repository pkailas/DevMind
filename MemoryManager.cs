// File: MemoryManager.cs  v7.0
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
    /// Manages DevMind's cross-session memory system.
    /// Layer 1: MEMORY.md index file in the solution root (always loaded into system prompt).
    /// Layer 2: Topic files in .devmind/memory/ (loaded on demand via recall_memory).
    /// </summary>
    public sealed class MemoryManager
    {
        private const int IndexLineCap = 200;
        private const int MaxTopicSizeBytes = 50 * 1024; // 50 KB
        private const int MaxSlugLength = 50;

        private static readonly string IndexHeader =
            "# DevMind Memory Index\n" +
            "<!-- Auto-managed by DevMind. Do not edit the entries below manually. -->\n\n";

        private readonly string _solutionRoot;
        private readonly string _indexPath;
        private readonly string _topicDir;

        /// <summary>
        /// Creates a MemoryManager for the given solution root directory.
        /// </summary>
        public MemoryManager(string solutionRoot)
        {
            _solutionRoot = solutionRoot ?? throw new ArgumentNullException(nameof(solutionRoot));
            _indexPath = Path.Combine(_solutionRoot, "MEMORY.md");
            _topicDir = Path.Combine(_solutionRoot, ".devmind", "memory");
        }

        // ── Layer 1: MEMORY.md ──────────────────────────────────────────────

        /// <summary>
        /// Returns the full content of MEMORY.md, or null if it doesn't exist.
        /// </summary>
        public string LoadIndex()
        {
            try
            {
                if (!File.Exists(_indexPath))
                    return null;
                return File.ReadAllText(_indexPath, Encoding.UTF8);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Adds or updates an entry in the MEMORY.md index.
        /// Creates MEMORY.md if it doesn't exist.
        /// Enforces the 200-line entry cap (oldest entries removed first).
        /// </summary>
        public void AddIndexEntry(string topicSlug, string description)
        {
            try
            {
                topicSlug = SanitizeSlug(topicSlug);
                if (string.IsNullOrEmpty(description))
                    description = topicSlug;

                var entries = LoadIndexEntries();

                // Remove existing entry for this slug (will be re-added at bottom)
                entries.RemoveAll(e =>
                    e.StartsWith($"- [{topicSlug}]", StringComparison.OrdinalIgnoreCase));

                // Add new entry at the end (newest at bottom)
                entries.Add($"- [{topicSlug}] {description}");

                // Enforce cap — remove oldest (top of list)
                while (entries.Count > IndexLineCap)
                    entries.RemoveAt(0);

                WriteIndex(entries);
            }
            catch
            {
                // Never crash the extension on memory file errors
            }
        }

        // ── Layer 2: Topic Files ────────────────────────────────────────────

        /// <summary>
        /// Returns the content of a topic file, or null if it doesn't exist.
        /// </summary>
        public string LoadTopic(string topicSlug)
        {
            try
            {
                topicSlug = SanitizeSlug(topicSlug);
                string path = Path.Combine(_topicDir, topicSlug + ".md");
                if (!File.Exists(path))
                    return null;
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Writes or overwrites a topic file and updates the index.
        /// Creates the .devmind/memory/ directory if needed.
        /// </summary>
        public void SaveTopic(string topicSlug, string content, string description)
        {
            try
            {
                topicSlug = SanitizeSlug(topicSlug);

                if (!Directory.Exists(_topicDir))
                    Directory.CreateDirectory(_topicDir);

                // Cap content size
                if (content != null && Encoding.UTF8.GetByteCount(content) > MaxTopicSizeBytes)
                {
                    // Truncate to approximate limit
                    int charLimit = MaxTopicSizeBytes / 2; // rough UTF-8 estimate
                    if (content.Length > charLimit)
                        content = content.Substring(0, charLimit) + "\n\n[TRUNCATED — exceeded 50 KB limit]";
                }

                string path = Path.Combine(_topicDir, topicSlug + ".md");
                File.WriteAllText(path, content ?? "", Encoding.UTF8);

                // Update the index entry
                AddIndexEntry(topicSlug, description ?? topicSlug);
            }
            catch
            {
                // Never crash the extension on memory file errors
            }
        }

        /// <summary>
        /// Lists all available topic slugs (filenames without .md extension).
        /// </summary>
        public List<string> ListTopics()
        {
            try
            {
                if (!Directory.Exists(_topicDir))
                    return new List<string>();

                return Directory.GetFiles(_topicDir, "*.md")
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Deletes a topic file and removes its index entry.
        /// </summary>
        public void DeleteTopic(string topicSlug)
        {
            try
            {
                topicSlug = SanitizeSlug(topicSlug);

                string path = Path.Combine(_topicDir, topicSlug + ".md");
                if (File.Exists(path))
                    File.Delete(path);

                // Remove from index
                var entries = LoadIndexEntries();
                entries.RemoveAll(e =>
                    e.StartsWith($"- [{topicSlug}]", StringComparison.OrdinalIgnoreCase));
                WriteIndex(entries);
            }
            catch
            {
                // Never crash the extension on memory file errors
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Sanitizes a topic slug: lowercase, alphanumeric + hyphens only, max 50 chars.
        /// </summary>
        public static string SanitizeSlug(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
                return "untitled";

            slug = slug.ToLowerInvariant().Trim();
            slug = Regex.Replace(slug, @"[^a-z0-9]+", "-");
            slug = slug.Trim('-');

            if (slug.Length > MaxSlugLength)
                slug = slug.Substring(0, MaxSlugLength).TrimEnd('-');

            return string.IsNullOrEmpty(slug) ? "untitled" : slug;
        }

        private List<string> LoadIndexEntries()
        {
            try
            {
                if (!File.Exists(_indexPath))
                    return new List<string>();

                return File.ReadAllLines(_indexPath, Encoding.UTF8)
                    .Where(line => line.StartsWith("- [", StringComparison.Ordinal))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private void WriteIndex(List<string> entries)
        {
            var sb = new StringBuilder();
            sb.Append(IndexHeader);
            foreach (string entry in entries)
            {
                sb.AppendLine(entry);
            }

            string dir = Path.GetDirectoryName(_indexPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_indexPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
