// File: BuildLockVerificationTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-68: the harness's verification rebuild failing — or warning — only because the user's running
// app holds the output files is the environment, not the agent's code.
//
// job-2152's code was clean, but the rebuild hit MSB3026 copy retries and MSB3027 / MSB3021 copy
// errors on files locked by devenv and VLink.PDFSanitizerConfig.exe, and the job ended
// stopped_incomplete with "51 warnings". Real jobs, real resolution and rebuild rewrite and parse;
// only the build process is replaced (BuildExecOverride), fed MSBuild's actual message shapes.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class BuildLockVerificationTests : IDisposable
    {
        private const string Targets = @"C:\Program Files\dotnet\sdk\10.0.302\Microsoft.Common.CurrentVersion.targets";
        private const string Proj = @" [C:\src\App\App.csproj]";
        private const string Lockers = "devenv.exe (116488), VLink.PDFSanitizerConfig.exe (83868)";

        private static string Retry(int file, int retry) =>
            $"{Targets}(5096,5): warning MSB3026: Could not copy \"C:\\src\\Shared\\bin\\Debug\\net10.0\\Shared{file}.dll\" to " +
            $"\"bin\\Debug\\net10.0\\Shared{file}.dll\". Beginning retry {retry} in 1000ms. The process cannot access the file " +
            $"'C:\\src\\App\\bin\\Debug\\net10.0\\Shared{file}.dll' because it is being used by another process. " +
            $"The file is locked by: \"{Lockers}\"" + Proj;

        private static string Exceeded(int file) =>
            $"{Targets}(5096,5): error MSB3027: Could not copy \"C:\\src\\Shared\\bin\\Debug\\net10.0\\Shared{file}.dll\" to " +
            $"\"bin\\Debug\\net10.0\\Shared{file}.dll\". Exceeded retry count of 10. Failed. The file is locked by: \"{Lockers}\"" + Proj;

        private static string UnableToCopy(int file) =>
            $"{Targets}(5096,5): error MSB3021: Unable to copy file \"C:\\src\\Shared\\bin\\Debug\\net10.0\\Shared{file}.dll\" to " +
            $"\"bin\\Debug\\net10.0\\Shared{file}.dll\". The process cannot access the file " +
            $"'C:\\src\\App\\bin\\Debug\\net10.0\\Shared{file}.dll' because it is being used by another process." + Proj;

        private const string CsWarning = @"C:\src\App\Model.cs(12,19): warning CS8618: Non-nullable property 'Name' must contain a non-null value when exiting constructor." + Proj;
        private const string CsError = @"C:\src\App\Program.cs(4,9): error CS0103: The name 'Baz' does not exist in the current context" + Proj;

        // MSBuild's shape: every diagnostic inline, the summary marker, every diagnostic again, the counts.
        private static string Output(IReadOnlyList<string> diagnostics, bool failed)
        {
            var sb = new StringBuilder("  Determining projects to restore...\r\n");
            foreach (string d in diagnostics) sb.Append(d).Append("\r\n");
            sb.Append(failed ? "\r\nBuild FAILED.\r\n\r\n" : "\r\nBuild succeeded.\r\n\r\n");
            foreach (string d in diagnostics) sb.Append(d).Append("\r\n");
            int w = diagnostics.Count(d => d.Contains(": warning ")), e = diagnostics.Count(d => d.Contains(": error "));
            sb.Append($"    {w} Warning(s)\r\n    {e} Error(s)\r\n\r\nTime Elapsed 00:00:37.01\r\n");
            return sb.ToString();
        }

        private static List<string> FiftyOneRetries() =>
            Enumerable.Range(1, 51).Select(i => Retry(file: (i - 1) / 10, retry: (i - 1) % 10 + 1)).ToList();

        private readonly string _dir;
        private readonly Dictionary<string, string?> _priorEnv = new();

        public BuildLockVerificationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h68_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "App.slnx"), "<Solution>\n</Solution>\n");   // resolves `dotnet build`
            foreach (string name in new[] { "DEVMIND_ENDPOINT", "DEVMIND_SERVER_TYPE", "DEVMIND_BUILD_COMMAND", VerificationBuild.RebuildEnvVar })
                _priorEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            Environment.SetEnvironmentVariable("DEVMIND_BUILD_COMMAND", null);
            Environment.SetEnvironmentVariable(VerificationBuild.RebuildEnvVar, null);
        }

        public void Dispose()
        {
            foreach (var kv in _priorEnv) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private async Task<(AgentJob job, JsonElement bv, JsonElement root, int testRuns)> RunJobAsync(
            string buildOutput, int exitCode, bool verifyTests = false)
        {
            using var server = new EditThenDoneLlmServer(Path.Combine(_dir, "newfile.txt"));
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);

            using var mgr = new AgentJobManager();
            mgr.BuildExecOverride = (_, _) => Task.FromResult((buildOutput, exitCode));
            int testRuns = 0;
            mgr.TestRunnerOverride = (_, _) =>
            {
                testRuns++;
                return Task.FromResult(TestVerification.FromRun("dotnet test", 0, CannedTestOutput.McpPass37));
            };

            var job = mgr.Start("p", _dir, 5, 30, allowCommit: false, verifyBuild: true, verifyTests: verifyTests,
                runTestBaseline: false);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 30_000 && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(20);
            Assert.Equal(AgentJobState.Done, job.State);

            using var doc = JsonDocument.Parse(await new AgentTaskTools(mgr).TaskResult(job.Id, CancellationToken.None));
            JsonElement root = doc.RootElement.Clone();
            return (job, root.GetProperty("build_verification"), root, testRuns);
        }

        private static string[] Reasons(JsonElement root) =>
            root.GetProperty("incomplete_reasons").ValueKind == JsonValueKind.Null
                ? Array.Empty<string>()
                : root.GetProperty("incomplete_reasons").EnumerateArray().Select(e => e.GetString()!).ToArray();

        private static string[] LockedBy(JsonElement bv) =>
            bv.GetProperty("locked_by").EnumerateArray().Select(e => e.GetString()!).ToArray();

        [Fact]
        public async Task FiftyOneLockWarnings_AndExit0_AreNotBuildWarnings_TheJobIsDone()
        {
            var (job, bv, root, _) = await RunJobAsync(Output(FiftyOneRetries(), failed: false), 0);

            Assert.Equal("done", root.GetProperty("state").GetString());
            Assert.Empty(Reasons(root));
            Assert.False(job.IsIncomplete);
            Assert.Equal(0, bv.GetProperty("warning_count").GetInt32());
            Assert.Equal(51, bv.GetProperty("lock_warning_count").GetInt32());
            Assert.Equal(0, bv.GetProperty("lock_error_count").GetInt32());
            Assert.False(bv.GetProperty("locked").GetBoolean());
            Assert.Equal(new[] { "devenv.exe (116488)", "VLink.PDFSanitizerConfig.exe (83868)" }, LockedBy(bv));
            Assert.Contains("[verification] File locks: 51 warning(s) and 0 error(s) not counted against the code",
                bv.GetProperty("output_tail").GetString());
        }

        [Fact]
        public async Task Exit1_WithOnlyLockErrors_IsBuildVerificationLocked_NotFailed_AndNotGreen()
        {
            var diagnostics = FiftyOneRetries().Take(10).Concat(new[] { Exceeded(0), UnableToCopy(0) }).ToList();
            var (job, bv, root, testRuns) = await RunJobAsync(Output(diagnostics, failed: true), 1, verifyTests: true);

            Assert.Equal("stopped_incomplete", root.GetProperty("state").GetString());
            Assert.Equal(new[]
            {
                "build_verification_locked",
                "build output is locked by devenv.exe (116488), VLink.PDFSanitizerConfig.exe (83868) — close them and re-run " +
                "verification — this is the environment, not the code",
            }, Reasons(root));
            Assert.True(bv.GetProperty("locked").GetBoolean());
            Assert.Equal(2, bv.GetProperty("lock_error_count").GetInt32());

            // A locked verification is not a green build: no test verification, no H-31 excuse.
            Assert.Equal(0, testRuns);
            Assert.Null(job.Tests);
            Assert.False(job.HarnessTestVerified);
        }

        [Fact]
        public async Task Exit1_WithACompilerErrorAndLockErrors_StaysFailed_RealErrorFirstThenTheLocks()
        {
            var diagnostics = new List<string> { CsError, Retry(0, 1), Exceeded(0), UnableToCopy(0) };
            var (_, bv, root, _) = await RunJobAsync(Output(diagnostics, failed: true), 1);

            string[] reasons = Reasons(root);
            Assert.Equal("build_verification_failed", reasons[0]);
            Assert.DoesNotContain("build_verification_locked", reasons);
            Assert.StartsWith(@"C:\src\App\Program.cs(4,9): error CS0103", reasons[1]);
            int firstLock = Array.FindIndex(reasons, r => r.Contains("MSB30"));
            Assert.True(firstLock > 1, string.Join(" | ", reasons));
            Assert.False(bv.GetProperty("locked").GetBoolean());
        }

        [Fact]
        public async Task ARealWarningPlusLockWarnings_CountsOnlyTheRealWarning()
        {
            var diagnostics = new List<string> { CsWarning }.Concat(FiftyOneRetries().Take(20)).ToList();
            var (_, bv, root, _) = await RunJobAsync(Output(diagnostics, failed: false), 0);

            Assert.Equal(1, bv.GetProperty("warning_count").GetInt32());
            Assert.Equal(20, bv.GetProperty("lock_warning_count").GetInt32());
            Assert.Equal(new[]
            {
                "build_warnings",
                "harness rebuild reported 1 warning(s)",
                @"C:\src\App\Model.cs(12,19): warning CS8618: Non-nullable property 'Name' must contain a non-null value when exiting constructor.",
            }, Reasons(root));
        }
    }
}
