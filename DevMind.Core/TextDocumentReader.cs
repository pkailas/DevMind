// File: TextDocumentReader.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Text extraction + chunking for non-PDF library documents (/library add):
//   * .md / .markdown / .txt — read as-is.
//   * .docx — OOXML zip: word/document.xml streamed with XmlReader (w:t text runs,
//     w:tab tabs, w:br/w:cr line breaks, paragraph ends become blank lines). No
//     external dependency — System.IO.Compression + System.Xml only.
// Chunking splits on paragraph boundaries up to a character budget sized to fit the
// embedding server's context; oversized single paragraphs are hard-split.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace DevMind
{
    /// <summary>Reads and chunks text-native documents for library ingestion.</summary>
    public static class TextDocumentReader
    {
        /// <summary>
        /// Per-chunk character budget. ~8K chars ≈ 2K tokens — comfortably inside the
        /// embedding server's 8192-token context even for token-dense text.
        /// </summary>
        public const int ChunkChars = 8_000;

        private static readonly string[] TextExtensions =
            { ".md", ".markdown", ".txt", ".docx" };

        /// <summary>True when the file is ingested via text extraction (vs PDF vision).</summary>
        public static bool IsTextDocument(string path)
        {
            string ext = Path.GetExtension(path);
            foreach (string known in TextExtensions)
            {
                if (string.Equals(ext, known, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Extracts the full plain text of a supported document.</summary>
        public static string ExtractText(string path)
        {
            if (string.Equals(Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase))
                return ExtractDocxText(path);
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Splits text into chunks of at most <paramref name="maxChars"/> characters,
        /// breaking at paragraph (blank-line) boundaries, then line boundaries, and
        /// hard-splitting only when a single line exceeds the budget. Whitespace-only
        /// input yields no chunks.
        /// </summary>
        public static List<string> ChunkText(string text, int maxChars = ChunkChars)
        {
            var chunks = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return chunks;

            var current = new StringBuilder();
            foreach (string paragraph in text.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.None))
            {
                foreach (string piece in SplitOversized(paragraph, maxChars))
                {
                    // +2 accounts for the paragraph separator restored on append.
                    if (current.Length > 0 && current.Length + piece.Length + 2 > maxChars)
                    {
                        FlushChunk(chunks, current);
                    }
                    if (current.Length > 0)
                        current.Append("\n\n");
                    current.Append(piece);
                }
            }
            FlushChunk(chunks, current);
            return chunks;
        }

        private static void FlushChunk(List<string> chunks, StringBuilder current)
        {
            string chunk = current.ToString().Trim();
            if (chunk.Length > 0)
                chunks.Add(chunk);
            current.Clear();
        }

        /// <summary>Splits one paragraph exceeding the budget: at line breaks first, then hard.</summary>
        private static IEnumerable<string> SplitOversized(string paragraph, int maxChars)
        {
            if (paragraph.Length <= maxChars)
            {
                yield return paragraph;
                yield break;
            }
            var current = new StringBuilder();
            foreach (string line in paragraph.Split('\n'))
            {
                if (current.Length > 0 && current.Length + line.Length + 1 > maxChars)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                if (line.Length > maxChars)
                {
                    // Single monster line: emit fixed-size slices.
                    for (int i = 0; i < line.Length; i += maxChars)
                        yield return line.Substring(i, Math.Min(maxChars, line.Length - i));
                    continue;
                }
                if (current.Length > 0)
                    current.Append('\n');
                current.Append(line);
            }
            if (current.Length > 0)
                yield return current.ToString();
        }

        /// <summary>
        /// Extracts text from a .docx: streams word/document.xml, emitting w:t runs,
        /// tabs, and line breaks, with a blank line after each paragraph. Element names
        /// are matched by LocalName so namespace prefixes don't matter.
        /// </summary>
        private static string ExtractDocxText(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                var entry = archive.GetEntry("word/document.xml");
                if (entry == null)
                    throw new InvalidOperationException(
                        $"Not a valid .docx (word/document.xml missing): {path}");

                var sb = new StringBuilder();
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
                using (var stream = entry.Open())
                using (var reader = XmlReader.Create(stream, settings))
                {
                    bool inTextRun = false;
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            switch (reader.LocalName)
                            {
                                // Self-closing <w:t/> emits no EndElement — don't latch on.
                                case "t": inTextRun = !reader.IsEmptyElement; break;
                                case "tab": sb.Append('\t'); break;
                                case "br":
                                case "cr": sb.Append('\n'); break;
                            }
                        }
                        else if (reader.NodeType == XmlNodeType.EndElement)
                        {
                            if (reader.LocalName == "t")
                                inTextRun = false;
                            else if (reader.LocalName == "p")
                                sb.Append("\n\n");
                        }
                        else if (inTextRun &&
                                 (reader.NodeType == XmlNodeType.Text ||
                                  reader.NodeType == XmlNodeType.SignificantWhitespace ||
                                  reader.NodeType == XmlNodeType.Whitespace))
                        {
                            sb.Append(reader.Value);
                        }
                    }
                }
                return sb.ToString();
            }
        }
    }
}
