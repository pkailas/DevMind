// File: TrainingLogger.cs  v7.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
    /// One file per session, gated behind <see cref="DevMindOptions.TrainingLogEnabled"/>.
    /// </summary>
    public sealed class TrainingLogger
    {
        private readonly string _sessionId;
        private readonly string _filePath;
        private readonly object _writeLock = new object();
        private bool _systemPromptLogged;

        /// <summary>
        /// Creates a new TrainingLogger for the given session.
        /// </summary>
        /// <param name="sessionId">Unique session identifier (refreshed on /restart).</param>
        /// <param name="logFolder">
        /// Folder for JSONL files. Null uses the default (training_logs/ next to the executable).
        /// </param>
        public TrainingLogger(string sessionId, string logFolder = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));

            if (string.IsNullOrEmpty(logFolder))
            {
                string exeDir = Path.GetDirectoryName(typeof(TrainingLogger).Assembly.Location);
                logFolder = Path.Combine(exeDir, "training_logs");
            }

            Directory.CreateDirectory(logFolder);

            string date = DateTime.UtcNow.ToString("yyyyMMdd");
            _filePath = Path.Combine(logFolder, $"training_{_sessionId}_{date}.jsonl");
        }

        /// <summary>
        /// Logs a single turn to the JSONL file. No-op if <see cref="DevMindOptions.TrainingLogEnabled"/> is false.
        /// </summary>
        public void LogTurn(TrainingTurnData data)
        {
            if (!DevMindOptions.Instance.TrainingLogEnabled)
                return;

            try
            {
                var entry = new TrainingEntry
                {
                    SessionId = _sessionId,
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
                    File.AppendAllText(_filePath, json + "\n");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrainingLogger] Write failed: {ex.Message}");
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
                        calls.Add(new ToolCallEntry { Type = "done" });
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
