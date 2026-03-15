// File: FileContentCache.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;

namespace DevMind
{
    internal class FileContentCache
    {
        private readonly Dictionary<string, CachedFile> _cache = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);

        public void Store(string filename, string content)
        {
            _cache[filename] = new CachedFile(content);
        }

        public string GetLineRange(string filename, int startLine, int endLine)
        {
            if (!_cache.TryGetValue(filename, out var cached)) return null;
            string[] lines = cached.Lines;
            int start = Math.Max(1, startLine) - 1;          // convert to 0-based
            int end   = Math.Min(lines.Length, endLine) - 1; // clamp to actual line count
            if (start > end || start >= lines.Length) return null;
            return string.Join("\n", lines, start, end - start + 1);
        }

        public string GetFull(string filename)
        {
            return _cache.TryGetValue(filename, out var cached) ? cached.Content : null;
        }

        public int GetLineCount(string filename)
        {
            return _cache.TryGetValue(filename, out var cached) ? cached.Lines.Length : -1;
        }

        public bool Contains(string filename)
        {
            return _cache.ContainsKey(filename);
        }

        public void Invalidate(string filename)
        {
            _cache.Remove(filename);
        }

        public void InvalidateAll()
        {
            _cache.Clear();
        }

        private sealed class CachedFile
        {
            public string Content { get; }
            public string[] Lines { get; }

            public CachedFile(string content)
            {
                Content = content;
                Lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            }
        }
    }
}
