// File: CliOptions.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.IO;
using System.Text.Json;

namespace DevMind
{
    /// <summary>
    /// CLI implementation of <see cref="ILlmOptions"/>. Property values are resolved
    /// in priority order: command-line args → devmind.json in working directory →
    /// hardcoded defaults (which match DevMindOptions where applicable).
    /// </summary>
    public sealed class CliOptions : ILlmOptions
    {
        // ── CLI-only properties (not part of ILlmOptions) ────────────────────────

        /// <summary>LLM endpoint base URL — e.g. http://127.0.0.1:1234/v1</summary>
        public string EndpointUrl { get; set; } = "http://127.0.0.1:1234/v1";

        /// <summary>Bearer token sent to the LLM endpoint.</summary>
        public string ApiKey { get; set; } = "lm-studio";

        /// <summary>Working directory for file operations. Defaults to CWD.</summary>
        public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();

        /// <summary>
        /// Explicit build command for run_build. Empty = auto-detect via
        /// <see cref="BuildCommandResolver"/> (DEVMIND_BUILD_COMMAND env var,
        /// then .vsixmanifest/package.json/.sln/.slnx/.csproj detection).
        /// </summary>
        public string BuildCommand { get; set; } = "";

        // ── ILlmOptions ───────────────────────────────────────────────────────────

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

        // Defaulting to false: brainwash performs a full-context replacement when
        // compaction thrashing is detected — too aggressive for CLI without explicit opt-in.
        public bool   MicroCompactBrainwash    { get; set; } = false;

        public bool   AlwaysConfirmPatch       { get; set; } = false;
        public int    AgenticLoopMaxDepth      { get; set; } = 5;
        // Per-turn generated-token budget before the loop pauses to ask. 0 disables.
        public int    AgenticTokenBudget       { get; set; } = 25000;

        // ── Factory ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a <see cref="CliOptions"/> from <paramref name="args"/>.
        /// Load order: --dir resolution → devmind.json merge → per-flag overrides.
        /// </summary>
        public static CliOptions FromArgs(string[] args)
        {
            var opts = new CliOptions();

            // Pass 1: resolve --dir first so devmind.json lookup uses the right directory.
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

            // Pass 2: merge devmind.json from working directory (lower priority than CLI args).
            string jsonPath = Path.Combine(opts.WorkingDirectory, "devmind.json");
            if (File.Exists(jsonPath))
                MergeJson(opts, jsonPath);

            // Pass 3: CLI args override everything.
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--endpoint"     when i + 1 < args.Length: opts.EndpointUrl             = args[++i]; break;
                    case "--api-key"      when i + 1 < args.Length: opts.ApiKey                  = args[++i]; break;
                    case "--model"        when i + 1 < args.Length: opts.ModelName               = args[++i]; break;
                    case "--system-prompt" when i + 1 < args.Length: opts.SystemPrompt           = args[++i]; break;
                    case "--build-command" when i + 1 < args.Length: opts.BuildCommand           = args[++i]; break;
                    case "--dir"          when i + 1 < args.Length: i++; break; // already applied in pass 1
                    case "--max-depth"    when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int md)) opts.AgenticLoopMaxDepth = md; break;
                    case "--context-size" when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int cs)) opts.ManualContextSize   = cs; break;
                    case "--timeout"      when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int to)) opts.RequestTimeoutMinutes = to; break;
                    case "--eviction"     when i + 1 < args.Length:
                        if (Enum.TryParse<ContextEvictionMode>(args[++i], ignoreCase: true, out var ev))
                            opts.ContextEviction = ev; break;
                    case "--server-type"  when i + 1 < args.Length:
                        if (Enum.TryParse<LlmServerType>(args[++i], ignoreCase: true, out var st))
                            opts.ServerType = st; break;
                    case "--always-confirm": opts.AlwaysConfirmPatch = true;  break;
                    case "--debug"         : opts.ShowDebugOutput    = true;  break;
                    case "--thinking"      : opts.ShowLlmThinking    = true;  break;
                    case "--no-thinking"   : opts.ShowLlmThinking    = false; break;
                    case "--no-budget"     : opts.ShowContextBudget  = false; break;
                    case "--brainwash"     : opts.MicroCompactBrainwash = true; break;
                }
            }

            return opts;
        }

        // ── JSON merge ────────────────────────────────────────────────────────────

        private static void MergeJson(CliOptions opts, string jsonPath)
        {
            try
            {
                string json = File.ReadAllText(jsonPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (TryString(root, "endpoint",            out string s)) opts.EndpointUrl             = s;
                if (TryString(root, "apiKey",              out s))        opts.ApiKey                  = s;
                if (TryString(root, "model",               out s))        opts.ModelName               = s;
                if (TryString(root, "systemPrompt",        out s))        opts.SystemPrompt            = s;
                if (TryString(root, "customContextEndpoint", out s))      opts.CustomContextEndpoint   = s;
                if (TryString(root, "buildCommand",        out s))        opts.BuildCommand            = s;

                if (TryInt(root, "maxDepth",               out int n))    opts.AgenticLoopMaxDepth     = n;
                if (TryInt(root, "manualContextSize",      out n))        opts.ManualContextSize       = n;
                if (TryInt(root, "requestTimeout",         out n))        opts.RequestTimeoutMinutes   = n;
                if (TryInt(root, "firstTokenTimeout",      out n))        opts.FirstTokenTimeoutMinutes = n;
                if (TryInt(root, "microCompactThreshold",  out n))        opts.MicroCompactThreshold   = n;

                if (TryBool(root, "alwaysConfirmPatch",    out bool b))   opts.AlwaysConfirmPatch      = b;
                if (TryBool(root, "microCompactSummarize", out b))        opts.MicroCompactSummarize   = b;
                if (TryBool(root, "microCompactBrainwash", out b))        opts.MicroCompactBrainwash   = b;
                if (TryBool(root, "showDebug",             out b))        opts.ShowDebugOutput         = b;
                if (TryBool(root, "showContextBudget",     out b))        opts.ShowContextBudget       = b;
                if (TryBool(root, "showThinking",          out b))        opts.ShowLlmThinking         = b;

                if (root.TryGetProperty("serverType", out var v) && v.ValueKind == JsonValueKind.String
                    && Enum.TryParse<LlmServerType>(v.GetString(), ignoreCase: true, out var st))
                    opts.ServerType = st;

                if (root.TryGetProperty("contextEviction", out v) && v.ValueKind == JsonValueKind.String
                    && Enum.TryParse<ContextEvictionMode>(v.GetString(), ignoreCase: true, out var ev))
                    opts.ContextEviction = ev;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CliOptions] Warning: failed to parse {jsonPath} — {ex.Message}");
            }
        }

        private static bool TryString(JsonElement root, string key, out string value)
        {
            if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            { value = v.GetString(); return true; }
            value = null; return false;
        }

        private static bool TryInt(JsonElement root, string key, out int value)
        {
            if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
            { value = v.GetInt32(); return true; }
            value = 0; return false;
        }

        private static bool TryBool(JsonElement root, string key, out bool value)
        {
            if (root.TryGetProperty(key, out var v) &&
                v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            { value = v.GetBoolean(); return true; }
            value = false; return false;
        }
    }
}
