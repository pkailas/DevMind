// File: DefaultPrompts.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The built-in system prompt used when no authored one is available — the floor every
// skin lands on, not a copy of the authored prompt.
//
// Design:
//   * ONE copy. This text previously existed four times over (TuiOptions, CliOptions,
//     HeadlessOptions, LlmClient), which is four places to drift and four places to
//     forget. The Options classes and LlmClient's last-resort fallback all point here.
//   * It carries the markdown-formatting guidance, not just the role sentence. The
//     authored prompt at %APPDATA%\devmind\system-prompt.md has had that guidance since
//     2026-09-20, so on this machine the fallback is never reached and its content looks
//     academic. On a machine that has never had that file — a fresh install, a
//     colleague's box — the fallback IS the prompt, and without this paragraph the
//     answers come back as prose with no tables and no fenced code.
//   * The formatting paragraph is worded identically to the authored prompt's, so the
//     two cannot disagree about what a well-formed answer looks like.
//   * A floor, not a mirror. Nothing else from the authored prompt belongs here: the
//     verification, research and reporting discipline it carries is the operator's
//     policy, and inventing a second copy of it is the drift this file exists to end.

namespace DevMind
{
    /// <summary>
    /// Built-in prompt text shared by every skin. Used only when no system prompt has
    /// been authored or passed in — see <see cref="SystemPromptFile"/> for the authored
    /// one, which replaces this when present.
    /// </summary>
    public static class DefaultPrompts
    {
        /// <summary>
        /// The default system prompt. Referenced as the initializer for
        /// <c>TuiOptions.SystemPrompt</c>, <c>CliOptions.SystemPrompt</c> and
        /// <c>HeadlessOptions.SystemPrompt</c>, and as <c>LlmClient</c>'s fallback when
        /// an <see cref="ILlmOptions"/> implementation supplies a blank one.
        /// </summary>
        public const string System =
            "You are a helpful coding assistant. Be concise and precise.\n" +
            "\n" +
            "Format answers as markdown: tables for comparisons, `inline code` for\n" +
            "identifiers, paths and commands, fenced blocks with a language tag for code.\n" +
            "Lead with the result, then the detail.";
    }
}
