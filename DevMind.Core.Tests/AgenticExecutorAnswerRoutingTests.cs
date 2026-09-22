// File: AgenticExecutorAnswerRoutingTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Pins the answer routing in AgenticExecutor.ExecuteBlocksAsync: a model-authored
// ANSWER (BlockType.Done = task_done.summary, BlockType.NeedsInput = ask_caller
// questions) must go through IAgenticHost.AppendAnswer, NOT the generic
// AppendOutput — that is the seam a skin (the TUI) overrides to render the text
// as inline markdown instead of literal markers.
//
// Pre-fix, both blocks went straight to AppendOutput, so a task_done summary —
// the dominant final-answer path — never reached the markdown renderer and its
// **bold** / ## heading / `code` markers rendered literally on screen.
//
// The recording host below IMPLEMENTS AppendAnswer (the interface has a default
// that forwards to AppendOutput, so inheriting it would make the two calls
// indistinguishable and the routing tests vacuous). A second, plain host that
// does NOT declare AppendAnswer at all proves the default keeps headless
// behaviour identical: the answer text still lands in AppendOutput.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    public class AgenticExecutorAnswerRoutingTests
    {
        // The dominant path: task_done.summary is a Done block. It must be
        // delivered via AppendAnswer (so the TUI can markdown-render it), with
        // the TrimEnd('\r','\n') + "\n" normalisation intact, and must NOT also
        // be delivered via AppendOutput.
        [Fact]
        public async Task DoneBlock_RoutesThroughAppendAnswer_NotAppendOutput()
        {
            var host = new AnswerRecordingHost();
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            const string summary = "## Result\nFixed **four** files via `patch`.";
            await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                Outcome(new ResponseBlock { Type = BlockType.Done, Content = summary }));

            // The answer — normalised to a single trailing newline — went to AppendAnswer.
            Assert.Single(host.Answers);
            Assert.Equal(summary + "\n", host.Answers[0]);

            // ...and it did NOT leak into the plain output channel.
            Assert.Empty(host.Outputs);
        }

        // Same routing for ask_caller (NeedsInput): same shape, same reader.
        [Fact]
        public async Task NeedsInputBlock_RoutesThroughAppendAnswer_NotAppendOutput()
        {
            var host = new AnswerRecordingHost();
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            const string questions = "1. Keep the **new** schema?\r\n";
            await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                Outcome(new ResponseBlock { Type = BlockType.NeedsInput, Content = questions }));

            Assert.Single(host.Answers);
            Assert.Equal("1. Keep the **new** schema?\n", host.Answers[0]);
            Assert.Empty(host.Outputs);
        }

        // A non-answer block must NOT be upgraded: the scratchpad stays on the
        // plain AppendOutput channel (its [SCRATCHPAD] status line) and never
        // reaches AppendAnswer.
        [Fact]
        public async Task ScratchpadBlock_StillGoesThroughAppendOutput()
        {
            var host = new AnswerRecordingHost();
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                Outcome(new ResponseBlock { Type = BlockType.Scratchpad, Content = "state" }));

            Assert.Empty(host.Answers);
            Assert.Contains(host.Outputs, o => o.Contains("[SCRATCHPAD]"));
        }

        // The default interface implementation: a host that does NOT declare
        // AppendAnswer still receives the answer text — via AppendOutput — so
        // headless/CLI behaviour is byte-for-byte identical to pre-fix.
        [Fact]
        public async Task HostWithoutOverride_ReceivesAnswerViaAppendOutput()
        {
            var host = new PlainHost(); // no AppendAnswer member — inherits the interface default
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            const string summary = "done with **bold**";
            await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                Outcome(new ResponseBlock { Type = BlockType.Done, Content = summary }));

            Assert.Equal(summary + "\n", host.Output);
        }

        private static ResponseOutcome Outcome(params ResponseBlock[] blocks)
            => new ResponseOutcome(new List<ResponseBlock>(blocks));

        // Records AppendOutput and AppendAnswer into SEPARATE lists. AppendAnswer
        // is declared (not inherited from the interface default) so the two
        // channels are distinguishable.
        private sealed class AnswerRecordingHost : IAgenticHost
        {
            public List<string> Outputs { get; } = new();
            public List<string> Answers { get; } = new();

            public void AppendOutput(string text, OutputColor color = OutputColor.Normal) => Outputs.Add(text);
            public void AppendAnswer(string text) => Answers.Add(text);

            // ── Inert members (unused by answer-only turns) ───────────────────
            public Task<(int, string)> RunShellAsync(string command, int? timeoutSeconds = null)
                => Task.FromResult((0, ""));
            public Task<string> SaveFileAsync(string fileName, string content, bool fromToolCall = false)
                => Task.FromResult(fileName);
            public Task<string> AppendFileAsync(string fileName, string content) => Task.FromResult(fileName);
            public Task<string> LoadFileContentAsync(string fileName, int rangeStart = 0, int rangeEnd = 0, bool forceFullRead = false)
                => Task.FromResult("");
            public void UpdateScratchpad(string content) { }
            public string TaskScratchpad => "";
            public string GetWorkingDirectory() => "";
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
            public Task<bool> ConfirmContinueAsync(string message) => Task.FromResult(true);
            public Task<string> RunSqlAsync(string query, string connectionString, string connectionName, bool allowWrite, int maxRows, int commandTimeout)
                => Task.FromResult("");
            public Task<string> RunDebugAsync(string command, IReadOnlyDictionary<string, string> args)
                => Task.FromResult("");
            public Task<string> RecallCacheAsync(string handle) => Task.FromResult("");
            public Task<string> ListCacheAsync() => Task.FromResult("");
        }

        // Does NOT declare AppendAnswer — the interface default (forward to
        // AppendOutput) handles it, exactly like the headless/CLI hosts.
        private sealed class PlainHost : IAgenticHost
        {
            public string Output { get; private set; } = "";

            public void AppendOutput(string text, OutputColor color = OutputColor.Normal) => Output += text;
            public void UpdateScratchpad(string content) { }
            public string TaskScratchpad => "";
            public string GetWorkingDirectory() => "";
            public Task<(int, string)> RunShellAsync(string command, int? timeoutSeconds = null)
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
            public Task<bool> ConfirmContinueAsync(string message) => Task.FromResult(true);
            public Task<string> RunSqlAsync(string query, string connectionString, string connectionName, bool allowWrite, int maxRows, int commandTimeout)
                => Task.FromResult("");
            public Task<string> RunDebugAsync(string command, IReadOnlyDictionary<string, string> args)
                => Task.FromResult("");
            public Task<string> RecallCacheAsync(string handle) => Task.FromResult("");
            public Task<string> ListCacheAsync() => Task.FromResult("");
        }
    }
}
