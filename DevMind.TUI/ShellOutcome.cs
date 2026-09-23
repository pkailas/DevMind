// File: ShellOutcome.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// How a shell command says whether it worked.
//
// Qwen Code can put the outcome glyph on the call line itself, because it runs the command
// and then prints. DevMind streams: the call line is on screen, and so is the output, before
// the exit code exists. Going back to rewrite the call line would mean editing text that is
// no longer the document's tail, which is the one edit the transcript deliberately cannot do.
//
// So the verdict is its own line at the bottom of the block. That turns out to read better
// than the glyph would have: the eye is already at the end of the output when the answer
// arrives, rather than having to go back up to a line it read ten lines ago.

using System;
using System.Globalization;

namespace DevMind
{
    /// <summary>The line that closes a shell block.</summary>
    public static class ShellOutcome
    {
        /// <summary>
        /// <c>✓ exit 0 · 2.1s</c> in green, or <c>x exit 1 · 2.1s</c> in red.
        /// <para>
        /// The duration is there because it is the number nobody thinks to ask for until a
        /// build starts taking four minutes, and it is free at this point — the clock has
        /// been running since the command started.
        /// </para>
        /// </summary>
        public static (string Line, OutputColor Color) Line(int exitCode, TimeSpan elapsed)
        {
            bool ok = exitCode == 0;
            string glyph = ok ? TranscriptVocabulary.GlyphOk : TranscriptVocabulary.GlyphFail;

            string text = string.Format(CultureInfo.InvariantCulture,
                "{0} exit {1} · {2}", glyph, exitCode, Duration(elapsed));

            return (text, ok ? OutputColor.Success : OutputColor.Error);
        }

        /// <summary>
        /// Tenths under a minute, then m:ss. A build that took 4.0s and one that took 4.04s
        /// are the same fact; one that took 4m 02s is a different one.
        /// </summary>
        public static string Duration(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            if (elapsed.TotalMinutes < 1)
                return string.Format(CultureInfo.InvariantCulture, "{0:0.0}s", elapsed.TotalSeconds);

            return string.Format(CultureInfo.InvariantCulture,
                "{0}m {1:00}s", (int)elapsed.TotalMinutes, elapsed.Seconds);
        }
    }
}
