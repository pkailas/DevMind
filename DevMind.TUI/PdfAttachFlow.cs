// File: PdfAttachFlow.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The pause-before-send for PDFs mentioned in a typed message (auto-attach). For each
// PDF, in order: count its pages and read its text layer, then ask. A PDF with text on
// any page defaults to text: Enter attaches nothing. A small PDF (its text within
// InlineTextMaxTokens and the free context) has its text included in the message as
// read_pdf's "--- page N ---" blocks; a larger one gets a one-line note saying it is NOT yet
// read, so the model reads it with read_pdf (a bare pointer let a model claim it had read a
// 9-page PDF without calling a tool). A page range attaches those pages as images. A PDF
// with no text layer keeps the image prompt (Enter = all pages). Once every PDF has been asked, the rendered pages are saved and staged as user attachments of the
// message. Nothing is staged until the end, so cancelling the whole send part-way leaves
// no stray images for the next message.
//
// Every effect is a delegate, so the flow is testable without a TUI, a PDF engine or an
// LLM client; Program.cs wires the real ones. After the first await the flow may run on a
// pool thread (Terminal.Gui 2.4.17's plain SynchronizationContext), so the delegates it
// calls past that point must be thread-safe — Program.cs's are (see its call site).

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind
{
    internal sealed class PdfAttachFlow
    {
        /// <summary>Dialog title.</summary>
        internal const string Title = "Attach PDF pages";

        /// <summary>The input hint shown under the cost line of a PDF with no text layer.</summary>
        internal const string RangeHint = "Range? Enter = all, e.g. 2-5 or 7, Esc = skip";

        /// <summary>Estimated prompt tokens per attached page (the client's per-image estimate).</summary>
        internal const int TokensPerPage = LlmClient.ImageTokenEstimatePerPart;

        /// <summary>Largest PDF text (estimated tokens, chars/4) Enter includes in the message;
        /// above it the PDF gets a reading note instead.</summary>
        internal const int InlineTextMaxTokens = 8000;

        /// <summary>The inline threshold in use (tests lower it); defaults to <see cref="InlineTextMaxTokens"/>.</summary>
        internal int InlineMaxTokens { get; init; } = InlineTextMaxTokens;

        /// <summary>Page count of a PDF; throws when the file cannot be read.</summary>
        public Func<string, int> GetPageCount { get; init; } = PdfRasterizer.GetPageCount;

        /// <summary>The text layer of every page: (path, pageCount) → all pages, or a failure.</summary>
        public Func<string, int, PdfTextResult> ExtractText { get; init; }
            = (path, pageCount) => PdfPageAttachments.ExtractText(path, 1, pageCount);

        /// <summary>Free prompt tokens given N images that will join the message but are not
        /// staged yet; null when unknown.</summary>
        public Func<int, int?> FreeTokens { get; init; } = _ => null;

        /// <summary>Shows the range prompt: (title, message, validate, ct) → text, or null on
        /// Esc / cancellation.</summary>
        public Func<string, string, Func<string, string?>, CancellationToken, Task<string?>> Prompt { get; init; }
            = (_, _, _, _) => Task.FromResult<string?>(null);

        /// <summary>Renders an inclusive 1-based page range.</summary>
        public Func<string, int, int, PdfRenderResult> Render { get; init; } = PdfPageAttachments.Render;

        /// <summary>Saves a page image: (dataUri, label) → saved path (null when the save failed).</summary>
        public Func<string, string, string?> Save { get; init; } = (_, _) => null;

        /// <summary>Stages a page image as a user attachment: (dataUri, savedPath, pdfPath, page) —
        /// the source PDF and page travel with it so its placeholder can name the PDF.</summary>
        public Action<string, string?, string, int> Stage { get; init; } = (_, _, _, _) => { };

        /// <summary>Writes one dim transcript line (no trailing newline).</summary>
        public Action<string> Echo { get; init; } = _ => { };

        private sealed class Chosen
        {
            public string Name = "";
            public string PdfPath = "";
            public int First;
            public int Last;
            public IReadOnlyList<RenderedPdfPage> Pages = Array.Empty<RenderedPdfPage>();

            /// <summary>Set when the PDF is read as text (its included text, or the note leaving
            /// it for read_pdf): nothing rendered or staged.</summary>
            public string? ReadingNote;
            public string? ReadingEcho;
        }

        /// <summary>
        /// Asks about each PDF in <paramref name="pdfs"/>, then saves and stages every chosen
        /// page. <paramref name="otherPendingImages"/> counts images that will join the message
        /// but are not staged yet (typed image paths), so the free figure accounts for them.
        /// The outcome's Send is false when <paramref name="ct"/> was cancelled while asking:
        /// nothing is staged and the caller must not send. Otherwise its ReadingNotes (one per
        /// PDF read as text: its included text or a read_pdf note) go on the end of the message
        /// (<see cref="AppendNotes"/>). Included text counts against the free figure of later PDFs.
        /// </summary>
        public async Task<PdfAttachOutcome> RunAsync(IReadOnlyList<string> pdfs, int otherPendingImages, CancellationToken ct)
        {
            var chosen = new List<Chosen>();
            int chosenPages = 0;
            int inlinedTokens = 0;

            foreach (string path in pdfs)
            {
                if (ct.IsCancellationRequested)
                    return PdfAttachOutcome.Cancelled;

                string name = Path.GetFileName(path);
                int pageCount;
                PdfTextResult textLayer;
                try
                {
                    pageCount = GetPageCount(path);
                    textLayer = pageCount > 0 ? ExtractText(path, pageCount) : new PdfTextResult();
                }
                catch (Exception ex)
                {
                    Echo($"[PDF skipped: {name} could not be read ({ex.Message})]");
                    continue;
                }
                if (!textLayer.Succeeded)
                {
                    Echo($"[PDF skipped: {name} could not be read ({textLayer.Message ?? textLayer.Failure.ToString()})]");
                    continue;
                }

                var textPages = new List<int>();
                var scannedPages = new List<int>();
                int textChars = 0;
                foreach (PdfPageText p in textLayer.Pages)
                {
                    if (p.HasText) { textPages.Add(p.PageNumber); textChars += p.CharCount; }
                    else scannedPages.Add(p.PageNumber);
                }
                bool hasText = textPages.Count > 0;
                int textTokens = textChars / 4; // LlmClient.EstimateTokens' chars/4 convention

                // Pages chosen for earlier PDFs in this message are not staged yet either, and
                // text included for earlier PDFs is not in the typed message the estimate sees.
                int? free = FreeTokens(otherPendingImages + chosenPages);
                if (free.HasValue)
                    free = Math.Max(0, free.Value - inlinedTokens);

                string? inlineText = null;
                int inlineTokens = 0;
                if (hasText)
                {
                    inlineText = BuildInlineText(path, pageCount, textPages, scannedPages, textLayer.Pages);
                    inlineTokens = inlineText.Length / 4;
                    if (inlineTokens > InlineMaxTokens || (free.HasValue && inlineTokens > free.Value))
                        inlineText = null;
                }
                string message = BuildPromptMessage(name, pageCount, textPages, scannedPages, textTokens, free,
                    inlineText != null, inlineTokens);

                bool asText = false;
                (int First, int Last) range = (1, pageCount);
                string? Validate(string text)
                {
                    string spec = (text ?? "").Trim();
                    (int First, int Last) r;
                    asText = spec.Length == 0 && hasText;
                    if (asText)
                        return null; // read as text: nothing is rendered, so no fit check
                    if (spec.Length == 0)
                        r = (1, pageCount);
                    else
                    {
                        try { r = PdfRasterizer.ParsePageSpec(spec, pageCount); }
                        catch (InvalidOperationException ex) { return ex.Message; }
                    }
                    int cost = (r.Last - r.First + 1) * TokensPerPage;
                    if (free.HasValue && cost > free.Value)
                    {
                        var (costText, freeText) = FormatCostAndFree(cost, free.Value);
                        return $"That's ~{costText} tokens; only ~{freeText} free — pick fewer pages.";
                    }
                    range = r;
                    return null;
                }

                string? answer = await Prompt(Title, message, Validate, ct);
                if (ct.IsCancellationRequested)
                    return PdfAttachOutcome.Cancelled;
                if (answer == null)
                {
                    Echo($"[skipped: {name}]");
                    continue;
                }

                if (asText && inlineText != null)
                {
                    chosen.Add(new Chosen
                    {
                        Name = name,
                        PdfPath = path,
                        ReadingNote = inlineText,
                        ReadingEcho = $"[PDF text included: {name} ({Pages(pageCount)}, ~{FormatTokens(inlineTokens)} tokens)]",
                    });
                    inlinedTokens += inlineTokens;
                    continue;
                }

                if (asText)
                {
                    chosen.Add(new Chosen
                    {
                        Name = name,
                        PdfPath = path,
                        ReadingNote = BuildReadingNote(path, pageCount, textPages, scannedPages),
                        ReadingEcho = $"[PDF for reading: {name} ({Pages(pageCount)}, ~{FormatTokens(textTokens)} tokens as text)]",
                    });
                    continue;
                }

                PdfRenderResult result = Render(path, range.First, range.Last);
                if (!result.Succeeded)
                {
                    Echo($"[PDF not attached: {name} — {DescribeFailure(result)}]");
                    continue;
                }
                chosen.Add(new Chosen { Name = name, PdfPath = path, First = range.First, Last = range.Last, Pages = result.Pages });
                chosenPages += result.Pages.Count;
            }

            if (ct.IsCancellationRequested)
                return PdfAttachOutcome.Cancelled;

            var notes = new List<string>();
            foreach (Chosen c in chosen)
            {
                if (c.ReadingNote != null)
                {
                    notes.Add(c.ReadingNote);
                    Echo(c.ReadingEcho!);
                    continue;
                }
                foreach (RenderedPdfPage page in c.Pages)
                {
                    string dataUri = PdfPageAttachments.BuildPngDataUri(page.PngBytes);
                    Stage(dataUri, Save(dataUri, $"{c.Name} p{page.PageNumber}"), c.PdfPath, page.PageNumber);
                }
                string pages = c.First == c.Last ? $"page {c.First}" : $"pages {c.First}-{c.Last}";
                int n = c.Pages.Count;
                Echo($"[attached: {c.Name} {pages} ({n} image{(n == 1 ? "" : "s")}, ~{FormatTokens(n * TokensPerPage)} tokens)]");
            }
            return new PdfAttachOutcome { Send = true, ReadingNotes = notes };
        }

        /// <summary>
        /// The prompt text. A PDF with text on any page offers text first (Enter: its text
        /// included when <paramref name="inline"/>, else left for read_pdf) and a range for page
        /// images; one with no text layer offers images only (Enter = all pages).
        /// <paramref name="inlineTokens"/> is the size of the text as it would be included.
        /// </summary>
        internal static string BuildPromptMessage(
            string name, int pageCount, IReadOnlyList<int> textPages, IReadOnlyList<int> scannedPages,
            int textTokens, int? free, bool inline, int inlineTokens)
        {
            if (textPages.Count == 0)
                return $"{name} — {Pages(pageCount)}, no text layer " +
                       $"(~{FormatTokens(pageCount * TokensPerPage)} tokens, {FormatFree(free)}).\n" +
                       RangeHint;

            string scanned = scannedPages.Count > 0 ? $"; scanned: {ReadPdfTool.Ranges(scannedPages)}" : "";
            return $"{name} — {Pages(pageCount)}; text on {textPages.Count} of them " +
                   $"(~{FormatTokens(textTokens)} tokens as text){scanned}.\n" +
                   (inline
                       ? $"Enter = include its text in your message (~{FormatTokens(inlineTokens)} tokens)"
                       : $"Enter = let DM read it as text (too large to include: ~{FormatTokens(inlineTokens)} tokens)") +
                   " · or a page range for page images " +
                   $"(~{FormatTokens(TokensPerPage)} tokens/page, {FormatFree(free)}), e.g. 2-5 · Esc = skip";
        }

        /// <summary>The line added to the message for a PDF left for read_pdf (too large to include).</summary>
        internal static string BuildReadingNote(
            string path, int pageCount, IReadOnlyList<int> textPages, IReadOnlyList<int> scannedPages)
        {
            string scanned = scannedPages.Count > 0 ? $"; scanned: {ReadPdfTool.Ranges(scannedPages)}" : "";
            string all = pageCount == 1 ? "1" : $"1-{pageCount}";
            return $"[PDF attached for reading — NOT yet read: {path} — {Pages(pageCount)}; " +
                   $"text layer on page{(textPages.Count == 1 ? "" : "s")} {ReadPdfTool.Ranges(textPages)}{scanned}. " +
                   "You have not seen its contents. " +
                   $"Call read_pdf (pages=\"{all}\") before saying anything about what it contains; " +
                   "use view_image with pages for layout or scanned pages.]";
        }

        /// <summary>
        /// A small PDF's text as included in the message: a header line, every page as
        /// read_pdf formats it (<see cref="ReadPdfTool.FormatPages"/>; a scanned page carries its
        /// view_image hint), then an end marker.
        /// </summary>
        internal static string BuildInlineText(
            string path, int pageCount, IReadOnlyList<int> textPages, IReadOnlyList<int> scannedPages,
            IReadOnlyList<PdfPageText> pages)
        {
            string scanned = "";
            if (scannedPages.Count > 0)
            {
                string ranges = ReadPdfTool.Ranges(scannedPages);
                // view_image takes one range per call: name the first when there are several.
                int comma = ranges.IndexOf(',');
                string example = comma < 0 ? ranges : ranges.Substring(0, comma);
                scanned = $"; scanned: {ranges} (use view_image with pages=\"{example}\" for those" +
                          (comma < 0 ? ")" : ", one range per call)");
            }
            return $"[PDF text included: {path} — {Pages(pageCount)}; " +
                   $"text layer on page{(textPages.Count == 1 ? "" : "s")} {ReadPdfTool.Ranges(textPages)}{scanned}]\n" +
                   ReadPdfTool.FormatPages(pages) + "\n" +
                   "[end of PDF text]";
        }

        /// <summary>The message as sent: the typed text, then each note (or included text) on its own line(s).</summary>
        internal static string AppendNotes(string message, IReadOnlyList<string> notes)
            => notes.Count == 0 ? message : message + "\n\n" + string.Join("\n", notes);

        private static string Pages(int n) => $"{n} page{(n == 1 ? "" : "s")}";

        /// <summary>Rounds to thousands ("14K"); under 1,000 the exact figure.</summary>
        internal static string FormatTokens(int tokens)
            => tokens >= 1000
                ? Math.Round(tokens / 1000.0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "K"
                : tokens.ToString(CultureInfo.InvariantCulture);

        /// <summary>A cost and the room it exceeds, rounded like <see cref="FormatTokens"/> —
        /// or exact ("3,072" / "3,000") when rounding would print the same figure twice.</summary>
        internal static (string Cost, string Free) FormatCostAndFree(int cost, int free)
        {
            string costText = FormatTokens(cost), freeText = FormatTokens(free);
            if (costText != freeText)
                return (costText, freeText);
            return (cost.ToString("N0", CultureInfo.InvariantCulture), free.ToString("N0", CultureInfo.InvariantCulture));
        }

        private static string FormatFree(int? free)
            => free.HasValue ? $"{FormatTokens(free.Value)} free" : "unknown free";

        private static string DescribeFailure(PdfRenderResult result) => result.Failure switch
        {
            PdfRenderFailure.PageTooLarge =>
                $"page {result.FailedPage} rendered too large ({result.FailedPageBytes / (1024.0 * 1024.0):F1} MB; max 10 MB)",
            _ => result.Message ?? result.Failure.ToString(),
        };
    }

    /// <summary>What <see cref="PdfAttachFlow.RunAsync"/> decided.</summary>
    internal sealed class PdfAttachOutcome
    {
        internal static readonly PdfAttachOutcome Cancelled = new PdfAttachOutcome { Send = false };

        /// <summary>False when the send was cancelled while asking: nothing staged, do not send.</summary>
        public bool Send { get; init; }

        /// <summary>One entry per PDF read as text (its included text or a read_pdf note), to
        /// append to the message.</summary>
        public IReadOnlyList<string> ReadingNotes { get; init; } = Array.Empty<string>();
    }
}
