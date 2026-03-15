// File: DevMindOptionsPage.cs  v5.3
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Community.VisualStudio.Toolkit;
using System.ComponentModel;
using System.Drawing.Design;
using System.Runtime.InteropServices;

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
    /// Provider class that hosts the options page in the Tools > Options dialog.
    /// </summary>
    internal partial class OptionsProvider
    {
        /// <summary>
        /// The dialog page shown under Tools > Options > DevMind > General.
        /// </summary>
        [ComVisible(true)]
        public class DevMindOptionsPage : BaseOptionPage<DevMindOptions> { }
    }

    /// <summary>
    /// Stores DevMind extension settings. Access via <see cref="DevMindOptions.Instance"/>
    /// or <see cref="BaseOptionModel{T}.GetLiveInstanceAsync"/>.
    /// </summary>
    public class DevMindOptions : BaseOptionModel<DevMindOptions>
    {
        /// <summary>
        /// The base URL for the OpenAI-compatible API endpoint (e.g., LM Studio, Ollama).
        /// </summary>
        [Category("Connection")]
        [DisplayName("Endpoint URL")]
        [Description("The base URL for the OpenAI-compatible API endpoint (e.g., LM Studio, Ollama).")]
        [DefaultValue("http://127.0.0.1:1234/v1")]
        public string EndpointUrl { get; set; } = "http://127.0.0.1:1234/v1";

        /// <summary>
        /// API key for authentication. Use 'lm-studio' for LM Studio default.
        /// </summary>
        [Category("Connection")]
        [DisplayName("API Key")]
        [Description("API key for authentication. Use 'lm-studio' for LM Studio default.")]
        [DefaultValue("lm-studio")]
        public string ApiKey { get; set; } = "lm-studio";

        /// <summary>
        /// The LLM server type — determines which endpoint is used for context-size detection.
        /// </summary>
        [Category("Connection")]
        [DisplayName("LLM Server Type")]
        [Description("Server type for context-size detection. llama-server uses /props; LM Studio uses /api/v0/models; Custom uses the endpoint you specify below.")]
        [DefaultValue(LlmServerType.LlamaServer)]
        public LlmServerType ServerType { get; set; } = LlmServerType.LlamaServer;

        /// <summary>
        /// Custom endpoint path used for context-size detection when Server Type is "Custom".
        /// Relative to the server root (e.g., /api/info) or absolute URL.
        /// </summary>
        [Category("Connection")]
        [DisplayName("Custom Context Endpoint")]
        [Description("Endpoint path for context-size detection when Server Type is Custom (e.g., /api/info). Must return JSON with n_ctx at root or in default_generation_settings.")]
        [DefaultValue("")]
        public string CustomContextEndpoint { get; set; } = "";

        /// <summary>
        /// The model name to use. Leave empty to use the server's default model.
        /// </summary>
        [Category("Model")]
        [DisplayName("Model Name")]
        [Description("The model name to use. Leave empty to use the server's default model.")]
        [DefaultValue("")]
        public string ModelName { get; set; } = "";

        /// <summary>
        /// The system prompt sent at the start of each conversation.
        /// </summary>
        [Category("Prompt")]
        [DisplayName("System Prompt")]
        [Description("The system prompt sent at the start of each conversation.")]
        [DefaultValue("You are a helpful coding assistant. Be concise and precise.")]
        [Editor(typeof(System.ComponentModel.Design.MultilineStringEditor), typeof(UITypeEditor))]
        public string SystemPrompt { get; set; } = "You are a helpful coding assistant. Be concise and precise.";

        /// <summary>
        /// Whether to automatically open generated files in the editor after creation.
        /// </summary>
        [Category("File Generation")]
        [DisplayName("Open file after creation")]
        [Description("Automatically open generated files in the editor after creation.")]
        [DefaultValue(true)]
        public bool OpenFileAfterGeneration { get; set; } = true;

        /// <summary>
        /// Maximum number of autonomous agentic loop iterations before stopping.
        /// Set to 0 to disable the agentic loop entirely.
        /// </summary>
        [Category("Agentic Loop")]
        [DisplayName("Max Agentic Depth")]
        [Description("Maximum number of autonomous loop iterations after the initial response (0 = disabled).")]
        [DefaultValue(5)]
        public int AgenticLoopMaxDepth { get; set; } = 5;

        /// <summary>
        /// Whether to display the context budget line after every LLM response.
        /// </summary>
        [Category("Context Management")]
        [DisplayName("Show Context Budget")]
        [Description("Display a color-coded context budget line after every LLM response.")]
        [DefaultValue(true)]
        public bool ShowContextBudget { get; set; } = true;

        /// <summary>
        /// Whether to display LLM thinking tokens (&lt;think&gt;...&lt;/think&gt;) in the output.
        /// When false (default), thinking tokens are suppressed entirely.
        /// When true, they are shown with a [THINKING] prefix in a muted color.
        /// </summary>
        [Category("Display")]
        [DisplayName("Show LLM Thinking")]
        [Description("When enabled, tokens inside <think>...</think> blocks are shown with a [THINKING] prefix. When disabled (default), they are suppressed.")]
        [DefaultValue(false)]
        public bool ShowLlmThinking { get; set; } = false;
    }
}
