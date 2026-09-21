// File: PatchPartialFailureToolResultTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for patch_file partial-failure reporting. A patch_file call
// can target several files; when only some resolved/applied, the old
// LoopHelpers arm returned the success header ("[PATCH applied to ...]") the
// moment PatchedPaths was non-empty and never reached the result.Errors check,
// so the model was told the call succeeded and never told which file did not
// change. (bd7a581 ruled patch_file safe because its branch "already tests
// result.Errors before its fallback" — true only when PatchedPaths is empty.
// On a partial success the Errors check was unreachable.)
//
// The producing code (AgenticExecutor) already records every failure mode in
// result.Errors under a "[PATCH-FAILED:" or "[PATCH-SKIPPED:" prefix and
// increments result.PatchesFailed; these tests pin the rendering fix in
// LoopHelpers.BuildToolResultContent.

using Xunit;

namespace DevMind.Core.Tests
{
    public class PatchPartialFailureToolResultTests
    {
        // ── Partial success: the actual defect ────────────────────────────────

        [Fact]
        public void PatchFile_PartialSuccess_ReportsAppliedHeaderAndNamesFailedFile()
        {
            var result = ExecutionResult.None();
            result.PatchedPaths.Add(@"C:\work\ok.cs");
            result.PatchesFailed++;
            result.Errors.Add("[PATCH-FAILED:C:\\work\\bad.cs] Resolve failed: FIND text not found. File was NOT modified.");

            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("patch_file", "filename", @"C:\work\bad.cs"),
                result,
                null);

            // Both halves must be present: what worked AND what did not.
            Assert.StartsWith("[PATCH applied to C:\\work\\ok.cs]", content);
            Assert.Contains("[PATCH-FAILED:", content);
            Assert.Contains(@"C:\work\bad.cs", content);
        }

        [Fact]
        public void PatchFile_PartialSuccess_SkippedPatchIsReportedToo()
        {
            var result = ExecutionResult.None();
            result.PatchedPaths.Add(@"C:\work\ok.cs");
            result.PatchesFailed++;
            result.Errors.Add("[PATCH-SKIPPED: C:\\work\\cancel.cs \u2014 user cancelled. Re-READ the file if you need to try again.]");

            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("patch_file", "filename", @"C:\work\cancel.cs"),
                result,
                null);

            Assert.StartsWith("[PATCH applied to C:\\work\\ok.cs]", content);
            Assert.Contains("[PATCH-SKIPPED:", content);
            Assert.Contains(@"C:\work\cancel.cs", content);
        }

        // ── Regression guard: the all-succeeded output is byte-identical ──────

        [Fact]
        public void PatchFile_AllSucceeded_NoEchoes_OutputUnchanged()
        {
            var result = ExecutionResult.None();
            result.PatchedPaths.Add(@"C:\work\a.cs");
            result.PatchedPaths.Add(@"C:\work\b.cs");

            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("patch_file", "filename", @"C:\work\a.cs"),
                result,
                null);

            Assert.Equal("[PATCH applied to C:\\work\\a.cs, C:\\work\\b.cs]", content);
        }

        [Fact]
        public void PatchFile_AllSucceeded_WithEchoes_OutputUnchanged()
        {
            var result = ExecutionResult.None();
            result.PatchedPaths.Add(@"C:\work\a.cs");
            result.ToolResultContents[@"C:\work\a.cs"] = "  1 | using DevMind;\n  2 | public class A {}";

            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("patch_file", "filename", @"C:\work\a.cs"),
                result,
                null);

            Assert.Equal(
                "[PATCH applied to C:\\work\\a.cs]\n  1 | using DevMind;\n  2 | public class A {}",
                content);
        }

        // ── Nothing patched, error present: today's behaviour, kept ───────────

        [Fact]
        public void PatchFile_NothingPatched_ErrorPresent_FailureReported()
        {
            var result = ExecutionResult.None();
            result.PatchesFailed++;
            result.Errors.Add("[PATCH-FAILED:C:\\work\\bad.cs] Apply failed: FIND text not found. File was NOT modified.");

            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("patch_file", "filename", @"C:\work\bad.cs"),
                result,
                null);

            Assert.Equal(
                "[PATCH-FAILED: [PATCH-FAILED:C:\\work\\bad.cs] Apply failed: FIND text not found. File was NOT modified.]",
                content);
        }

        // ── Nothing patched, no error: must NOT read as success ───────────────

        [Fact]
        public void PatchFile_NothingPatched_NoError_ExplicitFailureNotProcessed()
        {
            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("patch_file", "filename", @"C:\work\empty.cs"),
                ExecutionResult.None(),
                null);

            Assert.DoesNotContain("[PATCH processed]", content);
            Assert.Contains("PATCH_FILE FAILED", content);
            Assert.Contains("NOT modified", content);
            Assert.Contains("find-text", content);
        }

        // ── Errors from other tools must not be attributed to patch_file ──────

        [Fact]
        public void PatchFile_AllSucceeded_UnrelatedErrorNotAttributedToPatch()
        {
            var result = ExecutionResult.None();
            result.PatchedPaths.Add(@"C:\work\a.cs");
            // A failure recorded by a DIFFERENT tool in the same iteration
            // (e.g. run_shell) must not appear in the patch_file result.
            result.Errors.Add("some other tool blew up");

            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("patch_file", "filename", @"C:\work\a.cs"),
                result,
                null);

            Assert.Equal("[PATCH applied to C:\\work\\a.cs]", content);
            Assert.DoesNotContain("some other tool blew up", content);
        }

        [Fact]
        public void PatchFile_PartialSuccess_UnrelatedErrorNotAttributedToPatch()
        {
            var result = ExecutionResult.None();
            result.PatchedPaths.Add(@"C:\work\ok.cs");
            result.PatchesFailed++;
            result.Errors.Add("[PATCH-FAILED:C:\\work\\bad.cs] Apply failed: FIND text not found. File was NOT modified.");
            result.Errors.Add("shell command timed out");

            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("patch_file", "filename", @"C:\work\bad.cs"),
                result,
                null);

            Assert.Contains("[PATCH-FAILED:", content);
            Assert.Contains(@"C:\work\bad.cs", content);
            Assert.DoesNotContain("shell command timed out", content);
        }

        // ── PatchesFailed cross-check ─────────────────────────────────────────

        [Fact]
        public void PatchFile_FailureCountMatches_NoCrossCheckNote()
        {
            var result = ExecutionResult.None();
            result.PatchesFailed++;
            result.Errors.Add("[PATCH-FAILED:C:\\work\\bad.cs] Resolve error: boom");

            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("patch_file", "filename", @"C:\work\bad.cs"),
                result,
                null);

            // Count agrees with the listing — no redundant cross-check noise.
            Assert.DoesNotContain("recorded patch failures shown", content);
        }

        [Fact]
        public void PatchFile_FailureCountDisagrees_CrossCheckCited()
        {
            var result = ExecutionResult.None();
            result.PatchesFailed = 2;   // e.g. another failure was recorded out of band
            result.Errors.Add("[PATCH-FAILED:C:\\work\\bad.cs] Resolve error: boom");

            string content = LoopHelpers.BuildToolResultContent(
                MakeToolCall("patch_file", "filename", @"C:\work\bad.cs"),
                result,
                null);

            Assert.Contains("1 of 2 recorded patch failures shown", content);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static ToolCallResult MakeToolCall(string name, string argName, string argValue)
            => new ToolCallResult
            {
                Id = "call-test",
                Name = name,
                Arguments = new Dictionary<string, string> { [argName] = argValue }
            };
    }
}
