// File: FailedTestsParseTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-61: the harness keeps the NAMES of the failing tests from its own test runs.
// job-2164's baseline was red, its after-run green, and four reruns passed — the
// result said only "baseline test run had failing tests (exit code 1)", so the
// flaky test could not be named.
//
// Three layers:
//   * FailedTestsParseTests — the parser, against output captured from a real
//     failing `dotnet test` run on BEAST (Fixtures\dotnet-test-fail-*.txt; the
//     throwaway project lived under %TEMP%, its path is rewritten to C:\temp\...).
//   * FailedTestsPayloadTests — failed_tests / baseline_failed_tests and the
//     baseline_unavailable_reason suffix.
//   * FailedTestsJobTests — end-to-end through the worker loop and the
//     TestRunnerOverride seam, plus a pre-H-61 sidecar served by devmind_task_result.

using System.Text.Json;
using Xunit;

namespace DevMind.McpServer.Tests
{
    internal static class FailedTestFixtures
    {
        public const string SingleName = "H61.Fixture.SingleFailure.BreaksOnPurpose";
        public const string TheoryName = "H61.Fixture.TheoryFailure.RejectsArg(n: 1, s: \"a\")";

        /// <summary>The SDK info line, verbatim from a job transcript on this machine.</summary>
        public const string PruneLine =
            "         Failed to load prune package data from PrunePackageData folder, loading from targeting packs instead";

        public static string Read(string name)
            => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

        public static string Single => Read("dotnet-test-fail-single.txt");
        public static string Theory => Read("dotnet-test-fail-theory.txt");
        public static string Thirty => Read("dotnet-test-fail-30.txt");

        /// <summary>The fixture with the lines matching <paramref name="drop"/> removed.</summary>
        public static string Without(string output, Func<string, bool> drop)
            => string.Join("\n", output.Split('\n').Where(l => !drop(l)));

        public const string PassingOutput =
            "Test run for X\n" +
            "Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 40 ms - FailFixture.dll (net10.0)\n";
    }

    public sealed class FailedTestsParseTests
    {
        [Fact]
        public void VsTestFormatAlone_ParsesTheName()
        {
            string output = FailedTestFixtures.Without(FailedTestFixtures.Single, l => l.Contains("[xUnit.net"));
            Assert.DoesNotContain("[FAIL]", output);
            var (names, truncated) = FailedTestsParse.Parse(output);
            Assert.Equal(new[] { FailedTestFixtures.SingleName }, names);
            Assert.Equal(0, truncated);
        }

        [Fact]
        public void XunitFormatAlone_ParsesTheName()
        {
            string output = FailedTestFixtures.Without(FailedTestFixtures.Single, l => l.StartsWith("  Failed "));
            var (names, truncated) = FailedTestsParse.Parse(output);
            Assert.Equal(new[] { FailedTestFixtures.SingleName }, names);
            Assert.Equal(0, truncated);
        }

        [Fact]
        public void SameTestInBothFormats_IsOneName()
        {
            string output = FailedTestFixtures.Single;
            Assert.Contains("[FAIL]", output);
            Assert.Contains("  Failed " + FailedTestFixtures.SingleName + " [", output);
            var (names, _) = FailedTestsParse.Parse(output);
            Assert.Equal(new[] { FailedTestFixtures.SingleName }, names);
        }

        [Fact]
        public void TheoryWithArguments_KeepsTheArguments()
        {
            var (names, _) = FailedTestsParse.Parse(FailedTestFixtures.Theory);
            Assert.Equal(new[] { FailedTestFixtures.TheoryName }, names);
        }

        [Fact]
        public void SummaryAndPruneLines_AreNotCounted()
        {
            string noise = FailedTestFixtures.PruneLine + "\n" +
                "Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 60 ms - FailFixture.dll (net10.0)\n";
            Assert.Empty(FailedTestsParse.Parse(noise).names);

            var (names, _) = FailedTestsParse.Parse(noise + FailedTestFixtures.Single);
            Assert.Equal(new[] { FailedTestFixtures.SingleName }, names);
        }

        [Fact]
        public void BuildErrors_AreNotCounted()
        {
            string output =
                "C:\\repo\\Foo.cs(12,9): error CS1002: ; expected [C:\\repo\\Foo.csproj]\n" +
                "Build FAILED.\n" +
                "C:\\repo\\Foo.cs(12,9): error CS1002: ; expected [C:\\repo\\Foo.csproj]\n";
            Assert.Empty(FailedTestsParse.Parse(output).names);
        }

        [Fact]
        public void ThirtyFailures_KeepsTwentyFiveInFirstSeenOrder_CountsFiveDropped()
        {
            string output = FailedTestFixtures.Thirty;
            var (names, truncated) = FailedTestsParse.Parse(output);
            Assert.Equal(FailedTestsParse.MaxNames, names.Count);
            Assert.Equal(5, truncated);
            Assert.Equal(names.Count, names.Distinct().Count());

            // First-seen order: the order the names first appear in the output.
            var firstSeen = output.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.EndsWith("[FAIL]"))
                .Select(l => l.Substring(l.IndexOf(']') + 1).Trim())
                .Select(l => l.Substring(0, l.Length - "[FAIL]".Length).Trim())
                .Take(25);
            Assert.Equal(firstSeen, names);
        }

        [Fact]
        public void EmptyOrNull_YieldsNothing()
        {
            Assert.Empty(FailedTestsParse.Parse("").names);
            Assert.Empty(FailedTestsParse.Parse(null).names);
        }

        [Theory]
        [InlineData("H61.Fixture.SingleFailure.BreaksOnPurpose", "SingleFailure.BreaksOnPurpose")]
        [InlineData("H61.Fixture.TheoryFailure.RejectsArg(n: 1, s: \"a.b\")", "TheoryFailure.RejectsArg(n: 1, s: \"a.b\")")]
        [InlineData("Class.Method", "Class.Method")]
        [InlineData("Method", "Method")]
        public void ShortName_IsClassDotMethod(string full, string expected)
            => Assert.Equal(expected, FailedTestsParse.ShortName(full));

        [Fact]
        public void FromRun_FillsTheNamesFromTheFullOutput()
        {
            // The failure sits far ahead of the ~2 KB tail.
            string output = FailedTestFixtures.Single + new string('.', 5_000) + "\n";
            var run = TestVerification.FromRun("dotnet test", 1, output);
            Assert.DoesNotContain("BreaksOnPurpose", run.OutputTail);
            Assert.Equal(new[] { FailedTestFixtures.SingleName }, run.FailedTests);
            Assert.Equal(0, run.FailedTestsTruncated);
        }

        [Fact]
        public void FailedTestsSummary_ListsShortNamesThenMore()
        {
            var run = TestVerification.FromRun("dotnet test", 1, FailedTestFixtures.Thirty);
            string five = run.FailedTestsSummary(5);
            Assert.EndsWith(" +25 more", five);
            Assert.Equal(5, five.Split(" +")[0].Split(", ").Length);
            Assert.StartsWith("ManyFailures.Fails(i: ", five);
            Assert.EndsWith(" +5 more", run.FailedTestsSummary());
        }
    }

    public sealed class FailedTestsPayloadTests
    {
        private static JsonElement Payload(TestVerification after, TestVerification? baseline) =>
            JsonSerializer.SerializeToElement(TestVerificationPayload.Create(new AgentJob
            {
                Id = "job-h61",
                Prompt = "p",
                WorkingDirectory = @"C:\temp\hermetic",
                State = AgentJobState.Done,
                Tests = after,
                BaselineTests = baseline,
            })!);

        [Fact]
        public void RedBaseline_GreenAfter_NamesTheBaselineFailure()
        {
            var p = Payload(
                TestVerification.FromRun("dotnet test", 0, FailedTestFixtures.PassingOutput),
                TestVerification.FromRun("dotnet test", 1, FailedTestFixtures.Single));

            Assert.Empty(p.GetProperty("failed_tests").EnumerateArray());
            Assert.Equal(new[] { FailedTestFixtures.SingleName },
                p.GetProperty("baseline_failed_tests").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(
                "baseline test run had failing tests (exit code 1) — structural before-count, suite already red " +
                "before this task — failing: SingleFailure.BreaksOnPurpose",
                p.GetProperty("baseline_unavailable_reason").GetString());
        }

        [Fact]
        public void ManyBaselineFailures_ReasonQuotesFiveThenMore()
        {
            var p = Payload(
                TestVerification.FromRun("dotnet test", 0, FailedTestFixtures.PassingOutput),
                TestVerification.FromRun("dotnet test", 1, FailedTestFixtures.Thirty));
            string reason = p.GetProperty("baseline_unavailable_reason").GetString()!;
            string failing = reason.Substring(reason.IndexOf("failing: ", StringComparison.Ordinal) + "failing: ".Length);
            Assert.EndsWith(" +25 more", failing);
            Assert.Equal(5, failing.Split(" +")[0].Split(", ").Length);
            Assert.Equal(25, p.GetProperty("baseline_failed_tests").GetArrayLength());
        }

        [Fact]
        public void RedAfter_FailedTestsListed()
        {
            var p = Payload(
                TestVerification.FromRun("dotnet test", 1, FailedTestFixtures.Theory),
                TestVerification.FromRun("dotnet test", 0, FailedTestFixtures.PassingOutput));
            Assert.Equal(new[] { FailedTestFixtures.TheoryName },
                p.GetProperty("failed_tests").EnumerateArray().Select(e => e.GetString()));
            Assert.Empty(p.GetProperty("baseline_failed_tests").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, p.GetProperty("baseline_unavailable_reason").ValueKind);
        }

        [Fact]
        public void NoBaselineRun_BaselineFailedTestsNull()
        {
            var p = Payload(TestVerification.FromRun("dotnet test", 0, FailedTestFixtures.PassingOutput), null);
            Assert.Equal(JsonValueKind.Null, p.GetProperty("baseline_failed_tests").ValueKind);
            Assert.Empty(p.GetProperty("failed_tests").EnumerateArray());
            Assert.DoesNotContain("failing:", p.GetProperty("baseline_unavailable_reason").GetString());
        }
    }

    [Collection(ProcessEnvironmentCollection.Name)]
    public sealed class FailedTestsJobTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _tasksDir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;
        private readonly string? _priorTasksDir;

        public FailedTestsJobTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h61_job_{Guid.NewGuid():N}");
            _tasksDir = Path.Combine(_dir, "tasks");
            Directory.CreateDirectory(_tasksDir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            _priorTasksDir = Environment.GetEnvironmentVariable("DEVMIND_TASKS_DIR");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _tasksDir);
        }

        public void Dispose()
        {
            // Restore, never clear (see ResultSidecarTokenUsageTests).
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            Environment.SetEnvironmentVariable("DEVMIND_TASKS_DIR", _priorTasksDir);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public async Task RedBaseline_GreenAfter_ResultAndTranscriptNameTheTest()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            int calls = 0;
            using var mgr = new AgentJobManager();
            mgr.TestRunnerOverride = (_, _) => Task.FromResult(++calls == 1
                ? TestVerification.FromRun("dotnet test", 1, FailedTestFixtures.Single)
                : TestVerification.FromRun("dotnet test", 0, FailedTestFixtures.PassingOutput));

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: false, verifyTests: true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 30000 && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(50);
            Assert.Equal(AgentJobState.Done, job.State);
            Assert.Equal(2, calls);

            Assert.Contains("[job] test baseline: exit 1, total 2 (harness-measured); failed: SingleFailure.BreaksOnPurpose\n",
                job.GetTail());
            Assert.Contains("[job] test verification: passed (2 tests)\n", job.GetTail());

            using var doc = JsonDocument.Parse(await new AgentTaskTools(mgr).TaskResult(job.Id, CancellationToken.None));
            var tv = doc.RootElement.GetProperty("test_verification");
            Assert.Equal(new[] { FailedTestFixtures.SingleName },
                tv.GetProperty("baseline_failed_tests").EnumerateArray().Select(e => e.GetString()));
            Assert.Empty(tv.GetProperty("failed_tests").EnumerateArray());
            Assert.Contains("failing: SingleFailure.BreaksOnPurpose", tv.GetProperty("baseline_unavailable_reason").GetString());
        }

        [Fact]
        public async Task RedAfterRun_TranscriptLineNamesTheTest()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = new AgentJobManager();
            mgr.TestRunnerOverride = (_, _) => Task.FromResult(
                TestVerification.FromRun("dotnet test", 1, FailedTestFixtures.Theory));

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: false, verifyTests: true,
                runTestBaseline: false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 30000 && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(50);

            Assert.Contains("[job] test verification: failed (2 tests); failed: TheoryFailure.RejectsArg(n: 1, s: \"a\")\n",
                job.GetTail());
        }

        [Fact]
        public async Task PreH61Sidecar_LoadsWithoutTheNewFields()
        {
            // A sidecar as written before H-61: test_verification without failed_tests or
            // baseline_failed_tests.
            const string oldSidecar = """
                {"job_id":"job-preh61","state":"done","incomplete_reasons":[],"answer":"done","actions":[],
                 "iterations":3,"elapsed_seconds":12.5,"working_dir":"C:\\temp\\hermetic",
                 "ended_at_utc":"2026-10-01 10:00:00","build_verification":null,
                 "test_verification":{"command":"dotnet test","succeeded":true,"exit_code":0,"output_tail":"...",
                   "total":37,"total_unavailable_reason":null,"baseline_total":37,
                   "baseline_unavailable_reason":"baseline test run had failing tests (exit code 1) — structural before-count, suite already red before this task",
                   "delta":0,"tests_removed":0,"note":"n","forced_reason":null},
                 "mcp":null}
                """;
            File.WriteAllText(Path.Combine(AgentJobManager.TranscriptDir, "job-preh61.result.json"), oldSidecar);

            using var mgr = new AgentJobManager();
            using var doc = JsonDocument.Parse(await new AgentTaskTools(mgr).TaskResult("job-preh61", CancellationToken.None));
            var tv = doc.RootElement.GetProperty("result").GetProperty("test_verification");
            Assert.Equal(37, tv.GetProperty("total").GetInt32());
            Assert.False(tv.TryGetProperty("failed_tests", out _));
            Assert.False(tv.TryGetProperty("baseline_failed_tests", out _));
        }
    }
}
