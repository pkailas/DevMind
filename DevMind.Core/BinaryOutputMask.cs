// File: BinaryOutputMask.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Collapses binary data in shell/build/test output before the agent reads it.
//
// job-1694 dumped a PDF from a PowerShell script and several KB of raw JPEG bytes landed in
// its context: NUL runs and control characters, broken up by short printable fragments
// ("JFIF", a lone "C", the "├┐├" mojibake of an 0xFF marker read through the OEM code page).
// None of it is readable, and all of it costs tokens.
//
// A "binary region" starts and ends on a control character (anything char.IsControl except
// \t \r \n, plus U+FFFD, which is what an undecodable byte becomes). Printable gaps of up to
// MaxGap characters between control characters are bridged, because real binary is never
// pure control codes. The region is collapsed only when it holds at least MinControlChars
// control characters AND they are at least half of it — so ANSI-coloured lines (one ESC per
// ~5-10 chars) and ordinary non-ASCII text (letters, box-drawing) are never touched.
//
// The marker counts characters. A decoded binary byte is one character (one U+FFFD for
// invalid UTF-8, one code-page char otherwise), so the count is the byte count the agent
// would have received.

using System.Globalization;
using System.Text;

namespace DevMind
{
    public static class BinaryOutputMask
    {
        /// <summary>Fewest control characters a region needs before it is collapsed.</summary>
        internal const int MinControlChars = 16;

        /// <summary>Longest run of printable characters bridged inside one region.</summary>
        internal const int MaxGap = 8;

        /// <summary>Returns <paramref name="output"/> with every binary region replaced by
        /// "[... N bytes of binary data ...]". Text without control characters is returned as-is.</summary>
        public static string Collapse(string output)
        {
            if (string.IsNullOrEmpty(output)) return output ?? string.Empty;

            int first = IndexOfControl(output, 0);
            if (first < 0) return output;

            var sb = new StringBuilder(output.Length);
            sb.Append(output, 0, first);
            int i = first;
            while (i < output.Length)
            {
                if (!IsControl(output[i]))
                {
                    int next = IndexOfControl(output, i);
                    int end = next < 0 ? output.Length : next;
                    sb.Append(output, i, end - i);
                    i = end;
                    continue;
                }

                // Extend the region from control char to control char across short gaps.
                int start = i, last = i, controls = 1, gap = 0;
                for (int j = i + 1; j < output.Length; j++)
                {
                    if (IsControl(output[j])) { last = j; controls++; gap = 0; }
                    else if (++gap > MaxGap) break;
                }

                int length = last - start + 1;
                if (controls >= MinControlChars && controls * 2 >= length)
                    sb.Append("[... ")
                      .Append(length.ToString("N0", CultureInfo.InvariantCulture))
                      .Append(" bytes of binary data ...]");
                else
                    sb.Append(output, start, length);
                i = last + 1;
            }
            return sb.ToString();
        }

        private static bool IsControl(char c) =>
            (char.IsControl(c) && c != '\t' && c != '\r' && c != '\n') || c == '�';

        private static int IndexOfControl(string s, int from)
        {
            for (int i = from; i < s.Length; i++)
                if (IsControl(s[i])) return i;
            return -1;
        }
    }
}
