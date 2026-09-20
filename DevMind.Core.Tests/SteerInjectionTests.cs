// File: SteerInjectionTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// End-to-end behaviour of devmind_task_steer against the REAL HeadlessSession loop:
//
//   * the single-slot mailbox — supersession is reported AND the superseded steer is
//     recorded in the journal (never silently dropped)
//   * a steer folds into the prompt at the next iteration boundary (its own request,
//     not glued to the driver's synthetic re-trigger) and reaches the action journal
//   * an OVERRIDE is rejected on the LAST iteration and recorded as steer_rejected,
//     while a SUGGESTION is still folded on the last iteration
//   * a steer that is pending when the turn ends is recorded as steer_unconsumed
//
// The last-iteration cases need the loop to actually reach the depth the driver flags
// as "last", so they drive a real multi-iteration turn through a response-GATED server
// (see GatedSseServer): the server holds the model's reply until the test releases it,
// which pins the iteration boundary the steer is enqueued against — deterministic, not
// a sleep-and-pray race.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    // ── A response-gated variant of the test LLM server ───────────────────────────

    // Like TestInfra.FakeSseServer (each chat POST consumes the next SseQueue entry,
    // records the request body), but before replying to the FIRST chat POST it awaits
    // <see cref="GateBeforeFirstResponse"/>. That lets a test enqueue a steer while the
    // loop is blocked in its first SendMessageAsync — so the steer is GUARANTEED to be
    // pending at the SECOND iteration's drain (the loop cannot get ahead of the gate).
    internal sealed class GatedSseServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private int _sseIndex;

        public List<string> RequestBodies { get; } = new();
        public string BaseUrl { get; }
        public List<string> SseQueue { get; } = new();
        /// <summary>If set, the server awaits this before responding to the first chat POST.</summary>
        public Task? GateBeforeFirstResponse { get; set; }

        public GatedSseServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            bool firstChat = true;
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { return; }

                using (tcp)
                using (var stream = tcp.GetStream())
                {
                    var request = await ReadRequestAsync(stream);
                    if (request == null) continue;
                    var (method, path, body) = request.Value;

                    bool isChatPost = method == "POST" && body.Length > 0 &&
                        !path.Contains("/embeddings");
                    if (isChatPost)
                    {
                        lock (RequestBodies) RequestBodies.Add(body);

                        if (firstChat)
                        {
                            firstChat = false;
                            var gate = GateBeforeFirstResponse;
                            if (gate != null)
                                await gate;   // hold the reply until the test releases the gate
                        }

                        int idx;
                        lock (SseQueue) idx = _sseIndex < SseQueue.Count ? _sseIndex++ : -1;
                        string payload = idx >= 0 ? SseQueue[idx] : FakeSseServer.BuildTextSse("ok");
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(
                            "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: text/event-stream\r\n" +
                            $"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n" +
                            "Connection: close\r\n\r\n"));
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(payload));
                        await stream.FlushAsync();
                    }
                    else
                    {
                        // Health / model probes — empty JSON list, not recorded.
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                            "Content-Length: 12\r\nConnection: close\r\n\r\n{\"data\":[]}"));
                    }
                }
            }
        }

        // Minimal request reader (header + body up to Content-Length), mirroring FakeSseServer.
        private static async Task<(string method, string path, string body)?> ReadRequestAsync(NetworkStream stream)
        {
            var headerBuf = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                int n = await stream.ReadAsync(one.AsMemory(0, 1));
                if (n == 0) return null;
                headerBuf.WriteByte(one[0]);
                if (headerBuf.Length >= 4)
                {
                    var b = headerBuf.GetBuffer();
                    long len = headerBuf.Length;
                    if (b[len - 4] == '\r' && b[len - 3] == '\n' && b[len - 2] == '\r' && b[len - 1] == '\n')
                        break;
                }
            }
            string headerText = Encoding.ASCII.GetString(headerBuf.ToArray());
            string[] headerLines = headerText.Split("\r\n");
            string[] requestLine = headerLines[0].Split(' ');
            string method = requestLine[0];
            string path = requestLine.Length > 1 ? requestLine[1] : "";
            int contentLength = 0;
            foreach (string line in headerLines)
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(line.Substring(15).Trim());
            if (contentLength == 0) return (method, path, string.Empty);

            var bodyBytes = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = await stream.ReadAsync(bodyBytes.AsMemory(read, contentLength - read));
                if (n == 0) break;
                read += n;
            }
            return (method, path, Encoding.UTF8.GetString(bodyBytes, 0, read));
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _acceptLoop.Wait(2000); } catch { /* accept-loop teardown races are fine */ }
            _cts.Dispose();
        }
    }

    // ── The behaviour ─────────────────────────────────────────────────────────────

    public class SteerInjectionTests : IDisposable
    {
        private readonly string _dir;

        public SteerInjectionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_steer_inject_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private static HeadlessOptions Options(int maxDepth = 5) => new HeadlessOptions
        {
            RequestTimeoutMinutes = 1,
            FirstTokenTimeoutMinutes = 1,
            ManualContextSize = 32768,      // skip context probes
            AgenticLoopMaxDepth = maxDepth,
        };

        private HeadlessSession NewSession(string endpoint) => new(
            Options(), endpoint, apiKey: null!,
            workingDirectory: _dir, buildCommand: "dotnet build",
            promptFilePath: Path.Combine(_dir, "nonexistent-prompt.md"));

        private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline && !condition())
                await Task.Delay(10);
        }

        // ── Single-slot mailbox: supersession is reported, the old steer is recorded ──
        // EnqueueSteer now requires a turn in progress (the enqueue-after-turn-end fix), so
        // this drives a real turn through the gated server and enqueues while it is held.
        // What it proves is unchanged: last-write-wins, the superseded steer is recorded,
        // and its mode is reported so a downgrade onto a pending override is visible.

        [Fact]
        public async Task EnqueueSteer_SingleSlot_ReplacesAndRecordsTheSupersededSteer()
        {
            using var server = new GatedSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.GateBeforeFirstResponse = gate.Task;

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = NewSession(server.BaseUrl);
                var turn = session.RunTurnAsync("Work.");
                await WaitForAsync(() => server.RequestBodies.Count >= 1);   // turn in progress

                // First steer: nothing pending to supersede. (Turn in progress → accepted.)
                SteerEnqueueResult r1 = session.EnqueueSteer("first steer", SteerMode.Suggest);
                Assert.True(r1.Accepted);
                Assert.False(r1.Superseded);
                Assert.Null(r1.SupersededMode);
                // Pending — not yet folded, so not yet in the journal either.
                Assert.Empty(session.JournalForTest);

                // Second steer (override): replaces the still-un-consumed suggest.
                SteerEnqueueResult r2 = session.EnqueueSteer("second steer", SteerMode.Override);
                Assert.True(r2.Accepted);
                Assert.True(r2.Superseded);
                Assert.Equal(SteerMode.Suggest, r2.SupersededMode);        // mode of the steer it replaced
                var afterSecond = session.JournalForTest;
                HostAction superseded = Assert.Single(afterSecond, a => a.Kind == "steer_unconsumed");
                Assert.Contains("first steer", superseded.Detail); // the OLD steer, …
                Assert.DoesNotContain("second steer", superseded.Detail);
                Assert.DoesNotContain(afterSecond, a => a.Detail.Contains("second steer")); // … not the new one

                // Third steer (suggest) onto a pending OVERRIDE — the downgrade is visible.
                SteerEnqueueResult r3 = session.EnqueueSteer("third steer", SteerMode.Suggest);
                Assert.True(r3.Accepted);
                Assert.True(r3.Superseded);
                Assert.Equal(SteerMode.Override, r3.SupersededMode);      // it replaced a pending override

                gate.SetResult();                                          // let the turn end
                var result = await turn;
                Assert.Null(result.Error);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        // ── The refusal: no turn in progress → refused, nothing queued or recorded ──

        [Fact]
        public void EnqueueSteer_WithNoTurnInProgress_IsRefused_NotAccepted()
        {
            using var server = new FakeSseServer();
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = NewSession(server.BaseUrl);
                // No turn has been started — the mailbox must not accept a steer into a
                // session nothing will ever drain.
                SteerEnqueueResult r = session.EnqueueSteer("too late", SteerMode.Suggest);
                Assert.False(r.Accepted);
                Assert.Empty(session.JournalForTest);      // nothing was queued or recorded
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        // ── Atomicity: a steer cannot slip between the final drain and the flag clear ──
        // A steer enqueued BEFORE the turn ends (flag still true) is captured by the atomic
        // close and recorded as unconsumed; one enqueued AFTER (flag already cleared) is
        // refused. Taking and clearing in one lock acquisition is what makes "neither"
        // impossible — there is no moment where a steer is accepted but unaccounted for.

        [Fact]
        public async Task EnqueueSteer_AtomicClose_CapturesBeforeEnd_AndRefusesAfter()
        {
            using var server = new GatedSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.GateBeforeFirstResponse = gate.Task;

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = NewSession(server.BaseUrl);
                var turn = session.RunTurnAsync("Work.");
                await WaitForAsync(() => server.RequestBodies.Count >= 1);   // turn in progress

                // Enqueue BEFORE the turn ends (flag true) → accepted into the mailbox.
                SteerEnqueueResult before = session.EnqueueSteer("before the close", SteerMode.Suggest);
                Assert.True(before.Accepted);

                // End the turn — the atomic close takes-and-clears in one lock acquisition.
                gate.SetResult();
                var result = await turn;
                Assert.Null(result.Error);

                // The "before" steer was accepted but never folded (turn ended first) → unconsumed.
                HostAction captured = Assert.Single(result.Actions, a => a.Detail.Contains("before the close"));
                Assert.Equal("steer_unconsumed", captured.Kind);

                // Enqueue AFTER the turn ends (flag cleared) → REFUSED, and it is not recorded.
                SteerEnqueueResult after = session.EnqueueSteer("after the close", SteerMode.Suggest);
                Assert.False(after.Accepted);
                Assert.DoesNotContain(session.JournalForTest, a => a.Detail.Contains("after the close"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        // ── A steer folds into the next iteration boundary and reaches the journal ──

        [Fact]
        public async Task SteerFoldsIntoPrompt_AtNextBoundary_AndIsRecordedInTheJournal()
        {
            using var server = new GatedSseServer();
            // Two iterations: create_file (re-triggers) then task_done (terminal). The steer
            // is enqueued while iteration 1 is held, so a turn IS in progress, and it is
            // drained at the NEXT boundary — iteration 2's prompt, not the driver's re-trigger.
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("create_file",
                "{\"filename\":\"a.txt\",\"content\":\"x\"}"));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.GateBeforeFirstResponse = gate.Task;

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = NewSession(server.BaseUrl);
                var turn = session.RunTurnAsync("Fix the login bug.");
                await WaitForAsync(() => server.RequestBodies.Count >= 1);   // iteration 1 held
                // A steer can only be accepted while a turn is running — enqueue it now.
                SteerEnqueueResult r = session.EnqueueSteer("also mention the rollback plan", SteerMode.Suggest);
                Assert.True(r.Accepted);
                gate.SetResult();                                            // iter 1 → re-trigger → iter 2
                var result = await turn;
                Assert.Null(result.Error);
                Assert.Equal(2, result.Iterations);

                // The steer reached the model at the NEXT iteration boundary (iteration 2's
                // request) — not glued into the driver's re-trigger, and not in iteration 1.
                Assert.Equal(2, server.RequestBodies.Count);
                string sent = server.RequestBodies[1];
                Assert.Contains("also mention the rollback plan", sent);
                Assert.Contains("[CALLER STEER — suggestion]", sent);
                Assert.DoesNotContain("also mention the rollback plan", server.RequestBodies[0]);

                // …and it is in the action journal the result serves (devmind_task_result).
                HostAction steer = Assert.Single(result.Actions, a => a.Kind == "steer");
                Assert.True(steer.Success);
                Assert.Contains("also mention the rollback plan", steer.Detail);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        // ── Override on the LAST iteration is rejected; the steer is not sent ──

        [Fact]
        public async Task OverrideOnLastIteration_IsRejected_AndRecordedAsSteerRejected()
        {
            using var server = new GatedSseServer();
            // maxDepth == 1: iteration 1 (depth 0) re-triggers; iteration 2 (depth 1)
            // is the LAST iteration the driver will run.
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("create_file",
                "{\"filename\":\"a.txt\",\"content\":\"x\"}"));   // iter 1 → re-trigger (depth 1)
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"wrapped up\"}"));                 // iter 2 (LAST) → terminal
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.GateBeforeFirstResponse = gate.Task;

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = NewSession(server.BaseUrl);
                // maxDepth must be 1 for iteration 2 to be the last — set it on the session.
                session.SetMaxDepth(1);

                var turn = session.RunTurnAsync("Build it.");
                await WaitForAsync(() => server.RequestBodies.Count >= 1);   // iter 1 request received
                // Enqueue while the loop is blocked waiting for the reply to iter 1:
                // the steer is therefore pending at iteration 2's (the LAST one's) drain.
                session.EnqueueSteer("abort — use the other approach", SteerMode.Override);
                gate.SetResult();                                            // release: reply to iter 1

                var result = await turn;
                Assert.Null(result.Error);
                Assert.Equal(2, result.Iterations);

                // The override was NOT sent to the model in the last iteration's request.
                Assert.Equal(2, server.RequestBodies.Count);
                Assert.DoesNotContain("abort — use the other approach", server.RequestBodies[1]);
                Assert.DoesNotContain("[CALLER STEER — override]", server.RequestBodies[1]);

                // And it is recorded in the journal as a REJECTED steer.
                HostAction rejected = Assert.Single(result.Actions, a => a.Kind == "steer_rejected");
                Assert.False(rejected.Success);
                Assert.Contains("abort — use the other approach", rejected.Detail);
                Assert.Contains("last_iteration", rejected.Detail);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        // ── A Suggestion on the LAST iteration is still folded (the refinement) ──

        [Fact]
        public async Task SuggestOnLastIteration_IsStillFolded()
        {
            using var server = new GatedSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("create_file",
                "{\"filename\":\"a.txt\",\"content\":\"x\"}"));   // iter 1 → re-trigger (depth 1)
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"wrapped up\"}"));                 // iter 2 (LAST) → terminal
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.GateBeforeFirstResponse = gate.Task;

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = NewSession(server.BaseUrl);
                session.SetMaxDepth(1);

                var turn = session.RunTurnAsync("Build it.");
                await WaitForAsync(() => server.RequestBodies.Count >= 1);
                session.EnqueueSteer("mention the rollback plan in your summary", SteerMode.Suggest);
                gate.SetResult();

                var result = await turn;
                Assert.Null(result.Error);
                Assert.Equal(2, result.Iterations);

                // Unlike the override, the suggestion DID reach the last iteration's prompt.
                Assert.Equal(2, server.RequestBodies.Count);
                Assert.Contains("mention the rollback plan in your summary", server.RequestBodies[1]);
                Assert.Contains("[CALLER STEER — suggestion]", server.RequestBodies[1]);

                HostAction steer = Assert.Single(result.Actions, a => a.Kind == "steer");
                Assert.True(steer.Success);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        // ── A steer pending when the turn ends is recorded as unconsumed ──

        [Fact]
        public async Task SteerPendingAtTurnEnd_IsRecordedAsUnconsumed()
        {
            using var server = new GatedSseServer();
            // A single-iteration turn (task_done on iteration 1, no re-trigger). The
            // steer is enqueued after that (only) iteration boundary, so it is pending
            // when the turn ends — never folded.
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.GateBeforeFirstResponse = gate.Task;

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = NewSession(server.BaseUrl);
                var turn = session.RunTurnAsync("Do one thing.");
                await WaitForAsync(() => server.RequestBodies.Count >= 1);   // the (only) request out
                session.EnqueueSteer("never folded in time", SteerMode.Suggest);
                gate.SetResult();                                            // reply task_done → terminal

                var result = await turn;
                Assert.Null(result.Error);
                Assert.Equal(1, result.Iterations);

                // It was accepted but the turn ended before the next boundary.
                HostAction unconsumed = Assert.Single(result.Actions, a => a.Kind == "steer_unconsumed");
                Assert.False(unconsumed.Success);
                Assert.Contains("never folded in time", unconsumed.Detail);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }
    }
}
