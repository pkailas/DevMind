// File: LoopDriverTestEnv.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shared harness for driving a real LoopDriver through one iteration at a time:
// a scripted ILlmClient, a recording IAgenticHost, and no-op callbacks over a temp
// working directory.
//
// It started nested inside LoopDriverRunExecTests. It moved out when a second and
// third test class needed the same setup to reach LoopDriver’s terminal messages —
// three copies of a 170-line IAgenticHost stub would rot out of sync with the
// interface the first time a member was added to it.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    /// <summary>Entry points a test class reaches through
    /// <c>using static DevMind.Core.Tests.LoopDriverTest;</c>.</summary>
    internal static class LoopDriverTest
    {
        internal static ToolCallResult Tool(string name, string argumentsJson)
        {
            var tc = new ToolCallResult { Name = name, Id = "call_1" };
            if (argumentsJson != null)
            {
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
                Assert.NotNull(dict);
                foreach (var kv in dict)
                    tc.Arguments[kv.Key] = kv.Value;
            }
            return tc;
        }
    }

    /// <summary>
    /// One LoopDriver + fake llm/host/callbacks over a temp working directory.
    /// <see cref="RunToolTurn"/> feeds the next iteration's tool calls into
    /// FakeLlmClient and drives one ProcessIterationAsync.
    /// </summary>
    internal sealed class Env : IDisposable
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
            _llm.LastToolCalls = null!;
            State.ShellLoopPending = true;
            return _driver.ProcessIterationAsync(
                "task", prose, buildCommand: "dotnet build", CancellationToken.None);
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Scripted-shell IAgenticHost: per-command (exit, output), default (0, "").</summary>
    internal sealed class FakeHost : IAgenticHost
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
        public Task<(PatchResolveResult? result, string? failureReason)> ResolvePatchAsync(string patchContent, bool fromToolCall = false) =>
            throw new NotImplementedException("tests avoid patch blocks");
        public Task<(string? fullPath, string? failureReason)> ApplyResolvedPatchAsync(PatchResolveResult resolved) =>
            throw new NotImplementedException("tests avoid patch blocks");
        public Task<List<int>> ShowDiffPreviewAsync(List<PatchResolveResult> resolvedPatches, CancellationToken cancellationToken) =>
            Task.FromResult(new List<int>());
        public Task<string> RecallMemoryAsync(string topic) => Task.FromResult("");
        public Task<string> SaveMemoryAsync(string topic, string content, string description) => Task.FromResult("ok");
        public Task<string> ListMemoryTopicsAsync() => Task.FromResult("");
        public Task<string> SearchMemoryAsync(string pattern) => Task.FromResult("");
        public Task<string> QueryLibraryAsync(string question, int topK, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<string> QueryLibraryAsync(string question, int topK, string? docFilter, CancellationToken cancellationToken = default) => Task.FromResult("");
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
    internal sealed class FakeLlmClient : ILlmClient
    {
        public List<ToolCallResult> LastToolCalls { get; set; } = null!;
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
        public string? LastCompactionSummary => null;
        public string LastAssistantText => "";
        public string LastReasoning => "";
        public int EstimateHistoryTokens() => 0;
        public void AddToolResultMessage(string toolCallId, string content, string? toolName = null) { }
        public void StagePendingImage(string imageDataUri) { }
        public Task SendMessageAsync(string userMessage, Action<string> onToken, Action onComplete,
            Action<Exception> onError, bool deferCompression = false, string? combinedSystemPrompt = null,
            CancellationToken cancellationToken = default, bool forceToolChoiceRequired = false,
            string? imageBase64 = null, int maxTokens = 0, string? taskScratchpad = null)
            => throw new NotImplementedException("tests drive ProcessIterationAsync directly");
        public void ClearHistory(bool preserveScratchpad = false) { }
        public void PrependMessages(string[] roles, string[] contents) { }
    }

    internal sealed class NoopCallbacks : ILoopCallbacks
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
