// File: TuiTextPrompt.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// A single-line text prompt dialog for the TUI: title, a (wrapped) message, a TextField
// prefilled with a default, an error row for validation, and an Enter/Esc hint.
//
// It follows TuiAgenticHost.ConfirmAsync's session model exactly: a NON-BLOCKING
// app.Begin(dlg) resolved through a TaskCompletionSource, an exactly-once Close that owns
// the app.End teardown, and a 100 ms poll that turns Esc (Command.Quit → RequestStop,
// which in the Begin model only sets StopRequested) into a cancel. It must never use
// app.Run(dlg): a nested modal loop parks the main loop, and an OS focus-out/focus-in
// arriving meanwhile wedged it (the ConfirmAsync freeze).
//
// Kept out of TuiAgenticHost (which is nullable-disabled and already ~3K lines) so the
// signature can carry its null contract; TuiAgenticHost.PromptTextAsync is the entry point.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;
using TgScheme = Terminal.Gui.Drawing.Scheme;

namespace DevMind
{
    internal static class TuiTextPrompt
    {
        /// <summary>View.Id of the dialog's TextField (tests look it up by this).</summary>
        internal const string FieldId = "promptField";

        /// <summary>View.Id of the validation-error label under the field.</summary>
        internal const string ErrorId = "promptError";

        internal const string HintText = "Enter = submit    Esc = cancel";

        private static readonly TgColor FgError = new TgColor(0xF4, 0x47, 0x47); // #F44747, TuiStatusBar's error red

        /// <summary>
        /// Shows the prompt and resolves with the submitted text, or null on Esc, on
        /// <paramref name="ct"/> cancellation, or when the dialog could not be shown.
        /// Safe to call (and await) from any thread, the UI thread included: the dialog is
        /// built on a later main-loop pass, never inline, and nothing here blocks.
        /// <paramref name="validate"/> runs on the UI thread; a non-null return is shown as
        /// the error and keeps the dialog open. If it throws, the dialog closes and the
        /// returned task faults with that exception.
        /// </summary>
        internal static Task<string?> ShowAsync(
            IApplication app, View? restoreFocus, string title, string message,
            string defaultText, Func<string, string?>? validate, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromResult<string?>(null);

            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Always defer to a main-loop pass (what app.Invoke does off the UI thread). On the
            // UI thread Invoke would run inline — i.e. Begin inside the caller's key handler,
            // while the key that triggered it is still being dispatched.
            app.AddTimeout(TimeSpan.Zero, () =>
            {
                Build(app, restoreFocus, tcs, title, message, defaultText ?? string.Empty, validate, ct);
                return false; // one-shot
            });
            return tcs.Task;
        }

        private static void Build(
            IApplication app, View? restoreFocus, TaskCompletionSource<string?> tcs, string title,
            string message, string defaultText, Func<string, string?>? validate, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) { tcs.TrySetResult(null); return; }

            // ── Size: the same width rules as ConfirmAsync ───────────────────────────
            // 70% of the screen, floored at 40, capped at the screen; the body (message,
            // field, error, hint) is the dialog width minus the 5 columns of horizontal
            // chrome ConfirmAsync measured, so wrap width == label width by construction.
            string text = (message ?? string.Empty).Replace("\r", string.Empty).Trim();
            var drv = app.Driver;
            int screenRows = drv?.Rows ?? 25;
            int screenCols = drv?.Cols ?? 80;
            int dialogCols = Math.Min(screenCols, Math.Max(40, (int)(screenCols * 0.7)));
            int bodyWidth = Math.Max(1, dialogCols - 5);

            // A long message is capped at a third of the screen with a "(N more lines)" tail.
            string[] lines = text.Length == 0
                ? Array.Empty<string>()
                : TuiAgenticHost.CapBodyLines(TuiAgenticHost.WrapText(text, bodyWidth), Math.Max(2, screenRows / 3), bodyWidth);
            int msgRows = lines.Length;

            // Rows: message, a blank row, the field, the error row (reserved so a validation
            // error never re-lays the dialog out), the hint.
            int fieldY = msgRows == 0 ? 0 : msgRows + 1;
            int bodyRows = fieldY + 3;

            var dlg = new Dialog { Title = title ?? string.Empty, Width = dialogCols, Height = bodyRows };
            var noHotKey = (System.Text.Rune)0xFFFF; // data, not mnemonics: draw every '_'
            if (msgRows > 0)
            {
                dlg.Add(new Label
                {
                    Text = string.Join("\n", lines),
                    X = 1, Y = 0, Width = bodyWidth, Height = msgRows,
                    HotKeySpecifier = noHotKey,
                });
            }
            var field = new TextField
            {
                Id = FieldId,
                Text = defaultText,
                X = 1, Y = fieldY, Width = bodyWidth,
            };
            var error = new Label
            {
                Id = ErrorId,
                Text = string.Empty,
                X = 1, Y = fieldY + 1, Width = bodyWidth, Height = 1,
                HotKeySpecifier = noHotKey,
            };
            var hint = new Label
            {
                Text = HintText,
                X = 1, Y = fieldY + 2, Width = bodyWidth, Height = 1,
                HotKeySpecifier = noHotKey,
            };
            dlg.Add(field, error, hint);
            error.SetScheme(new TgScheme(dlg.GetScheme())
            {
                Normal = new TgAttribute(FgError, dlg.GetScheme().Normal.Background),
            });

            // Height from the dialog's real vertical chrome (measured, as in ConfirmAsync).
            dlg.Layout(new System.Drawing.Size(screenCols, screenRows));
            dlg.Height = Math.Min(screenRows, bodyRows + dlg.GetAdornmentsThickness().Vertical);

            SessionToken? token = null;
            object? pollToken = null;
            CancellationTokenRegistration ctReg = default;

            // Exactly-once teardown from every exit path (Enter, Esc, ct, shutdown). Must run
            // on the UI thread. The TCS is the latch: only the first caller tears down.
            void Close(string? result, Exception? fault = null)
            {
                bool first = fault != null ? tcs.TrySetException(fault) : tcs.TrySetResult(result);
                if (!first) return;
                ctReg.Unregister();
                if (pollToken != null) app.RemoveTimeout(pollToken);
                if (token != null && dlg.IsRunning) app.End(token);
                dlg.Dispose();
                restoreFocus?.SetFocus();
            }

            // Enter → Command.Accept on the field. Handling it here stops it bubbling to the
            // Dialog, whose OnAccepting would RequestStop (read by the poll as a cancel).
            field.Accepting += (s, e) =>
            {
                e.Handled = true;
                if (tcs.Task.IsCompleted) return;
                // Esc already pressed but not yet seen by the poll: Esc wins, not this Enter.
                if (dlg.StopRequested) { Close(null); return; }
                string current = field.Text ?? string.Empty;
                string? err;
                try { err = validate?.Invoke(current); }
                catch (Exception ex) { Close(null, ex); return; }
                if (err == null) { Close(current); return; }
                error.Text = err.Replace("\r", string.Empty).Replace("\n", " ");
                field.SetFocus(); // keep the text and the caret where the operator left them
            };

            token = app.Begin(dlg);
            if (token == null)
            {
                // Begin was vetoed (IsRunningChanging): the dialog never ran.
                dlg.Dispose();
                tcs.TrySetResult(null);
                return;
            }
            // Begin focuses the first focusable view; say which one explicitly. Keyboard focus
            // on a non-empty TextField selects all (TextField.OnHasFocusChanged), so typing
            // replaces the default and Enter alone keeps it.
            field.SetFocus();

            // Esc → Command.Quit → RequestStop only sets StopRequested in the Begin model;
            // poll for it (and for IsRunning dropping on shutdown) like ConfirmAsync.
            pollToken = app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                if (!dlg.IsRunning || dlg.StopRequested)
                {
                    Close(null);
                    return false;
                }
                return true;
            });

            // Registered last: a token cancelled meanwhile fires inline here, on the UI thread.
            // If the app is already shut down, Invoke throws; resolve the cancel anyway so the
            // awaiting caller is never left pending.
            ctReg = ct.Register(() =>
            {
                try { app.Invoke(() => Close(null)); }
                catch (Exception) { tcs.TrySetResult(null); }
            });
        }
    }
}
