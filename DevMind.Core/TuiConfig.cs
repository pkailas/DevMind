// File: TuiConfig.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Global TUI config persisted to %APPDATA%\devmind\devmind.json.
// Atomic write-back (write to .tmp then rename).

using System;
using System.Collections.Generic;
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

        /// <summary>Persisted context-window utilization limit (percent). -1 means "not set"
        /// (use the startup default); 0 means explicitly disabled; 1-99 sets the limit.</summary>
        [JsonPropertyName("contextLimitPercent")]
        public int ContextLimitPercent { get; set; } = -1;

        /// <summary>When true, completed turns are captured as JSONL training data.</summary>
        [JsonPropertyName("trainingLogEnabled")]
        public bool TrainingLogEnabled { get; set; } = false;

       /// <summary>Folder for training-log JSONL files. Blank falls back to
        /// <c>training_logs</c> beside the executable.</summary>
        [JsonPropertyName("trainingLogFolder")]
        public string TrainingLogFolder { get; set; } = "";

        /// <summary>Named SQL connection strings. Keys are connection names, values are connection strings.
        /// Stored securely — never logged or echoed.</summary>
        [JsonPropertyName("sqlConnections")]
        public Dictionary<string, string> SqlConnections { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string ConfigPath => Path.Combine(
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

                if (root.TryGetProperty("contextLimitPercent", out var clp) && clp.ValueKind == JsonValueKind.Number
                    && clp.TryGetInt32(out int clpVal))
                    config.ContextLimitPercent = clpVal;

                if (root.TryGetProperty("trainingLogEnabled", out var tle)
                    && (tle.ValueKind == JsonValueKind.True || tle.ValueKind == JsonValueKind.False))
                    config.TrainingLogEnabled = tle.GetBoolean();

              if (root.TryGetProperty("trainingLogFolder", out var tlf) && tlf.ValueKind == JsonValueKind.String)
                    config.TrainingLogFolder = tlf.GetString() ?? "";

                if (root.TryGetProperty("sqlConnections", out var sc) && sc.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in sc.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            config.SqlConnections[prop.Name] = prop.Value.GetString() ?? "";
                    }
                }
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
