// File: TranscriptNoiseTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// What the TUI transcript is allowed to show.
//
// The filter this pins was correct and had almost no effect, for a reason no unit test
// existed to catch: it was applied in IAgenticHost.AppendOutput, and the three lines that
// actually flooded the transcript — [LLM], [CONTEXT] and [TOOL_USE] — never travel through
// AppendOutput. Core emits them through the SSE onToken callback, and the token path
// rendered them as prose. Nothing disagreed with anything; the guard simply stood at the
// wrong door.
//
// So the samples below are not invented. They are the three lines copied verbatim from the
// screenshot that started this work, and they are driven through ShouldRenderToken — the
// decision the TOKEN path makes — not only through IsSuppressed. A regression that puts the
// filter back on one door only leaves these failing.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class TranscriptNoiseTests
    {
        // ── The three lines from the screenshot, verbatim ────────────────────────

        private const string LlmLine =
            "[LLM] 51 tok in 0.5s (104.8 tok/s) | Prompt: 133,192 / 262,144 (50%) | delta +998 | reuse 98%\n";

        private const string ContextLine =
            "[CONTEXT] 133,192 / 262,144 (50%) | Avg delta: 961 | Safe ceiling: 259,261\n";

        private const string ToolUseLine = "[TOOL_USE] Processing tool call(s)...\n";

        [Theory]
        [InlineData(LlmLine)]
        [InlineData(ContextLine)]
        [InlineData(ToolUseLine)]
        public void TheScreenshotLines_NeverReachTheTranscript_OnTheTokenPath(string token)
        {
            Assert.True(TranscriptNoise.IsInternalStatusLine(token),
                "The token path must recognise this as engine output, or it would also feed the token counter.");
            Assert.False(TranscriptNoise.ShouldRenderToken(token, isStatusLine: true, verbose: false),
                $"This line belongs in the status bar, not the scrollback: {token.TrimEnd()}");
        }

        [Theory]
        [InlineData(LlmLine)]
        [InlineData(ContextLine)]
        [InlineData(ToolUseLine)]
        public void VerboseRestoresTheFirehose_OnTheTokenPath(string token)
        {
            Assert.True(TranscriptNoise.ShouldRenderToken(token, isStatusLine: true, verbose: true));
        }

        [Fact]
        public void ModelProse_IsNeverTakenForAStatusLine()
        {
            Assert.False(TranscriptNoise.IsInternalStatusLine("The [Fact] attribute marks a test.\n"));
            Assert.True(TranscriptNoise.ShouldRenderToken("The [Fact] attribute marks a test.\n",
                isStatusLine: false, verbose: false));
        }

        // ── Every line class in the filter's table ───────────────────────────────

        [Theory]
        [InlineData("[TOOL_USE] Processing tool call(s)...\n")]
        [InlineData("[LLM] 51 tok in 0.5s (104.8 tok/s)\n")]
        [InlineData("[AGENTIC] Iteration 3/40\n")]
        [InlineData("[CONTEXT] 133,192 / 262,144 (50%)\n")]
        [InlineData("[CONTEXT] ~48,000 tok (estimate)\n")]
        [InlineData("[CONTEXT] Working: 12 messages\n")]
        public void RoutineChurn_IsSuppressed(string line)
        {
            Assert.True(TranscriptNoise.IsSuppressed(line, verbose: false));
        }

        [Theory]
        [InlineData("[CONTEXT] CRITICAL — 98% of the window\n")]
        [InlineData("[CONTEXT] Hard trim: dropped 4 messages\n")]
        [InlineData("[CONTEXT] Soft trim applied\n")]
        [InlineData("[CONTEXT] Compacting…\n")]
        [InlineData("[CONTEXT] Warning: approaching the limit\n")]
        [InlineData("[AGENTIC] Task complete\n")]
        [InlineData("[AGENTIC] Depth cap reached\n")]
        [InlineData("[AGENTIC] Cancelled\n")]
        [InlineData("[DROPPED] 2 tool results\n")]
        [InlineData("[SHELL] > git status --short\n")]
        [InlineData("[READ] Program.cs (1,204 lines)\n")]
        [InlineData("[PATCH] Applied 1 hunk\n")]
        [InlineData("[TEST] > dotnet test\n")]
        [InlineData("[DIFF] Program.cs\n")]
        [InlineData("[LSP] 3 diagnostics\n")]
        public void EventsAndToolCalls_AreKept(string line)
        {
            Assert.False(TranscriptNoise.IsSuppressed(line, verbose: false));
        }

        [Theory]
        [InlineData("[TOOL_USE] Processing tool call(s)...\n")]
        [InlineData("[LLM] 51 tok in 0.5s\n")]
        [InlineData("[AGENTIC] Iteration 3/40\n")]
        [InlineData("[CONTEXT] 133,192 / 262,144 (50%)\n")]
        public void VerboseKeepsEverything(string line)
        {
            Assert.False(TranscriptNoise.IsSuppressed(line, verbose: true));
        }

        [Fact]
        public void ALeadingNewline_DoesNotHideTheTag()
        {
            // Core's onToken lines routinely arrive with a leading "\n"; matching at
            // position 0 only would have let every one of them through.
            Assert.True(TranscriptNoise.IsSuppressed("\n[LLM] 51 tok in 0.5s\n", verbose: false));
            Assert.True(TranscriptNoise.IsSuppressed("\n\t [CONTEXT] 12 / 40\n", verbose: false));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("   \n")]
        public void NothingToClassify_IsKept(string line)
        {
            Assert.False(TranscriptNoise.IsSuppressed(line, verbose: false));
        }
    }
}
