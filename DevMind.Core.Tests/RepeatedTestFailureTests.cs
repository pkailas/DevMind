// File: RepeatedTestFailureTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-58 (job-2108, 2026-10-02): the agent re-ran the same failing layout tests ~10 times while
// changing its theory, and only dumped the real HTML late. The same failing test(s) in three
// consecutive runs now inject one harness note, and on a think=false job turn thinking on
// (effort "medium") until the tests pass or 15 requests have run with it.

using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class RepeatedTestFailureGuardTests
    {
        private static TestRunOutcome Fail(params string[] names) => new TestRunOutcome(false, names.OrderBy(n => n, StringComparer.Ordinal).ToList());
        private static readonly TestRunOutcome Pass = new TestRunOutcome(true, Array.Empty<string>());

        // ── Parse ──

        [Fact]
        public void Parse_VsTestNormalVerbosity_NamesTheFailures_IgnoringRestoreNoise()
        {
            string output =
                "          Failed to load prune package data from PrunePackageData folder, loading from targeting packs instead\n" +
                "  Failed Ns.LayoutTests.Get_Header_Pill [255 ms]\n" +
                "  Error Message:\n   Assert.Contains() Failure\n" +
                "  Failed Ns.WidthTests.LongFields [70 ms]\n" +
                "  Passed Ns.WidthTests.ShortFields [3 ms]\n" +
                "Test Run Failed.\nTotal tests: 11\n";

            var run = TestRunOutcome.Parse(output);

            Assert.NotNull(run);
            Assert.False(run!.Passed);
            Assert.Equal(new[] { "Ns.LayoutTests.Get_Header_Pill", "Ns.WidthTests.LongFields" }, run.FailedTests);
        }

        [Theory]
        [InlineData("Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 1 s - X.Tests.dll (net10.0)")]
        [InlineData("Test Run Successful.\nTotal tests: 85")]
        [InlineData("Test run summary: Passed!\n  total: 4")]
        public void Parse_PassingRun(string output)
        {
            var run = TestRunOutcome.Parse(output);
            Assert.NotNull(run);
            Assert.True(run!.Passed);
            Assert.Empty(run.FailedTests);
        }

        [Fact]
        public void Parse_MinimalAndMtpFormats()
        {
            var minimal = TestRunOutcome.Parse(
                "  Failed Ns.T.A [1 s]\nFailed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1 - X.Tests.dll (net10.0)");
            Assert.Equal(new[] { "Ns.T.A" }, minimal!.FailedTests);

            var mtp = TestRunOutcome.Parse("failed Ns.T.B (12ms)\nTest run summary: Failed!\n  failed: 1");
            Assert.False(mtp!.Passed);
            Assert.Equal(new[] { "Ns.T.B" }, mtp.FailedTests);
        }

        [Theory]
        [InlineData("")]
        [InlineData("Probe.cs(14,17): error CS1061: 'Control' does not contain a definition for 'AutoScaleMode'\nBuild FAILED.")]
        [InlineData("Failed to load prune package data from PrunePackageData folder")]
        public void Parse_NoTestRun_IsNull(string output) => Assert.Null(TestRunOutcome.Parse(output));

        // ── Guard ──

        [Fact]
        public void ThreeConsecutiveRunsWithTheSameFailures_ProduceExactlyOneNote()
        {
            var guard = new RepeatedTestFailureGuard();
            Assert.Null(guard.Observe(Fail("A", "B")));
            Assert.Null(guard.Observe(Fail("A", "B")));
            Assert.Equal(new[] { "A", "B" }, guard.Observe(Fail("A", "B")));
            Assert.Null(guard.Observe(Fail("A", "B")));   // 4th, 5th: no repeat
            Assert.Null(guard.Observe(Fail("A", "B")));
        }

        [Fact]
        public void AChangedSet_ResetsTheCount()
        {
            var guard = new RepeatedTestFailureGuard();
            guard.Observe(Fail("A"));
            guard.Observe(Fail("A"));
            Assert.Null(guard.Observe(Fail("B")));          // nothing in common: starts over
            Assert.Equal(1, guard.Streak);
            Assert.Null(guard.Observe(Fail("B")));
            Assert.Equal(new[] { "B" }, guard.Observe(Fail("B")));
        }

        [Fact]
        public void APassingRun_ResetsTheCount()
        {
            var guard = new RepeatedTestFailureGuard();
            guard.Observe(Fail("A"));
            guard.Observe(Fail("A"));
            Assert.Null(guard.Observe(Pass));
            Assert.Equal(0, guard.Streak);
            Assert.Null(guard.Observe(Fail("A")));
            Assert.Null(guard.Observe(Fail("A")));
            Assert.Equal(new[] { "A" }, guard.Observe(Fail("A")));
        }

        [Fact]
        public void AFilteredReRunOfOneFailure_StillCounts_AndTheNoteNamesWhatKeptFailing()
        {
            // job-2108 alternated full runs and --filter re-runs of one failing test.
            var guard = new RepeatedTestFailureGuard();
            guard.Observe(Fail("A", "B", "C"));
            guard.Observe(Fail("A"));
            Assert.Equal(new[] { "A" }, guard.Observe(Fail("A", "C")));
        }

        [Fact]
        public void NoTestRun_AndAFailureWithNoNames_NeitherCountNorReset()
        {
            var guard = new RepeatedTestFailureGuard();
            guard.Observe(Fail("A"));
            Assert.Null(guard.Observe(null));
            Assert.Null(guard.Observe(Fail()));
            guard.Observe(Fail("A"));
            Assert.Equal(new[] { "A" }, guard.Observe(Fail("A")));
        }

        [Fact]
        public void TheSameSetAfterAReset_DoesNotGetASecondNote()
        {
            var guard = new RepeatedTestFailureGuard();
            for (int i = 0; i < 3; i++) guard.Observe(Fail("A"));
            guard.Observe(Pass);
            for (int i = 0; i < 3; i++) Assert.Null(guard.Observe(Fail("A")));
        }

        [Fact]
        public void Note_IsTheBriefsText()
        {
            Assert.Equal(
                "The same test(s) have failed 3 runs in a row: Ns.T.A, Ns.T.B. Before changing code again, print the " +
                "actual value being asserted (rendered output, response body or variable) and compare it with the " +
                "expectation. Do not change the test's expectation to match without saying why.",
                RepeatedTestFailureGuard.Note(new[] { "Ns.T.A", "Ns.T.B" }));
        }

        // ── Auto-think decisions ──

        [Fact]
        public void AutoThink_EscalatesOnce_DeEscalatesWhenTestsPass()
        {
            var auto = new AutoThinkEscalation(jobThinks: false, autoThink: true);
            Assert.True(auto.TryEscalate());
            Assert.False(auto.TryEscalate());                 // already on
            auto.OnRequest();
            auto.OnRequest();
            Assert.Null(auto.TryDeEscalate(testsPassed: false));
            Assert.Equal("tests passed", auto.TryDeEscalate(testsPassed: true));
            Assert.False(auto.Active);
            Assert.Equal(1, auto.Escalations);
            Assert.Equal(2, auto.Iterations);
        }

        [Fact]
        public void AutoThink_DeEscalatesAtTheFifteenIterationCap()
        {
            var auto = new AutoThinkEscalation(jobThinks: false, autoThink: true);
            auto.TryEscalate();
            for (int i = 0; i < AutoThinkEscalation.MaxIterations - 1; i++)
            {
                auto.OnRequest();
                Assert.Null(auto.TryDeEscalate(testsPassed: false));
            }
            auto.OnRequest();
            Assert.Equal("15-iteration cap", auto.TryDeEscalate(testsPassed: false));
            auto.OnRequest();                                  // off: no longer counted
            Assert.Equal(15, auto.Iterations);
        }

        [Theory]
        [InlineData(true, true)]    // the job already thinks
        [InlineData(false, false)]  // auto_think=false
        [InlineData(true, false)]
        public void AutoThink_NeverEscalates_WhenNotAllowed(bool jobThinks, bool autoThink)
        {
            var auto = new AutoThinkEscalation(jobThinks, autoThink);
            Assert.False(auto.Allowed);
            Assert.False(auto.TryEscalate());
            auto.OnRequest();
            Assert.Equal(0, auto.Escalations);
            Assert.Equal(0, auto.Iterations);
        }
    }

    // ── Through the real headless loop ────────────────────────────────────────────

    public class RepeatedTestFailureSessionTests : IDisposable
    {
        private readonly string _dir;
        private string NoPromptFile => Path.Combine(_dir, "no-system-prompt.md");

        private const string NoteMarker = "Before changing code again, print the actual value being asserted";

        public RepeatedTestFailureSessionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h58_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "fail.txt"),
                "  Failed Ns.LayoutTests.Get_Header_Pill [255 ms]\n  Error Message:\n   Assert.Contains() Failure\nTest Run Failed.\nTotal tests: 3\n");
            File.WriteAllText(Path.Combine(_dir, "pass.txt"), "Test Run Successful.\nTotal tests: 3\n");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string Shell(string file) => FakeSseServer.BuildToolCallSse("run_shell",
            "{\"command\":\"Get-Content -Raw '" + Path.Combine(_dir, file).Replace("\\", "\\\\") + "'\"}");

        private async Task<HeadlessAgentResult> Run(FakeSseServer server, bool think = false, bool autoThink = true, int maxDepth = 8)
        {
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                return await HeadlessAgent.RunAsync(
                    "Fix the layout tests.",
                    new HeadlessOptions
                    {
                        RequestTimeoutMinutes = 1,
                        FirstTokenTimeoutMinutes = 1,
                        ManualContextSize = 32768,
                        AgenticLoopMaxDepth = maxDepth,
                        ShowLlmThinking = think,
                        ReasoningEffort = "low",
                        AutoThink = autoThink,
                    },
                    server.BaseUrl, apiKey: null!, workingDirectory: _dir, buildCommand: "dotnet build",
                    ct: CancellationToken.None, promptFilePath: NoPromptFile);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        private static List<string> Requests(FakeSseServer server)
        {
            lock (server.RequestBodies) return server.RequestBodies.ToList();
        }

        private static bool Thinks(string requestBody)
            => (bool?)JObject.Parse(requestBody)["chat_template_kwargs"]?["enable_thinking"] == true;

        private static string? Effort(string requestBody)
            => (string?)JObject.Parse(requestBody)["chat_template_kwargs"]?["reasoning_effort"];

        private static bool CanRunShell => OperatingSystem.IsWindows() && ShellRunner.IsPowerShellAvailable();

        [Fact]
        public async Task ThreeFailingRuns_OneNote_ThinkingOnUntilTheTestsPass()
        {
            if (!CanRunShell) return;
            using var server = new FakeSseServer();
            server.SseQueue.Add(Shell("fail.txt"));     // request 1
            server.SseQueue.Add(Shell("fail.txt"));     // request 2
            server.SseQueue.Add(Shell("fail.txt"));     // request 3 -> note + thinking on
            server.SseQueue.Add(Shell("pass.txt"));     // request 4 (thinking) -> tests pass, thinking off
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}")); // request 5

            var result = await Run(server);

            Assert.Null(result.Error);
            var requests = Requests(server);
            Assert.Equal(5, requests.Count);
            Assert.Equal(new[] { false, false, false, true, false }, requests.Select(Thinks));
            Assert.Equal("medium", Effort(requests[3]));

            // The note is injected once: absent before request 4, a single copy (history) after.
            for (int i = 0; i < 3; i++) Assert.DoesNotContain(NoteMarker, requests[i]);
            Assert.Single(Regex.Matches(requests[4], Regex.Escape(NoteMarker)));

            var notes = result.Actions.Where(a => a.Kind == "harness_note").Select(a => a.Detail).ToList();
            Assert.Equal(3, notes.Count);
            Assert.StartsWith("The same test(s) have failed 3 runs in a row: Ns.LayoutTests.Get_Header_Pill.", notes[0]);
            Assert.Equal("thinking auto-enabled (repeated failures: Ns.LayoutTests.Get_Header_Pill)", notes[1]);
            Assert.Equal("thinking auto-disabled (tests passed)", notes[2]);
            Assert.Equal(1, result.AutoThinkEscalations);
            Assert.Equal(1, result.AutoThinkIterations);
        }

        [Fact]
        public async Task ThinkingStaysOnForAtMostFifteenRequests()
        {
            if (!CanRunShell) return;
            using var server = new FakeSseServer { RepeatLastWhenExhausted = true };
            server.SseQueue.Add(Shell("fail.txt"));     // the same failing run, every request

            var result = await Run(server, maxDepth: 20);

            var thinks = Requests(server).Select(Thinks).ToList();
            Assert.True(thinks.Count >= 19, $"expected at least 19 requests, got {thinks.Count}");
            Assert.All(thinks.Take(3), t => Assert.False(t));
            Assert.All(thinks.Skip(3).Take(AutoThinkEscalation.MaxIterations), t => Assert.True(t));
            Assert.All(thinks.Skip(3 + AutoThinkEscalation.MaxIterations), t => Assert.False(t));

            Assert.Equal("thinking auto-disabled (15-iteration cap)",
                result.Actions.Last(a => a.Kind == "harness_note").Detail);
            // The same set keeps failing, but its note (and escalation) already happened.
            Assert.Single(result.Actions, a => a.Detail.StartsWith("The same test(s)", StringComparison.Ordinal));
            Assert.Equal(1, result.AutoThinkEscalations);
            Assert.Equal(AutoThinkEscalation.MaxIterations, result.AutoThinkIterations);
        }

        [Theory]
        [InlineData(true, true)]    // started with think=true: the job's own setting stands
        [InlineData(false, false)]  // auto_think=false: never escalate
        public async Task NoEscalation_WhenThinkIsOnOrAutoThinkIsOff(bool think, bool autoThink)
        {
            if (!CanRunShell) return;
            using var server = new FakeSseServer();
            for (int i = 0; i < 3; i++) server.SseQueue.Add(Shell("fail.txt"));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}"));

            var result = await Run(server, think, autoThink);

            Assert.Null(result.Error);
            var requests = Requests(server);
            Assert.Equal(4, requests.Count);
            Assert.All(requests, r => Assert.Equal(think, Thinks(r)));
            if (think) Assert.All(requests, r => Assert.Equal("low", Effort(r)));   // the job's own effort, untouched

            // The note still fires; nothing else does.
            var note = Assert.Single(result.Actions, a => a.Kind == "harness_note");
            Assert.StartsWith("The same test(s) have failed 3 runs in a row", note.Detail);
            Assert.Contains(NoteMarker, requests[3]);
            Assert.Equal(0, result.AutoThinkEscalations);
            Assert.Equal(0, result.AutoThinkIterations);
        }
    }
}
