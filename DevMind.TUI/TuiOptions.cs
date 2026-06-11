// File: TuiOptions.cs  v1.0 (SPIKE)
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Minimal ILlmOptions implementation for the TUI spike.
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
        public string SystemPrompt             { get; set; } = "You are a helpful coding assistant. Be concise and precise.";
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
        public bool   MicroCompactSummarize    { get; set; } = true;
        public bool   MicroCompactBrainwash    { get; set; } = false;
        public bool   AlwaysConfirmPatch       { get; set; } = false;
        public int    AgenticLoopMaxDepth      { get; set; } = 5;

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
                    case "--system-prompt" when i + 1 < args.Length: opts.SystemPrompt           = args[++i]; break;
                    case "--build-command" when i + 1 < args.Length: opts.BuildCommand           = args[++i]; break;
                    case "--dir"          when i + 1 < args.Length: i++; break;
                    case "--max-depth"    when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int md)) opts.AgenticLoopMaxDepth = md; break;
                    case "--context-size" when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int cs)) opts.ManualContextSize   = cs; break;
                    case "--timeout"      when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int to)) opts.RequestTimeoutMinutes = to; break;
                    case "--thinking"      : opts.ShowLlmThinking    = true;  break;
                    case "--no-thinking"   : opts.ShowLlmThinking    = false; break;
                }
            }

            return opts;
        }
    }
}
