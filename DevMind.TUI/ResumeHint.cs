// File: ResumeHint.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The line that makes --resume usable.
//
// Brief 07 gave DevMind `dm --resume <id>` and put the ids in /history. That left resuming
// as: launch, run /history, copy an id, quit, relaunch — five steps to undo one. The id you
// want is almost always the session you were just in, and the moment you want it is the
// moment after you left it.
//
// So it is printed on the way out, the way Claude Code and Qwen Code do it. The whole value
// is that it is in front of you without being asked for.
//
// It prints only when it would work. A hint that cannot work is worse than none: it invites
// someone to paste a command that fails, and the failure looks like a bug in resume rather
// than an answer to a question nobody asked.

using System;

namespace DevMind
{
    /// <summary>The exit hint. Pure — the caller owns when and where it is written.</summary>
    public static class ResumeHint
    {
        /// <summary>
        /// The two-line hint, or null when there is nothing worth saying.
        /// </summary>
        /// <param name="historyEnabled">
        /// False when the store is a NullHistoryStore. Nothing was written, so there is
        /// nothing to reopen — and the fix is a configuration change, not a flag.
        /// </param>
        /// <param name="turnsSaved">
        /// Turns actually persisted this session. Zero covers the common case of launching,
        /// looking at something and quitting: the id exists, but the session has nothing in
        /// it, and resuming an empty session is the same as starting a new one.
        /// </param>
        /// <param name="sessionId">What /history shows and --resume accepts.</param>
        public static string Build(bool historyEnabled, int turnsSaved, string sessionId)
        {
            if (!historyEnabled) return null;
            if (turnsSaved <= 0) return null;
            if (string.IsNullOrWhiteSpace(sessionId)) return null;

            // Indented under its own heading, so the command can be selected on its own line
            // and pasted without picking the prose off the front of it.
            return "Resume this session with:" + Environment.NewLine
                 + "  dm --resume " + sessionId;
        }
    }
}
