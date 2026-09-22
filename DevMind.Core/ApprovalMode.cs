// File: ApprovalMode.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Whether the agent asks before it changes anything.
//
// DevMind's default has always been "just do it": patches apply unseen unless the match was
// fuzzy, and create_file / append_file / delete_file / rename_file / run_shell had no
// confirmation path at all. That is the right default for work you are driving yourself,
// and the wrong one for watching a change land, or for handing the TUI to someone who
// should not have unattended write access.
//
// Two modes, not four. Qwen Code offers default / auto-edit / yolo / plan; DevMind has no
// plan mode and no edit-versus-shell split, so two modes that are honest beat four that are
// half-wired. The enum is named for the question it answers rather than for its members, so
// a third can be added without renaming anything.
//
// The decision lives in ApprovalPolicy rather than at the six call sites, because six
// scattered `if (mode == ...)` checks is how a new mutation tool silently ships ungated —
// adding a MutationKind forces a decision here, in one place, with a test over every pair.

namespace DevMind
{
    /// <summary>How much the agent asks before mutating anything.</summary>
    public enum ApprovalMode
    {
        /// <summary>
        /// Apply changes without asking — DevMind's long-standing behaviour, and the default.
        /// <c>AlwaysConfirmPatch</c> still forces the diff card for patches independently.
        /// </summary>
        Auto,

        /// <summary>
        /// Ask before every mutation: every patch goes through the diff-preview card
        /// (exact matches included), and each file write, delete, rename and shell command
        /// is confirmed first. A refusal is reported to the model AS a refusal.
        /// </summary>
        Manual,
    }

    /// <summary>
    /// The side-effecting operations approval can gate. Read-only tools are deliberately
    /// absent: there is no mode in which reading a file asks permission.
    /// </summary>
    public enum MutationKind
    {
        /// <summary>patch_file — gated via the existing diff-preview card, not a yes/no prompt.</summary>
        Patch,
        CreateFile,
        AppendFile,
        DeleteFile,
        RenameFile,

        /// <summary>run_shell. NOT run_build or run_tests — see <see cref="ApprovalPolicy"/>.</summary>
        Shell,
    }

    /// <summary>
    /// Parsing and display for the mode, shared by --mode, devmind.json and /mode so the
    /// three cannot disagree about what "manual" spells.
    /// </summary>
    public static class ApprovalModeText
    {
        /// <summary>
        /// Parses "auto" or "manual", case- and whitespace-insensitively. False for anything
        /// else, leaving the caller to keep its default: a typo in a launch argument should
        /// start in the safe-by-default mode, not refuse to start.
        /// </summary>
        public static bool TryParse(string text, out ApprovalMode mode)
        {
            mode = ApprovalMode.Auto;
            if (string.IsNullOrWhiteSpace(text)) return false;

            switch (text.Trim().ToLowerInvariant())
            {
                case "auto":   mode = ApprovalMode.Auto;   return true;
                case "manual": mode = ApprovalMode.Manual; return true;
                default:       return false;
            }
        }

        /// <summary>The canonical lower-case spelling, for display and for round-tripping.</summary>
        public static string Format(ApprovalMode mode)
            => mode == ApprovalMode.Manual ? "manual" : "auto";
    }

    /// <summary>
    /// Pure policy: does this mode require approval for this kind of mutation? No host, no
    /// options object, no I/O — so every pair is testable directly.
    /// </summary>
    public static class ApprovalPolicy
    {
        /// <summary>
        /// True when the operation must be confirmed before it happens.
        /// <para>
        /// <c>run_build</c> and <c>run_tests</c> are execution but not mutation, and are
        /// deliberately NOT gated in either mode. A build is how you verify the edit you
        /// just approved; asking twice per iteration is what makes people turn the mode off,
        /// and a mode nobody leaves on protects nothing. They also cannot write outside what
        /// the build system already does, which is not a boundary this gate was built for.
        /// </para>
        /// </summary>
        public static bool Requires(ApprovalMode mode, MutationKind kind)
        {
            switch (mode)
            {
                case ApprovalMode.Manual:
                    // Every kind, including Patch — which the executor honours by forcing
                    // the diff card rather than by asking a yes/no question.
                    return true;

                case ApprovalMode.Auto:
                default:
                    return false;
            }
        }
    }
}
