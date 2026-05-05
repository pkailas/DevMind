// File: OutputColor.cs  v1.1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

namespace DevMind
{
    /// <summary>
    /// Color categories for text appended to the DevMind output panel.
    /// </summary>
    public enum OutputColor
    {
        Normal,   // #CCCCCC — LLM response text, shell stdout
        Dim,      // #888888 — startup banner, status messages, [Stopped] notice
        Input,    // #569CD6 — echoed user input lines prefixed with "> "
        Error,    // #F44747 — shell stderr, LLM errors, build failures
        Success,  // #4EC94E — PATCH applied, file created, build succeeded
        Thinking, // #6A6A8A — LLM thinking tokens when ShowLlmThinking is enabled
        Warning   // #FFB900 — file not found, write guard, non-fatal warnings
    }
}
