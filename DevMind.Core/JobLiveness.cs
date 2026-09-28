// File: JobLiveness.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Liveness record for a delegated (headless) job — the input to the job runner's STALL
// watchdog (H-37). A job used to be killed by a wall-clock timeout; a job that is still
// progressing is already bounded by max_depth, so the only thing a timeout needs to catch is
// a hang. HeadlessSession / LoopDriver / BufferedAgenticHost tick this record on the events
// that prove the agent is alive, and the watchdog declares a stall only when none has
// happened for the whole window.
//
// Progress events (each one a Tick):
//   * model output  — every streamed chunk from the LLM, visible text or think tokens: a long
//                     generation that is still streaming is alive.
//   * tool call started / returned — every executed tool block, and each shell/test command.
//   * shell output  — every line a running shell command streams.
//   * iteration N completed — the loop finished processing a model response.
//
// A single long tool call that is silent (a 5-minute `dotnet test`) is covered by the
// in-flight exemption: while a shell/test command runs, the stall clock is held until the
// command's OWN timeout plus one window has passed. The command's timeout is what bounds it;
// only a command that outlives its own timeout by a whole window is a hang.
//
// Thread safety: ticks come from the LLM stream callback, the shell's output pump and the
// loop thread; the watchdog reads on its own task. The timestamp is a long of UTC ticks
// written with Interlocked; the description and the in-flight slot are immutable references
// published with Volatile.

using System;
using System.Threading;

namespace DevMind
{
    /// <summary>Thread-safe "last progress" record for one headless job (see file header).</summary>
    public sealed class JobLiveness
    {
        private readonly TimeProvider _clock;
        private long _lastProgressTicks;
        private string _lastProgress;
        private InFlightCall _inFlight;

        private sealed class InFlightCall
        {
            public required string What { get; init; }
            public required long StartedTicks { get; init; }
            public required TimeSpan Budget { get; init; }
        }

        /// <param name="clock">Clock for every timestamp (injectable for tests; default system).</param>
        /// <param name="initial">Description of the starting point, e.g. "job started".</param>
        public JobLiveness(TimeProvider clock = null, string initial = "job started")
        {
            _clock = clock ?? TimeProvider.System;
            Tick(initial);
        }

        /// <summary>UTC time of the most recent progress event.</summary>
        public DateTime LastProgressUtc =>
            new DateTime(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);

        /// <summary>What the most recent progress event was.</summary>
        public string LastProgress => Volatile.Read(ref _lastProgress);

        /// <summary>Records a progress event. Cheap enough for every streamed token.</summary>
        public void Tick(string what)
        {
            Volatile.Write(ref _lastProgress, what);
            Interlocked.Exchange(ref _lastProgressTicks, _clock.GetUtcNow().UtcDateTime.Ticks);
        }

        /// <summary>
        /// Marks a tool call with its own timeout as in flight (ticks "tool call started"); the
        /// returned handle ticks "tool call returned" and clears the slot when disposed. While
        /// the call is in flight the stall clock is held until <paramref name="budget"/> plus
        /// one stall window has passed since it started.
        /// </summary>
        public IDisposable BeginToolCall(string what, TimeSpan budget)
        {
            Tick($"tool call started: {what}");
            var call = new InFlightCall
            {
                What = what,
                StartedTicks = _clock.GetUtcNow().UtcDateTime.Ticks,
                Budget = budget,
            };
            Volatile.Write(ref _inFlight, call);
            return new EndCall(this, call);
        }

        private sealed class EndCall : IDisposable
        {
            private JobLiveness _owner;
            private readonly InFlightCall _call;
            public EndCall(JobLiveness owner, InFlightCall call) { _owner = owner; _call = call; }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;
                Interlocked.CompareExchange(ref owner._inFlight, null, _call);
                owner.Tick($"tool call returned: {_call.What}");
            }
        }

        /// <summary>
        /// True when no progress event has happened for <paramref name="window"/> and no
        /// in-flight tool call is still inside its own timeout (plus one window).
        /// <paramref name="reason"/> is the caller-facing "stalled: …" line.
        /// </summary>
        public bool IsStalled(TimeSpan window, out string reason)
        {
            reason = null;
            long nowTicks = _clock.GetUtcNow().UtcDateTime.Ticks;
            long lastTicks = Interlocked.Read(ref _lastProgressTicks);
            if (nowTicks - lastTicks < window.Ticks) return false;

            InFlightCall call = Volatile.Read(ref _inFlight);
            if (call != null && nowTicks - call.StartedTicks < (call.Budget + window).Ticks)
                return false;

            string last = $"{LastProgress} at {new DateTime(lastTicks, DateTimeKind.Utc):yyyy-MM-dd HH:mm:ss} UTC";
            if (call != null)
                last += $"; tool call '{call.What}' still running past its own {FormatSpan(call.Budget)} timeout";
            reason = $"stalled: no progress for {FormatSpan(window)} (last progress: {last})";
            return true;
        }

        /// <summary>"10 min", "90 s" — whole minutes when the span is a whole number of them.</summary>
        public static string FormatSpan(TimeSpan span) =>
            span.TotalMinutes >= 1 && span.Ticks % TimeSpan.TicksPerMinute == 0
                ? $"{(long)span.TotalMinutes} min"
                : $"{span.TotalSeconds:0.#} s";
    }
}
