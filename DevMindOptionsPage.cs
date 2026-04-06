// File: DevMindOptionsPage.cs  v7.4
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Community.VisualStudio.Toolkit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Design;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

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


    /// <summary>
    /// TypeConverter that populates the Active Profile dropdown from ProfileManager.
    /// </summary>
    public class ActiveProfileConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;

        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => true;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            try
            {
                var pm = new ProfileManager();
                var names = pm.GetAllProfiles().Select(p => p.Name).ToList();
                if (names.Count == 0)
                    names.Add("(none)");
                return new StandardValuesCollection(names);
            }
            catch
            {
                return new StandardValuesCollection(new List<string> { "(none)" });
            }
        }
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
        /// Raised when a profile action (save/delete/rename) is performed from the Options page,
        /// so the toolbar dropdown can refresh.
        /// </summary>
        public static event Action ProfileChanged;

        // ── Profile Management ───────────────────────────────────────────────

        /// <summary>
        /// The currently active profile. Select from the dropdown to switch profiles.
        /// </summary>
        [Category("  Profile")]
        [DisplayName("Active Profile")]
        [Description("Select a profile from the dropdown to switch connection settings immediately.")]
        [TypeConverter(typeof(ActiveProfileConverter))]
        [DefaultValue("")]
        public string ActiveProfileName
        {
            get
            {
                try
                {
                    var pm = new ProfileManager();
                    var active = pm.GetActiveProfile();
                    return active?.Name ?? "(none)";
                }
                catch { return "(unknown)"; }
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value) || value == "(none)")
                    return;
                try
                {
                    var pm = new ProfileManager();
                    var active = pm.GetActiveProfile();
                    if (active != null && string.Equals(active.Name, value, StringComparison.OrdinalIgnoreCase))
                        return;
                    var target = pm.GetAllProfiles()
                        .FirstOrDefault(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase));
                    if (target != null)
                    {
                        pm.SetActiveProfile(target.Id);
                        pm.ApplyProfile(target);
                        ProfileChanged?.Invoke();
                    }
                }
                catch (InvalidOperationException ex)
                {
                    System.Windows.MessageBox.Show(ex.Message, "DevMind — Switch Profile",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        /// <summary>
        /// Enter a profile name and click Apply/OK to save the current settings as a new profile.
        /// </summary>
        [Category("  Profile")]
        [DisplayName("Save Current as New Profile")]
        [Description("Type a profile name here and click Apply or OK to save the current connection settings as a new named profile.")]
        [DefaultValue("")]
        public string SaveAsNewProfile { get; set; } = "";

        /// <summary>
        /// Set to True and click Apply/OK to delete the currently active profile.
        /// Resets to False after the action completes.
        /// </summary>
        [Category("  Profile")]
        [DisplayName("Delete Current Profile")]
        [Description("Set to True and click Apply or OK to delete the currently active profile. You will be prompted for confirmation.")]
        [DefaultValue(false)]
        public bool DeleteCurrentProfile { get; set; } = false;

        /// <summary>
        /// Enter a new name and click Apply/OK to rename the currently active profile.
        /// </summary>
        [Category("  Profile")]
        [DisplayName("Rename Current Profile")]
        [Description("Type a new name here and click Apply or OK to rename the currently active profile.")]
        [DefaultValue("")]
        public string RenameCurrentProfile { get; set; } = "";

        /// <summary>
        /// Set to True and click Apply/OK to overwrite the active profile's stored
        /// values with the current settings. This is how you explicitly save tweaks
        /// back to a profile.
        /// </summary>
        [Category("  Profile")]
        [DisplayName("Update Current Profile")]
        [Description("Set to True and click Apply or OK to overwrite the active profile with the current connection settings. Resets to False after the action completes.")]
        [DefaultValue(false)]
        public bool UpdateCurrentProfile { get; set; } = false;

        public override void Save()
        {
            bool changed = false;

            // ── Save as new profile ──
            if (!string.IsNullOrWhiteSpace(SaveAsNewProfile))
            {
                try
                {
                    var pm = new ProfileManager();
                    pm.SaveCurrentAsProfile(SaveAsNewProfile);
                    changed = true;
                }
                catch (InvalidOperationException ex)
                {
                    System.Windows.MessageBox.Show(ex.Message, "DevMind — Save Profile",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                SaveAsNewProfile = "";
            }

            // ── Delete current profile ──
            if (DeleteCurrentProfile)
            {
                try
                {
                    var pm = new ProfileManager();
                    var active = pm.GetActiveProfile();
                    if (active != null)
                    {
                        var result = System.Windows.MessageBox.Show(
                            $"Delete profile \"{active.Name}\"?\nThis cannot be undone.",
                            "DevMind — Delete Profile",
                            MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (result == MessageBoxResult.Yes)
                        {
                            pm.DeleteProfile(active.Id);
                            // Switch to the first remaining profile
                            var next = pm.GetActiveProfile();
                            if (next != null)
                                pm.ApplyProfile(next);
                            changed = true;
                        }
                    }
                }
                catch (InvalidOperationException ex)
                {
                    System.Windows.MessageBox.Show(ex.Message, "DevMind — Delete Profile",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                DeleteCurrentProfile = false;
            }

            // ── Rename current profile ──
            if (!string.IsNullOrWhiteSpace(RenameCurrentProfile))
            {
                try
                {
                    var pm = new ProfileManager();
                    var active = pm.GetActiveProfile();
                    if (active != null)
                    {
                        pm.RenameProfile(active.Id, RenameCurrentProfile);
                        changed = true;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    System.Windows.MessageBox.Show(ex.Message, "DevMind — Rename Profile",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                RenameCurrentProfile = "";
            }

            // ── Update current profile ──
            if (UpdateCurrentProfile)
            {
                try
                {
                    var pm = new ProfileManager();
                    var active = pm.GetActiveProfile();
                    if (active != null)
                    {
                        pm.UpdateActiveProfileFromSettings();
                        changed = true;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    System.Windows.MessageBox.Show(ex.Message, "DevMind — Update Profile",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                UpdateCurrentProfile = false;
            }

            base.Save();

            if (changed)
                ProfileChanged?.Invoke();
        }

        // ── Connection ───────────────────────────────────────────────────────

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
        /// <summary>
        /// Maximum time in minutes to wait for the first token from the LLM.
        /// This covers the prompt-ingestion phase before the model begins generating.
        /// </summary>
        [Category("Connection")]
        [DisplayName("First Token Timeout (minutes)")]
        [Description("Maximum time to wait for the first response token. Increase for large prompts on slower hardware where prompt ingestion takes a long time.")]
        [DefaultValue(5)]
        public int FirstTokenTimeoutMinutes { get; set; } = 5;

        /// <summary>
        /// Maximum time in minutes to wait for a complete LLM response.
        /// </summary>
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

        /// <summary>
        /// Override auto-detected context size. Set to 0 to use auto-detection.
        /// Useful for cloud APIs (e.g., OpenRouter) that don't expose context size endpoints.
        /// </summary>
        [Category("Connection")]
        [DisplayName("Manual Context Size")]
        [Description("Override auto-detected context size. Set to 0 to use auto-detection. Useful for cloud APIs (e.g., OpenRouter) that don't expose context size endpoints.")]
        [DefaultValue(0)]
        public int ManualContextSize { get; set; } = 0;

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
        /// How DevMind communicates directives to the LLM.
        /// ToolUse sends JSON Schema tools (requires --jinja on llama-server).
        /// TextDirective uses the legacy text format.
        /// Auto tries ToolUse first, falls back to TextDirective on error.
        /// </summary>
        [Category("Directives")]
        [DisplayName("Directive Mode")]
        [Description("How DevMind communicates directives to the LLM. ToolUse sends JSON Schema tools (requires --jinja on llama-server). TextDirective uses the legacy text format. Auto tries ToolUse first, falls back to TextDirective on error.")]
        [DefaultValue(DirectiveMode.Auto)]
        public DirectiveMode DirectiveMode { get; set; } = DirectiveMode.Auto;

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

        /// <summary>
        /// Block-by-block mode setting for large file operations.
        /// Off: Always use full context mode.
        /// On: Always use block-by-block mode.
        /// Auto: Automatically choose based on file size and model constraints.
        /// </summary>
        [Category("Agentic Loop")]
        [DisplayName("Block-by-Block Mode")]
        [Description("Off: Always use full context mode. On: Always use block-by-block mode. Auto: Automatically choose based on file size and model constraints.")]
        [DefaultValue(DevMind.BlockByBlockModeType.Auto)]
        public BlockByBlockModeType BlockByBlockMode { get; set; } = BlockByBlockModeType.Auto;

        /// <summary>
        /// When enabled, all PATCH operations pause for user confirmation via an inline
        /// diff preview card — even exact matches. When disabled (default), only
        /// fuzzy-matched patches require confirmation.
        /// </summary>
        [Category("Agentic Loop")]
        [DisplayName("Always Confirm PATCH")]
        [Description("When enabled, all PATCH operations pause for confirmation — even exact matches. When disabled, only fuzzy-matched patches require confirmation.")]
        [DefaultValue(false)]
        public bool AlwaysConfirmPatch { get; set; } = false;

        /// <summary>
        /// Controls how aggressively old context is compressed via tiered eviction.
        /// Off = no eviction. Balanced = moderate compression of old turns.
        /// Aggressive = tight compression for long tasks.
        /// </summary>
        [Category("Context Management")]
        [DisplayName("Context Eviction")]
        [Description("Controls how aggressively old context is compressed. Off = no eviction. Balanced = moderate compression of old turns. Aggressive = tight compression for long tasks.")]
        [DefaultValue(ContextEvictionMode.Balanced)]
        public ContextEvictionMode ContextEviction { get; set; } = ContextEvictionMode.Balanced;

        /// <summary>
        /// When enabled, shows detailed diagnostic logging in the output panel
        /// including eviction details, turn tracking, and pinned message status.
        /// </summary>
        [Category("Context Management")]
        [DisplayName("Show Debug Output")]
        [Description("When enabled, shows detailed diagnostic logging in the output panel including eviction details, turn tracking, and pinned message status.")]
        [DefaultValue(false)]
        public bool ShowDebugOutput { get; set; } = false;

        /// <summary>
        /// Working budget percentage at which MicroCompact fires. Set to 0 to disable.
        /// Higher values maximize prompt cache hits but risk running out of context.
        /// </summary>
        [Category("Context Management")]
        [DisplayName("MicroCompact Enabled")]
        [Description("Enable predictive context compaction. When enabled, MicroCompact uses observed context growth rate to determine when to trim. Disable to turn off context compaction entirely.")]
        [DefaultValue(true)]
        public int MicroCompactThreshold { get; set; } = 85;

        /// <summary>
        /// When enabled, MicroCompact generates a semantic summary of trimmed messages
        /// using the same LLM endpoint (non-streaming, ~6s). The summary replaces dumb
        /// breadcrumbs with rich context. When disabled, uses breadcrumbs only.
        /// </summary>
        [Category("Context Management")]
        [DisplayName("MicroCompact Summarize")]
        [Description("Generate a semantic summary of trimmed messages during context compaction. Uses the same LLM server (non-streaming). Disable to use breadcrumbs only.")]
        [DefaultValue(true)]
        public bool MicroCompactSummarize { get; set; } = true;

        /// <summary>
        /// When enabled, escalates to brainwash (full context replacement) when compaction
        /// thrashing is detected (2+ compactions in recent turns with n_past still above 60%).
        /// </summary>
        [Category("Context Management")]
        [DisplayName("MicroCompact Brainwash")]
        [Description("Enable context brainwash escalation. When compaction thrashing is detected, replaces the entire conversation history with a synthetic minimal conversation preserving task context. Drops n_past from 80-100K to ~5K.")]
        [DefaultValue(false)]
        public bool MicroCompactBrainwash { get; set; } = false;

        // ── Training Data ───────────────────────────────────────────────────

        /// <summary>
        /// When enabled, captures fine-tuning training data as JSONL after each agentic turn.
        /// One file per session, stored in the Training Log Folder.
        /// </summary>
        [Category("Training Data")]
        [DisplayName("Training Log Enabled")]
        [Description("Capture fine-tuning training data as JSONL after each agentic turn. One file per session. No overhead when disabled.")]
        [DefaultValue(false)]
        public bool TrainingLogEnabled { get; set; } = false;

        /// <summary>
        /// Folder for training JSONL files. Leave empty to use the default (training_logs/ next to the extension).
        /// </summary>
        [Category("Training Data")]
        [DisplayName("Training Log Folder")]
        [Description("Folder for training JSONL files. Leave empty to use the default (training_logs/ next to the extension).")]
        [DefaultValue("")]
        public string TrainingLogFolder { get; set; } = "";

    }
}
