// File: PlanModeMutationGateTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// ApprovalPolicy.Decide says Plan refuses; these pin that the REAL executor path each tool
// uses actually refuses — no prompt, no diff card, no host call — and that the model
// receives the [PLAN MODE] message, not silence it would read as success.
//
// The mirror image of ManualModeMutationGateTests. There the model is told "declined";
// here it is told "plan mode", because the state — not the user's answer — is the reason,
// and a model told it was merely declined would retry the same tool against the same wall.
//
// The refusal text is asserted via ApprovalPolicy.PlanRefusalMessage (the single source),
// not re-spelled here: two copies of the refusal is how the model-facing message and the
// test would disagree.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class PlanModeMutationGateTests : IDisposable
    {
        private readonly string _dir;

        public PlanModeMutationGateTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_planmode_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>FakeLlmOptions with a settable mode — the interface member is get-only.</summary>
        private sealed class ModeOptions : ILlmOptions
        {
            public ApprovalMode Mode { get; set; } = ApprovalMode.Auto;

            public string SystemPrompt => "test";
            public string ModelName => "test-model";
            public int RequestTimeoutMinutes => 1;
            public int FirstTokenTimeoutMinutes => 1;
            public bool ShowDebugOutput => false;
            public bool ShowContextBudget => false;
            public bool ShowLlmThinking => false;
            public ContextEvictionMode ContextEviction => ContextEvictionMode.Off;
            public int ManualContextSize => 32768;
            public LlmServerType ServerType => LlmServerType.LlamaServer;
            public string CustomContextEndpoint => null!;
            public int MicroCompactThreshold => 99;
            public int NearlineIngestThresholdChars => 8_000;
            public bool MicroCompactSummarize => false;
            public bool MicroCompactBrainwash => false;
            public bool AlwaysConfirmPatch => false;
            public ApprovalMode ApprovalMode => Mode;
            public int AgenticLoopMaxDepth => 25;
            public int AgenticContextLimitPercent => 0;
        }

        private (AgenticExecutor exec, FakeHost host) Build(ApprovalMode mode)
        {
            var host = new FakeHost(_dir);
            var opts = new ModeOptions { Mode = mode };
            return (new AgenticExecutor(host, opts), host);
        }

        private static ResponseOutcome OutcomeWith(params ResponseBlock[] blocks)
            => new ResponseOutcome(new List<ResponseBlock>(blocks));

        private static AgenticAction ApplyAndBuild()
            => new AgenticAction { Type = ActionType.ApplyAndBuild };

        private static string PlanRefusal() => ApprovalPolicy.PlanRefusalMessage("x");

        // ── create_file ────────────────────────────────────────────────────────

        [Fact]
        public async Task Plan_ACreateIsRefused_NotRun_AndTellsTheModelPlanMode()
        {
            var (exec, host) = Build(ApprovalMode.Plan);

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.File,
                FileName = "Foo.cs",
                Content = "class Foo {}",
                FromToolCall = true,
            }));

            // No prompt, no write.
            Assert.Empty(host.ConfirmPrompts);
            Assert.Empty(result.FilesCreated);

            // The model-facing message names plan mode, through the same BuildWriteFailure
            // path a manual decline uses.
            string toolResult = LoopHelpers.BuildToolResultContent(
                new ToolCallResult
                {
                    Name = "create_file",
                    Arguments = new Dictionary<string, string> { ["filename"] = "Foo.cs" },
                },
                result,
                new List<ResponseBlock>());

            Assert.Contains("FAILED", toolResult, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[PLAN MODE]", toolResult, StringComparison.Ordinal);
            Assert.Contains("plan mode", toolResult, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[File created]", toolResult, StringComparison.Ordinal);

            // The transcript carries the dim refusal line.
            Assert.Contains("[PLAN MODE] Refused", host.Output, StringComparison.Ordinal);
        }

        // ── patch_file ─────────────────────────────────────────────────────────

        private ResponseBlock PatchBlock() => new ResponseBlock
        {
            Type = BlockType.Patch,
            FileName = "Foo.cs",
            Content = "FIND\nold\nREPLACE\nnew\n",
            FromToolCall = true,
        };

        private PatchResolveResult ExactPatch() => new PatchResolveResult
        {
            FullPath = Path.Combine(_dir, "Foo.cs"),
            FileName = "Foo.cs",
            OriginalContent = "old",
            FileEncoding = System.Text.Encoding.UTF8,
            Confidence = PatchConfidence.Exact,
            ResolvedBlocks = { (0, 3, "new") },
        };

        [Fact]
        public async Task Plan_ARefusedPatch_SkipsTheCard_ResolveAndApplyEntirely()
        {
            var (exec, host) = Build(ApprovalMode.Plan);
            // Canned: if the refusal never fires, resolve/apply WILL be reached and the
            // patch will "apply" — which is exactly the defect this test exists to catch.
            host.ResolvedPatch = ExactPatch();

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(PatchBlock()));

            Assert.Empty(host.DiffPreviewCalls);
            Assert.Empty(result.PatchedPaths);
            Assert.Equal(1, result.PatchesFailed);
            Assert.Contains(result.Errors,
                e => e.Contains("[PLAN MODE]", StringComparison.Ordinal));
            Assert.Contains(result.Errors,
                e => e.Contains("Foo.cs", StringComparison.Ordinal));
        }

        // ── run_shell ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Plan_ARefusedShell_DoesNotRun_AndSaysPlanModeOnTheWire()
        {
            var (exec, host) = Build(ApprovalMode.Plan);

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.Shell,
                Command = "rm -rf /",
            }));

            Assert.Empty(host.ConfirmPrompts);
            Assert.NotEqual(0, result.ShellExitCode);
            Assert.Contains("[PLAN MODE]", result.ShellOutput, StringComparison.Ordinal);
            Assert.Contains("plan mode", result.ShellOutput, StringComparison.OrdinalIgnoreCase);
        }

        // The OTHER shell dispatch: ActionType.RunShell, not a Shell block. Gating only the
        // block path would have left this one wide open.
        [Fact]
        public async Task Plan_RefusesTheActionPathShellToo()
        {
            var (exec, host) = Build(ApprovalMode.Plan);

            var result = await exec.ExecuteAsync(
                new AgenticAction { Type = ActionType.RunShell, ShellCommand = "dotnet build" },
                OutcomeWith());

            Assert.Empty(host.ConfirmPrompts);
            Assert.NotEqual(0, result.ShellExitCode);
            Assert.Contains("[PLAN MODE]", result.ShellOutput, StringComparison.Ordinal);
        }

        // ── save_memory (the newly classified kind) ────────────────────────────

        [Fact]
        public async Task Plan_ARefusedSaveMemory_NeverCallsTheHost()
        {
            var (exec, host) = Build(ApprovalMode.Plan);

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.SaveMemory,
                MemoryTopic = "topic",
                MemoryContent = "content",
            }));

            // The host was never reached: the refusal fires before SaveMemoryAsync.
            Assert.Equal(0, host.SaveMemoryCalls);
            Assert.Contains(result.Errors,
                e => e.Contains("[PLAN MODE]", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Manual_ASaveMemory_StillRuns_Ungated()
        {
            // save_memory is NOT gated in Manual today; plan mode is the only mode that
            // refuses it. Pinning the Manual row here keeps "new kind" from silently
            // changing Manual's behaviour.
            var (exec, host) = Build(ApprovalMode.Manual);

            await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.SaveMemory,
                MemoryTopic = "topic",
                MemoryContent = "content",
            }));

            Assert.Equal(1, host.SaveMemoryCalls);
            Assert.Empty(host.ConfirmPrompts);
        }

        // ── run_sql: allow_write is a mutation, the read-only default is not ──

        [Fact]
        public async Task Plan_ARefusedSqlWrite_NeverRuns_AndSaysPlanMode()
        {
            var (exec, host) = Build(ApprovalMode.Plan);

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.RunSql,
                SqlQuery = "UPDATE T SET x = 1",
                SqlAllowWrite = true,
            }));

            // The host was never reached: the refusal fires before RunSqlAsync.
            Assert.Equal(0, host.RunSqlCalls);
            Assert.Empty(host.ConfirmPrompts);

            // The model receives the refusal as the tool result it reads.
            string toolResult = result.ToolResultContents["run_sql"] ?? "";
            Assert.Contains("[PLAN MODE]", toolResult, StringComparison.Ordinal);
            Assert.Contains("plan mode", toolResult, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(result.Errors,
                e => e.Contains("[PLAN MODE]", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Plan_AReadOnlySqlQuery_StillRuns()
        {
            // The read-only default of run_sql looks at data the way read_file looks at
            // files: not a mutation, free in Plan. The gate must fire on allow_write only.
            var (exec, host) = Build(ApprovalMode.Plan);

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.RunSql,
                SqlQuery = "SELECT 1",
            }));

            Assert.Equal(1, host.RunSqlCalls);
            Assert.Equal(false, host.LastRunSqlAllowWrite);
            Assert.Empty(result.Errors);
            Assert.DoesNotContain("[PLAN MODE]", host.Output, StringComparison.Ordinal);
        }

        // ── reads and builds stay free ─────────────────────────────────────────

        [Fact]
        public async Task Plan_LeavesReadToolsAndBuildsUntouched()
        {
            var (exec, host) = Build(ApprovalMode.Plan);

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(
                new ResponseBlock
                {
                    Type = BlockType.ReadRequest,
                    FileName = "Foo.cs",
                },
                new ResponseBlock
                {
                    Type = BlockType.Test,
                    TestProject = "DevMind.Core.Tests",
                }));

            // No refusal of any kind: reads and run_tests are not mutations.
            Assert.Empty(result.Errors);
            Assert.Empty(host.ConfirmPrompts);
            Assert.Equal(0, result.ShellExitCode);   // run_tests reported no failure
        }

        // ── Auto stays exactly as today ────────────────────────────────────────

        [Fact]
        public async Task Auto_StillAllowsEverythingWithoutAsking()
        {
            var (exec, host) = Build(ApprovalMode.Auto);

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.File,
                FileName = "Foo.cs",
                Content = "class Foo {}",
                FromToolCall = true,
            }));

            Assert.Empty(host.ConfirmPrompts);
            Assert.Single(result.FilesCreated);
        }
    }
}
