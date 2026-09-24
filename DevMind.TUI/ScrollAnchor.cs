// File: ScrollAnchor.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Keeping the reader's place across a rebuild.
//
// Re-rendering the whole transcript at a new width moves every offset in the document. For
// someone at the bottom watching a run that does not matter — the bottom is still the
// bottom. For someone who has scrolled up two screens to read something it matters a lot:
// the naive rebuild drops them at the top, or at whatever character index they happened to
// be at, which is a different paragraph now.
//
// So the anchor is an ENTRY, not an offset. Find which entry the top of the viewport was
// showing before, and put that same entry back at the top afterwards. The arithmetic is two
// binary searches over the offsets the renderer recorded, which makes it a pure function and
// therefore a tested one.

using System;
using System.Collections.Generic;

namespace DevMind
{
    /// <summary>Maps a document offset to the entry that produced it, and back. Pure.</summary>
    internal static class ScrollAnchor
    {
        /// <summary>
        /// The index of the entry whose rendered text covers <paramref name="offset"/>.
        /// </summary>
        /// <param name="offsets">
        /// Each entry's start offset, ascending — what the renderer recorded as it drew.
        /// </param>
        /// <returns>
        /// The entry index, or -1 when there is nothing to anchor to. An offset past the end
        /// belongs to the last entry: the reader is looking at the newest thing there is.
        /// </returns>
        public static int Find(IReadOnlyList<int> offsets, int offset)
        {
            if (offsets == null || offsets.Count == 0) return -1;
            if (offset <= offsets[0]) return 0;

            int lo = 0, hi = offsets.Count - 1;
            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                if (offsets[mid] <= offset) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>
        /// Where that same entry starts after the rebuild.
        /// </summary>
        /// <returns>
        /// The new offset, or 0 when the entry no longer exists — which happens when the
        /// trim dropped it while the reader was looking at it. The top of the transcript is
        /// the honest answer to "your anchor is gone"; guessing an offset would put them
        /// somewhere they never were.
        /// </returns>
        public static int Restore(IReadOnlyList<int> offsets, int entryIndex)
        {
            if (offsets == null || offsets.Count == 0) return 0;
            if (entryIndex < 0) return 0;
            if (entryIndex >= offsets.Count) return offsets[offsets.Count - 1];
            return offsets[entryIndex];
        }

        /// <summary>
        /// How far the anchor entry moved: add this to a scroll position to keep the same
        /// entry under the same row.
        /// </summary>
        public static int Delta(IReadOnlyList<int> before, IReadOnlyList<int> after, int offset)
        {
            int entry = Find(before, offset);
            if (entry < 0) return 0;
            return Restore(after, entry) - Restore(before, entry);
        }
    }
}
