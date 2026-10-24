// File: AgenticExecutorApprovalRoutingTests.cs  v1.0
//
// Regression tests for the approval-prompt misrouting defect: in manual approval
// mode, every mutating action (run_shell, file write, MCP tool call) was posed
// through ConfirmContinueAsync — the token-budget guard's method — so the TUI
// showed a "Token budget" dialog with Continue/Stop buttons. The fix adds
// IAgenticHost.ConfirmActionAsync (with a default interface implementation that
// routes to ConfirmContinueAsync) and points the executor's Ask branch at it.
//
// These tests pin:
//   1. In Ask mode the executor calls ConfirmActionAsync, NOT ConfirmContinueAsync.
//   2. A host that does NOT override ConfirmActionAsync (the interface default)
//      still works — the DIM routes to ConfirmContinueAsync, preserving headless
//      and existing-fake behaviour.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    public class AgenticExecutorApprovalRoutingTests
    {
        // ── Host that records WHICH confirm method was called ──────────────────
        // Overrides ConfirmActionAsync so the two channels are distinguishable.
        private sealed class RoutingRecordingHost : IAgenticHost
        {
            public List<string> ConfirmActionPrompts { get; } = new();
            public List<string> ConfirmContinuePrompts { get; } = new();
            public List<string> ConfirmActionDetails { get; } = new();
            public System.Text.StringBuilder Output { get; } = new();
            public bool ConfirmAnswer { get; set; } = true;

            public Task<bool> ConfirmActionAsync(string action)
            {
                ConfirmActionPrompts.Add(action);
                return Task.FromResult(ConfirmAnswer);
            }

            // The interactive overload: short one-liner + the full dialog text.
            public Task<bool> ConfirmActionAsync(string action, string detail)
            {
                ConfirmActionPrompts.Add(action);
                ConfirmActionDetails.Add(detail);
                return Task.FromResult(ConfirmAnswer);
            }

            public Task<bool> ConfirmContinueAsync(string message)
            {
                ConfirmContinuePrompts.Add(message);
                return Task.FromResult(ConfirmAnswer);
            }

            // ── Inert members ─────────────────────────────────────────────────
            public void AppendOutput(string text, OutputColor color = OutputColor.Normal) => Output.Append(text);
            public void UpdateScratchpad(string content) { }
            public string TaskScratchpad => "";
            public string GetWorkingDirectory() => "";
            public Task<(int, string)> RunShellAsync(string command, int? timeoutSeconds = null, bool detach = false)
                => Task.FromResult((0, ""));
            public Task<string> SaveFileAsync(string fileName, string content, bool fromToolCall = false)
                => Task.FromResult(fileName);
            public Task<string> AppendFileAsync(string fileName, string content) => Task.FromResult(fileName);
            public Task<string> LoadFileContentAsync(string fileName, int rangeStart = 0, int rangeEnd = 0, bool forceFullRead = false)
                => Task.FromResult("");
            public Task<string> GrepFileAsync(string pattern, string filename, int? startLine, int? endLine)
                => Task.FromResult("");
            public Task<string> FindInFilesAsync(string pattern, string globPattern, int? startLine, int? endLine)
                => Task.FromResult("");
            public Task<string> DeleteFileAsync(string filename) => Task.FromResult("Deleted");
            public Task<string> RenameFileAsync(string oldFilename, string newFilename) => Task.FromResult("Renamed");
            public Task<string> GetFileDiffAsync(string filename) => Task.FromResult("");
            public int RunTestsCalls { get; private set; }
            public Task<string> RunTestsAsync(string project, string filter, int? timeoutSeconds = null)
            {
                RunTestsCalls++;
                return Task.FromResult("Passed! - Failed: 0, Passed: 3, Total: 3");
            }
            public Task<(PatchResolveResult? result, string? failureReason)> ResolvePatchAsync(string patchContent, bool fromToolCall = false)
                => Task.FromResult<(PatchResolveResult?, string?)>((null, null));
            public Task<(string? fullPath, string? failureReason)> ApplyResolvedPatchAsync(PatchResolveResult resolved)
                => Task.FromResult<(string?, string?)>((null, null));
            public Task<List<int>> ShowDiffPreviewAsync(List<PatchResolveResult> resolvedPatches, CancellationToken cancellationToken)
                => Task.FromResult(new List<int>());
            public Task<string> RecallMemoryAsync(string topic) => Task.FromResult("");
            public Task<string> SaveMemoryAsync(string topic, string content, string description)
                => Task.FromResult("ok");
            public Task<string> ListMemoryTopicsAsync() => Task.FromResult("");
            public Task<string> SearchMemoryAsync(string pattern) => Task.FromResult("");
            public Task<string> QueryLibraryAsync(string question, int topK, CancellationToken cancellationToken = default)
                => Task.FromResult("");
            public Task<string> QueryLibraryAsync(string question, int topK, string? docFilter, CancellationToken cancellationToken = default)
                => Task.FromResult("");
            public Task<string> ListFilesAsync(string glob, bool recursive, CancellationToken cancellationToken = default)
                => Task.FromResult("");
            public int GetPatchBackupCount() => 0;
            public Task<string> GetDiagnosticsAsync(string filename) => Task.FromResult("");
            public Task<string> GoToDefinitionAsync(string filename, int line, int character) => Task.FromResult("");
            public Task<string> FindReferencesAsync(string filename, int line, int character) => Task.FromResult("");
            public Task<string> HoverAsync(string filename, int line, int character) => Task.FromResult("");
            public Task<string> FindSymbolAsync(string query, int maxResults, string language, string path) => Task.FromResult("");
            public Task<string> WebSearchAsync(string query, int? maxResults) => Task.FromResult("");
            public Task<string> WebFetchAsync(string url, int offset) => Task.FromResult("");
            public Task<string> LearnSearchAsync(string query, int? maxResults) => Task.FromResult("");
            public Task<string> LearnFetchAsync(string url) => Task.FromResult("");
            public Task<string> LearnCodeSearchAsync(string query, int? maxResults) => Task.FromResult("");
            public Task<string> RunSqlAsync(string query, string connectionString, string connectionName, bool allowWrite, int maxRows, int commandTimeout)
                => Task.FromResult("");
            public Task<string> RunDebugAsync(string command, IReadOnlyDictionary<string, string> args)
                => Task.FromResult("");
            public Task<string> RecallCacheAsync(string handle) => Task.FromResult("");
            public Task<string> ListCacheAsync() => Task.FromResult("");
        }

        // ── Host that does NOT override ConfirmActionAsync ────────────────────
        // The interface default routes to ConfirmContinueAsync. This mirrors
        // BufferedAgenticHost and all existing test fakes.
        private sealed class DefaultImplHost : IAgenticHost
        {
            public List<string> ConfirmContinuePrompts { get; } = new();
            public bool ConfirmAnswer { get; set; } = true;

            // Only ConfirmContinueAsync is declared. ConfirmActionAsync is NOT
            // overridden — the interface default (→ ConfirmContinueAsync) handles it.
            public Task<bool> ConfirmContinueAsync(string message)
            {
                ConfirmContinuePrompts.Add(message);
                return Task.FromResult(ConfirmAnswer);
            }

            // ── Inert members ─────────────────────────────────────────────────
            public void AppendOutput(string text, OutputColor color = OutputColor.Normal) { }
            public void UpdateScratchpad(string content) { }
            public string TaskScratchpad => "";
            public string GetWorkingDirectory() => "";
            public Task<(int, string)> RunShellAsync(string command, int? timeoutSeconds = null, bool detach = false)
                => Task.FromResult((0, ""));
            public Task<string> SaveFileAsync(string fileName, string content, bool fromToolCall = false)
                => Task.FromResult(fileName);
            public Task<string> AppendFileAsync(string fileName, string content) => Task.FromResult(fileName);
            public Task<string> LoadFileContentAsync(string fileName, int rangeStart = 0, int rangeEnd = 0, bool forceFullRead = false)
                => Task.FromResult("");
            public Task<string> GrepFileAsync(string pattern, string filename, int? startLine, int? endLine)
                => Task.FromResult("");
            public Task<string> FindInFilesAsync(string pattern, string globPattern, int? startLine, int? endLine)
                => Task.FromResult("");
            public Task<string> DeleteFileAsync(string filename) => Task.FromResult("Deleted");
            public Task<string> RenameFileAsync(string oldFilename, string newFilename) => Task.FromResult("Renamed");
            public Task<string> GetFileDiffAsync(string filename) => Task.FromResult("");
            public Task<string> RunTestsAsync(string project, string filter, int? timeoutSeconds = null)
                => Task.FromResult("");
            public Task<(PatchResolveResult? result, string? failureReason)> ResolvePatchAsync(string patchContent, bool fromToolCall = false)
                => Task.FromResult<(PatchResolveResult?, string?)>((null, null));
            public Task<(string? fullPath, string? failureReason)> ApplyResolvedPatchAsync(PatchResolveResult resolved)
                => Task.FromResult<(string?, string?)>((null, null));
            public Task<List<int>> ShowDiffPreviewAsync(List<PatchResolveResult> resolvedPatches, CancellationToken cancellationToken)
                => Task.FromResult(new List<int>());
            public Task<string> RecallMemoryAsync(string topic) => Task.FromResult("");
            public Task<string> SaveMemoryAsync(string topic, string content, string description)
                => Task.FromResult("ok");
            public Task<string> ListMemoryTopicsAsync() => Task.FromResult("");
            public Task<string> SearchMemoryAsync(string pattern) => Task.FromResult("");
            public Task<string> QueryLibraryAsync(string question, int topK, CancellationToken cancellationToken = default)
                => Task.FromResult("");
            public Task<string> QueryLibraryAsync(string question, int topK, string? docFilter, CancellationToken cancellationToken = default)
                => Task.FromResult("");
            public Task<string> ListFilesAsync(string glob, bool recursive, CancellationToken cancellationToken = default)
                => Task.FromResult("");
            public int GetPatchBackupCount() => 0;
            public Task<string> GetDiagnosticsAsync(string filename) => Task.FromResult("");
            public Task<string> GoToDefinitionAsync(string filename, int line, int character) => Task.FromResult("");
            public Task<string> FindReferencesAsync(string filename, int line, int character) => Task.FromResult("");
            public Task<string> HoverAsync(string filename, int line, int character) => Task.FromResult("");
            public Task<string> FindSymbolAsync(string query, int maxResults, string language, string path) => Task.FromResult("");
            public Task<string> WebSearchAsync(string query, int? maxResults) => Task.FromResult("");
            public Task<string> WebFetchAsync(string url, int offset) => Task.FromResult("");
            public Task<string> LearnSearchAsync(string query, int? maxResults) => Task.FromResult("");
            public Task<string> LearnFetchAsync(string url) => Task.FromResult("");
            public Task<string> LearnCodeSearchAsync(string query, int? maxResults) => Task.FromResult("");
            public Task<string> RunSqlAsync(string query, string connectionString, string connectionName, bool allowWrite, int maxRows, int commandTimeout)
                => Task.FromResult("");
            public Task<string> RunDebugAsync(string command, IReadOnlyDictionary<string, string> args)
                => Task.FromResult("");
            public Task<string> RecallCacheAsync(string handle) => Task.FromResult("");
            public Task<string> ListCacheAsync() => Task.FromResult("");
        }

        // ── ILlmOptions with ApprovalMode = Manual ─────────────────────────────
        private sealed class ManualApprovalOptions : ILlmOptions
        {
            public string SystemPrompt => "test";
            public string ModelName => "test";
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
            public int NearlineIngestThresholdChars { get; set; } = 8_000;
            public bool MicroCompactSummarize => false;
            public bool MicroCompactBrainwash => false;
            public bool AlwaysConfirmPatch => false;
            public ApprovalMode Mode { get; set; } = ApprovalMode.Manual;
            public ApprovalMode ApprovalMode => Mode;
            public int AgenticLoopMaxDepth => 25;
            public int AgenticContextLimitPercent => 0;
        }

        private static ResponseOutcome ShellOutcome(string command)
        {
            var blocks = ToolCallMapper.Map(
                new List<ToolCallResult> { new ToolCallResult { Name = "run_shell", Arguments = new Dictionary<string, string> { ["command"] = command } } },
                buildCommand: "dotnet build");
            return new ResponseOutcome(blocks);
        }

        [Fact]
        public async Task AskMode_CallsConfirmActionAsync_NotConfirmContinueAsync()
        {
            var host = new RoutingRecordingHost();
            var executor = new AgenticExecutor(host, new ManualApprovalOptions());
            executor.SetCancellationToken(CancellationToken.None);

            await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                ShellOutcome("echo test"));

            // The approval question went through ConfirmActionAsync…
            Assert.Single(host.ConfirmActionPrompts);
            Assert.Contains("echo test", host.ConfirmActionPrompts[0]);

            // …NOT through ConfirmContinueAsync (the token-budget method).
            Assert.Empty(host.ConfirmContinuePrompts);
        }

        [Fact]
        public async Task AskMode_Decline_SkipsTheAction()
        {
            var host = new RoutingRecordingHost { ConfirmAnswer = false };
            var executor = new AgenticExecutor(host, new ManualApprovalOptions());
            executor.SetCancellationToken(CancellationToken.None);

            await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                ShellOutcome("echo test"));

            // Declined: ConfirmActionAsync was called and answered false.
            Assert.Single(host.ConfirmActionPrompts);
            Assert.Empty(host.ConfirmContinuePrompts);
        }

        [Fact]
        public async Task HostWithoutOverride_DefaultImplRoutesToConfirmContinueAsync()
        {
            // DefaultImplHost does NOT override ConfirmActionAsync. The interface
            // default routes to ConfirmContinueAsync, so the prompt appears there.
            var host = new DefaultImplHost();
            var executor = new AgenticExecutor(host, new ManualApprovalOptions());
            executor.SetCancellationToken(CancellationToken.None);

            await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                ShellOutcome("echo test"));

            // The DIM forwarded the call to ConfirmContinueAsync.
            Assert.Single(host.ConfirmContinuePrompts);
            Assert.Contains("echo test", host.ConfirmContinuePrompts[0]);
        }

        // ── Dialog text vs transcript text ─────────────────────────────────────
        // The approval dialog needs the tool name and the WHOLE action (Paul's popup read only
        // `Run shell: python -c " … (1093 chars total)`), while the transcript's [SKIPPED] /
        // [PLAN MODE] lines and the plan refusal sent to the model must stay one line.

        private const string MultiLineCommand = "python -c \"\nimport sys\nprint('hello')\nprint(sys.argv)\n\"";

        private static ResponseOutcome ToolOutcome(string name, Dictionary<string, string> args)
            => new ResponseOutcome(ToolCallMapper.Map(
                new List<ToolCallResult> { new ToolCallResult { Name = name, Arguments = args } },
                buildCommand: "dotnet build \"C:\\x\\X.slnx\""));

        private static string[] Lines(string s) => s.Replace("\r", "").Split('\n');

        [Fact]
        public async Task Dialog_RunShell_ShowsToolNameAndFullMultiLineCommand()
        {
            var host = new RoutingRecordingHost { ConfirmAnswer = false };
            var executor = new AgenticExecutor(host, new ManualApprovalOptions());
            executor.SetCancellationToken(CancellationToken.None);

            await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild },
                ShellOutcome(MultiLineCommand));

            Assert.Single(host.ConfirmActionDetails);
            string[] lines = Lines(host.ConfirmActionDetails[0]);
            Assert.Contains("run_shell", lines[0]);
            // Every line of the command, in order, line breaks kept.
            Assert.Equal(Lines(MultiLineCommand), lines.Skip(1).ToArray());

            // The short form stays one line.
            Assert.DoesNotContain("\n", host.ConfirmActionPrompts[0]);
        }

        [Fact]
        public async Task Dialog_RunBuild_NamesRunBuildNotRunShell()
        {
            var host = new RoutingRecordingHost { ConfirmAnswer = false };
            var executor = new AgenticExecutor(host, new ManualApprovalOptions());
            executor.SetCancellationToken(CancellationToken.None);

            await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild },
                ToolOutcome("run_build", new Dictionary<string, string>()));

            Assert.Single(host.ConfirmActionDetails);
            string[] lines = Lines(host.ConfirmActionDetails[0]);
            Assert.Contains("run_build", lines[0]);
            Assert.DoesNotContain("run_shell", lines[0]);
            Assert.Contains("dotnet build", host.ConfirmActionDetails[0]);
        }

        [Fact]
        public async Task Dialog_CreateFile_NamesToolAndKeepsNameAndSize()
        {
            var host = new RoutingRecordingHost { ConfirmAnswer = false };
            var executor = new AgenticExecutor(host, new ManualApprovalOptions());
            executor.SetCancellationToken(CancellationToken.None);

            await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild },
                ToolOutcome("create_file", new Dictionary<string, string> { ["filename"] = "Foo.cs", ["content"] = "class Foo {}" }));

            Assert.Single(host.ConfirmActionDetails);
            string detail = host.ConfirmActionDetails[0];
            Assert.Contains("create_file", Lines(detail)[0]);
            Assert.Contains("Foo.cs", detail);
            Assert.Contains("12 chars", detail);
            Assert.DoesNotContain("class Foo", detail); // no content preview
        }

        private sealed class NullMcp : IMcpToolInvoker
        {
            public Task<string> CallToolAsync(string server, string tool, Newtonsoft.Json.Linq.JObject? args, CancellationToken ct)
                => Task.FromResult("ok");
        }

        [Fact]
        public async Task Dialog_McpCall_NamesServerAndTool_AndShowsAllArguments()
        {
            var host = new RoutingRecordingHost { ConfirmAnswer = false };
            var executor = new AgenticExecutor(host, new ManualApprovalOptions()) { McpTools = new NullMcp() };
            executor.SetCancellationToken(CancellationToken.None);

            string longPrompt = new string('p', 400) + "_END";
            await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild },
                ToolOutcome("mcp__comfy__run_workflow", new Dictionary<string, string> { ["workflow"] = "sdxl", ["prompt"] = longPrompt }));

            Assert.Single(host.ConfirmActionDetails);
            string detail = host.ConfirmActionDetails[0];
            Assert.Contains("MCP comfy/run_workflow", Lines(detail)[0]);
            Assert.Contains(longPrompt, detail);      // not cut at 200 chars
            Assert.Contains("sdxl", detail);

            // The short form keeps the 200-char cut and stays one line.
            Assert.DoesNotContain("_END", host.ConfirmActionPrompts[0]);
            Assert.DoesNotContain("\n", host.ConfirmActionPrompts[0]);
        }

        [Fact]
        public async Task Transcript_SkippedLine_StaysOneLine_ForMultiLineCommand()
        {
            var host = new RoutingRecordingHost { ConfirmAnswer = false };
            var executor = new AgenticExecutor(host, new ManualApprovalOptions());
            executor.SetCancellationToken(CancellationToken.None);

            await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild },
                ShellOutcome(MultiLineCommand));

            string skipped = Lines(host.Output.ToString()).Single(l => l.StartsWith("[SKIPPED]"));
            Assert.Contains("python -c", skipped);
            Assert.DoesNotContain("print('hello')", host.Output.ToString()); // only the first line
            // The dialog DID get the full text — the transcript is short by choice, not by loss.
            Assert.Single(host.ConfirmActionDetails);
            Assert.Contains("print('hello')", host.ConfirmActionDetails[0]);
        }

        [Fact]
        public async Task PlanMode_RefusalAndTranscript_StayOneLine_AndNeverAsk()
        {
            var host = new RoutingRecordingHost();
            var executor = new AgenticExecutor(host, new ManualApprovalOptions { Mode = ApprovalMode.Plan });
            executor.SetCancellationToken(CancellationToken.None);

            var result = await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild },
                ShellOutcome(MultiLineCommand));

            Assert.Empty(host.ConfirmActionPrompts);
            Assert.Empty(host.ConfirmActionDetails);
            string refusal = result.Errors.Single(e => e.StartsWith("[PLAN MODE]"));
            Assert.DoesNotContain("\n", refusal);
            Assert.DoesNotContain("print('hello')", refusal);
            Assert.DoesNotContain("print('hello')", host.Output.ToString());
            Assert.Single(Lines(host.Output.ToString()), l => l.StartsWith("[PLAN MODE] Refused:"));
        }

        // ── "Why:" — the model's own prose from the same response ─────────────
        // LoopDriver puts the response's visible (thinking-stripped) prose first in the
        // block list as a Text block; the dialog shows its last paragraph between the
        // tool line and the command.

        private static ResponseOutcome WithProse(string prose, ResponseOutcome tools)
        {
            var blocks = new List<ResponseBlock>(tools.Blocks);
            blocks.Insert(0, new ResponseBlock { Type = BlockType.Text, Content = prose });
            return new ResponseOutcome(blocks);
        }

        private static async Task<RoutingRecordingHost> AskWith(ResponseOutcome outcome)
        {
            var host = new RoutingRecordingHost { ConfirmAnswer = false };
            var executor = new AgenticExecutor(host, new ManualApprovalOptions());
            executor.SetCancellationToken(CancellationToken.None);
            await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild }, outcome);
            return host;
        }

        [Fact]
        public async Task Why_ShowsLastParagraphOfProse_BetweenToolLineAndCommand()
        {
            string prose = "I looked at the build output.\r\n\r\nThe   test project is\r\nstale,  so I will   rebuild it.\n";
            var host = await AskWith(WithProse(prose, ShellOutcome("dotnet build X.slnx")));

            Assert.Single(host.ConfirmActionDetails);
            Assert.Equal("Tool: run_shell\nWhy: The test project is stale, so I will rebuild it.\ndotnet build X.slnx",
                host.ConfirmActionDetails[0]);
        }

        [Fact]
        public async Task Why_EmptyOrWhitespaceProse_OmitsTheWhyLine()
        {
            var none = await AskWith(ShellOutcome("echo hi"));
            Assert.Equal("Tool: run_shell\necho hi", none.ConfirmActionDetails[0]);

            var blank = await AskWith(WithProse("  \r\n\n  ", ShellOutcome("echo hi")));
            Assert.Equal("Tool: run_shell\necho hi", blank.ConfirmActionDetails[0]);
        }

        [Fact]
        public async Task Why_LongProse_IsCappedWithEllipsis_AndCommandFollows()
        {
            string prose = string.Join(" ", Enumerable.Repeat("because", 400));
            var host = await AskWith(WithProse(prose, ShellOutcome("echo hi")));

            string[] lines = Lines(host.ConfirmActionDetails[0]);
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("Why: because", lines[1]);
            Assert.EndsWith("…", lines[1]);
            Assert.True(lines[1].Length <= "Why: ".Length + AgenticExecutor.MaxApprovalReasonChars + 1,
                $"reason line is {lines[1].Length} chars");
            Assert.Equal("echo hi", lines[2]);
        }

        [Fact]
        public async Task Why_NeverShowsThinking_OrToolCallEchoes_OrStatusLines()
        {
            string prose = "<think>secret plan</think>[TOOL_USE] 1 call\nOkay.\n\nDelete the temp file now." +
                           "<tool_call>{\"name\":\"delete_file\"}</tool_call>";
            var host = await AskWith(WithProse(prose,
                ToolOutcome("delete_file", new Dictionary<string, string> { ["filename"] = "tmp.txt" })));

            string detail = host.ConfirmActionDetails[0];
            Assert.Contains("\nWhy: Delete the temp file now.\n", detail);
            Assert.DoesNotContain("secret", detail);
            Assert.DoesNotContain("think", detail);
            Assert.DoesNotContain("tool_call", detail);
            Assert.DoesNotContain("[TOOL_USE]", detail);
        }

        [Fact]
        public async Task Why_SharedByEveryGatedCallInTheResponse_AndTranscriptStaysShort()
        {
            var tools = ToolOutcome("create_file", new Dictionary<string, string> { ["filename"] = "A.cs", ["content"] = "x" });
            var more = ShellOutcome("echo two");
            var blocks = new List<ResponseBlock>(tools.Blocks);
            blocks.AddRange(more.Blocks);
            var host = await AskWith(WithProse("Write A then echo.", new ResponseOutcome(blocks)));

            Assert.Equal(2, host.ConfirmActionDetails.Count);
            Assert.All(host.ConfirmActionDetails, d => Assert.Equal("Why: Write A then echo.", Lines(d)[1]));
            Assert.DoesNotContain("Why:", host.Output.ToString());
            Assert.All(host.ConfirmActionPrompts, p => Assert.DoesNotContain("Why:", p));
        }

        // ── run_tests executes code, so it is gated exactly like run_build ─────
        // Auto runs it, Manual asks first (a decline means NOT run and the model is told
        // so), Plan refuses without asking.

        private static ResponseOutcome TestsOutcome()
            => ToolOutcome("run_tests", new Dictionary<string, string>
            {
                ["project"] = "DevMind.Core.Tests",
                ["filter"]  = "FullyQualifiedName~Approval",
            });

        private static string RunTestsToolResult(ExecutionResult result)
            => LoopHelpers.BuildToolResultContent(
                new ToolCallResult { Name = "run_tests", Arguments = new Dictionary<string, string>() },
                result,
                new List<ResponseBlock>());

        [Fact]
        public async Task RunTests_Manual_AsksFirst_AndADeclineRunsNothing_AndTellsTheModelNotRun()
        {
            var host = new RoutingRecordingHost { ConfirmAnswer = false };
            var executor = new AgenticExecutor(host, new ManualApprovalOptions());
            executor.SetCancellationToken(CancellationToken.None);

            var result = await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild },
                TestsOutcome());

            // Asked through the approval dialog, naming the tool and what would run.
            Assert.Single(host.ConfirmActionDetails);
            Assert.StartsWith("Tool: run_tests", host.ConfirmActionDetails[0]);
            Assert.Contains("DevMind.Core.Tests", host.ConfirmActionDetails[0]);
            Assert.Contains("FullyQualifiedName~Approval", host.ConfirmActionDetails[0]);
            Assert.DoesNotContain("\n", host.ConfirmActionPrompts[0]);
            Assert.Empty(host.ConfirmContinuePrompts);

            // Declined: nothing ran, and the result is a failure, not a pass.
            Assert.Equal(0, host.RunTestsCalls);
            Assert.NotEqual(0, result.ShellExitCode);

            string toolResult = RunTestsToolResult(result);
            Assert.Contains("declined", toolResult, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("NOT run", toolResult, StringComparison.Ordinal);
            Assert.DoesNotContain("[Tests executed]", toolResult, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RunTests_Manual_Approved_Runs()
        {
            var host = new RoutingRecordingHost { ConfirmAnswer = true };
            var executor = new AgenticExecutor(host, new ManualApprovalOptions());
            executor.SetCancellationToken(CancellationToken.None);

            var result = await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild },
                TestsOutcome());

            Assert.Single(host.ConfirmActionDetails);
            Assert.Equal(1, host.RunTestsCalls);
            Assert.Equal(0, result.ShellExitCode);
        }

        [Fact]
        public async Task RunTests_Plan_IsRefused_NeverAsks_NothingRuns()
        {
            var host = new RoutingRecordingHost();
            var executor = new AgenticExecutor(host, new ManualApprovalOptions { Mode = ApprovalMode.Plan });
            executor.SetCancellationToken(CancellationToken.None);

            var result = await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild },
                TestsOutcome());

            Assert.Empty(host.ConfirmActionPrompts);
            Assert.Empty(host.ConfirmContinuePrompts);
            Assert.Equal(0, host.RunTestsCalls);
            Assert.NotEqual(0, result.ShellExitCode);

            string toolResult = RunTestsToolResult(result);
            Assert.Contains("[PLAN MODE]", toolResult, StringComparison.Ordinal);
            Assert.Contains("plan mode", toolResult, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task RunTests_Auto_Runs_WithoutAsking()
        {
            var host = new RoutingRecordingHost();
            var executor = new AgenticExecutor(host, new ManualApprovalOptions { Mode = ApprovalMode.Auto });
            executor.SetCancellationToken(CancellationToken.None);

            var result = await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild },
                TestsOutcome());

            Assert.Empty(host.ConfirmActionPrompts);
            Assert.Empty(host.ConfirmContinuePrompts);
            Assert.Equal(1, host.RunTestsCalls);
            Assert.Equal(0, result.ShellExitCode);
        }
    }
}
