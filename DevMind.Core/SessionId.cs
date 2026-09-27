// File: SessionId.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Single source of truth for the session/run ID and machine name used
// across DevMind (history, trace, training logger).

using System;

namespace DevMind
{
    /// <summary>
    /// Session ID and machine name management. Matches the TS shell's algorithm:
    ///   - DEVMIND_TRACE_RUN_ID env var if set and non-empty
    ///   - Otherwise: YYYY-MM-DDTHHmmssZ-pid{Process.Id}
    /// </summary>
    public static class SessionId
    {
        static string _sessionId;
        static string _machineName;

        /// <summary>Return the session/run ID for this process. Computed once, cached.</summary>
        public static string Get()
        {
            if (_sessionId == null)
            {
                _sessionId = ComputeSessionId();
            }
            return _sessionId;
        }

        /// <summary>Reset the cached session ID so the next call computes a fresh one.</summary>
        public static void Reset()
        {
            _sessionId = null;
        }

        /// <summary>
        /// Continue an existing session under its own id, instead of computing a fresh one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Throws if the id has already been resolved. That guard is the whole point of this
        /// method existing rather than a settable property: consumers cache what
        /// <see cref="Get"/> hands them — the history writer keys rows on it, the nearline
        /// cache names its spill directory after it — so adopting afterwards would split one
        /// session across two ids. That is exactly the fork this was written to remove, and it
        /// would be invisible until someone went looking for the missing half.
        /// </para>
        /// <para>
        /// Call it immediately after parsing arguments, before anything reads the id.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">The id has already been resolved or adopted.</exception>
        public static void Adopt(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("A session id is required.", nameof(sessionId));

            if (_sessionId != null)
                throw new InvalidOperationException(
                    "The session id has already been resolved; adopting one now would split the " +
                    "session across two ids. Adopt before the first Get().");

            _sessionId = sessionId;
        }

        /// <summary>Return the machine hostname. Computed once, cached.</summary>
        public static string GetMachineName()
        {
            if (_machineName == null)
            {
                _machineName = Environment.MachineName;
            }
            return _machineName;
        }

        /// <summary>
        /// Mint a fresh session id WITHOUT touching the current one — for a fork (e.g. /rewind)
        /// that must not split the live session across two ids.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deliberately NOT <see cref="ComputeSessionId"/>: that one honors DEVMIND_TRACE_RUN_ID,
        /// which would pin the fork to the live session's id — an exact-id collision in the
        /// store, where two sessions' rows would merge. A fork id is stamped with the process
        /// id (so it groups with this run's artifacts) plus a random suffix, so it cannot equal
        /// the live id even when both are minted in the same second.
        /// </para>
        /// </remarks>
        public static string NewId()
        {
            string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHHmmssZ").Replace(":", "");
            return $"{stamp}-pid{System.Diagnostics.Process.GetCurrentProcess().Id}-fork{Guid.NewGuid():N}";
        }

        static string ComputeSessionId()
        {
            string inherited = Environment.GetEnvironmentVariable("DEVMIND_TRACE_RUN_ID");
            if (!string.IsNullOrEmpty(inherited)) return inherited;

            string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHHmmssZ").Replace(":", "");
            return $"{stamp}-pid{System.Diagnostics.Process.GetCurrentProcess().Id}";
        }
    }
}
