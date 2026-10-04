// File: HeadlessDepthAutoExtendTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Depth-cap auto-extension, end to end through the real HeadlessSession loop.
//
// A job that reaches max_depth while still converging gets its cap raised in place — no
// finish-up directive at the old cap, no context reset — and the extension is recorded. A
// job repeating one failure is not extended and behaves exactly as before; so does any job
// with auto-extension off. A caller override steer resets the convergence window — progress
// before the redirect no longer counts — and extension stays armed.
//
// The converging model is scripted against FakeSseServer as an edit / failing-build cycle:
// create a file (a mutation), then run a "build" that fails with a NEW error each time —
// a different failure after a change, i.e. one resolution per cycle.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class HeadlessDepthAutoExtendTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorServerType;

        public HeadlessDepthAutoExtendTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_autoext_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        // Never created: keeps the developer's real system-prompt file out of the request.
        private string NoPromptFile => Path.Combine(_dir, "no-system-prompt.md");

        private static HeadlessOptions Options(int maxDepth, bool autoExtend, int maxExtensions = 2) => new()
        {
            RequestTimeoutMinutes = 1,
            FirstTokenTimeoutMinutes = 1,
            ManualContextSize = 32768,
            AgenticLoopMaxDepth = maxDepth,
            AutoExtendDepth = autoExtend,
            MaxDepthExtensions = maxExtensions,
        };

        private static string FinishUp(int cap) => $"LAST iteration before the {cap}-iteration cap";

        // Edit, then a build that fails with a different error each cycle.
        private static FakeSseServer ConvergingModel(int cycles = 40)
        {
            var server = new FakeSseServer { RepeatLastWhenExhausted = true };
            for (int i = 0; i < cycles; i++)
            {
                server.SseQueue.Add(FakeSseServer.BuildToolCallSse("create_file",
                    $"{{\"filename\":\"fix{i}.txt\",\"content\":\"fix {i}\"}}"));
                server.SseQueue.Add(FakeSseServer.BuildToolCallSse("run_shell",
                    $"{{\"command\":\"Write-Output 'error CS{1000 + i}: problem {i}'; exit 1\"}}"));
            }
            return server;
        }

        // Edit, then a build that fails with the SAME error every time.
        private static FakeSseServer RepeatingModel()
        {
            var server = new FakeSseServer { RepeatLastWhenExhausted = true };
            for (int i = 0; i < 20; i++)
            {
                server.SseQueue.Add(FakeSseServer.BuildToolCallSse("create_file",
                    $"{{\"filename\":\"try{i}.txt\",\"content\":\"try {i}\"}}"));
                server.SseQueue.Add(FakeSseServer.BuildToolCallSse("run_shell",
                    "{\"command\":\"Write-Output 'error CS0246: Foo not found'; exit 1\"}"));
            }
            return server;
        }

        private static int Occurrences(IEnumerable<string> bodies, string needle)
            => bodies.Sum(b => { int n = 0, i = 0; while ((i = b.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; } return n; });

        // The request that first carries the needle (bodies are conversation prefixes).
        private static int FirstBodyWith(IReadOnlyList<string> bodies, string needle)
        {
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].Contains(needle, StringComparison.Ordinal)) return i;
            return -1;
        }

        [Fact]
        public async Task Converging_IsExtended_FinishUpOnlyAtTheNewCap_AndMaxExtensionsHonoured()
        {
            using var server = ConvergingModel();
            var transcript = new StringBuilder();
            using var session = new HeadlessSession(Options(maxDepth: 10, autoExtend: true, maxExtensions: 1),
                server.BaseUrl, apiKey: null!, workingDirectory: _dir, buildCommand: "dotnet build",
                promptFilePath: NoPromptFile);

            var result = await session.RunTurnAsync("Fix the build.", progress: t => transcript.Append(t));

            Assert.Null(result.Error);
            string log = transcript.ToString();

            // Extended once at the original cap, then declined at the new one (max_extensions = 1).
            var ext = Assert.Single(result.DepthExtensions);
            Assert.Equal(10, ext.AtDepth);
            Assert.Equal(20, ext.NewCap);
            Assert.Contains("failure(s) resolved", ext.SignalSummary);
            Assert.Contains("[AGENTIC] Depth cap reached (10) — still converging (", log);
            Assert.Contains("extending to 20 (extension 1/1).", log);
            Assert.Contains("[AGENTIC] Depth cap reached (20) — not extending: extensions used up (1/1).", log);

            // The finish-up directive was never sent at the original cap, and was sent once at the new one.
            Assert.Equal(0, Occurrences(server.RequestBodies, FinishUp(10)));
            Assert.Equal(1, Occurrences(server.RequestBodies, FinishUp(20)));
            Assert.Equal(server.RequestBodies.Count - 1, FirstBodyWith(server.RequestBodies, FinishUp(20)));

            // It finally stopped on the cap — the new one.
            Assert.True(result.HitDepthCap);
            Assert.Equal(20, result.EffectiveMaxDepth);
            Assert.StartsWith("[INCOMPLETE — iteration cap (20) reached;", result.Answer);

            // The session's own cap is restored for its next turn.
            Assert.Equal(10, session.EffectiveMaxDepth);
        }

        [Fact]
        public async Task RepeatingTheSameFailure_IsNotExtended_AndStopsAsBefore()
        {
            using var server = RepeatingModel();
            var transcript = new StringBuilder();
            using var session = new HeadlessSession(Options(maxDepth: 4, autoExtend: true),
                server.BaseUrl, apiKey: null!, workingDirectory: _dir, buildCommand: "dotnet build",
                promptFilePath: NoPromptFile);

            var result = await session.RunTurnAsync("Fix the build.", progress: t => transcript.Append(t));

            Assert.Null(result.Error);
            Assert.Empty(result.DepthExtensions);
            Assert.True(result.HitDepthCap);
            Assert.Equal(4, result.EffectiveMaxDepth);
            Assert.Contains("[AGENTIC] Depth cap reached (4) — not extending: no failure resolved", transcript.ToString());
            Assert.Equal(1, Occurrences(server.RequestBodies, FinishUp(4)));
            Assert.Equal(server.RequestBodies.Count - 1, FirstBodyWith(server.RequestBodies, FinishUp(4)));
        }

        [Fact]
        public async Task AutoExtendOff_ReproducesTheFixedCapExactly()
        {
            using var server = ConvergingModel();
            var transcript = new StringBuilder();
            using var session = new HeadlessSession(Options(maxDepth: 10, autoExtend: false),
                server.BaseUrl, apiKey: null!, workingDirectory: _dir, buildCommand: "dotnet build",
                promptFilePath: NoPromptFile);

            var result = await session.RunTurnAsync("Fix the build.", progress: t => transcript.Append(t));

            Assert.Null(result.Error);
            Assert.True(result.HitDepthCap);
            Assert.Empty(result.DepthExtensions);
            Assert.Equal(10, result.EffectiveMaxDepth);
            Assert.DoesNotContain("still converging", transcript.ToString());
            Assert.DoesNotContain("not extending", transcript.ToString());
            Assert.Equal(1, Occurrences(server.RequestBodies, FinishUp(10)));
            Assert.Equal(server.RequestBodies.Count - 1, FirstBodyWith(server.RequestBodies, FinishUp(10)));
            Assert.Equal(11, server.RequestBodies.Count);   // iterations 1..10 plus the finish-up one
        }

        [Fact]
        public async Task AnOverrideSteerDuringAnExtension_ResetsTheConvergenceWindow_AndExtensionStaysArmed()
        {
            using var server = ConvergingModel();
            var transcript = new StringBuilder();
            HeadlessSession? session = null;
            bool steered = false;
            session = new HeadlessSession(Options(maxDepth: 10, autoExtend: true, maxExtensions: 2),
                server.BaseUrl, apiKey: null!, workingDirectory: _dir, buildCommand: "dotnet build",
                promptFilePath: NoPromptFile);
            using var owned = session;

            var result = await session.RunTurnAsync("Fix the build.", progress: t =>
            {
                transcript.Append(t);
                // Steer the moment the first extension is granted: the drain at the next
                // boundary folds it (not the last iteration any more) and resets the window.
                if (!steered && t.Contains("extending to 20", StringComparison.Ordinal))
                {
                    steered = true;
                    Assert.True(session.EnqueueSteer("Stop changing code; write up what you have.", SteerMode.Override).Accepted);
                }
            });

            Assert.Null(result.Error);
            Assert.True(steered);
            string log = transcript.ToString();
            Assert.Contains("[STEER] override folded into the prompt", log);
            Assert.Contains("[STEER] override received — the depth-cap auto-extension convergence window was reset.", log);

            // Still armed: the job kept converging after the redirect and earned the second
            // extension — on evidence from iterations 11-20 only. Without the reset the window
            // would hold all 20 iterations (9 resolved, 10 mutations).
            Assert.Equal(2, result.DepthExtensions.Count);
            Assert.Equal(20, result.DepthExtensions[0].NewCap);
            var second = result.DepthExtensions[1];
            Assert.Equal(20, second.AtDepth);
            Assert.Equal(30, second.NewCap);
            Assert.Contains("4 failure(s) resolved, 5 mutation(s) in the last 10 iteration(s)", second.SignalSummary);

            Assert.Contains("[AGENTIC] Depth cap reached (30) — not extending: extensions used up (2/2).", log);
            Assert.Equal(0, Occurrences(server.RequestBodies, FinishUp(20)));
            Assert.Equal(1, Occurrences(server.RequestBodies, FinishUp(30)));
            Assert.True(result.HitDepthCap);
            Assert.Equal(30, result.EffectiveMaxDepth);
            Assert.Contains(result.Actions, a => a.Kind == "steer");
        }
    }
}
