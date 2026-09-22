// File: TuiStatusBar.cs  v1.3
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
    /// Everything the left status segment renders for one tick. A struct of plain values so
    /// <see cref="TuiStatusBar.Compose"/> can be exercised without a terminal.
    /// </summary>
    public readonly struct StatusFields
    {
        /// <summary>Spinner frame, or empty when idle.</summary>
        public readonly string Frame;

        /// <summary>Which phase the turn is in — also selects the colour.</summary>
        public readonly StatusState State;

        /// <summary>Elapsed turn time, already formatted (mm:ss.t).</summary>
        public readonly string Elapsed;

        /// <summary>1-based agentic round, and the configured cap (0 = no cap shown).</summary>
        public readonly int Round;
        public readonly int MaxRounds;

        /// <summary>Server-true token counts for this round.</summary>
        public readonly int InTokens;
        public readonly int OutTokens;

        /// <summary>Whether Esc would stop what is running — the affordance is only true while it is.</summary>
        public readonly bool Cancellable;

        public StatusFields(string frame, StatusState state, string elapsed,
                            int round, int maxRounds, int inTokens, int outTokens, bool cancellable)
        {
            Frame       = frame ?? string.Empty;
            State       = state;
            Elapsed     = elapsed ?? string.Empty;
            Round       = round;
            MaxRounds   = maxRounds;
            InTokens    = inTokens;
            OutTokens   = outTokens;
            Cancellable = cancellable;
        }
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
        private readonly Label _iterLabel;
        private readonly Label _steerLabel;
        private readonly Label _modeLabel;
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

            _iterLabel = MakeLabel("", FgDim);
            _iterLabel.X = Pos.Right(_meterLabel);
            _iterLabel.Y = 0;

            _steerLabel = MakeLabel("", FgDim);
            _steerLabel.X = Pos.Right(_iterLabel);
            _steerLabel.Y = 0;

            _modeLabel = MakeLabel("", FgDim);
            _modeLabel.X = Pos.Right(_steerLabel);
            _modeLabel.Y = 0;

            _rateLabel = MakeLabel("", FgDim);
            _rateLabel.X = Pos.Right(_modeLabel);
            _rateLabel.Y = 0;

            _rightGroup.Add(_lspDot, _lspText, _meterLabel, _iterLabel, _steerLabel, _modeLabel, _rateLabel);
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

        /// <summary>Update the iteration chip: " · it 12/40" (dim pending color).</summary>
        public void SetIteration(int current, int max)
        {
            string text = max > 0 ? $" · it {current}/{max}" : $" · it {current}";
            OnUi(() =>
            {
                _iterLabel.Text = text;
                Pin(_iterLabel, FgPending);
            });
        }

        /// <summary>Blank the iteration chip.</summary>
        public void ClearIteration()
        {
            OnUi(() => _iterLabel.Text = "");
        }

        /// <summary>
        /// Show that a steer is queued and waiting for the next iteration boundary, in
        /// amber so it reads as pending rather than done. Null blanks the chip.
        /// <para>
        /// The MODE is shown, not the text: an override and a suggestion do very different
        /// things to a running turn, and a user who queued one while meaning the other has
        /// only this to tell them before it lands. The message itself is already echoed into
        /// the transcript, where there is room for it.
        /// </para>
        /// </summary>
        /// <summary>
        /// Show the approval mode when it is Manual, and nothing when it is Auto.
        /// <para>
        /// Only the exceptional state gets a chip. Auto is the long-standing behaviour and
        /// the overwhelming default, so a permanent "auto" label would be noise that trains
        /// the eye to skip the very spot where "manual" needs to be noticed.
        /// </para>
        /// </summary>
        public void SetApprovalMode(ApprovalMode mode)
        {
            string text = mode == ApprovalMode.Manual ? " \u00b7 approval manual" : "";
            OnUi(() =>
            {
                _modeLabel.Text = text;
                Pin(_modeLabel, mode == ApprovalMode.Manual ? FgAmber : FgDim);
            });
        }

        public void SetSteerQueued(SteerMode? mode)
        {
            string text = mode == null
                ? ""
                : $" · steer {(mode == SteerMode.Override ? "override" : "suggest")}";
            OnUi(() =>
            {
                _steerLabel.Text = text;
                Pin(_steerLabel, mode == null ? FgDim : FgAmber);
            });
        }

        // ── Composition ──────────────────────────────────────────────────────────

        /// <summary>
        /// Render the left segment for one tick: spinner, phase, elapsed, round, token split
        /// and the Esc affordance — the single live line, the way Qwen Code's footer carries
        /// <c>(35s · ↑1.0k tokens · esc to cancel)</c>.
        /// <para>
        /// This is the only line in the app that changes many times a second, which is exactly
        /// why it is the only place the per-turn numbers belong; the transcript is a record and
        /// a record should not be rewritten ten times a second. Pure, so the format is pinned
        /// by a test rather than by squinting at a running terminal.
        /// </para>
        /// <para>
        /// The right-hand chips (context meter, iteration, mode, rate) are NOT composed here:
        /// each carries its own colour — the meter goes amber then red, a manual approval mode
        /// is amber — and folding them into one string would flatten that to a single
        /// attribute. They keep their own labels and their own setters.
        /// </para>
        /// </summary>
        public static string Compose(in StatusFields f)
        {
            if (f.State == StatusState.Ready) return "○ Ready";

            string label = f.State == StatusState.Thinking ? "Thinking..."
                         : f.State == StatusState.Error    ? "Error"
                         : "Generating...";

            var sb = new System.Text.StringBuilder();
            if (f.Frame.Length > 0) sb.Append(f.Frame).Append(' ');
            sb.Append(label);

            // Detail parts are comma-separated inside one paren group; the Esc affordance is
            // set off with a middle dot because it is an instruction, not another measurement.
            var parts = new System.Collections.Generic.List<string>(3);
            if (f.Elapsed.Length > 0) parts.Add(f.Elapsed);
            if (f.Round > 0)
                parts.Add(f.MaxRounds > 0 ? $"round {f.Round}/{f.MaxRounds}" : $"round {f.Round}");
            if (f.InTokens > 0 || f.OutTokens > 0)
                parts.Add($"{f.InTokens:N0} in / {f.OutTokens:N0} out");

            string detail = string.Join(", ", parts);
            if (f.Cancellable)
                detail = detail.Length > 0 ? detail + " · Esc cancels" : "Esc cancels";

            if (detail.Length > 0) sb.Append(" (").Append(detail).Append(')');
            return sb.ToString();
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
