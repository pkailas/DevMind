// File: ILoopCallbacks.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

namespace DevMind
{
    /// <summary>
    /// Loop-lifecycle and UI-state callbacks the Core LoopDriver invokes
    /// on its host. Distinct from IAgenticHost (file/shell/output side
    /// effects) — single-responsibility split.
    /// </summary>
    public interface ILoopCallbacks
    {
        /// <summary>Append a blank line to the output area.</summary>
        void AppendNewLine();

        /// <summary>Set the status bar text.</summary>
        void SetStatus(string text);

        /// <summary>Set the context indicator text.</summary>
        void SetContextIndicator(string text);

        /// <summary>Set the input box text.</summary>
        void SetInputText(string text);

        /// <summary>Get the current input box text.</summary>
        string GetInputText();

        /// <summary>Move keyboard focus to the input box.</summary>
        void FocusInput();

        /// <summary>Enable or disable the input affordances (Ask/Run buttons + input box).
        /// Stop button state may differ — implementation decides.</summary>
        void SetInputEnabled(bool enabled);

        /// <summary>Start the thinking-elapsed timer. Implementation owns
        /// the tick cadence and display surface.</summary>
        /// <param name="depth">Current agentic depth (for display).</param>
        /// <param name="maxDepth">Configured max depth (for display).</param>
        void StartThinkingTimer(int depth, int maxDepth);

        /// <summary>Stop the thinking-elapsed timer.</summary>
        void StopThinkingTimer();

        /// <summary>Get current context-window metrics for budget display.</summary>
        /// <returns>(used tokens, total tokens). Implementation may compute
        /// used as LastContextUsed falling back to EstimateHistoryTokens().</returns>
        (int used, int total) GetContextMetrics();
    }
}
