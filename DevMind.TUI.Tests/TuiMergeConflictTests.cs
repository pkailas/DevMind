// File: TuiMergeConflictTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-69: with the three-way merge now real, the interactive TUI keeps its behaviour on an
// overlapping change — the write enters the pending-conflict state, later writes wait for /resolve
// — while a headless job refuses the one write instead (MergeConflictTests in Core).
//
// Threading: same as TuiReadToolGoldenTests — with no running Terminal.Gui application the host
// writes transcript spans straight into the editor document, which only its creating thread may
// touch, so the case runs on one dedicated thread with a single-threaded SynchronizationContext.

using System.Collections.Concurrent;
using Xunit;

namespace DevMind.TUI.Tests
{
    public sealed class TuiMergeConflictTests : IDisposable
    {
        private readonly string _dir;

        public TuiMergeConflictTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_tuimerge_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public Task AnOverlappingSave_EntersThePendingConflictState_AndLaterWritesWaitForResolve() => SingleThread.Run(async () =>
        {
            const string original = "alpha\nbravo\ncharlie\n";
            string path = Path.Combine(_dir, "notes.txt");
            File.WriteAllText(path, original);

            var host = new TuiAgenticHost(_dir, new Terminal.Gui.Editor.Editor());
            IAgenticHost h = host;
            await h.LoadFileContentAsync(path, 0, 0, false);              // the base the merge works from
            string onDisk = original.Replace("bravo", "BRAVO (outside)");
            File.WriteAllText(path, onDisk);

            Assert.Null(await h.SaveFileAsync(path, original.Replace("bravo", "BRAVO (agent)"), fromToolCall: true));
            Assert.Equal(onDisk, File.ReadAllText(path));

            Assert.Null(await h.SaveFileAsync(path, "anything\n", fromToolCall: true));   // waits for /resolve
            Assert.Equal(onDisk, File.ReadAllText(path));

            Assert.Contains("cancelled", host.ResolvePendingConflict("cancel"));
            Assert.Equal("[MERGE] No pending conflict to resolve.", host.ResolvePendingConflict("cancel"));
        });

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
