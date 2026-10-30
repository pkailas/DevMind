// File: PdfAttachFlowTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// PdfAttachFlow — the per-PDF prompt auto-attach runs before a typed message is sent.
// Every effect is a fake: the prompt is scripted (each answer is run through the flow's
// validate exactly as the dialog would, an error keeping it "open" for the next answer),
// the text layer is canned (default: no text on any page, the image-only prompt),
// rendering returns canned pages, save/stage/echo record what they get.

using System.Text;
using Xunit;

namespace DevMind.TUI.Tests
{
    public sealed class PdfAttachFlowTests
    {
        private const string Pdf = @"C:\docs\report.pdf";
        private const string Pdf2 = @"C:\docs\spec.pdf";

        private readonly List<(string DataUri, string? SavedPath, string PdfPath, int Page)> _staged = new();
        private readonly List<string> _saveLabels = new();
        private readonly List<string> _echo = new();
        private readonly List<string> _messages = new();
        private readonly List<string> _errors = new();
        private readonly List<(string Path, int First, int Last)> _renders = new();
        private readonly List<int> _freeArgs = new();
        private readonly Queue<string?> _answers = new();

        private PdfAttachFlow Flow(
            Func<string, int>? pageCount = null,
            Func<int, int?>? free = null,
            Func<string, int, int, PdfRenderResult>? render = null,
            Action<string>? onPrompt = null,
            Func<string, int, PdfTextResult>? extract = null) => new PdfAttachFlow
        {
            GetPageCount = pageCount ?? (_ => 3),
            ExtractText = extract ?? ((_, n) => Layer(n, Array.Empty<int>())),
            FreeTokens = extra => { _freeArgs.Add(extra); return (free ?? (_ => 100_000))(extra); },
            Prompt = (title, message, validate, ct) =>
            {
                Assert.Equal(PdfAttachFlow.Title, title);
                _messages.Add(message);
                onPrompt?.Invoke(message);
                while (true)
                {
                    Assert.True(_answers.Count > 0, "prompt asked again but the script has no answer left");
                    string? answer = _answers.Dequeue();
                    if (answer == null || ct.IsCancellationRequested)
                        return Task.FromResult<string?>(null);
                    string? error = validate(answer);
                    if (error == null)
                        return Task.FromResult<string?>(answer);
                    _errors.Add(error);
                }
            },
            Render = render ?? FakeRender,
            Save = (dataUri, label) => { _saveLabels.Add(label); return @"C:\saved\" + label; },
            Stage = (dataUri, savedPath, pdfPath, page) => _staged.Add((dataUri, savedPath, pdfPath, page)),
            Echo = line => _echo.Add(line),
        };

        private PdfRenderResult FakeRender(string path, int first, int last)
        {
            _renders.Add((path, first, last));
            var pages = Enumerable.Range(first, last - first + 1)
                .Select(n => new RenderedPdfPage { PageNumber = n, PageCount = 99, PngBytes = Encoding.ASCII.GetBytes($"png{n}") })
                .ToList();
            return new PdfRenderResult { Pages = pages, TotalBytes = pages.Count * 4 };
        }

        private void Answer(params string?[] answers)
        {
            foreach (string? a in answers) _answers.Enqueue(a);
        }

        /// <summary>A text layer of <paramref name="pageCount"/> pages; the listed pages carry
        /// 4,000 chars of text each (~1K tokens as text), the rest none (scanned).</summary>
        private static PdfTextResult Layer(int pageCount, params int[] textPages) => new PdfTextResult
        {
            Pages = Enumerable.Range(1, pageCount).Select(n => textPages.Contains(n)
                ? new PdfPageText { PageNumber = n, Text = new string('x', 4000), HasText = true }
                : new PdfPageText { PageNumber = n, Text = "", HasText = false }).ToList(),
        };

        private const string ReadHint =
            "Read it with read_pdf (pages optional, e.g. pages=\"2-5\"); use view_image with pages for layout or scanned pages.]";

        [Fact]
        public async Task NoTextLayer_Enter_AttachesAllPages_WithSavedPathsAndLabels()
        {
            Answer("");

            var outcome = await Flow().RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.True(outcome.Send);
            Assert.Empty(outcome.ReadingNotes);
            Assert.Equal("report.pdf — 3 pages, no text layer (~3K tokens, 100K free).\n" + PdfAttachFlow.RangeHint, Assert.Single(_messages));
            Assert.Equal((Pdf, 1, 3), Assert.Single(_renders));
            Assert.Equal(new[] { "report.pdf p1", "report.pdf p2", "report.pdf p3" }, _saveLabels);
            Assert.Equal(3, _staged.Count);
            Assert.Equal(PdfPageAttachments.BuildPngDataUri(Encoding.ASCII.GetBytes("png1")), _staged[0].DataUri);
            Assert.Equal(@"C:\saved\report.pdf p3", _staged[2].SavedPath);
            Assert.Equal("[attached: report.pdf pages 1-3 (3 images, ~3K tokens)]", Assert.Single(_echo));
        }

        [Fact]
        public async Task Range_2to3_AttachesTwoPages()
        {
            Answer("2-3");

            await Flow().RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.Equal((Pdf, 2, 3), Assert.Single(_renders));
            Assert.Equal(new[] { "report.pdf p2", "report.pdf p3" }, _saveLabels);
            Assert.Equal(2, _staged.Count);
            Assert.Equal("[attached: report.pdf pages 2-3 (2 images, ~2K tokens)]", Assert.Single(_echo));
            // Each page carries its source PDF and page number for its placeholder.
            Assert.Equal(new[] { (Pdf, 2), (Pdf, 3) }, _staged.Select(s => (s.PdfPath, s.Page)));
        }

        [Fact]
        public async Task InvalidSpec_IsReAsked_WithParsePageSpecsMessage()
        {
            Answer("abc", "7", "2");

            await Flow().RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.Equal(new[]
            {
                "Invalid page 'abc' — use a page number, a range like 2-5, or 'all'.",
                "Page 7 is out of range — the PDF has 3 page(s).",
            }, _errors);
            Assert.Single(_messages); // one dialog, kept open
            Assert.Equal((Pdf, 2, 2), Assert.Single(_renders));
            Assert.Equal("[attached: report.pdf page 2 (1 image, ~1K tokens)]", Assert.Single(_echo));
        }

        [Fact]
        public async Task OverBudget_IsReAsked_WithTheFitMessage()
        {
            Answer("", "4-6", "1-2");

            await Flow(pageCount: _ => 10, free: _ => 3_000).RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.Equal(new[]
            {
                "That's ~10K tokens; only ~3K free — pick fewer pages.",
                "That's ~3,072 tokens; only ~3,000 free — pick fewer pages.", // same rounding → exact
            }, _errors);
            Assert.StartsWith("report.pdf — 10 pages, no text layer (~10K tokens, 3K free).", Assert.Single(_messages));
            Assert.Equal((Pdf, 1, 2), Assert.Single(_renders));
            Assert.Equal(2, _staged.Count);
        }

        [Fact]
        public async Task UnknownFree_ShowsUnknown_AndAllowsAnyRange()
        {
            Answer("");

            await Flow(pageCount: _ => 500, free: _ => null).RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.StartsWith("report.pdf — 500 pages, no text layer (~512K tokens, unknown free).", Assert.Single(_messages));
            Assert.Empty(_errors);
            Assert.Equal((Pdf, 1, 500), Assert.Single(_renders));
        }

        [Fact]
        public async Task Esc_SkipsThePdf_AndStagesNothing()
        {
            Answer((string?)null);

            var outcome = await Flow().RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.True(outcome.Send); // the text still sends
            Assert.Empty(outcome.ReadingNotes);
            Assert.Empty(_renders);
            Assert.Empty(_staged);
            Assert.Equal("[skipped: report.pdf]", Assert.Single(_echo));
        }

        [Fact]
        public async Task TwoPdfs_SecondPromptsFreeFigure_CountsTheFirstsPages()
        {
            Answer("1-2", "");

            // 1 typed image is pending; the second PDF must also see the first's 2 pages.
            await Flow(free: extra => 50_000 - extra * 1024).RunAsync(new[] { Pdf, Pdf2 }, 1, CancellationToken.None);

            Assert.Equal(new[] { 1, 3 }, _freeArgs);
            Assert.Contains("49K free", _messages[0]);  // 50,000 - 1,024
            Assert.Contains("47K free", _messages[1]);  // 50,000 - 3,072
            Assert.Equal(new[] { (Pdf, 1, 2), (Pdf2, 1, 3) }, _renders);
            Assert.Equal(5, _staged.Count);
            Assert.Equal(new[]
            {
                "[attached: report.pdf pages 1-2 (2 images, ~2K tokens)]",
                "[attached: spec.pdf pages 1-3 (3 images, ~3K tokens)]",
            }, _echo);
        }

        [Fact]
        public async Task RenderFailure_StagesNothing_AndEchoesTheError()
        {
            Answer("");
            var flow = Flow(render: (p, f, l) => new PdfRenderResult { Failure = PdfRenderFailure.Unreadable, Message = "bad xref" });

            var outcome = await flow.RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.True(outcome.Send);
            Assert.Empty(_staged);
            Assert.Empty(_saveLabels);
            Assert.Equal("[PDF not attached: report.pdf — bad xref]", Assert.Single(_echo));
        }

        [Fact]
        public async Task PageCountFailure_SkipsWithoutPrompting()
        {
            var flow = Flow(pageCount: _ => throw new InvalidDataException("encrypted"));

            var outcome = await flow.RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.True(outcome.Send);
            Assert.Empty(_messages);
            Assert.Empty(_staged);
            Assert.Equal("[PDF skipped: report.pdf could not be read (encrypted)]", Assert.Single(_echo));
        }

        [Fact]
        public async Task CancelledWhileAsking_ReturnsFalse_AndStagesNothingEvenForEarlierPdfs()
        {
            using var cts = new CancellationTokenSource();
            Answer("", "");
            var flow = Flow(onPrompt: m => { if (m.StartsWith("spec.pdf")) cts.Cancel(); });

            var outcome = await flow.RunAsync(new[] { Pdf, Pdf2 }, 0, cts.Token);

            Assert.False(outcome.Send);
            Assert.Empty(outcome.ReadingNotes);
            Assert.Single(_renders); // the first PDF was rendered...
            Assert.Empty(_staged);   // ...but nothing is staged
            Assert.Empty(_echo);
        }

        // ── Text-first: a PDF with a text layer ─────────────────────────────

        [Fact]
        public async Task TextPdf_Enter_StagesNothing_ReturnsReadingNote()
        {
            Answer("");

            var outcome = await Flow(extract: (_, n) => Layer(n, 1, 2, 3)).RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.True(outcome.Send);
            Assert.Equal(
                "report.pdf — 3 pages; text on 3 of them (~3K tokens as text).\n" +
                "Enter = let DM read it as text · or a page range for page images (~1K tokens/page, 100K free), e.g. 2-5 · Esc = skip",
                Assert.Single(_messages));
            Assert.Empty(_renders);
            Assert.Empty(_saveLabels);
            Assert.Empty(_staged);
            Assert.Equal($"[PDF attached for reading: {Pdf} — 3 pages; text layer on pages 1-3. " + ReadHint,
                Assert.Single(outcome.ReadingNotes));
            Assert.Equal("[PDF for reading: report.pdf (3 pages, ~3K tokens as text)]", Assert.Single(_echo));
        }

        [Fact]
        public async Task TextPdf_Range_AttachesPageImages_AndNoNote()
        {
            Answer("2-3");

            var outcome = await Flow(extract: (_, n) => Layer(n, 1, 2, 3)).RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.True(outcome.Send);
            Assert.Empty(outcome.ReadingNotes);
            Assert.Equal((Pdf, 2, 3), Assert.Single(_renders));
            Assert.Equal(new[] { (Pdf, 2), (Pdf, 3) }, _staged.Select(s => (s.PdfPath, s.Page)));
            Assert.Equal("[attached: report.pdf pages 2-3 (2 images, ~2K tokens)]", Assert.Single(_echo));
        }

        [Fact]
        public async Task TextPdf_RangeStillGetsTheFitCheck()
        {
            Answer("1-5", "1-2");

            await Flow(pageCount: _ => 8, free: _ => 3_000, extract: (_, n) => Layer(n, 1, 2))
                .RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.Equal("That's ~5K tokens; only ~3K free — pick fewer pages.", Assert.Single(_errors));
            Assert.Equal((Pdf, 1, 2), Assert.Single(_renders));
        }

        [Fact]
        public async Task MixedPdf_PromptAndNote_ListTheScannedPages()
        {
            Answer("");

            var outcome = await Flow(pageCount: _ => 8, extract: (_, n) => Layer(n, 1, 2, 3, 4, 5, 6, 8))
                .RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.Equal(
                "report.pdf — 8 pages; text on 7 of them (~7K tokens as text); scanned: 7.\n" +
                "Enter = let DM read it as text · or a page range for page images (~1K tokens/page, 100K free), e.g. 2-5 · Esc = skip",
                Assert.Single(_messages));
            Assert.Equal($"[PDF attached for reading: {Pdf} — 8 pages; text layer on pages 1-6, 8; scanned: 7. " + ReadHint,
                Assert.Single(outcome.ReadingNotes));
            Assert.Empty(_staged);
        }

        [Fact]
        public void PromptMessage_ThreeCases()
        {
            // No text layer: today's wording plus "no text layer"; Enter = all pages as images.
            Assert.Equal("scan.pdf — 2 pages, no text layer (~2K tokens, unknown free).\n" + PdfAttachFlow.RangeHint,
                PdfAttachFlow.BuildPromptMessage("scan.pdf", 2, Array.Empty<int>(), new[] { 1, 2 }, 0, null));
            // Text on every page: no scanned list.
            Assert.Equal(
                "a.pdf — 1 page; text on 1 of them (~250 tokens as text).\n" +
                "Enter = let DM read it as text · or a page range for page images (~1K tokens/page, 9K free), e.g. 2-5 · Esc = skip",
                PdfAttachFlow.BuildPromptMessage("a.pdf", 1, new[] { 1 }, Array.Empty<int>(), 250, 9_000));
            // Mixed: the scanned pages are listed.
            Assert.StartsWith("b.pdf — 7 pages; text on 3 of them (~4K tokens as text); scanned: 3, 5-7.\n",
                PdfAttachFlow.BuildPromptMessage("b.pdf", 7, new[] { 1, 2, 4 }, new[] { 3, 5, 6, 7 }, 4_000, 50_000));
        }

        [Fact]
        public void ReadingNote_SingleTextPage_SaysPage()
            => Assert.Equal($"[PDF attached for reading: {Pdf} — 2 pages; text layer on page 1; scanned: 2. " + ReadHint,
                PdfAttachFlow.BuildReadingNote(Pdf, 2, new[] { 1 }, new[] { 2 }));

        [Fact]
        public async Task TwoPdfs_OneReadAsText_OneAsRange_GivesOneNoteAndTheImages()
        {
            Answer("", "1-2");

            var outcome = await Flow(extract: (_, n) => Layer(n, 1, 2, 3)).RunAsync(new[] { Pdf, Pdf2 }, 0, CancellationToken.None);

            Assert.True(outcome.Send);
            Assert.StartsWith($"[PDF attached for reading: {Pdf} — 3 pages;", Assert.Single(outcome.ReadingNotes));
            Assert.Equal((Pdf2, 1, 2), Assert.Single(_renders));
            Assert.Equal(new[] { (Pdf2, 1), (Pdf2, 2) }, _staged.Select(s => (s.PdfPath, s.Page)));
            // The text PDF renders nothing, so the second PDF's free figure counts no pages for it.
            Assert.Equal(new[] { 0, 0 }, _freeArgs);
            Assert.Equal(new[]
            {
                "[PDF for reading: report.pdf (3 pages, ~3K tokens as text)]",
                "[attached: spec.pdf pages 1-2 (2 images, ~2K tokens)]",
            }, _echo);
        }

        [Fact]
        public async Task ExtractTextFailure_SkipsWithoutPrompting()
        {
            var flow = Flow(extract: (_, _) => new PdfTextResult { Failure = PdfRenderFailure.Unreadable, Message = "password required" });

            var outcome = await flow.RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.True(outcome.Send);
            Assert.Empty(outcome.ReadingNotes);
            Assert.Empty(_messages);
            Assert.Empty(_staged);
            Assert.Equal("[PDF skipped: report.pdf could not be read (password required)]", Assert.Single(_echo));
        }

        [Fact]
        public async Task ExtractTextThrows_SkipsWithoutPrompting()
        {
            var flow = Flow(extract: (_, _) => throw new InvalidDataException("bad xref"));

            var outcome = await flow.RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.True(outcome.Send);
            Assert.Empty(_messages);
            Assert.Equal("[PDF skipped: report.pdf could not be read (bad xref)]", Assert.Single(_echo));
        }

        [Fact]
        public void AppendNotes_AddsOneLinePerPdf_AfterABlankLine()
        {
            Assert.Equal("summarise it", PdfAttachFlow.AppendNotes("summarise it", Array.Empty<string>()));
            Assert.Equal("summarise them\n\n[note a]\n[note b]",
                PdfAttachFlow.AppendNotes("summarise them", new[] { "[note a]", "[note b]" }));
        }

        [Theory]
        [InlineData(0, "0")]
        [InlineData(999, "999")]
        [InlineData(1024, "1K")]
        [InlineData(14_336, "14K")]
        [InlineData(120_500, "121K")]
        public void FormatTokens_RoundsToThousands(int tokens, string expected)
            => Assert.Equal(expected, PdfAttachFlow.FormatTokens(tokens));
    }
}
