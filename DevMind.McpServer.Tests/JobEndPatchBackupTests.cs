// File: JobEndPatchBackupTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// These tests pin the TRIGGER for the PATCH-backup drain, not the mechanism.
//
// The drain itself was already covered: call DrainPatchBackups(), or dispose a
// HeadlessSession, and the backup files go away. What no test asserted was that
// anything actually fires at the end of a delegated job — and nothing did. A
// finished job deliberately KEEPS its HeadlessSession so devmind_task_continue can
// resume the conversation, so disposal happens only on idle expiry (which is
// piggybacked on a later job's creation), on eviction past the retention cap, or on
// a clean shutdown. Run one job and walk away and the backups simply stayed; kill
// the server and they were orphaned for good. Thousands accumulated in %TEMP%\DevMind
// that way.
//
// So the test below runs a REAL job through the REAL AgentJobManager against a stub
// LLM that issues one patch, and asserts on the backup file on disk once the job
// reports Done — while the session is still alive and continuable.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class JobEndPatchBackupTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;

        public JobEndPatchBackupTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_jobend_bak_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
        }

        public void Dispose()
        {
            if (_priorEndpoint == null) Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", null);
            else Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            if (_priorServerType == null) Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", null);
            else Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>The flat folder PatchEngine writes every backup into. Shared with
        /// live sessions, so these tests only ever look at backups of their own
        /// uniquely-named target file and never sweep or clear it.</summary>
        private static string PatchBackupDir => Path.Combine(Path.GetTempPath(), "DevMind");

        private static string[] BackupsOf(string targetFileName)
            => Directory.Exists(PatchBackupDir)
                ? Directory.GetFiles(PatchBackupDir, $"{targetFileName}.*.bak")
                : Array.Empty<string>();

        // ── The trigger ──────────────────────────────────────────────────────
        // One job, one patch, then Done. Finishing the job must have removed the
        // backup the patch created, and must NOT have ended the conversation.
        [Fact]
        public async Task FinishedJob_DrainsPatchBackups_AndKeepsSessionContinuable()
        {
            // A unique target file name so the backups found on disk can only be ours.
            string targetName = $"jobend_{Guid.NewGuid():N}.txt";
            string target = Path.Combine(_dir, targetName);
            File.WriteAllText(target, "alpha\nbravo\ncharlie\n");

            Assert.Empty(BackupsOf(targetName)); // nothing of ours there to begin with

            using var server = new PatchThenDoneLlmServer(target, find: "bravo", replace: "delta");
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = new AgentJobManager();
            var job = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false, verifyTests: false, runTestBaseline: false);

            await WaitForTerminal(job);
            Assert.Equal(AgentJobState.Done, job.State);

            // The patch really landed — otherwise no backup was ever created and the
            // assertion below would pass for the wrong reason.
            Assert.Contains("delta", File.ReadAllText(target));
            Assert.Contains(job.Result!.Actions, a => a.Kind == "patch");

            // The trigger: the job ending took the backup with it.
            string[] leftovers = BackupsOf(targetName);
            Assert.True(leftovers.Length == 0,
                $"the finished job left {leftovers.Length} PATCH backup(s) orphaned in {PatchBackupDir}: "
                + string.Join(", ", leftovers.Select(Path.GetFileName)));

            // …and the conversation survived it. Dropping the backups must not be a
            // disguised disposal: a continuation needs this session.
            Assert.NotNull(job.Session);
            var continuation = mgr.Continue(job.Id, "keep going", 5, 30,
                verifyBuild: false, error: out string error,
                verifyTests: false, runTestBaseline: false);
            Assert.Null(error);
            Assert.NotNull(continuation);

            await WaitForTerminal(continuation!);
        }

        // ── The sweep's liveness guard ───────────────────────────────────────
        // The startup sweep skips itself entirely while another agent is mid-job,
        // because that agent may own live backups in the same flat folder. The signal
        // is the active-job marker's pid.

        [Fact]
        public void IsJobActiveElsewhere_NoMarker_IsFalse()
        {
            ClearMarker();
            Assert.False(AgentJobManager.IsJobActiveElsewhere());
        }

        [Fact]
        public void IsJobActiveElsewhere_MarkerForThisProcess_IsFalse()
        {
            // Our own marker is not "somebody else" — a server must not be blocked
            // from cleaning up by a marker it wrote itself.
            WriteMarker(Environment.ProcessId);
            try { Assert.False(AgentJobManager.IsJobActiveElsewhere()); }
            finally { ClearMarker(); }
        }

        [Fact]
        public void IsJobActiveElsewhere_MarkerForDeadProcess_IsFalse()
        {
            // The leftover a force-kill leaves behind. If a stale marker counted as
            // live, the sweep would never run again on this machine.
            var doomed = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
            { CreateNoWindow = true, UseShellExecute = false })!;
            doomed.WaitForExit();
            int deadPid = doomed.Id;
            doomed.Dispose();

            WriteMarker(deadPid);
            try { Assert.False(AgentJobManager.IsJobActiveElsewhere()); }
            finally { ClearMarker(); }
        }

        [Fact]
        public void IsJobActiveElsewhere_MarkerForLiveProcess_IsTrue()
        {
            var other = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true })!;
            try
            {
                WriteMarker(other.Id);
                Assert.True(AgentJobManager.IsJobActiveElsewhere());
            }
            finally
            {
                ClearMarker();
                try { other.Kill(entireProcessTree: true); } catch { /* already gone */ }
                other.Dispose();
            }
        }

        [Fact]
        public void IsJobActiveElsewhere_MalformedMarker_IsFalse()
        {
            Directory.CreateDirectory(AgentJobManager.TranscriptDir);
            File.WriteAllText(MarkerPath, "not json at all");
            try { Assert.False(AgentJobManager.IsJobActiveElsewhere()); }
            finally { ClearMarker(); }
        }

        private static string MarkerPath => Path.Combine(AgentJobManager.TranscriptDir, "_active.json");

        private static void WriteMarker(int pid)
        {
            Directory.CreateDirectory(AgentJobManager.TranscriptDir);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(new
            {
                job_id = "job-test",
                state = "running",
                pid,
            }));
        }

        private static void ClearMarker()
        {
            try { File.Delete(MarkerPath); } catch { /* best effort */ }
        }

        private static async Task WaitForTerminal(AgentJob job, int timeoutMs = 60000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs
                   && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(20);
            Assert.False(job.State is AgentJobState.Queued or AgentJobState.Running,
                $"job {job.Id} never reached a terminal state within {timeoutMs}ms");
        }
    }

    /// <summary>Stub LLM server for the backup-drain trigger: POST #1 answers with a
    /// patch_file tool call (a patch is what creates a backup file — create_file does
    /// not), every later POST answers with task_done. Same SSE/Content-Length mechanics
    /// as the other stub servers in this assembly, which are sealed and answer with
    /// their own fixed tool calls.</summary>
    internal sealed class PatchThenDoneLlmServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _filePath;
        private readonly string _find;
        private readonly string _replace;
        private int _postCount;

        public string BaseUrl { get; }

        public PatchThenDoneLlmServer(string filePath, string find, string replace)
        {
            _filePath = filePath;
            _find = find;
            _replace = replace;
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

        private static string ToolCallSse(string name, string argsJson, string id)
        {
            string delta = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new { index = 0, id, type = "function", function = new { name, arguments = argsJson } }
                            }
                        }
                    }
                }
            });
            return $"data: {delta}\n\ndata: [DONE]\n\n";
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
                    var (method, _, body) = request.Value;

                    byte[] payload;
                    string contentType;
                    if (method == "POST" && !string.IsNullOrEmpty(body))
                    {
                        int post = Interlocked.Increment(ref _postCount);
                        string sse = post == 1
                            ? ToolCallSse("patch_file",
                                JsonSerializer.Serialize(new
                                {
                                    filename = _filePath,
                                    find = _find,
                                    replace = _replace,
                                }),
                                "call_1")
                            : ToolCallSse("task_done",
                                JsonSerializer.Serialize(new { summary = "done" }),
                                $"call_{post}");
                        payload = Encoding.UTF8.GetBytes(sse);
                        contentType = "text/event-stream";
                    }
                    else
                    {
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

        // Byte-by-byte header read + exact Content-Length body read (a ReadToEnd
        // hangs on keep-alive).
        private static async Task<(string method, string path, string body)?> ReadRequestAsync(Stream stream)
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
            try { _listener.Stop(); } catch { /* best effort */ }
        }
    }
}
