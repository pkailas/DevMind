// File: CacheReuseMonitor.cs  v1.0
//
// Rolling measurement of how much of the prompt the backend served from its
// prefix/KV cache (timings.cache_n) versus re-evaluated (timings.prompt_n).
// Only append-only turns count as samples: after a compaction/brainwash a
// rebuild is expected, so those turns prove nothing about the backend.
//
// The verdict distinguishes backends that hold their cache across appends
// (KV-cache transformers, healthy checkpoint restore) from ones that
// re-prefill the whole prompt every turn (hybrid/recurrent models such as
// Gated DeltaNet / Mamba on llama.cpp builds with broken checkpoint restore).
// The context-strategy switch uses this to pick incremental vs deep
// compaction; the [LLM] status line surfaces the per-turn ratio.

using System.Collections.Generic;
using System.Linq;

namespace DevMind
{
    /// <summary>
    /// Context-management policy. Transformer backends reuse their KV cache up to the first
    /// edited position, so incremental in-place compaction is cheap; hybrid/recurrent backends
    /// (Gated DeltaNet, Mamba) re-prefill the whole prompt after any history edit, so deep,
    /// rare compaction (brainwash) and strictly append-only turns win.
    /// </summary>
    public enum ContextStrategy
    {
        /// <summary>Pick per measured cache reuse; fall back to the model-name hint.</summary>
        Auto,

        /// <summary>Incremental compaction, standard thresholds (KV-cache transformers).</summary>
        Transformer,

        /// <summary>Deep+rare compaction, evictions only on already-rebuilding turns (hybrid/recurrent models).</summary>
        Hybrid,
    }

    /// <summary>What the append-only reuse samples say about the backend's prefix cache.</summary>
    public enum CacheReuseVerdict
    {
        /// <summary>Not enough samples, or samples are ambiguous.</summary>
        Unknown,

        /// <summary>The backend reuses its prefix cache across append-only turns.</summary>
        Reusing,

        /// <summary>The backend re-prefills the full prompt even on append-only turns.</summary>
        NotReusing,
    }

    /// <summary>
    /// Tracks per-turn prompt-cache reuse from server timings. Fed by
    /// <c>LlmClient.ParseTimings</c>; read by the status line and the
    /// context-strategy policy. Not thread-safe — accessed only from the
    /// send/stream path like the rest of LlmClient's timing state.
    /// </summary>
    public sealed class CacheReuseMonitor
    {
        private const int WindowSize = 4;

        // Below this the prompt is too small to say anything about reuse
        // (e.g. the first turn after /new is mostly system prompt).
        private const int MinPromptTokensForSample = 512;

        private const double ReusingThreshold = 0.50;
        private const double NotReusingThreshold = 0.15;
        private const int MinSamplesForVerdict = 3;

        private readonly Queue<double> _appendOnlyRatios = new Queue<double>();

        /// <summary>Reuse ratio of the most recent turn (any turn, including rebuilds), or -1 before the first measurement.</summary>
        public double LastReuseRatio { get; private set; } = -1;

        /// <summary>True when the most recent turn followed a history mutation (compaction/brainwash/eviction), so a rebuild was expected.</summary>
        public bool LastTurnWasRebuild { get; private set; }

        /// <summary>Number of append-only samples currently in the rolling window.</summary>
        public int SampleCount => _appendOnlyRatios.Count;

        /// <summary>Mean reuse ratio over the append-only window, or null with no samples.</summary>
        public double? RollingRatio => _appendOnlyRatios.Count > 0 ? _appendOnlyRatios.Average() : (double?)null;

        /// <summary>
        /// Records one turn's timings. <paramref name="promptN"/> = freshly evaluated prompt
        /// tokens, <paramref name="cacheN"/> = tokens served from the server's cache.
        /// <paramref name="historyMutated"/> = DevMind rewrote or removed already-sent
        /// messages before this send, so low reuse is expected and the turn is excluded
        /// from the rolling window.
        /// </summary>
        public void RecordTurn(int promptN, int cacheN, bool historyMutated)
        {
            long total = (long)promptN + cacheN;
            if (total <= 0)
                return;

            LastReuseRatio = (double)cacheN / total;
            LastTurnWasRebuild = historyMutated;

            if (historyMutated || total < MinPromptTokensForSample)
                return;

            _appendOnlyRatios.Enqueue(LastReuseRatio);
            while (_appendOnlyRatios.Count > WindowSize)
                _appendOnlyRatios.Dequeue();
        }

        /// <summary>Current verdict from the append-only window; see <see cref="CacheReuseVerdict"/>.</summary>
        public CacheReuseVerdict Verdict
        {
            get
            {
                if (_appendOnlyRatios.Count < MinSamplesForVerdict)
                    return CacheReuseVerdict.Unknown;
                double avg = _appendOnlyRatios.Average();
                if (avg >= ReusingThreshold) return CacheReuseVerdict.Reusing;
                if (avg <= NotReusingThreshold) return CacheReuseVerdict.NotReusing;
                return CacheReuseVerdict.Unknown;
            }
        }
    }
}
