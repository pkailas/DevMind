// File: PdfPageAttachments.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The shared mechanics of attaching PDF pages as images: content-based PDF detection
// (%PDF- magic bytes, never the extension), rendering a page range to PNGs with the
// per-page size cap, and the PNG data: URI. /image and view_image both use it; each
// caller keeps its own policy (page caps, cursors, staging) and its own message texts,
// so failures come back as a kind plus the facts a caller needs to word them.

using System;
using System.Collections.Generic;
using System.IO;

namespace DevMind
{
    /// <summary>One rendered PDF page.</summary>
    public sealed class RenderedPdfPage
    {
        /// <summary>1-based page number in the source PDF.</summary>
        public int PageNumber { get; init; }

        /// <summary>Total pages in the source PDF.</summary>
        public int PageCount { get; init; }

        /// <summary>PNG-encoded page image.</summary>
        public byte[] PngBytes { get; init; }
    }

    /// <summary>Why <see cref="PdfPageAttachments.Render"/> produced no pages.</summary>
    public enum PdfRenderFailure
    {
        /// <summary>Rendered; <see cref="PdfRenderResult.Pages"/> holds every page.</summary>
        None,

        /// <summary>The range falls outside the document; <see cref="PdfRenderResult.Message"/>
        /// is PdfRasterizer's user-presentable text.</summary>
        OutOfRange,

        /// <summary>A page rendered over <see cref="ImagePathScanner.MaxImageBytes"/>; see
        /// <see cref="PdfRenderResult.FailedPage"/> and <see cref="PdfRenderResult.FailedPageBytes"/>.</summary>
        PageTooLarge,

        /// <summary>PDFium could not render the file (corrupt or password-protected);
        /// <see cref="PdfRenderResult.Message"/> is the exception's message.</summary>
        Unreadable,
    }

    /// <summary>The outcome of rendering a page range: all pages, or none and why.</summary>
    public sealed class PdfRenderResult
    {
        /// <summary>The rendered pages in order first..last; empty on failure.</summary>
        public IReadOnlyList<RenderedPdfPage> Pages { get; init; } = Array.Empty<RenderedPdfPage>();

        public PdfRenderFailure Failure { get; init; }

        public bool Succeeded => Failure == PdfRenderFailure.None;

        /// <summary>The exception message for <see cref="PdfRenderFailure.OutOfRange"/> and
        /// <see cref="PdfRenderFailure.Unreadable"/>; null otherwise.</summary>
        public string Message { get; init; }

        /// <summary>The first page over the cap (<see cref="PdfRenderFailure.PageTooLarge"/>); 0 otherwise.</summary>
        public int FailedPage { get; init; }

        /// <summary>That page's PNG size in bytes; 0 otherwise.</summary>
        public long FailedPageBytes { get; init; }

        /// <summary>Sum of the rendered pages' PNG sizes; 0 on failure.</summary>
        public long TotalBytes { get; init; }
    }

    /// <summary>Shared PDF-page attachment mechanics for /image and view_image.</summary>
    public static class PdfPageAttachments
    {
        /// <summary>True when the file starts with the PDF magic bytes <c>%PDF-</c>. I/O errors
        /// (missing, locked) propagate for the caller to report.</summary>
        public static bool IsPdf(string path)
        {
            var header = new byte[5];
            int read;
            using (var stream = File.OpenRead(path))
                read = stream.Read(header, 0, header.Length);
            return read == 5 && header[0] == (byte)'%' && header[1] == (byte)'P' && header[2] == (byte)'D'
                   && header[3] == (byte)'F' && header[4] == (byte)'-';
        }

        /// <summary>
        /// Renders the inclusive 1-based range <paramref name="first"/>..<paramref name="last"/>
        /// to PNGs (<see cref="PdfRasterizer.RenderPagesToPng"/>). All or nothing: a range outside
        /// the document, an unrenderable file, or any page over
        /// <see cref="ImagePathScanner.MaxImageBytes"/> returns no pages. Never throws for those.
        /// </summary>
        public static PdfRenderResult Render(string path, int first, int last)
            => Render(path, first, last, ImagePathScanner.MaxImageBytes);

        /// <summary><see cref="Render(string,int,int)"/> with the per-page cap as a parameter
        /// (tests: real renders never approach 10 MB).</summary>
        internal static PdfRenderResult Render(string path, int first, int last, long maxPageBytes)
        {
            List<PdfPageImage> rendered;
            try
            {
                rendered = PdfRasterizer.RenderPagesToPng(path, first, last);
            }
            catch (InvalidOperationException ex)
            {
                // Out of range — PdfRasterizer's message is already presentable.
                return new PdfRenderResult { Failure = PdfRenderFailure.OutOfRange, Message = ex.Message };
            }
            catch (Exception ex)
            {
                // Docnet exceptions derive from Exception, not InvalidOperationException.
                return new PdfRenderResult { Failure = PdfRenderFailure.Unreadable, Message = ex.Message };
            }

            var pages = new List<RenderedPdfPage>(rendered.Count);
            long totalBytes = 0;
            // Pages come back in order first..last, so the index gives the page number.
            for (int i = 0; i < rendered.Count; i++)
            {
                byte[] png = rendered[i].PngBytes;
                if (png.Length > maxPageBytes)
                    return new PdfRenderResult
                    {
                        Failure = PdfRenderFailure.PageTooLarge,
                        FailedPage = first + i,
                        FailedPageBytes = png.Length,
                    };
                totalBytes += png.Length;
                pages.Add(new RenderedPdfPage { PageNumber = first + i, PageCount = rendered[i].PageCount, PngBytes = png });
            }
            return new PdfRenderResult { Pages = pages, TotalBytes = totalBytes };
        }

        /// <summary>The data: URI the chat protocol expects for a rendered page.</summary>
        public static string BuildPngDataUri(byte[] pngBytes)
            => $"data:image/png;base64,{Convert.ToBase64String(pngBytes)}";
    }
}
