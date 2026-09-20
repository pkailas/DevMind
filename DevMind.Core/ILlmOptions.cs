// File: ILlmOptions.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.

namespace DevMind
{
    /// <summary>
    /// Runtime options contract for <see cref="LlmClient"/> and <see cref="AgenticExecutor"/>.
    /// Implementations must be long-lived shared instances (singleton or DI-scoped) — not copies.
    /// SystemPrompt in particular is written by the host layer during request processing to inject
    /// context; LlmClient reads it live. Passing a value-copy breaks that propagation contract.
    /// </summary>
    public interface ILlmOptions
    {
        string SystemPrompt { get; }
        string ModelName { get; }
        int RequestTimeoutMinutes { get; }
        int FirstTokenTimeoutMinutes { get; }
        bool ShowDebugOutput { get; }
        bool ShowContextBudget { get; }
        bool ShowLlmThinking { get; }
        ContextEvictionMode ContextEviction { get; }
        int ManualContextSize { get; }
        LlmServerType ServerType { get; }
        string CustomContextEndpoint { get; }
        int MicroCompactThreshold { get; }

        /// <summary>Characters above which a tool result is nearline-capped at ingest (full text
        /// cached + spilled to the durable output dir, head+tail excerpt enters history). Non-positive
        /// falls back to the built-in default (8,000). Positive values below 6,000 are clamped up to
        /// 6,000 — the excerpt keeps a 4,000-char head and a 2,000-char tail, so a smaller threshold
        /// cannot produce a shorter excerpt than the original.</summary>
        int NearlineIngestThresholdChars { get; }
        bool MicroCompactSummarize { get; }
        bool MicroCompactBrainwash { get; }
        bool AlwaysConfirmPatch { get; }
        int AgenticLoopMaxDepth { get; }

        /// <summary>Context-window utilization limit (percent). When a round's context usage
        /// (n_past / n_ctx) reaches this, the loop pauses and asks before continuing.
        /// 0 disables the guard.</summary>
        int AgenticContextLimitPercent { get; }
    }
}
