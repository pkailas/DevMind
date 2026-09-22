// File: ApprovalModeControl.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The one place the approval mode changes. /mode and the Shift+Tab binding both come
// through here, so they cannot drift into doing different things — a key that flips the
// live options but forgets to persist, or a command that persists but leaves the executor
// reading the old value, are both bugs you would only find by trying the other one.
//
// It is also the only part of the toggle that a test can reach. The binding itself is
// Terminal.Gui and needs a real terminal and a real keypress; what a test CAN pin is that
// flipping twice returns to where it started, that each flip is written to disk, and that
// the text the operator sees names the mode. So all of that lives here, free of any view.
//
// The transcript line matters as much as the flash. A status-bar flash is gone in two
// seconds and an accidental Shift+Tab is silent after that — leaving someone wondering why
// the agent started asking about every write. A line in the transcript is the record that
// survives, and it is timestamped by its position in the conversation.

using System;

namespace DevMind
{
    /// <summary>
    /// Applies and describes approval-mode changes. No Terminal.Gui: the caller owns the
    /// status bar and the transcript, this owns what changes and what it is called.
    /// </summary>
    public static class ApprovalModeControl
    {
        /// <summary>The other mode. Two modes, so a toggle is an alternation.</summary>
        public static ApprovalMode Next(ApprovalMode current)
            => current == ApprovalMode.Manual ? ApprovalMode.Auto : ApprovalMode.Manual;

        /// <summary>
        /// Writes the mode to the live options the executor reads at its next dispatch, and
        /// persists it so it survives a restart. Returns the mode, so a caller can flip and
        /// report in one expression.
        /// </summary>
        /// <remarks>
        /// Both halves matter and neither is optional. Without the options write the change
        /// does nothing this session; without the config write it does nothing next session,
        /// and a mode you have to re-enable every launch is one people stop enabling.
        /// A failed save is swallowed by TuiConfig.Save itself (it already treats persistence
        /// as best-effort), so the session still gets the mode it asked for.
        /// </remarks>
        public static ApprovalMode Apply(ApprovalMode mode, TuiOptions options, TuiConfig config)
        {
            if (options != null)
                options.ApprovalMode = mode;

            if (config != null)
            {
                config.ApprovalMode = mode;
                config.Save();
            }

            return mode;
        }

        /// <summary>
        /// The transcript line for a mode change — the durable record, unlike the flash.
        /// Says what the mode DOES, not just its name: "manual" alone does not tell you that
        /// the next create_file will stop and wait.
        /// </summary>
        public static string TranscriptLine(ApprovalMode mode)
            => mode == ApprovalMode.Manual
                ? "[MODE] manual — every mutation will ask before it happens\n"
                : "[MODE] auto — mutations apply without asking\n";

        /// <summary>The short status-bar flash. Same fact, in the width a chip has.</summary>
        public static string StatusFlash(ApprovalMode mode)
            => mode == ApprovalMode.Manual
                ? "Approval: manual — mutations will ask"
                : "Approval: auto — mutations apply";
    }
}
