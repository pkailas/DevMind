// File: TurnTeardownMarshalTests.cs  v1.0
//
// Headless tests for TuiTurnTeardown: the UI-thread marshal the input handler's post-await
// code goes through, and the shared end-of-busy teardown. Terminal.Gui 2.4.17 installs a
// plain SynchronizationContext, so the handler's finally blocks resume on a pool thread; the
// teardown must still run on the UI thread, clear the turn flag before re-enabling the input
// box, and leave the box enabled and focused.
//
// Same harness shape as PromptTextDialogTests.cs: a real Terminal.Gui app whose main loop
// runs on a background thread (the ANSI driver degrades to buffer-only I/O under VSTest).

using System;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class TurnTeardownMarshalTests
    {
        sealed class TestApp : IDisposable
        {
            public IApplication App { get; }
            public TuiInputBox InputBox { get; private set; } = null!;
            public View Other { get; private set; } = null!;
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
                        var window = new Window { Title = "DevMind teardown test" };
                        // A second focusable view, so "focus returned to the input box" is
                        // observable rather than the only possible state.
                        var other = new TextField { X = 0, Y = 0, Width = 20 };
                        var inputBox = new TuiInputBox();
                        window.Add(other, inputBox.View);
                        ta.InputBox = inputBox;
                        ta.Other = other;
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

        // Run a function on the UI thread and return its result (test-side, independent of
        // the code under test).
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

        [Fact]
        public async Task EndBusy_FromPoolThread_RunsOnUiThread_ClearsFlagFirst_AndRestoresInput()
        {
            using var ta = TestApp.Start();

            // The state a busy operation leaves: input box disabled, focus elsewhere.
            await OnUiAsync(ta, () =>
            {
                ta.InputBox.View.CanFocus = false;
                ta.InputBox.SetActive(false);
                ta.Other.SetFocus();
            });
            Assert.False(await OnUiAsync(ta, () => ta.InputBox.View.HasFocus), "precondition: input box focused");

            bool turnRunning = true;
            int callerThread = -1, clearThread = -1, statusThread = -1;
            bool canFocusWhenCleared = true, canFocusAtStatus = false;

            // What the handler's finally does after `await Task.Run(...)`: it is on a pool thread.
            await Task.Run(async () =>
            {
                callerThread = Environment.CurrentManagedThreadId;
                await TuiTurnTeardown.RunOnUiAsync(ta.App, () => TuiTurnTeardown.EndBusy(
                    clearTurnState: () =>
                    {
                        clearThread = Environment.CurrentManagedThreadId;
                        canFocusWhenCleared = ta.InputBox.View.CanFocus;
                        turnRunning = false;
                    },
                    inputBox: ta.InputBox,
                    resetStatus: () =>
                    {
                        statusThread = Environment.CurrentManagedThreadId;
                        canFocusAtStatus = ta.InputBox.View.CanFocus;
                    })).WaitAsync(TimeSpan.FromSeconds(5));
            });

            Assert.NotEqual(ta.UiThreadId, callerThread);   // the caller really was off the UI thread
            Assert.Equal(ta.UiThreadId, clearThread);
            Assert.Equal(ta.UiThreadId, statusThread);
            Assert.False(turnRunning);
            Assert.False(canFocusWhenCleared);              // flag cleared BEFORE the box re-enabled
            Assert.True(canFocusAtStatus);                  // status tail runs after the restore
            Assert.True(await OnUiAsync(ta, () => ta.InputBox.View.CanFocus), "input box not re-enabled");
            Assert.True(await OnUiAsync(ta, () => ta.InputBox.View.HasFocus), "focus did not return to the input box");
        }

        [Fact]
        public async Task RunOnUiAsync_OnUiThread_RunsInline_AndIsAlreadyComplete()
        {
            using var ta = TestApp.Start();

            var r = await OnUiAsync(ta, () =>
            {
                bool ran = false;
                int runThread = -1;
                Task t = TuiTurnTeardown.RunOnUiAsync(ta.App, () =>
                {
                    ran = true;
                    runThread = Environment.CurrentManagedThreadId;
                });
                // Inline: done before RunOnUiAsync returned, so an await of it on the UI thread
                // continues synchronously and never waits on the UI thread itself.
                return (ranBeforeReturn: ran, completed: t.IsCompletedSuccessfully, runThread);
            });

            Assert.True(r.ranBeforeReturn, "action did not run inline on the UI thread");
            Assert.True(r.completed, "task was not already complete");
            Assert.Equal(ta.UiThreadId, r.runThread);
        }

        [Fact]
        public async Task RunOnUiAsync_ActionThrows_FaultsTask()
        {
            using var ta = TestApp.Start();
            // Task.Run(Func<Task>) unwraps: t is the marshal's own Task, called off the UI thread.
            Task t = Task.Run(() => TuiTurnTeardown.RunOnUiAsync(
                ta.App, () => throw new InvalidOperationException("boom")));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => t.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("boom", ex.Message);
            // The main loop survived the faulting action.
            Assert.True(await OnUiAsync(ta, () => true));
        }

        [Fact]
        public async Task RunOnUiAsync_AfterDispose_SkipsAction_AndCompletes()
        {
            var ta = TestApp.Start();
            ta.Dispose();
            Assert.False(ta.App.Initialized);

            bool ran = false;
            Task t = Task.Run(() => TuiTurnTeardown.RunOnUiAsync(ta.App, () => ran = true));
            await t.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(t.IsCompletedSuccessfully);
            Assert.False(ran);
        }
    }
}
