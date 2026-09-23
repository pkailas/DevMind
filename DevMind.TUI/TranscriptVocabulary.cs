// File: TranscriptVocabulary.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Translating the engine's [TAG] lines into the transcript's own vocabulary.
//
// The engine writes its tags for the CLI and the headless transcript, where they are the
// contract: an operator reading a piped session, and the resume filter, both match on them.
// The TUI has a screen, and on a screen a tag is the wrong device. Every line starts in
// column zero in the same weight, so the reader parses "[WRITE GUARD]" / "[FILE]" /
// "[SHELL]" / "[AGENTIC]" to work out whether they are looking at a tool call, its output,
// the loop reporting, or the model talking — a lookup they perform on every line.
//
// Qwen Code and Claude Code spend three cheap devices instead: a GLYPH that encodes the
// outcome, a LABEL that is a word rather than a tag, and INDENTATION that nests output under
// the call that produced it. This does the first two. It is a translation at the TUI's door,
// not a rename: the tags keep flowing to everything else exactly as they did.
//
// Nothing is ever dropped. An unrecognised tag comes through as a neutral state line with
// its tag spelled as a word, because a transcript that silently swallows a line it has not
// been taught about is worse than one that shows an ugly line.

using System;
using System.Collections.Generic;

namespace DevMind
{
    /// <summary>What a translated transcript line is, which decides how it nests.</summary>
    public enum TranscriptKind
    {
        /// <summary>A tool call — opens a block; its output nests underneath.</summary>
        Call,
        /// <summary>Text with no tag of its own — belongs to the call above it.</summary>
        Output,
        /// <summary>A loop or session event — closes any open block, never nests.</summary>
        State,
        /// <summary>The user's own echoed input. Passes through untouched.</summary>
        User,
    }

    /// <summary>One line of engine output, translated. <see cref="Render"/> is what is drawn.</summary>
    public readonly struct TranscriptEvent
    {
        /// <summary>Outcome marker, or empty for output lines.</summary>
        public readonly string Glyph;

        /// <summary>The tag as a word — "Shell", "Write", "Edit" — or empty.</summary>
        public readonly string Label;

        /// <summary>Everything after the tag, verbatim.</summary>
        public readonly string Detail;

        public readonly TranscriptKind Kind;
        public readonly OutputColor Color;

        public TranscriptEvent(string glyph, string label, string detail,
                               TranscriptKind kind, OutputColor color)
        {
            Glyph  = glyph ?? string.Empty;
            Label  = label ?? string.Empty;
            Detail = detail ?? string.Empty;
            Kind   = kind;
            Color  = color;
        }

        /// <summary>The line as it appears, without indentation or newline.</summary>
        public string Render()
        {
            if (Glyph.Length == 0 && Label.Length == 0) return Detail;

            var sb = new System.Text.StringBuilder();
            if (Glyph.Length > 0) sb.Append(Glyph);
            if (Label.Length > 0)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Label);
            }
            if (Detail.Length > 0)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Detail);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Turns a tagged engine line into a glyph, a label and a detail. Pure — the only thing
    /// it knows about is text, which is why every row of the table below is a test.
    /// </summary>
    public static class TranscriptVocabulary
    {
        // Glyphs. `x` is deliberately ASCII, as Qwen Code has it: failure is the one outcome
        // that must be legible in a terminal whose font is missing half of Unicode.
        public const string GlyphOk      = "✓";  // ✓ success
        public const string GlyphFail    = "x";       //   failure
        public const string GlyphWarn    = "⚠";  // ⚠ warning
        public const string GlyphRunning = "›";  // › a call whose outcome is not known yet
        public const string GlyphState   = "●";  // ● a loop or session event
        public const string GlyphSteer   = "⟲";  // ⟲ the turn was redirected
        public const string GlyphAsk     = "?";       //   the agent needs the caller

        /// <summary>The heading the answer path carries when the model calls ask_caller.</summary>
        public const string NeedsInputHeading = "NEEDS INPUT";

        /// <summary>How <c>NEEDS INPUT — questions for the caller:</c> renders.</summary>
        public const string NeedsInputLine = GlyphAsk + " Needs input";

        // Tag → label. Where the tag is already the right word it still appears here, so the
        // table is the complete list of what this transcript knows how to say — a tag that is
        // missing is a test failure, not a silent fallback in production.
        private static readonly Dictionary<string, string> Labels =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // File and shell work. FILE and WRITE GUARD both become "Write": the reader
                // cares that a file was written, not which of the engine's two code paths
                // said so.
                ["SHELL"]       = "Shell",
                ["FILE"]        = "Write",
                ["WRITE GUARD"] = "Write",
                ["SANDBOX"]     = "Write",
                ["APPEND"]      = "Append",
                ["READ"]        = "Read",
                ["AUTO-READ"]   = "Read",
                ["PATCH"]       = "Edit",
                ["DELETE"]      = "Delete",
                ["RENAME"]      = "Rename",

                // Search and inspection.
                ["GREP"]        = "Grep",
                ["FIND"]        = "Find",
                ["LIST"]        = "List",
                ["DIFF"]        = "Diff",
                ["LSP"]         = "Lsp",
                ["TEST"]        = "Test",
                ["DEBUG"]       = "Debug",

                // Knowledge and data.
                ["MEMORY"]      = "Memory",
                ["LIBRARY"]     = "Library",
                ["RECALL"]      = "Recall",
                ["LIST_CACHE"]  = "Cache",
                ["WEB"]         = "Web",
                ["LEARN"]       = "Learn",
                ["SQL"]         = "Sql",
                ["DIGEST"]      = "Digest",
                ["SCRATCHPAD"]  = "Scratchpad",

                // The /resolve flow keeps its own word: a conflict is not an edit that
                // happened, it is an edit that is waiting for a decision.
                ["MERGE"]          = "Merge",
                ["MERGE CONFLICT"] = "Merge",

                ["SKIPPED"]     = "Skipped",
                ["BLOCKED"]     = "Blocked",
                ["STALE"]       = "Stale",
                ["ERROR"]       = "Error",
            };

        // Tags whose line is an event, not a call: they close an open block instead of
        // opening one, and they carry no label because the sentence after them already
        // reads as one ("● Task complete", "● Context CRITICAL: …").
        private static readonly Dictionary<string, string> States =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AGENTIC"]  = "",
                ["CONTEXT"]  = "Context",
                ["DROPPED"]  = "Context",
                ["SQUEEZED"] = "Context",
                ["MODE"]     = "Mode",
                ["RESUME"]   = "Resume",
                ["SCREEN"]   = "Screen",
            };

        /// <summary>
        /// Translate one line. <paramref name="line"/> is a single line with no trailing
        /// newline; <paramref name="color"/> is the colour the engine asked for, which is
        /// where the outcome comes from — Success, Warning and Error are exactly the three
        /// things a glyph has to say.
        /// </summary>
        public static TranscriptEvent Translate(string line, OutputColor color)
        {
            string text = line ?? string.Empty;

            // The user's own echo is already the shape every other tool uses. Left alone.
            if (color == OutputColor.Input && text.StartsWith(">", StringComparison.Ordinal))
                return new TranscriptEvent("", "", text, TranscriptKind.User, color);

            if (!TrySplitTag(text, out string tag, out string detail))
                return new TranscriptEvent("", "", text, TranscriptKind.Output, color);

            // [STEER] is its own gesture — the turn was redirected while it ran, which is
            // neither an outcome nor a loop event.
            if (tag == "STEER")
                return new TranscriptEvent(GlyphSteer, "Steer", detail, TranscriptKind.State, color);

            if (States.TryGetValue(tag, out string stateLabel))
                return new TranscriptEvent(GlyphState, stateLabel, detail, TranscriptKind.State, color);

            // "[X ERROR]" is the error form of whatever X is, and always fails regardless of
            // the colour the caller chose.
            if (tag.EndsWith(" ERROR", StringComparison.Ordinal))
            {
                string stem = tag.Substring(0, tag.Length - " ERROR".Length);
                string errLabel = Labels.TryGetValue(stem, out string mapped) ? mapped : AsWord(stem);
                return new TranscriptEvent(GlyphFail, errLabel, detail, TranscriptKind.Call, OutputColor.Error);
            }

            if (Labels.TryGetValue(tag, out string label))
                return new TranscriptEvent(GlyphFor(color), label, Trim(tag, detail),
                                           TranscriptKind.Call, color);

            // "[FOO GUARD]" and anything else this has not been taught. Shown, never dropped.
            return new TranscriptEvent(GlyphState, AsWord(tag), detail, TranscriptKind.State, color);
        }

        /// <summary>
        /// Whether this tag has a translation. Exposed so a test can walk the emitting files
        /// and fail when a new tag is added without a word to say it in — the fallback keeps
        /// such a line visible, but visible as "● Some Tag" among "✓ Write" lines, which
        /// reads as a defect in the feature rather than a gap in a table.
        /// </summary>
        public static bool Knows(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            if (tag == "STEER") return true;
            if (Labels.ContainsKey(tag) || States.ContainsKey(tag)) return true;

            return tag.EndsWith(" ERROR", StringComparison.Ordinal)
                && Labels.ContainsKey(tag.Substring(0, tag.Length - " ERROR".Length));
        }

        /// <summary>
        /// Whether a line of model prose is the ask_caller heading. It arrives through the
        /// answer path rather than the tool door — it is something the model said, not
        /// something the engine reported — so it is recognised here and rendered as the event
        /// it actually is: the run has stopped and is waiting for a person.
        /// </summary>
        public static bool IsNeedsInputHeading(string line)
            => line != null && line.TrimStart().StartsWith(NeedsInputHeading, StringComparison.Ordinal);

        // The verb the label already says. "[FILE] Saved x" becomes "✓ Write Saved x"
        // without this, and a line that says the same thing twice reads as a line that has
        // not been thought about. Only the exact wording the emit sites use is listed: a
        // prefix that does not match is simply left alone.
        private static readonly Dictionary<string, string[]> Redundant =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["FILE"]      = new[] { "Saved " },
                ["APPEND"]    = new[] { "Appended to ", "Created " },
                ["PATCH"]     = new[] { "Applied to " },
                ["READ"]      = new[] { "Loaded " },
                ["AUTO-READ"] = new[] { "Loading " },
                ["DELETE"]    = new[] { "Deleted ", "Removed " },
                ["RENAME"]    = new[] { "Renamed " },
            };

        private static string Trim(string tag, string detail)
        {
            if (!Redundant.TryGetValue(tag, out string[] prefixes)) return detail;

            foreach (string prefix in prefixes)
            {
                if (detail.StartsWith(prefix, StringComparison.Ordinal))
                    return detail.Substring(prefix.Length);
            }
            return detail;
        }

        /// <summary>The outcome glyph for a colour — the engine's only statement of how it went.</summary>
        public static string GlyphFor(OutputColor color)
        {
            switch (color)
            {
                case OutputColor.Success: return GlyphOk;
                case OutputColor.Error:   return GlyphFail;
                case OutputColor.Warning: return GlyphWarn;
                default:                  return GlyphRunning;
            }
        }

        /// <summary>
        /// Split "[TAG] rest" into its tag and the rest. The tag must be upper-case: a model
        /// writing "[Fact]" or "[1]" in prose is not tagging anything.
        /// </summary>
        public static bool TrySplitTag(string text, out string tag, out string detail)
        {
            tag = null;
            detail = null;
            if (string.IsNullOrEmpty(text) || text[0] != '[') return false;

            int close = text.IndexOf(']');
            if (close <= 1) return false;

            // The first character decides it. "[1]" is a footnote and "[Fact]" is an
            // attribute the model is talking about; neither is the engine reporting.
            if (!char.IsUpper(text[1])) return false;

            for (int i = 1; i < close; i++)
            {
                char c = text[i];
                if (!(char.IsUpper(c) || char.IsDigit(c) || c == ' ' || c == '_' || c == '-'))
                    return false;
            }

            tag = text.Substring(1, close - 1);
            detail = text.Substring(close + 1).TrimStart();

            // "[SHELL] > cmd" — the angle bracket was the tag's way of saying "a command
            // follows"; the label says that now, and two markers read as a quotation.
            if (detail.StartsWith("> ", StringComparison.Ordinal))
                detail = detail.Substring(2);

            return true;
        }

        /// <summary>"LIST_CACHE ERROR" → "List_cache Error" — a tag spelled as words.</summary>
        public static string AsWord(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return string.Empty;

            string[] parts = tag.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1).ToLowerInvariant();
            }
            return string.Join(" ", parts);
        }
    }
}
