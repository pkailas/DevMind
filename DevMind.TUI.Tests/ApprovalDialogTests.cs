// File: ApprovalDialogTests.cs  v1.0
//
// Regression tests for the approval-prompt defect:
//   1. The dialog title is "Approve action" (not "Token budget") with
//      Approve/Skip buttons, Skip as the default.
//   2. The dialog body is NOT empty: the Label has non-zero height and its
//      Text contains the message, for both a short prompt and a long MCP prompt.
//
// These tests run a real (headless) Terminal.Gui app, reusing the TestApp
// harness from ConfirmDialogFocusTests.cs (Init + RunAsync on the same UI
// thread, ANSI driver degrades to buffer-only when the console is redirected).

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Terminal.Gui.Editor.Document;
using Xunit;
using GuiEditor = Terminal.Gui.Editor.Editor;

namespace DevMind.TUI.Tests
{
    public class ApprovalDialogTests
    {
        // ── Headless T.Gui app: main loop on a background thread ──────────────
        // Mirrors TestApp from ConfirmDialogFocusTests.cs.
        sealed class TestApp : IDisposable
        {
            public IApplication App { get; }
            public TuiAgenticHost Host { get; private set; } = null!;
            public View Window { get; private set; } = null!;
            public Exception? RunException { get; private set; }
            private readonly CancellationTokenSource _cts = new();
            private Task _runTask = Task.CompletedTask;
            private TestApp(IApplication app) { App = app; }

            public static TestApp Start(int? cols = null, int? rows = null)
            {
                Application.MaximumIterationsPerSecond = ushort.MaxValue;
                var app = Application.Create();
                var ta = new TestApp(app);
                if (cols is int c && rows is int r && app.Driver is not null)
                    app.Driver.SetScreenSize(c, r);
                var ready = new ManualResetEventSlim(false);
                ta._runTask = Task.Run(async () =>
                {
                    try
                    {
                        app.Init(DriverRegistry.Names.ANSI);
                        var window = new Window { Title = "DevMind approval test" };
                        var outputView = new GuiEditor
                        {
                            ReadOnly = true,
                            CanFocus = false,
                            Document = new TextDocument(),
                            WordWrap = true,
                            X = 0, Y = 0,
                            Width = Dim.Fill(),
                            Height = Dim.Fill(),
                        };
                        var inputBox = new TuiInputBox();
                        window.Add(outputView, inputBox.View);
                        var host = new TuiAgenticHost(string.Empty, outputView);
                        host.FocusInputView = inputBox.View;
                        ta.Window = window;
                        ta.Host = host;
                        app.Invoke(ready.Set);
                        await app.RunAsync(window, ta._cts.Token);
                    }
                    catch (Exception ex) { ta.RunException = ex; }
                    finally { ready.Set(); }
                });
                if (!ready.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("main loop did not start");
                return ta;
            }

            public void Dispose()
            {
                try { _cts.Cancel(); } catch { }
                try { _runTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
                try { App.Dispose(); } catch { }
            }
        }

        static async Task<Dialog> WaitDialogOpenAsync(TestApp ta)
        {
            var sw = Stopwatch.StartNew();
            while (ta.App.TopRunnableView is not Dialog && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (ta.RunException is { } ex) throw new InvalidOperationException("run loop faulted", ex);
                await Task.Delay(20);
            }
            Assert.True(ta.App.TopRunnableView is Dialog, "approval dialog did not open");
            return (Dialog)ta.App.TopRunnableView!;
        }

        static async Task<bool> AwaitAsync(Task<bool> t)
            => await t.WaitAsync(TimeSpan.FromSeconds(5));

        // Run an action on the UI thread and AWAIT its completion. app.Invoke is
        // fire-and-forget (it queues the callback to the main loop), so a test that
        // reads a value the callback writes must wait for the callback to finish —
        // otherwise it races the render loop and reads a stale/null value.
        static async Task InvokeOnUIAsync(TestApp ta, Action action)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ta.App.Invoke(() =>
            {
                try { action(); } finally { done.TrySetResult(true); }
            });
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        static async Task WaitDialogClosedAsync(TestApp ta)
        {
            var sw = Stopwatch.StartNew();
            while (ta.App.TopRunnableView is Dialog && sw.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(20);
            Assert.True(ta.App.TopRunnableView is not Dialog, "dialog was not closed");
        }

        // ── Test: no line of the wrapped message can be clipped ─────────────────
        // The wrap width and the label's resolved Frame.Width must be the same
        // number: any wrapped line longer than the label is cut off on screen.
        // The old code derived the two independently (bodyWidth from a
        // Math.Max(40, …) floor the dialog never had; the label from Dim.Fill(2))
        // and they disagreed by 1–2 columns in a 120-col run, clipping 79–80-char
        // lines. This test opens the dialog at a couple of screen widths with a
        // long unbroken-plus-spaced message and asserts the laid-out label is at
        // least as wide as every line it holds.
        [Fact]
        public async Task ApprovalDialog_NoWrappedLineIsClipped_AcrossWidths()
        {
            foreach (var cols in new[] { 120, 50 })
            {
                using var ta = TestApp.Start(cols, 30);

                // A long unbroken token (forces hard-breaks inside the token) plus
                // spaced words (forces space-wraps): every wrap path in WrapText.
                string longWord = new string('x', 57);
                string msg = $"prefix {longWord} and then {longWord} some spaced words " +
                             string.Join(" ", Enumerable.Range(1, 30).Select(i => $"w{i}"));
                var confirmTask = ((IAgenticHost)ta.Host).ConfirmActionAsync(msg);
                var dlg = await WaitDialogOpenAsync(ta);

                Label? label = null;
                await InvokeOnUIAsync(ta, () =>
                {
                    var d = (Dialog)ta.App.TopRunnableView!;
                    foreach (var sv in d.SubViews)
                        if (sv is Label l) { label = l; break; }
                });
                Assert.NotNull(label);

                // Every line of the label's text must fit in the laid-out label.
                int maxWidth = label!.Frame.Width;
                int longest = 0;
                foreach (string line in (label.Text ?? "").Split('\n'))
                    longest = Math.Max(longest, line.Length);
                Assert.True(maxWidth >= longest,
                    $"cols={cols}: label Frame.Width={maxWidth} but longest line is {longest} chars — it would be clipped");

                // Close with Esc.
                ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));
                bool result = await AwaitAsync(confirmTask);
                Assert.False(result);
                await WaitDialogClosedAsync(ta);
            }
        }

        // ── Test: CRLF messages leave no stray carriage returns ─────────────────
        // Windows line endings in the message would leave a \r at the end of
        // every wrapped line (WrapText splits on \n only). The label's text must
        // be clean of them.
        [Fact]
        public async Task ApprovalDialog_CrlfMessage_NoCarriageReturns()
        {
            using var ta = TestApp.Start(120, 30);
            string msg = "line one of the message\r\nline two of the message\r\nline three";
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmActionAsync(msg);
            var dlg = await WaitDialogOpenAsync(ta);

            Label? label = null;
            await InvokeOnUIAsync(ta, () =>
            {
                var d = (Dialog)ta.App.TopRunnableView!;
                foreach (var sv in d.SubViews)
                    if (sv is Label l) { label = l; break; }
            });
            Assert.NotNull(label);
            Assert.DoesNotContain("\r", label!.Text ?? "");

            // Close with Esc.
            ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));
            bool result = await AwaitAsync(confirmTask);
            Assert.False(result);
            await WaitDialogClosedAsync(ta);
        }

        // ── Test 1: title + buttons ───────────────────────────────────────────
        [Fact]
        public async Task ApprovalDialog_HasCorrectTitleAndButtons()
        {
            using var ta = TestApp.Start();
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmActionAsync("Delete file Foo.cs?");
            var dlg = await WaitDialogOpenAsync(ta);

            // Title is "Approve action", not "Token budget".
            Assert.Equal("Approve action", dlg.Title);

            // Two buttons: Approve and Skip.
            Assert.Equal(2, dlg.Buttons.Count());
            Assert.Contains("Approve", dlg.Buttons[0].Text);
            Assert.Contains("Skip", dlg.Buttons[1].Text);

            // Skip (the negative) is the default — Enter declines, not acts.
            Assert.True(dlg.Buttons[1].IsDefault, "Skip should be the default button");
            Assert.False(dlg.Buttons[0].IsDefault, "Approve should NOT be the default button");

            // Answer with Esc → decline, so the test doesn't hang.
            ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));
            bool result = await AwaitAsync(confirmTask);
            Assert.False(result, "Esc should resolve false (decline)");
            await WaitDialogClosedAsync(ta);
        }

        // ── Test 2: short prompt — label body is present ──────────────────────
        [Fact]
        public async Task ApprovalDialog_ShortPrompt_BodyIsNotEmpty()
        {
            using var ta = TestApp.Start();
            const string prompt = "Delete file Foo.cs?";
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmActionAsync(prompt);
            var dlg = await WaitDialogOpenAsync(ta);

            // Find the Label in the dialog's sub-views (awaited so the callback
            // has finished before we read it).
            Label? label = null;
            await InvokeOnUIAsync(ta, () =>
            {
                var d = (Dialog)ta.App.TopRunnableView!;
                foreach (var sv in d.SubViews)
                    if (sv is Label l) { label = l; break; }
            });
            Assert.NotNull(label);

            // The label has non-zero height (the old Dim.Fill(2) resolved to 0).
            Assert.True(label!.Frame.Height > 0,
                $"label height is {label.Frame.Height} — the body is empty (the Dim.Fill(2) bug)");

            // The label's text contains the prompt.
            Assert.Contains("Foo.cs", label.Text ?? "");

            // Close with Esc.
            ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));
            bool result = await AwaitAsync(confirmTask);
            Assert.False(result);
            await WaitDialogClosedAsync(ta);
        }

        // ── Test 3: long MCP prompt — label body is present ───────────────────
        [Fact]
        public async Task ApprovalDialog_LongMcpPrompt_BodyIsNotEmpty()
        {
            using var ta = TestApp.Start();
            // A realistic MCP prompt: the kind DescribeMcpArgs produces for a
            // nested JSON arguments object, truncated to 200 chars + ellipsis.
            string json = "{\"workflow\":\"sdXL_base\",\"steps\":50,\"cfg\":7.5," +
                           "\"sampler\":\"Euler a\",\"scheduler\":\"Normal\",\"seed\":42," +
                           "\"width\":1024,\"height\":1024,\"batch_size\":1," +
                           "\"prompt\":\"a photo of a cat sitting on a mat, highly detailed\"}";
            string prompt = "Run MCP tool comfy.run_workflow " + json;
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmActionAsync(prompt);
            var dlg = await WaitDialogOpenAsync(ta);

            // Find the Label (awaited so the callback has finished before we read it).
            Label? label = null;
            await InvokeOnUIAsync(ta, () =>
            {
                var d = (Dialog)ta.App.TopRunnableView!;
                foreach (var sv in d.SubViews)
                    if (sv is Label l) { label = l; break; }
            });
            Assert.NotNull(label);

            // The label has non-zero height — the regression assertion for the
            // empty body. With the old Dim.Fill(2), this was 0 for any message.
            Assert.True(label!.Frame.Height > 0,
                $"label height is {label.Frame.Height} — the body is empty for a long MCP prompt");

            // The label's text contains the tool name (the first word of the prompt).
            Assert.Contains("comfy.run_workflow", label.Text ?? "");

            // Close with Esc.
            ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));
            bool result = await AwaitAsync(confirmTask);
            Assert.False(result);
            await WaitDialogClosedAsync(ta);
        }

        // ── Rendered-screen tests: what is DRAWN, not what Label.Text holds ───
        // The tests above check Label.Text and Frame.Height, which were both fine
        // while the dialog body rendered BLANK on screen. These force a full
        // layout+draw on the UI thread and read the driver's cell buffer, then
        // assert every line of the message is visible inside the dialog, on its
        // own row, above the button row.

        sealed record Rendered(string[] Rows, System.Drawing.Rectangle Dialog, int ButtonRow, string[] LabelLines, string Diag);

        static async Task<Rendered> RenderDialogAsync(TestApp ta)
        {
            Rendered? r = null;
            await InvokeOnUIAsync(ta, () =>
            {
                ta.App.LayoutAndDraw(true);
                var d = (Dialog)ta.App.TopRunnableView!;
                Label? label = null;
                foreach (var sv in d.SubViews)
                    if (sv is Label l) { label = l; break; }
                var cells = ta.App.Driver!.Contents!;
                int rows = cells.GetLength(0), cols = cells.GetLength(1);
                var lines = new string[rows];
                for (int y = 0; y < rows; y++)
                {
                    var sb = new System.Text.StringBuilder(cols);
                    for (int x = 0; x < cols; x++)
                    {
                        string g = cells[y, x].Grapheme;
                        sb.Append(string.IsNullOrEmpty(g) ? " " : g);
                    }
                    lines[y] = sb.ToString();
                }
                var dlgRect = d.FrameToScreen();
                int buttonRow = d.Buttons[0].FrameToScreen().Y;
                string diag = $"dialog={dlgRect} viewport={d.Viewport} label.Frame={label?.Frame} " +
                              $"label.Screen={label?.FrameToScreen()} buttonRow={buttonRow}\n" +
                              string.Join("\n", lines.Skip(Math.Max(0, dlgRect.Y)).Take(dlgRect.Height));
                r = new Rendered(lines, dlgRect, buttonRow,
                                 (label?.Text ?? "").Split('\n'), diag);
            });
            return r!;
        }

        static void AssertAllLinesVisible(Rendered r)
        {
            int prevRow = r.Dialog.Y; // top border row; first body line must be below it
            foreach (string line in r.LabelLines)
            {
                string want = line.TrimEnd();
                if (want.Length == 0) continue;
                int found = -1;
                for (int y = prevRow + 1; y < r.ButtonRow; y++)
                {
                    string row = r.Rows[y];
                    int left = Math.Max(0, r.Dialog.X), width = Math.Min(r.Dialog.Width, row.Length - left);
                    if (row.Substring(left, width).Contains(want)) { found = y; break; }
                }
                Assert.True(found >= 0,
                    $"line \"{want}\" is not drawn on its own row between the title and the button row (row {r.ButtonRow}).\n{r.Diag}");
                prevRow = found;
            }
        }

        [Fact]
        public async Task ApprovalDialog_ShortPrompt_TextIsDrawnOnScreen()
        {
            using var ta = TestApp.Start(120, 30);
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmActionAsync("Create file Foo.cs (123 chars)?");
            await WaitDialogOpenAsync(ta);

            var r = await RenderDialogAsync(ta);
            Assert.Contains(r.Rows.Skip(r.Dialog.Y).Take(r.ButtonRow - r.Dialog.Y), row => row.Contains("Foo.cs"));
            AssertAllLinesVisible(r);

            ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));
            Assert.False(await AwaitAsync(confirmTask));
            await WaitDialogClosedAsync(ta);
        }

        [Fact]
        public async Task ApprovalDialog_WrappedPrompt_AllLinesDrawnAboveButtons()
        {
            using var ta = TestApp.Start(120, 30);
            string msg = "Run MCP tool comfy.run_workflow " +
                         string.Join(" ", Enumerable.Range(1, 60).Select(i => $"arg{i}=v{i}"));
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmActionAsync(msg);
            await WaitDialogOpenAsync(ta);

            var r = await RenderDialogAsync(ta);
            Assert.True(r.LabelLines.Length >= 3, $"expected a wrapped (multi-line) prompt, got {r.LabelLines.Length} line(s)");
            AssertAllLinesVisible(r);

            ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));
            Assert.False(await AwaitAsync(confirmTask));
            await WaitDialogClosedAsync(ta);
        }

        // ── Tool name + full action, and no wasted rows ─────────────────────────
        // Paul's popup after 1.0.615 read only `Run shell: python -c " … (1093 chars total)`,
        // with one blank row between that line and the buttons. These drive the
        // two-text overload the executor uses (short one-liner + full dialog text).

        static int RowOf(Rendered r, string text)
        {
            for (int y = Math.Max(0, r.Dialog.Y); y < r.Dialog.Y + r.Dialog.Height && y < r.Rows.Length; y++)
                if (r.Rows[y].Contains(text)) return y;
            return -1;
        }

        [Fact]
        public async Task ApprovalDialog_OneLinePrompt_NoBlankRowAboveButtons()
        {
            using var ta = TestApp.Start(120, 30);
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmActionAsync(
                "Delete file Foo.cs?", "Delete file Foo.cs?");
            await WaitDialogOpenAsync(ta);

            var r = await RenderDialogAsync(ta);
            int textRow = RowOf(r, "Delete file Foo.cs?");
            int buttonTextRow = RowOf(r, "Skip"); // not "Approve": the title says "Approve action"
            Assert.True(textRow >= 0, $"prompt text not drawn.\n{r.Diag}");
            Assert.True(buttonTextRow >= 0, $"button text not drawn.\n{r.Diag}");
            Assert.True(buttonTextRow == textRow + 1,
                $"text on row {textRow}, buttons on row {buttonTextRow}: {buttonTextRow - textRow - 1} blank row(s) between them.\n{r.Diag}");

            ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));
            Assert.False(await AwaitAsync(confirmTask));
            await WaitDialogClosedAsync(ta);
        }

        [Fact]
        public async Task ApprovalDialog_MultiLineShell_ShowsToolNameAndEveryLineInOrder()
        {
            using var ta = TestApp.Start(120, 30);
            string[] detailLines =
            {
                "Tool: run_shell",
                "python -c \"",
                "import sys",
                "print('hello world')",
                "print(sys.argv)",
                "\"",
            };
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmActionAsync(
                "Run shell: python -c \" … (58 chars total)", string.Join("\n", detailLines));
            await WaitDialogOpenAsync(ta);

            var r = await RenderDialogAsync(ta);
            int prev = r.Dialog.Y;
            foreach (string want in detailLines)
            {
                int row = -1;
                for (int y = prev + 1; y < r.Dialog.Y + r.Dialog.Height; y++)
                    if (r.Rows[y].Contains(want)) { row = y; break; }
                Assert.True(row > prev, $"line \"{want}\" not drawn below row {prev}.\n{r.Diag}");
                prev = row;
            }
            int buttonTextRow = RowOf(r, "Skip");
            Assert.True(prev < buttonTextRow, $"last body line (row {prev}) is not above the buttons (row {buttonTextRow}).\n{r.Diag}");

            ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));
            Assert.False(await AwaitAsync(confirmTask));
            await WaitDialogClosedAsync(ta);
        }

        [Fact]
        public async Task ApprovalDialog_OverCapPrompt_SaysHowManyLinesAreHidden()
        {
            // 30 rows → cap = 10 body rows. 40 short lines: 9 shown + the indicator line,
            // so 31 lines are hidden.
            using var ta = TestApp.Start(120, 30);
            var lines = new[] { "Tool: run_shell" }.Concat(Enumerable.Range(1, 39).Select(i => $"echo line{i:00}")).ToArray();
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmActionAsync("Run shell: echo line01 …", string.Join("\n", lines));
            await WaitDialogOpenAsync(ta);

            var r = await RenderDialogAsync(ta);
            int buttonTextRow = RowOf(r, "Skip");
            int indicatorRow = RowOf(r, "(31 more lines)");
            Assert.True(indicatorRow >= 0, $"no \"(31 more lines)\" indicator drawn.\n{r.Diag}");
            Assert.True(indicatorRow == buttonTextRow - 1,
                $"indicator on row {indicatorRow}, buttons on row {buttonTextRow} — it must be the last body row.\n{r.Diag}");
            // The 9 visible lines are the first 9, in order; the 10th is not drawn.
            Assert.True(RowOf(r, "Tool: run_shell") > r.Dialog.Y);
            Assert.True(RowOf(r, "echo line08") == indicatorRow - 1, $"line 9 of 40 should sit just above the indicator.\n{r.Diag}");
            Assert.Equal(-1, RowOf(r, "echo line09"));
            // Nothing overlaps the buttons: the button row holds no body text.
            Assert.DoesNotContain("echo", r.Rows[buttonTextRow]);
            Assert.Contains("Approve", r.Rows[buttonTextRow]);

            ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));
            Assert.False(await AwaitAsync(confirmTask));
            await WaitDialogClosedAsync(ta);
        }

        // ── Test 4: ConfirmContinueAsync still uses "Token budget" ────────────
        // The budget guard's dialog is unchanged — only the approval prompt moved.
        [Fact]
        public async Task ConfirmContinueAsync_StillUsesTokenBudgetTitle()
        {
            using var ta = TestApp.Start();
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmContinueAsync("Context at 90%. Continue?");
            var dlg = await WaitDialogOpenAsync(ta);

            // The budget guard's title is unchanged.
            Assert.Equal("Token budget", dlg.Title);

            // The budget guard's buttons are unchanged.
            Assert.Contains("Continue", dlg.Buttons[0].Text);
            Assert.Contains("Stop", dlg.Buttons[1].Text);

            // Close with Esc.
            ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));
            bool result = await AwaitAsync(confirmTask);
            Assert.False(result);
            await WaitDialogClosedAsync(ta);
        }
    }
}
