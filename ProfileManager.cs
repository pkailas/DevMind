// File: ProfileManager.cs  v3.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>
    /// A named connection profile storing endpoint, model, and context settings.
    /// </summary>
    public sealed class ProfileData
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("endpoint")]
        public string Endpoint { get; set; }

        [JsonProperty("apiKey")]
        public string ApiKey { get; set; }

        [JsonProperty("modelName")]
        public string ModelName { get; set; }

        [JsonProperty("manualContextSize")]
        public int ManualContextSize { get; set; }

        [JsonProperty("serverType")]
        public string ServerType { get; set; }

        [JsonProperty("contextEviction")]
        public string ContextEviction { get; set; }

        [JsonProperty("directiveMode")]
        public string DirectiveMode { get; set; }

        public override string ToString() => Name ?? Id ?? "(unnamed)";
    }

    /// <summary>
    /// Root JSON container for the profiles file.
    /// </summary>
    internal sealed class ProfileStore
    {
        /// <summary>
        /// Schema version — bumped to trigger one-time migration (e.g., reset stale profiles).
        /// </summary>
        [JsonProperty("version")]
        public int Version { get; set; }

        [JsonProperty("activeProfile")]
        public string ActiveProfile { get; set; }

        [JsonProperty("profiles")]
        public List<ProfileData> Profiles { get; set; } = new List<ProfileData>();

        internal const int CurrentVersion = 3;
    }

    /// <summary>
    /// Manages named connection profiles — save, load, switch, CRUD.
    /// Profiles are stored in <c>%LOCALAPPDATA%\DevMind\profiles.json</c>.
    /// </summary>
    public sealed class ProfileManager
    {
        private static readonly string ProfileDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevMind");

        private static readonly string ProfilePath =
            Path.Combine(ProfileDir, "profiles.json");

        private ProfileStore _store;

        /// <summary>
        /// Raised after a profile is applied to settings so the UI can refresh.
        /// </summary>
        public event Action<ProfileData> ProfileApplied;

        public ProfileManager()
        {
            Load();
        }

        /// <summary>
        /// Re-reads the profile store from disk. Call after external changes
        /// (e.g., profile actions from the Options page).
        /// </summary>
        public void Reload() => Load();

        // ── Query ────────────────────────────────────────────────────────────

        public IReadOnlyList<ProfileData> GetAllProfiles() => _store.Profiles.AsReadOnly();

        public ProfileData GetActiveProfile()
        {
            if (string.IsNullOrEmpty(_store.ActiveProfile))
                return _store.Profiles.FirstOrDefault();
            return _store.Profiles.FirstOrDefault(p =>
                string.Equals(p.Id, _store.ActiveProfile, StringComparison.OrdinalIgnoreCase))
                ?? _store.Profiles.FirstOrDefault();
        }

        public string ActiveProfileId => _store.ActiveProfile;

        // ── CRUD ─────────────────────────────────────────────────────────────

        public ProfileData CreateProfile(string name, string endpoint, string apiKey,
            string modelName, int manualContextSize, LlmServerType serverType,
            ContextEvictionMode contextEviction, DirectiveMode directiveMode = DirectiveMode.Auto)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Profile name cannot be empty.", nameof(name));
            if (_store.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A profile named '{name}' already exists.");

            var profile = new ProfileData
            {
                Id = Slugify(name),
                Name = name,
                Endpoint = endpoint ?? "",
                ApiKey = apiKey ?? "",
                ModelName = modelName ?? "",
                ManualContextSize = manualContextSize,
                ServerType = serverType.ToString(),
                ContextEviction = contextEviction.ToString(),
                DirectiveMode = directiveMode.ToString()
            };

            // Ensure unique ID
            if (_store.Profiles.Any(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase)))
                profile.Id = profile.Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);

            _store.Profiles.Add(profile);
            Save();
            return profile;
        }

        public ProfileData SaveCurrentAsProfile(string name)
        {
            var opts = DevMindOptions.Instance;
            var profile = CreateProfile(
                name,
                opts.EndpointUrl,
                opts.ApiKey,
                opts.ModelName,
                opts.ManualContextSize,
                opts.ServerType,
                opts.ContextEviction,
                opts.DirectiveMode);

            // Activate the newly created profile
            _store.ActiveProfile = profile.Id;
            Save();

            return profile;
        }

        public void UpdateProfile(ProfileData profile)
        {
            int idx = _store.Profiles.FindIndex(p =>
                string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                throw new InvalidOperationException($"Profile '{profile.Id}' not found.");
            _store.Profiles[idx] = profile;
            Save();
        }

        public void DeleteProfile(string id)
        {
            _store.Profiles.RemoveAll(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_store.ActiveProfile, id, StringComparison.OrdinalIgnoreCase))
                _store.ActiveProfile = _store.Profiles.FirstOrDefault()?.Id;
            Save();
        }

        public void RenameProfile(string id, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("Profile name cannot be empty.", nameof(newName));
            var profile = _store.Profiles.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (profile == null)
                throw new InvalidOperationException($"Profile '{id}' not found.");
            if (_store.Profiles.Any(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A profile named '{newName}' already exists.");
            profile.Name = newName;
            Save();
        }

        public ProfileData DuplicateProfile(string id)
        {
            var source = _store.Profiles.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (source == null)
                throw new InvalidOperationException($"Profile '{id}' not found.");

            string newName = source.Name + " (copy)";
            int n = 2;
            while (_store.Profiles.Any(p => string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                newName = source.Name + $" (copy {n++})";
            }

            Enum.TryParse(source.ServerType, true, out LlmServerType serverType);
            Enum.TryParse(source.ContextEviction, true, out ContextEvictionMode eviction);
            Enum.TryParse(source.DirectiveMode, true, out DirectiveMode dirMode);

            return CreateProfile(newName, source.Endpoint, source.ApiKey,
                source.ModelName, source.ManualContextSize, serverType, eviction, dirMode);
        }

        // ── Apply / Activate ─────────────────────────────────────────────────

        /// <summary>
        /// Writes the profile's values into DevMindOptions and saves.
        /// Does NOT call <c>DevMindOptions.Save()</c> to avoid triggering
        /// the Saved event redundantly — the caller should handle reconnection.
        /// </summary>
        public void SetActiveProfile(string id)
        {
            _store.ActiveProfile = id;
            Save();
        }

        /// <summary>
        /// Applies the given profile's settings to <see cref="DevMindOptions.Instance"/>
        /// and persists them. Raises <see cref="ProfileApplied"/>.
        /// </summary>
        public void ApplyProfile(ProfileData profile)
        {
            if (profile == null) return;

            var opts = DevMindOptions.Instance;
            opts.EndpointUrl = profile.Endpoint ?? "http://127.0.0.1:1234/v1";
            opts.ApiKey = profile.ApiKey ?? "";
            opts.ModelName = profile.ModelName ?? "";
            opts.ManualContextSize = profile.ManualContextSize;

            if (Enum.TryParse(profile.ServerType, true, out LlmServerType st))
                opts.ServerType = st;
            if (Enum.TryParse(profile.ContextEviction, true, out ContextEvictionMode ev))
                opts.ContextEviction = ev;
            if (Enum.TryParse(profile.DirectiveMode, true, out DirectiveMode dm))
                opts.DirectiveMode = dm;

            opts.Save();

            _store.ActiveProfile = profile.Id;
            Save();

            ProfileApplied?.Invoke(profile);
        }

        /// <summary>
        /// Overwrites the active profile's stored values with the current
        /// <see cref="DevMindOptions.Instance"/> settings. This is the explicit
        /// "save changes back to profile" action.
        /// </summary>
        public void UpdateActiveProfileFromSettings()
        {
            var active = GetActiveProfile();
            if (active == null) return;

            var opts = DevMindOptions.Instance;
            active.Endpoint = opts.EndpointUrl;
            active.ApiKey = opts.ApiKey;
            active.ModelName = opts.ModelName;
            active.ManualContextSize = opts.ManualContextSize;
            active.ServerType = opts.ServerType.ToString();
            active.ContextEviction = opts.ContextEviction.ToString();
            active.DirectiveMode = opts.DirectiveMode.ToString();
            Save();
        }

        /// <summary>
        /// Deletes all existing profiles and recreates a single "Default" profile
        /// from the current <see cref="DevMindOptions.Instance"/> values.
        /// </summary>
        public void ResetToDefault()
        {
            _store = new ProfileStore();
            var opts = DevMindOptions.Instance;
            var defaultProfile = new ProfileData
            {
                Id = "default",
                Name = "Default",
                Endpoint = opts.EndpointUrl,
                ApiKey = opts.ApiKey,
                ModelName = opts.ModelName,
                ManualContextSize = opts.ManualContextSize,
                ServerType = opts.ServerType.ToString(),
                ContextEviction = opts.ContextEviction.ToString(),
                DirectiveMode = opts.DirectiveMode.ToString()
            };
            _store.Profiles.Add(defaultProfile);
            _store.ActiveProfile = "default";
            Save();
        }

        // ── Persistence ──────────────────────────────────────────────────────

        private void Load()
        {
            try
            {
                if (File.Exists(ProfilePath))
                {
                    string json = File.ReadAllText(ProfilePath);
                    _store = JsonConvert.DeserializeObject<ProfileStore>(json) ?? new ProfileStore();
                }
                else
                {
                    _store = new ProfileStore();
                }
            }
            catch
            {
                _store = new ProfileStore();
            }

            // Migration: reset stale profiles from older schema versions
            if (_store.Version < ProfileStore.CurrentVersion)
            {
                _store = new ProfileStore();
            }

            // First run or post-migration: seed a "Default" profile from current settings
            if (_store.Profiles.Count == 0)
            {
                var opts = DevMindOptions.Instance;
                var defaultProfile = new ProfileData
                {
                    Id = "default",
                    Name = "Default",
                    Endpoint = opts.EndpointUrl,
                    ApiKey = opts.ApiKey,
                    ModelName = opts.ModelName,
                    ManualContextSize = opts.ManualContextSize,
                    ServerType = opts.ServerType.ToString(),
                    ContextEviction = opts.ContextEviction.ToString(),
                    DirectiveMode = opts.DirectiveMode.ToString()
                };
                _store.Profiles.Add(defaultProfile);
                _store.ActiveProfile = "default";
                _store.Version = ProfileStore.CurrentVersion;
                Save();
            }
        }

        private void Save()
        {
            try
            {
                if (!Directory.Exists(ProfileDir))
                    Directory.CreateDirectory(ProfileDir);

                // TODO: API keys are stored in plain text. Encrypt with DPAPI
                // (ProtectedData.Protect/Unprotect, DataProtectionScope.CurrentUser)
                // before serialization in a future pass.
                string json = JsonConvert.SerializeObject(_store, Formatting.Indented);
                File.WriteAllText(ProfilePath, json);
            }
            catch
            {
                // Best-effort — don't crash the extension on write failure.
            }
        }

        private static string Slugify(string name)
        {
            string slug = name.ToLowerInvariant().Trim();
            slug = Regex.Replace(slug, @"[^a-z0-9]+", "-");
            slug = slug.Trim('-');
            return string.IsNullOrEmpty(slug) ? "profile" : slug;
        }
    }
}
