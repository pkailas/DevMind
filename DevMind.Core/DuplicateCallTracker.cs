// File: DuplicateCallTracker.cs  v1.0
//
// WHY: Analysis of 13,573 logged agent turns found 3,972 byte-identical
// discovery calls (read_file / grep_file / find_in_files) repeated within a
// single session, across 129 of 221 sessions — one session repeated itself 349
// times. The agent re-asks questions it already has answers to, burning a full
// round-trip each time.
//
// WHAT THIS IS NOT: a content cache. File content is already cached and
// staleness-checked by FileCache.InvalidateIfStale, so a repeat call is cheap
// on disk. The cost is the TURN, not the I/O. This tracker therefore does not
// suppress or substitute any result — it ANNOTATES a repeat so the model can
// notice the loop it is in. Results remain byte-for-byte what they would have
// been, which means a stale annotation can never produce a wrong answer.

using System;
using System.Collections.Concurrent;

namespace DevMind
{
    /// <summary>
    /// Tracks repeated identical discovery calls within a session and produces a
    /// short annotation for repeats. Thread-safe. Never alters tool results.
    /// </summary>
    public sealed class DuplicateCallTracker
    {
        /// <summary>Max distinct keys retained before the tracker resets itself.
        /// Bounds memory on very long sessions; a reset only loses annotations.</summary>
        private const int MaxKeys = 2000;

        private readonly ConcurrentDictionary<string, Entry> _seen =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        private sealed class Entry
        {
            public int Count;
            public int FirstTurn;
        }

        /// <summary>
        /// Records a call and returns an annotation when it is an exact repeat, or
        /// null on first sight. <paramref name="turnNumber"/> may be 0 when the caller
        /// has no turn counter; the annotation then omits the turn reference.
        /// </summary>
        /// <param name="tool">Tool name, e.g. "read_file".</param>
        /// <param name="argsKey">Stable serialization of the arguments. Callers must
        /// build this identically for identical calls; differing whitespace or
        /// ordering will read as a distinct call.</param>
        /// <param name="turnNumber">Current turn, or 0 if unknown.</param>
        public string Note(string tool, string argsKey, int turnNumber = 0)
        {
            if (string.IsNullOrEmpty(tool) || argsKey == null)
                return null;

            if (_seen.Count >= MaxKeys)
                _seen.Clear();

            string key = tool + "\u0001" + argsKey;
            bool repeat = false;
            int count = 1;
            int firstTurn = turnNumber;

            _seen.AddOrUpdate(
                key,
                _ => new Entry { Count = 1, FirstTurn = turnNumber },
                (_, existing) =>
                {
                    lock (existing)
                    {
                        existing.Count++;
                        count = existing.Count;
                        firstTurn = existing.FirstTurn;
                    }
                    repeat = true;
                    return existing;
                });

            if (!repeat)
                return null;

            string where = firstTurn > 0 ? $" (first at turn {firstTurn})" : "";
            string ordinal = count == 2 ? "2nd" : count == 3 ? "3rd" : count + "th";

            if (count >= 3)
            {
                return $"[REPEAT x{count}] This is the {ordinal} identical {tool} call{where}. " +
                       "You already have this result earlier in the conversation. Re-reading it will " +
                       "not produce new information — if you are stuck, change approach rather than " +
                       "repeating the lookup, or state what is blocking you.";
            }

            return $"[REPEAT] Identical {tool} call{where} — this result is already above in the conversation.";
        }

        /// <summary>Clears tracked calls. Call on session reset (e.g. /new).</summary>
        public void Reset() => _seen.Clear();

        /// <summary>Distinct call keys currently tracked. Diagnostics only.</summary>
        public int TrackedKeys => _seen.Count;
    }
}
