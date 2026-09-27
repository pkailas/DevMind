// File: PromptHistory.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shell-style prompt history for the TUI input box. Pure class — no
// Terminal.Gui dependency — so the walk/dedupe/cap/persistence logic is
// testable without a UI. Up recalls previously submitted prompts, Down walks
// forward; the list persists across sessions as JSON Lines (one JSON-encoded
// string per line) so multi-line prompts survive.

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DevMind
{
    /// <summary>
    /// In-memory prompt history with optional JSON-Lines persistence and
    /// shell-style Up/Down browsing. All file I/O failures are swallowed —
    /// this class never throws into the UI.
    /// </summary>
    public sealed class PromptHistory
    {
        private readonly List<string> _entries;
        private readonly string? _path;
        private readonly int _cap;

        // Browsing state. _savedDraft is the text the user had before the first
        // Up; _index is the position in _entries currently shown (-1 = not browsing).
        private bool _browsing;
        private int _index = -1;
        private string _savedDraft = string.Empty;

        /// <summary>
        /// Create a history and load any persisted entries.
        /// </summary>
        /// <param name="path">
        /// Persistence file path (JSON Lines), or null for in-memory only.
        /// The caller resolves %APPDATA% — this class never does.
        /// </param>
        /// <param name="cap">Maximum number of entries (oldest dropped past this).</param>
        public PromptHistory(string? path, int cap = 500)
        {
            _path = path;
            _cap = Math.Max(1, cap);
            _entries = new List<string>();
            Load();
        }

        /// <summary>True while an Up/Down walk is in progress.</summary>
        public bool IsBrowsing => _browsing;

        /// <summary>
        /// Record a submitted prompt. Null/whitespace is ignored; an entry
        /// identical to the most recent one is skipped. Appends to memory and
        /// the file; trims to the cap (oldest dropped, file rewritten).
        /// Resets any in-progress browsing.
        /// </summary>
        public void Add(string entry)
        {
            ResetBrowsing();

            if (string.IsNullOrWhiteSpace(entry)) return;
            if (_entries.Count > 0 &&
                _entries[_entries.Count - 1] == entry)
                return;

            _entries.Add(entry);
            if (_entries.Count > _cap)
                _entries.RemoveRange(0, _entries.Count - _cap);

            Persist();
        }

        /// <summary>
        /// Recall the previous (older) prompt. The first call saves
        /// <paramref name="currentDraft"/> and returns the newest entry;
        /// further calls walk older. Returns false at the oldest entry
        /// (stays put).
        /// </summary>
        public bool TryPrevious(string currentDraft, out string text)
        {
            if (!_browsing)
            {
                if (_entries.Count == 0)
                {
                    text = currentDraft;
                    return false;
                }
                _savedDraft = currentDraft;
                _index = _entries.Count - 1;
                _browsing = true;
            }
            else if (_index > 0)
            {
                _index--;
            }
            else
            {
                text = _entries[0];
                return false; // at the oldest — stay put
            }

            text = _entries[_index];
            return true;
        }

        /// <summary>
        /// Walk forward (newer). Stepping past the newest returns the saved
        /// draft and ends browsing. Returns false when not browsing.
        /// </summary>
        public bool TryNext(out string text)
        {
            if (!_browsing)
            {
                text = string.Empty;
                return false;
            }

            if (_index < _entries.Count - 1)
            {
                _index++;
                text = _entries[_index];
                return true;
            }

            // Past the newest: restore the pre-browse draft and end browsing.
            text = _savedDraft;
            ResetBrowsing();
            return true;
        }

        /// <summary>Cancel any in-progress Up/Down walk.</summary>
        public void ResetBrowsing()
        {
            _browsing = false;
            _index = -1;
            _savedDraft = string.Empty;
        }

        // ── Persistence (JSON Lines: one JSON-encoded string per line) ─────────

        private void Load()
        {
            if (_path == null) return;
            try
            {
                if (!File.Exists(_path)) return;
                var loaded = new List<string>(_cap);
                foreach (string line in File.ReadLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        string? value = JsonSerializer.Deserialize<string>(line);
                        if (value == null) continue;
                        loaded.Add(value);
                    }
                    catch
                    {
                        // Skip unparseable lines silently.
                    }
                }
                // Keep only the most recent _cap entries.
                _entries.AddRange(loaded.Count > _cap
                    ? loaded.GetRange(loaded.Count - _cap, _cap)
                    : loaded);
            }
            catch
            {
                // File I/O failures are swallowed — never throw into the UI.
            }
        }

        private void Persist()
        {
            if (_path == null) return;
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllLines(_path,
                    _entries.Select(s => JsonSerializer.Serialize(s)));
            }
            catch
            {
                // Swallowed — a history that can't persist is better than a crash.
            }
        }
    }
}
