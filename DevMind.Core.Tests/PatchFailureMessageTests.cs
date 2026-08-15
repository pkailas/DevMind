// File: PatchFailureMessageTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for the collapsed patch-failure message in
// AgenticExecutor.ExecuteBatchPatchesAsync.
//
// Before the fix, BOTH resolve-phase and apply-phase failures produced the
// identical message:
//   [PATCH-FAILED:<file>] FIND text not found — file was NOT modified.
//   READ the file to get exact current content, then retry PATCH with correct
//   FIND text.
//
// For an APPLY-phase failure (merge conflict, write-permission, disk error,
// pending-conflict block) that message is actively misleading: the FIND text
// WAS found and resolved. It sent the model to re-read and re-RETRY the
// identical patch — the exact loop the fix is meant to break.
//
// These tests drive the real AgenticExecutor over a scripted fake host and
// assert on the EXECUTOR's error message content (result.Errors), so they
// fail on the old collapsed message:
//   - a failed APPLY must NOT claim "FIND text not found";
//   - a failed RESOLVE must NOT claim "FIND text not found" either;
//   - the true cause from the host must be relayed verbatim;
//   - and the distinct Resolve/Apply phase prefixes must be present.
//
// With the pre-fix code the first two tests fail: the old message literally
// contains "FIND text not found". With the post-fix code they pass because the
// executor now emits "Resolve failed: {cause}" / "Apply failed: {cause}".

using Xunit;

namespace DevMind.Core.Tests
{
    public class PatchFailureMessageTests
    {
        private const string PatchText =
            "PATCH target.txt\nFIND:\nfoo\nREPLACE:\nbar\nEND_PATCH";

        private const string CollapsedFindNotFound = "FIND text not found";

        // ── The core regression: APPLY-phase failure ──────────────────────────

        [Fact]
        public async Task ApplyPhaseFailure_MustNotClaimFindTextNotFound()
        {
            var host = new ScriptedPatchHost
            {
                ResolveOutcome = (Ok: true, Result: MakeResolved("target.txt"), Failure: null),
                ApplyOutcome   = (Ok: false, Path: null, Failure:
                    "Merge conflict — the file changed outside this patch since it was read.")
            };
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            var result = await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                new ResponseOutcome(new List<ResponseBlock>
                {
                    new ResponseBlock
                    {
                        Type = BlockType.Patch,
                        Content = PatchText,
                        FileName = "target.txt",
                        FromToolCall = true
                    }
                }));

            Assert.True(result.PatchesFailed > 0, "expected the apply phase to fail");
            Assert.Contains(result.Errors, e => e.Contains("[PATCH-FAILED:target.txt]"));

            // THE bug: an apply-phase failure must not tell the model the FIND
            // text wasn't found (it was — resolution succeeded).
            Assert.DoesNotContain(CollapsedFindNotFound, string.Join("\n", result.Errors));

            // The real cause must be relayed, and it must be labelled as an apply failure.
            Assert.Contains(result.Errors, e =>
                e.Contains("Apply failed: Merge conflict") &&
                e.Contains("File was NOT modified"));
        }

        [Fact]
        public async Task ApplyPhaseFailure_RelaysHostCauseVerbatim()
        {
            string cause = "Write failed: access to the path is denied.";
            var host = new ScriptedPatchHost
            {
                ResolveOutcome = (Ok: true, Result: MakeResolved("target.txt"), Failure: null),
                ApplyOutcome   = (Ok: false, Path: null, Failure: cause)
            };
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            var result = await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                OutcomeWithPatch("target.txt"));

            string errors = string.Join("\n", result.Errors);
            // The cause the host reported must appear verbatim in the error.
            Assert.Contains(cause, errors);
            Assert.DoesNotContain(CollapsedFindNotFound, errors);
        }

        // ── The other half: RESOLVE-phase failure ──────────────────────────────

        [Fact]
        public async Task ResolvePhaseFailure_MustNotClaimFindTextNotFound()
        {
            // A resolve failure whose cause is explicitly NOT a find-match problem.
            string cause = "No filename specified in the PATCH header — the first line must be 'PATCH <filename>'.";
            var host = new ScriptedPatchHost
            {
                ResolveOutcome = (Ok: false, Result: null, Failure: cause)
            };
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            var result = await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                OutcomeWithPatch("target.txt"));

            string errors = string.Join("\n", result.Errors);
            Assert.True(result.PatchesFailed > 0);
            // Must not use the blanket FIND-not-found wording.
            Assert.DoesNotContain(CollapsedFindNotFound, errors);
            // Must be labelled as a resolve failure and carry the cause.
            Assert.Contains(result.Errors, e =>
                e.Contains("Resolve failed:") &&
                e.Contains(cause) &&
                e.Contains("File was NOT modified"));
        }

        // ── Exception paths: must still not collapse into FIND-not-found ───────

        [Fact]
        public async Task ApplyPhaseException_MustNotClaimFindTextNotFound()
        {
            var host = new ScriptedPatchHost
            {
                ResolveOutcome = (Ok: true, Result: MakeResolved("target.txt"), Failure: null),
                ApplyThrows = new IOException("disk full")
            };
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            var result = await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                OutcomeWithPatch("target.txt"));

            string errors = string.Join("\n", result.Errors);
            Assert.True(result.PatchesFailed > 0);
            Assert.DoesNotContain(CollapsedFindNotFound, errors);
            // Exception surfaced under the apply label with the file named.
            Assert.Contains(result.Errors, e =>
                e.Contains("[PATCH-FAILED:target.txt]") &&
                e.Contains("Apply error:") &&
                e.Contains("disk full"));
        }

        [Fact]
        public async Task ResolvePhaseException_MustNotClaimFindTextNotFound()
        {
            var host = new ScriptedPatchHost
            {
                ResolveThrows = new ArgumentException("bad patch content")
            };
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            var result = await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                OutcomeWithPatch("target.txt"));

            string errors = string.Join("\n", result.Errors);
            Assert.True(result.PatchesFailed > 0);
            Assert.DoesNotContain(CollapsedFindNotFound, errors);
            Assert.Contains(result.Errors, e =>
                e.Contains("[PATCH-FAILED:target.txt]") &&
                e.Contains("Resolve error:") &&
                e.Contains("bad patch content"));
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static ResponseOutcome OutcomeWithPatch(string fileName) =>
            new(new List<ResponseBlock>
            {
                new ResponseBlock
                {
                    Type = BlockType.Patch,
                    Content = PatchText,
                    FileName = fileName,
                    FromToolCall = true
                }
            });

        private static PatchResolveResult MakeResolved(string fileName) => new()
        {
            FullPath = fileName,
            FileName = fileName,
            Confidence = PatchConfidence.Exact,   // auto-apply path, no preview gate
            OriginalContent = "foo\n",
            FileEncoding = System.Text.Encoding.UTF8
        };

        /// <summary>
        /// IAgenticHost stub that scripts exactly the two patch calls the executor
        /// makes for a single exact-confidence patch: ResolvePatchAsync then
        /// ApplyResolvedPatchAsync. Every other member is inert.
        /// </summary>
        private sealed class ScriptedPatchHost : IAgenticHost
        {
            public (bool Ok, PatchResolveResult Result, string Failure) ResolveOutcome;
            public (bool Ok, string Path, string Failure) ApplyOutcome;
            public Exception ResolveThrows;
            public Exception ApplyThrows;

            public string Output { get; private set; } = "";

            public Task<(PatchResolveResult, string)> ResolvePatchAsync(string patchContent, bool fromToolCall = false)
            {
                if (ResolveThrows != null) throw ResolveThrows;
                return Task.FromResult<(PatchResolveResult, string)>(
                    ResolveOutcome.Ok
                        ? (ResolveOutcome.Result, (string)null)
                        : (null, ResolveOutcome.Failure));
            }

            public Task<(string, string)> ApplyResolvedPatchAsync(PatchResolveResult resolved)
            {
                if (ApplyThrows != null) throw ApplyThrows;
                return Task.FromResult<(string, string)>(
                    ApplyOutcome.Ok
                        ? (ApplyOutcome.Path, (string)null)
                        : (null, ApplyOutcome.Failure));
            }

            public Task<List<int>> ShowDiffPreviewAsync(List<PatchResolveResult> resolvedPatches, CancellationToken cancellationToken)
                => Task.FromResult(new List<int>());

            // ── Inert members (unused by a patch-only turn) ──────────────────
            public Task<(int, string)> RunShellAsync(string command, int? timeoutSeconds = null)
                => Task.FromResult((0, ""));
            public Task<string> SaveFileAsync(string fileName, string content, bool fromToolCall = false)
                => Task.FromResult(fileName);
            public Task<string> AppendFileAsync(string fileName, string content) => Task.FromResult(fileName);
            public Task<string> LoadFileContentAsync(string fileName, int rangeStart = 0, int rangeEnd = 0, bool forceFullRead = false)
                => Task.FromResult("");
            public void AppendOutput(string text, OutputColor color = OutputColor.Normal) => Output += text;
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
            public Task<string> RecallMemoryAsync(string topic) => Task.FromResult("");
            public Task<string> SaveMemoryAsync(string topic, string content, string description)
                => Task.FromResult("ok");
            public Task<string> ListMemoryTopicsAsync() => Task.FromResult("");
            public Task<string> SearchMemoryAsync(string pattern) => Task.FromResult("");
            public Task<string> QueryLibraryAsync(string question, int topK, CancellationToken cancellationToken = default)
                => Task.FromResult("");
            public Task<string> ListFilesAsync(string glob, bool recursive, CancellationToken cancellationToken = default)
                => Task.FromResult("");
            public int GetPatchBackupCount() => 0;
            public Task<string> GetDiagnosticsAsync(string filename) => Task.FromResult("");
            public Task<string> GoToDefinitionAsync(string filename, int line, int character) => Task.FromResult("");
            public Task<string> FindReferencesAsync(string filename, int line, int character) => Task.FromResult("");
            public Task<string> HoverAsync(string filename, int line, int character) => Task.FromResult("");
            public Task<string> FindSymbolAsync(string query, int maxResults, string language) => Task.FromResult("");
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
