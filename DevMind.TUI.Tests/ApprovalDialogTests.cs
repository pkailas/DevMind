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
