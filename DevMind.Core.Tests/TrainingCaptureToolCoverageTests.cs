// File: TrainingCaptureToolCoverageTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The training corpus was blind to two thirds of the tool surface. ExtractToolCalls
// switched over BlockType with eleven cases and no default arm, so scratchpad, ask_caller,
// append_file, list_files and every LSP / memory / web / learn / library / cache tool
// vanished from tool_calls regardless of what the model did. That produced a confident
// wrong answer — "zero scratchpad calls across 47 jobs" — about a tool the model narrates
// using in 60 of 317 transcripts.
//
// Compounding it, a SUCCESSFUL scratchpad update emitted nothing to the job transcript
// (output only on the catch path), so the second instrument was blind too.
//
// Both halves are pinned here. The corpus half asserts an unlisted type survives into
// tool_calls and that the eleven listed cases are untouched; the transcript half drives a
// real AgenticExecutor and asserts the success line lands.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class TrainingCaptureToolCoverageTests
    {
        private static ResponseBlock Block(BlockType type, string? content = null, string? fileName = null)
            => new ResponseBlock { Type = type, Content = content, FileName = fileName };

        // ── The corpus half ─────────────────────────────────────────────────────

        [Theory]
        [InlineData(BlockType.Scratchpad, "scratchpad")]
        [InlineData(BlockType.NeedsInput, "needs_input")]
        [InlineData(BlockType.AppendFile, "append_file")]
        [InlineData(BlockType.ListFiles, "list_files")]
        [InlineData(BlockType.GetDiagnostics, "get_diagnostics")]
        [InlineData(BlockType.RecallCache, "recall_cache")]
        [InlineData(BlockType.LearnCodeSearch, "learn_code_search")]
        [InlineData(BlockType.RunSql, "run_sql")]
        public void ABlockTypeOutsideTheElevenListedCases_SurvivesIntoToolCalls(BlockType type, string expectedName)
        {
            var calls = JsonlTrainingLogger.ExtractToolCalls(new List<ResponseBlock> { Block(type, "x") });

            Assert.NotNull(calls);
            var entry = Assert.Single(calls!);
            Assert.Equal(expectedName, entry.Type);
        }

        // Every BlockType except prose must produce an entry. Enumerated from the enum itself
        // so a type added later cannot be dropped by omission — the exact defect this fixes.
        [Fact]
        public void EveryNonTextBlockType_ProducesAToolCallEntry()
        {
            foreach (BlockType type in Enum.GetValues<BlockType>())
            {
                if (type == BlockType.Text) continue;

                var calls = JsonlTrainingLogger.ExtractToolCalls(new List<ResponseBlock> { Block(type, "x", "a.cs") });

                Assert.True(calls != null && calls.Count == 1,
                    $"BlockType.{type} produced no tool_calls entry — it has vanished from the corpus again");
                Assert.False(string.IsNullOrEmpty(calls![0].Type), $"BlockType.{type} recorded an empty type");
            }
        }

        // Prose is not a tool call. A default arm that swept it in would record every
        // narration turn as a call and inflate every adoption count.
        [Fact]
        public void ProseText_IsStillNotRecordedAsAToolCall()
        {
            var calls = JsonlTrainingLogger.ExtractToolCalls(new List<ResponseBlock> { Block(BlockType.Text, "just talking") });

            Assert.Null(calls);
        }

        // The eleven listed cases keep their exact shape — names and which field they fill.
        [Fact]
        public void TheListedCases_AreUnchanged()
        {
            var blocks = new List<ResponseBlock>
            {
                new ResponseBlock { Type = BlockType.ReadRequest, FileName = "a.cs" },
                new ResponseBlock { Type = BlockType.Shell, Command = "dotnet build" },
                new ResponseBlock { Type = BlockType.Grep, FileName = "a.cs", Pattern = "TODO" },
                new ResponseBlock { Type = BlockType.Find, GlobPattern = "*.cs", Pattern = "class" },
                new ResponseBlock { Type = BlockType.Rename, RenameFrom = "old.cs", RenameTo = "new.cs" },
                new ResponseBlock { Type = BlockType.Test, TestProject = "X.csproj" },
                new ResponseBlock { Type = BlockType.Done, Content = "finished" },
            };

            var calls = JsonlTrainingLogger.ExtractToolCalls(blocks)!;

            Assert.Equal(new[] { "read", "shell", "grep", "find", "rename", "test", "done" }, calls.Select(c => c.Type));
            Assert.Equal("a.cs", calls[0].Filename);
            Assert.Equal("dotnet build", calls[1].Command);
            Assert.Equal("TODO", calls[2].Pattern);
            Assert.Equal("*.cs", calls[3].Filename);          // Find records the glob in Filename
            Assert.Equal("old.cs", calls[4].Filename);        // Rename records the source
            Assert.Equal("X.csproj", calls[5].Filename);
            Assert.Equal("finished", calls[6].Summary);
        }

        // The default arm records the type and nothing else — a plausible wrong field is
        // worse for analysis than an absent one.
        [Fact]
        public void TheDefaultArm_RecordsTypeOnly()
        {
            var calls = JsonlTrainingLogger.ExtractToolCalls(new List<ResponseBlock>
            {
                new ResponseBlock { Type = BlockType.Scratchpad, Content = "plan: …", FileName = "should-not-leak.cs", Command = "nor-this" },
            })!;

            var e = Assert.Single(calls);
            Assert.Equal("scratchpad", e.Type);
            Assert.Null(e.Filename);
            Assert.Null(e.Command);
            Assert.Null(e.Pattern);
            Assert.Null(e.Summary);
        }

        [Theory]
        [InlineData(BlockType.Scratchpad, "scratchpad")]
        [InlineData(BlockType.NeedsInput, "needs_input")]
        [InlineData(BlockType.LearnCodeSearch, "learn_code_search")]
        [InlineData(BlockType.Hover, "hover")]
        public void TypeNames_AreLowerSnakeCaseOfTheEnumName(BlockType type, string expected)
        {
            Assert.Equal(expected, JsonlTrainingLogger.ToolCallTypeName(type));
        }

        // ── The transcript half ─────────────────────────────────────────────────

        [Fact]
        public async Task ASuccessfulScratchpadUpdate_LeavesALineInTheTranscript()
        {
            using var env = new Env();
            var executor = new AgenticExecutor(env.Host, new FakeLlmOptions());
            var outcome = new ResponseOutcome(new List<ResponseBlock>
            {
                Block(BlockType.Scratchpad, "goal: fix parser\nnext: run tests"),
            });

            var result = await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild }, outcome);

            Assert.Empty(result.Errors);
            Assert.Contains("[SCRATCHPAD] updated (", env.Host.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("[SCRATCHPAD ERROR]", env.Host.Output, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ClearingTheScratchpad_SaysSo()
        {
            using var env = new Env();
            var executor = new AgenticExecutor(env.Host, new FakeLlmOptions());
            var outcome = new ResponseOutcome(new List<ResponseBlock> { Block(BlockType.Scratchpad, "   ") });

            await executor.ExecuteAsync(new AgenticAction { Type = ActionType.ApplyAndBuild }, outcome);

            Assert.Contains("[SCRATCHPAD] cleared", env.Host.Output, StringComparison.Ordinal);
        }
    }
}
