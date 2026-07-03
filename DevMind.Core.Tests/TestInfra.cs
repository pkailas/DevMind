// File: TestInfra.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shared test infrastructure for LlmClient-facing tests:
//   * FakeSseServer — raw-TcpListener HTTP server that records chat POST bodies and
//     replies with a minimal SSE stream (no Windows URL-ACL needed).
//   * FakeLlmOptions — ILlmOptions with manual context size (no probes) and all
//     compaction machinery idle.
//   * PdfTestFiles — runtime-generated minimal PDFs with correct xref tables.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DevMind.Core.Tests
{
    /// <summary>
    /// Minimal single-threaded HTTP server over TcpListener: accepts sequential
    /// requests, records each chat POST body, and replies with a fixed SSE stream
    /// (one content delta + [DONE]). GET probes get an empty JSON list and are not
    /// recorded. TcpListener binds an ephemeral loopback port, so no admin rights
    /// or URL ACLs are needed.
    /// </summary>
    internal sealed class FakeSseServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public List<string> RequestBodies { get; } = new();
        public string BaseUrl { get; }

        /// <summary>Text streamed back for every chat POST (default "ok").</summary>
        public string ResponseText { get; set; } = "ok";

        public FakeSseServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
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
                    var (method, body) = request.Value;

                    // Record only the chat POSTs under test — the client also sends
                    // health/models GET probes, which must not pollute assertions.
                    bool isChatPost = method == "POST" && body.Length > 0;
                    if (isChatPost)
                    {
                        lock (RequestBodies) RequestBodies.Add(body);
                    }

                    byte[] payload;
                    string contentType;
                    if (isChatPost)
                    {
                        string escaped = ResponseText.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        string sse =
                            "data: {\"choices\":[{\"delta\":{\"content\":\"" + escaped + "\"}}]}\n\n" +
                            "data: [DONE]\n\n";
                        payload = Encoding.UTF8.GetBytes(sse);
                        contentType = "text/event-stream";
                    }
                    else
                    {
                        payload = Encoding.UTF8.GetBytes("{\"data\":[]}");
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

        private static async Task<(string method, string body)?> ReadRequestAsync(NetworkStream stream)
        {
            // Read until end of headers.
            var headerBuf = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                int n = await stream.ReadAsync(one.AsMemory(0, 1));
                if (n == 0) return null; // connection closed (e.g. probe)
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
            string method = headerLines[0].Split(' ')[0]; // "POST /v1/chat/completions HTTP/1.1"
            int contentLength = 0;
            foreach (string line in headerLines)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(line.Substring(15).Trim());
            }
            if (contentLength == 0) return (method, string.Empty);

            var bodyBytes = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = await stream.ReadAsync(bodyBytes.AsMemory(read, contentLength - read));
                if (n == 0) break;
                read += n;
            }
            return (method, Encoding.UTF8.GetString(bodyBytes, 0, read));
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _acceptLoop.Wait(2000); } catch { /* accept loop teardown races are fine */ }
            _cts.Dispose();
        }
    }

    /// <summary>Minimal ILlmOptions: manual context size (no probes), all compaction
    /// machinery effectively idle so history stays exactly what the test appends.</summary>
    internal sealed class FakeLlmOptions : ILlmOptions
    {
        public string SystemPrompt => "You are a test assistant.";
        public string ModelName => "test-model";
        public int RequestTimeoutMinutes => 1;
        public int FirstTokenTimeoutMinutes => 1;
        public bool ShowDebugOutput => false;
        public bool ShowContextBudget => false;
        public bool ShowLlmThinking => false;
        public ContextEvictionMode ContextEviction => ContextEvictionMode.Off;
        public int ManualContextSize => 32768;
        public LlmServerType ServerType => LlmServerType.LlamaServer;
        public string CustomContextEndpoint => null!; // Core is nullable-oblivious; null means "no custom endpoint"
        public int MicroCompactThreshold => 99;
        public bool MicroCompactSummarize => false;
        public bool MicroCompactBrainwash => false;
        public bool AlwaysConfirmPatch => false;
        public int AgenticLoopMaxDepth => 25;
        public int AgenticContextLimitPercent => 0;
    }

    /// <summary>Runtime-generated minimal PDFs (no binary fixtures).</summary>
    internal static class PdfTestFiles
    {
        /// <summary>
        /// Writes a minimal PDF (1..N identical pages) with a correct xref table. Object
        /// byte offsets are computed while assembling so the file is strictly valid.
        /// </summary>
        public static string WriteTempPdf(int widthPts, int heightPts, int pages = 1)
        {
            var kids = new StringBuilder();
            for (int i = 0; i < pages; i++)
                kids.Append($"{3 + i} 0 R ");

            var objects = new List<string>
            {
                "1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n",
                $"2 0 obj\n<</Type/Pages/Kids[{kids.ToString().TrimEnd()}]/Count {pages}>>\nendobj\n",
            };
            for (int i = 0; i < pages; i++)
                objects.Add($"{3 + i} 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 {widthPts} {heightPts}]>>\nendobj\n");

            var sb = new StringBuilder();
            sb.Append("%PDF-1.4\n");
            var offsets = new long[objects.Count];
            for (int i = 0; i < objects.Count; i++)
            {
                offsets[i] = Encoding.ASCII.GetByteCount(sb.ToString());
                sb.Append(objects[i]);
            }

            long xrefOffset = Encoding.ASCII.GetByteCount(sb.ToString());
            sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (long off in offsets)
                sb.Append($"{off:D10} 00000 n \n");
            sb.Append($"trailer\n<</Size {objects.Count + 1}/Root 1 0 R>>\nstartxref\n{xrefOffset}\n%%EOF");

            string path = Path.Combine(Path.GetTempPath(), $"devmind_test_{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
            return path;
        }
    }
}
