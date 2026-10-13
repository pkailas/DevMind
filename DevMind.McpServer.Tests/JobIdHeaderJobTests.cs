// File: JobIdHeaderJobTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The job-level half of the X-DevMind-Job-Id feature. DevMind.Core.Tests\JobIdHeaderTests.cs
// pins the header at the LlmClient level (every request path, header absent when the id is
// null); this pins the wiring the job runner is responsible for:
//   * a headless job's own chat POSTs name THAT job's id, and
//   * a devmind_task_continue names its OWN new id — not the parent's, which is the
//     failure mode a reused session invites (the session outlives its job).
//
// Real AgentJobManager, real HeadlessSession, real LlmClient over a real socket — the
// assertion is on the request header the backend actually receives, which is the only
// thing Strata can log.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace DevMind.McpServer.Tests
{
    /// <summary>Fake LLM server for the job-level header tests: GET probes get {}, a chat
    /// POST gets an SSE task_done stream, and every chat POST records its request headers so
    /// a test can read X-DevMind-Job-Id off the wire. Same read pattern as
    /// NoExecuteInheritanceTests.FakeLlmServer (Content-Length body, Connection: close).</summary>
    internal sealed class JobIdCapturingLlmServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public string BaseUrl { get; }

        /// <summary>Request headers of every chat POST, in order (lock it to read it).</summary>
        public List<string[]> ChatHeaders { get; } = new();

        public JobIdCapturingLlmServer()
        {
            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        /// <summary>The value of one header on a recorded chat POST, or null when absent.</summary>
        public string? HeaderValue(int postIndex, string name)
        {
            string[] lines;
            lock (ChatHeaders)
            {
                if (postIndex < 0 || postIndex >= ChatHeaders.Count) return null;
                lines = ChatHeaders[postIndex];
            }
            foreach (string line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (string.Equals(line.Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(colon + 1).Trim();
            }
            return null;
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
                    var (method, path, body, headers) = request.Value;

                    byte[] payload;
                    string contentType;
                    if (method == "POST" && !string.IsNullOrEmpty(body))
                    {
                        lock (ChatHeaders) ChatHeaders.Add(headers);
                        // POST /v1/chat/completions → SSE task_done stream: the turn ends
                        // immediately, so no process is ever spawned by these jobs.
                        string sse = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,"
                            + "\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"task_done\","
                            + "\"arguments\":\"{\\\"summary\\\":\\\"done\\\"}\"}}]}}]}\n\n"
                            + "data: [DONE]\n\n";
                        payload = Encoding.UTF8.GetBytes(sse);
                        contentType = "text/event-stream";
                    }
                    else
                    {
                        payload = Encoding.UTF8.GetBytes("{}");
                        contentType = "application/json";
                    }

                    string responseHeaders =
                        "HTTP/1.1 200 OK\r\n" +
                        $"Content-Type: {contentType}\r\n" +
                        $"Content-Length: {payload.Length}\r\n" +
                        "Connection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(responseHeaders));
                    await stream.WriteAsync(payload);
                    await stream.FlushAsync();
                }
            }
        }

        private static async Task<(string method, string path, string body, string[] headers)?> ReadRequestAsync(NetworkStream stream)
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

            return (method, path, body, headerLines);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }

    public sealed class JobIdHeaderJobTests : IDisposable
    {
        private const string Header = "X-DevMind-Job-Id";

        private readonly string _dir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;

        public JobIdHeaderJobTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_jobid_header_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
        }

        public void Dispose()
        {
            RestoreEnv("DEVMIND_ENDPOINT", _priorEndpoint);
            RestoreEnv("DEVMIND_SERVER_TYPE", _priorServerType);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static void RestoreEnv(string name, string? prior)
        {
            Environment.SetEnvironmentVariable(name, prior);
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

        // ── A job's requests name that job ───────────────────────────────────

        [Fact]
        public async Task AJob_CarriesItsOwnJobId_OnEveryChatRequest()
        {
            using var server = new JobIdCapturingLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var job = mgr.Start("do the thing", _dir, 5, 30,
                allowCommit: false, verifyBuild: false);

            await WaitForDone(job);
            Assert.Null(job.Error);

            Assert.True(server.ChatHeaders.Count >= 1, "the job sent no chat POST");
            for (int i = 0; i < server.ChatHeaders.Count; i++)
                Assert.Equal(job.Id, server.HeaderValue(i, Header));
        }

        // ── A continuation names ITS OWN id, not the parent's ────────────────

        [Fact]
        public async Task AContinuation_CarriesItsOwnJobId_NotTheParentS()
        {
            using var server = new JobIdCapturingLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var parent = mgr.Start("do the thing", _dir, 5, 30,
                allowCommit: false, verifyBuild: false);
            await WaitForDone(parent);
            int parentPosts = server.ChatHeaders.Count;
            Assert.Equal(parent.Id, server.HeaderValue(parentPosts - 1, Header));

            var child = mgr.Continue(parent.Id, "continue", 5, 30,
                verifyBuild: false, out string err);
            Assert.Null(err);
            Assert.NotNull(child);
            Assert.NotEqual(parent.Id, child.Id);

            await WaitForDone(child);
            Assert.Null(child.Error);

            // The continued turn reuses the parent's SESSION (same LlmClient), so without the
            // job-id re-sync every request below would still name the parent.
            int childPosts = server.ChatHeaders.Count;
            Assert.True(childPosts > parentPosts, "the continuation sent no chat POST");
            for (int i = parentPosts; i < childPosts; i++)
                Assert.Equal(child.Id, server.HeaderValue(i, Header));
        }
    }
}
