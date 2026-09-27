// File: ProseWidth.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The width the model's prose wraps to. One place, because a wrap width that is
// computed two ways drifts: the live path and the rebuild would each pick a
// number, and at some width nobody tested the transcript would not line up.
//
// The cap is prose-only on purpose. Tables, code and diffs are the model's own
// output wearing prose clothing — they keep the full view width, because
// columns are a claim the table is making and a cap would quietly break it.

using System;

namespace DevMind
{
    /// <summary>
    /// The columns a prose line may occupy: the view's width, narrowed to the
    /// cap when the cap is set.
    /// </summary>
    internal static class ProseWidth
    {
        /// <summary>
        /// <paramref name="viewWidth"/> narrowed to <paramref name="cap"/>. A cap of
        /// 0 (or negative) means "not set" and the view width stands.
        /// </summary>
        public static int Effective(int viewWidth, int cap) =>
            cap > 0 ? Math.Min(viewWidth, cap) : viewWidth;
    }
}
