// File: ToolCallNudgesTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Watchlist H-46 / H-47 / H-50 / H-51b: the per-tool-call nudges, driven through
// HarnessNudges.ObserveIteration exactly as HeadlessSession drives them. Command and output
// shapes are taken from the jobs that motivated them — job-1859 (bin\ and deps.json while a
// test was red), job-1858 (404s chasing framework source), job-1954 (Select-Object -Skip
// paging) and job-1866 (one isolation test re-run with a new redirect file each time).

using System.Text.RegularExpressions;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ToolCallNudgesTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────────

        private static ToolCallEvidence Call(string name, string result = "", int? exit = null, params (string k, string v)[] args)
            => new ToolCallEvidence
            {
                Name = name,
                Arguments = args.ToDictionary(a => a.k, a => a.v),
                Result = result,
                ExitCode = exit,
            };

        private static ToolCallEvidence Shell(string command, string output = "", int exit = 0)
            => Call("run_shell", output, exit, ("command", command));

        private static ToolCallEvidence Read(string path) => Call("read_file", "content", null, ("filename", path));

        private static ToolCallEvidence Fetch(string result, string tool = "web_fetch")
            => Call(tool, result, null, ("url", "https://github.com/dotnet/aspnetcore/blob/main/x.cs"));

        private const string TestsRed =
            "  Failed Foo.Tests.LogsPageTests.Page_HidesButton [12 ms]\n" +
            "Failed!  - Failed:     1, Passed:    36, Skipped:     0, Total:    37, Duration: 4 s - Foo.Tests.dll (net10.0)\n";

        private const string TestsGreen =
            "Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 4 s - Foo.Tests.dll (net10.0)\n";

        private static ToolCallEvidence DotnetTest(string output, int exit) =>
            Shell("dotnet test Foo.Tests\\Foo.Tests.csproj", output, exit);

        // Feeds each call as its own iteration and collects every nudge.
        private static List<string> Run(HarnessNudges n, params ToolCallEvidence[] calls)
        {
            var all = new List<string>();
            foreach (var c in calls) all.AddRange(n.ObserveIteration("", c.Result, new[] { c }));
            return all;
        }

        private static int Count(List<string> nudges, string message) => nudges.Count(m => m == message);

        // ── H-46: build output as evidence ───────────────────────────────────────

        [Theory]
        [InlineData("read_file", "filename", @"C:\repo\src\App\bin\Debug\net10.0\App.deps.json")]
        [InlineData("read_file", "filename", @"C:\repo\src\App\obj\project.assets.json")]
        [InlineData("grep_file", "filename", @"C:\repo\out\App.runtimeconfig.json")]
        [InlineData("read_file", "filename", @"C:\repo\lib\Vendor.dll")]
        [InlineData("read_file", "filename", @"C:\repo\lib\Vendor.pdb")]
        [InlineData("find_in_files", "glob", "**/bin/**/*.json")]
        [InlineData("list_files", "glob", "src/App/obj/*")]
        public void H46_ReadingBuildOutputWhileTestsAreRed_FiresOnce(string tool, string arg, string target)
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                DotnetTest(TestsRed, 1),
                Call(tool, "x", null, (arg, target)),
                Call(tool, "x", null, (arg, target)));

            Assert.Equal(1, Count(nudges, ToolCallNudges.BuildOutputMessage));
        }

        [Theory]
        [InlineData(@"Get-ChildItem ""C:\repo\src\App\bin\Debug\net10.0"" -Filter ""appsettings*.json"" | ForEach-Object { Get-Content $_.FullName }")]
        [InlineData(@"Select-String -Path C:\repo\out\App.deps.json -Pattern ""Microsoft.AspNetCore""")]
        [InlineData(@"type obj\Debug\net10.0\App.AssemblyInfo.cs")]
        public void H46_AShellCommandThatReadsBuildOutput_Counts(string command)
        {
            var n = new HarnessNudges();
            var nudges = Run(n, DotnetTest(TestsRed, 1), Shell(command, "listing"));
            Assert.Equal(1, Count(nudges, ToolCallNudges.BuildOutputMessage));
        }

        [Fact]
        public void H46_OnAGreenJob_ReadingBinDoesNotFire()
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                DotnetTest(TestsGreen, 0),
                Read(@"C:\repo\src\App\bin\Debug\net10.0\App.deps.json"));
            Assert.Empty(nudges);
        }

        [Fact]
        public void H46_BeforeAnyBuildOrTestRan_DoesNotFire()
        {
            Assert.Empty(Run(new HarnessNudges(), Read(@"C:\repo\src\App\bin\Debug\App.dll")));
        }

        [Fact]
        public void H46_AfterTheTestsGoGreenAgain_DoesNotFire()
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                DotnetTest(TestsRed, 1),
                DotnetTest(TestsGreen, 0),
                Read(@"C:\repo\src\App\bin\Debug\App.deps.json"));
            Assert.Empty(nudges);
        }

        [Fact]
        public void H46_AFailingBuildCountsToo_AndSourceReadsDoNot()
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                Call("run_build", "Build FAILED.", 1),
                Read(@"C:\repo\src\App\Program.cs"),                       // source: not build output
                Shell(@"dotnet build -o bin\probe", "Build FAILED.", 1));   // a build, not a read
            Assert.Empty(nudges);

            nudges = Run(n, Read(@"C:\repo\src\App\obj\Debug\App.dll"));
            Assert.Equal(new[] { ToolCallNudges.BuildOutputMessage }, nudges);
        }

        [Fact]
        public void H46_ARedTestRunPrintedThroughSelectString_StillCountsAsRed()
        {
            // job-1866-style: the run is redirected and the model prints its own excerpt.
            var n = new HarnessNudges();
            var nudges = Run(n,
                Shell(@"dotnet test Foo.Tests.csproj 2>&1 | Out-File C:\t\a.txt; Select-String -Path C:\t\a.txt -Pattern Failed",
                      "L57: Failed!  - Failed:     1, Passed:    36, Skipped:     0, Total:    37, Duration: 4 s - Foo.Tests.dll", 0),
                Read(@"C:\repo\bin\Debug\Foo.deps.json"));
            Assert.Equal(1, Count(nudges, ToolCallNudges.BuildOutputMessage));
        }

        // ── H-47: failed fetches ─────────────────────────────────────────────────

        [Fact]
        public void H47_TwoFailedFetchesInARow_FireOnTheSecond_Once()
        {
            var n = new HarnessNudges();
            Assert.Empty(Run(n, Fetch("[web_fetch error] 404 NotFound")));
            var nudges = Run(n,
                Fetch("404: Not Found"),
                Fetch("[web_fetch error] 404 NotFound"),
                Fetch("[web_fetch error] 404 NotFound"));
            Assert.Equal(new[] { ToolCallNudges.StopFetchingMessage }, nudges);
        }

        [Fact]
        public void H47_ASuccessfulFetchResetsTheCount()
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                Fetch("[web_fetch error] 404 NotFound"),
                Fetch("namespace Microsoft.AspNetCore.Hosting.Server.Features { public class ServerAddressesFeature ... }"),
                Fetch("[web_fetch error] 404 NotFound"));
            Assert.Empty(nudges);
        }

        [Fact]
        public void H47_OtherToolsBetweenFetchesDoNotReset_AndLearnFetchCounts()
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                Fetch("[learn_fetch] No content extracted from https://learn.microsoft.com/x", "learn_fetch"),
                Read(@"C:\repo\src\App\Program.cs"),
                Fetch(""));
            Assert.Equal(new[] { ToolCallNudges.StopFetchingMessage }, nudges);
        }

        [Theory]
        [InlineData("[web_fetch error] 404 NotFound", true)]
        [InlineData("[web_fetch error] Timed out after 30s fetching https://x", true)]
        [InlineData("[learn_fetch error] No response from Learn MCP server for https://x", true)]
        [InlineData("[web_fetch] No content extracted from https://x", true)]
        [InlineData("404: Not Found", true)]
        [InlineData("   ", true)]
        [InlineData("public sealed class ServerAddressesFeature : IServerAddressesFeature { }", false)]
        public void H47_IsFailedFetch(string result, bool expected)
        {
            Assert.Equal(expected, ToolCallNudges.IsFailedFetch(result));
        }

        [Fact]
        public void H47_ALongPageThatMentionsNotFound_IsNotAFailure()
        {
            string page = "Returns 404 Not Found when the key is missing. " + new string('x', 400);
            Assert.False(ToolCallNudges.IsFailedFetch(page));
        }

        // ── H-50: paging shell output ────────────────────────────────────────────

        private static ToolCallEvidence Page(int skip) =>
            Shell($"git show HEAD:src/App/Program.cs | Select-String -Pattern 'Map' | Select-Object -Skip {skip} -First 3", "...");

        [Fact]
        public void H50_ThreePagingCallsOverOneSource_FireOnTheThird_Once()
        {
            var n = new HarnessNudges();
            Assert.Empty(Run(n, Page(13), Page(16)));
            var nudges = Run(n, Page(19), Page(22));
            Assert.Equal(new[] { ToolCallNudges.PagingMessage }, nudges);
        }

        [Fact]
        public void H50_ADifferentSourceStartsOver()
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                Page(13), Page(16),
                Shell("git show HEAD:src/App/Startup.cs | Select-Object -Skip 19 -First 3", "..."));
            Assert.Empty(nudges);
        }

        [Fact]
        public void H50_AnotherToolCallBetweenBreaksTheRun()
        {
            var n = new HarnessNudges();
            var nudges = Run(n, Page(13), Page(16), Read(@"C:\repo\src\App\Program.cs"), Page(19));
            Assert.Empty(nudges);
        }

        [Fact]
        public void H50_TheSameCommandThreeTimesIsNotPaging()
        {
            var n = new HarnessNudges();
            var nudges = Run(n, Page(13), Page(13), Page(13));
            Assert.DoesNotContain(ToolCallNudges.PagingMessage, nudges);
        }

        [Theory]
        [InlineData("Get-Content a.log | Select-Object -Skip 40 -First 20", "Get-Content a.log | Select-Object -Skip # -First #")]
        [InlineData("Get-Content a.log -TotalCount 120", "Get-Content a.log -TotalCount #")]
        [InlineData("Get-Content a.log | Select-Object -Index 3,4,5", "Get-Content a.log | Select-Object -Index #")]
        [InlineData("cat a.log | head -n 40 | tail -n 20", "cat a.log | head -n # | tail -n #")]
        [InlineData("cat a.log | tail -20", "cat a.log | tail -#")]
        [InlineData("Get-Content a.log", null)]
        public void H50_PagingKey(string command, string? expected)
        {
            Assert.Equal(expected, ToolCallNudges.PagingKey(command));
        }

        // ── H-51b: same run, same result ─────────────────────────────────────────

        // job-1866's shape: the same --filter run, output redirected to a new file each time,
        // and the model printing its own excerpt (line numbers and durations differ).
        private static ToolCallEvidence Iso(string file, int line, int seconds, string filter = "FullyQualifiedName~TestDataRootIsolationTests",
            int failed = 0, int passed = 3)
        {
            string cmd =
                $"dotnet test \"C:\\Users\\p\\source\\repos\\VLink.Warehouses\\tests\\Svc.Tests\\Svc.Tests.csproj\" --filter \"{filter}\" " +
                $"--nologo -l \"console;verbosity=detailed\" 2>&1 | Out-File -Encoding utf8 C:\\Users\\p\\AppData\\Local\\Temp\\devmind\\{file}; " +
                $"Select-String -Path C:\\Users\\p\\AppData\\Local\\Temp\\devmind\\{file} -Pattern \"Hosting environment|Passed|Failed\" | ForEach-Object {{ \"L$($_.LineNumber): $($_.Line.Trim())\" }}";
            string summary = failed == 0 ? "Passed!" : "Failed!";
            string output =
                $"L{line}: Hosting environment: Staging\n" +
                $"L{line + 40}: {summary}  - Failed:     {failed}, Passed:     {passed}, Skipped:     0, Total:     {failed + passed}, Duration: {seconds} s - Svc.Tests.dll (net10.0)";
            return Shell(cmd, output, 0);
        }

        [Fact]
        public void H51b_Job1866Replay_SameFilterDifferentRedirectFiles_FiresOnTheThird_Once()
        {
            var n = new HarnessNudges();
            Assert.Empty(Run(n, Iso("isoA.txt", 12, 2), Iso("isoB.txt", 14, 3)));
            var nudges = Run(n, Iso("isoC.txt", 12, 2), Iso("isoD.txt", 13, 4), Iso("isoE.txt", 12, 2));
            Assert.Equal(new[] { ToolCallNudges.SameResultMessage }, nudges);
        }

        [Fact]
        public void H51b_ADifferentFilterStartsOver()
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                Iso("isoA.txt", 12, 2), Iso("isoB.txt", 12, 2),
                Iso("isoC.txt", 12, 2, filter: "FullyQualifiedName~LogsPageTests"));
            Assert.Empty(nudges);

            nudges = Run(n,
                Iso("isoD.txt", 12, 2, filter: "FullyQualifiedName~LogsPageTests"),
                Iso("isoE.txt", 12, 2, filter: "FullyQualifiedName~LogsPageTests"));
            Assert.Equal(new[] { ToolCallNudges.SameResultMessage }, nudges);
        }

        [Fact]
        public void H51b_AChangedOutcomeStartsOver()
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                Iso("isoA.txt", 12, 2), Iso("isoB.txt", 12, 2),
                Iso("isoC.txt", 12, 2, passed: 4));
            Assert.Empty(nudges);
        }

        [Fact]
        public void H51b_AFileChangeBetweenRunsStartsOver()
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                Iso("isoA.txt", 12, 2), Iso("isoB.txt", 12, 2),
                Call("patch_file", "[PATCH applied to C:\\repo\\Program.cs]", null, ("filename", @"C:\repo\Program.cs")),
                Iso("isoC.txt", 12, 2), Iso("isoD.txt", 12, 2));
            Assert.Empty(nudges);
        }

        [Fact]
        public void H51b_ReadsBetweenRunsDoNotBreakTheStreak()
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                Iso("isoA.txt", 12, 2), Read(@"C:\repo\tests\Svc.Tests\Fixture.cs"),
                Iso("isoB.txt", 12, 2), Iso("isoC.txt", 12, 2));
            Assert.Equal(new[] { ToolCallNudges.SameResultMessage }, nudges);
        }

        [Theory]
        [InlineData("dotnet test Svc.Tests.csproj --filter \"FullyQualifiedName~Iso\" > C:\\t\\a.txt; Get-Content C:\\t\\a.txt")]
        [InlineData("dotnet test Svc.Tests.csproj --filter \"FullyQualifiedName~Iso\" *> b.txt")]
        [InlineData("$env:ASPNETCORE_ENVIRONMENT=\"Staging\"; dotnet test Svc.Tests.csproj --filter \"FullyQualifiedName~Iso\" 2>&1 | Tee-Object c.txt")]
        [InlineData("dotnet test \"Svc.Tests.csproj\" -v q --nologo --filter:FullyQualifiedName~Iso -l \"console;verbosity=detailed\"")]
        [InlineData("dotnet test Svc.Tests.csproj --environment ASPNETCORE_ENVIRONMENT=Staging --filter 'FullyQualifiedName~ISO'")]
        public void H51b_TestKey_IgnoresRedirectsEnvLoggersAndCase(string command)
        {
            Assert.Equal("svc.tests.csproj | fullyqualifiedname~iso",
                ToolCallNudges.TestKey(new ToolCallEvidence { Name = "run_shell" }, command));
        }

        [Fact]
        public void H51b_TestKey_TheFilterKeepsItsPipeAlternatives()
        {
            Assert.Equal("svc.tests.csproj | fullyqualifiedname~a|fullyqualifiedname~b",
                ToolCallNudges.TestKey(new ToolCallEvidence { Name = "run_shell" },
                    "dotnet test Svc.Tests.csproj --filter \"FullyQualifiedName~A|FullyQualifiedName~B\" | Select-String Passed"));
        }

        [Fact]
        public void H51b_RunTestsTool_SameProjectAndFilter_Fires()
        {
            ToolCallEvidence RunTests() =>
                Call("run_tests", TestsGreen, 0, ("project", "Foo.Tests.csproj"), ("filter", "FullyQualifiedName~Bar"));
            var n = new HarnessNudges();
            Assert.Equal(new[] { ToolCallNudges.SameResultMessage }, Run(n, RunTests(), RunTests(), RunTests()));
        }

        [Fact]
        public void H51b_OtherShellCommands_SameNormalizedCommandAndOutput_Fire()
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                Shell(@"[Environment]::GetEnvironmentVariable('X','User') > C:\Users\p\AppData\Local\Temp\x1.txt; Get-Content C:\Users\p\AppData\Local\Temp\x1.txt", "Staging"),
                Shell(@"[Environment]::GetEnvironmentVariable('X','User') > C:\Users\p\AppData\Local\Temp\x2.txt; Get-Content C:\Users\p\AppData\Local\Temp\x2.txt", "Staging"),
                Shell(@"[Environment]::GetEnvironmentVariable('X','User') > C:\Users\p\AppData\Local\Temp\x3.txt; Get-Content C:\Users\p\AppData\Local\Temp\x3.txt", "Staging"));
            Assert.Equal(new[] { ToolCallNudges.SameResultMessage }, nudges);
        }

        [Fact]
        public void H51b_OtherShellCommands_DifferentOutput_DoNotFire()
        {
            var n = new HarnessNudges();
            var nudges = Run(n,
                Shell("git status --short", " M a.cs"),
                Shell("git status --short", " M a.cs\n M b.cs"),
                Shell("git status --short", " M a.cs"));
            Assert.Empty(nudges);
        }

        [Fact]
        public void H51b_AndH58_DoNotBothFire_ForTheSameRepeatedFailingTests()
        {
            var nudges = new HarnessNudges();
            var h58 = new RepeatedTestFailureGuard();
            var fired = new List<string>();
            IReadOnlyList<string>? h58Fired = null;

            for (int i = 0; i < 4; i++)
            {
                var run = DotnetTest(TestsRed, 1);
                fired.AddRange(nudges.ObserveIteration("", run.Result, new[] { run }));
                h58Fired ??= h58.Observe(TestRunOutcome.Parse(run.Result));
            }

            Assert.NotNull(h58Fired);                                   // H-58 owns this loop
            Assert.DoesNotContain(ToolCallNudges.SameResultMessage, fired);
        }

        [Fact]
        public void H51b_ARepeatedCompileError_IsLeftToH25()
        {
            var n = new HarnessNudges();
            const string output = "Probe.cs(14,17): error CS1061: 'Control' does not contain a definition for 'AutoScaleMode'";
            var nudges = Run(n,
                Shell("dotnet build Probe.csproj", output, 1),
                Shell("dotnet build Probe.csproj", output, 1),
                Shell("dotnet build Probe.csproj", output, 1));
            Assert.Equal(new[] { HarnessNudges.RepeatedCompileErrorMessage + "\nError: CS1061 'AutoScaleMode'" }, nudges);
        }

        // ── Per turn ─────────────────────────────────────────────────────────────

        [Fact]
        public void TheGuardsArePerTurn()
        {
            var n = new HarnessNudges();
            Assert.Single(Run(n, Fetch(""), Fetch("")));
            Assert.Empty(Run(n, Fetch(""), Fetch("")));     // once per key per turn

            n.ResetToolCallGuards();
            Assert.Empty(Run(n, Fetch("")));                 // the count started over
            Assert.Single(Run(n, Fetch("")));
        }

        [Fact]
        public void WithoutToolCalls_OnlyTheTextNudgesRun()
        {
            var n = new HarnessNudges();
            for (int i = 0; i < 3; i++)
                Assert.Empty(n.ObserveIteration("", "", null));
        }
    }

    // End to end: HeadlessSession builds the per-call evidence from the real iteration and folds
    // the nudge into the next prompt once, journalled as "nudge".
    public class ToolCallNudgesSessionTests : IDisposable
    {
        private readonly string _dir;
        private string NoPromptFile => Path.Combine(_dir, "no-system-prompt.md");

        public ToolCallNudgesSessionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_nudge_calls_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public async Task PagingShellOutput_NudgeInjectedOnce_AndJournalled()
        {
            if (!OperatingSystem.IsWindows() || !ShellRunner.IsPowerShellAvailable())
                return; // the scripted commands run through PowerShell

            using var server = new FakeSseServer { RepeatLastWhenExhausted = true };
            foreach (int skip in new[] { 1, 2, 3, 4, 5 })
                server.SseQueue.Add(FakeSseServer.BuildToolCallSse("run_shell",
                    $"{{\"command\":\"1..40 | Select-Object -Skip {skip} -First 2\"}}"));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"Read it.\"}"));

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                var result = await HeadlessAgent.RunAsync(
                    "Read lines 1-12 of the range.",
                    new HeadlessOptions
                    {
                        RequestTimeoutMinutes = 1,
                        FirstTokenTimeoutMinutes = 1,
                        ManualContextSize = 32768,
                        AgenticLoopMaxDepth = 10,
                    },
                    server.BaseUrl, apiKey: null!, workingDirectory: _dir, buildCommand: "dotnet build",
                    ct: CancellationToken.None, promptFilePath: NoPromptFile);

                Assert.Null(result.Error);
                var nudge = Assert.Single(result.Actions, a => a.Kind == "nudge");
                Assert.Contains("Use read_file with start_line/end_line", nudge.Detail);

                string last;
                lock (server.RequestBodies) last = server.RequestBodies[^1];
                Assert.Single(Regex.Matches(last, Regex.Escape("Use read_file with start_line/end_line")));
                Assert.Contains("[HARNESS GUARD] Use read_file", last);   // framed as the harness, not a steer
                lock (server.RequestBodies)
                    for (int i = 0; i < 3; i++)
                        Assert.DoesNotContain("Use read_file with start_line/end_line", server.RequestBodies[i]);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }
    }
}
