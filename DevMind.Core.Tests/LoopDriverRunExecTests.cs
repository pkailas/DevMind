// File: LoopDriverRunExecTests.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Covers the run/exec implicit-DONE fallback in LoopDriver:
//   * preserved: a successful `dotnet run` as the ONLY shell work of a pure-shell
//     turn still terminates the loop (rescues "build and run this" tasks that
//     never emit task_done).
//   * fixed bug: a successful `dotnet run` AFTER file work (scaffold → run) or
//     after a prior successful run/exec is an intermediate step — the loop must
//     re-trigger, not truncate the task (e.g. "run this and report on it").
//   * IsRunOrExecCommand no longer classifies bare "*.exe" commands.
//   * explicit task_done still wins (checked before the fallback).

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class LoopDriverRunExecTests
    {
        // ── IsRunOrExecCommand classification ────────────────────────────────────

        [Theory]
        [InlineData("dotnet run", true)]
        [InlineData("dotnet run --project src/App", true)]
        [InlineData("dotnet exec app.dll", true)]
        [InlineData("dotnet run; echo done", true)]
        [InlineData("dotnet build", false)]
        [InlineData("dotnet test", false)]
        [InlineData(@"C:\temp\app\bin\app.exe", false)]          // bare .exe — dropped on purpose
        [InlineData(@"C:\temp\app.exe --check", false)]          // bare .exe with args
        [InlineData(@"MSBuild.exe My.csproj", false)]
        [InlineData("", false)]
        [InlineData("git status", false)]
        public void IsRunOrExecCommand_Classifies(string command, bool expected)
        {
            Assert.Equal(expected, LoopHelpers.IsRunOrExecCommand(command));
        }

        [Fact]
        public void LoopState_ResetForUserTurn_ClearsRunExecGateFlags()
        {
            var state = new LoopState
            {
                HadFileMutationThisTurn  = true,
                RunExecSucceededThisTurn = true,
            };

            state.ResetForUserTurn();

            Assert.False(state.HadFileMutationThisTurn);
            Assert.False(state.RunExecSucceededThisTurn);
        }

        // ── LoopDriver fallback behaviour ─────────────────────────────────────────

        [Fact]
        public async Task RunExec_PureShellTurn_FirstRun_Terminates()
        {
            using var env = new Env();
            // "Build and run this": no file tools, no task_done — the run is the deliverable.
            var turn1 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet build\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn1.Kind); // build ≠ run/exec — loop continues

            var turn2 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run\"}"));
            Assert.Equal(LoopIterationKind.Terminal, turn2.Kind);
            Assert.Contains("Run/exec command succeeded", env.Host.Output);
        }

        [Fact]
        public async Task RunExec_AfterScaffolding_Files_DoesNotTerminate()
        {
            using var env = new Env();
            // The observed bug: task is "produce a written analysis". The model scaffolds
            // a throwaway app (file work) and runs it to probe runtime behaviour, then must
            // still write the analysis. The successful run must NOT end the task.
            var turn1 = await env.RunToolTurn(Tool("create_file",
                "{\"filename\":\"probe.cs\",\"content\":\"using System;\\nstatic class P{static void Main(){System.Console.WriteLine(1);}}\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn1.Kind);
            Assert.True(env.State.HadFileMutationThisTurn);

            // Before the fix, this iteration returned Terminal ("treating as task complete").
            var turn2 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn2.Kind);
            Assert.DoesNotContain("treating as task complete", env.Host.Output);
            Assert.True(env.State.RunExecSucceededThisTurn);
        }

        [Fact]
        public async Task RunExec_SecondSuccessfulRunSameTurn_DoesNotTerminate()
        {
            using var env = new Env();
            // A successful run that was gated by prior file work sets the
            // RunExecSucceededThisTurn flag; a LATER run/exec in the same turn is
            // always an intermediate compare-and-check step, never the deliverable.
            await env.RunToolTurn(Tool("create_file",
                "{\"filename\":\"probe.cs\",\"content\":\"x\"}"));

            var run1 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, run1.Kind);
            Assert.True(env.State.RunExecSucceededThisTurn);

            var run2 = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run --variant two\"}"));
            Assert.Equal(LoopIterationKind.ShouldReTrigger, run2.Kind);
            Assert.DoesNotContain("treating as task complete", env.Host.Output);
        }

        [Fact]
        public async Task RunExec_Failing_DoesNotTerminate()
        {
            using var env = new Env();
            env.Host.ShellResults["dotnet run"] = (exitCode: 1, output: "Unhandled exception");

            var turn = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dotnet run\"}"));

            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);
            Assert.False(env.State.RunExecSucceededThisTurn);
        }

        [Fact]
        public async Task ExplicitTaskDone_TakesPrecedenceOverRunExecFallback()
        {
            using var env = new Env();
            // Model packs a final shell run together with task_done: the explicit
            // DONE check must win (order preserved — the fallback stays a fallback).
            var turn = await env.RunToolTurn(
                Tool("run_shell", "{\"command\":\"dotnet run\"}"),
                Tool("task_done", "{\"summary\":\"done\"}"));

            Assert.Equal(LoopIterationKind.Terminal, turn.Kind);
            Assert.Contains("Task complete.", env.Host.Output);
        }

        // ── Prose-finish re-prompt: task_done vs ask_caller ─────────────────────────
        // The signalling gap: a model that ends in prose with questions buried in it,
        // instead of calling ask_caller, terminates as "done" and the caller never learns
        // input is wanted. The structural hook is the no-tool-call terminal gate. These
        // tests pin that the one-shot re-prompt (a) fires for a long prose ending and
        // (b) presents BOTH terminal tools — task_done AND ask_caller — so the model
        // makes the done-vs-needs_input decision at the exact point where it stops.

        [Fact]
        public async Task ProseFinish_NoToolCall_FiresOneShotRePromptWithBothTerminalTools()
        {
            using var env = new Env();
            // A prose ending with no tool call (the "2 of 4 runs" failure mode — the
            // model writes numbered questions in the final text and stops).
            string prose = "I need to clarify a couple of things before proceeding: " +
                           "1. Should the endpoint use SQL or an ORM? 2. What auth is expected?";

            var turn = await env.RunProseTurn(prose);

            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);
            Assert.True(env.State.PromptedForTaskDone);          // one-shot consumed
            Assert.True(env.State.ShellLoopPending);
            // The re-prompt must name BOTH terminal tools so the decision is explicit.
            Assert.Contains("task_done", turn.NextContextualMessage);
            Assert.Contains("ask_caller", turn.NextContextualMessage);
            Assert.Equal(SyntheticPrompts.ProseFinish, turn.NextContextualMessage);
        }

        [Fact]
        public async Task ProseFinish_ShortQuestionOnlyAlso_FiresRePrompt()
        {
            using var env = new Env();
            // Regression for the loosened gate: a SHORT question-only ending (fewer
            // than the old 40-char threshold) must still reach the decision prompt,
            // not terminate as "done" with the question buried in the answer.
            var turn = await env.RunProseTurn("Should I use SQL or ORM?");

            Assert.Equal(LoopIterationKind.ShouldReTrigger, turn.Kind);
            Assert.True(env.State.PromptedForTaskDone);
            Assert.Contains("ask_caller", turn.NextContextualMessage);
            Assert.Contains("task_done", turn.NextContextualMessage);
        }

        [Fact]
        public async Task ProseFinish_RePromptIsOneShot_SecondProseTerminates()
        {
            using var env = new Env();
            var first = await env.RunProseTurn("I need to clarify: which database should I target here?");
            Assert.Equal(LoopIterationKind.ShouldReTrigger, first.Kind);

            // The model STILL answers in prose (ignores the re-prompt) — the loop must
            // not loop forever: PromptedForTaskDone is already set, so it accepts
            // prose-finish and terminates (reason null → caller sees "done").
            var second = await env.RunProseTurn("I'll make a reasonable assumption and note it.");
            Assert.Equal(LoopIterationKind.Terminal, second.Kind);
            Assert.Null(second.TerminalReason);
        }

        // ── Test harness ──────────────────────────────────────────────────────────

        private static ToolCallResult Tool(string name, string argumentsJson)
        {
            var tc = new ToolCallResult { Name = name, Id = "call_1" };
            if (argumentsJson != null)
            {
                foreach (var kv in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson))
                    tc.Arguments[kv.Key] = kv.Value;
            }
            return tc;
        }

        /// <summary>
        /// One LoopDriver + fake llm/host/callbacks over a temp working directory.
        /// <see cref="RunToolTurn"/> feeds the next iteration's tool calls into
        /// FakeLlmClient and drives one ProcessIterationAsync.
        /// </summary>
        private sealed class Env : IDisposable
        {
            public LoopState State { get; } = new LoopState();
            public FakeHost Host { get; }
            private readonly FakeLlmClient _llm;
            private readonly LoopDriver _driver;
            private readonly string _dir;

            public Env()
            {
                _dir = Path.Combine(Path.GetTempPath(), $"devmind_looptest_{Guid.NewGuid():N}");
                Directory.CreateDirectory(_dir);

                _llm = new FakeLlmClient();
                Host = new FakeHost(_dir);
                var callbacks = new NoopCallbacks();
                var options = new FakeLlmOptions();
                _driver = new LoopDriver(_llm, Host, callbacks, options, State);
            }

            public Task<LoopIterationResult> RunToolTurn(params ToolCallResult[] calls)
            {
                _llm.LastToolCalls = calls.ToList();
                return _driver.ProcessIterationAsync(
                    "task", "working on it", buildCommand: "dotnet build", CancellationToken.None);
            }

            /// <summary>
            /// Drives one NO-TOOL-CALL iteration: the model answered in plain prose.
            /// <paramref name="prose"/> is the assistant response; ShellLoopPending is set so
            /// the loop is inside an agentic cycle (the real precondition — a tool turn just ran).
            /// </summary>
            public Task<LoopIterationResult> RunProseTurn(string prose)
            {
                _llm.LastToolCalls = null;
                State.ShellLoopPending = true;
                return _driver.ProcessIterationAsync(
                    "task", prose, buildCommand: "dotnet build", CancellationToken.None);
            }

            public void Dispose() => Directory.Delete(_dir, recursive: true);
        }

        /// <summary>Scripted-shell IAgenticHost: per-command (exit, output), default (0, "").</summary>
        private sealed class FakeHost : IAgenticHost
        {
            private readonly string _dir;
            public Dictionary<string, (int exitCode, string output)> ShellResults { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string Output { get; private set; } = "";

            public FakeHost(string dir) => _dir = dir;

            public Task<(int, string)> RunShellAsync(string command, int? timeoutSeconds = null)
            {
                ShellResults.TryGetValue(command, out var r);
                return Task.FromResult(r.exitCode != 0 ? r : (0, ""));
            }

            public Task<string> SaveFileAsync(string fileName, string content, bool fromToolCall = false)
                => Task.FromResult(Path.Combine(_dir, Path.GetFileName(fileName)));
            public Task<string> AppendFileAsync(string fileName, string content)
                => Task.FromResult(Path.Combine(_dir, Path.GetFileName(fileName)));
            public Task<string> LoadFileContentAsync(string fileName, int rangeStart = 0, int rangeEnd = 0, bool forceFullRead = false)
                => Task.FromResult("");
            public void AppendOutput(string text, OutputColor color = OutputColor.Normal) => Output += text;
            public void UpdateScratchpad(string content) { }
            public string TaskScratchpad => "";
            public string GetWorkingDirectory() => _dir;
            public Task<string> GrepFileAsync(string pattern, string filename, int? startLine, int? endLine) => Task.FromResult("");
            public Task<string> FindInFilesAsync(string pattern, string globPattern, int? startLine, int? endLine) => Task.FromResult("");
            public Task<string> DeleteFileAsync(string filename) => Task.FromResult("Deleted: " + filename);
            public Task<string> RenameFileAsync(string oldFilename, string newFilename) => Task.FromResult("Renamed");
            public Task<string> GetFileDiffAsync(string filename) => Task.FromResult("");
            public Task<string> RunTestsAsync(string project, string filter, int? timeoutSeconds = null) => Task.FromResult("");
            public Task<(PatchResolveResult, string)> ResolvePatchAsync(string patchContent, bool fromToolCall = false) =>
                throw new NotImplementedException("tests avoid patch blocks");
            public Task<(string, string)> ApplyResolvedPatchAsync(PatchResolveResult resolved) =>
                throw new NotImplementedException("tests avoid patch blocks");
            public Task<List<int>> ShowDiffPreviewAsync(List<PatchResolveResult> resolvedPatches, CancellationToken cancellationToken) =>
                Task.FromResult(new List<int>());
            public Task<string> RecallMemoryAsync(string topic) => Task.FromResult("");
            public Task<string> SaveMemoryAsync(string topic, string content, string description) => Task.FromResult("ok");
            public Task<string> ListMemoryTopicsAsync() => Task.FromResult("");
            public Task<string> SearchMemoryAsync(string pattern) => Task.FromResult("");
            public Task<string> QueryLibraryAsync(string question, int topK, CancellationToken cancellationToken = default) => Task.FromResult("");
            public Task<string> ListFilesAsync(string glob, bool recursive, CancellationToken cancellationToken = default) => Task.FromResult("");
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
            public Task<string> RunDebugAsync(string command, IReadOnlyDictionary<string, string> args) => Task.FromResult("");
            public Task<string> RecallCacheAsync(string handle) => Task.FromResult("");
            public Task<string> ListCacheAsync() => Task.FromResult("");
        }

        /// <summary>ILlmClient stub: LastToolCalls is scripted per iteration; context
        /// metrics are zero so the context-window guard never fires.</summary>
        private sealed class FakeLlmClient : ILlmClient
        {
            public List<ToolCallResult> LastToolCalls { get; set; }
            public int ServerContextSize => 0;
            public int MaxPromptTokens => 0;
            public int LastContextUsed => 0;
            public int LastGeneratedTokens => 0;
            public int LiveGeneratedTokens => 0;
            public int LivePromptTokens => 0;
            public double LiveTokensPerSecond => 0;
            public double LastGeneratedMs => 0;
            public int LastPromptTokens => 0;
            public int CurrentTurn => 1;
            public string SystemPromptContent => "";
            public string LastCompactionSummary => null;
            public string LastAssistantText => "";
            public int EstimateHistoryTokens() => 0;
            public void AddToolResultMessage(string toolCallId, string content, string toolName = null) { }
            public void StagePendingImage(string imageDataUri) { }
            public Task SendMessageAsync(string userMessage, Action<string> onToken, Action onComplete,
                Action<Exception> onError, bool deferCompression = false, string combinedSystemPrompt = null,
                CancellationToken cancellationToken = default, bool forceToolChoiceRequired = false,
                string imageBase64 = null, int maxTokens = 0)
                => throw new NotImplementedException("tests drive ProcessIterationAsync directly");
            public void ClearHistory(bool preserveScratchpad = false) { }
            public void PrependMessages(string[] roles, string[] contents) { }
        }

        private sealed class NoopCallbacks : ILoopCallbacks
        {
            public void AppendNewLine() { }
            public void SetStatus(string text) { }
            public void SetContextIndicator(string text) { }
            public void SetInputText(string text) { }
            public string GetInputText() => "";
            public void FocusInput() { }
            public void SetInputEnabled(bool enabled) { }
            public void StartThinkingTimer(int depth, int maxDepth) { }
            public void StopThinkingTimer() { }
            public (int used, int total) GetContextMetrics() => (0, 0);
        }
    }
}
