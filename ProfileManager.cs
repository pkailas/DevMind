// File: ProfileManager.cs  v4.3
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
    /// A named profile storing a full snapshot of DevMind settings. When a
    /// profile is applied, every field below is written into
    /// <see cref="DevMindOptions.Instance"/>. The initializer values on each
    /// property MUST mirror the defaults declared on <see cref="DevMindOptions"/>
    /// so a freshly-constructed <c>ProfileData</c> represents a pristine install.
    /// </summary>
    public sealed class ProfileData
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        // ── Connection ───────────────────────────────────────────────────────
        [JsonProperty("endpoint")]
        public string Endpoint { get; set; } = "http://127.0.0.1:1234/v1";

        [JsonProperty("apiKey")]
        public string ApiKey { get; set; } = "lm-studio";

        [JsonProperty("serverType")]
        public string ServerType { get; set; } = nameof(LlmServerType.LlamaServer);

        [JsonProperty("customContextEndpoint")]
        public string CustomContextEndpoint { get; set; } = "";

        [JsonProperty("manualContextSize")]
        public int ManualContextSize { get; set; } = 0;

        [JsonProperty("firstTokenTimeoutMinutes")]
        public int FirstTokenTimeoutMinutes { get; set; } = 5;

        [JsonProperty("requestTimeoutMinutes")]
        public int RequestTimeoutMinutes { get; set; } = 10;

        // ── Model ────────────────────────────────────────────────────────────
        [JsonProperty("modelName")]
        public string ModelName { get; set; } = "";

        // ── Prompt ───────────────────────────────────────────────────────────
        [JsonProperty("systemPrompt")]
        public string SystemPrompt { get; set; }
            = "You are a helpful coding assistant. Be concise and precise.";

        // ── File Generation ──────────────────────────────────────────────────
        [JsonProperty("openFileAfterGeneration")]
        public bool OpenFileAfterGeneration { get; set; } = true;

        // ── Agentic Loop ─────────────────────────────────────────────────────
        [JsonProperty("agenticLoopMaxDepth")]
        public int AgenticLoopMaxDepth { get; set; } = 5;

        [JsonProperty("blockByBlockMode")]
        public string BlockByBlockMode { get; set; } = nameof(BlockByBlockModeType.Auto);

        [JsonProperty("alwaysConfirmPatch")]
        public bool AlwaysConfirmPatch { get; set; } = false;

        // ── Context Management ───────────────────────────────────────────────
        [JsonProperty("contextEviction")]
        public string ContextEviction { get; set; } = nameof(ContextEvictionMode.Balanced);

        [JsonProperty("showContextBudget")]
        public bool ShowContextBudget { get; set; } = true;

        [JsonProperty("showDebugOutput")]
        public bool ShowDebugOutput { get; set; } = false;

        [JsonProperty("microCompactThreshold")]
        public int MicroCompactThreshold { get; set; } = 85;

        [JsonProperty("microCompactSummarize")]
        public bool MicroCompactSummarize { get; set; } = true;

        [JsonProperty("microCompactBrainwash")]
        public bool MicroCompactBrainwash { get; set; } = false;

        // ── Directives ───────────────────────────────────────────────────────
        [JsonProperty("directiveMode")]
        public string DirectiveMode { get; set; } = nameof(DevMind.DirectiveMode.Auto);

        // ── Display ──────────────────────────────────────────────────────────
        [JsonProperty("showLlmThinking")]
        public bool ShowLlmThinking { get; set; } = false;

        // ── Training Data ────────────────────────────────────────────────────
        [JsonProperty("trainingLogEnabled")]
        public bool TrainingLogEnabled { get; set; } = false;

        [JsonProperty("trainingLogFolder")]
        public string TrainingLogFolder { get; set; } = "";

        public override string ToString() => Name ?? Id ?? "(unnamed)";
    }

    /// <summary>
    /// Root JSON container for the profiles file.
    /// </summary>
    internal sealed class ProfileStore
    {
        /// <summary>
        /// Schema version — bumped to trigger migration.
        /// v3 → v4: added 16 previously-unpersisted DevMindOptions fields.
        /// </summary>
        [JsonProperty("version")]
        public int Version { get; set; }

        [JsonProperty("activeProfile")]
        public string ActiveProfile { get; set; }

        [JsonProperty("profiles")]
        public List<ProfileData> Profiles { get; set; } = new List<ProfileData>();

        internal const int CurrentVersion = 4;
    }

    /// <summary>
    /// Manages named connection profiles — save, load, switch, CRUD.
    /// Profiles are stored in <c>%LOCALAPPDATA%\DevMind\profiles.json</c>.
    /// </summary>
    public sealed class ProfileManager
    {
        private const string DefaultProfileId = "default";

        private static readonly string ProfileDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevMind");

        private static readonly string ProfilePath =
            Path.Combine(ProfileDir, "profiles.json");

        private ProfileStore _store;

        /// <summary>
        /// Raised after a profile is applied to settings so the UI can refresh.
        /// </summary>
        public event Action<ProfileData> ProfileApplied;

        // ── Notification sink ────────────────────────────────────────────────
        // ProfileManager is constructed eagerly at tool-window startup, before
        // the OutputBox is ready to accept messages. We queue notifications
        // emitted during early construction and flush them when the tool window
        // attaches a sink via AttachNotificationSink. Static so the queue
        // survives across multiple ProfileManager instances (each
        // DevMindOptionsPage.Save() creates a fresh one).
        private static event Action<string> _notification;
        private static readonly List<string> _pendingNotifications = new List<string>();
        private static readonly object _notifyLock = new object();

        /// <summary>
        /// Subscribes a sink to receive ProfileManager notifications (corruption
        /// detected, Save failed). Any notifications emitted before the first
        /// sink is attached are flushed immediately on attach.
        /// </summary>
        public static void AttachNotificationSink(Action<string> sink)
        {
            if (sink == null) return;
            List<string> backlog;
            lock (_notifyLock)
            {
                _notification += sink;
                backlog = new List<string>(_pendingNotifications);
                _pendingNotifications.Clear();
            }
            // Drain outside the lock so the sink can't deadlock by re-entering.
            foreach (var msg in backlog)
            {
                try { sink(msg); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[PROFILE] Sink threw: {ex.Message}"); }
            }
        }

        private static void RaiseNotification(string message)
        {
            Action<string> handler;
            lock (_notifyLock)
            {
                handler = _notification;
                if (handler == null)
                {
                    _pendingNotifications.Add(message);
                    return;
                }
            }
            try { handler(message); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[PROFILE] Sink threw: {ex.Message}"); }
        }

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

        /// <summary>
        /// Creates a new profile, snapshotting every setting from
        /// <see cref="DevMindOptions.Instance"/> at the moment of the call.
        /// </summary>
        public ProfileData CreateProfile(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Profile name cannot be empty.", nameof(name));
            if (_store.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A profile named '{name}' already exists.");

            var profile = new ProfileData
            {
                Id = Slugify(name),
                Name = name
            };
            SnapshotInstanceTo(profile);

            // Ensure unique ID
            if (_store.Profiles.Any(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase)))
                profile.Id = profile.Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);

            _store.Profiles.Add(profile);
            Save();
            return profile;
        }

        public ProfileData SaveCurrentAsProfile(string name)
        {
            // Inline the create-logic rather than chaining CreateProfile + Save,
            // so the "add profile + mark it active" mutation lands in a single
            // disk write. Two writes risked a torn-state window where the new
            // profile existed but wasn't yet active — and doubled the IO cost
            // for what the user sees as one atomic action.
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Profile name cannot be empty.", nameof(name));
            if (_store.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A profile named '{name}' already exists.");

            var profile = new ProfileData
            {
                Id = Slugify(name),
                Name = name
            };
            SnapshotInstanceTo(profile);

            if (_store.Profiles.Any(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase)))
                profile.Id = profile.Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);

            _store.Profiles.Add(profile);
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
            // Refuse to delete the last remaining profile — the user would
            // otherwise land in a profileless state requiring a JSON edit or
            // reinstall to recover. The Options page is expected to prevent
            // this in the UI; we throw defensively so the caller can surface
            // a clear error.
            if (_store.Profiles.Count <= 1)
                throw new InvalidOperationException("Cannot delete the only remaining profile.");

            bool wasActive = string.Equals(_store.ActiveProfile, id, StringComparison.OrdinalIgnoreCase);
            int removed = _store.Profiles.RemoveAll(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return; // Id not found — no-op, no apply.

            ProfileData next = null;
            if (wasActive)
            {
                next = _store.Profiles.FirstOrDefault();
                _store.ActiveProfile = next?.Id;
            }

            // Persist the deletion FIRST, then apply the next profile's values
            // to DevMindOptions.Instance. Ordering matters: if a crash happens
            // between Save() and ApplyProfile(), the settings store may still
            // hold the deleted profile's values, but profiles.json will have
            // shifted the active pointer on restart — the next launch will
            // reconcile by applying the (now) active profile's stored values
            // via normal load paths, rather than leaving a dangling reference.
            Save();

            if (wasActive && next != null)
                ApplyProfile(next);
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

            // Deep-copy via JSON round-trip so every field (including any added
            // later) is duplicated without another maintenance burden.
            var clone = JsonConvert.DeserializeObject<ProfileData>(
                JsonConvert.SerializeObject(source));
            clone.Name = newName;
            clone.Id = Slugify(newName);
            if (_store.Profiles.Any(p => string.Equals(p.Id, clone.Id, StringComparison.OrdinalIgnoreCase)))
                clone.Id = clone.Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);

            _store.Profiles.Add(clone);
            Save();
            return clone;
        }

        // ── Apply / Activate ─────────────────────────────────────────────────

        /// <summary>
        /// Sets the active profile id without applying its values. Use this
        /// only when the caller will apply values separately.
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

            // Connection
            opts.EndpointUrl = profile.Endpoint ?? "http://127.0.0.1:1234/v1";
            opts.ApiKey = profile.ApiKey ?? "";
            if (Enum.TryParse(profile.ServerType, true, out LlmServerType st))
                opts.ServerType = st;
            opts.CustomContextEndpoint = profile.CustomContextEndpoint ?? "";
            opts.ManualContextSize = profile.ManualContextSize;
            opts.FirstTokenTimeoutMinutes = profile.FirstTokenTimeoutMinutes;
            opts.RequestTimeoutMinutes = profile.RequestTimeoutMinutes;

            // Model
            opts.ModelName = profile.ModelName ?? "";

            // Prompt
            opts.SystemPrompt = profile.SystemPrompt ?? "";

            // File Generation
            opts.OpenFileAfterGeneration = profile.OpenFileAfterGeneration;

            // Agentic Loop
            opts.AgenticLoopMaxDepth = profile.AgenticLoopMaxDepth;
            if (Enum.TryParse(profile.BlockByBlockMode, true, out BlockByBlockModeType bbm))
                opts.BlockByBlockMode = bbm;
            opts.AlwaysConfirmPatch = profile.AlwaysConfirmPatch;

            // Context Management
            if (Enum.TryParse(profile.ContextEviction, true, out ContextEvictionMode ev))
                opts.ContextEviction = ev;
            opts.ShowContextBudget = profile.ShowContextBudget;
            opts.ShowDebugOutput = profile.ShowDebugOutput;
            opts.MicroCompactThreshold = profile.MicroCompactThreshold;
            opts.MicroCompactSummarize = profile.MicroCompactSummarize;
            opts.MicroCompactBrainwash = profile.MicroCompactBrainwash;

            // Directives
            if (Enum.TryParse(profile.DirectiveMode, true, out DirectiveMode dm))
                opts.DirectiveMode = dm;

            // Display
            opts.ShowLlmThinking = profile.ShowLlmThinking;

            // Training Data
            opts.TrainingLogEnabled = profile.TrainingLogEnabled;
            opts.TrainingLogFolder = profile.TrainingLogFolder ?? "";

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

            SnapshotInstanceTo(active);
            Save();
        }

        /// <summary>
        /// Deletes all existing profiles and recreates a single pristine
        /// "Default" profile from the <see cref="DevMindOptions"/> default
        /// values (NOT from <see cref="DevMindOptions.Instance"/>).
        /// </summary>
        public void ResetToDefault()
        {
            _store = new ProfileStore();
            // Field initializers on ProfileData supply the pristine defaults.
            var defaultProfile = new ProfileData
            {
                Id = DefaultProfileId,
                Name = "Default"
            };
            _store.Profiles.Add(defaultProfile);
            _store.ActiveProfile = DefaultProfileId;
            Save();
        }

        /// <summary>
        /// Copies every settable field from <see cref="DevMindOptions.Instance"/>
        /// onto <paramref name="p"/>. Id and Name are untouched.
        /// </summary>
        private static void SnapshotInstanceTo(ProfileData p)
        {
            var opts = DevMindOptions.Instance;

            // Connection
            p.Endpoint = opts.EndpointUrl;
            p.ApiKey = opts.ApiKey;
            p.ServerType = opts.ServerType.ToString();
            p.CustomContextEndpoint = opts.CustomContextEndpoint;
            p.ManualContextSize = opts.ManualContextSize;
            p.FirstTokenTimeoutMinutes = opts.FirstTokenTimeoutMinutes;
            p.RequestTimeoutMinutes = opts.RequestTimeoutMinutes;

            // Model
            p.ModelName = opts.ModelName;

            // Prompt
            p.SystemPrompt = opts.SystemPrompt;

            // File Generation
            p.OpenFileAfterGeneration = opts.OpenFileAfterGeneration;

            // Agentic Loop
            p.AgenticLoopMaxDepth = opts.AgenticLoopMaxDepth;
            p.BlockByBlockMode = opts.BlockByBlockMode.ToString();
            p.AlwaysConfirmPatch = opts.AlwaysConfirmPatch;

            // Context Management
            p.ContextEviction = opts.ContextEviction.ToString();
            p.ShowContextBudget = opts.ShowContextBudget;
            p.ShowDebugOutput = opts.ShowDebugOutput;
            p.MicroCompactThreshold = opts.MicroCompactThreshold;
            p.MicroCompactSummarize = opts.MicroCompactSummarize;
            p.MicroCompactBrainwash = opts.MicroCompactBrainwash;

            // Directives
            p.DirectiveMode = opts.DirectiveMode.ToString();

            // Display
            p.ShowLlmThinking = opts.ShowLlmThinking;

            // Training Data
            p.TrainingLogEnabled = opts.TrainingLogEnabled;
            p.TrainingLogFolder = opts.TrainingLogFolder;
        }

        // ── Persistence ──────────────────────────────────────────────────────

        private void Load()
        {
            // Distinguish first-run (file absent) from corruption (file exists
            // but unreadable or malformed). Only the latter warrants a backup
            // and user notification.
            if (!File.Exists(ProfilePath))
            {
                _store = new ProfileStore();
            }
            else
            {
                try
                {
                    string json = File.ReadAllText(ProfilePath);
                    _store = JsonConvert.DeserializeObject<ProfileStore>(json) ?? new ProfileStore();
                }
                catch (Exception ex)
                {
                    // Corruption: preserve the bad file for forensics before
                    // we overwrite it with a seeded Default on save. The backup
                    // is a best-effort copy — if copy itself fails (permissions,
                    // disk full), we still reset to a pristine store and surface
                    // a distinct notification so the user knows.
                    string backupName = null;
                    try { backupName = BackupCorruptFile(); }
                    catch (Exception copyEx) { System.Diagnostics.Debug.WriteLine($"[PROFILE] Backup of corrupt file failed: {copyEx.Message}"); }

                    _store = new ProfileStore();

                    RaiseNotification(backupName != null
                        ? $"[PROFILE] profiles.json was corrupt — backed up to {backupName} and reset to Default. Error: {ex.Message}"
                        : $"[PROFILE] profiles.json was corrupt and could not be backed up. Reset to Default. Error: {ex.Message}");
                }
            }

            // ── Migration ──
            if (_store.Version < ProfileStore.CurrentVersion)
            {
                if (_store.Version == 3 && _store.Profiles.Count > 0)
                {
                    // v3 → v4: existing profiles lack the 16 newly-persisted fields.
                    // Newtonsoft.Json leaves missing fields at their property
                    // initializer defaults; for non-Default profiles we overwrite
                    // those with the user's current live settings so each existing
                    // profile starts its new fields from a realistic snapshot
                    // rather than pristine defaults. The Default profile keeps
                    // pristine defaults to match its "reset" semantics.
                    MigrateV3ToV4();
                    _store.Version = ProfileStore.CurrentVersion;
                    Save();
                }
                else
                {
                    // Unknown earlier version or empty — wipe and fall through
                    // to the seeding branch below.
                    _store = new ProfileStore();
                }
            }

            // First run or post-wipe: seed a pristine "Default" profile.
            if (_store.Profiles.Count == 0)
            {
                var defaultProfile = new ProfileData
                {
                    Id = DefaultProfileId,
                    Name = "Default"
                    // All other fields use initializer defaults.
                };
                _store.Profiles.Add(defaultProfile);
                _store.ActiveProfile = DefaultProfileId;
                _store.Version = ProfileStore.CurrentVersion;
                Save();
            }
        }

        private void MigrateV3ToV4()
        {
            var opts = DevMindOptions.Instance;
            foreach (var p in _store.Profiles)
            {
                bool isDefault = string.Equals(p.Id, DefaultProfileId, StringComparison.OrdinalIgnoreCase);
                if (isDefault)
                {
                    // Default profile: pristine initializer values already in place.
                    continue;
                }

                // Snapshot the 16 newly-persisted fields from the user's current
                // live settings. (The other 7 fields were present in v3 and were
                // restored correctly by the deserializer.)
                p.SystemPrompt = opts.SystemPrompt;
                p.OpenFileAfterGeneration = opts.OpenFileAfterGeneration;
                p.AgenticLoopMaxDepth = opts.AgenticLoopMaxDepth;
                p.BlockByBlockMode = opts.BlockByBlockMode.ToString();
                p.AlwaysConfirmPatch = opts.AlwaysConfirmPatch;
                p.FirstTokenTimeoutMinutes = opts.FirstTokenTimeoutMinutes;
                p.RequestTimeoutMinutes = opts.RequestTimeoutMinutes;
                p.CustomContextEndpoint = opts.CustomContextEndpoint;
                p.ShowContextBudget = opts.ShowContextBudget;
                p.ShowDebugOutput = opts.ShowDebugOutput;
                p.MicroCompactThreshold = opts.MicroCompactThreshold;
                p.MicroCompactSummarize = opts.MicroCompactSummarize;
                p.MicroCompactBrainwash = opts.MicroCompactBrainwash;
                p.ShowLlmThinking = opts.ShowLlmThinking;
                p.TrainingLogEnabled = opts.TrainingLogEnabled;
                p.TrainingLogFolder = opts.TrainingLogFolder;
            }
        }

        private void Save()
        {
            // Always stamp the current schema version so callers that mutate
            // _store without going through the Load() seed path (e.g., ResetToDefault)
            // don't produce a file that the next Load() treats as stale and wipes.
            _store.Version = ProfileStore.CurrentVersion;

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
            catch (Exception ex)
            {
                // Best-effort — don't crash the extension on write failure.
                // Surface the failure to Debug and to any attached sink so the
                // user knows their change didn't persist (previously this was
                // silent, which hid real "disk full" / "permission denied"
                // problems until the user noticed settings didn't survive
                // restarts). Do NOT rethrow.
                System.Diagnostics.Debug.WriteLine($"[PROFILE] Save failed: {ex.Message}");
                RaiseNotification($"[PROFILE] Save failed — your latest profile change was not persisted. {ex.Message}");
            }
        }

        /// <summary>
        /// Copies the current profiles.json to a timestamped backup so it can
        /// be inspected after a corruption-detected load. Returns the backup
        /// file name (leaf only) for display.
        /// </summary>
        private static string BackupCorruptFile()
        {
            // Millisecond-precision timestamp reduces collision risk; if two
            // corrupt loads happen within the same millisecond (effectively
            // impossible on modern hardware but defensible), a numeric suffix
            // disambiguates. Leaf-only return keeps UI messages short —
            // ProfileDir is well-known.
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string name = $"profiles.json.corrupt-{stamp}";
            string path = Path.Combine(ProfileDir, name);
            int n = 1;
            while (File.Exists(path))
            {
                name = $"profiles.json.corrupt-{stamp}-{n++}";
                path = Path.Combine(ProfileDir, name);
            }
            File.Copy(ProfilePath, path, overwrite: false);
            return name;
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
