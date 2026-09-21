// File: TrainingCaptureGateTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Direct assertion that TrainingCapture.Disabled actually BLOCKS corpus capture —
// not merely that the flag is set (TrainingCapture_IsDisabledForThisTestAssembly
// pins only the flag, which is what let mutation M4 — gate removed — survive).
//
// Hermetic: DEVMIND_GLOBAL_DIR redirects TuiConfig.Load() to a temp directory
// holding a devmind.json with trainingLogEnabled=true and a temp corpus folder,
// so the operator's real %APPDATA%\devmind\devmind.json is never read. The turn
// runs against a loopback FakeSseServer (task_done immediately), and a snapshot
// of the operator's real corpus folder proves nothing leaked there either.
//
// Two tests, paired as a real guard:
//   * GateOn (the assembly default from TrainingCaptureInitializer): after a full
//     headless turn, the configured corpus folder must be EMPTY (in fact not even
//     created — the logger only creates it on first write).
//   * GateOff (the positive control): the same run with the gate explicitly
//     cleared must produce exactly ONE training file in that folder. Without this,
//     the GateOn assertion would also pass for a broken config seam — an empty
//     folder because nothing was ever wired up, not because the gate blocked it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class TrainingCaptureGateTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _configDir;
        private readonly string _corpusFolder;
        private readonly string? _priorGlobalDir;
        private readonly string? _priorServerType;

        public TrainingCaptureGateTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_tcg_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);

            // The hermetic config: a global dir that holds ONLY this test's
            // devmind.json. TuiConfig.Load() → DevMindPaths.GlobalDir →
            // DEVMIND_GLOBAL_DIR → _configDir, so the operator's real config is
            // never read and never written.
            _configDir = Path.Combine(_dir, "config");
            Directory.CreateDirectory(_configDir);
            _corpusFolder = Path.Combine(_dir, "corpus");
            File.WriteAllText(Path.Combine(_configDir, "devmind.json"),
                "{\"trainingLogEnabled\":true,\"trainingLogFolder\":\""
                + _corpusFolder.Replace("\\", "\\\\") + "\"}");

            _priorGlobalDir = Environment.GetEnvironmentVariable("DEVMIND_GLOBAL_DIR");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");

            // Set for the lifetime of this test instance: the gate decision is made in
            // the HeadlessSession constructor, which TuiConfig.Load() resolves the
            // config from — so the redirect must be in place before that ctor runs.
            // Restore (and the flag, which the positive control may clear) in Dispose.
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _configDir);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
        }

        public void Dispose()
        {
            // Restore env vars, then the flag the positive control may have cleared.
            SetEnv("DEVMIND_GLOBAL_DIR", _priorGlobalDir);
            SetEnv("DEVMIND_SERVER_TYPE", _priorServerType);
            TrainingCapture.Disabled = true; // assembly default (the initializer set it)
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public async Task GateOn_HeadlessTurn_WritesNothingToConfiguredCorpusFolder()
        {
            // This assembly's [ModuleInitializer] set the gate before any test ran.
            Assert.True(TrainingCapture.Disabled);
            var files = await RunHeadlessTurnAsync();

            // The gate blocked logger construction, so no file AND no folder —
            // JsonlTrainingLogger creates the folder only on its first LogTurn.
            Assert.Empty(files);
            Assert.False(Directory.Exists(_corpusFolder),
                $"corpus folder {_corpusFolder} was created — the logger ran despite the gate");
        }

        [Fact]
        public async Task GateOff_HeadlessTurn_WritesExactlyOneFileToConfiguredCorpusFolder()
        {
            // Positive control: with the gate explicitly cleared, the SAME hermetic
            // config must produce exactly one training file for the session id.
            // This is what makes the GateOn test a real guard: if the config seam
            // were broken (env override not honoured, JSON not parsed, …) the
            // GateOn folder would be empty for the wrong reason and this test
            // would fail — catching a vacuous pass.
            TrainingCapture.Disabled = false;
            try
            {
                var files = await RunHeadlessTurnAsync();
                Assert.Single(files);
                Assert.StartsWith("training_job_tcg_", Path.GetFileName(files[0]));
                Assert.Single(File.ReadAllLines(files[0])); // the turn's JSONL entry
            }
            finally
            {
                TrainingCapture.Disabled = true;
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static void SetEnv(string name, string? value) =>
            Environment.SetEnvironmentVariable(name, value);

        /// <summary>
        /// Runs one full headless turn (task_done immediately) against the
        /// hermetic temp config and returns the files the configured corpus
        /// folder holds afterwards. Also proves nothing leaked into the
        /// operator's real corpus folder (read-only snapshot; never written).
        /// </summary>
        private async Task<List<string>> RunHeadlessTurnAsync()
        {
            string realCorpus = "G:\\DevMind_Tracing";
            int realCorpusFilesBefore = Directory.Exists(realCorpus)
                ? Directory.EnumerateFiles(realCorpus, "*.jsonl").Count()
                : 0;

            using var server = new FakeSseServer();
            server.SseQueue.Add(FakeSseServer.BuildToolCallSse("task_done",
                "{\"summary\":\"done\"}"));

            using var session = new HeadlessSession(
                new HeadlessOptions
                {
                    RequestTimeoutMinutes = 1,
                    FirstTokenTimeoutMinutes = 1,
                    ManualContextSize = 32768, // skip context probes
                    AgenticLoopMaxDepth = 5,
                },
                server.BaseUrl, apiKey: null!,
                workingDirectory: _dir,
                buildCommand: "dotnet build",
                sessionId: "job_tcg_gate",
                promptFilePath: Path.Combine(_dir, "no-system-prompt.md"));

            var result = await session.RunTurnAsync("p", ct: CancellationToken.None);
            Assert.Null(result.Error);

            var files = Directory.Exists(_corpusFolder)
                ? Directory.GetFiles(_corpusFolder).ToList()
                : new List<string>();

            int realCorpusFilesAfter = Directory.Exists(realCorpus)
                ? Directory.EnumerateFiles(realCorpus, "*.jsonl").Count()
                : 0;
            Assert.True(realCorpusFilesBefore == realCorpusFilesAfter,
                "files appeared in the operator's real corpus folder "
                + realCorpus + ": before=" + realCorpusFilesBefore + " after=" + realCorpusFilesAfter);

            return files;
        }
    }
}
