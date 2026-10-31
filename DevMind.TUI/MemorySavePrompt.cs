// File: MemorySavePrompt.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The text and the answer rules of the TUI's "Save memory?" prompt, which
// TuiAgenticHost.ConfirmMemorySaveAsync shows before any save_memory write. Kept out of the
// host so the message, the validation and the answer-to-choice mapping can be tested
// without a running Terminal.Gui app.

#nullable enable

using System;
using System.Globalization;
using System.IO;

namespace DevMind
{
    internal static class MemorySavePrompt
    {
        internal const string Title = "Save memory?";

        internal const string ReAskMessage = "Type 1 or 2, then Enter (Esc = don't save).";

        private const int MaxDescriptionChars = 80;
        private const int MaxPathChars = 60;

        /// <summary>
        /// The prompt body: what would be saved, then the three answers.
        /// <paramref name="repoMemoryDir"/> is the folder layer's directory (shortened for display).
        /// </summary>
        internal static string BuildMessage(string slug, string? description, int contentLength, string repoMemoryDir)
        {
            string desc = string.IsNullOrWhiteSpace(description) ? slug : description!.Trim().Replace("\r", " ").Replace("\n", " ");
            if (desc.Length > MaxDescriptionChars) desc = desc.Substring(0, MaxDescriptionChars - 1) + "…";
            return $"\"{slug}\" — {desc} ({contentLength.ToString("N0", CultureInfo.InvariantCulture)} chars)\n"
                + $"1 = this folder ({ShortenPath(repoMemoryDir)}) · 2 = global (all folders) · Esc = don't save";
        }

        /// <summary>Null (accept) for "1" or "2" after trimming; otherwise the re-ask message.</summary>
        internal static string? Validate(string input)
        {
            string t = (input ?? string.Empty).Trim();
            return t == "1" || t == "2" ? null : ReAskMessage;
        }

        /// <summary>The choice for a prompt answer: null (Esc) declines, "2" is global, "1" is this folder.</summary>
        internal static MemorySaveChoice ToChoice(string? answer)
        {
            if (answer == null) return MemorySaveChoice.Declined;
            return answer.Trim() switch
            {
                "1" => MemorySaveChoice.Repo,
                "2" => MemorySaveChoice.Global,
                _ => MemorySaveChoice.Declined,
            };
        }

        /// <summary>
        /// The user profile becomes "~"; a path still longer than 60 chars keeps its root and
        /// its last three segments ("C:\…\workflows\.devmind\memory").
        /// </summary>
        internal static string ShortenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string p = path;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && p.StartsWith(home, StringComparison.OrdinalIgnoreCase)
                && (p.Length == home.Length || p[home.Length] == Path.DirectorySeparatorChar))
                p = "~" + p.Substring(home.Length);
            if (p.Length <= MaxPathChars) return p;

            string[] parts = p.Split(Path.DirectorySeparatorChar);
            if (parts.Length <= 4) return p;
            return parts[0] + Path.DirectorySeparatorChar + "…" + Path.DirectorySeparatorChar
                + string.Join(Path.DirectorySeparatorChar.ToString(), parts, parts.Length - 3, 3);
        }
    }
}
