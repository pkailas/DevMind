// File: ITrainingLogger.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

namespace DevMind
{
    /// <summary>
    /// Host-agnostic sink for completed-turn training data. The agentic loop
    /// (<see cref="LoopDriver"/>) owns the WHEN (it calls this once per turn that
    /// <see cref="LoopIterationResult.ShouldLogTurn"/> is set); the host owns the
    /// HOW (it constructs a concrete logger — e.g. <see cref="JsonlTrainingLogger"/> —
    /// from its config and injects it through the LoopDriver constructor).
    /// </summary>
    public interface ITrainingLogger
    {
        /// <summary>
        /// True when this logger is configured to capture turns. The loop checks this
        /// before building the (non-trivial) turn payload, so a disabled logger costs nothing.
        /// </summary>
        bool Enabled { get; }

        /// <summary>Persists a single completed turn. Implementations must never throw
        /// into the agentic loop — a logging failure must not break the turn.</summary>
        void LogTurn(TrainingTurnData data);
    }
}
