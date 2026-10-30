// File: ImagePathScanner.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Auto-attach detection for typed messages: finds file paths in user input that
// point at real image files, so the host can stage them as multimodal content
// without an explicit /image command. Three passes, in order: double-quoted
// segments, single-quoted segments, then whitespace-delimited tokens (where a
// token starting a rooted path may extend over following space-separated tokens,
// so unquoted paths with spaces work). A candidate must carry a known image
// extension, exist on disk, be within the size cap, AND sniff as a real image by
// magic bytes — a text file renamed .png is never attached. Only an ACCEPTED
// candidate consumes its text; a rejected quoted segment leaves its contents for
// the later passes. PDFs are deliberately excluded: they need a rasterize/page
// decision, which stays behind /image.

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

        // Punctuation that prose puts in front of a bare path ("(see C:\a.png)").
        private static readonly char[] LeadingPunctuation = { '(', '[', '{', '\'', '"' };

        /// <summary>
        /// Most whitespace-separated tokens an unquoted rooted path may span
        /// (the first token plus up to 11 following ones).
        /// </summary>
        private const int MaxPathTokens = 12;

        /// <summary>
        /// Scans input for image paths and returns each unique, validated image in
        /// order of first appearance. Rules:
        /// <list type="bullet">
        /// <item>Double-quoted segments (<c>"..."</c>) are candidates with spaces intact.
        /// Windows Terminal drag-and-drop is covered whether it pastes the path quoted
        /// or bare.</item>
        /// <item>Single-quoted segments (<c>'...'</c>) are candidates too, but only when
        /// delimited at word boundaries, so prose apostrophes (<c>don't</c>, <c>Paul's</c>)
        /// never open or close one — see <see cref="IsSingleQuoteOpener"/> and
        /// <see cref="IsSingleQuoteCloser"/>.</item>
        /// <item>Remaining text is split on whitespace; each token is a candidate after
        /// trimming leading <c>( [ { ' "</c> and trailing prose punctuation.</item>
        /// <item>A token that starts a ROOTED path (<c>C:\</c>, <c>C:/</c> or
        /// <c>\\server\share</c>) may extend over up to <see cref="MaxPathTokens"/> tokens
        /// separated only by plain spaces; the LONGEST extension that validates wins, and
        /// its tokens are not rescanned. The candidate is the original substring, so runs
        /// of several spaces inside a file name are preserved. Relative paths containing
        /// spaces are not extended — quote them.</item>
        /// </list>
        /// A candidate that fails validation consumes nothing. Disk is touched only for
        /// candidates ending in an image extension; there is no directory listing.
        /// </summary>
        public static List<DetectedImage> Scan(string input, string workingDirectory)
        {
            var results = new List<DetectedImage>();
            if (string.IsNullOrWhiteSpace(input))
                return results;

            var state = new ScanState(input, workingDirectory);
            ScanDoubleQuoted(state);
            ScanSingleQuoted(state);
            ScanTokens(state);

            // Appearance order matters — staged images reach the model in this order,
            // and users refer to them positionally ("the first screenshot").
            state.Found.Sort((a, b) => a.Pos.CompareTo(b.Pos));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var found in state.Found)
            {
                if (seen.Add(found.Image.FullPath))
                    results.Add(found.Image);
            }
            return results;
        }

        /// <summary>Reads the file and builds the data: URI the chat protocol expects.</summary>
        public static string BuildDataUri(DetectedImage image)
        {
            byte[] bytes = File.ReadAllBytes(image.FullPath);
            return $"data:{image.MimeType};base64,{Convert.ToBase64String(bytes)}";
        }

        /// <summary>Per-call scan state: accepted images, consumed text, validation cache.</summary>
        private sealed class ScanState
        {
            public readonly string Input;
            public readonly string WorkingDirectory;
            /// <summary>Input with accepted candidates blanked to spaces.</summary>
            public readonly char[] Masked;
            /// <summary>True where an accepted candidate (quotes included) consumed the text.</summary>
            public readonly bool[] Consumed;
            public readonly List<(int Pos, DetectedImage Image)> Found = new List<(int, DetectedImage)>();
            private readonly Dictionary<string, DetectedImage> _validated =
                new Dictionary<string, DetectedImage>(StringComparer.OrdinalIgnoreCase);

            public ScanState(string input, string workingDirectory)
            {
                Input = input;
                WorkingDirectory = workingDirectory;
                Masked = input.ToCharArray();
                Consumed = new bool[input.Length];
            }

            /// <summary>Extension check, then resolve + validate (cached per full path).</summary>
            public bool TryAccept(string candidate, out DetectedImage image)
            {
                image = null;
                if (!HasImageExtension(candidate))
                    return false;
                string fullPath = Resolve(candidate, WorkingDirectory);
                if (fullPath == null)
                    return false;
                if (!_validated.TryGetValue(fullPath, out image))
                {
                    image = Validate(fullPath);
                    _validated[fullPath] = image;
                }
                return image != null;
            }

            /// <summary>Records an accepted image and blanks [start, endExclusive) for later passes.</summary>
            public void Accept(int pos, DetectedImage image, int start, int endExclusive)
            {
                Found.Add((pos, image));
                for (int i = start; i < endExclusive; i++)
                {
                    Masked[i] = ' ';
                    Consumed[i] = true;
                }
            }
        }

        /// <summary>
        /// Pass 1: double-quoted segments, paired left to right (spaces intact).
        /// A segment is consumed only when its trimmed contents validate.
        /// </summary>
        private static void ScanDoubleQuoted(ScanState state)
        {
            string input = state.Input;
            int pos = 0;
            while (true)
            {
                int open = input.IndexOf('"', pos);
                if (open < 0) break;
                int close = input.IndexOf('"', open + 1);
                if (close < 0) break;
                string quoted = input.Substring(open + 1, close - open - 1).Trim();
                if (state.TryAccept(quoted, out DetectedImage image))
                    state.Accept(open, image, open, close + 1);
                pos = close + 1;
            }
        }

        /// <summary>
        /// Pass 2: single-quoted segments on the text pass 1 left. An opener that
        /// finds no closer, or whose contents fail validation, consumes nothing and
        /// the search resumes at the next apostrophe.
        /// </summary>
        private static void ScanSingleQuoted(ScanState state)
        {
            string text = new string(state.Masked);
            for (int open = text.IndexOf('\''); open >= 0; open = text.IndexOf('\'', open + 1))
            {
                if (!IsSingleQuoteOpener(text, open))
                    continue;
                int close = -1;
                for (int j = open + 2; j < text.Length; j++)
                {
                    if (IsSingleQuoteCloser(text, j))
                    {
                        close = j;
                        break;
                    }
                }
                if (close < 0)
                    continue;
                string quoted = text.Substring(open + 1, close - open - 1).Trim();
                if (state.TryAccept(quoted, out DetectedImage image))
                {
                    state.Accept(open, image, open, close + 1);
                    open = close; // resume after the accepted segment
                }
            }
        }

        /// <summary>
        /// A <c>'</c> opens a single-quoted segment when it is at the start of the text
        /// or follows whitespace, <c>(</c> or <c>[</c>, AND the next character is not
        /// whitespace. The apostrophes in <c>don't</c> / <c>Paul's</c> follow a letter, so
        /// they never open one.
        /// </summary>
        private static bool IsSingleQuoteOpener(string text, int i)
        {
            if (text[i] != '\'' || i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]))
                return false;
            if (i == 0)
                return true;
            char prev = text[i - 1];
            return char.IsWhiteSpace(prev) || prev == '(' || prev == '[';
        }

        /// <summary>
        /// A <c>'</c> closes a single-quoted segment when the previous character is not
        /// whitespace AND it is at the end of the text or is followed by whitespace or
        /// trailing prose punctuation (<see cref="TrailingPunctuation"/>). The first such
        /// <c>'</c> after the opener closes it; an apostrophe inside a name followed by a
        /// letter (<c>'C:\Paul's\a.png'</c>) does not.
        /// </summary>
        private static bool IsSingleQuoteCloser(string text, int j)
        {
            if (text[j] != '\'' || char.IsWhiteSpace(text[j - 1]))
                return false;
            if (j + 1 == text.Length)
                return true;
            char next = text[j + 1];
            return char.IsWhiteSpace(next) || Array.IndexOf(TrailingPunctuation, next) >= 0;
        }

        /// <summary>
        /// Pass 3: whitespace-delimited tokens on the text passes 1–2 left. A token
        /// that starts a rooted path is also tried extended over following tokens —
        /// as the ORIGINAL substring, so the exact spacing (double spaces included)
        /// is kept — up to <see cref="MaxPathTokens"/> tokens, stopping at any gap
        /// that is not plain spaces (tab, newline) or that touches consumed text.
        /// The longest validating candidate wins, including over the bare token, and
        /// the tokens it covers are skipped.
        /// </summary>
        private static void ScanTokens(ScanState state)
        {
            char[] masked = state.Masked;
            var tokens = new List<(int Start, int End)>();
            int t = 0;
            while (t < masked.Length)
            {
                while (t < masked.Length && char.IsWhiteSpace(masked[t])) t++;
                int start = t;
                while (t < masked.Length && !char.IsWhiteSpace(masked[t])) t++;
                if (t > start)
                    tokens.Add((start, t));
            }

            for (int k = 0; k < tokens.Count; k++)
            {
                int start = tokens[k].Start;
                while (start < tokens[k].End && Array.IndexOf(LeadingPunctuation, masked[start]) >= 0)
                    start++;
                if (start == tokens[k].End)
                    continue;

                int last = IsRootedStart(masked, start, tokens[k].End)
                    ? Math.Min(tokens.Count - 1, k + MaxPathTokens - 1)
                    : k;
                DetectedImage best = null;
                int bestIndex = -1;
                for (int m = k; m <= last; m++)
                {
                    if (m > k && !IsPlainSpaceGap(state, tokens[m - 1].End, tokens[m].Start))
                        break;
                    string candidate = state.Input.Substring(start, tokens[m].End - start)
                        .TrimEnd(TrailingPunctuation);
                    if (state.TryAccept(candidate, out DetectedImage image))
                    {
                        best = image;
                        bestIndex = m;
                    }
                }
                if (best != null)
                {
                    state.Accept(start, best, start, tokens[bestIndex].End);
                    k = bestIndex;
                }
            }
        }

        /// <summary>True for <c>X:\</c>, <c>X:/</c> or <c>\\server</c> at <paramref name="start"/>.</summary>
        private static bool IsRootedStart(char[] text, int start, int end)
        {
            if (end - start < 3)
                return false;
            if (char.IsLetter(text[start]) && text[start + 1] == ':'
                && (text[start + 2] == '\\' || text[start + 2] == '/'))
                return true;
            return text[start] == '\\' && text[start + 1] == '\\' && text[start + 2] != '\\';
        }

        /// <summary>True when [from, to) is only plain spaces that no accepted candidate consumed.</summary>
        private static bool IsPlainSpaceGap(ScanState state, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                if (state.Input[i] != ' ' || state.Consumed[i])
                    return false;
            }
            return true;
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
            => TryValidate(fullPath, out DetectedImage image, out _) ? image : null;

        /// <summary>
        /// The checks auto-attach applies (exists, non-empty, within <see cref="MaxImageBytes"/>,
        /// magic bytes of a supported format), reporting WHY a file is rejected so a caller that
        /// must answer — the view_image tool — can say so.
        /// </summary>
        /// <param name="fullPath">Absolute path of the candidate file.</param>
        /// <param name="image">The validated image; null when rejected.</param>
        /// <param name="error">A one-line reason when rejected; null on success.</param>
        public static bool TryValidate(string fullPath, out DetectedImage image, out string error)
        {
            image = null;
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    error = "file not found";
                    return false;
                }
                if (info.Length == 0)
                {
                    error = "the file is empty";
                    return false;
                }
                if (info.Length > MaxImageBytes)
                {
                    error = $"the file is too large ({info.Length / 1024.0 / 1024.0:F1} MB; the limit is {MaxImageBytes / 1024 / 1024} MB)";
                    return false;
                }

                var header = new byte[12];
                int read;
                using (var stream = File.OpenRead(fullPath))
                    read = stream.Read(header, 0, header.Length);

                string mime = SniffMime(header, read);
                if (mime == null)
                {
                    error = "not an image — its contents are not PNG, JPEG, GIF, WebP or BMP";
                    return false;
                }

                image = new DetectedImage { FullPath = fullPath, MimeType = mime, SizeBytes = info.Length };
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                // Unreadable (locked, permissions). Auto-attach skips it silently.
                error = $"the file could not be read ({ex.Message})";
                return false;
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
