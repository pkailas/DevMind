// File: SlashCompletionTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// SlashCompletion: pure state machine for slash-command autocomplete.
// No Terminal.Gui dependency — all rules tested in isolation.

using System.Collections.Generic;
using System.Linq;
using DevMind;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class SlashCompletionTests
    {
        // A realistic command set: enough to exercise ordering and the 8-row cap.
        static List<CompletionMatch> MakeCommands() => new List<CompletionMatch>
        {
            new CompletionMatch("/history", "List sessions"),
            new CompletionMatch("/help", "Show help"),
            new CompletionMatch("/mode", "Approval mode"),
            new CompletionMatch("/new", "Start new conversation"),
            new CompletionMatch("/quit", "Exit DevMind"),
            new CompletionMatch("/rules", "Behavioral rules"),
            new CompletionMatch("/steer", "Steer a running turn"),
            new CompletionMatch("/title", "Rename session"),
            new CompletionMatch("/think", "Toggle thinking"),
            new CompletionMatch("/cache", "Show nearline cache"),
        };

        static SlashCompletion MakeCompletion() => new SlashCompletion(MakeCommands());

        // ── Open / Close rules ──────────────────────────────────────────────────

        [Fact]
        public void Opens_OnSlash()
        {
            var sc = MakeCompletion();
            sc.Update("/", historyBrowsing: false);
            Assert.True(sc.IsOpen);
        }

        [Fact]
        public void Closes_OnWhitespaceInText()
        {
            var sc = MakeCompletion();
            sc.Update("/help ", historyBrowsing: false);
            Assert.False(sc.IsOpen);
        }

        [Fact]
        public void Closes_NoMatch()
        {
            var sc = MakeCompletion();
            sc.Update("/zzz", historyBrowsing: false);
            Assert.False(sc.IsOpen);
        }

        [Fact]
        public void Closes_WhenHistoryBrowsing()
        {
            var sc = MakeCompletion();
            sc.Update("/he", historyBrowsing: true);
            Assert.False(sc.IsOpen);
        }

        [Fact]
        public void Closes_WhenTextDoesNotStartWithSlash()
        {
            var sc = MakeCompletion();
            sc.Update("help", historyBrowsing: false);
            Assert.False(sc.IsOpen);
        }

        [Fact]
        public void Closes_OnEmptyText()
        {
            var sc = MakeCompletion();
            sc.Update("", historyBrowsing: false);
            Assert.False(sc.IsOpen);
        }

        // ── Ordering ────────────────────────────────────────────────────────────

        [Fact]
        public void ExactMatch_SortedFirst()
        {
            var sc = MakeCompletion();
            sc.Update("/help", historyBrowsing: false);
            Assert.True(sc.IsOpen);
            Assert.Equal(1, sc.Matches.Count);
            Assert.Equal("/help", sc.Matches[0].Name);
        }

        [Fact]
        public void Rest_AreAlphabetical()
        {
            var sc = MakeCompletion();
            sc.Update("/h", historyBrowsing: false);
            // /h matches /help and /history — no exact match, so purely alphabetical:
            // /help < /history
            Assert.True(sc.IsOpen);
            Assert.Equal(2, sc.Matches.Count);
            Assert.Equal("/help", sc.Matches[0].Name);
            Assert.Equal("/history", sc.Matches[1].Name);
        }

        [Fact]
        public void MultipleMatches_AreAlphabetical_ExactFirst()
        {
            var cmds = new List<CompletionMatch>
            {
                new CompletionMatch("/zebra", "Z"),
                new CompletionMatch("/apple", "A"),
                new CompletionMatch("/banana", "B"),
                new CompletionMatch("/cherry", "C"),
            };
            var sc = new SlashCompletion(cmds);
            sc.Update("/a", historyBrowsing: false);
            Assert.True(sc.IsOpen);
            Assert.Equal("/apple", sc.Matches[0].Name);
        }

        [Fact]
        public void ExactMatch_ComesBeforeAlphabetical()
        {
            // "/b" matches /banana (exact) and /banana starts with "b"
            // Add a command that alphabetically comes before the exact match
            var cmds = new List<CompletionMatch>
            {
                new CompletionMatch("/apple", "A"),
                new CompletionMatch("/banana", "B"),
                new CompletionMatch("/bacon", "B2"),
            };
            var sc = new SlashCompletion(cmds);
            sc.Update("/banana", historyBrowsing: false);
            Assert.True(sc.IsOpen);
            // /banana is exact → first
            Assert.Equal("/banana", sc.Matches[0].Name);
        }

        // ── Cap of 8 ────────────────────────────────────────────────────────────

        [Fact]
        public void Caps_AtEightRows()
        {
            var sc = MakeCompletion(); // 10 commands
            sc.Update("/", historyBrowsing: false);
            Assert.True(sc.IsOpen);
            Assert.True(sc.Matches.Count <= SlashCompletion.MaxRows);
            Assert.Equal(8, sc.Matches.Count);
        }

        // ── MoveSelection ───────────────────────────────────────────────────────

        [Fact]
        public void MoveSelection_ClampsAtTop()
        {
            var sc = MakeCompletion();
            sc.Update("/", historyBrowsing: false);
            sc.MoveSelection(-1);
            Assert.Equal(0, sc.SelectedIndex);
        }

        [Fact]
        public void MoveSelection_ClampsAtBottom()
        {
            var sc = MakeCompletion();
            sc.Update("/", historyBrowsing: false);
            for (int i = 0; i < sc.Matches.Count + 5; i++)
                sc.MoveSelection(+1);
            Assert.Equal(sc.Matches.Count - 1, sc.SelectedIndex);
        }

        [Fact]
        public void MoveSelection_UpDown()
        {
            var sc = MakeCompletion();
            sc.Update("/", historyBrowsing: false);
            sc.MoveSelection(+1);
            Assert.Equal(1, sc.SelectedIndex);
            sc.MoveSelection(-1);
            Assert.Equal(0, sc.SelectedIndex);
        }

        // ── Accept ──────────────────────────────────────────────────────────────

        [Fact]
        public void Accept_ReturnsNamePlusTrailingSpace()
        {
            var sc = MakeCompletion();
            sc.Update("/help", historyBrowsing: false);
            Assert.Equal("/help ", sc.Accept());
        }

        [Fact]
        public void Accept_UsesSelectedIndex()
        {
            var sc = MakeCompletion();
            sc.Update("/", historyBrowsing: false);
            sc.MoveSelection(+1);
            string accepted = sc.Accept();
            Assert.Equal(sc.Matches[1].Name + " ", accepted);
        }

        // ── IsExactMatch ────────────────────────────────────────────────────────

        [Fact]
        public void IsExactMatch_IsCaseInsensitive()
        {
            var sc = MakeCompletion();
            sc.Update("/", historyBrowsing: false);
            Assert.True(sc.IsExactMatch("/HELP"));
            Assert.True(sc.IsExactMatch("/help"));
            Assert.True(sc.IsExactMatch("/History"));
            Assert.False(sc.IsExactMatch("/hel"));
            Assert.False(sc.IsExactMatch("/help extra"));
        }

        // ── Dismiss ─────────────────────────────────────────────────────────────

        [Fact]
        public void Dismiss_ClosesPopup()
        {
            var sc = MakeCompletion();
            sc.Update("/he", historyBrowsing: false);
            Assert.True(sc.IsOpen);
            sc.Dismiss();
            Assert.False(sc.IsOpen);
        }

        [Fact]
        public void Dismiss_HoldsUntilTextChanges()
        {
            var sc = MakeCompletion();
            sc.Update("/he", historyBrowsing: false);
            sc.Dismiss();

            // Same text → still closed
            sc.Update("/he", historyBrowsing: false);
            Assert.False(sc.IsOpen);

            // Text changes → resets, reopens
            sc.Update("/hel", historyBrowsing: false);
            Assert.True(sc.IsOpen);
        }

        // ── Selection resets to 0 when matches change ───────────────────────────

        [Fact]
        public void Selection_ResetsToZero_WhenMatchesChange()
        {
            var sc = MakeCompletion();
            sc.Update("/", historyBrowsing: false);
            sc.MoveSelection(+3);
            Assert.Equal(3, sc.SelectedIndex);

            // Typing more characters changes the match set → selection resets
            sc.Update("/h", historyBrowsing: false);
            Assert.Equal(0, sc.SelectedIndex);
        }
    }
}
