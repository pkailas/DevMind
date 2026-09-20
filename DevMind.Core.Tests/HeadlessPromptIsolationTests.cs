// File: HeadlessPromptIsolationTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Proves the promptFilePath seam on HeadlessSession/HeadlessAgent actually decides what
// reaches the request body, so the headless test suite is hermetic with respect to the
// developer's real %APPDATA%\devmind\system-prompt.md.
//
// Why this file exists: commit 9282676 made BuildSystemPrompt read that global file on
// every turn, with promptFilePath as an override for tests. The override was applied to
// NoExecuteTests but not to HeadlessAgentTests, so those nine tests were assembling their
// prompt from whatever the developer happened to have authored. Nothing failed, because no
// assertion inspected the persona — the suite was green as a property of the machine, not
// of the repo.
//
// These tests never read, move or modify the real file. They prove isolation the only way
// that is safe AND conclusive: by showing the injected path is load-bearing in BOTH
// directions. If an injected file's content appears verbatim, and a nonexistent injected
// path yields options.SystemPrompt instead, then the production default path is provably
// not consulted — whatever it happens to contain on this machine.

using Xunit;

namespace DevMind.Core.Tests
{
    public class HeadlessPromptIsolationTests : IDisposable
    {
        private readonly string _dir;

        public HeadlessPromptIsolationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_promptiso_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private static HeadlessOptions Options() => new HeadlessOptions
        {
            RequestTimeoutMinutes = 1,
            FirstTokenTimeoutMinutes = 1,
            ManualContextSize = 32768, // skip context probes
            AgenticLoopMaxDepth = 5,
            SystemPrompt = "SENTINEL-OPTIONS-PERSONA-2f8c1d",
        };

        /// <summary>Runs one scripted turn and returns the raw body of the single chat
        /// request, which carries the assembled system prompt as message[0].</summary>
        private async Task<string> CaptureRequestBodyAsync(string promptFilePath)
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"done\"}"));

            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = new HeadlessSession(Options(), server.BaseUrl, apiKey: null!,
                    workingDirectory: _dir, buildCommand: "dotnet build",
                    promptFilePath: promptFilePath);
                var result = await session.RunTurnAsync("Say done.", ct: CancellationToken.None);
                Assert.Null(result.Error);
                return Assert.Single(server.RequestBodies);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        // Direction 1: an injected file that EXISTS supplies the base prompt. This is the
        // half that proves the seam is wired at all — if promptFilePath were ignored, the
        // sentinel could not appear.
        [Fact]
        public async Task InjectedPromptFile_ContentReachesTheRequest()
        {
            string promptPath = Path.Combine(_dir, "injected-prompt.md");
            const string sentinel = "SENTINEL-INJECTED-FILE-PERSONA-a41b07";
            File.WriteAllText(promptPath, sentinel);

            string body = await CaptureRequestBodyAsync(promptPath);

            Assert.Contains(sentinel, body);
            // The file REPLACES options.SystemPrompt — it does not append to it.
            Assert.DoesNotContain("SENTINEL-OPTIONS-PERSONA-2f8c1d", body);
        }

        // Direction 2: an injected path that does NOT exist falls back to
        // options.SystemPrompt. Together with direction 1 this pins that the seam alone
        // determines the base prompt, so SystemPromptFile.Path is never consulted — which
        // is exactly the hermeticity the nine HeadlessAgentTests sites now rely on.
        [Fact]
        public async Task NonexistentPromptFile_FallsBackToOptionsSystemPrompt()
        {
            string absent = Path.Combine(_dir, "no-system-prompt.md");
            Assert.False(File.Exists(absent)); // precondition

            string body = await CaptureRequestBodyAsync(absent);

            Assert.Contains("SENTINEL-OPTIONS-PERSONA-2f8c1d", body);
        }

        // The seam must not be bypassable by a path that merely looks empty: a file that
        // exists but is whitespace-only is "absent" per SystemPromptFile.LoadFrom, so it
        // must also fall back rather than sending a blank persona.
        [Fact]
        public async Task WhitespaceOnlyPromptFile_FallsBackToOptionsSystemPrompt()
        {
            string promptPath = Path.Combine(_dir, "blank-prompt.md");
            File.WriteAllText(promptPath, "   \n\t\n  ");

            string body = await CaptureRequestBodyAsync(promptPath);

            Assert.Contains("SENTINEL-OPTIONS-PERSONA-2f8c1d", body);
        }

        // Every HeadlessSession/HeadlessAgent construction in the test suite must pass
        // promptFilePath. This is the guard that stops the next new test from silently
        // reintroducing the leak: it reads the test sources rather than exercising the
        // engine, because the failure mode is an omission at a call site, and an omission
        // is invisible to any runtime assertion.
        [Fact]
        public void EveryHeadlessConstructionInTests_PassesPromptFilePath()
        {
            string testsDir = FindTestsSourceDir();
            var offenders = new List<string>();

            foreach (string file in Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories))
            {
                // Skip build output — bin/obj can hold stale copies of these sources.
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;
                if (Path.GetFileName(file) == nameof(HeadlessPromptIsolationTests) + ".cs")
                    continue; // this file's own helper is the seam under test

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains("new HeadlessSession") &&
                        !lines[i].Contains("HeadlessAgent.RunAsync"))
                        continue;

                    // The call spans several lines; promptFilePath is the last argument.
                    // Scan forward to the closing ");" of the invocation.
                    string call = string.Join("\n", lines.Skip(i).Take(12));
                    int close = call.IndexOf(");", StringComparison.Ordinal);
                    if (close >= 0) call = call.Substring(0, close);

                    if (!call.Contains("promptFilePath"))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
                }
            }

            Assert.True(offenders.Count == 0,
                "These test call sites construct a headless session without promptFilePath, so they " +
                "read the developer's real %APPDATA%\\devmind\\system-prompt.md and the suite's result " +
                "becomes a property of the machine:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>Walks up from the test assembly to the DevMind.Core.Tests source dir.</summary>
        private static string FindTestsSourceDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && dir.Name != "DevMind.Core.Tests")
                dir = dir.Parent;

            Assert.True(dir != null,
                $"could not locate the DevMind.Core.Tests source dir from {AppContext.BaseDirectory}");
            return dir!.FullName;
        }
    }
}
