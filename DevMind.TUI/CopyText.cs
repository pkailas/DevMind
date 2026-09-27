// File: CopyText.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// What a copy of a transcript selection actually contains.
//
// The transcript on screen is not what the model wrote: the model's fenced code blocks
// carry a dim line-number gutter, display chrome that exists for the reader's eyes and
// would pollute a copy — pasted code with line numbers in it is a paste the reader has to
// clean up before it runs. So a copy walks the selection and skips the offsets the host
// has marked non-copyable, the same offset-based span store the draw path paints from.
//
// The decision is pure: given the document text, the selection range and a "is this
// offset non-copyable?" answer, it returns the text. The span store itself stays in the
// host, where it lives with the document it describes — the flag is set when the gutter
// is emitted, so a resize rebuild and a resume replay, which re-emit the transcript
// through the same code, re-mark it without any second store to keep in agreement.

using System;
using System.Text;

namespace DevMind
{
    /// <summary>Builds the text a copy of a selection carries, with display chrome skipped.</summary>
    public static class CopyText
    {
        /// <summary>
        /// The characters of <paramref name="documentText"/> in <c>[start, end)</c>, minus
        /// the offsets <paramref name="isNonCopyable"/> marks as display chrome (a gutter
        /// number). A selection that starts mid-gutter skips the gutter's remaining
        /// characters the same way it skips the whole gutter — the rule is per offset, not
        /// per span, so there is no special case for a partial one.
        /// </summary>
        /// <param name="documentText">The document, '\n' line endings — the same basis the
        /// span offsets and the Editor's selection offsets are on.</param>
        /// <param name="start">Selection start, inclusive, a document offset.</param>
        /// <param name="end">Selection end, exclusive, a document offset.</param>
        /// <param name="isNonCopyable">True for an offset inside a non-copyable span.</param>
        public static string Build(string documentText, int start, int end, Func<int, bool> isNonCopyable)
        {
            if (documentText == null) throw new ArgumentNullException(nameof(documentText));
            if (isNonCopyable == null) throw new ArgumentNullException(nameof(isNonCopyable));

            if (start < 0) start = 0;
            if (end > documentText.Length) end = documentText.Length;
            if (end <= start) return string.Empty;

            var sb = new StringBuilder(end - start);
            for (int i = start; i < end; i++)
                if (!isNonCopyable(i))
                    sb.Append(documentText[i]);

            return sb.ToString();
        }
    }
}
