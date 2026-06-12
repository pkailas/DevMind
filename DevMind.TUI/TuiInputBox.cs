// File: TuiInputBox.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Multi-line bordered input box for the DevMind TUI (Phase 4 of the
// presentation parity plan). A second gui-cs/Editor instance configured as an
// input field:
//   * Multiline stays true (the default) so the document can hold newlines,
//     but Enter is REBOUND from Command.NewLine to Command.Accept — submit —
//     and Ctrl+Enter is bound to Command.NewLine. (Recorded in
//     DEVMIND_STATUS.md §5: Shift+Enter is indistinguishable from Enter at the
//     terminal layer — the VT input encoding has no distinct sequence — while
//     Ctrl+Enter arrives distinct. This rebinding mirrors what Editor itself
//     does for Multiline=false, so it is a framework-sanctioned configuration.)
//   * Rounded border; blue (#569CD6) when active, dim (#888888) while a turn
//     is running (DevMindShell InputBox parity). Border color is set on the
//     border's AdornmentView via Border.GetOrCreateView() — Border itself is
//     a facade, not a View.
//   * Height = Dim.Auto(Content, min 1, max 6) — the box grows one row per
//     line up to 6 content rows; the border adds its own thickness on top.
//     Editor.UpdateContentSize keeps ContentSize.Height = line count when
//     WordWrap is off, which is what Dim.Auto(Content) consumes.

// Suppress obsolete warnings for Terminal.Gui v2 legacy APIs.
#pragma warning disable CS0618

using System;
using System.Diagnostics;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Editor.Document;
using GuiEditor = Terminal.Gui.Editor.Editor;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;
using TgScheme = Terminal.Gui.Drawing.Scheme;

namespace DevMind
{
    /// <summary>
    /// Multi-line bordered input box. Add <see cref="View"/> to the window;
    /// subscribe to <c>View.Accepting</c> for submission (Enter).
    /// </summary>
    public sealed class TuiInputBox
    {
        private static readonly TgColor BgBlack      = new TgColor(0x00, 0x00, 0x00);
        private static readonly TgColor FgNormal     = new TgColor(0xCC, 0xCC, 0xCC); // #CCCCCC
        private static readonly TgColor BorderActive = new TgColor(0x56, 0x9C, 0xD6); // #569CD6 (theme.input)
        private static readonly TgColor BorderIdle   = new TgColor(0x88, 0x88, 0x88); // #888888 (theme.dim)

        private readonly GuiEditor _editor;

        public TuiInputBox()
        {
            _editor = new GuiEditor
            {
                X = 0,
                Y = Pos.AnchorEnd() - 1,                          // bottom edge one row above the status bar
                Width = Dim.Fill(),
                Height = Dim.Auto(DimAutoStyle.Content, 1, 6),    // 1..6 content rows; border adds on top
                Document = new TextDocument(),                    // Editor's document is null by default
                WordWrap = false,
                ReadOnly = false,
                CanFocus = true,
                BorderStyle = LineStyle.Rounded,
            };

            // Text colors: normal-on-black, matching the output pane.
            _editor.SetScheme(new TgScheme(_editor.GetScheme())
            {
                Normal   = new TgAttribute(FgNormal, BgBlack),
                Editable = new TgAttribute(FgNormal, BgBlack),
            });

            // Enter → submit (Command.Accept raises View.Accepting);
            // Ctrl+Enter → insert newline.
            //
            // TRAP (verified 2026-06-11, offline dispatch harness): the newline command
            // id must be taken from the Editor's OWN stock Enter binding, NOT from this
            // process's Command.NewLine. Terminal.Gui.Editor 2.5.0 is compiled against
            // a newer core whose Command enum ordinals drifted: it registers its newline
            // handler under literal 43, while core 2.4.4's Command.NewLine is 44 (43 is
            // DeleteAll there). Binding Command.NewLine therefore dispatches into a
            // handler that does not exist ("not supported by this View" → NotBound →
            // swallowed). Command.Accept (=1) agrees across both versions, and its
            // handler lives in core's View, so the Enter→Accept rebind is unaffected.
            Command editorNewLine = (Command)43; // Editor 2.5.0's compiled-in NewLine ordinal
            if (_editor.KeyBindings.TryGet(Key.Enter, out KeyBinding stockEnter) &&
                stockEnter.Commands is { Length: > 0 })
            {
                editorNewLine = stockEnter.Commands[0];
            }

            _editor.KeyBindings.Remove(Key.Enter);
            _editor.KeyBindings.Add(Key.Enter, Command.Accept);
            _editor.KeyBindings.Add(Key.Enter.WithCtrl, editorNewLine);

            // ── Paste (verified 2026-06-11 against decompiled core 2.4.4 + Editor 2.5.0) ──
            //
            // Two delivery paths, two fixes. Root causes:
            //
            // (1) Windows Terminal binds Ctrl+V itself (default "paste" action) — the
            //     keystroke NEVER reaches the app. WT injects the clipboard as a
            //     BRACKETED PASTE (ESC[200~ … ESC[201~). Core 2.4.4 parses that fully:
            //     parser → InputProcessor.Paste → Driver.Paste →
            //     ApplicationImpl.RaisePasteEvent → focused view's Command.Paste (=62)
            //     with a PastePayload → View.DefaultPasteHandler → Pasting event →
            //     OnPaste(text). Editor 2.5.0 does NOT override OnPaste, and the plain
            //     View default returns false ("no text model") — the payload is dropped.
            //     FIX: subscribe View.Pasting (raised by DefaultPasteHandler BEFORE
            //     OnPaste; PastingEventArgs.Text is already sanitized — control chars
            //     stripped, tab/CR/LF preserved), insert at the caret, Handled = true.
            //
            //     (Same Command-ordinal drift as Enter, one slot over: Editor registers
            //     its own clipboard paste under literal 61 = its enum's Paste, which this
            //     process's core calls Cut; core's Paste is 62. So the bracketed payload
            //     correctly lands in core's DefaultPasteHandler, not Editor's.)
            //
            // (2) Hosts that DON'T intercept Ctrl+V (conhost, or WT with the paste
            //     keybinding unbound): the keystroke reaches the app and dispatches
            //     Editor's stock binding — which reads the driver's in-process clipboard
            //     (TryGetClipboardData) and returns true even when that read fails,
            //     silently swallowing the key. The driver clipboard has the same STA
            //     constraint that forced Program.cs's copy path to shell out, so the
            //     mirror fix: intercept Ctrl+V in the KeyDown EVENT (raised before
            //     binding dispatch; Handled=true stops the broken stock path) and read
            //     via a one-shot STA PowerShell Get-Clipboard.
            //
            // No double-paste: WT paste produces no Ctrl+V key event, and a real Ctrl+V
            // key event produces no bracketed payload — exactly one path fires per host.
            _editor.Pasting += (s, e) =>
            {
                TuiAgenticHost.Diag($"[PASTE] Editor.Pasting fired len={e.Text?.Length ?? -1}");
                e.Handled = true;
                InsertAtCaret(e.Text);
            };

            _editor.KeyDown += (s, key) =>
            {
                // Diag (inert unless DEVMIND_TUI_DIAG is set): trace raw keys reaching
                // the input editor — used to verify binding paths.
                TuiAgenticHost.Diag($"[INPUT] KeyDown 0x{(uint)key.KeyCode:X8} \"{key}\"");

                if (key.KeyCode != Key.V.WithCtrl.KeyCode) return;
                key.Handled = true;
                string clip = ReadWindowsClipboardText();
                TuiAgenticHost.Diag($"[PASTE] Ctrl+V KeyDown intercepted; STA clipboard read len={clip?.Length ?? -1}");
                InsertAtCaret(clip);
            };

            // Scroll-offset correction: when a newline is inserted, Editor's
            // EnsureCaretVisible scrolls against the OLD viewport height (Dim.Auto
            // grows the box on the NEXT layout pass), leaving a residual scroll that
            // hides the top line while a blank row shows at the bottom. After each
            // layout, if the whole document fits the (now-grown) viewport, pin the
            // scroll back to the top. Beyond the 6-row cap, normal caret-following
            // scroll applies untouched.
            _editor.SubViewsLaidOut += (s, e) =>
            {
                System.Drawing.Rectangle vp = _editor.Viewport;
                if (vp.Y != 0 && _editor.GetContentSize().Height <= vp.Height)
                    _editor.Viewport = new System.Drawing.Rectangle(vp.X, 0, vp.Width, vp.Height);
            };

            SetActive(true);

            // Build stamp (inert unless DEVMIND_TUI_DIAG is set): proves at runtime that
            // THIS binary contains the paste fix + trace ladder — distinguishes a broken
            // paste path from a stale published exe in one log line.
            TuiAgenticHost.Diag("[PASTE] TuiInputBox ctor: paste-fix v2 (Pasting hook + Ctrl+V STA fallback + trace ladder) active");
        }

        /// <summary>The Editor view — add to the window, subscribe to Accepting, focus.</summary>
        public GuiEditor View => _editor;

        /// <summary>Current input text ('\n' line endings).</summary>
        public string Text => _editor.Text ?? string.Empty;

        /// <summary>Clear the input box.</summary>
        public void Clear() => _editor.Text = string.Empty;

        /// <summary>
        /// Border color: blue while accepting input, dim while a turn is running.
        /// Thread-safe (marshals via the instance IApplication when attached).
        /// </summary>
        public void SetActive(bool active)
        {
            TgColor fg = active ? BorderActive : BorderIdle;
            IApplication app = _editor.App;
            if (app == null) PinBorder(fg);
            else app.Invoke(() => PinBorder(fg));
        }

        // ── Paste helpers ────────────────────────────────────────────────────────

        // Insert text at the caret. Runs on the UI thread (Pasting / KeyDown handlers,
        // and Program.cs's app-level Paste hook for WT bracketed-paste payloads).
        public void InsertAtCaret(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                TuiAgenticHost.Diag("[PASTE] InsertAtCaret: empty text — nothing inserted");
                return;
            }

            // The document is '\n'-based — a stray '\r' renders as a glyph and skews
            // offsets (same normalization as TuiAgenticHost.AppendCore). Multi-line
            // content keeps its newlines; Multiline=true renders them as real rows.
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (text.Length == 0) return;

            TextDocument doc = _editor.Document;
            if (doc == null)
            {
                TuiAgenticHost.Diag("[PASTE] InsertAtCaret: Document is null — nothing inserted");
                return;
            }

            try
            {
                int offset = Math.Clamp(_editor.CaretOffset, 0, doc.TextLength);
                doc.Insert(offset, text);
                _editor.CaretOffset = offset + text.Length;
                TuiAgenticHost.Diag($"[PASTE] InsertAtCaret: inserted len={text.Length} at offset={offset} docLen={doc.TextLength}");
            }
            catch (Exception ex)
            {
                TuiAgenticHost.Diag($"[PASTE] InsertAtCaret: EXCEPTION {ex}");
            }
        }

        // Mirror of Program.cs's CopySelectionToClipboard, in the read direction.
        // The in-process driver clipboard needs an STA thread (why Editor's stock
        // paste fails), so shell out to a one-shot STA PowerShell. Get-Clipboard -Raw
        // returns the clipboard as one string (newlines intact); stdout is re-encoded
        // to UTF-8 inside that process so code and box-drawing glyphs survive the
        // pipe (plain console codepage would mangle them). PowerShell's pipeline
        // appends exactly one trailing newline to the output — strip exactly one.
        private static string ReadWindowsClipboardText()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -NonInteractive -STA -Command " +
                                "\"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                                "Get-Clipboard -Raw\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                };

                using var proc = Process.Start(psi);
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                if (output.EndsWith("\r\n", StringComparison.Ordinal))
                    return output.Substring(0, output.Length - 2);
                if (output.EndsWith("\n", StringComparison.Ordinal))
                    return output.Substring(0, output.Length - 1);
                return output;
            }
            catch
            {
                // Paste must never take down the UI loop — fail silently (the user
                // sees nothing inserted, same as an empty clipboard).
                return null;
            }
        }

        private void PinBorder(TgColor fg)
        {
            View borderView = _editor.Border?.GetOrCreateView();
            if (borderView == null) return;
            borderView.SetScheme(new TgScheme(borderView.GetScheme())
            {
                Normal = new TgAttribute(fg, BgBlack),
            });
            borderView.SetNeedsDraw();
        }
    }
}
