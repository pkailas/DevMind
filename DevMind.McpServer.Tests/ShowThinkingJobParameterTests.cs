// File: ShowThinkingJobParameterTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the per-job show_thinking parameter — think-token visibility in a
// job's transcript, previously controlled ONLY by the DEVMIND_TASK_SHOW_THINKING
// environment variable (invisible process-inheritance chain, global, static,
// fails silently when wrong).
//
// Behaviours proven here, each against a REAL AgentJobManager + a stub LLM
// server that emits think tokens:
//   * show_thinking: true streams the think blocks into the transcript
//     (the parameter reaches the session; not the env var — the env var is
//     explicitly OFF in that test).
//   * show_thinking omitted falls back to the environment variable
//     (env set → stream; env unset → filtered, default behaviour).
//   * An explicit show_thinking: false wins over the environment variable
//     (the parameter beats the env var, both directions).
//   * Continuations inherit the parent's setting — explicit true, explicit
//     false, and the omitted/null "env fallback" state (proven end-to-end:
//     an inherited omission still honours the env var on turn N).
//   * GRAY 1: show_thinking: true implies think: true at the tool boundary —
//     an ask to display reasoning that would never be generated is a silent
//     no-op, so it turns generation on instead (stated in the tool
//     description).

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.McpServer.Tests
{
    /// <summary>Stub LLM server whose chat POST answers with ONE SSE stream that
    /// carries a &lt;think&gt;…&lt;/think&gt; block followed by a task_done
    /// tool call — the minimum shape for exercising the transcript's thinking
    /// visibility without spawning any process. Think tokens travel on
    /// delta.content (LlmClient feeds content deltas to ThinkFilter as-is;
    /// the &lt;think&gt;/&lt;/think&gt; tags are the transport ThinkFilter expects).
    ///
    /// With visibility ON the turn's transcript gains a
    /// "[THINKING] …" line; with it OFF the block is filtered and the same
    /// turn completes identically (visible answer "ok") — that asymmetry is
    /// what the tests assert.</summary>
    internal sealed class ThinkTokenLlmServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public string BaseUrl { get; }

        public ThinkTokenLlmServer()
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

        private static string BuildThinkThenDoneSse()
        {
            // The think tags are built from unicode escapes so nothing in the file
            // write path can mistake them for markup and swallow them — that is
            // exactly how this stub regressed (opener stripped, closers left, and
            // the payload the tests assert on never sent). The wire format is
            // byte-identical: less-than + "think" + greater-than; the closer adds a
            // slash after the less-than.
            const string OPENER = "\u003Cthink\u003E";
            const string CLOSER = "\u003C/think\u003E";
            return
                "data: {\"choices\":[{\"delta\":{\"content\":\"" + OPENER + "thinking hard here\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"" + CLOSER + "ok\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0," +
                    "\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"task_done\"," +
                    "\"arguments\":\"{\\\"summary\\\":\\\"done\\\"}\"}}]}}]}\n\n" +
                "data: [DONE]\n\n";
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
                        // POST /v1/chat/completions → SSE: think block, then task_done.
                        payload = Encoding.UTF8.GetBytes(BuildThinkThenDoneSse());
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

        // Reads headers byte-by-byte until blank line; reads exactly Content-Length
        // bytes of body (avoids ReadToEnd hanging on keep-alive). Same pattern as
        // NoExecuteInheritanceTests.FakeLlmServer.
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

    public sealed class ShowThinkingJobParameterTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;
        private readonly string? _priorShowThinking;

        public ShowThinkingJobParameterTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_showthinking_job_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            _priorShowThinking = Environment.GetEnvironmentVariable("DEVMIND_TASK_SHOW_THINKING");
            // Force the llama request template (the env var the Core tests set for
            // stub-server-driven turns) so the local server speaks the same shape.
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            // Pin the env var OFF up front — tests that want the fallback set it
            // explicitly, so a leaked machine-level setting cannot mask a failure.
            Environment.SetEnvironmentVariable("DEVMIND_TASK_SHOW_THINKING", null);
        }

        public void Dispose()
        {
            RestoreEnv("DEVMIND_ENDPOINT", _priorEndpoint);
            RestoreEnv("DEVMIND_SERVER_TYPE", _priorServerType);
            RestoreEnv("DEVMIND_TASK_SHOW_THINKING", _priorShowThinking);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static void RestoreEnv(string name, string? prior)
        {
            if (prior == null)
                Environment.SetEnvironmentVariable(name, null);
            else
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

        // ── show_thinking: true → visible reasoning in the transcript ────────

        [Fact]
        public async Task ShowThinkingTrue_StreamsThinkBlocksIntoTranscript()
        {
            // Env var is OFF (pinned in the constructor) — what appears below is
            // the PARAMETER's doing, not the legacy env chain's.
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var job = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false,
                think: true, showThinking: true);
            Assert.True(job.ShowThinking);

            await WaitForDone(job);
            Assert.Null(job.Error);

            // THE assertion: the think block reached the job's transcript.
            string tail = job.GetTail();
            Assert.Contains("[THINKING]", tail);
            Assert.Contains("thinking hard here", tail);

            // Visibility is display-only — the turn's outcome is identical with
            // think streaming on or off. Answer is the task_done summary
            // (HeadlessAgent.cs:431-438); the visible prose "ok" lands in the
            // transcript; think content never leaks into the answer.
            Assert.Equal("done", job.Result!.Answer); // the task_done summary
            Assert.DoesNotContain("thinking hard here", job.Result!.Answer); // no think leak
            Assert.Contains("ok", job.GetTail()); // visible prose reached the transcript
        }

        // ── omitted → the env var applies (legacy fallback preserved) ────────

        [Fact]
        public async Task ShowThinkingOmitted_FallsBackToEnvironmentVariable()
        {
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            Environment.SetEnvironmentVariable("DEVMIND_TASK_SHOW_THINKING", "1");
            using var mgr = new DevMind.McpServer.AgentJobManager();

            // show_thinking omitted (null) → the session keeps the env fallback.
            var job = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false, think: true);
            Assert.Null(job.ShowThinking);

            await WaitForDone(job);
            Assert.Null(job.Error);
            Assert.Contains("[THINKING]", job.GetTail());
        }

        [Fact]
        public async Task ShowThinkingOmitted_WithEnvironmentUnset_FiltersThinkBlocks()
        {
            // Current default behaviour: env unset, parameter omitted → filtered.
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var job = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false, think: true);
            Assert.Null(job.ShowThinking);

            await WaitForDone(job);
            Assert.Null(job.Error);
            Assert.DoesNotContain("[THINKING]", job.GetTail());

            // Filtered, not lost — the turn still completes. Answer is the
            // task_done summary (HeadlessAgent.cs:431-438); the visible prose
            // "ok" lands in the transcript; think content never leaks into the
            // answer even when filtered out of the display.
            Assert.Equal("done", job.Result!.Answer); // the task_done summary
            Assert.DoesNotContain("thinking hard here", job.Result!.Answer);
            Assert.Contains("ok", job.GetTail()); // visible prose reached the transcript
        }

        // ── explicit value wins over the environment variable ────────────────

        [Fact]
        public async Task ShowThinkingFalse_WinsOverEnvironmentVariable()
        {
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            // Env says SHOW — the explicit false must beat it.
            Environment.SetEnvironmentVariable("DEVMIND_TASK_SHOW_THINKING", "1");
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var job = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false,
                think: true, showThinking: false);

            await WaitForDone(job);
            Assert.Null(job.Error);
            Assert.DoesNotContain("[THINKING]", job.GetTail());
            // Explicit false wins over the env var (proven above) AND the turn's
            // outcome is unchanged: Answer is the task_done summary
            // (HeadlessAgent.cs:431-438), the visible prose "ok" lands in the
            // transcript, and think content never leaks into the answer.
            Assert.Equal("done", job.Result!.Answer); // the task_done summary
            Assert.DoesNotContain("thinking hard here", job.Result!.Answer);
            Assert.Contains("ok", job.GetTail()); // visible prose reached the transcript
        }

        // ── continuations inherit the parent's setting ───────────────────────

        [Fact]
        public async Task Continuation_InheritsExplicitShowThinking_FromParent()
        {
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();

            // Explicit true inherits.
            var p1 = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false,
                think: true, showThinking: true);
            await WaitForDone(p1);
            var c1 = mgr.Continue(p1.Id, "continue", 5, 30,
                verifyBuild: false, out string err1);
            Assert.Null(err1);
            Assert.NotNull(c1);
            Assert.True(c1.ShowThinking);
            await WaitForDone(c1);

            // An explicit override on the continuation beats the inherited value
            // (a display preference flips either way — unlike noExecute's ratchet).
            var c2 = mgr.Continue(c1.Id, "now stop showing", 5, 30,
                verifyBuild: false, out string err2, showThinking: false);
            Assert.Null(err2);
            Assert.NotNull(c2);
            Assert.False(c2.ShowThinking);
            await WaitForDone(c2);

            // Explicit false also inherits (fresh chain).
            var p2 = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false,
                think: true, showThinking: false);
            await WaitForDone(p2);
            var c3 = mgr.Continue(p2.Id, "continue", 5, 30,
                verifyBuild: false, out string err3);
            Assert.Null(err3);
            Assert.NotNull(c3);
            Assert.False(c3.ShowThinking);
            await WaitForDone(c3);
        }

        [Fact]
        public async Task Continuation_InheritsOmittedShowThinking_AsEnvFallback()
        {
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            // Env says SHOW for the WHOLE chain — both turns must honour it,
            // which is only possible if the omission (null) survives the
            // continuation instead of degrading to an implicit false.
            Environment.SetEnvironmentVariable("DEVMIND_TASK_SHOW_THINKING", "1");
            using var mgr = new DevMind.McpServer.AgentJobManager();

            var parent = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false, think: true);
            Assert.Null(parent.ShowThinking);
            await WaitForDone(parent);
            Assert.Contains("[THINKING]", parent.GetTail()); // env applied on turn 1

            var child = mgr.Continue(parent.Id, "continue", 5, 30,
                verifyBuild: false, out string err);
            Assert.Null(err);
            Assert.NotNull(child);
            Assert.Null(child.ShowThinking); // the omission inherited, not a false

            await WaitForDone(child);
            Assert.Null(child.Error);
            // THE assertion: the env fallback is still in effect on turn 2.
            Assert.Contains("[THINKING]", child.GetTail());
        }

        // ── GRAY 1: show_thinking implies think at the tool boundary ─────────

        [Fact]
        public async Task TaskStart_ShowThinkingTrue_ImpliesThink()
        {
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new DevMind.McpServer.AgentJobManager();
            var tools = new DevMind.McpServer.AgentTaskTools(mgr);

            // think omitted, show_thinking: true → the job must carry BOTH.
            string json = await tools.TaskStart(
                "p", _dir,
                think: null, show_thinking: true);
            Assert.Contains("job_id", json); // not an Err(...) response

            var job = mgr.List().First();
            Assert.True(job.ShowThinking);
            Assert.True(job.Think); // implied — reasoning that is never generated
                                    // would make the display ask a silent no-op

            await WaitForDone(job);
            Assert.Null(job.Error);
            // And it actually works: generation was turned on AND displayed.
            Assert.Contains("[THINKING]", job.GetTail());
        }
    }
}
