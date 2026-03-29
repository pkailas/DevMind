// File: DiffBatchBar.xaml.cs  v1.0.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DevMind
{
    /// <summary>
    /// Batch control bar shown below stacked DiffPreviewCards when a single
    /// LLM response contains multiple PATCH blocks. Apply All / Skip All
    /// resolves all pending cards at once.
    /// </summary>
    public partial class DiffBatchBar : UserControl
    {
        private readonly List<DiffPreviewCard> _cards;

        public DiffBatchBar(List<DiffPreviewCard> cards)
        {
            InitializeComponent();
            _cards = cards ?? new List<DiffPreviewCard>();
        }

        private void ApplyAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var card in _cards)
            {
                if (!card.UserDecision.IsCompleted)
                    card.ResolveApply();
            }
            ApplyAllButton.IsEnabled = false;
            SkipAllButton.IsEnabled = false;
            ApplyAllButton.Content = "\u2713 All Applied";
            ApplyAllButton.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x4E));
        }

        private void SkipAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var card in _cards)
            {
                if (!card.UserDecision.IsCompleted)
                    card.ResolveSkip();
            }
            ApplyAllButton.IsEnabled = false;
            SkipAllButton.IsEnabled = false;
            SkipAllButton.Content = "\u2717 All Skipped";
            SkipAllButton.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        }
    }
}
