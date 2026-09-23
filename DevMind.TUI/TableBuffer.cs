// File: TableBuffer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Deciding when a table has ended.
//
// Prose arrives one completed line at a time, and that is fine for every other markdown
// construct: bold opens and closes inside a line. A table cannot be drawn until its last row
// has arrived, because the column widths depend on every row — so lines have to be held, and
// holding a line is the dangerous part. Held text that is never released is text the model
// wrote and the reader never saw.
//
// So the release conditions are the whole design. A pipe line with no separator under it is
// prose that happens to contain pipes and is released immediately; anything that is not a
// row closes the table and is then itself processed normally; and Flush releases whatever is
// held, which is what makes a table at the very end of a response appear at all.
//
// Nothing here knows about Terminal.Gui. It takes lines and hands back either prose to print
// or a table to lay out, so every one of those conditions is a test.

using System;
using System.Collections.Generic;

namespace DevMind
{
    /// <summary>What the buffer wants done with what it just gave back.</summary>
    internal enum TableBufferAction
    {
        /// <summary>Nothing to emit — the line is being held.</summary>
        Hold,
        /// <summary>Print these lines as ordinary prose, in order.</summary>
        Prose,
        /// <summary>Lay out this table, then print any trailing prose lines.</summary>
        Table,
    }

    /// <summary>The buffer's answer for one fed line.</summary>
    internal sealed class TableBufferResult
    {
        public TableBufferAction Action { get; }

        /// <summary>Prose to print, in order. For <see cref="TableBufferAction.Table"/> these
        /// follow the table — the line that closed it is not swallowed.</summary>
        public IReadOnlyList<string> Prose { get; }

        public PipeTableModel Table { get; }

        public TableBufferResult(TableBufferAction action, IReadOnlyList<string> prose, PipeTableModel table)
        {
            Action = action;
            Prose  = prose ?? Array.Empty<string>();
            Table  = table;
        }

        public static readonly TableBufferResult Held =
            new TableBufferResult(TableBufferAction.Hold, null, null);
    }

    /// <summary>
    /// Collects pipe-table lines out of a stream of prose lines. Stateful across calls, in
    /// arrival order.
    /// </summary>
    internal sealed class TableBuffer
    {
        private readonly List<string> _held = new List<string>();
        private bool _isTable;   // the separator row has been seen; this is definitely a table

        /// <summary>True while lines are being withheld from the transcript.</summary>
        public bool IsHolding => _held.Count > 0;

        /// <summary>
        /// Feed one completed prose line (trailing newline included or not — it is preserved
        /// on anything released as prose).
        /// </summary>
        public TableBufferResult Feed(string line)
        {
            string probe = (line ?? string.Empty).TrimEnd('\r', '\n');

            if (_held.Count == 0)
            {
                // Not in a table: only a pipe line is worth holding, and only on the chance
                // that a separator follows it.
                if (PipeTable.IsPipeLine(probe))
                {
                    _held.Add(line);
                    return TableBufferResult.Held;
                }
                return Passthrough(line);
            }

            if (!_isTable)
            {
                // One pipe line held. This second line decides what it was.
                if (PipeTable.IsSeparatorLine(probe))
                {
                    _held.Add(line);
                    _isTable = true;
                    return TableBufferResult.Held;
                }

                // It was prose all along. Release the held line AND this one, in order —
                // a line held on a guess must never cost the reader a line of text.
                var released = new List<string>(_held) { line };
                Reset();
                return new TableBufferResult(TableBufferAction.Prose, released, null);
            }

            // In a table. A row continues it; anything else ends it.
            if (PipeTable.IsPipeLine(probe))
            {
                _held.Add(line);
                return TableBufferResult.Held;
            }

            return Close(trailing: line);
        }

        /// <summary>
        /// End of the response. Releases whatever is held — a table that ends a reply has no
        /// closing line to trigger it, and this is the only thing standing between that and a
        /// silently dropped answer.
        /// </summary>
        public TableBufferResult Flush()
        {
            if (_held.Count == 0) return TableBufferResult.Held;
            return _isTable ? Close(trailing: null)
                            : ReleaseAsProse();
        }

        private TableBufferResult Close(string trailing)
        {
            PipeTableModel table = PipeTable.TryParse(_held);

            if (table == null)
            {
                // Held lines that turned out not to parse. Never drop them.
                var lines = new List<string>(_held);
                if (trailing != null) lines.Add(trailing);
                Reset();
                return new TableBufferResult(TableBufferAction.Prose, lines, null);
            }

            var after = new List<string>();
            if (trailing != null) after.Add(trailing);
            Reset();
            return new TableBufferResult(TableBufferAction.Table, after, table);
        }

        private TableBufferResult ReleaseAsProse()
        {
            var lines = new List<string>(_held);
            Reset();
            return new TableBufferResult(TableBufferAction.Prose, lines, null);
        }

        private static TableBufferResult Passthrough(string line)
            => new TableBufferResult(TableBufferAction.Prose, new[] { line }, null);

        private void Reset()
        {
            _held.Clear();
            _isTable = false;
        }
    }
}
