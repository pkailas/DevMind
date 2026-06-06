// File: DevMindOptionsPage.cs  v7.10
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Community.VisualStudio.Toolkit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace DevMind
{
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
        public class DevMindOptionsPage : BaseOptionPage<DevMindOptions>
        {
            // OnClosed fires exactly once per Tools > Options dialog close,
            // regardless of how many times the user toggled between Options
            // categories in the left tree. On OK, Save() already consumed and
            // cleared the pending switch; on Cancel, Save() was never called,
            // so this hook discards the stale value before the next dialog
            // session sees it. OnActivate would be wrong here — it refires on
            // every page-tree selection and would wipe a live in-progress pick.
            protected override void OnClosed(EventArgs e)
            {
                DevMindOptions.Instance?.ClearPendingProfileSwitch();
                base.OnClosed(e);
            }
        }
    }

    /// <summary>
    /// Stores DevMind extension settings. Access via <see cref="DevMindOptions.Instance"/>
    /// or <see cref="BaseOptionModel{T}.GetLiveInstanceAsync"/>.
    /// Data properties are defined in DevMindOptions.Data.cs (partial class).
    /// </summary>
    public partial class DevMindOptions : BaseOptionModel<DevMindOptions>
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
                // Defer the switch: validate now, apply at end of Save() after
                // base.Save() has persisted the user's live edits. This way
                // clicking Cancel on the Options dialog discards the pending
                // switch without ever mutating profiles.json or the settings
                // store. See Save() for the apply-at-end logic.
                if (string.IsNullOrWhiteSpace(value) || value == "(none)")
                    return;
                try
                {
                    var pm = new ProfileManager();
                    var active = pm.GetActiveProfile();
                    if (active != null && string.Equals(active.Name, value, StringComparison.OrdinalIgnoreCase))
                    {
                        // Selecting the active profile is a no-op.
                        _pendingProfileSwitch = null;
                        return;
                    }
                    var target = pm.GetAllProfiles()
                        .FirstOrDefault(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase));
                    if (target != null)
                        _pendingProfileSwitch = target.Id;
                }
                catch
                {
                    // Swallow — a corrupt profile store should not break the dropdown.
                    // Save() will also guard its own pm-access path.
                }
            }
        }

        // Holds the Id of the profile the user selected from the dropdown during
        // the current Options-dialog session. Applied at the end of Save() —
        // NOT in the setter — so Cancel discards it without touching disk.
        private string _pendingProfileSwitch;

        /// <summary>
        /// Clears any deferred profile switch. Called from <c>OnClosed</c> on
        /// the page so a Cancel'd dialog leaves no stale pending value behind
        /// for the next dialog session (both the page and the model singleton
        /// outlive the dialog).
        /// </summary>
        internal void ClearPendingProfileSwitch() => _pendingProfileSwitch = null;

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

        // Reentry guard: a profile action inside Save() (e.g., ApplyProfile)
        // calls DevMindOptions.Save() again, which re-enters this override.
        // Without this flag, a still-true trigger would cause the nested call
        // to re-run the action (e.g., cascading delete-confirmation dialogs).
        // [ThreadStatic] isolates per-thread so unrelated threads can't interfere;
        // VS Options dialog edits run on the UI thread so the recursive chain
        // stays on the same thread and sees the flag set.
        [ThreadStatic]
        private static bool _inSave;

        public override void Save()
        {
            if (_inSave)
            {
                // Reentrant call — skip profile actions and just persist
                // whatever is on the instance now, so the settings-store
                // write still happens for the inner caller.
                base.Save();
                return;
            }

            _inSave = true;
            try
            {
                bool changed = false;

                // ── Save as new profile ──
                if (!string.IsNullOrWhiteSpace(SaveAsNewProfile))
                {
                    // Reset only on success so a failure (e.g. duplicate name)
                    // leaves the user's typed value in the field for correction
                    // and retry. The Bug 2 reentry guard (_inSave) still prevents
                    // cascading Save() from re-executing this block — clearing
                    // the trigger at the top is no longer needed for reentry
                    // safety.
                    try
                    {
                        var pm = new ProfileManager();
                        pm.SaveCurrentAsProfile(SaveAsNewProfile);
                        SaveAsNewProfile = "";
                        changed = true;
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Windows.MessageBox.Show(ex.Message, "DevMind — Save Profile",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                // ── Delete current profile ──
                if (DeleteCurrentProfile)
                {
                    try
                    {
                        var pm = new ProfileManager();
                        var active = pm.GetActiveProfile();
                        if (active == null)
                        {
                            // Nothing to delete — resolved, clear trigger.
                            DeleteCurrentProfile = false;
                        }
                        else
                        {
                            var result = System.Windows.MessageBox.Show(
                                $"Delete profile \"{active.Name}\"?\nThis cannot be undone.",
                                "DevMind — Delete Profile",
                                MessageBoxButton.YesNo, MessageBoxImage.Question);
                            if (result == MessageBoxResult.Yes)
                            {
                                // DeleteProfile internally applies the next
                                // active profile (Bug 6). Throws if this is
                                // the only remaining profile — trigger stays
                                // true so the user can create another profile
                                // first and retry with one click.
                                pm.DeleteProfile(active.Id);
                                DeleteCurrentProfile = false;
                                changed = true;
                            }
                            else
                            {
                                // User declined — deliberate choice, not a
                                // failure. Clear so the prompt doesn't reappear
                                // on the next unrelated Save().
                                DeleteCurrentProfile = false;
                            }
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Windows.MessageBox.Show(ex.Message, "DevMind — Delete Profile",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                // ── Rename current profile ──
                if (!string.IsNullOrWhiteSpace(RenameCurrentProfile))
                {
                    try
                    {
                        var pm = new ProfileManager();
                        var active = pm.GetActiveProfile();
                        if (active == null)
                        {
                            // Nothing to rename — resolved, clear trigger.
                            RenameCurrentProfile = "";
                        }
                        else
                        {
                            // Reset only on success so the user's typed name
                            // survives a duplicate-name error for edit-and-retry.
                            pm.RenameProfile(active.Id, RenameCurrentProfile);
                            RenameCurrentProfile = "";
                            changed = true;
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Windows.MessageBox.Show(ex.Message, "DevMind — Rename Profile",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                // ── Update current profile ──
                if (UpdateCurrentProfile)
                {
                    try
                    {
                        var pm = new ProfileManager();
                        var active = pm.GetActiveProfile();
                        if (active == null)
                        {
                            // Nothing to update — resolved, clear trigger.
                            UpdateCurrentProfile = false;
                        }
                        else
                        {
                            pm.UpdateActiveProfileFromSettings();
                            UpdateCurrentProfile = false;
                            changed = true;
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Windows.MessageBox.Show(ex.Message, "DevMind — Update Profile",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                base.Save();

                // ── Apply deferred profile switch ──
                // Runs AFTER base.Save() so the user's live edits are persisted
                // to settings storage first. ApplyProfile then overwrites those
                // fields with the target profile's values — intentional: the
                // dropdown change expresses an intent to adopt that profile.
                // ApplyProfile calls opts.Save() internally, which re-enters
                // this override; the Bug 2 reentry guard short-circuits that
                // nested call to just base.Save().
                if (!string.IsNullOrEmpty(_pendingProfileSwitch))
                {
                    string targetId = _pendingProfileSwitch;
                    _pendingProfileSwitch = null;
                    try
                    {
                        var pm = new ProfileManager();
                        var target = pm.GetAllProfiles()
                            .FirstOrDefault(p => string.Equals(p.Id, targetId, StringComparison.OrdinalIgnoreCase));
                        if (target != null)
                        {
                            pm.SetActiveProfile(target.Id);
                            pm.ApplyProfile(target);
                            changed = true;
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Windows.MessageBox.Show(ex.Message, "DevMind — Switch Profile",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                if (changed)
                    ProfileChanged?.Invoke();
            }
            finally
            {
                _inSave = false;
            }
        }
    }
}
