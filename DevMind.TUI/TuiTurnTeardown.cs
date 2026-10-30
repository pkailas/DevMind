// File: TuiTurnTeardown.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The UI-thread marshal and the one end-of-busy teardown used by Program.cs's input handler.
//
// Terminal.Gui 2.4.17's Application.Init installs a plain `new SynchronizationContext()`, whose
// Post goes to the thread pool. So an `await` started on the UI thread resumes on a POOL thread,
// and every statement in the input handler after its first real await runs off the UI thread.
// Views are not thread-checked by Terminal.Gui, so such a touch does not throw — it races the
// main loop. Everything the handler does to a view after an await goes through RunOnUiAsync.
//
// Kept out of Program.cs so the tests can drive the marshal and the teardown headlessly.

#nullable enable

using System;
using System.Threading.Tasks;
using Terminal.Gui.App;

namespace DevMind
{
    internal static class TuiTurnTeardown
    {
        /// <summary>
        /// Run <paramref name="action"/> on <paramref name="app"/>'s UI thread. The returned Task
        /// completes once the action has run, and faults if it threw.
        /// <para>
        /// On the UI thread (with the top runnable running) IApplication.Invoke runs the action
        /// INLINE, so the Task is already complete and an await of it continues synchronously —
        /// the handler's no-await paths keep their old, synchronous behaviour. Off it, Invoke
        /// queues the action to the main loop and the caller awaits asynchronously: no thread
        /// ever blocks waiting on the UI thread, so this cannot deadlock against itself.
        /// </para>
        /// <para>
        /// An app that is no longer initialized (a turn ending after app.Dispose at exit) has no
        /// views left to restore: the action is skipped and the Task completes, rather than
        /// Invoke's NotInitializedException escaping an async void handler.
        /// </para>
        /// </summary>
        internal static Task RunOnUiAsync(IApplication app, Action action)
        {
            ArgumentNullException.ThrowIfNull(app);
            ArgumentNullException.ThrowIfNull(action);
            if (!app.Initialized)
                return Task.CompletedTask;

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                app.Invoke(() =>
                {
                    try { action(); done.TrySetResult(); }
                    catch (Exception ex) { done.TrySetException(ex); }
                });
            }
            catch (NotInitializedException)
            {
                // Shut down between the check and the Invoke — same as not initialized.
                return Task.CompletedTask;
            }
            return done.Task;
        }

        /// <summary>
        /// The end-of-busy teardown shared by every long operation the input handler starts
        /// (agentic turn, /t, /digest, /library add and replace). UI thread only — call it
        /// through <see cref="RunOnUiAsync"/>.
        /// <para>
        /// Order is the contract: <paramref name="clearTurnState"/> (the turn flag, streaming
        /// off) runs BEFORE the input box is re-enabled, so a submit the box accepts afterwards
        /// never sees the old turn as running; <paramref name="resetStatus"/> is the path's
        /// status-bar tail (EndTurn for a turn, Ready for the others) and runs last.
        /// </para>
        /// </summary>
        internal static void EndBusy(Action clearTurnState, TuiInputBox inputBox, Action resetStatus)
        {
            clearTurnState();
            RestoreInput(inputBox);
            resetStatus();
        }

        /// <summary>Re-enable, re-color and focus the input box. UI thread only.</summary>
        internal static void RestoreInput(TuiInputBox inputBox)
        {
            inputBox.View.CanFocus = true;
            inputBox.SetActive(true);
            inputBox.View.SetFocus();
        }
    }
}
