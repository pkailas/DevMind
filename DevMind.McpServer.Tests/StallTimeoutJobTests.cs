// File: StallTimeoutJobTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-37: delegated jobs are cancelled on a STALL, never on wall-clock time. job-1726 was
// killed at the old 30-minute wall-clock limit at iteration 122/200 while still making
// progress. End-to-end through AgentJobManager against a scripted fake LLM server, on a
// manual clock so "ten minutes" of simulated time costs milliseconds:
//   1. a job that keeps completing iterations past several windows is NOT killed;
//   2. a job that goes silent is killed after the window, and the error says
//      "stalled: no progress for N min (last progress: <what> at <time>)";
//   3. one long model generation that is still streaming is not killed;
//   4. one long shell command that streams output is not killed;
//   5. one long SILENT shell command inside its own timeout is not killed;
//   6. the harness phases (test baseline, build/test verification) are not counted.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    /// <summary>Hand-driven clock for stall tests (thread-safe).</summary>
    internal sealed class StallManualClock : TimeProvider
    {
        private long _utcTicks;
        public StallManualClock(DateTime startUtc) { _utcTicks = startUtc.Ticks; }
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);
        public DateTime UtcNow => new(Interlocked.Read(ref _utcTicks), DateTimeKind.Utc);
    }

    public sealed class StallTimeoutJobTests : IDisposable
    {
        private static readonly DateTime Start = new(2026, 9, 28, 14, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

        private readonly string _dir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;

        public StallTimeoutJobTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_stall_job_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static AgentJobManager NewManager(StallManualClock clock) => new AgentJobManager
        {
            Clock = clock,
            WatchdogPollOverride = TimeSpan.FromMilliseconds(10),
        };

        // ── 1. Progressing past the window ───────────────────────────────────
        [Fact]
        public async Task ProgressingJob_PastSeveralWindows_IsNotKilled()
        {
            var clock = new StallManualClock(Start);
            // A 30-second window (the seam); every model response takes 18 simulated seconds
            // (a slow, shared GPU): nine of them is 162 s — more than five windows.
            using var server = new ScriptedLlmServer(async (post, stream, ct) =>
            {
                clock.Advance(TimeSpan.FromSeconds(18));
                await Task.Delay(30, ct); // real time for the watchdog to poll
                if (post <= 8)
                    await ScriptedLlmServer.WriteSseAsync(stream, ScriptedLlmServer.ToolCallEvent("list_files", "{}", $"call_{post}"));
                else
                    await ScriptedLlmServer.WriteSseAsync(stream, ScriptedLlmServer.ToolCallEvent("task_done", "{\"summary\":\"done\"}", $"call_{post}"));
            });
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = NewManager(clock);
            mgr.StallWindowOverride = TimeSpan.FromSeconds(30);
            var job = mgr.Start("p", _dir, 50, 10, allowCommit: false, verifyBuild: false);
            await WaitForTerminal(job);

            Assert.Null(job.StallReason);
            Assert.Equal(AgentJobState.Done, job.State);
            Assert.Equal(9, job.Result!.Iterations);
            Assert.True(clock.UtcNow - Start >= TimeSpan.FromSeconds(162));
        }

        // ── 2. Silent job is killed with the stall reason ─────────────────────
        [Fact]
        public async Task SilentJob_IsKilledAfterTheWindow_WithStalledReasonNamingLastProgress()
        {
            var clock = new StallManualClock(Start);
            var secondRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new ScriptedLlmServer(async (post, stream, ct) =>
            {
                if (post == 1)
                {
                    await ScriptedLlmServer.WriteSseAsync(stream, ScriptedLlmServer.ToolCallEvent("list_files", "{}", "call_1"));
                    return;
                }
                secondRequest.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // the model server hangs: no bytes, ever
            });
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = NewManager(clock);
            var job = mgr.Start("p", _dir, 50, 10, allowCommit: false, verifyBuild: false);

            await secondRequest.Task.WaitAsync(TimeSpan.FromSeconds(30));
            DateTime lastProgressAt = clock.UtcNow;

            clock.Advance(Window - TimeSpan.FromSeconds(1));
            await Task.Delay(150);
            Assert.Equal(AgentJobState.Running, job.State); // not yet: one second short

            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitForTerminal(job);

            Assert.Equal(AgentJobState.Cancelled, job.State);
            Assert.Equal(
                $"stalled: no progress for 10 min (last progress: iteration 1 completed at {lastProgressAt:yyyy-MM-dd HH:mm:ss} UTC)",
                job.Error);
            Assert.Equal(job.Error, job.StallReason);
        }

        // ── 3. One long, still-streaming generation ──────────────────────────
        [Fact]
        public async Task LongStreamingGeneration_IsNotKilled()
        {
            var clock = new StallManualClock(Start);
            using var server = new ScriptedLlmServer(async (post, stream, ct) =>
            {
                await ScriptedLlmServer.WriteStreamHeadersAsync(stream);
                // 12 chunks, each 5 simulated minutes after the last: one 60-minute response.
                for (int i = 0; i < 12; i++)
                {
                    clock.Advance(TimeSpan.FromMinutes(5));
                    await ScriptedLlmServer.WriteEventAsync(stream, ScriptedLlmServer.ContentEvent($"word{i} "));
                    await Task.Delay(30, ct);
                }
                await ScriptedLlmServer.WriteEventAsync(stream,
                    ScriptedLlmServer.ToolCallEvent("task_done", "{\"summary\":\"done\"}", "call_1"));
                await ScriptedLlmServer.WriteDoneAsync(stream);
            });
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = NewManager(clock);
            var job = mgr.Start("p", _dir, 50, 10, allowCommit: false, verifyBuild: false);
            await WaitForTerminal(job);

            Assert.Null(job.StallReason);
            Assert.Equal(AgentJobState.Done, job.State);
        }

        // ── 4. One long shell command that streams output ────────────────────
        // The command's simulated budget (timeout_seconds 10) is far below the window, so the
        // in-flight exemption expires almost at once: only the streamed lines keep it alive.
        // The clock moves 6 simulated minutes per NEW output line the job has seen.
        [Fact]
        public async Task LongShellCommand_ThatStreamsOutput_IsNotKilled()
        {
            const string command = "ping -n 5 127.0.0.1";
            var clock = new StallManualClock(Start);
            using var server = new ScriptedLlmServer(async (post, stream, ct) =>
            {
                string evt = post == 1
                    ? ScriptedLlmServer.ToolCallEvent("run_shell",
                        JsonSerializer.Serialize(new { command, timeout_seconds = 10 }), "call_1")
                    : ScriptedLlmServer.ToolCallEvent("task_done", "{\"summary\":\"done\"}", $"call_{post}");
                await ScriptedLlmServer.WriteSseAsync(stream, evt);
            });
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = NewManager(clock);
            var job = mgr.Start("p", _dir, 50, 10, allowCommit: false, verifyBuild: false);

            int advances = 0, seenReplies = 0;
            while (job.State is AgentJobState.Queued or AgentJobState.Running)
            {
                int replies = CountOccurrences(job.GetTail(), "Reply from 127.0.0.1");
                if (replies > seenReplies)
                {
                    seenReplies = replies;
                    clock.Advance(TimeSpan.FromMinutes(6));
                    advances++;
                }
                await Task.Delay(10);
            }

            Assert.Null(job.StallReason);
            Assert.Equal(AgentJobState.Done, job.State);
            Assert.True(advances >= 3, $"only {advances} streamed lines drove the clock — the test did not exercise a long call");
        }

        // ── 5. One long SILENT shell command inside its own timeout ──────────
        [Fact]
        public async Task LongSilentShellCommand_InsideItsOwnTimeout_IsNotKilled()
        {
            const string command = "powershell -NoProfile -Command \"Start-Sleep -Seconds 2\"";
            var clock = new StallManualClock(Start);
            using var server = new ScriptedLlmServer(async (post, stream, ct) =>
            {
                string evt = post == 1
                    ? ScriptedLlmServer.ToolCallEvent("run_shell",
                        JsonSerializer.Serialize(new { command, timeout_seconds = 3600 }), "call_1")
                    : ScriptedLlmServer.ToolCallEvent("task_done", "{\"summary\":\"done\"}", $"call_{post}");
                await ScriptedLlmServer.WriteSseAsync(stream, evt);
            });
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = NewManager(clock);
            var job = mgr.Start("p", _dir, 50, 10, allowCommit: false, verifyBuild: false);

            // Once the command is running, 30 simulated minutes pass without a byte from it —
            // three windows, well inside its own one-hour timeout.
            await WaitUntil(() => job.GetTail().Contains("[SHELL] > powershell"));
            clock.Advance(TimeSpan.FromMinutes(30));
            await Task.Delay(200);
            Assert.Null(job.StallReason);

            await WaitForTerminal(job);
            Assert.Null(job.StallReason);
            Assert.Equal(AgentJobState.Done, job.State);
        }

        // ── 6. Harness phases are not the agent's time ───────────────────────
        [Fact]
        public async Task HarnessBaselineAndVerification_AreNotCountedOrKilled()
        {
            var clock = new StallManualClock(Start);
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = NewManager(clock);
            var tokens = new List<CancellationToken>();
            // Each harness phase takes 30 simulated minutes (three windows) with no progress tick.
            mgr.BuildRunnerOverride = async (_, ct) =>
            {
                tokens.Add(ct);
                clock.Advance(TimeSpan.FromMinutes(30));
                await Task.Delay(200);
                return new BuildVerification { Command = "dotnet build", ExitCode = 0, OutputTail = "Build succeeded." };
            };
            mgr.TestRunnerOverride = async (_, ct) =>
            {
                tokens.Add(ct);
                clock.Advance(TimeSpan.FromMinutes(30));
                await Task.Delay(200);
                return TestVerification.FromRun("dotnet test", 0, "Test run for X\n" + CannedTestOutput.McpPass37 + "\n");
            };

            var job = mgr.Start("p", _dir, 50, 10, allowCommit: false, verifyBuild: true, verifyTests: true);
            await WaitForTerminal(job);

            Assert.Null(job.StallReason);
            Assert.Equal(AgentJobState.Done, job.State);
            Assert.NotNull(job.BaselineTests);
            Assert.True(job.Build!.Succeeded);
            Assert.True(job.Tests!.Succeeded);
            Assert.Equal(3, tokens.Count); // baseline, build, after-run tests
            Assert.All(tokens, t => Assert.False(t.IsCancellationRequested));
        }

        private static int CountOccurrences(string text, string needle)
        {
            int n = 0;
            for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 30000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(sw.ElapsedMilliseconds < timeoutMs, "condition not reached in time");
                await Task.Delay(10);
            }
        }

        private static Task WaitForTerminal(AgentJob job) =>
            WaitUntil(() => job.State is not (AgentJobState.Queued or AgentJobState.Running));
    }

    /// <summary>
    /// Fake OpenAI-compatible server whose every POST is answered by a script
    /// (1-based request number, the raw connection stream, a shutdown token). GETs get "{}".
    /// </summary>
    internal sealed class ScriptedLlmServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<int, Stream, CancellationToken, Task> _onPost;
        private int _postCount;

        public string BaseUrl { get; }

        public ScriptedLlmServer(Func<int, Stream, CancellationToken, Task> onPost)
        {
            _onPost = onPost;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
            _ = Task.Run(LoopAsync);
        }

        public static string ToolCallEvent(string name, string argsJson, string id) => JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { delta = new { tool_calls = new[] { new { index = 0, id, type = "function", function = new { name, arguments = argsJson } } } } }
            }
        });

        public static string ContentEvent(string text) => JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = text } } }
        });

        /// <summary>A whole SSE response (one event + [DONE]) with a Content-Length.</summary>
        public static async Task WriteSseAsync(Stream stream, string evt)
        {
            byte[] payload = Encoding.UTF8.GetBytes($"data: {evt}\n\ndata: [DONE]\n\n");
            string headers = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" +
                             $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }

        /// <summary>Headers for a streamed response: no length, the body ends when the connection closes.</summary>
        public static async Task WriteStreamHeadersAsync(Stream stream)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"));
            await stream.FlushAsync();
        }

        public static async Task WriteEventAsync(Stream stream, string evt)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: {evt}\n\n"));
            await stream.FlushAsync();
        }

        public static async Task WriteDoneAsync(Stream stream)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
            await stream.FlushAsync();
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }

                _ = Task.Run(() => HandleAsync(tcp)); // a hung request must not block the next
            }
        }

        private async Task HandleAsync(TcpClient tcp)
        {
            using (tcp)
            using (var stream = tcp.GetStream())
            {
                try
                {
                    var (method, body) = await ReadRequestAsync(stream);
                    if (method == "POST" && body.Length > 0)
                    {
                        await _onPost(Interlocked.Increment(ref _postCount), stream, _cts.Token);
                        return;
                    }
                    byte[] payload = Encoding.UTF8.GetBytes("{}");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                        $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(payload);
                }
                catch (Exception) { /* client went away or server shut down */ }
            }
        }

        private static async Task<(string method, string body)> ReadRequestAsync(Stream stream)
        {
            var header = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one.AsMemory(0, 1)) == 0) return ("", "");
                header.WriteByte(one[0]);
                long len = header.Length;
                var b = header.GetBuffer();
                if (len >= 4 && b[len - 4] == '\r' && b[len - 3] == '\n' && b[len - 2] == '\r' && b[len - 1] == '\n')
                    break;
            }

            string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            string method = lines[0].Split(' ')[0];
            int contentLength = 0;
            foreach (string line in lines)
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(line.Substring(15).Trim());

            var body = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = await stream.ReadAsync(body.AsMemory(read, contentLength - read));
                if (n == 0) break;
                read += n;
            }
            return (method, Encoding.UTF8.GetString(body, 0, read));
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* best effort */ }
        }
    }
}
