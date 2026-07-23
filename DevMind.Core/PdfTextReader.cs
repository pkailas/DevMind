// File: PdfTextReader.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Extracts text layers from digital PDFs via Docnet (PDFium).
// Rejoins wrapped identifiers that PDF extractors split across lines.

using Docnet.Core;
using Docnet.Core.Models;

namespace DevMind
{
    /// <summary>
    /// Reads text layers from digital (non-scanned) PDFs. Uses Docnet/PDFium
    /// with the same initialization pattern as <see cref="PdfRasterizer"/>.
    /// </summary>
    public static class PdfTextReader
    {
        /// <summary>
        /// Extracts the full text layer from a PDF, one page at a time.
        /// Applies <see cref="RejoinWrappedIdentifiers"/> to each page separately,
        /// then concatenates pages with a blank line between.
        /// Line endings are normalized to \n.
        /// </summary>
        public static string ExtractText(string pdfPath)
        {
            using (var docReader = DocLib.Instance.GetDocReader(
                pdfPath, new PageDimensions(PdfRasterizer.MaxRenderDimOne, PdfRasterizer.MaxRenderDimTwo)))
            {
                int pageCount = docReader.GetPageCount();
                var sb = new System.Text.StringBuilder();

                for (int page = 0; page < pageCount; page++)
                {
                    using (var pageReader = docReader.GetPageReader(page))
                    {
                        string pageText = pageReader.GetText();
                        pageText = RejoinWrappedIdentifiers(pageText);
                        // Normalize line endings
                        pageText = pageText.Replace("\r\n", "\n").Replace("\r", "\n");

                        if (sb.Length > 0)
                            sb.AppendLine();
                        sb.Append(pageText);
                    }
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// Returns true if the PDF has a substantial text layer (digital, not scanned).
        /// Heuristic: total non-whitespace chars >= 50 AND average non-whitespace per page >= 10.
        /// </summary>
        public static bool HasTextLayer(string pdfPath)
        {
            using (var docReader = DocLib.Instance.GetDocReader(
                pdfPath, new PageDimensions(PdfRasterizer.MaxRenderDimOne, PdfRasterizer.MaxRenderDimTwo)))
            {
                int pageCount = docReader.GetPageCount();
                if (pageCount == 0)
                    return false;

                int totalNonWhitespace = 0;
                for (int page = 0; page < pageCount; page++)
                {
                    using (var pageReader = docReader.GetPageReader(page))
                    {
                        string pageText = pageReader.GetText();
                        foreach (char c in pageText)
                        {
                            if (!char.IsWhiteSpace(c))
                                totalNonWhitespace++;
                        }
                    }
                }

                int avgPerPage = totalNonWhitespace / pageCount;
                return totalNonWhitespace >= 50 && avgPerPage >= 10;
            }
        }

        /// <summary>
        /// Rejoins consecutive "bare identifier fragment" lines into a single line.
        /// A bare identifier fragment is a line that, after Trim(), matches <c>^[A-Z0-9_]+$</c>
        /// and contains at least one A-Z letter.
        ///
        /// This fixes PDF extractors that wrap long identifiers (e.g. <c>PARENT_CATEGORY_ID</c>)
        /// across physical lines (<c>PARENT_CATEGO</c> / <c>RY_ID</c>).
        /// Real bulleted items (e.g. <c>• AW_RESOURCE</c>) fail the bare regex because of the
        /// leading bullet character, so they pass through unchanged.
        /// </summary>
        public static string RejoinWrappedIdentifiers(string pageText)
        {
            // Normalize line endings first
            string normalized = pageText.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = normalized.Split('\n');

            var result = new System.Collections.Generic.List<string>(lines.Length);
            var currentRun = new System.Collections.Generic.List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                bool isBareFragment = IsBareIdentifierFragment(trimmed);

                if (isBareFragment)
                {
                    currentRun.Add(trimmed);
                }
                else
                {
                    // Flush any accumulated bare-fragment run
                    if (currentRun.Count >= 2)
                    {
                        result.Add(string.Concat(currentRun));
                    }
                    else if (currentRun.Count == 1)
                    {
                        result.Add(currentRun[0]);
                    }
                    currentRun.Clear();

                    // Pass through the current non-bare line as-is
                    result.Add(lines[i]);
                }
            }

            // Flush any remaining run at end of input
            if (currentRun.Count >= 2)
            {
                result.Add(string.Concat(currentRun));
            }
            else if (currentRun.Count == 1)
            {
                result.Add(currentRun[0]);
            }

            return string.Join("\n", result);
        }

        private static bool IsBareIdentifierFragment(string trimmed)
        {
            if (string.IsNullOrEmpty(trimmed))
                return false;

            // Must match ^[A-Z0-9_]+$ AND contain at least one A-Z letter
            bool hasLetter = false;
            foreach (char c in trimmed)
            {
                if (c >= 'A' && c <= 'Z')
                {
                    hasLetter = true;
                }
                else if (!((c >= '0' && c <= '9') || c == '_'))
                {
                    return false; // invalid character for bare fragment
                }
            }

            return hasLetter;
        }
    }
}
