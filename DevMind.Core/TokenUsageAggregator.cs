// File: TokenUsageAggregator.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Per-job (per-headless-turn) accumulation of server-reported token usage.
// One <see cref="LlmClient.RequestUsage"/> is recorded for every model request
// that returned usage — see LlmClient.LastRequestUsage for how the per-request
// values are derived from llama.cpp timings / vLLM usage.

namespace DevMind
{
    /// <summary>
    /// Server-reported token counts for ONE model request (one POST to
    /// /v1/chat/completions). All fields nullable: null means the server did not
    /// report that quantity (e.g. vLLM reports no cached/new split, a failed
    /// request reports nothing).
    /// </summary>
    public sealed class RequestUsage
    {
        /// <summary>Full prompt size the server reported (cached + newly processed) —
        /// what an API bill counts. llama.cpp: timings.prompt_n + timings.cache_n;
        /// vLLM: usage.prompt_tokens.</summary>
        public int? PromptTotal { get; init; }

        /// <summary>Prompt tokens the server had to process fresh (cache hits
        /// excluded). llama.cpp: timings.prompt_n. Null when the server did not
        /// report a cached/new split (vLLM without prompt_tokens_details).</summary>
        public int? PromptNew { get; init; }

        /// <summary>Completion / generated tokens. llama.cpp: timings.predicted_n;
        /// vLLM: usage.completion_tokens.</summary>
        public int? Completion { get; init; }
    }

    /// <summary>
    /// Sums <see cref="RequestUsage"/> records over one unit of work (one headless
    /// turn = one delegated job). Nulls propagate: a sum is null until the first
    /// non-null value arrives, and <see cref="PromptNewTotal"/> is null when the
    /// server never reported the cached/new split for ANY request in the unit.
    /// </summary>
    public sealed class TokenUsageAggregator
    {
        public long PromptTotal { get; private set; }
        public long PromptNew { get; private set; }
        public long Completion { get; private set; }

        /// <summary>True when at least one request reported the cached/new split.</summary>
        public bool SawNewSplit { get; private set; }
        /// <summary>True when at least one request did NOT report the cached/new split
        /// (while reporting usage at all) — the "new" sum is then a partial sum.</summary>
        public bool SawMissingNewSplit { get; private set; }
        /// <summary>Number of model requests that returned usage.</summary>
        public int RequestCount { get; private set; }

        /// <summary>Adds one completed request's usage. Null records are ignored
        /// (a failed request that returned no usage contributes nothing).</summary>
        public void Add(RequestUsage usage)
        {
            if (usage == null) return;
            RequestCount++;
            if (usage.PromptTotal.HasValue) PromptTotal += usage.PromptTotal.Value;
            if (usage.Completion.HasValue) Completion += usage.Completion.Value;
            if (usage.PromptNew.HasValue) { PromptNew += usage.PromptNew.Value; SawNewSplit = true; }
            else SawMissingNewSplit = true;
        }

        /// <summary>Full prompt tokens across every request in this unit, or null when
        /// no request reported prompt usage.</summary>
        public long? TotalPromptTokens() => RequestCount > 0 && PromptTotal > 0 ? PromptTotal : (long?)null;

        /// <summary>Only the freshly-processed prompt tokens (cache hits excluded).
        /// Null when the server reported the split for NO request in the unit.</summary>
        public long? TotalNewPromptTokens() => SawNewSplit && PromptNew > 0 ? PromptNew : (long?)null;

        /// <summary>True when the "new" sum mixes requests with and without the split.</summary>
        public bool IsNewPartial() => SawNewSplit && SawMissingNewSplit;

        /// <summary>Completion tokens across every request in this unit, or null when
        /// no request reported completion usage.</summary>
        public long? TotalCompletionTokens() => RequestCount > 0 && Completion > 0 ? Completion : (long?)null;
    }
}
