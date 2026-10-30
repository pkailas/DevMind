// File: PdfAttachFlowTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// PdfAttachFlow — the per-PDF page-range prompt auto-attach runs before a typed message is
// sent. Every effect is a fake: the prompt is scripted (each answer is run through the
// flow's validate exactly as the dialog would, an error keeping it "open" for the next
// answer), rendering returns canned pages, save/stage/echo record what they get.

using System.Text;
using Xunit;

namespace DevMind.TUI.Tests
{
    public sealed class PdfAttachFlowTests
    {
        private const string Pdf = @"C:\docs\report.pdf";
        private const string Pdf2 = @"C:\docs\spec.pdf";

        private readonly List<(string DataUri, string? SavedPath)> _staged = new();
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
            Action<string>? onPrompt = null) => new PdfAttachFlow
        {
            GetPageCount = pageCount ?? (_ => 3),
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
            Stage = (dataUri, savedPath) => _staged.Add((dataUri, savedPath)),
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

        [Fact]
        public async Task Enter_AttachesAllPages_WithSavedPathsAndLabels()
        {
            Answer("");

            bool sent = await Flow().RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.True(sent);
            Assert.Equal("report.pdf — 3 pages (~3K tokens, 100K free).\n" + PdfAttachFlow.RangeHint, Assert.Single(_messages));
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
            Assert.StartsWith("report.pdf — 10 pages (~10K tokens, 3K free).", Assert.Single(_messages));
            Assert.Equal((Pdf, 1, 2), Assert.Single(_renders));
            Assert.Equal(2, _staged.Count);
        }

        [Fact]
        public async Task UnknownFree_ShowsUnknown_AndAllowsAnyRange()
        {
            Answer("");

            await Flow(pageCount: _ => 500, free: _ => null).RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.StartsWith("report.pdf — 500 pages (~512K tokens, unknown free).", Assert.Single(_messages));
            Assert.Empty(_errors);
            Assert.Equal((Pdf, 1, 500), Assert.Single(_renders));
        }

        [Fact]
        public async Task Esc_SkipsThePdf_AndStagesNothing()
        {
            Answer((string?)null);

            bool sent = await Flow().RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.True(sent); // the text still sends
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

            bool sent = await flow.RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.True(sent);
            Assert.Empty(_staged);
            Assert.Empty(_saveLabels);
            Assert.Equal("[PDF not attached: report.pdf — bad xref]", Assert.Single(_echo));
        }

        [Fact]
        public async Task PageCountFailure_SkipsWithoutPrompting()
        {
            var flow = Flow(pageCount: _ => throw new InvalidDataException("encrypted"));

            bool sent = await flow.RunAsync(new[] { Pdf }, 0, CancellationToken.None);

            Assert.True(sent);
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

            bool sent = await flow.RunAsync(new[] { Pdf, Pdf2 }, 0, cts.Token);

            Assert.False(sent);
            Assert.Single(_renders); // the first PDF was rendered...
            Assert.Empty(_staged);   // ...but nothing is staged
            Assert.Empty(_echo);
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
