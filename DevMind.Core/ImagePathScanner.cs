// File: ImagePathScanner.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Auto-attach detection for typed messages: finds file paths in user input that
// point at real image files, so the host can stage them as multimodal content
// without an explicit /image command. Quoted segments are checked first (drag-
// dropping a file into a Windows terminal pastes a quoted path), then plain
// whitespace-delimited tokens. A candidate must carry a known image extension,
// exist on disk, be within the size cap, AND sniff as a real image by magic
// bytes — a text file renamed .png is never attached. PDFs are deliberately
// excluded: they need a rasterize/page decision, which stays behind /image.

using System;
using System.Collections.Generic;
using System.IO;

namespace DevMind
{
    /// <summary>A validated on-disk image found in a typed message.</summary>
    public sealed class DetectedImage
    {
        public string FullPath { get; set; }
        /// <summary>MIME type from magic-byte sniffing (authoritative over the extension).</summary>
        public string MimeType { get; set; }
        public long SizeBytes { get; set; }
    }

    /// <summary>Finds attachable image paths in free-form user input.</summary>
    public static class ImagePathScanner
    {
        /// <summary>Same cap as /image (llama-server's own remote-image limit).</summary>
        public const int MaxImageBytes = 10 * 1024 * 1024;

        private static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

        // Punctuation that prose attaches to a trailing path ("see error.png?").
        private static readonly char[] TrailingPunctuation =
            { '.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"' };

        /// <summary>
        /// Scans input for image paths and returns each unique, validated image in
        /// order of first appearance. Unquoted paths containing spaces are not
        /// detected — quote them (drag-and-drop does this automatically).
        /// </summary>
        public static List<DetectedImage> Scan(string input, string workingDirectory)
        {
            var results = new List<DetectedImage>();
            if (string.IsNullOrWhiteSpace(input))
                return results;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in ExtractCandidates(input))
            {
                string fullPath = Resolve(candidate, workingDirectory);
                if (fullPath == null || !seen.Add(fullPath))
                    continue;
                var image = Validate(fullPath);
                if (image != null)
                    results.Add(image);
            }
            return results;
        }

        /// <summary>Reads the file and builds the data: URI the chat protocol expects.</summary>
        public static string BuildDataUri(DetectedImage image)
        {
            byte[] bytes = File.ReadAllBytes(image.FullPath);
            return $"data:{image.MimeType};base64,{Convert.ToBase64String(bytes)}";
        }

        /// <summary>
        /// Yields path candidates in order of appearance: contents of double-quoted
        /// segments (spaces intact), plus whitespace tokens from the unquoted rest
        /// with trailing prose punctuation trimmed. Appearance order matters — staged
        /// images reach the model in this order, and users refer to them positionally
        /// ("the first screenshot"). Only image-extension candidates survive.
        /// </summary>
        private static IEnumerable<string> ExtractCandidates(string input)
        {
            var ordered = new List<(int Pos, string Text)>();

            // Quoted segments first, masking them out so the token pass skips them.
            var masked = input.ToCharArray();
            int pos = 0;
            while (true)
            {
                int open = input.IndexOf('"', pos);
                if (open < 0) break;
                int close = input.IndexOf('"', open + 1);
                if (close < 0) break;
                string quoted = input.Substring(open + 1, close - open - 1).Trim();
                if (HasImageExtension(quoted))
                    ordered.Add((open, quoted));
                for (int i = open; i <= close; i++)
                    masked[i] = ' ';
                pos = close + 1;
            }

            // Whitespace-delimited tokens from the unquoted remainder.
            int t = 0;
            while (t < masked.Length)
            {
                while (t < masked.Length && char.IsWhiteSpace(masked[t])) t++;
                int start = t;
                while (t < masked.Length && !char.IsWhiteSpace(masked[t])) t++;
                if (t > start)
                {
                    string token = new string(masked, start, t - start).TrimEnd(TrailingPunctuation);
                    if (HasImageExtension(token))
                        ordered.Add((start, token));
                }
            }

            ordered.Sort((a, b) => a.Pos.CompareTo(b.Pos));
            foreach (var candidate in ordered)
                yield return candidate.Text;
        }

        private static bool HasImageExtension(string candidate)
        {
            if (string.IsNullOrEmpty(candidate))
                return false;
            foreach (string ext in ImageExtensions)
            {
                if (candidate.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string Resolve(string candidate, string workingDirectory)
        {
            try
            {
                string path = Path.IsPathRooted(candidate)
                    ? candidate
                    : Path.GetFullPath(Path.Combine(workingDirectory ?? ".", candidate));
                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null; // invalid path characters etc. — just not a path
            }
        }

        /// <summary>Null unless the file is within the cap and sniffs as a real image.</summary>
        private static DetectedImage Validate(string fullPath)
        {
            try
            {
                var info = new FileInfo(fullPath);
                if (info.Length == 0 || info.Length > MaxImageBytes)
                    return null;

                var header = new byte[12];
                int read;
                using (var stream = File.OpenRead(fullPath))
                    read = stream.Read(header, 0, header.Length);

                string mime = SniffMime(header, read);
                if (mime == null)
                    return null;

                return new DetectedImage { FullPath = fullPath, MimeType = mime, SizeBytes = info.Length };
            }
            catch
            {
                return null; // unreadable (locked, permissions) — skip silently
            }
        }

        /// <summary>Magic-byte detection for the supported formats; null when none match.</summary>
        internal static string SniffMime(byte[] header, int length)
        {
            if (length >= 8
                && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
                return "image/png";
            if (length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return "image/jpeg";
            if (length >= 4 && header[0] == 'G' && header[1] == 'I' && header[2] == 'F' && header[3] == '8')
                return "image/gif";
            if (length >= 12
                && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                && header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P')
                return "image/webp";
            if (length >= 2 && header[0] == 'B' && header[1] == 'M')
                return "image/bmp";
            return null;
        }
    }
}
