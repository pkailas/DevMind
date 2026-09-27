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
// Three modes. Qwen Code offers default / auto-edit / yolo / plan; DevMind has no
// edit-versus-shell split, so three modes that are honest beat five that are half-wired.
// Plan is the odd one out: it does not ask, it REFUSES — analyze-only, with a plain, actionable
// refusal the model can finish with a plan instead of retrying. The enum is named for the
// question it answers rather than for its members, so a fourth can be added without renaming.
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

        /// <summary>
        /// Analyze only. Every mutation is REFUSED outright — no prompt, no diff card —
        /// and the refusal the model receives says so in plain terms, so the run ends in a
        /// concrete plan. Read-only tools, run_build and run_tests work as usual.
        /// </summary>
        Plan,
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

        /// <summary>
        /// save_memory — writes .devmind/memory into the repo. Not gated in Auto or Manual
        /// today (the executor dispatches it without a confirm), so those modes keep that
        /// exact behaviour; only <see cref="ApprovalMode.Plan"/> refuses it.
        /// </summary>
        Memory,

        /// <summary>
        /// run_sql with allow_write — the one run_sql call that can change a database.
        /// The read-only default of the same tool is NOT a mutation (it is the tool's way
        /// of looking at data, like read_file) and stays free in every mode; the gate in
        /// the executor applies only when allow_write is true. Not gated in Auto or Manual
        /// today, so those modes keep that exact behaviour; only
        /// <see cref="ApprovalMode.Plan"/> refuses it.
        /// </summary>
        SqlWrite,
    }

    /// <summary>
    /// Parsing and display for the mode, shared by --mode, devmind.json and /mode so the
    /// three cannot disagree about what "plan" spells.
    /// </summary>
    public static class ApprovalModeText
    {
        /// <summary>
        /// Parses "auto", "manual" or "plan", case- and whitespace-insensitively. False for
        /// anything else, leaving the caller to keep its default: a typo in a launch argument
        /// should start in the safe-by-default mode, not refuse to start.
        /// </summary>
        public static bool TryParse(string text, out ApprovalMode mode)
        {
            mode = ApprovalMode.Auto;
            if (string.IsNullOrWhiteSpace(text)) return false;

            switch (text.Trim().ToLowerInvariant())
            {
                case "auto":   mode = ApprovalMode.Auto;   return true;
                case "manual": mode = ApprovalMode.Manual; return true;
                case "plan":   mode = ApprovalMode.Plan;   return true;
                default:       return false;
            }
        }

        /// <summary>The canonical lower-case spelling, for display and for round-tripping.</summary>
        public static string Format(ApprovalMode mode) => mode switch
        {
            ApprovalMode.Manual => "manual",
            ApprovalMode.Plan   => "plan",
            _                   => "auto",
        };
    }

    /// <summary>The answer to "may this mutation happen?" — one of three, so no caller
    /// can silently invent a fourth (e.g. a Plan mode that merely asks).</summary>
    public enum ApprovalDecision
    {
        /// <summary>Proceed without asking.</summary>
        Allow,

        /// <summary>Ask the operator first; a decline is reported to the model as a refusal.</summary>
        Ask,

        /// <summary>
        /// Do not perform it, and do not ask. The operator chose analyze-only: the refusal
        /// <see cref="ApprovalPolicy.PlanRefusalMessage"/> is what the model receives, so it
        /// finishes with a plan rather than retrying.
        /// </summary>
        Refuse,
    }

    /// <summary>
    /// Pure policy: may this mode perform this kind of mutation? No host, no options
    /// object, no I/O — so every pair is testable directly.
    /// <para>
    /// <see cref="Decide"/> is the decision; <see cref="Requires"/> is its legacy projection
    /// ("is it Ask?"), kept so existing callers keep compiling and keep their exact
    /// behaviour: Auto never asks, Manual always asks — and in Plan, <c>Requires</c> is
    /// true, i.e. Plan is NOT an "ask" for any caller still on the old API. The executor's
    /// own gate reads <c>Decide</c> directly.
    /// </para>
    /// <para>
    /// <c>run_build</c> and <c>run_tests</c> are execution but not mutation, and are
    /// deliberately NOT gated in any mode. A build is how you verify the edit you just
    /// approved (or, in Plan, the ONLY verification you are left with); asking twice per
    /// iteration is what makes people turn the mode off, and a mode nobody leaves on
    /// protects nothing. They also cannot write outside what the build system already does,
    /// which is not a boundary this gate was built for.
    /// </para>
    /// </summary>
    public static class ApprovalPolicy
    {
        /// <summary>
        /// What the model receives when Plan refuses an operation. Plain and actionable on
        /// purpose: a bare "refused" is what makes a model retry the same tool and burn the
        /// rest of its budget on the same wall.
        /// </summary>
        public static string PlanRefusalMessage(string operation)
            => "[PLAN MODE] Refused: " + operation + ". The user has DevMind in plan mode: "
             + "analyze and propose changes, don't make them. Finish with a concrete plan: "
             + "files, changes, order, and how you'll verify.";

        /// <summary>
        /// The decision for one (mode, kind) pair. Auto → Allow. Manual → Ask. Plan →
        /// Refuse, for every kind including Patch.
        /// </summary>
        public static ApprovalDecision Decide(ApprovalMode mode, MutationKind kind)
        {
            switch (mode)
            {
                case ApprovalMode.Manual:
                    // Every kind EXCEPT the kinds Manual never gated: a new kind must keep
                    // the mode's exact existing behaviour outside Plan. save_memory and
                    // run_sql-allow-write are both dispatched today without a confirm —
                    // Manual asking for either now would be a behaviour change, not a Plan
                    // feature.
                    if (kind == MutationKind.Memory || kind == MutationKind.SqlWrite)
                        return ApprovalDecision.Allow;

                    // Every other kind, including Patch — which the executor honours by
                    // forcing the diff card rather than by asking a yes/no question.
                    return ApprovalDecision.Ask;

                case ApprovalMode.Plan:
                    // No prompt, no diff card: the refusal IS the answer, and it is the same
                    // for every kind, so a new MutationKind cannot ship ungated here either.
                    return ApprovalDecision.Refuse;

                case ApprovalMode.Auto:
                default:
                    return ApprovalDecision.Allow;
            }
        }

        /// <summary>
        /// True when the operation must be confirmed before it happens.
        /// </summary>
        public static bool Requires(ApprovalMode mode, MutationKind kind)
            => Decide(mode, kind) == ApprovalDecision.Ask;
    }
}

