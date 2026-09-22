// File: TranscriptNoise.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// One decision about what the TUI transcript is allowed to show, for two doors.
//
// The engine emits its per-iteration state as bracketed status lines, because the CLI skin
// and the history record want them. The TUI does not: the status bar already carries the
// context meter, the token rate and the iteration, so writing the same numbers into the
// scrollback says everything twice and buries the model's actual output.
//
// Suppression used to live as a private method on TuiAgenticHost, applied in
// IAgenticHost.AppendOutput. That guarded only one of the two doors. [LLM], [CONTEXT] and
// [TOOL_USE] never come through AppendOutput at all — Core emits them through the SSE
// onToken callback, which the TUI's token path renders as prose. So the filter was real,
// correct, and had no effect on the three lines that dominate the transcript.
//
// The decision therefore lives here, free of Terminal.Gui and of the host, so both doors
// can call it and one test can pin it.

using System;

namespace DevMind
{
    /// <summary>
    /// Decides whether a line of engine output belongs in the TUI transcript.
    /// Pure and UI-free: both the host-side <c>AppendOutput</c> door and the streaming
    /// token path in <c>Program.cs</c> call this, so the two cannot drift apart.
    /// </summary>
    public static class TranscriptNoise
    {
        /// <summary>
        /// True when <c>DEVMIND_TUI_VERBOSE</c> is set — the firehose escape hatch. Read once:
        /// the variable is a launch-time choice, and re-reading it per token would put an
        /// environment lookup on the hot streaming path.
        /// </summary>
        public static readonly bool Verbose =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DEVMIND_TUI_VERBOSE"));

        /// <summary>
        /// Whether <paramref name="text"/> is engine churn the transcript should drop,
        /// using the process-wide <see cref="Verbose"/> setting.
        /// </summary>
        public static bool IsSuppressed(string text) => IsSuppressed(text, Verbose);

        /// <summary>
        /// Whether <paramref name="text"/> is engine churn the transcript should drop.
        /// <para>
        /// Dropped: <c>[TOOL_USE]</c>, <c>[LLM]</c>, <c>[AGENTIC] Iteration</c>, and the routine
        /// <c>[CONTEXT]</c> usage meter (numeric, <c>~</c> estimate, or <c>Working:</c>).
        /// </para>
        /// <para>
        /// Kept: every real tool-call line (<c>[READ]</c>, <c>[SHELL]</c>, <c>[FILE]</c>,
        /// <c>[PATCH]</c>, <c>[GREP]</c>, <c>[FIND]</c>, <c>[LSP]</c>, <c>[DIFF]</c>,
        /// <c>[TEST]</c>), every <c>[AGENTIC]</c> terminal state (Task complete / Depth cap /
        /// Aborted / Cancelled / Run-succeeded), and every <c>[CONTEXT]</c> signal line
        /// (CRITICAL, Warning, Hard/Soft trim, Compacting, Brainwash, …). Those are events,
        /// not churn, and the status bar has nowhere to put them.
        /// </para>
        /// </summary>
        public static bool IsSuppressed(string text, bool verbose)
        {
            if (verbose) return false;
            if (string.IsNullOrEmpty(text)) return false;

            // Engine status lines arrive as a complete "[TAG] …\n" call, often with a leading
            // "\n" (Core's onToken lines). Find the first non-whitespace char and match there.
            int i = 0;
            while (i < text.Length && (text[i] == '\n' || text[i] == '\r' || text[i] == ' ' || text[i] == '\t'))
                i++;
            if (i >= text.Length) return false;

            // [TOOL_USE] / [LLM] — always per-turn churn.
            if (StartsAt(text, i, "[TOOL_USE]")) return true;
            if (StartsAt(text, i, "[LLM]")) return true;

            // [AGENTIC] — drop only the per-iteration counter; KEEP terminal states.
            if (StartsAt(text, i, "[AGENTIC] Iteration")) return true;

            // [CONTEXT] — drop the routine usage meter; KEEP the signal lines.
            if (StartsAt(text, i, "[CONTEXT] "))
            {
                int j = i + "[CONTEXT] ".Length;
                if (j < text.Length)
                {
                    char c = text[j];
                    if (char.IsDigit(c) || c == '~') return true;
                    if (StartsAt(text, j, "Working:")) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a streamed token is a DevMind-internal status/diagnostic line
        /// (<c>[CONTEXT]</c>, <c>[LLM]</c>, <c>[TOOL_USE]</c>, <c>[DIAG…]</c>, <c>[DROPPED]</c>,
        /// <c>[SQUEEZED]</c>, …) rather than model output.
        /// <para>
        /// A different question from <see cref="IsSuppressed(string, bool)"/>, and both are
        /// needed: this one decides whether a token counts as output at all (it must not feed
        /// the token counter, and must not flip the thinking→generating phase — the engine
        /// injects some of these before the model has streamed a single token). Suppression
        /// then decides whether the transcript shows it.
        /// </para>
        /// <para>
        /// Matches a leading all-caps bracket tag; mixed-case model brackets (e.g. "[Fact]")
        /// are intentionally NOT matched, so real model content still counts.
        /// </para>
        /// </summary>
        public static bool IsInternalStatusLine(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            int i = 0;
            while (i < token.Length && char.IsWhiteSpace(token[i])) i++;
            if (i >= token.Length || token[i] != '[') return false;
            i++;
            if (i >= token.Length || !char.IsUpper(token[i])) return false;
            for (; i < token.Length; i++)
            {
                char c = token[i];
                if (c == ']') return true;
                if (!(char.IsUpper(c) || char.IsDigit(c) || c == '_' || c == '-' || c == ' ')) return false;
            }
            return false;
        }

        /// <summary>
        /// The token path's routing decision: does this streamed token belong in the
        /// transcript? False for the engine churn whose numbers the status bar already
        /// carries — those are pushed there instead.
        /// </summary>
        public static bool ShouldRenderToken(string visible, bool isStatusLine, bool verbose)
            => !(isStatusLine && IsSuppressed(visible, verbose));

        /// <summary>Ordinal prefix match at a given offset, without allocating a substring.</summary>
        public static bool StartsAt(string s, int offset, string prefix)
            => offset + prefix.Length <= s.Length
               && string.CompareOrdinal(s, offset, prefix, 0, prefix.Length) == 0;
    }
}
