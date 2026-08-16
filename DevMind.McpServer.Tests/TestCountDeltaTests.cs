// File: TestCountDeltaTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the STRUCTURAL test-count delta on devmind_task_* results. The
// harness derives totals from the `dotnet test` output it captured itself, so
// a number in the result is no longer just what the agent claimed (the "391 as
// a 391-passing" misreport that motivated this; standing rule 4).
//
// Three layers:
//   * TestSummaryParseTests — the parser: realistic line parses, a failing
//     run still yields its total (a failing run's counts are still a fact
//     about the suite), garbage/missing/ambiguous output yields null + a
//     stated reason — never a guess, never an exception.
//   * TestVerificationPayloadTests — the delta logic: before+after -> delta;
//     before missing -> total with no delta and the reason stated; removed
//     tests reported, never an incomplete_reason.
//   * TestBaselineInvocationTests — end-to-end against a fake LLM server and
//     the TestRunnerOverride seam: test_baseline "before-run" produces EXACTLY
//     two suite invocations (baseline + after); "off" produces EXACTLY one —
//     asserted by invocation COUNT, not by timing.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    // ── The real .NET 10 summary line on this machine (captured from an
    // actual run — not guessed). Column widths vary; the field names and
    // their order are the contract. ────────────────────────────────────────
    internal static class CannedTestOutput
    {
        public const string McpPass37 =
            "Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 4 s - DevMind.McpServer.Tests.dll (net10.0)";

        public const string McpFail2 =
            "Failed!  - Failed:     2, Passed:    35, Skipped:     0, Total:    37, Duration: 6 s - DevMind.McpServer.Tests.dll (net10.0)";

        /// <summary>A realistic run tail: progress noise + the summary line.</summary>
        public static string RealisticTailPass37 =>
            "  Determining projects to restore...\n" +
            "  Restored DevMind.McpServer.Tests (in 1.2s).\n" +
            "  DevMind.McpServer -> bin\\Debug\\net10.0\\DevMind.McpServer.dll\n" +
            "  DevMind.McpServer.Tests -> bin\\Debug\\net10.0\\DevMind.McpServer.Tests.dll\n" +
            "Test run for DevMind.McpServer.Tests (net10.0)\n" +
            "A total of 1 test files matched the specified pattern.\n" +
            McpPass37 + "\n";
    }

    public sealed class TestSummaryParseTests
    {
        [Fact]
        public void RealisticPassedTail_ParsesTheTotal()
        {
            var (total, why) = TestSummaryParse.Parse(CannedTestOutput.RealisticTailPass37);
            Assert.Equal(37, total);
            Assert.Null(why);
        }

        [Fact]
        public void BareSummaryLine_Parses()
        {
            var (total, why) = TestSummaryParse.Parse(CannedTestOutput.McpPass37);
            Assert.Equal(37, total);
            Assert.Null(why);
        }

        [Fact]
        public void DifferentColumnWidths_Parse()
        {
            // Wider counts (Core's 479) — whitespace tolerance, not a fixed width.
            const string line =
                "Passed!  - Failed:      0, Passed:   479, Skipped:      0, Total:   479, Duration: 42 s - DevMind.Core.Tests.dll (net10.0)";
            var (total, why) = TestSummaryParse.Parse("Test run for DevMind.Core.Tests (net10.0)\n" + line + "\n");
            Assert.Equal(479, total);
            Assert.Null(why);
        }

        [Fact]
        public void MultiProjectRun_SumsTheProjectTotals()
        {
            string output =
                "Test run for DevMind.Core.Tests (net10.0)\n" +
                "  DevMind.Core.Tests -> bin\\Debug\\net10.0\\DevMind.Core.Tests.dll\n" +
                "Passed!  - Failed:     0, Passed:   479, Skipped:     0, Total:   479, Duration: 42 s - DevMind.Core.Tests.dll (net10.0)\n" +
                "Test run for DevMind.McpServer.Tests (net10.0)\n" +
                CannedTestOutput.McpPass37 + "\n";
            var (total, why) = TestSummaryParse.Parse(output);
            Assert.Equal(479 + 37, total);
            Assert.Null(why);
        }

        [Fact]
        public void FailingRun_StillYieldsItsTotal()
        {
            // Design decision, asserted here: a FAILING after-run still reports its
            // total — the suite's size is a fact even when tests are red, and the
            // run's redness is already surfaced by `succeeded`/`exit_code` on the
            // same payload. (The baseline is stricter: a failed BASELINE run yields
            // null — see TestVerificationPayloadTests.)
            string output = "Test run for DevMind.McpServer.Tests (net10.0)\n"
                + CannedTestOutput.McpFail2 + "\n";
            var (total, why) = TestSummaryParse.Parse(output);
            Assert.Equal(37, total);
            Assert.Null(why);
        }

        [Fact]
        public void GarbageOutput_YieldsNullNotAGuessAndDoesNotThrow()
        {
            string garbage = "### \u0000\u0001 random bytes \u001b[31mFailed: notanumber, Passed: xx"
                + Environment.NewLine + "totally not a test run" + Environment.NewLine
                + "Total: (see above)";
            var (total, why) = TestSummaryParse.Parse(garbage);
            Assert.Null(total);
            Assert.False(string.IsNullOrWhiteSpace(why));
        }

        [Fact]
        public void BuildErrorOnlyOutput_NoSummaryLine_YieldsNullWithReason()
        {
            string output =
                "  Determining projects to restore...\n" +
                "  DevMind.Core -> bin\\Debug\\net10.0\\DevMind.Core.dll\n" +
                "C:\\repo\\Foo.cs(12,9): error CS1002: ; expected\n" +
                "Build FAILED.\n";
            var (total, why) = TestSummaryParse.Parse(output);
            Assert.Null(total);
            Assert.Equal("no test summary line found in output", why);
        }

        [Fact]
        public void EmptyOrWhitespaceOutput_YieldsNullWithReason()
        {
            var (t1, w1) = TestSummaryParse.Parse("");
            Assert.Null(t1);
            Assert.False(string.IsNullOrWhiteSpace(w1));
            var (t2, w2) = TestSummaryParse.Parse("   \n  \n");
            Assert.Null(t2);
            Assert.False(string.IsNullOrWhiteSpace(w2));
        }

        [Fact]
        public void SameProjectTwice_Ambiguous_YieldsNullNotAGuess()
        {
            // Two summary lines for one project (e.g. a retried run, or truncated
            // interleaved output): the harness cannot tell which is authoritative —
            // null, with the ambiguity stated.
            string output =
                CannedTestOutput.McpPass37 + "\n" +
                "Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 5 s - DevMind.McpServer.Tests.dll (net10.0)\n";
            var (total, why) = TestSummaryParse.Parse(output);
            Assert.Null(total);
            Assert.Contains("ambiguous output", why);
            Assert.Contains("DevMind.McpServer.Tests.dll", why);
        }

        [Fact]
        public void InternallyInconsistentCounts_YieldsNullNotAGuess()
        {
            // Failed+Passed+Skipped != Total on a line that otherwise LOOKS like a
            // summary: this is not the line we think it is — do not report it.
            const string line =
                "Passed!  - Failed:     1, Passed:    1, Skipped:     0, Total:    5, Duration: 1 s - Something.dll (net10.0)";
            var (total, why) = TestSummaryParse.Parse(line);
            Assert.Null(total);
            Assert.Contains("inconsistent", why);
        }
    }

    // ── Payload: the delta logic and the exact caller-visible note ─────────
    public sealed class TestVerificationPayloadTests
    {
        private static AgentJob NewJob(TestVerification? tests, TestVerification? baseline,
            AgentJobState state = AgentJobState.Done) => new()
        {
            Id = "job-test",
            Prompt = "p",
            WorkingDirectory = @"C:\temp\hermetic",
            State = state,
            Tests = tests,
            BaselineTests = baseline,
        };

        private static TestVerification Run(int? total, int exitCode = 0, string? parseFailure = null) => new()
        {
            Raw = new BuildVerification { Command = "dotnet test", ExitCode = exitCode, OutputTail = "..." },
            Total = total,
            ParseFailure = parseFailure,
        };

        private static JsonElement Payload(AgentJob job)
            => JsonSerializer.SerializeToElement(TestVerificationPayload.Create(job)!);

        [Fact]
        public void BeforeAndAfter_DeltaReported()
        {
            var job = NewJob(Run(479), Run(466));
            var p = Payload(job);
            Assert.Equal(479, p.GetProperty("total").GetInt32());
            Assert.Equal(466, p.GetProperty("baseline_total").GetInt32());
            Assert.Equal(13, p.GetProperty("delta").GetInt32());
            Assert.Equal(0, p.GetProperty("tests_removed").GetInt32());
            Assert.Null(p.GetProperty("total_unavailable_reason").GetString());
            Assert.Null(p.GetProperty("baseline_unavailable_reason").GetString());
            Assert.Equal(
                "harness-measured test counts: 466 before this task, 479 after (delta +13)",
                p.GetProperty("note").GetString());
        }

        [Fact]
        public void FlatRun_DeltaZero()
        {
            var job = NewJob(Run(37), Run(37));
            var p = Payload(job);
            Assert.Equal(0, p.GetProperty("delta").GetInt32());
            Assert.Equal(
                "harness-measured test counts: 37 before this task, 37 after (delta 0)",
                p.GetProperty("note").GetString());
        }

        [Fact]
        public void RemovedTests_FieldReportedNotIncompleteReason()
        {
            // Deleting a test can be legitimate: the FIELD is emitted, but it must
            // NOT flip the job to incomplete.
            var job = NewJob(Run(477), Run(479));
            Assert.False(job.IsIncomplete);
            Assert.DoesNotContain("tests_removed", job.IncompleteReasons());

            var p = Payload(job);
            Assert.Equal(-2, p.GetProperty("delta").GetInt32());
            Assert.Equal(2, p.GetProperty("tests_removed").GetInt32());
            Assert.Equal(
                "harness-measured test counts: 479 before this task, 477 after (delta -2)",
                p.GetProperty("note").GetString());
        }

        [Fact]
        public void BaselineMissing_TotalReportedNoDeltaClaimedReasonStated()
        {
            // BaselineTests null == the before-run was never performed (test_baseline
            // "off" or the job never reached it).
            var job = NewJob(Run(37), null);
            var p = Payload(job);
            Assert.Equal(37, p.GetProperty("total").GetInt32());
            Assert.True(p.GetProperty("baseline_total").ValueKind == JsonValueKind.Null);
            Assert.True(p.GetProperty("delta").ValueKind == JsonValueKind.Null);
            Assert.True(p.GetProperty("tests_removed").ValueKind == JsonValueKind.Null);
            string? reason = p.GetProperty("baseline_unavailable_reason").GetString();
            Assert.Contains("baseline run skipped", reason);
            Assert.Equal(
                "harness-measured test counts: 37 after; " +
                "baseline unavailable (baseline run skipped (test_baseline off or job did not reach the before-run)) — " +
                "total reported, no delta claimed",
                p.GetProperty("note").GetString());
        }

        [Fact]
        public void BaselineRunFailed_BeforeCountIsNullNotGuess()
        {
            var job = NewJob(Run(37), Run(37, exitCode: 1)); // total present but exit 1
            var p = Payload(job);
            Assert.Equal(37, p.GetProperty("total").GetInt32());
            Assert.True(p.GetProperty("baseline_total").ValueKind == JsonValueKind.Null);
            Assert.True(p.GetProperty("delta").ValueKind == JsonValueKind.Null);
            Assert.Contains("baseline test run failed (exit code 1)",
                p.GetProperty("baseline_unavailable_reason").GetString());
        }

        [Fact]
        public void BaselineUnparseable_BeforeCountIsNullWithReason()
        {
            var job = NewJob(Run(37),
                Run(null, exitCode: 0, parseFailure: "no test summary line found in output"));
            var p = Payload(job);
            Assert.True(p.GetProperty("baseline_total").ValueKind == JsonValueKind.Null);
            Assert.True(p.GetProperty("delta").ValueKind == JsonValueKind.Null);
            Assert.Contains("no parseable total", p.GetProperty("baseline_unavailable_reason").GetString());
        }

        [Fact]
        public void AfterParseFailed_TotalNullReasonStatedNoDelta()
        {
            var job = NewJob(
                Run(null, exitCode: 0, parseFailure: "no test summary line found in output"),
                Run(466));
            var p = Payload(job);
            Assert.True(p.GetProperty("total").ValueKind == JsonValueKind.Null);
            Assert.Equal("no test summary line found in output",
                p.GetProperty("total_unavailable_reason").GetString());
            Assert.Equal(466, p.GetProperty("baseline_total").GetInt32());
            Assert.True(p.GetProperty("delta").ValueKind == JsonValueKind.Null);
            Assert.True(p.GetProperty("tests_removed").ValueKind == JsonValueKind.Null);
            Assert.Equal(
                "no harness-measured test total (no test summary line found in output)",
                p.GetProperty("note").GetString());
        }

        [Fact]
        public void VerifyTestsSkipped_TestVerificationStaysNull()
        {
            // A caller passing neither flag sees today's exact behavior: no file
            // changes (or verify_tests off) -> Tests null -> test_verification null.
            var job = NewJob(null, null);
            Assert.Null(TestVerificationPayload.Create(job));
        }

        [Fact]
        public void RawRunFieldsKeepTodayNamesAndValues()
        {
            // The shared BuildVerification shape is preserved inside test_verification:
            // command/succeeded/exit_code/output_tail exactly as before.
            var job = NewJob(Run(37), Run(37));
            var p = Payload(job);
            Assert.Equal("dotnet test", p.GetProperty("command").GetString());
            Assert.True(p.GetProperty("succeeded").GetBoolean());
            Assert.Equal(0, p.GetProperty("exit_code").GetInt32());
            Assert.Equal("...", p.GetProperty("output_tail").GetString());
        }
    }

    // ── End-to-end: suite INVOCATION COUNTS through the real worker loop ────
    // The fake LLM server makes the agent create one file (a "save" action, so
    // HasFileChanges is true and the after-run gate passes), then stop. The
    // TestRunnerOverride seam stands in for `dotnet test` and COUNTS each
    // invocation — "off skips the before-run" is asserted as "exactly one
    // invocation", not "it was fast". No dotnet process is spawned and no
    // user-level path is resolved (working dir is a fresh temp dir).
    public sealed class TestBaselineInvocationTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;

        public TestBaselineInvocationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_testcount_job_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            // llama request template — the shape FakeLlmServer speaks (see the
            // NoExecuteInheritanceTests pattern).
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

        [Fact]
        public async Task BeforeRun_Default_RunsSuiteTwiceAndReportsDelta()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            var calls = new List<int>();
            using var mgr = new AgentJobManager();
            mgr.TestRunnerOverride = (_, _) =>
            {
                int n = calls.Count + 1;
                calls.Add(n);
                // Baseline (1st call) reports 37; after (2nd) reports 38.
                string summary = n == 1
                    ? CannedTestOutput.McpPass37
                    : "Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 4 s - DevMind.McpServer.Tests.dll (net10.0)";
                return Task.FromResult(TestVerification.FromRun("dotnet test", 0, "Test run for X\n" + summary + "\n"));
            };

            var job = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false, verifyTests: true); // runTestBaseline defaults to true
            await WaitForDone(job);

            // THE assertion: before-run + after-run = exactly two invocations.
            Assert.Equal(2, calls.Count);

            Assert.NotNull(job.BaselineTests);
            Assert.Equal(37, job.BaselineTests.Total);
            Assert.NotNull(job.Tests);
            Assert.Equal(38, job.Tests.Total);

            var p = JsonSerializer.SerializeToElement(TestVerificationPayload.Create(job)!);
            Assert.Equal(37, p.GetProperty("baseline_total").GetInt32());
            Assert.Equal(38, p.GetProperty("total").GetInt32());
            Assert.Equal(1, p.GetProperty("delta").GetInt32());
            Assert.Equal(
                "harness-measured test counts: 37 before this task, 38 after (delta +1)",
                p.GetProperty("note").GetString());
        }

        [Fact]
        public async Task BaselineOff_RunsSuiteExactlyOnceNoDeltaClaimed()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            var calls = new List<int>();
            using var mgr = new AgentJobManager();
            mgr.TestRunnerOverride = (_, _) =>
            {
                calls.Add(calls.Count + 1);
                return Task.FromResult(
                    TestVerification.FromRun("dotnet test", 0, "Test run for X\n" + CannedTestOutput.McpPass37 + "\n"));
            };

            var job = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false, verifyTests: true, runTestBaseline: false);
            await WaitForDone(job);

            // "off" skips the before-run ENTIRELY: exactly ONE suite invocation
            // (the after-run), not merely a faster second one.
            Assert.Equal(1, calls.Count);

            Assert.Null(job.BaselineTests);
            Assert.NotNull(job.Tests);
            Assert.Equal(37, job.Tests.Total);

            var p = JsonSerializer.SerializeToElement(TestVerificationPayload.Create(job)!);
            Assert.True(p.GetProperty("baseline_total").ValueKind == JsonValueKind.Null);
            Assert.True(p.GetProperty("delta").ValueKind == JsonValueKind.Null);
            Assert.Equal(37, p.GetProperty("total").GetInt32());
            Assert.Contains("baseline run skipped", p.GetProperty("baseline_unavailable_reason").GetString());
        }

        [Fact]
        public async Task VerifyTestsOff_NeverRunsSuite()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            var calls = new List<int>();
            using var mgr = new AgentJobManager();
            mgr.TestRunnerOverride = (_, _) =>
            {
                calls.Add(calls.Count + 1);
                return Task.FromResult(
                    TestVerification.FromRun("dotnet test", 0, "Test run for X\n" + CannedTestOutput.McpPass37 + "\n"));
            };

            var job = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false, verifyTests: false); // neither flag
            await WaitForDone(job);

            // Neither flag -> today's exact behavior: no suite run at all.
            Assert.Empty(calls);
            Assert.Null(job.BaselineTests);
            Assert.Null(job.Tests);
            Assert.Null(TestVerificationPayload.Create(job));
        }

        private static async Task<AgentJob> WaitForDone(AgentJob job, int timeoutMs = 30000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs
                   && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(50);
            Assert.Equal(AgentJobState.Done, job.State);
            return job;
        }
    }

    /// <summary>Fake LLM server for the test-count tests: POST #1 answers with a
    /// create_file tool call (so the job HAS file changes and the after-run gate
    /// passes), every later POST answers with task_done. Same SSE/Content-Length
    /// mechanics as the existing FakeLlmServer (that one is sealed and always
    /// returns task_done, so a variant is needed).</summary>
    internal sealed class EditThenDoneLlmServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _filePath;
        private int _postCount;
        // (TcpListener resolved via using System.Net — no qualification needed.)

        public string BaseUrl { get; }

        public EditThenDoneLlmServer(string filePath)
        {
            _filePath = filePath;
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
                    var (method, path, body) = request.Value;

                    byte[] payload;
                    string contentType;
                    if (method == "POST" && !string.IsNullOrEmpty(body))
                    {
                        int post = System.Threading.Interlocked.Increment(ref _postCount);
                        string sse = post == 1
                            ? ToolCallSse("create_file",
                                JsonSerializer.Serialize(new
                                {
                                    filename = _filePath,
                                    content = "int TestCountDeltaMarker = 1;\n"
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

        // Same byte-by-byte header read + exact Content-Length body read as
        // FakeLlmServer.ReadRequestAsync (avoids ReadToEnd hanging on keep-alive).
        private static async Task<(string method, string path, string body)?> ReadRequestAsync(System.IO.Stream stream)
        {
            var headerBuf = new System.IO.MemoryStream();
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
                if (line.StartsWith("Content-Length:", System.StringComparison.OrdinalIgnoreCase))
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
}
