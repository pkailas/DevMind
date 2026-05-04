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
        bool MicroCompactSummarize { get; }
        bool MicroCompactBrainwash { get; }
        bool AlwaysConfirmPatch { get; }
        int AgenticLoopMaxDepth { get; }
    }
}
