// File: TuiConfig.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Global TUI config persisted to %APPDATA%\devmind\devmind.json.
// Atomic write-back (write to .tmp then rename).

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevMind
{
    /// <summary>
    /// Global configuration for DevMind.TUI, persisted to
    /// %APPDATA%\devmind\devmind.json with atomic write-back.
    /// </summary>
    public sealed class TuiConfig
    {
        private const string ConfigDirName = "devmind";
        private const string ConfigFileName = "devmind.json";

        [JsonPropertyName("behavioralRules")]
        public string BehavioralRules { get; set; } = "";

        [JsonPropertyName("workingDirectory")]
        public string WorkingDirectory { get; set; } = null;

        /// <summary>Persisted agentic depth cap (1-200). 0 means "not set" — the
        /// startup default / CLI --max-depth applies instead.</summary>
        [JsonPropertyName("depthCap")]
        public int DepthCap { get; set; } = 0;

        /// <summary>Persisted per-turn generated-token budget. -1 means "not set" (use the
        /// startup default); 0 means explicitly disabled; &gt;0 sets the budget.</summary>
        [JsonPropertyName("tokenBudget")]
        public int TokenBudget { get; set; } = -1;

        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ConfigDirName, ConfigFileName);

        /// <summary>
        /// Loads the config from disk. Returns defaults if the file does not exist
        /// or if parsing fails silently.
        /// </summary>
        public static TuiConfig Load()
        {
            var config = new TuiConfig();
            string path = ConfigPath;

            if (!File.Exists(path))
                return config;

            try
            {
                string json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("behavioralRules", out var rules) && rules.ValueKind == JsonValueKind.String)
                    config.BehavioralRules = rules.GetString() ?? "";

                if (root.TryGetProperty("workingDirectory", out var dir) && dir.ValueKind == JsonValueKind.String)
                    config.WorkingDirectory = dir.GetString();

                if (root.TryGetProperty("depthCap", out var depth) && depth.ValueKind == JsonValueKind.Number
                    && depth.TryGetInt32(out int depthVal))
                    config.DepthCap = depthVal;

                if (root.TryGetProperty("tokenBudget", out var tb) && tb.ValueKind == JsonValueKind.Number
                    && tb.TryGetInt32(out int tbVal))
                    config.TokenBudget = tbVal;
            }
            catch
            {
                // Silently ignore parse errors — return defaults.
            }

            return config;
        }

        /// <summary>
        /// Persists the config to disk using atomic write (write to .tmp, then rename).
        /// </summary>
        public void Save()
        {
            string path = ConfigPath;
            string dir = Path.GetDirectoryName(path);

            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string tmpPath = path + ".tmp";

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                };

                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(tmpPath, json, Encoding.UTF8);
                // overwrite: true — File.Move(src, dest) throws if dest already exists,
                // which silently broke every save after the file was first created.
                File.Move(tmpPath, path, overwrite: true);
            }
            catch
            {
                // Silently ignore write errors — config is best-effort.
            }
        }
    }
}
