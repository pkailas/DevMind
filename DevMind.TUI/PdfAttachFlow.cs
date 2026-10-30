// File: PdfAttachFlow.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The pause-before-send for PDFs mentioned in a typed message (auto-attach). For each
// PDF, in order: count its pages, ask which pages to attach (showing the cost and the
// room left), render the chosen pages. Once every PDF has been asked, the rendered pages
// are saved and staged as user attachments of the message. Nothing is staged until the
// end, so cancelling the whole send part-way leaves no stray images for the next message.
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

        /// <summary>The input hint shown under the cost line.</summary>
        internal const string RangeHint = "Range? Enter = all, e.g. 2-5 or 7, Esc = skip";

        /// <summary>Estimated prompt tokens per attached page (the client's per-image estimate).</summary>
        internal const int TokensPerPage = LlmClient.ImageTokenEstimatePerPart;

        /// <summary>Page count of a PDF; throws when the file cannot be read.</summary>
        public Func<string, int> GetPageCount { get; init; } = PdfRasterizer.GetPageCount;

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

        /// <summary>Stages a page image as a user attachment: (dataUri, savedPath).</summary>
        public Action<string, string?> Stage { get; init; } = (_, _) => { };

        /// <summary>Writes one dim transcript line (no trailing newline).</summary>
        public Action<string> Echo { get; init; } = _ => { };

        private sealed class Chosen
        {
            public string Name = "";
            public int First;
            public int Last;
            public IReadOnlyList<RenderedPdfPage> Pages = Array.Empty<RenderedPdfPage>();
        }

        /// <summary>
        /// Asks about each PDF in <paramref name="pdfs"/>, then saves and stages every chosen
        /// page. <paramref name="otherPendingImages"/> counts images that will join the message
        /// but are not staged yet (typed image paths), so the free figure accounts for them.
        /// Returns false when <paramref name="ct"/> was cancelled while asking: nothing is
        /// staged and the caller must not send.
        /// </summary>
        public async Task<bool> RunAsync(IReadOnlyList<string> pdfs, int otherPendingImages, CancellationToken ct)
        {
            var chosen = new List<Chosen>();
            int chosenPages = 0;

            foreach (string path in pdfs)
            {
                if (ct.IsCancellationRequested)
                    return false;

                string name = Path.GetFileName(path);
                int pageCount;
                try
                {
                    pageCount = GetPageCount(path);
                }
                catch (Exception ex)
                {
                    Echo($"[PDF skipped: {name} could not be read ({ex.Message})]");
                    continue;
                }

                // Pages chosen for earlier PDFs in this message are not staged yet either.
                int? free = FreeTokens(otherPendingImages + chosenPages);
                string message =
                    $"{name} — {pageCount} page{(pageCount == 1 ? "" : "s")} " +
                    $"(~{FormatTokens(pageCount * TokensPerPage)} tokens, {FormatFree(free)}).\n" +
                    RangeHint;

                (int First, int Last) range = (1, pageCount);
                string? Validate(string text)
                {
                    string spec = (text ?? "").Trim();
                    (int First, int Last) r;
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
                    return false;
                if (answer == null)
                {
                    Echo($"[skipped: {name}]");
                    continue;
                }

                PdfRenderResult result = Render(path, range.First, range.Last);
                if (!result.Succeeded)
                {
                    Echo($"[PDF not attached: {name} — {DescribeFailure(result)}]");
                    continue;
                }
                chosen.Add(new Chosen { Name = name, First = range.First, Last = range.Last, Pages = result.Pages });
                chosenPages += result.Pages.Count;
            }

            if (ct.IsCancellationRequested)
                return false;

            foreach (Chosen c in chosen)
            {
                foreach (RenderedPdfPage page in c.Pages)
                {
                    string dataUri = PdfPageAttachments.BuildPngDataUri(page.PngBytes);
                    Stage(dataUri, Save(dataUri, $"{c.Name} p{page.PageNumber}"));
                }
                string pages = c.First == c.Last ? $"page {c.First}" : $"pages {c.First}-{c.Last}";
                int n = c.Pages.Count;
                Echo($"[attached: {c.Name} {pages} ({n} image{(n == 1 ? "" : "s")}, ~{FormatTokens(n * TokensPerPage)} tokens)]");
            }
            return true;
        }

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
}
