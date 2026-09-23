// File: TuiOptions.cs  v1.4
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Minimal ILlmOptions implementation for the TUI.
// Mirrors CliOptions but without the CLI-specific arg parsing.

using System;
using System.IO;

namespace DevMind
{
    /// <summary>
    /// TUI implementation of ILlmOptions. Property values resolved from
    //  environment variables → command-line args → hardcoded defaults.
    /// </summary>
    public sealed class TuiOptions : ILlmOptions
    {
        // CLI-only properties (not part of ILlmOptions).
        public string EndpointUrl { get; set; } = "http://127.0.0.1:1234/v1";
        public string ApiKey { get; set; } = "lm-studio";
        public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();

        /// <summary>
        /// Explicit build command for run_build. Empty = auto-detect via
        /// <see cref="BuildCommandResolver"/> (DEVMIND_BUILD_COMMAND env var,
        /// then .vsixmanifest/package.json/.sln/.slnx/.csproj detection).
        /// </summary>
        public string BuildCommand { get; set; } = "";

        // ILlmOptions.
        public string SystemPrompt             { get; set; } = DefaultPrompts.System;

        /// <summary>
        /// The prompt supplied by --system-prompt on this invocation, or null when the
        /// operator did not type one. Distinct from <see cref="SystemPrompt"/>, which is
        /// ALWAYS populated (built-in default, or devmind.json) and so cannot tell an
        /// explicit choice from a default. Only the argument parser sets this; config
        /// files never do.
        /// </summary>
        public string ExplicitSystemPrompt { get; set; }

        public string ModelName                { get; set; } = "";
        public int    RequestTimeoutMinutes    { get; set; } = 10;
        public int    FirstTokenTimeoutMinutes { get; set; } = 5;
        public bool   ShowDebugOutput          { get; set; } = false;
        public bool   ShowContextBudget        { get; set; } = true;
        public bool   ShowLlmThinking          { get; set; } = false;
        public ContextEvictionMode ContextEviction { get; set; } = ContextEvictionMode.Balanced;
        public int    ManualContextSize        { get; set; } = 0;
        public LlmServerType ServerType        { get; set; } = LlmServerType.LlamaServer;
        public string CustomContextEndpoint    { get; set; } = "";
        public int    MicroCompactThreshold    { get; set; } = 85;
        public int    NearlineIngestThresholdChars { get; set; } = 8_000;
        public bool   MicroCompactSummarize    { get; set; } = true;
        public bool   MicroCompactBrainwash    { get; set; } = false;
        public bool   AlwaysConfirmPatch       { get; set; } = false;

        /// <summary>
        /// Whether to confirm every mutation. Settable at runtime: /mode writes here and the
        /// executor re-reads it on the next dispatch, which is the same way /think flips
        /// ShowLlmThinking mid-session.
        /// </summary>
        public ApprovalMode ApprovalMode        { get; set; } = ApprovalMode.Auto;
        public int    AgenticLoopMaxDepth      { get; set; } = 5;
        // Context-window utilization % at which the loop pauses to ask. 0 disables.
        public int    AgenticContextLimitPercent { get; set; } = 78;

        /// <summary>Session id to reopen at launch (<c>--resume &lt;id&gt;</c>). Blank = a new session.</summary>
        public string ResumeSessionId { get; set; } = "";

        /// <summary>Reopen the most recent session on this machine (<c>--continue</c> / <c>-c</c>).</summary>
        public bool   ContinueLatest  { get; set; } = false;

        /// <summary>Builds a TuiOptions from command-line args and environment variables.</summary>
        public static TuiOptions FromArgs(string[] args)
        {
            var opts = new TuiOptions();

            // Environment variable defaults.
            string envEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            string envApiKey = Environment.GetEnvironmentVariable("DEVMIND_API_KEY");
            if (!string.IsNullOrEmpty(envEndpoint)) opts.EndpointUrl = envEndpoint;
            if (!string.IsNullOrEmpty(envApiKey)) opts.ApiKey = envApiKey;

            // Pass 1: resolve --dir first.
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--dir")
                {
                    string dir = args[i + 1];
                    if (Directory.Exists(dir))
                        opts.WorkingDirectory = Path.GetFullPath(dir);
                    break;
                }
            }

            // Pass 2: CLI args override everything.
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--endpoint"     when i + 1 < args.Length: opts.EndpointUrl             = args[++i]; break;
                    case "--api-key"      when i + 1 < args.Length: opts.ApiKey                  = args[++i]; break;
                    case "--model"        when i + 1 < args.Length: opts.ModelName               = args[++i]; break;
                    case "--mode" when i + 1 < args.Length:
                        // Unknown values are ignored rather than fatal: the safe reading of
                        // a typo is the default, not a refusal to start.
                        if (ApprovalModeText.TryParse(args[++i], out ApprovalMode parsedMode))
                            opts.ApprovalMode = parsedMode;
                        break;
                    case "--system-prompt" when i + 1 < args.Length:
                        // Both: SystemPrompt keeps every existing reader working,
                        // ExplicitSystemPrompt records that this one was typed.
                        opts.ExplicitSystemPrompt = opts.SystemPrompt = args[++i];
                        break;
                    case "--build-command" when i + 1 < args.Length: opts.BuildCommand           = args[++i]; break;
                    case "--dir"          when i + 1 < args.Length: i++; break;
                    case "--max-depth"    when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int md)) opts.AgenticLoopMaxDepth = md; break;
                    case "--context-limit" when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int cl)) opts.AgenticContextLimitPercent = cl; break;
                    case "--context-size" when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int cs)) opts.ManualContextSize   = cs; break;
                    case "--timeout"      when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int to)) opts.RequestTimeoutMinutes = to; break;
                    case "--resume"       when i + 1 < args.Length: opts.ResumeSessionId        = args[++i]; break;
                    case "--continue":
                    case "-c"              : opts.ContinueLatest     = true;  break;
                    case "--thinking"      : opts.ShowLlmThinking    = true;  break;
                    case "--no-thinking"   : opts.ShowLlmThinking    = false; break;
                }
            }

            return opts;
        }
    }
}
