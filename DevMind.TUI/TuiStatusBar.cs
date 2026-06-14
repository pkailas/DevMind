// File: TuiStatusBar.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Composed-Labels status row for the DevMind TUI (Phase 2 of the presentation
// parity plan). Mirrors DevMindShell's StatusBar.tsx:
//   left : colored state ("○ Ready" / "Thinking..." / error) + dim affordance hints
//   right: LSP chip ("● LSP C#/TS") + context meter ("52,363 / 256k (20%)")
//
// Built from plain Labels with per-label pinned Schemes — no custom draw code,
// no APIs beyond those already proven in Program.cs/TuiAgenticHost (SetScheme,
// Pos/Dim layout, instance IApplication.Invoke). A full-width root View paints
// the row black so there is no background seam between the left and right groups.
//
// All mutators marshal to the UI thread via the instance IApplication.Invoke
// (same pattern as TuiAgenticHost.AppendOutput): before app.Run attaches the
// window, App is null and we are on the main thread, so mutate directly.

// Suppress obsolete warnings for Terminal.Gui v2 legacy APIs.
#pragma warning disable CS0618

using System;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;
using TgScheme = Terminal.Gui.Drawing.Scheme;

namespace DevMind
{
    /// <summary>Visual state of the left status segment — drives its color.</summary>
    public enum StatusState
    {
        /// <summary>Idle, input enabled (green).</summary>
        Ready,
        /// <summary>Generating / processing (pale yellow).</summary>
        Busy,
        /// <summary>Model reasoning phase (orange).</summary>
        Thinking,
        /// <summary>Turn failed (red).</summary>
        Error,
    }

    /// <summary>
    /// Bottom status row: state + hints on the left, LSP chip + context meter on
    /// the right. Add <see cref="Root"/> to the window; mutators are thread-safe.
    /// </summary>
    public sealed class TuiStatusBar
    {
        // Palette — same hex values as DevMindShell's theme.ts / OutputColor.
        private static readonly TgColor BgBlack    = new TgColor(0x00, 0x00, 0x00);
        private static readonly TgColor FgDim      = new TgColor(0x88, 0x88, 0x88); // #888888
        private static readonly TgColor FgSuccess  = new TgColor(0x4E, 0xC9, 0x4E); // #4EC94E
        private static readonly TgColor FgPending  = new TgColor(0xDC, 0xDC, 0xAA); // #DCDCAA
        private static readonly TgColor FgThinking = new TgColor(0xE0, 0x7A, 0x0C); // #E07A0C
        private static readonly TgColor FgError    = new TgColor(0xF4, 0x47, 0x47); // #F44747
        private static readonly TgColor FgAmber    = new TgColor(0xEB, 0xC0, 0x3F); // #EBC03F

        private readonly View  _root;
        private readonly Label _stateLabel;
        private readonly Label _hintLabel;
        private readonly View  _rightGroup;
        private readonly Label _lspDot;
        private readonly Label _lspText;
        private readonly Label _meterLabel;
        private readonly Label _rateLabel;

        public TuiStatusBar(int toolCount, bool lspEnabled, string lspLanguages)
        {
            _root = new View
            {
                X = 0, Y = Pos.AnchorEnd(1),
                Width = Dim.Fill(), Height = 1,
                CanFocus = false,
            };
            Pin(_root, FgDim);

            // Left group: colored state + dim affordance hints. The hint label is
            // positioned off the state label's right edge, so state texts always
            // carry a trailing space (EnsureTrailingSpace) for separation.
            _stateLabel = MakeLabel("○ Ready ", FgSuccess);
            _stateLabel.X = 0;
            _stateLabel.Y = 0;

            _hintLabel = MakeLabel($"({toolCount} tools · Enter send · Ctrl+Enter newline · F10 or /quit)", FgDim);
            _hintLabel.X = Pos.Right(_stateLabel);
            _hintLabel.Y = 0;

            // Right group: a Dim.Auto(Content) container anchored to the right edge,
            // so the chip + meter stay right-aligned as the meter text grows.
            _rightGroup = new View
            {
                X = Pos.AnchorEnd(), Y = 0,
                Width = Dim.Auto(DimAutoStyle.Content, null, null), Height = 1,
                CanFocus = false,
            };
            Pin(_rightGroup, FgDim);

            _lspDot = MakeLabel(lspEnabled ? "●" : "○", lspEnabled ? FgSuccess : FgDim);
            _lspDot.X = 0;
            _lspDot.Y = 0;

            _lspText = MakeLabel(lspEnabled ? $" LSP {lspLanguages}  " : " LSP off  ", FgDim);
            _lspText.X = Pos.Right(_lspDot);
            _lspText.Y = 0;

            _meterLabel = MakeLabel("", FgDim);
            _meterLabel.X = Pos.Right(_lspText);
            _meterLabel.Y = 0;

            _rateLabel = MakeLabel("", FgDim);
            _rateLabel.X = Pos.Right(_meterLabel);
            _rateLabel.Y = 0;

            _rightGroup.Add(_lspDot, _lspText, _meterLabel, _rateLabel);
            _root.Add(_stateLabel, _hintLabel, _rightGroup);
        }

        /// <summary>The single full-width view to add to the window's bottom row.</summary>
        public View Root => _root;

        // ── Mutators (thread-safe) ───────────────────────────────────────────────

        /// <summary>Set the left state segment's text and color.</summary>
        public void SetState(string text, StatusState state)
        {
            TgColor color;
            switch (state)
            {
                case StatusState.Ready:    color = FgSuccess;  break;
                case StatusState.Thinking: color = FgThinking; break;
                case StatusState.Error:    color = FgError;    break;
                case StatusState.Busy:
                default:                   color = FgPending;  break;
            }

            string display = EnsureTrailingSpace(text ?? string.Empty);
            OnUi(() =>
            {
                _stateLabel.Text = display;
                Pin(_stateLabel, color);
            });
        }

        /// <summary>Idle state: green "○ Ready".</summary>
        public void SetReady() => SetState("○ Ready", StatusState.Ready);

        /// <summary>Busy state with the given text (pale-yellow).</summary>
        public void SetBusy(string text) => SetState(text, StatusState.Busy);

        /// <summary>
        /// Update the context meter. With a known total: "52,363 / 256k (20%)",
        /// dim under 70% utilization, amber to 90%, red above. Total unknown
        /// (no server response yet): "~N tok" estimate, dim.
        /// </summary>
        public void SetContextMeter(int used, int total)
        {
            string text;
            TgColor color;
            if (total > 0)
            {
                double util = (double)used / total;
                int pct = (int)Math.Round(util * 100.0);
                color = util < 0.70 ? FgDim : util < 0.90 ? FgAmber : FgError;
                text = $"{used:N0} / {total / 1024}k ({pct}%)";
            }
            else
            {
                text = used > 0 ? $"~{used:N0} tok" : "";
                color = FgDim;
            }

            OnUi(() =>
            {
                _meterLabel.Text = text;
                Pin(_meterLabel, color);
            });
        }

        /// <summary>
        /// Update the tok/s chip from the last turn's server timings.
        /// Pass 0 (or negative) to clear it.
        /// </summary>
        public void SetTokRate(double tokPerSec)
        {
            string text = tokPerSec > 0 ? $" · {tokPerSec:F1} tok/s" : "";
            OnUi(() => _rateLabel.Text = text);
        }

        /// <summary>Refresh the LSP chip (dot green when ready, amber when busy).</summary>
        public void SetLspBusy(bool busy)
        {
            OnUi(() => Pin(_lspDot, busy ? FgAmber : FgSuccess));
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static string EnsureTrailingSpace(string text)
            => text.Length == 0 || text.EndsWith(" ", StringComparison.Ordinal) ? text : text + " ";

        private static Label MakeLabel(string text, TgColor fg)
        {
            var label = new Label
            {
                Text = text,
                Width = Dim.Auto(DimAutoStyle.Text, null, null),
                Height = 1,
                CanFocus = false,
            };
            Pin(label, fg);
            return label;
        }

        // Pin the view's Normal role to fg-on-black so labels render with their own
        // color regardless of the window scheme (same pattern as the output Editor).
        private static void Pin(View view, TgColor fg)
        {
            view.SetScheme(new TgScheme(view.GetScheme())
            {
                Normal = new TgAttribute(fg, BgBlack),
            });
        }

        // Marshal to the UI thread via the instance IApplication (null before
        // app.Run attaches the window — then we are still on the main thread).
        private void OnUi(Action action)
        {
            IApplication app = _root.App;
            if (app == null) action();
            else app.Invoke(action);
        }
    }
}
