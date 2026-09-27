// File: NewFileLineEnding.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The line ending a NEW text file is written with (H-30). TextFileFormat keeps an existing
// file's ending; a new file used to be written with whatever the model sent — LF — so every
// file DevMind created in a CRLF repo came out LF (job-1714: PromptHistory.cs and its tests;
// job-1715: SlashCompletion*.cs; `git add` warned "LF will be replaced by CRLF").
//
// The decision, first match wins:
//   1. .gitattributes: an `eol=lf` / `eol=crlf` that applies to the path, read from every
//      .gitattributes between the file's folder and the repository root (deeper files and later
//      lines win, as in git). `-text` / `binary` means git leaves the bytes alone, so the text
//      is written as given. Parsed here — no git process per write. $GIT_DIR/info/attributes
//      and core.eol / core.autocrlf are not read.
//   2. .sh is LF: bash rejects a CRLF script, whatever its neighbours use.
//   3. The dominant ending of neighbouring text files: same extension in the same folder, then
//      any text file in the folder, then the same two in the parent folder. At most
//      SampleFiles files per tier, SampleBytes of each; cloud placeholders, binaries and
//      oversized files are skipped (ContextEngine.ShouldSkipForContentSearch — opening a
//      OneDrive online-only file downloads it).
//   4. CRLF on Windows, LF elsewhere.
//
// The content is then normalised fully — \r\n, lone \r and lone \n all become the chosen
// ending — so mixed endings from the model do not survive into the file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    public static class NewFileLineEnding
    {
        public const int SampleFiles = 10;
        public const int SampleBytes = 4096;
        private const int MaxAncestorLevels = 32;

        /// <summary>
        /// The line ending for a new file at <paramref name="path"/>: "\r\n" or "\n", or null
        /// when .gitattributes marks the path -text/binary (write the text as given).
        /// </summary>
        public static string Choose(string path)
        {
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);

            GitAttributesEol attr = FromGitAttributes(full);
            if (attr == GitAttributesEol.Lf) return "\n";
            if (attr == GitAttributesEol.Crlf) return "\r\n";
            if (attr == GitAttributesEol.AsIs) return null;

            string ext = Path.GetExtension(full);
            if (string.Equals(ext, ".sh", StringComparison.OrdinalIgnoreCase)) return "\n";

            if (!string.IsNullOrEmpty(dir))
            {
                string parent = Path.GetDirectoryName(dir);
                foreach (string folder in parent != null ? new[] { dir, parent } : new[] { dir })
                {
                    string found = DominantIn(folder, ext, full) ?? DominantIn(folder, null, full);
                    if (found != null) return found;
                }
            }

            return OperatingSystem.IsWindows() ? "\r\n" : "\n";
        }

        /// <summary>
        /// <paramref name="text"/> with every line break (\r\n, lone \r, lone \n) converted to
        /// <paramref name="newLine"/>; unchanged when <paramref name="newLine"/> is null.
        /// </summary>
        public static string Normalize(string text, string newLine)
        {
            if (string.IsNullOrEmpty(text) || newLine == null) return text;
            string lf = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return newLine == "\n" ? lf : lf.Replace("\n", newLine);
        }

        /// <summary><paramref name="text"/> normalised to <see cref="Choose"/> for <paramref name="path"/>.</summary>
        public static string Apply(string path, string text) => Normalize(text, Choose(path));

        // ── 3. Neighbouring files ─────────────────────────────────────────────────

        // Vote by file: each sampled text file with line breaks counts once for its own dominant
        // ending. null when no sampled file had a line break.
        private static string DominantIn(string folder, string ext, string exclude)
        {
            IEnumerable<string> candidates;
            try
            {
                if (!Directory.Exists(folder)) return null;
                candidates = Directory.EnumerateFiles(folder, ext != null ? "*" + ext : "*");
            }
            catch { return null; }

            int crlf = 0, lf = 0, sampled = 0;
            try
            {
                foreach (string file in candidates)
                {
                    if (sampled >= SampleFiles) break;
                    if (string.Equals(file, exclude, StringComparison.OrdinalIgnoreCase)) continue;
                    // "*.cs" also matches "x.csproj" in EnumerateFiles' 8.3 legacy matching.
                    if (ext != null && !string.Equals(Path.GetExtension(file), ext, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal)) continue;
                    if (ContextEngine.ShouldSkipForContentSearch(file)) continue;

                    string sample = ReadSample(file);
                    if (sample == null) continue;
                    sampled++;
                    string nl = TextFileFormat.DominantNewLine(sample);
                    if (nl == "\r\n") crlf++;
                    else if (nl == "\n") lf++;
                }
            }
            catch { /* enumeration can fail mid-way (access, a vanished folder) — use what we have */ }

            if (crlf + lf == 0) return null;
            return crlf >= lf ? "\r\n" : "\n";
        }

        // The first SampleBytes of a file as text, or null when it looks binary (a NUL byte)
        // or cannot be read.
        private static string ReadSample(string file)
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[SampleBytes];
                int n = fs.Read(buf, 0, buf.Length);
                if (Array.IndexOf(buf, (byte)0, 0, n) >= 0) return null;
                return Encoding.UTF8.GetString(buf, 0, n);
            }
            catch { return null; }
        }

        // ── 1. .gitattributes ─────────────────────────────────────────────────────

        internal enum GitAttributesEol { Unspecified, Lf, Crlf, AsIs }

        internal static GitAttributesEol FromGitAttributes(string fullPath)
        {
            // Collect .gitattributes from the file's folder up to the repository root (the first
            // folder holding .git) — shallowest first, so deeper files override, as in git.
            var files = new List<string>();
            string dir = Path.GetDirectoryName(fullPath);
            for (int level = 0; dir != null && level < MaxAncestorLevels; level++)
            {
                string attrs = Path.Combine(dir, ".gitattributes");
                if (File.Exists(attrs)) files.Add(attrs);
                if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) break;
                dir = Path.GetDirectoryName(dir);
            }
            files.Reverse();

            string eol = null;     // "lf" / "crlf" / null
            bool asIs = false;     // -text or binary
            foreach (string attrsFile in files)
            {
                string baseDir = Path.GetDirectoryName(attrsFile);
                string rel = Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/');
                string[] lines;
                try { lines = File.ReadAllLines(attrsFile); }
                catch { continue; }

                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line.StartsWith("[attr]", StringComparison.Ordinal)) continue;
                    string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2 || !PatternMatches(parts[0], rel)) continue;

                    foreach (string a in parts.Skip(1))
                    {
                        switch (a.ToLowerInvariant())
                        {
                            case "eol=lf":   eol = "lf";   asIs = false; break;
                            case "eol=crlf": eol = "crlf"; asIs = false; break;
                            case "-eol":
                            case "!eol":     eol = null; break;
                            case "-text":
                            case "binary":   asIs = true; eol = null; break;
                            case "text":
                            case "text=auto": asIs = false; break;
                        }
                    }
                }
            }

            if (asIs) return GitAttributesEol.AsIs;
            return eol == "lf" ? GitAttributesEol.Lf
                 : eol == "crlf" ? GitAttributesEol.Crlf
                 : GitAttributesEol.Unspecified;
        }

        // gitattributes pattern semantics, simplified: a pattern with no '/' (other than a
        // trailing one) matches the file name at any depth; otherwise it matches the path
        // relative to the .gitattributes folder. * ? and ** as in gitignore.
        internal static bool PatternMatches(string pattern, string relPath)
        {
            if (pattern.StartsWith("\"", StringComparison.Ordinal)) return false; // quoted patterns: not supported
            string p = pattern.TrimEnd('/');
            bool anchored = p.Contains('/');
            if (p.StartsWith("/", StringComparison.Ordinal)) p = p.Substring(1);
            string subject = anchored ? relPath : relPath.Substring(relPath.LastIndexOf('/') + 1);

            var rx = new StringBuilder("^");
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '*' && i + 1 < p.Length && p[i + 1] == '*')
                {
                    bool slashAfter = i + 2 < p.Length && p[i + 2] == '/';
                    rx.Append(slashAfter ? "(?:.*/)?" : ".*");
                    i += slashAfter ? 2 : 1;
                }
                else if (c == '*') rx.Append("[^/]*");
                else if (c == '?') rx.Append("[^/]");
                else rx.Append(Regex.Escape(c.ToString()));
            }
            rx.Append('$');

            var options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
            return Regex.IsMatch(subject, rx.ToString(), options);
        }
    }
}
