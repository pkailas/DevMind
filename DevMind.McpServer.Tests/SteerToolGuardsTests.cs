// File: SteerToolGuardsTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The GUARDS of devmind_task_steer, driven against a REAL AgentJobManager and
// (where a job must be in a particular state) a REAL job against a stub LLM server:
//
//   * a FINISHED job is refused and the error points the caller at devmind_task_continue
//   * a QUEUED job is refused and the error points the caller at devmind_task_status
//   * an UNKNOWN job_id is refused with the "not in this server process" answer
//   * an INVALID mode is refused (mode is an explicit flag, not parsed from the text)
//   * a RUNNING job (with a live session) is accepted — the single slot it fills
//
// devmind_task_continue is deliberately untouched by this feature; these tests pin that
// steer only reaches a running job and, when it cannot, says exactly which tool to use.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.McpServer.Tests
{
    /// <summary>
    /// A stub LLM server that ANSWERS GET probes (health/models/context) immediately but
    /// HOLDS every chat POST until <see cref="Release"/> is called — after which it
    /// answers each POST with a task_done SSE. Holding the POST parks a started job in the
    /// Running state with a live session (its worker thread is blocked in RunTurnAsync),
    /// which is exactly the state devmind_task_steer targets; and because the worker runs
    /// jobs one at a time, a second job started while the first is parked stays Queued.
    /// </summary>
    internal sealed class GatedHangingLlmServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _gate =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstPost =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public string BaseUrl { get; }

        /// <summary>Completes when the first chat POST arrives. By then the worker has already
        /// set the session's turn-in-progress flag (it is set at turn start, before the first
        /// request is sent), so awaiting this is a deterministic "a turn IS running" signal —
        /// a steer enqueued after it is guaranteed to hit the accepted path, not a startup window.</summary>
        public Task FirstPostReceived => _firstPost.Task;

        public GatedHangingLlmServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _ = Task.Run(LoopAsync);
        }

        /// <summary>Stops holding the POSTs and lets every held request complete with task_done.</summary>
        public void Release() => _gate.SetResult();

        private const string TaskDoneSse =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,"
            + "\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"task_done\","
            + "\"arguments\":\"{\\\"summary\\\":\\\"done\\\"}\"}}]}}]}\n\n"
            + "data: [DONE]\n\n";

        private async Task LoopAsync()
        {
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

                    byte[] payload;
                    string contentType;
                    if (method == "POST" && !string.IsNullOrEmpty(body))
                    {
                        // A chat POST arrived — the turn is in progress. Signal that, then
                        // hold the POST until released (so the job parks in Running with a live
                        // turn), and finish the job once released.
                        _firstPost.TrySetResult();
                        try { await _gate.Task; } catch { return; }
                        payload = Encoding.UTF8.GetBytes(TaskDoneSse);
                        contentType = "text/event-stream";
                    }
                    else
                    {
                        // GET health/models/context probes — answer immediately.
                        payload = Encoding.UTF8.GetBytes("{}");
                        contentType = "application/json";
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n" +
                        $"Content-Type: {contentType}\r\n" +
                        $"Content-Length: {payload.Length}\r\n" +
                        "Connection: close\r\n\r\n"));
                    await stream.WriteAsync(payload);
                    await stream.FlushAsync();
                }
            }
        }

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
            _gate.TrySetResult();   // never leave a parked job holding its connection
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }

    public sealed class SteerToolGuardsTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;

        public SteerToolGuardsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_steer_guard_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            // The local stub speaks the llama request shape; AgentJobManager reads the
            // endpoint at construction, LlmClient reads DEVMIND_SERVER_TYPE per request.
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
        }

        public void Dispose()
        {
            if (_priorEndpoint == null)
                Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", null);
            else
                Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            if (_priorServerType == null)
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", null);
            else
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        // ── Helpers ──

        private static async Task WaitForStateAsync(DevMind.McpServer.AgentJob job,
            DevMind.McpServer.AgentJobState expected, int timeoutMs = 20000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && job.State != expected)
                await Task.Delay(25);
            Assert.True(job.State == expected,
                $"job {job.Id} timed out at {job.State} (expected {expected})");
        }

        // The tool returns {"error": "..."} on a guard hit and a JSON object with
        // accepted=true on success. Parse into (accepted?, error?) so assertions are
        // robust to the indented JSON formatting.
        private static (bool? accepted, string? error) Parse(string toolResult)
        {
            using var doc = JsonDocument.Parse(toolResult);
            bool? accepted = doc.RootElement.TryGetProperty("accepted", out var a) ? a.GetBoolean() : null;
            string? error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            return (accepted, error);
        }

        // ── A finished job is refused, and the error names the right tool ──

        [Fact]
        public async Task Steer_FinishedJob_IsRejected_NamingContinue()
        {
            using var server = new FakeLlmServer();          // task_done → the job finishes fast
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();
            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: false);
            await WaitForStateAsync(job, DevMind.McpServer.AgentJobState.Done);

            var (accepted, error) = Parse(await new DevMind.McpServer.AgentTaskTools(mgr)
                .TaskSteer(job.Id, "change course", "override"));

            Assert.Null(accepted);                            // no "accepted" field → an error
            Assert.NotNull(error);
            Assert.Contains("not running", error);
            Assert.Contains("devmind_task_continue", error);
            Assert.DoesNotContain("devmind_task_status", error);
        }

        // ── A queued job is refused, and the error names the right tool ──

        [Fact]
        public async Task Steer_QueuedJob_IsRejected_NamingStatus()
        {
            using var server = new GatedHangingLlmServer();  // holds the POST → job A parks in Running
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var a = mgr.Start("a", _dir, 5, 30, allowCommit: false, verifyBuild: false);
            await WaitForStateAsync(a, DevMind.McpServer.AgentJobState.Running);  // worker now blocked on A
            var b = mgr.Start("b", _dir, 5, 30, allowCommit: false, verifyBuild: false);
            await WaitForStateAsync(b, DevMind.McpServer.AgentJobState.Queued);   // B waits behind A

            var (accepted, error) = Parse(await new DevMind.McpServer.AgentTaskTools(mgr)
                .TaskSteer(b.Id, "change course", "suggest"));

            Assert.Null(accepted);
            Assert.NotNull(error);
            Assert.Contains("not running", error);
            Assert.Contains("devmind_task_status", error);    // not yet started — poll status, not continue
            Assert.DoesNotContain("devmind_task_continue", error);

            server.Release();                                  // let both jobs finish so dispose is clean
            await WaitForStateAsync(a, DevMind.McpServer.AgentJobState.Done);
            await WaitForStateAsync(b, DevMind.McpServer.AgentJobState.Done);
        }

        // ── A running job (with a live session) is accepted into the single slot ──

        [Fact]
        public async Task Steer_RunningJob_IsAccepted()
        {
            using var server = new GatedHangingLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var job = mgr.Start("a", _dir, 5, 30, allowCommit: false, verifyBuild: false);
            await WaitForStateAsync(job, DevMind.McpServer.AgentJobState.Running);
            // A steer is only accepted while a turn is actually RUNNING (the enqueue-after-
            // turn-end fix). The first POST arriving means the turn is in progress, so this is
            // a deterministic accepted case — not a startup window that would now be refused.
            await server.FirstPostReceived;

            var (accepted, error) = Parse(await new DevMind.McpServer.AgentTaskTools(mgr)
                .TaskSteer(job.Id, "fold this in", "suggest"));

            Assert.Null(error);
            Assert.Equal(true, accepted);

            server.Release();
            await WaitForStateAsync(job, DevMind.McpServer.AgentJobState.Done);
        }

        // ── The fix: the turn has ended but the job is still Running (build verification) ──
        // Pre-fix this window stranded a steer (accepted=true, then lost or leaked to a
        // continuation). Now the worker holds the job in Running through build verification
        // AFTER the turn's flag has been cleared, giving a stable "Running, no turn" state:
        // EnqueueSteer refuses it, so TaskSteer returns accepted=false with a reason.

        [Fact]
        public async Task Steer_TurnEndedButJobStillRunning_IsRefused_WithReason()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();
            // Hold the build-verification window open: the job stays Running (turn already
            // ended, flag cleared) for as long as this delay runs.
            mgr.BuildRunnerOverride = async (_, ct) =>
            {
                await Task.Delay(2500, ct);
                return new DevMind.McpServer.BuildVerification
                {
                    Command = "dotnet build", ExitCode = 0, OutputTail = "Build succeeded.",
                };
            };

            var job = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: true, verifyTests: false);
            var tools = new DevMind.McpServer.AgentTaskTools(mgr);

            // Wait for the exact window: the turn has ended (Result set) but the job is still
            // Running because build verification is holding it.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000
                   && !(job.Result != null && job.State == DevMind.McpServer.AgentJobState.Running))
                await Task.Delay(10);
            Assert.True(job.Result != null && job.State == DevMind.McpServer.AgentJobState.Running,
                "never reached the Running-but-no-turn (build verification) window");

            var (accepted, error) = Parse(await tools.TaskSteer(job.Id, "change course", "suggest"));
            Assert.Equal(false, accepted);        // REFUSED — not accepted=true (the old bug)
            Assert.Null(error);                   // a structured refusal, not an "error"

            using var doc = System.Text.Json.JsonDocument.Parse(
                await tools.TaskSteer(job.Id, "change course", "suggest"));
            Assert.True(doc.RootElement.TryGetProperty("reason", out var reason));
            Assert.Contains("not in a turn", reason.GetString());

            await WaitForDoneAsync(job);
        }

        // ── Outcome 2: a steer sent while job N is Running-but-finished never reaches N+1 ──
        // The continuation reuses job N's session. Pre-fix, a steer enqueued in this window
        // stranded in _pendingSteer and drained into the CONTINUATION's first iteration,
        // recorded in N+1's journal. Now it is refused outright, so it is in NEITHER job's
        // journal. This is the "attributed to a job nobody steered" regression.

        [Fact]
        public async Task Steer_SentToFinishedJob_NeverAppearsInContinuationJournal()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();
            mgr.BuildRunnerOverride = async (_, ct) =>
            {
                await Task.Delay(2500, ct);
                return new DevMind.McpServer.BuildVerification
                {
                    Command = "dotnet build", ExitCode = 0, OutputTail = "Build succeeded.",
                };
            };

            var n = mgr.Start("n", _dir, 5, 30,
                allowCommit: false, verifyBuild: true, verifyTests: false);
            var tools = new DevMind.McpServer.AgentTaskTools(mgr);

            // Enter the Running-but-no-turn window (turn ended, build verification holding it).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000
                   && !(n.Result != null && n.State == DevMind.McpServer.AgentJobState.Running))
                await Task.Delay(10);
            Assert.True(n.Result != null && n.State == DevMind.McpServer.AgentJobState.Running,
                "never reached the Running-but-no-turn window");

            // The steer that pre-fix would have leaked. Now it is refused — nothing queued.
            var (accepted, _) = Parse(await tools.TaskSteer(n.Id, "leaky steer", "suggest"));
            Assert.Equal(false, accepted);
            await WaitForDoneAsync(n);

            // Continue as a NEW job (reuses n's session, conversation intact).
            var n1 = mgr.Continue(n.Id, "continue", 5, 30, verifyBuild: false, out string contErr);
            Assert.Null(contErr);
            Assert.NotNull(n1);
            await WaitForDoneAsync(n1);

            // THE invariant: the steer is in NEITHER job's journal — it was refused, so it
            // was never queued and never drained. Job N+1 (which n did not steer) is clean.
            Assert.NotNull(n.Result);
            Assert.NotNull(n1.Result);
            Assert.DoesNotContain(n.Result!.Actions, a => a.Detail.Contains("leaky steer"));
            Assert.DoesNotContain(n1.Result!.Actions, a => a.Detail.Contains("leaky steer"));
        }

        // Polls until the job leaves Queued/Running.
        private static async Task WaitForDoneAsync(DevMind.McpServer.AgentJob job, int timeoutMs = 30000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs
                   && job.State is DevMind.McpServer.AgentJobState.Queued
                      or DevMind.McpServer.AgentJobState.Running)
                await Task.Delay(20);
            Assert.Equal(DevMind.McpServer.AgentJobState.Done, job.State);
        }

        // ── An unknown job_id is refused with the honest answer ──

        [Fact]
        public async Task Steer_UnknownJob_IsRejected()
        {
            using var server = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var (accepted, error) = Parse(await new DevMind.McpServer.AgentTaskTools(mgr)
                .TaskSteer("no-such-job", "change course", "suggest"));

            Assert.Null(accepted);
            Assert.NotNull(error);
            Assert.Contains("Unknown job_id", error);
        }

        // ── Mode is an explicit flag, not parsed from the text ──

        [Fact]
        public async Task Steer_InvalidMode_IsRejected()
        {
            using var server = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();
            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: false);
            await WaitForStateAsync(job, DevMind.McpServer.AgentJobState.Done);

            var (accepted, error) = Parse(await new DevMind.McpServer.AgentTaskTools(mgr)
                .TaskSteer(job.Id, "change course", "yell"));

            Assert.Null(accepted);
            Assert.NotNull(error);
            Assert.Contains("mode must be", error);
        }

        // ── An empty message is refused before any job lookup ──

        [Fact]
        public async Task Steer_EmptyMessage_IsRejected()
        {
            using var server = new FakeLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var (accepted, error) = Parse(await new DevMind.McpServer.AgentTaskTools(mgr)
                .TaskSteer("whatever", "   ", "suggest"));

            Assert.Null(accepted);
            Assert.NotNull(error);
            Assert.Contains("message is required", error);
        }
    }
}
