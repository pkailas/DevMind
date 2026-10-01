// File: ReasoningEffort.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The reasoning_effort chat-template kwarg sent alongside enable_thinking=true.
// Qwen3.8 templates (Flash-Next / 27B) read it only when thinking is on:
//   xhigh  — injects a "think carefully" system line (the template DEFAULT when the kwarg is absent)
//   high   — mapped to xhigh by the template
//   medium — injects nothing
//   low    — injects a "keep thinking brief" line
// DevMind defaults to medium so think:true no longer silently runs at xhigh.

using System;

namespace DevMind
{
    public static class ReasoningEffort
    {
        public const string Default = "medium";

        /// <summary>Accepted values, lower case, in increasing effort.</summary>
        public static readonly string[] Allowed = { "low", "medium", "high", "xhigh" };

        /// <summary>"low, medium, high, xhigh" — for error messages and descriptions.</summary>
        public static string AllowedList => string.Join(", ", Allowed);

        /// <summary>
        /// Normalizes <paramref name="value"/> (trimmed, case-insensitive) to its lower-case
        /// form. Returns false for null/blank or anything outside <see cref="Allowed"/>.
        /// </summary>
        public static bool TryNormalize(string value, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim().ToLowerInvariant();
            if (Array.IndexOf(Allowed, v) < 0) return false;
            normalized = v;
            return true;
        }

        /// <summary>The normalized value, or <see cref="Default"/> when it is absent or invalid.</summary>
        public static string NormalizeOrDefault(string value)
            => TryNormalize(value, out string n) ? n : Default;
    }
}
