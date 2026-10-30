// File: ResumeRewindUiMarshalTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// /resume and /rewind dispatched from a real (headless) Terminal.Gui UI thread, with a history
// store whose every call genuinely yields. Terminal.Gui 2.4.17's plain SynchronizationContext
// resumes the handlers on a pool thread after those awaits, so each UI effect after them —
// the session reset, the transcript replay, the input-box text — must reach the UI thread
// through ctx.RunOnUiThread (wired exactly as Program.cs wires it: TuiTurnTeardown.RunOnUiAsync)
// and in the handler's order: the reset finishes on the OLD session id before the fork's id
// is adopted and its rows are prepended and replayed.
//
// Same harness shape as TurnTeardownMarshalTests.cs: the main loop runs on a background thread.

using DevMind;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class ResumeRewindUiMarshalTests
    {
        sealed class TestApp : IDisposable
        {
            public IApplication App { get; }
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
                        var window = new Window { Title = "DevMind resume/rewind test" };
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

        // Run a function on the UI thread and return its result (test-side).
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

        // A store whose every call really yields — the continuation cannot stay on the caller.
        private sealed class YieldingHistoryStore : IHistoryStore
        {
            public HistoryMessage[] Messages { get; set; } = Array.Empty<HistoryMessage>();
            public SessionSummary[] Sessions { get; set; } = Array.Empty<SessionSummary>();

            public async Task<HistoryMessage[]> LoadSessionMessagesAsync(string sessionId)
            { await Task.Yield(); return Messages; }
            public async Task SaveMessagesAsync(HistoryMessage[] messages) => await Task.Yield();
            public async Task UpsertSessionAsync(string sessionId, string machineName) => await Task.Yield();
            public async Task SetSessionTitleAsync(string sessionId, string title) => await Task.Yield();
            public async Task<SessionSummary[]> ListSessionsAsync(string machineName)
            { await Task.Yield(); return Sessions; }
            public Task<HistoryMessage[]> LoadMessagesAsync(string machineName, int maxTurns)
                => Task.FromResult(Array.Empty<HistoryMessage>());
            public Task InitAsync() => Task.CompletedTask;
            public Task CloseAsync() => Task.CompletedTask;
        }

        private const string S = "2026-10-30T100000Z-pid1";

        private static HistoryMessage Row(int turn, string role, string content) => new HistoryMessage
        {
            SessionId = S,
            MachineName = "beast",
            TurnIndex = turn,
            Role = role,
            Content = content,
            CreatedAt = new DateTime(2026, 10, 30, 10, 0, turn, DateTimeKind.Utc),
        };

        private static HistoryMessage[] Session() => new[]
        {
            Row(0, "user", "Fix the login bug."),
            Row(0, "assistant", "Fixed it in Auth.cs."),
            Row(1, "user", "Now add tests."),
            Row(1, "assistant", "Tests added."),
            Row(2, "user", "Ship it."),
            Row(2, "assistant", "Shipped."),
        };

        // Records each effect with the thread it ran on, in order.
        private sealed class Recorder
        {
            private readonly object _gate = new();
            private readonly List<(string name, int thread)> _calls = new();
            public void Add(string name)
            {
                lock (_gate) _calls.Add((name, Environment.CurrentManagedThreadId));
            }
            public (string name, int thread)[] Calls { get { lock (_gate) return _calls.ToArray(); } }
            public string[] Names => Calls.Select(c => c.name).ToArray();
            public int ThreadOf(string name) => Calls.Single(c => c.name == name).thread;
        }

        private static (CommandContext ctx, Recorder rec, Func<string?> idAtReset) Context(TestApp ta)
        {
            var rec = new Recorder();
            // The host's session id, as SessionId would hold it: Reset clears it, Adopt sets it.
            string? currentId = S, idAtReset = null;
            var ctx = new CommandContext
            {
                HistoryStore = new YieldingHistoryStore
                {
                    Messages = Session(),
                    Sessions = new[] { new SessionSummary { SessionId = S, Title = "Auth work", MessageCount = 6 } },
                },
                SessionId = S,
                MachineName = "beast",
                // Wired exactly as Program.cs wires it.
                RunOnUiThread = action => TuiTurnTeardown.RunOnUiAsync(ta.App, action),
                ResetConversation = () => { rec.Add("Reset"); idAtReset = currentId; currentId = null; },
                AdoptSessionId = id => { rec.Add("Adopt"); currentId = id; },
                PrependMessages = (roles, contents) => rec.Add("Prepend"),
                ReplayTranscript = (roles, contents) => rec.Add("Replay"),
                SetInputBoxText = text => rec.Add("SetInputBoxText"),
            };
            return (ctx, rec, () => idAtReset);
        }

        // Start the dispatch ON the UI thread, as the input handler does, then wait for it.
        static async Task<CommandResult> DispatchFromUiAsync(TestApp ta, string input, CommandContext ctx)
        {
            Task<CommandResult> dispatch = await OnUiAsync(ta, () => SlashCommand.Dispatch(input, ctx));
            return await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task Resume_AfterYieldingStoreAwaits_ReplaysOnUiThread_AfterPrepend()
        {
            using var ta = TestApp.Start();
            var (ctx, rec, _) = Context(ta);

            var result = await DispatchFromUiAsync(ta, "/resume 1", ctx);

            Assert.False(result.IsError, result.Message);
            Assert.Equal(new[] { "Prepend", "Replay" }, rec.Names);
            // The awaits genuinely moved the handler off the UI thread (the non-UI prepend
            // ran there) — so the replay landing on the UI thread is the marshal's doing.
            Assert.NotEqual(ta.UiThreadId, rec.ThreadOf("Prepend"));
            Assert.Equal(ta.UiThreadId, rec.ThreadOf("Replay"));
        }

        [Fact]
        public async Task Rewind_AfterYieldingStoreAwaits_ResetsAndReplaysOnUiThread_InOrder()
        {
            using var ta = TestApp.Start();
            var (ctx, rec, idAtReset) = Context(ta);

            var result = await DispatchFromUiAsync(ta, "/rewind 3", ctx);

            Assert.False(result.IsError, result.Message);
            // The reset finishes before the adopt (so it deletes the OLD session's images and
            // resets the OLD id) and before the prepend (its ClearHistory cannot wipe the fork).
            Assert.Equal(new[] { "Reset", "Adopt", "Prepend", "Replay", "SetInputBoxText" }, rec.Names);
            Assert.Equal(S, idAtReset());
            Assert.NotEqual(ta.UiThreadId, rec.ThreadOf("Adopt"));
            Assert.NotEqual(ta.UiThreadId, rec.ThreadOf("Prepend"));
            Assert.Equal(ta.UiThreadId, rec.ThreadOf("Reset"));
            Assert.Equal(ta.UiThreadId, rec.ThreadOf("Replay"));
            Assert.Equal(ta.UiThreadId, rec.ThreadOf("SetInputBoxText"));
        }
    }
}
