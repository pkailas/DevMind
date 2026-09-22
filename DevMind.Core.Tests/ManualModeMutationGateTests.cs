// File: ManualModeMutationGateTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// ApprovalPolicy says WHETHER to ask; these pin that AgenticExecutor actually asks, and —
// the part that matters most — what the model is told when the answer is no.
//
// A declined write must reach the model as a failure. The precedent is bd7a581: the write
// tools' empty-result fallbacks used to return "[File created]" / "[Content appended]" /
// "[File deleted]" / "[File renamed]" for writes the host had refused, so a model believed
// a file existed and burned iterations failing against it. A decline is a refusal of exactly
// that shape, so it goes down exactly that path — the executor performs nothing and records
// a cause, and LoopHelpers' BuildWriteFailure turns an empty result list into an unmistakable
// failure with the cause relayed. These tests exist to stop a second, success-shaped decline
// message being invented alongside it.
//
// Auto must be untouched: asserting the host is never consulted (call count 0) is how that
// is pinned, rather than by the absence of a failure.

using Xunit;

namespace DevMind.Core.Tests
{
    public class ManualModeMutationGateTests : IDisposable
    {
        private readonly string _dir;

        public ManualModeMutationGateTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_approval_{Guid.NewGuid():N}");
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

        private (AgenticExecutor exec, FakeHost host, ModeOptions opts) Build(
            ApprovalMode mode, bool confirmAnswer = true)
        {
            var host = new FakeHost(_dir) { ConfirmAnswer = confirmAnswer };
            var opts = new ModeOptions { Mode = mode };
            return (new AgenticExecutor(host, opts), host, opts);
        }

        private static ResponseOutcome OutcomeWith(params ResponseBlock[] blocks)
            => new ResponseOutcome(new List<ResponseBlock>(blocks));

        private static AgenticAction ApplyAndBuild()
            => new AgenticAction { Type = ActionType.ApplyAndBuild };

        // ── create_file ────────────────────────────────────────────────────────

        [Fact]
        public async Task Manual_ADeclinedCreate_WritesNothing_AndTellsTheModelItFailed()
        {
            var (exec, host, _) = Build(ApprovalMode.Manual, confirmAnswer: false);

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.File,
                FileName = "Foo.cs",
                Content = "class Foo {}",
                FromToolCall = true,
            }));

            Assert.Single(host.ConfirmPrompts);
            Assert.Contains("Create file Foo.cs", host.ConfirmPrompts[0], StringComparison.Ordinal);

            // Nothing written: the empty list is what makes LoopHelpers report a failure.
            Assert.Empty(result.FilesCreated);

            // And the cause is on record, so the relayed message says WHY.
            Assert.Contains(result.Errors, e => e.Contains("declined", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("[SKIPPED]", host.Output, StringComparison.Ordinal);
        }

        // The assertion the brief calls out as mattering most: the model must not be told the
        // file exists. Driven through the real LoopHelpers path rather than inspected.
        [Fact]
        public async Task Manual_ADeclinedCreate_NeverReportsFileCreated()
        {
            var (exec, _, _) = Build(ApprovalMode.Manual, confirmAnswer: false);

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.File,
                FileName = "Foo.cs",
                Content = "class Foo {}",
                FromToolCall = true,
            }));

            string toolResult = LoopHelpers.BuildToolResultContent(
                new ToolCallResult
                {
                    Name = "create_file",
                    Arguments = new Dictionary<string, string> { ["filename"] = "Foo.cs" },
                },
                result,
                new List<ResponseBlock>());

            Assert.DoesNotContain("[File created]", toolResult, StringComparison.Ordinal);
            Assert.Contains("FAILED", toolResult, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Foo.cs", toolResult, StringComparison.Ordinal);
            Assert.Contains("declined", toolResult, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Manual_AnApprovedCreate_Writes()
        {
            var (exec, host, _) = Build(ApprovalMode.Manual, confirmAnswer: true);

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.File,
                FileName = "Foo.cs",
                Content = "class Foo {}",
                FromToolCall = true,
            }));

            Assert.Single(host.ConfirmPrompts);
            Assert.Single(result.FilesCreated);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public async Task Auto_NeverAsks()
        {
            var (exec, host, _) = Build(ApprovalMode.Auto);

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

        // ── delete_file ────────────────────────────────────────────────────────

        [Fact]
        public async Task Manual_ADeclinedDelete_DeletesNothing()
        {
            var (exec, host, _) = Build(ApprovalMode.Manual, confirmAnswer: false);

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.Delete,
                FileName = "Gone.cs",
            }));

            Assert.Single(host.ConfirmPrompts);
            Assert.Contains("Delete file Gone.cs", host.ConfirmPrompts[0], StringComparison.Ordinal);
            Assert.Empty(result.FilesDeleted);
            Assert.Contains(result.Errors, e => e.Contains("declined", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Auto_NeverAsksBeforeDeleting()
        {
            var (exec, host, _) = Build(ApprovalMode.Auto);

            await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.Delete,
                FileName = "Gone.cs",
            }));

            Assert.Empty(host.ConfirmPrompts);
        }

        // ── run_shell ──────────────────────────────────────────────────────────

        // A declined command must reach the model as output it will read, not as silence.
        // LoopHelpers' run_shell case returns ShellOutput verbatim when non-empty.
        [Fact]
        public async Task Manual_ADeclinedShell_DoesNotRun_AndSaysSoOnTheWire()
        {
            var (exec, host, _) = Build(ApprovalMode.Manual, confirmAnswer: false);

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.Shell,
                Command = "rm -rf /",
            }));

            Assert.Single(host.ConfirmPrompts);
            Assert.Contains("Run shell:", host.ConfirmPrompts[0], StringComparison.Ordinal);
            Assert.Contains("rm -rf /", host.ConfirmPrompts[0], StringComparison.Ordinal);

            Assert.NotEqual(0, result.ShellExitCode);
            Assert.Contains("NOT executed", result.ShellOutput, StringComparison.Ordinal);
            Assert.Contains("declined", result.ShellOutput, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Manual_AnApprovedShell_Runs()
        {
            var (exec, host, _) = Build(ApprovalMode.Manual, confirmAnswer: true);
            host.ShellResults["echo hi"] = (0, "hi");

            var result = await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.Shell,
                Command = "echo hi",
            }));

            Assert.Single(host.ConfirmPrompts);
            Assert.Equal(0, result.ShellExitCode);
            Assert.DoesNotContain("NOT executed", result.ShellOutput ?? "", StringComparison.Ordinal);
        }

        [Fact]
        public async Task Auto_NeverAsksBeforeRunningShell()
        {
            var (exec, host, _) = Build(ApprovalMode.Auto);
            host.ShellResults["echo hi"] = (0, "hi");

            await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.Shell,
                Command = "echo hi",
            }));

            Assert.Empty(host.ConfirmPrompts);
        }

        // The OTHER shell dispatch: ActionType.RunShell, not a Shell block. Gating only the
        // block path would have left this one wide open.
        [Fact]
        public async Task Manual_GatesTheActionPathShellToo()
        {
            var (exec, host, _) = Build(ApprovalMode.Manual, confirmAnswer: false);

            var result = await exec.ExecuteAsync(
                new AgenticAction { Type = ActionType.RunShell, ShellCommand = "dotnet build" },
                OutcomeWith());

            Assert.Single(host.ConfirmPrompts);
            Assert.Contains("dotnet build", host.ConfirmPrompts[0], StringComparison.Ordinal);
            Assert.NotEqual(0, result.ShellExitCode);
            Assert.Contains("NOT executed", result.ShellOutput, StringComparison.Ordinal);
        }

        // ── the prompt text ────────────────────────────────────────────────────

        // A here-string pasted whole into a confirm dialog is unreadable at exactly the
        // moment reading matters, so a multi-line command is summarised. The full text is
        // already in the transcript above.
        [Fact]
        public async Task ALongCommand_IsSummarisedInThePrompt()
        {
            var (exec, host, _) = Build(ApprovalMode.Manual, confirmAnswer: false);

            string command = "cat > f <<'EOF'\n" + new string('x', 500) + "\nEOF";

            await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(new ResponseBlock
            {
                Type = BlockType.Shell,
                Command = command,
            }));

            string prompt = host.ConfirmPrompts[0];
            Assert.DoesNotContain("\n", prompt, StringComparison.Ordinal);
            Assert.Contains("chars total", prompt, StringComparison.Ordinal);
            Assert.True(prompt.Length < 200, $"the confirm prompt is {prompt.Length} chars — too long to read in a dialog");
        }

        // ── patch_file ─────────────────────────────────────────────────────────

        // Manual means every patch is LOOKED at. An exact-confidence patch is the one auto
        // mode applies unseen, so it is the only case that distinguishes the two modes —
        // a fuzzy patch already goes through the card in both.
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
        public async Task Manual_SendsAnExactPatchThroughTheDiffCard()
        {
            var (exec, host, _) = Build(ApprovalMode.Manual);
            host.ResolvedPatch = ExactPatch();

            await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(PatchBlock()));

            Assert.Single(host.DiffPreviewCalls);
            Assert.Equal(1, host.DiffPreviewCalls[0]);
        }

        [Fact]
        public async Task Auto_AppliesAnExactPatchWithoutTheCard()
        {
            var (exec, host, _) = Build(ApprovalMode.Auto);
            host.ResolvedPatch = ExactPatch();

            await exec.ExecuteAsync(ApplyAndBuild(), OutcomeWith(PatchBlock()));

            Assert.Empty(host.DiffPreviewCalls);
        }
    }
}
