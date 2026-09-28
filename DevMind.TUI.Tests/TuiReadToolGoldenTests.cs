// File: TuiReadToolGoldenTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The shared read-side golden cases (DevMind.Core.Tests/Shared/ReadToolGolden.cs, linked)
// through TuiAgenticHost. Same cases as BufferedReadToolGoldenTests and McpReadToolGoldenTests.
//
// Threading: with no running Terminal.Gui application the host writes transcript spans
// straight into the editor document, which only its creating thread may touch (in the app
// they are queued and pumped on the UI thread). The git branch awaits a process, so each case
// runs on one dedicated thread with a single-threaded SynchronizationContext — continuations
// come back to the thread that built the editor, as they do to the UI thread in the app.

using System.Collections.Concurrent;
using DevMind.ReadToolGolden;
using Xunit;

namespace DevMind.TUI.Tests
{
    public sealed class TuiReadToolGoldenTests
    {
        public static IEnumerable<object[]> Cases => ReadToolGoldenCases.Names;

        [Theory]
        [MemberData(nameof(Cases))]
        public Task Golden(string name) => SingleThread.Run(() =>
            ReadToolGoldenCases.RunAsync(name,
                root => new AgentHostAdapter(new TuiAgenticHost(root, new Terminal.Gui.Editor.Editor()))));

        /// <summary>Runs an async body to completion on one dedicated thread.</summary>
        private sealed class SingleThread : SynchronizationContext
        {
            private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();

            public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

            public static Task Run(Func<Task> body)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = new Thread(() =>
                {
                    var ctx = new SingleThread();
                    SetSynchronizationContext(ctx);
                    Task task;
                    try { task = body(); }
                    catch (Exception ex) { done.SetException(ex); return; }
                    task.ContinueWith(_ => ctx._queue.CompleteAdding(), TaskScheduler.Default);
                    foreach (var (d, state) in ctx._queue.GetConsumingEnumerable()) d(state);
                    if (task.IsFaulted) done.SetException(task.Exception!.InnerExceptions);
                    else if (task.IsCanceled) done.SetCanceled();
                    else done.SetResult();
                });
                thread.IsBackground = true;
                thread.Start();
                return done.Task;
            }
        }
    }
}
