// File: ThinkFilter.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Text;

namespace DevMind
{
    /// <summary>
    /// Strips &lt;think&gt;...&lt;/think&gt; blocks from streaming SSE tokens.
    /// Stateful across tokens — call <see cref="Reset"/> between conversations.
    /// </summary>
    public sealed class ThinkFilter
    {
        private bool _inThinkBlock;
        private readonly StringBuilder _thinkBuffer = new StringBuilder();

        /// <summary>
        /// Processes one SSE token chunk. Returns the visible text with think content removed.
        /// Sets <paramref name="pendingThinkText"/> to the think content for optional display,
        /// or null if there is no pending think text this chunk.
        /// </summary>
        public string Process(string chunk, bool showThinking, out string pendingThinkText)
        {
            pendingThinkText = null;

            if (_inThinkBlock)
            {
                _thinkBuffer.Append(chunk);
                string bufStr = _thinkBuffer.ToString();
                int closeIdx = bufStr.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (closeIdx >= 0)
                {
                    string after = bufStr.Substring(closeIdx + "</think>".Length);
                    _inThinkBlock = false;
                    int chunkCloseIdx = chunk.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                    if (showThinking && chunkCloseIdx > 0)
                        pendingThinkText = chunk.Substring(0, chunkCloseIdx);
                    _thinkBuffer.Clear();
                    return after;
                }
                if (showThinking)
                    pendingThinkText = chunk;
                return string.Empty;
            }

            int openIdx = chunk.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (openIdx >= 0)
            {
                string before = chunk.Substring(0, openIdx);
                string rest = chunk.Substring(openIdx + "<think>".Length);
                _inThinkBlock = true;
                _thinkBuffer.Clear();
                _thinkBuffer.Append(rest);

                int closeIdx = rest.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (closeIdx >= 0)
                {
                    string thinkContent = rest.Substring(0, closeIdx);
                    string after = rest.Substring(closeIdx + "</think>".Length);
                    _inThinkBlock = false;
                    _thinkBuffer.Clear();
                    if (showThinking && !string.IsNullOrEmpty(thinkContent))
                        pendingThinkText = "[THINKING] " + thinkContent;
                    return before + after;
                }
                if (showThinking && !string.IsNullOrEmpty(rest))
                    pendingThinkText = "[THINKING] " + rest;
                return before;
            }

            return chunk;
        }

        /// <summary>Resets think-block state for a new conversation or restart.</summary>
        public void Reset()
        {
            _inThinkBlock = false;
            _thinkBuffer.Clear();
        }
    }
}
