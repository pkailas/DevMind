// File: TranscriptModel.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The transcript's source, kept so it can be drawn again.
//
// Until now the transcript WAS the painted document: spans went into a rope and the thing
// they were painted from was gone. That is why every width-dependent decision had to be
// made once and lived with — a table laid out at 200 columns is shredded by the Editor's
// own wrap at 70, and brief 14 could only decline to re-lay it out because there was
// nothing left to lay out from.
//
// So the entries here are the INPUT to each emitter, not its output: the text the engine
// passed, the markdown line the model wrote, the two versions of a patched file. Re-drawing
// is then replaying the same calls, which is what makes the live path and the rebuild the
// same code rather than two renderers that have to be kept in agreement.
//
// The model is the source of truth and the document is a projection of it.

using System;
using System.Collections.Generic;

namespace DevMind
{
    /// <summary>Which emitter an entry replays through.</summary>
    internal enum TranscriptEntryKind
    {
        /// <summary>Engine output, already past the noise filter.</summary>
        Output,
        /// <summary>One completed line of model prose, before table buffering.</summary>
        Prose,
        /// <summary>End of a response: release anything the table buffer holds.</summary>
        ProseFlush,
        /// <summary>A fenced code block, or a file listing's body.</summary>
        Code,
        /// <summary>A file listing — retained as its input so the cap and notice re-derive.</summary>
        Listing,
        /// <summary>A patch, retained as both versions so the diff re-lays out.</summary>
        Diff,
    }

    /// <summary>One retained call. Immutable; the payload is whatever that emitter takes.</summary>
    internal sealed class TranscriptEntry
    {
        public TranscriptEntryKind Kind { get; }

        public string Text { get; }
        public OutputColor Color { get; }
        public string Language { get; }
        public bool NestUnderCall { get; }
        public string Path { get; }
        public string Second { get; }
        public int Count { get; }

        private TranscriptEntry(TranscriptEntryKind kind, string text, OutputColor color,
                                string language, bool nestUnderCall, string path, string second, int count)
        {
            Kind          = kind;
            Text          = text ?? string.Empty;
            Color         = color;
            Language      = language;
            NestUnderCall = nestUnderCall;
            Path          = path;
            Second        = second;
            Count         = count;
        }

        public static TranscriptEntry Output(string text, OutputColor color)
            => new TranscriptEntry(TranscriptEntryKind.Output, text, color, null, false, null, null, 0);

        public static TranscriptEntry Prose(string line)
            => new TranscriptEntry(TranscriptEntryKind.Prose, line, OutputColor.Normal, null, false, null, null, 0);

        public static TranscriptEntry ProseFlush()
            => new TranscriptEntry(TranscriptEntryKind.ProseFlush, "", OutputColor.Normal, null, false, null, null, 0);

        public static TranscriptEntry Code(string code, string language, bool nestUnderCall)
            => new TranscriptEntry(TranscriptEntryKind.Code, code, OutputColor.Normal, language, nestUnderCall, null, null, 0);

        public static TranscriptEntry Listing(string content, string fullPath, int lineCount)
            => new TranscriptEntry(TranscriptEntryKind.Listing, content, OutputColor.Normal, null, false, fullPath, null, lineCount);

        public static TranscriptEntry Diff(string oldContent, string newContent, string path)
            => new TranscriptEntry(TranscriptEntryKind.Diff, oldContent, OutputColor.Normal, null, false, path, newContent, 0);

        /// <summary>Roughly what this entry costs to keep. Used only by the cap.</summary>
        public int Weight => Text.Length + (Second?.Length ?? 0) + 64;
    }

    /// <summary>
    /// The retained transcript, in arrival order. Not thread-safe: every caller is on the
    /// UI thread or the single agentic worker, exactly as the emitters already were.
    /// </summary>
    internal sealed class TranscriptModel
    {
        private readonly List<TranscriptEntry> _entries = new List<TranscriptEntry>();
        private int _weight;

        public IReadOnlyList<TranscriptEntry> Entries => _entries;

        public int Weight => _weight;

        public void Append(TranscriptEntry entry)
        {
            if (entry == null) return;
            _entries.Add(entry);
            _weight += entry.Weight;
        }

        /// <summary>
        /// Drop the oldest entries until the retained weight is under <paramref name="keep"/>.
        /// <para>
        /// WHOLE entries only. Half a diff or half a table is not a thing this can re-render —
        /// the payload is an emitter's argument, not a run of characters — so the cap that
        /// used to trim painted text now trims retained calls. The document is a projection
        /// of what survives, which is why it stays bounded without a second cap of its own.
        /// </para>
        /// </summary>
        /// <returns>
        /// How many entries were dropped. The host needs the number, not a yes: the scroll
        /// anchor is an entry INDEX into the list as it was last rendered, and every entry
        /// dropped from the front shifts that index by one.
        /// </returns>
        public int Trim(int max, int keep)
        {
            if (max <= 0 || _weight <= max) return 0;

            int dropped = 0;
            while (_entries.Count > 1 && _weight > keep)
            {
                _weight -= _entries[0].Weight;
                _entries.RemoveAt(0);
                dropped++;
            }

            return dropped;
        }

        public void Clear()
        {
            _entries.Clear();
            _weight = 0;
        }
    }
}
