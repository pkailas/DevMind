// File: DevMindOptions.Data.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.ComponentModel;
#if !PROBE
using System.Drawing.Design;
#endif

namespace DevMind
{
    /// <summary>
    /// Identifies the type of LLM server for context-size detection.
    /// </summary>
    public enum LlmServerType
    {
        [Description("llama-server")]
        LlamaServer,
        [Description("LM Studio")]
        LmStudio,
        [Description("Custom")]
        Custom
    }

    /// <summary>
    /// Controls how aggressively old context is compressed via tiered eviction.
    /// </summary>
    public enum ContextEvictionMode
    {
        [Description("Off")]
        Off,
        [Description("Balanced")]
        Balanced,
        [Description("Aggressive")]
        Aggressive
    }

    /// <summary>
    /// Controls how DevMind communicates directives to the LLM.
    /// </summary>
    public enum DirectiveMode
    {
        [Description("Tool Use")]
        ToolUse,
        [Description("Text Directives")]
        TextDirective,
        [Description("Auto")]
        Auto
    }

    /// <summary>
    /// Block-by-block mode setting for large file operations.
    /// </summary>
    public enum BlockByBlockModeType
    {
        [Description("Off")]
        Off,
        [Description("On")]
        On,
        [Description("Auto")]
        Auto
    }

    public partial class DevMindOptions
    {
#if PROBE
        private static DevMindOptions _probeInstance = new DevMindOptions();

        /// <summary>Static Instance for probe/test use — populated by UseInMemoryDefaults().</summary>
        public static DevMindOptions Instance => _probeInstance;
#endif

        /// <summary>
        /// Configures a self-contained in-memory DevMindOptions for probe / unit-test use.
        /// Must be called before constructing LlmClient. No-op in VS mode.
        /// </summary>
        public static void UseInMemoryDefaults()
        {
#if PROBE
            _probeInstance = new DevMindOptions
            {
                EndpointUrl              = "http://localhost:8080/v1",
                ApiKey                   = "",
                ModelName                = "",
                SystemPrompt             = "You are a code analysis assistant. Use the provided tools to read files and answer the user's question. When done, call task_done with your final answer.",
                DirectiveMode            = DirectiveMode.ToolUse,
                ServerType               = LlmServerType.LlamaServer,
                AgenticLoopMaxDepth      = 10,
                ContextEviction          = ContextEvictionMode.Off,
                MicroCompactThreshold    = 0,
                MicroCompactSummarize    = false,
                MicroCompactBrainwash    = false,
                ShowDebugOutput          = false,
                ShowLlmThinking          = false,
                ShowContextBudget        = false,
                TrainingLogEnabled       = false,
                TrainingLogFolder        = "",
                FirstTokenTimeoutMinutes = 5,
                RequestTimeoutMinutes    = 10,
                ManualContextSize        = 0,
                CustomContextEndpoint    = "",
            };
#endif
        }

        // ── Connection ───────────────────────────────────────────────────────

        [Category("Connection")]
        [DisplayName("Endpoint URL")]
        [Description("The base URL for the OpenAI-compatible API endpoint (e.g., LM Studio, Ollama).")]
        [DefaultValue("http://127.0.0.1:1234/v1")]
        public string EndpointUrl { get; set; } = "http://127.0.0.1:1234/v1";

        [Category("Connection")]
        [DisplayName("API Key")]
        [Description("API key for authentication. Use 'lm-studio' for LM Studio default.")]
        [DefaultValue("lm-studio")]
        public string ApiKey { get; set; } = "lm-studio";

        [Category("Connection")]
        [DisplayName("LLM Server Type")]
        [Description("Server type for context-size detection. llama-server uses /props; LM Studio uses /api/v0/models; Custom uses the endpoint you specify below.")]
        [DefaultValue(LlmServerType.LlamaServer)]
        public LlmServerType ServerType { get; set; } = LlmServerType.LlamaServer;

        [Category("Connection")]
        [DisplayName("First Token Timeout (minutes)")]
        [Description("Maximum time to wait for the first response token. Increase for large prompts on slower hardware where prompt ingestion takes a long time.")]
        [DefaultValue(5)]
        public int FirstTokenTimeoutMinutes { get; set; } = 5;

        [Category("Connection")]
        [DisplayName("Request Timeout (minutes)")]
        [Description("Maximum time to wait for a complete LLM response including prompt processing and generation. Increase for large prompts on slower hardware.")]
        [DefaultValue(10)]
        public int RequestTimeoutMinutes { get; set; } = 10;

        [Category("Connection")]
        [DisplayName("Custom Context Endpoint")]
        [Description("Endpoint path for context-size detection when Server Type is Custom (e.g., /api/info). Must return JSON with n_ctx at root or in default_generation_settings.")]
        [DefaultValue("")]
        public string CustomContextEndpoint { get; set; } = "";

        [Category("Connection")]
        [DisplayName("Manual Context Size")]
        [Description("Override auto-detected context size. Set to 0 to use auto-detection. Useful for cloud APIs (e.g., OpenRouter) that don't expose context size endpoints.")]
        [DefaultValue(0)]
        public int ManualContextSize { get; set; } = 0;

        // ── Model ────────────────────────────────────────────────────────────

        [Category("Model")]
        [DisplayName("Model Name")]
        [Description("The model name to use. Leave empty to use the server's default model.")]
        [DefaultValue("")]
        public string ModelName { get; set; } = "";

        // ── Prompt ───────────────────────────────────────────────────────────

        [Category("Prompt")]
        [DisplayName("System Prompt")]
        [Description("The system prompt sent at the start of each conversation.")]
        [DefaultValue("You are a helpful coding assistant. Be concise and precise.")]
#if !PROBE
        [Editor(typeof(System.ComponentModel.Design.MultilineStringEditor), typeof(UITypeEditor))]
#endif
        public string SystemPrompt { get; set; } = "You are a helpful coding assistant. Be concise and precise.";

        // ── File Generation ──────────────────────────────────────────────────

        [Category("File Generation")]
        [DisplayName("Open file after creation")]
        [Description("Automatically open generated files in the editor after creation.")]
        [DefaultValue(true)]
        public bool OpenFileAfterGeneration { get; set; } = true;

        // ── Agentic Loop ─────────────────────────────────────────────────────

        [Category("Agentic Loop")]
        [DisplayName("Max Agentic Depth")]
        [Description("Maximum number of autonomous loop iterations after the initial response (0 = disabled).")]
        [DefaultValue(5)]
        public int AgenticLoopMaxDepth { get; set; } = 5;

        [Category("Agentic Loop")]
        [DisplayName("Block-by-Block Mode")]
        [Description("Off: Always use full context mode. On: Always use block-by-block mode. Auto: Automatically choose based on file size and model constraints.")]
        [DefaultValue(DevMind.BlockByBlockModeType.Auto)]
        public BlockByBlockModeType BlockByBlockMode { get; set; } = BlockByBlockModeType.Auto;

        [Category("Agentic Loop")]
        [DisplayName("Always Confirm PATCH")]
        [Description("When enabled, all PATCH operations pause for confirmation — even exact matches. When disabled, only fuzzy-matched patches require confirmation.")]
        [DefaultValue(false)]
        public bool AlwaysConfirmPatch { get; set; } = false;

        // ── Directives ───────────────────────────────────────────────────────

        [Category("Directives")]
        [DisplayName("Directive Mode")]
        [Description("How DevMind communicates directives to the LLM. ToolUse sends JSON Schema tools (requires --jinja on llama-server). TextDirective uses the legacy text format. Auto tries ToolUse first, falls back to TextDirective on error.")]
        [DefaultValue(DirectiveMode.Auto)]
        public DirectiveMode DirectiveMode { get; set; } = DirectiveMode.Auto;

        // ── Display ──────────────────────────────────────────────────────────

        [Category("Display")]
        [DisplayName("Show LLM Thinking")]
        [Description("When enabled, tokens inside <think>...</think> blocks are shown with a [THINKING] prefix. When disabled (default), they are suppressed.")]
        [DefaultValue(false)]
        public bool ShowLlmThinking { get; set; } = false;

        // ── Context Management ───────────────────────────────────────────────

        [Category("Context Management")]
        [DisplayName("Show Context Budget")]
        [Description("Display a color-coded context budget line after every LLM response.")]
        [DefaultValue(true)]
        public bool ShowContextBudget { get; set; } = true;

        [Category("Context Management")]
        [DisplayName("Context Eviction")]
        [Description("Controls how aggressively old context is compressed. Off = no eviction. Balanced = moderate compression of old turns. Aggressive = tight compression for long tasks.")]
        [DefaultValue(ContextEvictionMode.Balanced)]
        public ContextEvictionMode ContextEviction { get; set; } = ContextEvictionMode.Balanced;

        [Category("Context Management")]
        [DisplayName("Show Debug Output")]
        [Description("When enabled, shows detailed diagnostic logging in the output panel including eviction details, turn tracking, and pinned message status.")]
        [DefaultValue(false)]
        public bool ShowDebugOutput { get; set; } = false;

        [Category("Context Management")]
        [DisplayName("MicroCompact Enabled")]
        [Description("Enable predictive context compaction. When enabled, MicroCompact uses observed context growth rate to determine when to trim. Disable to turn off context compaction entirely.")]
        [DefaultValue(true)]
        public int MicroCompactThreshold { get; set; } = 85;

        [Category("Context Management")]
        [DisplayName("MicroCompact Summarize")]
        [Description("Generate a semantic summary of trimmed messages during context compaction. Uses the same LLM server (non-streaming). Disable to use breadcrumbs only.")]
        [DefaultValue(true)]
        public bool MicroCompactSummarize { get; set; } = true;

        [Category("Context Management")]
        [DisplayName("MicroCompact Brainwash")]
        [Description("Enable context brainwash escalation. When compaction thrashing is detected, replaces the entire conversation history with a synthetic minimal conversation preserving task context. Drops n_past from 80-100K to ~5K.")]
        [DefaultValue(false)]
        public bool MicroCompactBrainwash { get; set; } = false;

        // ── Training Data ────────────────────────────────────────────────────

        [Category("Training Data")]
        [DisplayName("Training Log Enabled")]
        [Description("Capture fine-tuning training data as JSONL after each agentic turn. One file per session. No overhead when disabled.")]
        [DefaultValue(false)]
        public bool TrainingLogEnabled { get; set; } = false;

        [Category("Training Data")]
        [DisplayName("Training Log Folder")]
        [Description("Folder for training JSONL files. Leave empty to use the default (training_logs/ next to the extension).")]
        [DefaultValue("")]
        public string TrainingLogFolder { get; set; } = "";
    }
}
