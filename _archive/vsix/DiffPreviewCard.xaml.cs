// File: DiffPreviewCard.xaml.cs  v1.0.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DevMind
{
    /// <summary>
    /// Inline diff preview card rendered inside the chat output panel.
    /// Exposes a <see cref="UserDecision"/> TaskCompletionSource that the
    /// agentic loop awaits: true = Apply, false = Skip.
    /// </summary>
    public partial class DiffPreviewCard : UserControl
    {
        private readonly TaskCompletionSource<bool> _tcs =
            new TaskCompletionSource<bool>();

        /// <summary>
        /// Awaitable result: true if the user clicked Apply, false if Skip.
        /// Also resolves false on cancellation.
        /// </summary>
        public Task<bool> UserDecision => _tcs.Task;

        /// <summary>
        /// The resolved patch data. Populated by the caller so the agentic
        /// loop can apply the patch after approval.
        /// </summary>
        public PatchResolveResult ResolveResult { get; set; }

        public DiffPreviewCard()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Configures the card with diff content.
        /// </summary>
        /// <param name="fileName">Target filename for the header.</param>
        /// <param name="confidence">Match confidence level.</param>
        /// <param name="oldLines">Lines being removed (from FIND text).</param>
        /// <param name="newLines">Lines being added (from REPLACE text).</param>
        /// <param name="contextBefore">Up to 3 context lines before the change.</param>
        /// <param name="contextAfter">Up to 3 context lines after the change.</param>
        public void Configure(
            string fileName,
            PatchConfidence confidence,
            string[] oldLines,
            string[] newLines,
            string[] contextBefore,
            string[] contextAfter)
        {
            HeaderText.Text = $"PATCH \u2014 {fileName}";

            if (confidence == PatchConfidence.Fuzzy)
            {
                ConfidenceBadge.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0x7F, 0x17)); // amber
                ConfidenceText.Text = "Fuzzy \u26A0\uFE0F";
            }
            else
            {
                ConfidenceBadge.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)); // green
                ConfidenceText.Text = "Exact \u2713";
            }

            var items = new List<DiffLineItem>();

            // Context before
            if (contextBefore != null)
            {
                foreach (string line in contextBefore)
                    items.Add(DiffLineItem.Context(line));
            }

            // Removed lines
            if (oldLines != null)
            {
                foreach (string line in oldLines)
                    items.Add(DiffLineItem.Removed(line));
            }

            // Added lines
            if (newLines != null)
            {
                foreach (string line in newLines)
                    items.Add(DiffLineItem.Added(line));
            }

            // Context after
            if (contextAfter != null)
            {
                foreach (string line in contextAfter)
                    items.Add(DiffLineItem.Context(line));
            }

            DiffLines.ItemsSource = items;
        }

        /// <summary>
        /// Configures the card for a multi-block patch, showing all FIND/REPLACE pairs.
        /// </summary>
        public void ConfigureMultiBlock(
            string fileName,
            PatchConfidence confidence,
            List<(string findText, string replaceText)> pairs,
            string fileContent)
        {
            HeaderText.Text = $"PATCH \u2014 {fileName}";

            if (confidence == PatchConfidence.Fuzzy)
            {
                ConfidenceBadge.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0x7F, 0x17));
                ConfidenceText.Text = "Fuzzy \u26A0\uFE0F";
            }
            else
            {
                ConfidenceBadge.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
                ConfidenceText.Text = "Exact \u2713";
            }

            var items = new List<DiffLineItem>();
            string[] contentLines = (fileContent ?? "").Replace("\r\n", "\n").Split('\n');

            foreach (var (findText, replaceText) in pairs)
            {
                if (items.Count > 0)
                    items.Add(DiffLineItem.Separator());

                string[] findLines = (findText ?? "").Replace("\r\n", "\n").Split('\n');
                string[] replaceLines = (replaceText ?? "").Replace("\r\n", "\n").Split('\n');

                // Find approximate location in file for context
                string findNorm = (findText ?? "").Replace("\r\n", "\n").Trim();
                string contentNorm = (fileContent ?? "").Replace("\r\n", "\n");
                int charIdx = contentNorm.IndexOf(findNorm, StringComparison.Ordinal);
                int matchLineNum = 0;
                if (charIdx >= 0)
                    matchLineNum = contentNorm.Substring(0, charIdx).Split('\n').Length - 1;

                // Context before (up to 3 lines)
                int ctxStart = Math.Max(0, matchLineNum - 3);
                for (int i = ctxStart; i < matchLineNum && i < contentLines.Length; i++)
                    items.Add(DiffLineItem.Context(contentLines[i]));

                // Removed lines
                foreach (string line in findLines)
                    items.Add(DiffLineItem.Removed(line));

                // Added lines
                foreach (string line in replaceLines)
                    items.Add(DiffLineItem.Added(line));

                // Context after (up to 3 lines)
                int afterStart = matchLineNum + findLines.Length;
                int afterEnd = Math.Min(contentLines.Length, afterStart + 3);
                for (int i = afterStart; i < afterEnd; i++)
                    items.Add(DiffLineItem.Context(contentLines[i]));
            }

            DiffLines.ItemsSource = items;
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e) => ResolveApply();

        private void SkipButton_Click(object sender, RoutedEventArgs e) => ResolveSkip();

        /// <summary>
        /// Programmatically resolves this card as "Apply". Safe to call from
        /// the batch bar or any UI-thread code.
        /// </summary>
        public void ResolveApply()
        {
            if (_tcs.Task.IsCompleted) return;
            _tcs.TrySetResult(true);
            ApplyButton.IsEnabled = false;
            SkipButton.IsEnabled = false;
            ApplyButton.Content = "\u2713 Applied";
            ApplyButton.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x4E));
        }

        /// <summary>
        /// Programmatically resolves this card as "Skip". Safe to call from
        /// the batch bar or any UI-thread code.
        /// </summary>
        public void ResolveSkip()
        {
            if (_tcs.Task.IsCompleted) return;
            _tcs.TrySetResult(false);
            ApplyButton.IsEnabled = false;
            SkipButton.IsEnabled = false;
            SkipButton.Content = "\u2717 Skipped";
            SkipButton.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        }

        /// <summary>
        /// Cancels the pending decision (e.g. when the Stop button is pressed).
        /// </summary>
        public void Cancel()
        {
            _tcs.TrySetCanceled();
            ApplyButton.IsEnabled = false;
            SkipButton.IsEnabled = false;
            SkipButton.Content = "\u2717 Cancelled";
            SkipButton.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }
    }

    /// <summary>
    /// View model for a single line in the diff preview.
    /// </summary>
    public class DiffLineItem
    {
        public string Text { get; set; }
        public Brush Background { get; set; }
        public Brush Foreground { get; set; }

        private static readonly Brush RemovedBg = new SolidColorBrush(Color.FromRgb(0x3C, 0x1F, 0x1F));
        private static readonly Brush AddedBg   = new SolidColorBrush(Color.FromRgb(0x1F, 0x3C, 0x1F));
        private static readonly Brush ContextBg = Brushes.Transparent;
        private static readonly Brush SepBg     = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));

        private static readonly Brush RemovedFg = new SolidColorBrush(Color.FromRgb(0xF4, 0x8E, 0x8E));
        private static readonly Brush AddedFg   = new SolidColorBrush(Color.FromRgb(0x8E, 0xF4, 0x8E));
        private static readonly Brush ContextFg = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        private static readonly Brush SepFg     = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        public static DiffLineItem Removed(string text)
            => new DiffLineItem { Text = "\u2212 " + text, Background = RemovedBg, Foreground = RemovedFg };

        public static DiffLineItem Added(string text)
            => new DiffLineItem { Text = "+ " + text, Background = AddedBg, Foreground = AddedFg };

        public static DiffLineItem Context(string text)
            => new DiffLineItem { Text = "  " + text, Background = ContextBg, Foreground = ContextFg };

        public static DiffLineItem Separator()
            => new DiffLineItem { Text = "  \u2500\u2500\u2500", Background = SepBg, Foreground = SepFg };
    }
}
