// File: BaselineBuildAndForcedTestsTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-45: a test baseline taken on a tree that does not compile is a partial count — job-1859
// reported delta +747 for a real +17. The harness now builds right before the baseline and,
// when that build fails, takes no baseline at all: baseline_total null, delta null,
// baseline_unavailable_reason "baseline build failed", the first error in the note. A
// genuinely failing TEST still yields a real baseline count — a build cannot mistake one.
//
// H-09: with verify_tests off, a job that changed a build-affecting file (Directory.Build.props,
// a .csproj, appsettings*.json, ...) gets a test verification anyway, marked forced_reason,
// with no baseline. job-1658 bumped Directory.Build.props with verify_tests off and a version
// assertion went red unnoticed for two commits.
//
// Real jobs through the real AgentJobManager against EditThenDoneLlmServer (one create_file,
// then task_done); the build and test runs go through the TestRunnerOverride /
// BaselineBuildOverride seams.

using System.Diagnostics;
using System.Text.Json;
using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class BaselineBuildAndForcedTestsTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;

        public BaselineBuildAndForcedTestsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h45_h09_job_{Guid.NewGuid():N}");
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

        // A compile failure as `dotnet build` prints it.
        private const string CompileFailure =
            "  Determining projects to restore...\n" +
            "  All projects are up-to-date for restore.\n" +
            @"C:\repo\Foo.Tests\BarTests.cs(12,9): error CS0103: The name 'Baz' does not exist in the current context [C:\repo\Foo.Tests\Foo.Tests.csproj]" + "\n" +
            "\nBuild FAILED.\n";

        private const string BuildOk = "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n";

        // A red test run whose failure message quotes compiler-looking text — the case an
        // output scan could mistake for a build failure.
        private const string FailingTestRun =
            "Test run for Foo.Tests (net10.0)\n" +
            "  Failed Foo.Tests.ParserTests.ReportsTheError [3 ms]\n" +
            "  Error Message:\n" +
            "   Assert.Equal() Failure: expected \"error CS0103: The name 'Baz' does not exist\"\n" +
            "Failed!  - Failed:     2, Passed:    35, Skipped:     0, Total:    37, Duration: 6 s - Foo.Tests.dll (net10.0)\n";

        private static BuildVerification Build(int exitCode, string output) => new()
        {
            Command = "dotnet build",
            ExitCode = exitCode,
            OutputTail = output,
        };

        private static TestVerification Tests(int exitCode, string output) =>
            TestVerification.FromRun("dotnet test", exitCode, output);

        private static async Task WaitForDone(AgentJob job, int timeoutMs = 30_000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(20);
            Assert.Equal(AgentJobState.Done, job.State);
        }

        private static JsonElement Payload(AgentJob job) =>
            JsonSerializer.SerializeToElement(TestVerificationPayload.Create(job)!);

        // ── H-45 ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task BaselineOnATreeThatDoesNotCompile_IsNotTaken_NoPartialCount()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            int testRuns = 0;
            using var mgr = new AgentJobManager();
            mgr.BaselineBuildOverride = (_, _) => Task.FromResult<BuildVerification?>(Build(1, CompileFailure));
            mgr.TestRunnerOverride = (_, _) =>
            {
                testRuns++;
                // Were the baseline run, this is the partial count a broken tree gives.
                return Task.FromResult(Tests(0, CannedTestOutput.McpPass37));
            };

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: false, verifyTests: true);
            await WaitForDone(job);

            Assert.Equal(1, testRuns);                 // the after-run only — no baseline test run
            Assert.Null(job.BaselineTests);
            Assert.Contains("[job] test baseline: skipped — baseline build failed (exit 1: ", job.GetTail());

            var p = Payload(job);
            Assert.Equal(37, p.GetProperty("total").GetInt32());
            Assert.Equal(JsonValueKind.Null, p.GetProperty("baseline_total").ValueKind);
            Assert.Equal(JsonValueKind.Null, p.GetProperty("delta").ValueKind);
            Assert.Equal("baseline build failed", p.GetProperty("baseline_unavailable_reason").GetString());
            Assert.Contains("baseline build failed: ", p.GetProperty("note").GetString());
            Assert.Contains("error CS0103: The name 'Baz' does not exist", p.GetProperty("note").GetString());
            Assert.Equal(JsonValueKind.Null, p.GetProperty("forced_reason").ValueKind);
        }

        [Fact]
        public async Task ABaselineWithFailingTests_OnATreeThatBuilds_IsARealCount()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            int testRuns = 0;
            using var mgr = new AgentJobManager();
            mgr.BaselineBuildOverride = (_, _) => Task.FromResult<BuildVerification?>(Build(0, BuildOk));
            mgr.TestRunnerOverride = (_, _) =>
            {
                testRuns++;
                return Task.FromResult(testRuns == 1
                    ? Tests(1, FailingTestRun)
                    : Tests(0, "Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 4 s - Foo.Tests.dll (net10.0)\n"));
            };

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: false, verifyTests: true);
            await WaitForDone(job);

            Assert.Equal(2, testRuns);
            var p = Payload(job);
            Assert.Equal(37, p.GetProperty("baseline_total").GetInt32());
            Assert.Equal(1, p.GetProperty("delta").GetInt32());
            Assert.StartsWith("baseline test run had failing tests", p.GetProperty("baseline_unavailable_reason").GetString());
        }

        [Fact]
        public void FirstErrorExcerpt_NamesTheCompilerError()
        {
            Assert.Equal(
                @"C:\repo\Foo.Tests\BarTests.cs(12,9): error CS0103: The name 'Baz' does not exist in the current context [C:\repo\Foo.Tests\Foo.Tests.csproj]",
                TestVerificationPayload.FirstErrorExcerpt(CompileFailure));
            Assert.Equal("MSBUILD : error: project file not found",
                TestVerificationPayload.FirstErrorExcerpt("noise\nMSBUILD : error: project file not found\n"));
            Assert.Equal("noise", TestVerificationPayload.FirstErrorExcerpt("\nnoise\nBuild FAILED.\n"));
            Assert.Equal("no build output", TestVerificationPayload.FirstErrorExcerpt(""));
            Assert.True(TestVerificationPayload.FirstErrorExcerpt(new string('x', 500)).Length <= 200);
        }

        // ── H-09 ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task VerifyTestsOff_ButDirectoryBuildPropsChanged_ForcesTestVerification()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "Directory.Build.props"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            int testRuns = 0, baselineBuilds = 0;
            using var mgr = new AgentJobManager();
            mgr.BaselineBuildOverride = (_, _) => { baselineBuilds++; return Task.FromResult<BuildVerification?>(Build(0, BuildOk)); };
            mgr.TestRunnerOverride = (_, _) => { testRuns++; return Task.FromResult(Tests(1, CannedTestOutput.McpFail2)); };

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: false, verifyTests: false);
            await WaitForDone(job);

            Assert.Equal(1, testRuns);
            Assert.Equal(0, baselineBuilds);           // no before-run: verify_tests was off at the start
            Assert.Equal("build-affecting file changed: Directory.Build.props", job.TestsForcedReason);
            Assert.Contains("[job] test verification: forced (build-affecting change: Directory.Build.props)", job.GetTail());

            var p = Payload(job);
            Assert.Equal("build-affecting file changed: Directory.Build.props", p.GetProperty("forced_reason").GetString());
            Assert.Equal("verify_tests forced after the run; no before-run", p.GetProperty("baseline_unavailable_reason").GetString());
            Assert.Equal(JsonValueKind.Null, p.GetProperty("delta").ValueKind);

            // The forced run is a real verification: red tests make the job incomplete.
            Assert.True(job.IsIncomplete);
            Assert.Contains("test_verification_failed", job.IncompleteReasons());
        }

        [Fact]
        public async Task VerifyTestsOff_AndOnlyACsFileChanged_RunsNoTests()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "Widget.cs"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            int testRuns = 0;
            using var mgr = new AgentJobManager();
            mgr.TestRunnerOverride = (_, _) => { testRuns++; return Task.FromResult(Tests(0, CannedTestOutput.McpPass37)); };

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: false, verifyTests: false);
            await WaitForDone(job);

            Assert.Equal(0, testRuns);
            Assert.Null(job.Tests);
            Assert.Null(job.TestsForcedReason);
            Assert.DoesNotContain("test verification", job.GetTail());
        }

        [Fact]
        public async Task VerifyTestsOn_IsUnchanged_NoForcedReason()
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "Directory.Build.props"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            int testRuns = 0;
            using var mgr = new AgentJobManager();
            mgr.BaselineBuildOverride = (_, _) => Task.FromResult<BuildVerification?>(Build(0, BuildOk));
            mgr.TestRunnerOverride = (_, _) => { testRuns++; return Task.FromResult(Tests(0, CannedTestOutput.McpPass37)); };

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: false, verifyTests: true);
            await WaitForDone(job);

            Assert.Equal(2, testRuns);                 // baseline + after, as before
            Assert.Null(job.TestsForcedReason);
            Assert.DoesNotContain("forced", job.GetTail());
            var p = Payload(job);
            Assert.Equal(JsonValueKind.Null, p.GetProperty("forced_reason").ValueKind);
            Assert.Equal(0, p.GetProperty("delta").GetInt32());
        }

        [Fact]
        public async Task AForcedGreenRun_DoesNotExcuseAnIncompleteDeclaration()
        {
            // H-31/H-43: a green harness test run excuses an INCOMPLETE: line only when
            // verify_tests was requested — the agent was then told the harness runs the suite.
            // With verify_tests off it was told to run the suite itself, so its declared gap
            // stands even when a forced run is green. Unchanged by H-09, deliberately.
            const string answer = "Bumped the version.\nINCOMPLETE: the full suite was not run";
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "Directory.Build.props"), answer);
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = new AgentJobManager();
            mgr.TestRunnerOverride = (_, _) => Task.FromResult(Tests(0, CannedTestOutput.McpPass37));

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: false, verifyTests: false);
            await WaitForDone(job);

            Assert.True(job.Tests is { Succeeded: true });
            Assert.True(job.IsIncomplete);
            Assert.Contains(SelfReportedIncompleteDetector.Reason, job.IncompleteReasons());
            Assert.Null(job.SelfReportNote);
        }

        [Theory]
        [InlineData("Directory.Build.props", true)]
        [InlineData("Directory.Build.targets", true)]
        [InlineData("Directory.Packages.props", true)]
        [InlineData("Foo.csproj", true)]
        [InlineData("Custom.props", true)]
        [InlineData("Custom.targets", true)]
        [InlineData("global.json", true)]
        [InlineData("appsettings.json", true)]
        [InlineData("appsettings.Development.json", true)]
        [InlineData("DevMind.slnx", true)]
        [InlineData("Legacy.sln", true)]
        [InlineData("Widget.cs", false)]
        [InlineData("settings.json", false)]
        [InlineData("README.md", false)]
        public void IsBuildAffecting(string fileName, bool expected)
        {
            Assert.Equal(expected, AgentJobManager.IsBuildAffecting(fileName));
        }

        [Fact]
        public void FirstBuildAffectingChange_ReadsTheJournalDetailShapes()
        {
            HeadlessAgentResult Result(params HostAction[] actions) => new() { Actions = actions };
            HostAction A(string kind, string detail, bool ok = true) => new() { Kind = kind, Detail = detail, Success = ok };

            Assert.Equal("Foo.csproj", AgentJobManager.FirstBuildAffectingChange(Result(
                A("save", @"C:\r\Widget.cs (12 lines)"),
                A("save", @"C:\r\Foo\Foo.csproj (new, 30 lines)"))));
            Assert.Equal("Directory.Build.props", AgentJobManager.FirstBuildAffectingChange(Result(
                A("patch", @"C:\r\Directory.Build.props"))));
            Assert.Equal("appsettings.json", AgentJobManager.FirstBuildAffectingChange(Result(
                A("append", @"C:\r\appsettings.json (created)"))));
            Assert.Equal("New.csproj", AgentJobManager.FirstBuildAffectingChange(Result(
                A("rename", @"C:\r\Old.txt → C:\r\New.csproj"))));
            Assert.Null(AgentJobManager.FirstBuildAffectingChange(Result(
                A("shell", "dotnet build Foo.csproj"),                 // not a file change
                A("patch", @"C:\r\Foo.csproj", ok: false))));          // failed write
        }
    }
}
