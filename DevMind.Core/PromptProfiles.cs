// File: PromptProfiles.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Saved system prompts: one .md file per named profile, under
// %APPDATA%\devmind\prompts\ (same state directory as devmind.json and
// system-prompt.md).
//
// Why a folder of files and not a section of devmind.json: a prompt is prose, and
// prose is edited in an editor, not inside JSON string escapes. The same reason
// system-prompt.md is a file.
//
// Design, inherited from SystemPromptFile:
//   * Read on EVERY call — no caching. The system prompt is rebuilt per turn, so a
//     profile edited in another editor takes effect on the next turn, and a profile
//     deleted mid-session degrades to the default chain instead of breaking it.
//   * Absence is a normal state. The prompts folder is NOT created here; an absent
//     folder means the operator has saved none, and reads return an empty list.
//   * All IO exceptions are swallowed — this sits inside prompt assembly, which must
//     never throw.
//   * Name validation is a security boundary, not cosmetics. The name becomes a file
//     name inside ProfilesDir, so anything outside a narrow character class could walk
//     out of that folder ("..", "\", "/", ":"). IsValidName is checked before any path
//     is built.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DevMind
{
    public static class PromptProfiles
    {
        /// <summary>
        /// Name of the folder holding saved prompt profiles inside the per-user
        /// DevMind state directory (%APPDATA%\devmind\).
        /// </summary>
        public const string ProfilesDirName = "prompts";

        /// <summary>Extension of every profile file. The profile name is the file name without it.</summary>
        public const string Extension = ".md";

        /// <summary>
        /// Absolute path to the saved-profiles folder
        /// (%APPDATA%\devmind\prompts). Derived from
        /// <see cref="DevMindPaths.GlobalDir"/>, so the DEVMIND_GLOBAL_DIR test seam
        /// redirects it. Intentionally NOT created here — an absent folder means no
        /// saved prompts, which is a normal state.
        /// </summary>
        public static string ProfilesDir =>
            System.IO.Path.Combine(DevMindPaths.GlobalDir, ProfilesDirName);

        /// <summary>
        /// Whether a name can safely become a file inside <see cref="ProfilesDir"/>:
        /// non-empty, and every character an ASCII letter, ASCII digit, '-' or '_'.
        /// Deliberately stricter than "no path separators" — the point is that no
        /// character in the name can be reinterpreted by the filesystem or the shell,
        /// so a name can never escape the folder and never depends on the machine's
        /// encoding. Non-ASCII letters are rejected for that reason, not because they
        /// are dangerous.
        /// </summary>
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z')
                       || (c >= 'A' && c <= 'Z')
                       || (c >= '0' && c <= '9')
                       || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>Saved profile names in the default folder, sorted. Empty when it is absent. Never throws.</summary>
        public static string[] ListProfiles() => ListProfilesIn(ProfilesDir);

        /// <summary>
        /// Saved profile names in an explicit folder — the hermetic test seam. Names are
        /// the file names without the extension, sorted case-insensitively so the listing
        /// is stable regardless of how the operator capitalised them on disk. An absent
        /// or unreadable folder is an empty list. Never throws.
        /// </summary>
        public static string[] ListProfilesIn(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    return Array.Empty<string>();

                var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in Directory.EnumerateFiles(dir, "*" + Extension))
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(file);
                    // A stray file with an unusable name (someone dropped "weird name.md"
                    // in there) is listed as nothing rather than as a name /prompt could
                    // never select — the listing and the setter must agree.
                    if (IsValidName(name))
                        names.Add(name);
                }
                return names.ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Loads a saved profile from the default folder. Null when the name is invalid,
        /// the file is absent, or its contents are empty/whitespace-only — the caller
        /// falls back to the default prompt chain. Never throws.
        /// </summary>
        public static string LoadProfile(string name) => LoadProfileIn(ProfilesDir, name);

        /// <summary>
        /// Loads a saved profile from an explicit folder — the hermetic test seam.
        /// An invalid name returns null WITHOUT touching the filesystem: the name is not
        /// allowed to become a path. Otherwise this is exactly
        /// <see cref="SystemPromptFile.LoadFrom"/>: trimmed contents, null for absent,
        /// empty or whitespace-only, and null for any IO error. Never throws.
        /// </summary>
        public static string LoadProfileIn(string dir, string name)
        {
            if (string.IsNullOrWhiteSpace(dir) || !IsValidName(name))
                return null;

            return SystemPromptFile.LoadFrom(System.IO.Path.Combine(dir, name + Extension));
        }
    }
}
