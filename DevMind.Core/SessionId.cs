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

        static string ComputeSessionId()
        {
            string inherited = Environment.GetEnvironmentVariable("DEVMIND_TRACE_RUN_ID");
            if (!string.IsNullOrEmpty(inherited)) return inherited;

            string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHHmmssZ").Replace(":", "");
            return $"{stamp}-pid{System.Diagnostics.Process.GetCurrentProcess().Id}";
        }
    }
}
