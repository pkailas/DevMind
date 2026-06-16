// File: JsonlTrainingLogger.cs  v8.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Append-only JSONL training-turn logger. Ported into DevMind.Core from the retired
// VS extension's TrainingLogger.cs (v7.1) so capture is host-agnostic and survives
// future UI/rebuild changes. The JSONL schema is byte-compatible with v7.1 — same
// field names, same TurnOutcome enum, same filename pattern — so old and new logs
// concatenate cleanly.
//
// Decoupled from the old DevMindOptions.Instance singleton: enablement and the log
// folder are constructor inputs, and the session id is resolved at write time via a
// provider (so a new session — e.g. TUI /new — rolls to a fresh file automatically).

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;

namespace DevMind
{
    /// <summary>
    /// Outcome classification for a single training turn.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum TurnOutcome
    {
        Read,
        Write,
        Shell,
        Done,
        Error,
        NoToolCalls
    }

    /// <summary>
    /// Captures fine-tuning training data as append-only JSONL.
    /// One file per session, gated behind <see cref="Enabled"/>.
    /// </summary>
    public sealed class JsonlTrainingLogger : ITrainingLogger
    {
        private readonly Func<string> _sessionIdProvider;
        private readonly string _defaultFolder;
        private readonly object _writeLock = new object();

        private string _lastSessionId;
        private bool _systemPromptLogged;

        /// <summary>
        /// Creates a training logger.
        /// </summary>
        /// <param name="sessionIdProvider">
        /// Resolves the current session id at write time (so the filename rolls when the
        /// session changes). Must not be null.
        /// </param>
        /// <param name="enabled">When false, <see cref="LogTurn"/> is a no-op.</param>
        /// <param name="logFolder">
        /// Folder for JSONL files. Blank uses the default: <c>training_logs/</c> next to the executable.
        /// </param>
        public JsonlTrainingLogger(Func<string> sessionIdProvider, bool enabled, string logFolder = null)
        {
            _sessionIdProvider = sessionIdProvider ?? throw new ArgumentNullException(nameof(sessionIdProvider));
            Enabled = enabled;
            Folder = logFolder;

            // AppContext.BaseDirectory (not Assembly.Location, which is EMPTY in a single-file
            // publish — Path.GetDirectoryName("") is null and Path.Combine then throws).
            string exeDir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(exeDir))
                exeDir = Directory.GetCurrentDirectory();
            _defaultFolder = Path.Combine(exeDir, "training_logs");
        }

        /// <inheritdoc />
        /// <remarks>Settable so a host (e.g. the TUI /training-log command) can toggle capture
        /// live; persisting the new value to config is the host's responsibility.</remarks>
        public bool Enabled { get; set; }

        /// <summary>Configured log folder (may be blank → falls back to the default). Settable
        /// so a host can retarget capture live.</summary>
        public string Folder { get; set; }

        /// <summary>The folder writes actually land in — the configured <see cref="Folder"/>,
        /// or <c>training_logs/</c> beside the executable when it is blank.</summary>
        public string ResolvedFolder => string.IsNullOrWhiteSpace(Folder) ? _defaultFolder : Folder;

        /// <summary>
        /// Newest <c>training_*.jsonl</c> last-write time (UTC) in <see cref="ResolvedFolder"/>,
        /// or null when the folder is absent or empty. A status probe for "is it actually writing?".
        /// </summary>
        public DateTime? GetLastWriteUtc()
        {
            try
            {
                string folder = ResolvedFolder;
                if (!Directory.Exists(folder)) return null;

                DateTime? latest = null;
                foreach (string f in Directory.EnumerateFiles(folder, "training_*.jsonl"))
                {
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (latest == null || t > latest) latest = t;
                }
                return latest;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Logs a single turn to the JSONL file. No-op if disabled.
        /// </summary>
        public void LogTurn(TrainingTurnData data)
        {
            if (!Enabled || data == null)
                return;

            try
            {
                string sessionId = _sessionIdProvider();
                if (string.IsNullOrEmpty(sessionId))
                    return; // cannot name the file without a session id

                // A new session (e.g. TUI /new) re-logs the system prompt once for it.
                if (!string.Equals(sessionId, _lastSessionId, StringComparison.Ordinal))
                {
                    _lastSessionId = sessionId;
                    _systemPromptLogged = false;
                }

                // Resolve folder at write time so config changes take effect without restart.
                string logFolder = ResolvedFolder;
                Directory.CreateDirectory(logFolder);

                string date = DateTime.UtcNow.ToString("yyyyMMdd");
                string filePath = Path.Combine(logFolder, $"training_{sessionId}_{date}.jsonl");

                var entry = new TrainingEntry
                {
                    SessionId = sessionId,
                    TurnNumber = data.TurnNumber,
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    SystemPrompt = _systemPromptLogged ? null : data.SystemPrompt,
                    UserMessage = data.UserMessage,
                    AssistantResponse = data.AssistantResponse,
                    ToolCalls = data.ToolCalls,
                    ToolResults = data.ToolResults,
                    SummaryContext = data.SummaryContext,
                    Metrics = data.Metrics,
                    Outcome = data.Outcome,
                    QualityFlag = null
                };

                if (!_systemPromptLogged && data.SystemPrompt != null)
                    _systemPromptLogged = true;

                string json = JsonConvert.SerializeObject(entry, Formatting.None, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Include,
                    DefaultValueHandling = DefaultValueHandling.Include
                });

                lock (_writeLock)
                {
                    File.AppendAllText(filePath, json + "\n");
                }
            }
            catch (Exception ex)
            {
                // Logging must never break the agentic loop.
                System.Diagnostics.Debug.WriteLine($"[JsonlTrainingLogger] Write failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Classifies a turn outcome from execution results and response outcome flags.
        /// </summary>
        public static TurnOutcome ClassifyOutcome(
            bool isDone,
            bool hasErrors,
            bool hasReadRequests,
            bool hasPatches,
            bool hasFileCreation,
            bool hasShellCommands,
            bool hasToolCalls)
        {
            if (isDone) return TurnOutcome.Done;
            if (hasErrors) return TurnOutcome.Error;
            if (hasShellCommands) return TurnOutcome.Shell;
            if (hasPatches || hasFileCreation) return TurnOutcome.Write;
            if (hasReadRequests) return TurnOutcome.Read;
            if (!hasToolCalls) return TurnOutcome.NoToolCalls;
            return TurnOutcome.Read;
        }

        /// <summary>
        /// Builds tool_calls array from parsed response blocks.
        /// </summary>
        public static List<ToolCallEntry> ExtractToolCalls(List<ResponseBlock> blocks)
        {
            if (blocks == null) return null;

            var calls = new List<ToolCallEntry>();
            foreach (var block in blocks)
            {
                switch (block.Type)
                {
                    case BlockType.File:
                        calls.Add(new ToolCallEntry { Type = "file", Filename = block.FileName });
                        break;
                    case BlockType.Patch:
                        calls.Add(new ToolCallEntry { Type = "patch", Filename = block.FileName });
                        break;
                    case BlockType.Shell:
                        calls.Add(new ToolCallEntry { Type = "shell", Command = block.Command });
                        break;
                    case BlockType.ReadRequest:
                        calls.Add(new ToolCallEntry { Type = "read", Filename = block.FileName });
                        break;
                    case BlockType.Grep:
                        calls.Add(new ToolCallEntry { Type = "grep", Filename = block.FileName });
                        break;
                    case BlockType.Find:
                        calls.Add(new ToolCallEntry { Type = "find", Filename = block.GlobPattern });
                        break;
                    case BlockType.Delete:
                        calls.Add(new ToolCallEntry { Type = "delete", Filename = block.FileName });
                        break;
                    case BlockType.Rename:
                        calls.Add(new ToolCallEntry { Type = "rename", Filename = block.RenameFrom });
                        break;
                    case BlockType.Test:
                        calls.Add(new ToolCallEntry { Type = "test", Filename = block.TestProject });
                        break;
                    case BlockType.Diff:
                        calls.Add(new ToolCallEntry { Type = "diff", Filename = block.FileName });
                        break;
                    case BlockType.Done:
                        calls.Add(new ToolCallEntry { Type = "done", Summary = block.Content });
                        break;
                }
            }
            return calls.Count > 0 ? calls : null;
        }

        /// <summary>
        /// Builds tool_results array from an ExecutionResult.
        /// </summary>
        public static List<ToolResultEntry> ExtractToolResults(ExecutionResult result)
        {
            if (result == null) return null;

            var results = new List<ToolResultEntry>();

            foreach (var path in result.FilesCreated)
                results.Add(new ToolResultEntry { Type = "file", Filename = Path.GetFileName(path), Success = true });

            foreach (var path in result.PatchedPaths)
                results.Add(new ToolResultEntry { Type = "patch", Filename = Path.GetFileName(path), Success = true });

            if (result.PatchesFailed > 0)
            {
                for (int i = 0; i < result.PatchesFailed; i++)
                    results.Add(new ToolResultEntry { Type = "patch", Success = false });
            }

            foreach (var path in result.FilesDeleted)
                results.Add(new ToolResultEntry { Type = "delete", Filename = Path.GetFileName(path), Success = true });

            foreach (var entry in result.FilesRenamed)
                results.Add(new ToolResultEntry { Type = "rename", Filename = entry, Success = true });

            foreach (var path in result.FilesAppended)
                results.Add(new ToolResultEntry { Type = "append", Filename = Path.GetFileName(path), Success = true });

            if (result.ShellExitCode.HasValue)
            {
                results.Add(new ToolResultEntry
                {
                    Type = "shell",
                    Success = result.ShellExitCode == 0,
                    LineCount = result.ShellOutput?.Split('\n').Length ?? 0
                });
            }

            foreach (var kv in result.ToolResultContents)
            {
                string content = kv.Value;
                if (content != null && content.Length > 2000)
                    content = content.Substring(0, 1500) + $"[...truncated, total {kv.Value.Length} chars]" + kv.Value.Substring(kv.Value.Length - 500);
                results.Add(new ToolResultEntry { Type = "read", Filename = kv.Key, Success = true, Content = content });
            }

            foreach (var err in result.Errors)
                results.Add(new ToolResultEntry { Type = "error", Success = false });

            return results.Count > 0 ? results : null;
        }

        #region JSON DTOs

        [JsonObject(MemberSerialization.OptIn)]
        internal sealed class TrainingEntry
        {
            [JsonProperty("session_id")]
            public string SessionId { get; set; }

            [JsonProperty("turn_number")]
            public int TurnNumber { get; set; }

            [JsonProperty("timestamp")]
            public string Timestamp { get; set; }

            [JsonProperty("system_prompt")]
            public string SystemPrompt { get; set; }

            [JsonProperty("user_message")]
            public string UserMessage { get; set; }

            [JsonProperty("assistant_response")]
            public string AssistantResponse { get; set; }

            [JsonProperty("tool_calls")]
            public List<ToolCallEntry> ToolCalls { get; set; }

            [JsonProperty("tool_results")]
            public List<ToolResultEntry> ToolResults { get; set; }

            [JsonProperty("summary_context")]
            public string SummaryContext { get; set; }

            [JsonProperty("metrics")]
            public MetricsEntry Metrics { get; set; }

            [JsonProperty("outcome")]
            public TurnOutcome Outcome { get; set; }

            [JsonProperty("quality_flag")]
            public string QualityFlag { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Input data for a single training log turn.
    /// </summary>
    public sealed class TrainingTurnData
    {
        public int TurnNumber { get; set; }
        public string SystemPrompt { get; set; }
        public string UserMessage { get; set; }
        public string AssistantResponse { get; set; }
        public List<ToolCallEntry> ToolCalls { get; set; }
        public List<ToolResultEntry> ToolResults { get; set; }
        public string SummaryContext { get; set; }
        public MetricsEntry Metrics { get; set; }
        public TurnOutcome Outcome { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ToolCallEntry
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("filename", NullValueHandling = NullValueHandling.Ignore)]
        public string Filename { get; set; }

        [JsonProperty("command", NullValueHandling = NullValueHandling.Ignore)]
        public string Command { get; set; }

        [JsonProperty("summary", NullValueHandling = NullValueHandling.Ignore)]
        public string Summary { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ToolResultEntry
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("filename", NullValueHandling = NullValueHandling.Ignore)]
        public string Filename { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("line_count", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int LineCount { get; set; }

        [JsonProperty("content", NullValueHandling = NullValueHandling.Ignore)]
        public string Content { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class MetricsEntry
    {
        [JsonProperty("n_past")]
        public int NPast { get; set; }

        [JsonProperty("n_ctx")]
        public int NCtx { get; set; }

        [JsonProperty("predicted_tokens")]
        public int PredictedTokens { get; set; }

        [JsonProperty("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonProperty("tok_per_sec")]
        public double TokPerSec { get; set; }

        [JsonProperty("iteration")]
        public int Iteration { get; set; }

        [JsonProperty("context_percent")]
        public int ContextPercent { get; set; }
    }
}
