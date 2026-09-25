// File: TextFileFormat.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The on-disk format of an EXISTING text file — its encoding/BOM and its dominant line ending —
// so an edit writes the file back the way it found it.
//
// Field evidence: job-1676's edits (and a driver-side patch on 2026-09-24) stripped the UTF-8
// BOM from installer .ps1 files that must keep it — Windows PowerShell 5.1 reads a BOM-less
// script as ANSI and mangles every non-ASCII character. patch_file dropped it on purpose for
// "script" extensions; create_file-overwrite and append_file wrote File.WriteAllText's default
// (UTF-8, no BOM) whatever the file had, and the MCP tools forced BOM-less for scripts and a BOM
// for everything else.
//
// The rule now: an existing file keeps its BOM (or lack of one) and its dominant line ending.
// New files are untouched by this — each tool keeps its own new-file behaviour.
//
// Encoding detection is PatchEngine.ReadFilePreservingEncoding — one BOM detector, shared.

using System.IO;
using System.Text;

namespace DevMind
{
    public sealed class TextFileFormat
    {
        /// <summary>The file's encoding, carrying its BOM (or not) as found.</summary>
        public Encoding Encoding { get; }

        /// <summary>"\r\n" or "\n" — the file's dominant line ending; null when it has no
        /// line breaks (then new text is written as given).</summary>
        public string NewLine { get; }

        private TextFileFormat(Encoding encoding, string newLine)
        {
            Encoding = encoding;
            NewLine = newLine;
        }

        /// <summary>Reads an existing file's format. The file must exist.</summary>
        public static TextFileFormat Detect(string path)
        {
            var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(path);
            return new TextFileFormat(encoding, DominantNewLine(content));
        }

        /// <summary>"\r\n" when CRLF breaks outnumber bare LF breaks, "\n" when there are
        /// any breaks otherwise, null for a single-line file.</summary>
        public static string DominantNewLine(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            int crlf = 0, lf = 0;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] != '\n') continue;
                if (i > 0 && content[i - 1] == '\r') crlf++;
                else lf++;
            }
            if (crlf + lf == 0) return null;
            return crlf > lf ? "\r\n" : "\n";
        }

        /// <summary><paramref name="text"/> with every line break converted to the file's
        /// dominant line ending (unchanged when the file has none).</summary>
        public string NormalizeLineEndings(string text)
        {
            if (string.IsNullOrEmpty(text) || NewLine == null) return text;
            string lf = text.Replace("\r\n", "\n");
            return NewLine == "\n" ? lf : lf.Replace("\n", "\r\n");
        }

        /// <summary>Writes <paramref name="text"/> exactly as given, in the file's encoding.</summary>
        public void Write(string path, string text) => File.WriteAllText(path, text, Encoding);

        /// <summary>
        /// Replaces the whole content of <paramref name="path"/>. An existing file keeps its
        /// BOM/encoding and dominant line ending; a new file is written with
        /// <paramref name="newFileEncoding"/> (default: UTF-8 without BOM — what
        /// File.WriteAllText(path, text) writes) and its text as given.
        /// Returns the text actually written, for callers that cache it.
        /// </summary>
        public static string WritePreserving(string path, string text, Encoding newFileEncoding = null)
        {
            text ??= string.Empty;
            if (!File.Exists(path))
            {
                File.WriteAllText(path, text, newFileEncoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return text;
            }
            var format = Detect(path);
            string written = format.NormalizeLineEndings(text);
            format.Write(path, written);
            return written;
        }
    }
}
