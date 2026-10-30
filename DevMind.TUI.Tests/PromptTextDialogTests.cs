// File: PromptTextDialogTests.cs  v1.0
//
// Headless tests for TuiAgenticHost.PromptTextAsync (TuiTextPrompt): Enter returns the
// field's text, Esc and ct cancellation return null, validate keeps the dialog open with
// the error shown until it passes, close is exactly-once, and the field has focus on open.
//
// Same harness as ConfirmDialogFocusTests.cs: a real Terminal.Gui app whose main loop runs
// on a background thread (the ANSI driver degrades to buffer-only I/O under VSTest), driven
// cross-thread with app.Invoke + injected keys.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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
    public class PromptTextDialogTests
    {
        private readonly ITestOutputHelper _output;

        public PromptTextDialogTests(ITestOutputHelper output) { _output = output; }

        // ── Headless T.Gui app: main loop on a background thread ──────────────
        // Mirrors TestApp from ConfirmDialogFocusTests.cs.
        sealed class TestApp : IDisposable
        {
            public IApplication App { get; }
            public TuiAgenticHost Host { get; private set; } = null!;
            public View InputView { get; private set; } = null!;
            public int UiThreadId { get; private set; }
            public Exception? RunException { get; private set; }
            private readonly CancellationTokenSource _cts = new();
            private Task _runTask = Task.CompletedTask;
            private TestApp(IApplication app) { App = app; }

            public static TestApp Start()
            {
                Application.MaximumIterationsPerSecond = ushort.MaxValue;
                var app = Application.Create();
                var ta = new TestApp(app);
                var ready = new ManualResetEventSlim(false);
                ta._runTask = Task.Run(async () =>
                {
                    try
                    {
                        app.Init(DriverRegistry.Names.ANSI);
                        ta.UiThreadId = Environment.CurrentManagedThreadId;
                        var window = new Window { Title = "DevMind prompt test" };
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
                        ta.Host = host;
                        ta.InputView = inputBox.View;
                        app.Invoke(ready.Set);
                        await app.RunAsync(window, ta._cts.Token);
                    }
                    catch (Exception ex) { ta.RunException = ex; }
                    finally { ready.Set(); }
                });
                if (!ready.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException(
                        "main loop did not start" + (ta.RunException is { } re ? $" (inner: {re})" : ""));
                return ta;
            }

            public void Dispose()
            {
                try { _cts.Cancel(); } catch { }
                try { _runTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
                try { App.Dispose(); } catch { }
            }
        }

        // Run a function on the UI thread and return its result.
        static async Task<T> OnUiAsync<T>(TestApp ta, Func<T> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            ta.App.Invoke(() =>
            {
                try { tcs.TrySetResult(f()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        static Task OnUiAsync(TestApp ta, Action a) => OnUiAsync(ta, () => { a(); return true; });

        static async Task<Dialog> WaitDialogOpenAsync(TestApp ta)
        {
            var sw = Stopwatch.StartNew();
            while (ta.App.TopRunnableView is not Dialog && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (ta.RunException is { } ex) throw new InvalidOperationException("run loop faulted", ex);
                await Task.Delay(20);
            }
            Assert.True(ta.App.TopRunnableView is Dialog, "prompt dialog did not open");
            return (Dialog)ta.App.TopRunnableView!;
        }

        static async Task WaitDialogClosedAsync(TestApp ta)
        {
            var sw = Stopwatch.StartNew();
            while (ta.App.TopRunnableView is Dialog && sw.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(20);
            Assert.True(ta.App.TopRunnableView is not Dialog, "dialog session was not ended");
        }

        static async Task AssertLoopAliveAsync(TestApp ta)
            => Assert.True(await OnUiAsync(ta, () => true), "main loop did not process an Invoke");

        static TextField Field(Dialog dlg) => (TextField)dlg.SubViews.Single(v => v.Id == TuiTextPrompt.FieldId);
        static Label Error(Dialog dlg) => (Label)dlg.SubViews.Single(v => v.Id == TuiTextPrompt.ErrorId);

        static Task<string?> AwaitAsync(Task<string?> t) => t.WaitAsync(TimeSpan.FromSeconds(5));

        static Task Press(TestApp ta, Key key) => OnUiAsync(ta, () => ta.App.Keyboard.RaiseKeyDownEvent(key));

        [Fact]
        public async Task Enter_WithDefault_ReturnsDefault()
        {
            using var ta = TestApp.Start();
            var t = ta.Host.PromptTextAsync("Pages", "report.pdf — 14 pages. Range?", "1-14");
            var dlg = await WaitDialogOpenAsync(ta);
            Assert.Equal("1-14", await OnUiAsync(ta, () => Field(dlg).Text));

            await Press(ta, Key.Enter);

            Assert.Equal("1-14", await AwaitAsync(t));
            await WaitDialogClosedAsync(ta);
            await AssertLoopAliveAsync(ta);
        }

        [Fact]
        public async Task TypedText_ReplacesSelectedDefault_AndIsReturned()
        {
            using var ta = TestApp.Start();
            var t = ta.Host.PromptTextAsync("Pages", "Range?", "1-14");
            await WaitDialogOpenAsync(ta);

            // Keyboard focus on a non-empty TextField selects all, so typing replaces the default.
            foreach (char c in "2-5") await Press(ta, new Key(c));
            await Press(ta, Key.Enter);

            Assert.Equal("2-5", await AwaitAsync(t));
            await WaitDialogClosedAsync(ta);
        }

        [Fact]
        public async Task EmptyEnter_ReturnsEmptyString()
        {
            using var ta = TestApp.Start();
            var t = ta.Host.PromptTextAsync("Pages", "Range?");
            await WaitDialogOpenAsync(ta);

            await Press(ta, Key.Enter);

            Assert.Equal(string.Empty, await AwaitAsync(t));
            await WaitDialogClosedAsync(ta);
        }

        [Fact]
        public async Task Esc_ReturnsNull()
        {
            using var ta = TestApp.Start();
            var t = ta.Host.PromptTextAsync("Pages", "Range?", "1-14");
            await WaitDialogOpenAsync(ta);

            await Press(ta, Key.Esc);

            Assert.Null(await AwaitAsync(t));
            await WaitDialogClosedAsync(ta);
            await AssertLoopAliveAsync(ta);
        }

        [Fact]
        public async Task CancellationToken_ReturnsNull_AndCloses()
        {
            using var ta = TestApp.Start();
            using var cts = new CancellationTokenSource();
            var t = ta.Host.PromptTextAsync("Pages", "Range?", "1-14", ct: cts.Token);
            await WaitDialogOpenAsync(ta);

            cts.Cancel(); // from the test thread: the close is marshalled to the UI thread

            Assert.Null(await AwaitAsync(t));
            await WaitDialogClosedAsync(ta);
            await AssertLoopAliveAsync(ta);
        }

        [Fact]
        public async Task PreCancelledToken_ReturnsNull_WithoutOpening()
        {
            using var ta = TestApp.Start();
            var t = ta.Host.PromptTextAsync("Pages", "Range?", "1-14", ct: new CancellationToken(true));

            Assert.Null(await AwaitAsync(t));
            await Task.Delay(200);
            Assert.True(ta.App.TopRunnableView is not Dialog, "a dialog opened for a cancelled token");
        }

        [Fact]
        public async Task Validate_Error_KeepsDialogOpen_ShowsError_ThenValidEntryReturns()
        {
            using var ta = TestApp.Start();
            var seen = new List<string>();
            var t = ta.Host.PromptTextAsync("Pages", "Range?", "99",
                validate: s => { seen.Add(s); return s == "2-5" ? null : $"'{s}' won't fit"; });
            var dlg = await WaitDialogOpenAsync(ta);

            await Press(ta, Key.Enter);
            await Task.Delay(300); // > the 100 ms poll: a wrongly-stopped dialog would have closed

            Assert.False(t.IsCompleted, "dialog resolved although validate returned an error");
            Assert.True(ta.App.TopRunnableView is Dialog, "dialog closed although validate returned an error");
            Assert.Equal("'99' won't fit", await OnUiAsync(ta, () => Error(dlg).Text));
            Assert.Equal("99", await OnUiAsync(ta, () => Field(dlg).Text)); // text kept
            Assert.True(await OnUiAsync(ta, () => Field(dlg).HasFocus), "focus left the field after the error");

            await OnUiAsync(ta, () => { Field(dlg).Text = "2-5"; });
            await Press(ta, Key.Enter);

            Assert.Equal("2-5", await AwaitAsync(t));
            Assert.Equal(new[] { "99", "2-5" }, seen); // called with the current text each time
            await WaitDialogClosedAsync(ta);
        }

        [Fact]
        public async Task Validate_Throws_FaultsTask_AndCloses()
        {
            using var ta = TestApp.Start();
            var t = ta.Host.PromptTextAsync("Pages", "Range?", "x",
                validate: _ => throw new InvalidOperationException("boom"));
            await WaitDialogOpenAsync(ta);

            await Press(ta, Key.Enter);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AwaitAsync(t));
            Assert.Equal("boom", ex.Message);
            await WaitDialogClosedAsync(ta);
            await AssertLoopAliveAsync(ta);
        }

        [Fact]
        public async Task EnterThenCancel_SamePass_ClosesOnce_EnterWins()
        {
            using var ta = TestApp.Start();
            using var cts = new CancellationTokenSource();
            var t = ta.Host.PromptTextAsync("Pages", "Range?", "3", ct: cts.Token);
            await WaitDialogOpenAsync(ta);

            // Enter closes; the cancel then reaches Close a second time on the same thread.
            await OnUiAsync(ta, () =>
            {
                ta.App.Keyboard.RaiseKeyDownEvent(Key.Enter);
                cts.Cancel();
            });

            Assert.Equal("3", await AwaitAsync(t));
            await WaitDialogClosedAsync(ta);
            await AssertLoopAliveAsync(ta);
        }

        [Fact]
        public async Task EscThenEnter_SamePass_ClosesOnce_EscWins()
        {
            using var ta = TestApp.Start();
            var t = ta.Host.PromptTextAsync("Pages", "Range?", "3");
            await WaitDialogOpenAsync(ta);

            // Esc latches StopRequested; the Enter arriving before the poll sees it must not win.
            await OnUiAsync(ta, () =>
            {
                ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc);
                ta.App.Keyboard.RaiseKeyDownEvent(Key.Enter);
            });

            Assert.Null(await AwaitAsync(t));
            await WaitDialogClosedAsync(ta);
            await Task.Delay(250); // let the (removed) poll's slot pass: nothing may throw
            Assert.Null(ta.RunException);
            await AssertLoopAliveAsync(ta);
        }

        [Fact]
        public async Task Focus_IsInTextField_OnOpen_AndReturnsToInputAfterClose()
        {
            using var ta = TestApp.Start();
            var t = ta.Host.PromptTextAsync("Pages", "Range?", "1-14");
            var dlg = await WaitDialogOpenAsync(ta);

            Assert.True(await OnUiAsync(ta, () => dlg.MostFocused == Field(dlg)), "TextField is not focused on open");
            Assert.Equal(4, await OnUiAsync(ta, () => Field(dlg).SelectedLength)); // default selected

            await Press(ta, Key.Esc);
            Assert.Null(await AwaitAsync(t));
            await WaitDialogClosedAsync(ta);
            Assert.True(await OnUiAsync(ta, () => ta.InputView.HasFocus), "focus did not return to the input view");
        }

        [Fact]
        public async Task AwaitedFromUiThread_Resolves()
        {
            using var ta = TestApp.Start();
            var done = new TaskCompletionSource<(string? text, int callThread, int resumeThread)>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // What job 8's caller does: await the prompt inside the UI thread's input handler.
            ta.App.Invoke(async () =>
            {
                try
                {
                    int callThread = Environment.CurrentManagedThreadId;
                    string? text = await ta.Host.PromptTextAsync("Pages", "Range?", "1-14");
                    done.TrySetResult((text, callThread, Environment.CurrentManagedThreadId));
                }
                catch (Exception ex) { done.TrySetException(ex); }
            });

            await WaitDialogOpenAsync(ta);
            await Press(ta, Key.Enter);
            var r = await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("1-14", r.text);
            Assert.Equal(ta.UiThreadId, r.callThread);
            _output.WriteLine($"ui={ta.UiThreadId} call={r.callThread} resume={r.resumeThread} " +
                              $"(resume on UI thread: {r.resumeThread == ta.UiThreadId})");
            await WaitDialogClosedAsync(ta);
        }

        // Program.cs's app-wide Esc handler runs BEFORE the dialog and marks Esc handled.
        // Measured: with such a handler and no yield, Esc never reaches the prompt (the task
        // stays pending). The handler yields when TuiTextPrompt.IsTopmost — mirrored here.
        static void InstallAppEscHandler(TestApp ta) => ta.App.Keyboard.KeyDown += (s, k) =>
        {
            if (k.KeyCode != Key.Esc.KeyCode) return;
            if (TuiTextPrompt.IsTopmost(ta.App)) return;
            k.Handled = true;
        };

        [Fact]
        public async Task Esc_ReachesThePrompt_PastAnAppWideEscHandler()
        {
            using var ta = TestApp.Start();
            Assert.False(await OnUiAsync(ta, () => TuiTextPrompt.IsTopmost(ta.App)));
            await OnUiAsync(ta, () => InstallAppEscHandler(ta));
            var t = ta.Host.PromptTextAsync("Pages", "Range?", "1-14");
            await WaitDialogOpenAsync(ta);
            Assert.True(await OnUiAsync(ta, () => TuiTextPrompt.IsTopmost(ta.App)));

            await Press(ta, Key.Esc);

            Assert.Null(await AwaitAsync(t));
            await WaitDialogClosedAsync(ta);
            Assert.False(await OnUiAsync(ta, () => TuiTextPrompt.IsTopmost(ta.App)));
        }
    }
}
