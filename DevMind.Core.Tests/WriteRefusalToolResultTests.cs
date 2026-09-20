// File: WriteRefusalToolResultTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for write-refusal reporting: a write refused by the host
// (path outside the allowed write roots, write guard, pending merge conflict)
// returned a success-shaped tool_result — create_file reported "[File created]",
// append_file "[Content appended]", delete_file "[File deleted]",
// rename_file "[File renamed]" — so a model believed the file existed and spent
// iterations failing against a file that had never been written.
//
// The refusal itself (BufferedAgenticHost.IsWriteAllowed → null) is correct and
// unchanged; these tests prove the tool_result now reads unmistakably as a
// failure. The integration tests drive the real path:
//   AgenticExecutor → IAgenticHost.SaveFileAsync/AppendFileAsync (sandboxed
//   BufferedAgenticHost, path outside the working directory) → null → empty
//   FilesCreated/FilesAppended → LoopHelpers.BuildToolResultContent.
// The unit tests pin the refusal message shape (failure marker, target path,
// write-roots guidance when no cause is on record) and confirm the success
// message is untouched.

using Xunit;

namespace DevMind.Core.Tests
{
    public class WriteRefusalToolResultTests : IDisposable
    {
        private readonly string _outside;
        private readonly string _outsideFile;

        public WriteRefusalToolResultTests()
        {
            // Sibling of the working directory — strictly outside it (the root
            // match is separator-anchored, so a sibling sharing a prefix cannot
            // slip through).
            _outside = Path.Combine(Path.GetTempPath(), $"devmind_wrt_outside_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_outside);
            _outsideFile = Path.Combine(_outside, "sandbox-target.txt");
        }

        public void Dispose()
        {
            try { Directory.Delete(_outside, recursive: true); } catch { }
        }

        // ── Integration: the real refusal path end-to-end ─────────────────────

        [Fact]
        public async Task CreateFile_RefusedBySandbox_ToolResultReportsFailure()
        {
            var (result, content) = await RunWriteToolAsync(
                "create_file",
                new ResponseBlock
                {
                    Type = BlockType.File,
                    FileName = _outsideFile,
                    Content = "hello",
                    FromToolCall = true
                });

            Assert.Empty(result.FilesCreated);          // nothing was written
            Assert.False(File.Exists(_outsideFile));    // ... and the disk agrees
            AssertFailureShape(content, "CREATE_FILE");
        }

        [Fact]
        public async Task AppendFile_RefusedBySandbox_ToolResultReportsFailure()
        {
            var (result, content) = await RunWriteToolAsync(
                "append_file",
                new ResponseBlock
                {
                    Type = BlockType.AppendFile,
                    FileName = _outsideFile,
                    Content = "hello",
                    FromToolCall = true
                });

            Assert.Empty(result.FilesAppended);         // nothing was appended
            Assert.False(File.Exists(_outsideFile));    // ... and the disk agrees
            AssertFailureShape(content, "APPEND_FILE");
        }

        [Fact]
        public async Task DeleteFile_RefusedBySandbox_ToolResultReportsFailure()
        {
            // The file must exist first: a missing path fails at file resolution
            // ("not found") and never reaches the sandbox gate. The host returns
            // a non-empty "DELETE blocked: ..." string, which the executor routes
            // into result.Errors — the message must carry that cause, never the
            // old "[File deleted]" success line.
            File.WriteAllText(_outsideFile, "victim");

            var (result, content) = await RunWriteToolAsync(
                "delete_file",
                new ResponseBlock
                {
                    Type = BlockType.Delete,
                    FileName = _outsideFile,
                    FromToolCall = true
                });

            Assert.Empty(result.FilesDeleted);          // nothing was deleted
            Assert.True(File.Exists(_outsideFile));     // ... and the disk agrees
            AssertFailureShape(content, "DELETE_FILE");
        }

        // ── Unit: refusal message shape ────────────────────────────────────────

        [Fact]
        public void CreateFile_EmptyResult_DefaultRefusalMessageNamesWriteRoots()
        {
            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("create_file", "filename", @"C:\Users\someone\AppData\Local\Temp\scratch.txt"),
                ExecutionResult.None(),
                null);

            AssertFailureShape(content, "CREATE_FILE");
            // Actionable: says WHERE writes are allowed, since the host's
            // null-return refusal adds no cause to result.Errors.
            Assert.Contains("allowed write roots", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"C:\Users\someone\AppData\Local\Temp\scratch.txt", content);
        }

        [Fact]
        public void CreateFile_FailureRelaysRecordedErrorWhenPresent()
        {
            var result = ExecutionResult.None();
            result.Errors.Add("disk full");

            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("create_file", "filename", "notes.txt"),
                result,
                null);

            AssertFailureShape(content, "CREATE_FILE");
            Assert.Contains("disk full", content);
        }

        // ── Unit: the success path is untouched ────────────────────────────────

        [Fact]
        public void CreateFile_Success_StillReportsCreatedPath()
        {
            var result = ExecutionResult.None();
            result.FilesCreated.Add(@"C:\work\new.txt");

            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("create_file", "filename", @"C:\work\new.txt"),
                result,
                null);

            Assert.Equal("[File created: C:\\work\\new.txt]", content);
        }

        [Fact]
        public void AppendFile_Success_StillReportsAppendedPath()
        {
            var result = ExecutionResult.None();
            result.FilesAppended.Add(@"C:\work\log.txt");

            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("append_file", "filename", @"C:\work\log.txt"),
                result,
                null);

            Assert.Equal("[Content appended to C:\\work\\log.txt]", content);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>Asserts the tool_result reads as failure, not success.</summary>
        private static void AssertFailureShape(string content, string upperToolName)
        {
            Assert.Contains($"{upperToolName} FAILED", content);
            Assert.DoesNotContain("File created", content);
            Assert.DoesNotContain("Content appended", content);
            Assert.DoesNotContain("File deleted", content);
            Assert.DoesNotContain("File renamed", content);

            // Every tool_result in the LoopHelpers switch is bracket-delimited.
            // The first version of the failure message opened "[" and never closed
            // it, and the assertions above passed anyway because they only check
            // for substrings - so the delimiters are pinned explicitly here.
            Assert.StartsWith("[", content);
            Assert.EndsWith("]", content);
        }

        private static ToolCallResult MakeToolCall(string name, string argName, string argValue)
            => new ToolCallResult
            {
                Id = "call-test",
                Name = name,
                Arguments = new Dictionary<string, string> { [argName] = argValue }
            };

        /// <summary>
        /// Runs one write tool call through the real AgenticExecutor against a
        /// sandboxed BufferedAgenticHost (writes outside the working directory
        /// refused with null) and returns the ExecutionResult plus the
        /// tool_result string the model receives.
        /// </summary>
        private async Task<(ExecutionResult result, string content)> RunWriteToolAsync(
            string toolName, ResponseBlock block)
        {
            string workDir = Path.Combine(Path.GetTempPath(), $"devmind_wrt_work_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(workDir);
                var host = new BufferedAgenticHost(workDir) { RestrictWritesToWorkingDirectory = true };
                var executor = new AgenticExecutor(host, new FakeLlmOptions());
                executor.SetCancellationToken(CancellationToken.None);

                ExecutionResult result = await executor.ExecuteAsync(
                    new AgenticAction { Type = ActionType.ApplyAndBuild },
                    new ResponseOutcome(new List<ResponseBlock> { block }));

                string content = LoopHelpers.BuildToolResultContent(
                    MakeToolCall(toolName, "filename", _outsideFile),
                    result,
                    new List<ResponseBlock> { block });
                return (result, content);
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { }
            }
        }
    }
}
