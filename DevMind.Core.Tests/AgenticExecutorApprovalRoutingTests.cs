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
            public bool ConfirmAnswer { get; set; } = true;

            public Task<bool> ConfirmActionAsync(string action)
            {
                ConfirmActionPrompts.Add(action);
                return Task.FromResult(ConfirmAnswer);
            }

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
            public Task<string> WebFetchAsync(string url) => Task.FromResult("");
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
            public Task<string> WebFetchAsync(string url) => Task.FromResult("");
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
            public ApprovalMode ApprovalMode => ApprovalMode.Manual;
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
    }
}
