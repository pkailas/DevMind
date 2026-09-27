// File: SlashCompletionPopup.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Thin Terminal.Gui view that renders the SlashCompletion state as up to 8
// Label rows directly above the input box. Composed from Labels (same pattern
// as TuiStatusBar and the jump-toast in Program.cs) — no custom OnDraw.
//
// The popup is NOT focusable and does not intercept mouse input. It is added
// to the window AFTER the input box and toast so it draws on top.

#pragma warning disable CS0618

using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;
using TgScheme = Terminal.Gui.Drawing.Scheme;

namespace DevMind
{
    /// <summary>
    /// Renders the <see cref="SlashCompletion"/> state as a list of command rows.
    /// Add <see cref="View"/> to the window; call <see cref="Update"/> when the
    /// completion state changes.
    /// </summary>
    public sealed class SlashCompletionPopup
    {
        private static readonly TgColor BgBlack    = new TgColor(0x00, 0x00, 0x00);
        private static readonly TgColor FgNormal   = new TgColor(0xCC, 0xCC, 0xCC); // #CCCCCC
        private static readonly TgColor FgSelected = new TgColor(0x56, 0x9C, 0xD6); // #569CD6 (matches input border active)

        private readonly View _root;
        private readonly Label[] _rows = new Label[SlashCompletion.MaxRows];

        public SlashCompletionPopup()
        {
            _root = new View
            {
                X = 0,
                Y = 0, // set by caller via Pos.Top(inputBox.View) - rowCount
                Width = Dim.Fill(),
                Height = 0,
                CanFocus = false,
                Visible = false,
            };

            var normalAttr = new TgAttribute(FgNormal, BgBlack);

            for (int i = 0; i < _rows.Length; i++)
            {
                var label = new Label
                {
                    X = 1,   // one column of left padding
                    Y = i,
                    Width = Dim.Fill(2), // leave 1 col each side
                    Height = 1,
                    CanFocus = false,
                };
                // Default: normal text on black
                label.SetScheme(new TgScheme(label.GetScheme())
                {
                    Normal = normalAttr,
                });
                _rows[i] = label;
                _root.Add(label);
            }
        }

        /// <summary>The root view to add to the window.</summary>
        public View View => _root;

        /// <summary>Current number of visible rows (0 when closed).</summary>
        public int RowCount { get; private set; }

        /// <summary>
        /// Update the popup to reflect the current completion state.
        /// <paramref name="completion"/> is the state machine; <paramref name="rowLimit"/>
        /// is the maximum number of rows the terminal can fit (the popup shrinks
        /// to fit when the terminal is short).
        /// </summary>
        public void Update(SlashCompletion completion, int rowLimit)
        {
            if (completion == null || !completion.IsOpen)
            {
                Close();
                return;
            }

            var matches = completion.Matches;
            int rows = System.Math.Min(matches.Count, System.Math.Min(rowLimit, SlashCompletion.MaxRows));
            RowCount = rows;

            _root.Height = rows;
            _root.Visible = true;

            var selectedAttr = new TgAttribute(FgSelected, BgBlack);
            var normalAttr = new TgAttribute(FgNormal, BgBlack);

            for (int i = 0; i < SlashCompletion.MaxRows; i++)
            {
                var label = _rows[i];
                if (i < rows)
                {
                    var match = matches[i];
                    string name = match.Name.PadRight(12);
                    string desc = match.Description;
                    // Truncate to fit the label width (we don't know it at update time;
                    // Label with Dim.Fill clips visually, but we truncate to a sane max
                    // to avoid absurdly long strings).
                    if (desc.Length > 60)
                        desc = desc.Substring(0, 57) + "…";
                    label.Text = name + " " + desc;
                    label.Visible = true;

                    if (i == completion.SelectedIndex)
                        label.SetScheme(new TgScheme(label.GetScheme()) { Normal = selectedAttr });
                    else
                        label.SetScheme(new TgScheme(label.GetScheme()) { Normal = normalAttr });
                }
                else
                {
                    label.Visible = false;
                }
            }
        }

        /// <summary>Close the popup (hide all rows).</summary>
        public void Close()
        {
            RowCount = 0;
            _root.Height = 0;
            _root.Visible = false;
            for (int i = 0; i < _rows.Length; i++)
                _rows[i].Visible = false;
        }

        /// <summary>
        /// Set the Y position of the popup so it sits directly above the input box.
        /// <paramref name="inputTop"/> is the top Y of the input box view.
        /// </summary>
        public void PositionAboveInput(int inputTop)
        {
            if (RowCount > 0)
                _root.Y = inputTop - RowCount;
        }
    }
}
