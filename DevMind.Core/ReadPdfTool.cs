// File: ReadPdfTool.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// read_pdf — the model-callable PDF text reader (PDFium's text layer via
// PdfPageAttachments.ExtractText). Without pages it reports the page count and which pages
// have a text layer; with pages it returns "--- page N ---" blocks, capped per call at a page
// boundary with a continuation hint. Pages without a text layer (scans) point at view_image.
// The executor resolves the path and checks %PDF-; this file only reads and formats.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DevMind
{
    /// <summary>read_pdf's reading and result formatting.</summary>
    public static class ReadPdfTool
    {
        /// <summary>
        /// Most characters one read_pdf result carries (~10K tokens). It has to sit under
        /// LlmClient's 50,000-char verbatim cap on a stored tool result (MaxToolResultChars),
        /// which cuts blindly and would drop the continuation hint; 40,000 leaves room for the
        /// header and the hint. read_pdf is exempt from the 8,000-char nearline ingest excerpt
        /// (like read_file, it pages itself), so this cap is what bounds it.
        /// </summary>
        public const int MaxOutputChars = 40_000;

        /// <summary>Prefix of every read_pdf error result.</summary>
        public const string ErrorPrefix = "[READ_PDF ERROR]";

        /// <summary>The line standing in for (or following) a page without a text layer.</summary>
        internal static string NoTextLayerHint(int page)
            => $"[no text layer — call view_image with pages=\"{page}\"]";

        /// <summary>
        /// Reads <paramref name="pdfPath"/> (an existing PDF): the page summary when
        /// <paramref name="pageSpec"/> is blank, else the text of those pages
        /// (<see cref="PdfRasterizer.ParsePageSpec"/> syntax) up to <see cref="MaxOutputChars"/>.
        /// Never throws for a bad range or an unreadable file — those are error results.
        /// </summary>
        /// <returns>The call's tool result and its one-line transcript form.</returns>
        public static (string Text, string LogLine) Read(string pdfPath, string pageSpec)
            => Read(pdfPath, pageSpec, MaxOutputChars);

        /// <summary><see cref="Read(string,string)"/> with the cap as a parameter (tests).</summary>
        internal static (string Text, string LogLine) Read(string pdfPath, string pageSpec, int maxChars)
        {
            int pageCount;
            try
            {
                pageCount = PdfRasterizer.GetPageCount(pdfPath);
            }
            catch (Exception ex)
            {
                return Unreadable(pdfPath, ex.Message);
            }

            bool summary = string.IsNullOrWhiteSpace(pageSpec);
            int first = 1, last = pageCount;
            if (!summary)
            {
                try
                {
                    (first, last) = PdfRasterizer.ParsePageSpec(pageSpec.Trim(), pageCount);
                }
                catch (InvalidOperationException ex)
                {
                    return Error(pdfPath, ex.Message);
                }
            }

            IReadOnlyList<PdfPageText> pages = Array.Empty<PdfPageText>();
            if (pageCount > 0)
            {
                PdfTextResult text = PdfPageAttachments.ExtractText(pdfPath, first, last);
                if (text.Failure == PdfRenderFailure.OutOfRange)
                    return Error(pdfPath, text.Message);
                if (!text.Succeeded)
                    return Unreadable(pdfPath, text.Message);
                pages = text.Pages;
            }

            return summary
                ? Summary(pdfPath, pageCount, pages, maxChars)
                : PageText(pdfPath, pageCount, first, last, pages, maxChars);
        }

        private static (string Text, string LogLine) Summary(string pdfPath, int pageCount,
            IReadOnlyList<PdfPageText> pages, int maxChars)
        {
            var withText = pages.Where(p => p.HasText).Select(p => p.PageNumber).ToList();
            var withoutText = pages.Where(p => !p.HasText).Select(p => p.PageNumber).ToList();
            int total = pages.Sum(p => p.CharCount);
            string text =
                $"PDF {pdfPath}: {pageCount} page(s). Text layer: pages with text: {Ranges(withText)}; " +
                $"pages without text (scanned — use view_image with pages): {Ranges(withoutText)}. " +
                $"Total text: {N(total)} chars. Call read_pdf with pages (e.g. \"1-3\") to read the text; " +
                $"each call returns up to {N(maxChars)} chars.";
            return (text, $"[READ_PDF] {pdfPath}: {pageCount} page(s), {withText.Count} with text, {N(total)} chars");
        }

        private static (string Text, string LogLine) PageText(string pdfPath, int pageCount, int first, int last,
            IReadOnlyList<PdfPageText> pages, int maxChars)
        {
            string label = first == last ? $"page {first}" : $"pages {first}-{last}";
            var sb = new StringBuilder($"PDF {pdfPath} — {label} of {pageCount}:\n");
            int shown = 0;
            string truncation = null;
            foreach (PdfPageText page in pages)
            {
                string heading = $"--- page {page.PageNumber} ---\n";
                string body = page.HasText
                    ? page.Text
                    : (page.Text.Length > 0 ? page.Text + "\n" : "") + NoTextLayerHint(page.PageNumber);
                if (sb.Length + heading.Length + body.Length + 1 <= maxChars)
                {
                    sb.Append(heading).Append(body).Append('\n');
                    shown++;
                    continue;
                }

                int n = page.PageNumber;
                if (shown == 0)
                {
                    // A single page bigger than the cap: show its head; read_pdf cannot page
                    // inside a page, so the rest of it is view_image's.
                    int room = Math.Max(0, maxChars - sb.Length - heading.Length - 1);
                    sb.Append(heading).Append(body, 0, Math.Min(room, body.Length)).Append('\n');
                    truncation = n == last
                        ? $"[truncated — page {n} is cut at {N(maxChars)} chars; view_image with pages=\"{n}\" shows the whole page]"
                        : $"[truncated — page {n} is cut at {N(maxChars)} chars (view_image with pages=\"{n}\" shows the whole page); " +
                          $"call read_pdf again with pages=\"{Range(n + 1, last)}\" for the rest]";
                }
                else
                {
                    truncation = $"[truncated — call read_pdf again with pages=\"{Range(n, last)}\" for the rest]";
                }
                break;
            }

            if (truncation != null)
                sb.Append(truncation);
            string text = sb.ToString().TrimEnd('\n');
            return (text, $"[READ_PDF] {pdfPath}: {label} of {pageCount}, {N(text.Length)} chars{(truncation != null ? " (truncated)" : "")}");
        }

        /// <summary>A read_pdf error result: <c>[READ_PDF ERROR] path: message</c>.</summary>
        internal static (string Text, string LogLine) Error(string pdfPath, string message)
        {
            string line = $"{ErrorPrefix} {pdfPath}: {message.TrimEnd()}";
            return (line, line);
        }

        private static (string Text, string LogLine) Unreadable(string pdfPath, string message)
            => Error(pdfPath, $"the PDF could not be read (corrupt or password-protected?): {message}");

        /// <summary>"1-3, 5, 7-8"; "none" for an empty list. Expects ascending page numbers.</summary>
        internal static string Ranges(IReadOnlyList<int> pages)
        {
            if (pages.Count == 0)
                return "none";
            var parts = new List<string>();
            int start = pages[0], prev = pages[0];
            for (int i = 1; i <= pages.Count; i++)
            {
                if (i < pages.Count && pages[i] == prev + 1)
                {
                    prev = pages[i];
                    continue;
                }
                parts.Add(Range(start, prev));
                if (i < pages.Count)
                    start = prev = pages[i];
            }
            return string.Join(", ", parts);
        }

        private static string Range(int first, int last) => first == last ? $"{first}" : $"{first}-{last}";

        private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
