// File: SlashCompletion.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Pure state machine for slash-command autocomplete in the TUI input box.
// No Terminal.Gui dependency — testable in isolation.
//
// Rules (see brief):
//   OPEN when: text starts with "/", no whitespace in text, ≥1 command matches,
//              user hasn't dismissed for the current text, and prompt history
//              is not browsing.
//   Matches: Name starts with typed text (case-insensitive).
//   Order: exact match first, then the rest alphabetically.
//   Cap: at most 8 rows.
//   Accept: selected command's Name + one trailing space.
//   Dismiss: closes the popup; resets when the text changes.

using System;
using System.Collections.Generic;
using System.Linq;

namespace DevMind
{
    /// <summary>One row in the autocomplete popup.</summary>
    public readonly record struct CompletionMatch(string Name, string Description);

    /// <summary>
    /// Pure state machine for slash-command autocomplete.
    /// Call <see cref="Update"/> on every text change; read <see cref="IsOpen"/>
    /// and <see cref="Matches"/> to drive the popup view.
    /// </summary>
    public sealed class SlashCompletion
    {
        /// <summary>Maximum rows shown in the popup.</summary>
        public const int MaxRows = 8;

        private readonly List<CompletionMatch> _allCommands;
        private List<CompletionMatch> _matches = new();
        private int _selectedIndex;
        private string _dismissedText = null!;

        /// <summary>
        /// Create a completion state machine.
        /// <paramref name="commands"/> is the full command list (name + description).
        /// </summary>
        public SlashCompletion(IEnumerable<CompletionMatch> commands)
        {
            _allCommands = commands?.ToList() ?? new List<CompletionMatch>();
        }

        /// <summary>Whether the popup is currently open.</summary>
        public bool IsOpen => _matches.Count > 0;

        /// <summary>The current matches (already sorted, capped at MaxRows).</summary>
        public IReadOnlyList<CompletionMatch> Matches => _matches;

        /// <summary>Index of the selected row (0-based).</summary>
        public int SelectedIndex => _selectedIndex;

        /// <summary>
        /// Re-evaluate the popup state for the given input text.
        /// <paramref name="historyBrowsing"/> is true when prompt-history Up/Down
        /// is active — the popup must not open in that state.
        /// </summary>
        public void Update(string text, bool historyBrowsing)
        {
            // Dismissal resets once the text changes to something other than the
            // dismissed text. If text equals the dismissed text, the dismissal holds.
            if (_dismissedText != null && text != _dismissedText)
                _dismissedText = null;

            _matches = ComputeMatches(text, historyBrowsing);
            _selectedIndex = 0; // selection resets to 0 when matches change
        }

        /// <summary>
        /// Move the selection by <paramref name="delta"/> (+1 down, -1 up).
        /// Clamps at the first/last row; no wrap.
        /// </summary>
        public void MoveSelection(int delta)
        {
            if (_matches.Count == 0) return;
            _selectedIndex = Math.Clamp(_selectedIndex + delta, 0, _matches.Count - 1);
        }

        /// <summary>
        /// Returns the completed text for the selected match: Name + one trailing space.
        /// Throws <see cref="InvalidOperationException"/> when the popup is closed.
        /// </summary>
        public string Accept()
        {
            if (_matches.Count == 0)
                throw new InvalidOperationException("SlashCompletion.Accept called while the popup is closed.");
            return _matches[_selectedIndex].Name + " ";
        }

        /// <summary>
        /// True when <paramref name="text"/> is exactly a matching command name
        /// (case-insensitive). Used to decide whether Enter should submit as-is.
        /// </summary>
        public bool IsExactMatch(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return _allCommands.Any(c =>
                string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Close the popup and mark the current text as dismissed.
        /// The popup won't reopen for the same text; it resets on the next text change.
        /// </summary>
        public void Dismiss()
        {
            if (_matches.Count > 0)
                _dismissedText = CurrentText;
            _matches = new List<CompletionMatch>();
            _selectedIndex = 0;
        }

        // The text that was active when the last Update call produced matches.
        private string CurrentText { get; set; } = "";

        private List<CompletionMatch> ComputeMatches(string text, bool historyBrowsing)
        {
            CurrentText = text ?? "";

            if (historyBrowsing)
                return new List<CompletionMatch>();
            if (string.IsNullOrEmpty(text) || !text.StartsWith("/"))
                return new List<CompletionMatch>();
            if (text.Any(c => char.IsWhiteSpace(c)))
                return new List<CompletionMatch>();
            if (_dismissedText != null && text == _dismissedText)
                return new List<CompletionMatch>();

            var prefix = text.ToLowerInvariant();
            var candidates = _allCommands
                .Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
                return new List<CompletionMatch>();

            // Exact match first, then the rest alphabetically (by Name, case-insensitive).
            var exact = candidates
                .Where(c => string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var rest = candidates
                .Where(c => !string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var ordered = new List<CompletionMatch>(exact.Count + rest.Count);
            ordered.AddRange(exact);
            ordered.AddRange(rest);

            if (ordered.Count > MaxRows)
                ordered = ordered.Take(MaxRows).ToList();

            return ordered;
        }
    }
}
