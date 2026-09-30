// File: ConfirmDialogFocusTests.cs
//
// Regression tests for the confirm-dialog lockup: with the old code,
// TuiAgenticHost.ConfirmAsync ran a NESTED modal loop (app.Run(dlg)) from inside an
// app.Invoke callback, which parked the main loop for the dialog's whole life. An OS
// focus-out/focus-in arriving in that window wedged the nested loop and the awaiting
// agent loop hung forever. The fix presents the dialog as a NON-BLOCKING session
// (app.Begin) so the main loop keeps pumping underneath.
//
// These tests run a real (headless) Terminal.Gui app: the ANSI driver degrades to
// buffer-only input/output when the console is redirected (VSTest), so no real
// terminal is needed and nothing blocks. The main loop runs on a background thread;
// the test drives it cross-thread exactly as the agent worker does (app.Invoke +
// injected keys).
//
// NOTE on what the focus test proves: it proves the main loop STAYS ALIVE while the
// dialog is open (no nested loop blocking it) and that the dialog can be answered and
// closed, leaving the loop pumping. It does NOT simulate an OS focus-out/focus-in
// event — the test driver (degraded mode, no real terminal) cannot emit one. The
// focus-event wedge itself is addressed structurally (no nested Run => no re-entrant
// Iteration on the shared main loop that a focus event could wedge).
using System;
using System.Diagnostics;
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
    public class ConfirmDialogFocusTests
    {
        // ── Headless T.Gui app: main loop on a background thread ────────────────────
        // All tests live in this one class, so xUnit runs them sequentially and the
        // process-global Application model-usage state is never contended.
        sealed class TestApp : IDisposable
        {
            public IApplication App { get; }
            public TuiAgenticHost Host { get; private set; } = null!;
            public View Window { get; private set; } = null!;
            public Exception? RunException { get; private set; }

            private readonly CancellationTokenSource _cts = new();
            private Task _runTask = Task.CompletedTask;

            private TestApp(IApplication app)
            {
                App = app;
            }

            public static TestApp Start()
            {
                // No per-iteration throttle — keep iterations fast and deterministic.
                Application.MaximumIterationsPerSecond = ushort.MaxValue;

                var app = Application.Create();
                var ta = new TestApp(app);

                // Init, the ENTIRE view tree, and Run must all run on the SAME (UI) thread:
                //   (a) Terminal.Gui's MainLoopCoordinator is "constructed on the thread in
                //       which it is used" (Init builds the loop, Run pumps it), and
                //   (b) the Views and their TextDocuments are thread-affine (VerifyAccess) —
                //       they must be created on the thread that draws them.
                // AppTestHelper does the same: the runnable (view tree) is built inside the
                // background Task.Run, not on the test thread.
                var ready = new ManualResetEventSlim(false);
                ta._runTask = Task.Run(async () =>
                {
                    try
                    {
                        app.Init(DriverRegistry.Names.ANSI);

                        var window = new Window { Title = "DevMind confirm test" };
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

                        app.Invoke(ready.Set); // fires once the main loop pumps a Zero timeout
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

        // Poll until the confirm dialog is the top runnable. Fails (no hang) if it
        // does not open within the timeout.
        static async Task<Dialog> WaitDialogOpenAsync(TestApp ta)
        {
            var sw = Stopwatch.StartNew();
            while (ta.App.TopRunnableView is not Dialog && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (ta.RunException is { } ex) throw new InvalidOperationException("run loop faulted", ex);
                await Task.Delay(20);
            }
            Assert.True(ta.App.TopRunnableView is Dialog, "confirm dialog did not open");
            return (Dialog)ta.App.TopRunnableView!; // ! : TopRunnableView is View? (nullable)
        }

        // Await the confirm result with a hard timeout so a regression can never hang.
        static async Task<bool> AwaitAsync(Task<bool> confirmTask)
            => await confirmTask.WaitAsync(TimeSpan.FromSeconds(5));

        [Fact]
        public async Task YesButton_ResolvesTrue()
        {
            using var ta = TestApp.Start();
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmContinueAsync("apply the patch?");
            var dlg = await WaitDialogOpenAsync(ta);

            // Press the YES button (_Continue) on the UI thread. InvokeCommand(Accept) is the
            // public, sanctioned way to fire a button (it routes through OnAccepting ->
            // raises the Accepting event), exactly what Enter does.
            ta.App.Invoke(() =>
            {
                var d = (Dialog)ta.App.TopRunnableView!;
                d.Buttons[0].InvokeCommand(Command.Accept);
            });

            bool result = await AwaitAsync(confirmTask);
            Assert.True(result, "Yes button should resolve true");
            await WaitDialogClosedAsync(ta);
        }

        [Fact]
        public async Task NoButton_ResolvesFalse()
        {
            using var ta = TestApp.Start();
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmContinueAsync("apply the patch?");
            await WaitDialogOpenAsync(ta);

            // Press the NO button (_Stop, the default) on the UI thread.
            ta.App.Invoke(() =>
            {
                var d = (Dialog)ta.App.TopRunnableView!;
                d.Buttons[1].InvokeCommand(Command.Accept);
            });

            bool result = await AwaitAsync(confirmTask);
            Assert.False(result, "No button should resolve false");
            await WaitDialogClosedAsync(ta);
        }

        [Fact]
        public async Task Esc_ResolvesFalse_NotPending()
        {
            using var ta = TestApp.Start();
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmContinueAsync("apply the patch?");
            await WaitDialogOpenAsync(ta);

            // Esc → Command.Quit → RequestStop (sets StopRequested; nothing else calls End
            // in the Begin model). The dialog's poll must detect it and resolve a decline.
            ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));

            // Must resolve (false) and NOT be left pending — the old nested-Run code left
            // the TCS pending forever once the loop wedged.
            bool result = await AwaitAsync(confirmTask);
            Assert.False(result, "Esc should resolve false (decline)");
            await WaitDialogClosedAsync(ta);
        }

        [Fact]
        public async Task FocusLoss_DialogStaysLive_ThenResolves()
        {
            using var ta = TestApp.Start();
            var confirmTask = ((IAgenticHost)ta.Host).ConfirmContinueAsync("apply the patch?");
            await WaitDialogOpenAsync(ta);

            // THE anti-freeze assertion: while the dialog is open, the main loop must
            // still process Invokes from other threads. With the old nested app.Run(dlg),
            // the main loop was parked inside the nested loop and this Invoke would only
            // run (if at all) via the nested loop's timer pass — and a focus event wedged
            // even that. With Begin, the outer loop is free and this runs immediately.
            var liveness = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ta.App.Invoke(() => liveness.TrySetResult(true));
            bool aliveWhileOpen = await liveness.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(aliveWhileOpen, "main loop did not process an Invoke while the dialog was open");

            // Answer with Esc → decline.
            ta.App.Invoke(() => ta.App.Keyboard.RaiseKeyDownEvent(Key.Esc));
            bool result = await AwaitAsync(confirmTask);
            Assert.False(result);

            // After the dialog closes, the main loop is STILL alive and processing input.
            var post = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ta.App.Invoke(() => post.TrySetResult(true));
            bool aliveAfterClose = await post.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(aliveAfterClose, "main loop did not process an Invoke after the dialog closed");
        }

        // Poll until the dialog is no longer the top runnable (the session was ended).
        static async Task WaitDialogClosedAsync(TestApp ta)
        {
            var sw = Stopwatch.StartNew();
            while (ta.App.TopRunnableView is Dialog && sw.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(20);
            Assert.True(ta.App.TopRunnableView is not Dialog, "dialog session was not ended");
        }
    }
}
