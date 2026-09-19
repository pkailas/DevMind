// File: NoExecuteInheritanceTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for no_execute inheritance across devmind_task_continue — the failure
// mode that motivated the whole change: a constraint the caller set on turn 1
// must NOT evaporate on turn 2 (or turn N).
//
// Two layers are covered:
//   * ResolveContinuationNoExecute — the pure inheritance decision (table).
//   * AgentJobManager end-to-end — a real job is started with noExecute, the
//     worker runs it to completion against a fake LLM server (task_done only,
//     no processes spawned), the session is retained, devmind_task_continue is
//     called, and the NEW job is asserted to have inherited NoExecute. This is
//     the real continuation path (session transfer + flag inheritance), not a
//     comment.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.McpServer.Tests
{
    /// <summary>Minimal fake LLM server that handles the full LlmClient request cycle:
    /// GET (health/models probes) → 200 {}; POST (chat) → SSE with a task_done tool
    /// call. Uses Content-Length to read the body, and Connection: close to terminate
    /// the socket cleanly — same pattern as DevMind.Core.Tests' FakeSseServer.
    /// Sufficient for driving HeadlessSession through a clean turn without spawning
    /// any process.</summary>
    internal sealed class FakeLlmServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public string BaseUrl { get; }

        public FakeLlmServer()
        {
            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private static int GetFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

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
                        // POST /v1/chat/completions → SSE task_done stream.
                        string sse = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,"
                            + "\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"task_done\","
                            + "\"arguments\":\"{\\\"summary\\\":\\\"done\\\"}\"}}]}}]}\n\n"
                            + "data: [DONE]\n\n";
                        payload = Encoding.UTF8.GetBytes(sse);
                        contentType = "text/event-stream";
                    }
                    else
                    {
                        // GET health/models probes.
                        payload = Encoding.UTF8.GetBytes("{}");
                        contentType = "application/json";
                    }

                    string headers =
                        "HTTP/1.1 200 OK\r\n" +
                        $"Content-Type: {contentType}\r\n" +
                        $"Content-Length: {payload.Length}\r\n" +
                        "Connection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
                    await stream.WriteAsync(payload);
                    await stream.FlushAsync();
                }
            }
        }

        // Reads headers byte-by-byte until blank line; reads exactly Content-Length bytes
        // of body (avoids ReadToEnd hanging on keep-alive). Same pattern as TestInfra.ReadRequestAsync.
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
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(line.Substring(15).Trim());
            }

            string body = string.Empty;
            if (contentLength > 0)
            {
                var bodyBuf = new byte[contentLength];
                int offset = 0;
                while (offset < contentLength)
                {
                    int read = await stream.ReadAsync(bodyBuf.AsMemory(offset, contentLength - offset));
                    if (read == 0) break;
                    offset += read;
                }
                body = Encoding.UTF8.GetString(bodyBuf, 0, offset);
            }

            return (method, path, body);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }

    public sealed class ResolveContinuationNoExecuteTests
    {
        [Theory]
        // Inherited: parent has it, caller says nothing (null) or false -> still on.
        [InlineData(true, null, true)]
        [InlineData(true, false, true)]   // false must NOT relax the parent's restriction
        [InlineData(true, true, true)]
        // Parent didn't have it.
        [InlineData(false, null, false)]
        [InlineData(false, false, false)]
        // Caller can turn it ON for a continuation that didn't have it.
        [InlineData(false, true, true)]
        public void Resolves(bool parent, bool? requested, bool expected)
        {
            Assert.Equal(expected,
                DevMind.McpServer.AgentJobManager.ResolveContinuationNoExecute(parent, requested));
        }
    }

    public sealed class NoExecuteInheritanceTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;

        public NoExecuteInheritanceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_noexec_job_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            // Force the llama request template (the env var the Core tests set for
            // FakeSseServer-driven turns) so the local server speaks the same shape.
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
        }

        public void Dispose()
        {
            // Restore env vars (AgentJobManager reads endpoint at construction;
            // LlmClient reads DEVMIND_SERVER_TYPE per request).
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

        private static async Task<DevMind.McpServer.AgentJob> WaitForDone(
            DevMind.McpServer.AgentJob job, int timeoutMs = 20000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs
                   && job.State is DevMind.McpServer.AgentJobState.Queued
                      or DevMind.McpServer.AgentJobState.Running)
                await Task.Delay(50);
            Assert.Equal(DevMind.McpServer.AgentJobState.Done, job.State);
            return job;
        }

        // ── The real continuation path: inherited on the NEXT job ─────────────

        [Fact]
        public async Task Continuation_InheritsNoExecute_FromParent()
        {
            // Every turn ends immediately with task_done — nothing is executed,
            // so nothing can hang.
            using var server = new FakeLlmServer();

            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var parent = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false, noExecute: true);
            Assert.True(parent.NoExecute);

            await WaitForDone(parent);
            // The worker retains the finished session so the job is continuable.
            Assert.NotNull(parent.Session);
            var retainedSession = parent.Session; // capture before transfer nulls it

            var child = mgr.Continue(parent.Id, "continue", 5, 30,
                verifyBuild: false, out string err);
            Assert.Null(err);
            Assert.NotNull(child);
            Assert.Equal(parent.Id, child.ParentJobId);

            // THE assertion: the continuation inherited the restriction.
            Assert.True(child.NoExecute);

            // The parent's session transferred to the child (real continuation).
            Assert.Same(retainedSession, child.Session);
        }

        [Fact]
        public async Task Continuation_KeepsNoExecute_EvenWhenCallerPassesFalse()
        {
            using var server = new FakeLlmServer();

            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var parent = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false, noExecute: true);
            await WaitForDone(parent);

            // Passing noExecute: false must NOT relax the parent's restriction.
            var child = mgr.Continue(parent.Id, "continue", 5, 30,
                verifyBuild: false, out string err, noExecute: false);
            Assert.Null(err);
            Assert.NotNull(child);
            Assert.True(child.NoExecute);
        }

        [Fact]
        public async Task Continuation_WoNoExecuteParent_StaysOff_UntilRequested()
        {
            using var server = new FakeLlmServer();

            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var parent = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false, noExecute: false);
            await WaitForDone(parent);

            // No restriction on parent, caller says nothing -> stays off.
            var child = mgr.Continue(parent.Id, "continue", 5, 30,
                verifyBuild: false, out string err);
            Assert.Null(err);
            Assert.NotNull(child);
            Assert.False(child.NoExecute);

            // A LATER continuation can turn it on explicitly.
            await WaitForDone(child);
            var grand = mgr.Continue(child.Id, "now restrict", 5, 30,
                verifyBuild: false, out string err2, noExecute: true);
            Assert.Null(err2);
            Assert.NotNull(grand);
            Assert.True(grand.NoExecute);
        }
    }
}
